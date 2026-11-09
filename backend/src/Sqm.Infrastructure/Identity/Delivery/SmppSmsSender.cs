using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity.Delivery;

/// <summary>Something that sends one text to one number.</summary>
internal interface ISmsSender : IDisposable
{
    Task SendAsync(string destination, string text, CancellationToken ct);
}

/// <summary>Sends a text through an SMSC, over SMPP 3.4, which is what MCI's gateway speaks.</summary>
/// <remarks>
/// <para>
/// A connection per message: bind as a transmitter, submit, unbind. Codes are rare and a held
/// session is one more thing for a gateway to drop, which is the same reasoning as the mail sender's.
/// Only the four operations that needs are spoken, and the SMSC's link check is answered in case it
/// asks during that time.
/// </para>
/// <para>
/// Written here rather than taken from a library: the maintained .NET SMPP libraries are commercial,
/// and what a transmitter needs is a few fixed records. A failure says only what the SMSC answered,
/// never the text, which holds the code, and never the password.
/// </para>
/// </remarks>
internal sealed class SmppSmsSender : ISmsSender
{
    private const uint BindTransmitter = 0x00000002;
    private const uint SubmitSm = 0x00000004;
    private const uint Unbind = 0x00000006;
    private const uint EnquireLink = 0x00000015;
    private const uint GenericNack = 0x80000000;
    private const uint Response = 0x80000000;

    private const byte InterfaceVersion = 0x34;
    private const ushort MessagePayloadTag = 0x0424;
    private const int HeaderLength = 16;
    private const int MaxPduLength = 64 * 1024;

    private readonly SmppGatewayOptions _options;

    public SmppSmsSender(SmppGatewayOptions options)
    {
        _options = options;

        if (options.DataCoding is not (0 or 3 or 8))
        {
            throw new InvalidOperationException(
                "Auth:Delivery:Smpp:DataCoding must be 0 (the SMSC's default alphabet), 3 (Latin-1) or 8 (UCS-2).");
        }

        if (!string.Equals(options.DestinationFormat, "International", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.DestinationFormat, "National", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Auth:Delivery:Smpp:DestinationFormat must be International or National.");
        }
    }

    public async Task SendAsync(string destination, string text, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_options.Host!, _options.Port, deadline.Token).ConfigureAwait(false);

            var session = new Session(client.GetStream());

            await session.RequestAsync(BindTransmitter, BindBody(), "the sign-in", deadline.Token)
                .ConfigureAwait(false);

            await session.RequestAsync(SubmitSm, SubmitBody(destination, text), "the message",
                deadline.Token).ConfigureAwait(false);

            // Courtesy: the message is already accepted, so a gateway that hangs up instead of
            // answering is no failure of ours.
            try
            {
                await session.RequestAsync(Unbind, [], "the sign-out", deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ContactDeliveryException
                or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Nothing to do.
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ContactDeliveryException("The SMS gateway did not answer in time.");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new ContactDeliveryException(
                $"The SMS gateway could not be reached ({ex.GetType().Name}).", ex);
        }
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose()
    {
        // A connection lives for one message; there is nothing held between them.
    }

    // ======================================================================= the records

    private byte[] BindBody()
    {
        var body = new PduWriter();
        body.CString(_options.SystemId!, 16);
        body.CString(_options.Password ?? string.Empty, 9);
        body.CString(_options.SystemType, 13);
        body.Byte(InterfaceVersion);
        body.Byte(_options.SourceTon);
        body.Byte(_options.SourceNpi);
        body.CString(string.Empty, 41);
        return body.ToArray();
    }

    private byte[] SubmitBody(string destination, string text)
    {
        var encoded = SmsText.Encode(text, _options.DataCoding);
        var fitsOnePart = SmsText.Length(text, _options.DataCoding) <= SmsText.Limit(_options.DataCoding);

        var body = new PduWriter();
        body.CString(string.Empty, 6);                                   // service_type
        body.Byte(_options.SourceTon);
        body.Byte(_options.SourceNpi);
        body.CString(_options.SourceAddress!, 21);
        body.Byte(_options.DestinationTon);
        body.Byte(_options.DestinationNpi);
        body.CString(Number(destination), 21);
        body.Byte(0);                                                    // esm_class
        body.Byte(0);                                                    // protocol_id
        body.Byte(0);                                                    // priority_flag
        body.CString(string.Empty, 17);                                  // schedule_delivery_time
        body.CString(string.Empty, 17);                                  // validity_period
        body.Byte(0);                                                    // registered_delivery
        body.Byte(0);                                                    // replace_if_present_flag
        body.Byte(_options.DataCoding);
        body.Byte(0);                                                    // sm_default_msg_id

        if (fitsOnePart)
        {
            body.Byte((byte)encoded.Length);
            body.Bytes(encoded);
        }
        else
        {
            // Too long for one part: the whole text in message_payload, and short_message empty,
            // which is how SMPP 3.4 carries a long message without the ESME splitting it.
            body.Byte(0);
            body.UInt16(MessagePayloadTag);
            body.UInt16((ushort)encoded.Length);
            body.Bytes(encoded);
        }

        return body.ToArray();
    }

    /// <summary>The number as the gateway wants it, from the stored international form.</summary>
    private string Number(string destination)
    {
        var digits = destination.TrimStart('+');

        if (string.Equals(_options.DestinationFormat, "National", StringComparison.OrdinalIgnoreCase)
            && digits.StartsWith(_options.CountryCode, StringComparison.Ordinal))
        {
            return "0" + digits[_options.CountryCode.Length..];
        }

        return digits;
    }

    // ======================================================================= the conversation

    private sealed class Session
    {
        private readonly Stream _stream;
        private uint _sequence;

        public Session(Stream stream) => _stream = stream;

        /// <summary>Sends one request and waits for its response, answering link checks meanwhile.</summary>
        public async Task RequestAsync(uint command, byte[] body, string what, CancellationToken ct)
        {
            var sequence = ++_sequence;
            await WriteAsync(command, 0, sequence, body, ct).ConfigureAwait(false);

            while (true)
            {
                var (id, status, number, _) = await ReadAsync(ct).ConfigureAwait(false);

                if (id == EnquireLink)
                {
                    await WriteAsync(EnquireLink | Response, 0, number, [], ct).ConfigureAwait(false);
                    continue;
                }

                if (id == GenericNack)
                {
                    throw new ContactDeliveryException(
                        $"The SMS gateway did not understand {what}: {Describe(status)}.");
                }

                if (id == (command | Response) && number == sequence)
                {
                    if (status != 0)
                    {
                        throw new ContactDeliveryException(
                            $"The SMS gateway refused {what}: {Describe(status)}.");
                    }

                    return;
                }

                // Anything else - a delivery report for another session, say - is not ours.
            }
        }

        private async Task WriteAsync(
            uint command, uint status, uint sequence, byte[] body, CancellationToken ct)
        {
            var pdu = new byte[HeaderLength + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(0), (uint)pdu.Length);
            BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(4), command);
            BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(8), status);
            BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(12), sequence);
            body.CopyTo(pdu, HeaderLength);

            await _stream.WriteAsync(pdu, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private async Task<(uint Id, uint Status, uint Sequence, byte[] Body)> ReadAsync(
            CancellationToken ct)
        {
            var header = new byte[HeaderLength];
            await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0));

            if (length is < HeaderLength or > MaxPduLength)
            {
                throw new ContactDeliveryException("The SMS gateway sent something that is not SMPP.");
            }

            var body = new byte[length - HeaderLength];
            await _stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

            return (
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)),
                body);
        }

        private static string Describe(uint status) => status switch
        {
            0x0000000A => "invalid source address (0x0000000A)",
            0x0000000B => "invalid destination address (0x0000000B)",
            0x0000000D => "bind failed (0x0000000D)",
            0x0000000E => "invalid password (0x0000000E)",
            0x0000000F => "invalid system id (0x0000000F)",
            0x00000045 => "submit failed (0x00000045)",
            0x00000058 => "throttled (0x00000058)",
            _ => "status 0x" + status.ToString("X8", CultureInfo.InvariantCulture),
        };
    }

    private sealed class PduWriter
    {
        private readonly List<byte> _buffer = [];

        /// <summary>A C-octet string: ASCII and a closing zero, refused if longer than SMPP allows.</summary>
        public void CString(string value, int maxWithTerminator)
        {
            var bytes = Encoding.ASCII.GetBytes(value);

            if (bytes.Length + 1 > maxWithTerminator)
            {
                throw new ContactDeliveryException(
                    $"A field is longer than SMPP allows ({maxWithTerminator - 1} characters).");
            }

            _buffer.AddRange(bytes);
            _buffer.Add(0);
        }

        public void Byte(byte value) => _buffer.Add(value);

        public void Bytes(byte[] value) => _buffer.AddRange(value);

        public void UInt16(ushort value)
        {
            _buffer.Add((byte)(value >> 8));
            _buffer.Add((byte)value);
        }

        public byte[] ToArray() => [.. _buffer];
    }
}

/// <summary>Text, in the encoding an SMSC is told it is in.</summary>
internal static class SmsText
{
    /// <summary>
    /// The GSM 03.38 default alphabet, one character per code from 0x00 to 0x7F; 0x1B is the escape.
    /// </summary>
    /// <remarks>
    /// It is mostly ASCII, and the places it is not are the ones that bite: '@' is 0x00, '$' is
    /// 0x02 and '_' is 0x11, where ASCII would send '¡', '¤' and '§'.
    /// </remarks>
    internal const string Gsm =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001bÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?"
        + "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    /// <summary>Characters reached through the escape, and the code after it.</summary>
    private static readonly Dictionary<char, byte> GsmExtension = new()
    {
        ['^'] = 0x14, ['{'] = 0x28, ['}'] = 0x29, ['\\'] = 0x2F, ['['] = 0x3C, ['~'] = 0x3D,
        [']'] = 0x3E, ['|'] = 0x40, ['€'] = 0x65,
    };

    private const byte Escape = 0x1B;
    private const byte Question = 0x3F;

    /// <summary>The octets of the text: one per GSM character, unpacked, as SMPP carries them.</summary>
    public static byte[] Encode(string text, byte dataCoding) => dataCoding switch
    {
        3 => Encoding.Latin1.GetBytes(text),
        8 => Encoding.BigEndianUnicode.GetBytes(text),
        _ => EncodeGsm(text),
    };

    /// <summary>What one part holds: 160 GSM characters, or 140 octets.</summary>
    public static int Limit(byte dataCoding) => dataCoding == 0 ? 160 : 140;

    /// <summary>How much of a part the text takes, in the unit <see cref="Limit"/> counts.</summary>
    public static int Length(string text, byte dataCoding) => Encode(text, dataCoding).Length;

    private static byte[] EncodeGsm(string text)
    {
        var bytes = new List<byte>(text.Length);

        foreach (var c in text)
        {
            var code = Gsm.IndexOf(c, StringComparison.Ordinal);

            if (code >= 0 && code != Escape)
            {
                bytes.Add((byte)code);
            }
            else if (GsmExtension.TryGetValue(c, out var extended))
            {
                bytes.Add(Escape);
                bytes.Add(extended);
            }
            else
            {
                bytes.Add(Question);
            }
        }

        return [.. bytes];
    }
}
