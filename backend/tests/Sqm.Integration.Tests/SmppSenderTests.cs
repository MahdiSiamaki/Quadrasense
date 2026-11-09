using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sqm.Application.Identity;
using Sqm.Infrastructure.Identity.Delivery;

namespace Sqm.Integration.Tests;

/// <summary>
/// Texts through an SMSC over SMPP 3.4, against an SMSC played by the test: what goes down the wire,
/// field by field, and what each answer it can give becomes. Needs no database and no gateway.
/// </summary>
public sealed class SmppSenderTests
{
    private const uint BindTransmitter = 0x00000002;
    private const uint SubmitSm = 0x00000004;
    private const uint Unbind = 0x00000006;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContactDelivery Delivery(int port, Func<SmppGatewayOptions, SmppGatewayOptions>? change = null)
    {
        var smpp = new SmppGatewayOptions
        {
            Host = "127.0.0.1",
            Port = port,
            SystemId = "quadra",
            Password = "secret12",
            SystemType = "OTP",
            SourceAddress = "3000",
            SourceTon = 3,
            SourceNpi = 1,
            Timeout = TimeSpan.FromSeconds(5),
        };

        return new ContactDelivery(
            new DeliveryOptions { Smpp = change is null ? smpp : change(smpp) },
            isDevelopment: false,
            NullLogger<ContactDelivery>.Instance);
    }

    private static OutgoingMessage Message(string text) => new("subject", "a long body", text);

    // ======================================================================= what is sent

    [Fact]
    public async Task It_signs_in_as_a_transmitter_with_the_configured_account()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct);

        var bind = (await smsc.ReceivedAsync())[0];
        var fields = new PduReader(bind.Body);

        Assert.Equal(BindTransmitter, bind.Command);
        Assert.Equal("quadra", fields.CString());
        Assert.Equal("secret12", fields.CString());
        Assert.Equal("OTP", fields.CString());
        Assert.Equal(0x34, fields.Byte());
        Assert.Equal(3, fields.Byte());
        Assert.Equal(1, fields.Byte());
        Assert.Equal(string.Empty, fields.CString());
    }

    [Fact]
    public async Task It_submits_the_brief_to_the_number_in_international_form()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port);

        await delivery.SendAsync(
            ContactChannel.Sms, "+989121234567", Message("123456 is your code."), Ct);

        var submit = (await smsc.ReceivedAsync())[1];
        var sm = SubmitFields.Read(submit.Body);

        Assert.Equal(SubmitSm, submit.Command);
        Assert.Equal((3, 1, "3000"), (sm.SourceTon, sm.SourceNpi, sm.Source));
        Assert.Equal((1, 1, "989121234567"), (sm.DestinationTon, sm.DestinationNpi, sm.Destination));
        Assert.Equal(0, sm.DataCoding);
        Assert.Equal("123456 is your code.", Encoding.ASCII.GetString(sm.ShortMessage));
        Assert.Null(sm.Payload);
    }

    [Fact]
    public async Task It_signs_out_when_done()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct);

        Assert.Equal(
            [BindTransmitter, SubmitSm, Unbind],
            (await smsc.ReceivedAsync()).Select(p => p.Command));
    }

    [Fact]
    public async Task A_gateway_that_wants_the_number_as_dialled_at_home_gets_it()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port, o => new SmppGatewayOptions
        {
            Host = o.Host, Port = o.Port, SystemId = o.SystemId, Password = o.Password,
            SourceAddress = o.SourceAddress, DestinationTon = 2, DestinationFormat = "National",
        });

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct);

        var sm = SubmitFields.Read((await smsc.ReceivedAsync())[1].Body);

        Assert.Equal((2, "09121234567"), (sm.DestinationTon, sm.Destination));
    }

    [Fact]
    public async Task Characters_that_differ_from_ascii_in_the_gsm_alphabet_are_sent_as_gsm()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("a@b_c$d{e}"), Ct);

        var sm = SubmitFields.Read((await smsc.ReceivedAsync())[1].Body);

        Assert.Equal(
            new byte[] { 0x61, 0x00, 0x62, 0x11, 0x63, 0x02, 0x64, 0x1B, 0x28, 0x65, 0x1B, 0x29 },
            sm.ShortMessage);
    }

    [Fact]
    public async Task A_character_the_gsm_alphabet_lacks_becomes_a_question_mark()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("کد 1"), Ct);

        var sm = SubmitFields.Read((await smsc.ReceivedAsync())[1].Body);

        Assert.Equal("?? 1", Encoding.ASCII.GetString(sm.ShortMessage));
    }

    [Fact]
    public async Task Ucs2_carries_any_script_when_asked_for()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port, o => new SmppGatewayOptions
        {
            Host = o.Host, Port = o.Port, SystemId = o.SystemId, Password = o.Password,
            SourceAddress = o.SourceAddress, DataCoding = 8,
        });

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("کد 1"), Ct);

        var sm = SubmitFields.Read((await smsc.ReceivedAsync())[1].Body);

        Assert.Equal(8, sm.DataCoding);
        Assert.Equal("کد 1", Encoding.BigEndianUnicode.GetString(sm.ShortMessage));
    }

    [Fact]
    public async Task A_text_too_long_for_one_part_goes_whole_in_the_payload()
    {
        using var smsc = new FakeSmsc();
        using var delivery = Delivery(smsc.Port);
        var text = new string('x', 200);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message(text), Ct);

        var sm = SubmitFields.Read((await smsc.ReceivedAsync())[1].Body);

        Assert.Empty(sm.ShortMessage);
        Assert.Equal(text, Encoding.ASCII.GetString(sm.Payload!));
    }

    [Fact]
    public void Every_message_the_system_sends_fits_one_part_for_a_long_username()
    {
        var username = new string('u', 40);

        var briefs = new[]
        {
            AccountMessages.ResetCode("123456", username, TimeSpan.FromMinutes(10)),
            AccountMessages.VerifyCode("123456", TimeSpan.FromMinutes(10)),
            AccountMessages.Notice(AccountNotice.PasswordReset, username),
            AccountMessages.Notice(AccountNotice.TwoStepOn, username),
            AccountMessages.Notice(AccountNotice.TwoStepOff, username),
        }.Select(m => m.Brief ?? m.Text);

        Assert.All(briefs, brief => Assert.True(brief.Length <= 160, $"{brief.Length}: {brief}"));
    }

    [Fact]
    public async Task A_link_check_from_the_smsc_is_answered_and_the_message_still_goes()
    {
        using var smsc = new FakeSmsc { CheckLinkBeforeAnswering = true };
        using var delivery = Delivery(smsc.Port);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct);

        var received = await smsc.ReceivedAsync();
        Assert.Contains(received, p => p.Command == 0x80000015);
        Assert.Contains(received, p => p.Command == SubmitSm);
    }

    // ======================================================================= what goes wrong

    [Fact]
    public async Task A_refused_sign_in_is_reported_with_the_smscs_reason_and_never_the_password()
    {
        using var smsc = new FakeSmsc { BindStatus = 0x0000000E };
        using var delivery = Delivery(smsc.Port);

        var failure = await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct));

        Assert.Contains("invalid password", failure.Message);
        Assert.DoesNotContain("secret12", failure.Message);
        Assert.DoesNotContain(await smsc.ReceivedAsync(), p => p.Command == SubmitSm);
    }

    [Fact]
    public async Task A_refused_message_is_reported_without_its_text()
    {
        using var smsc = new FakeSmsc { SubmitStatus = 0x00000045 };
        using var delivery = Delivery(smsc.Port);

        var failure = await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct));

        Assert.Contains("submit failed", failure.Message);
        Assert.DoesNotContain("123456", failure.Message);
    }

    [Fact]
    public async Task An_smsc_that_never_answers_fails_in_time()
    {
        using var smsc = new FakeSmsc { Silent = true };
        using var delivery = Delivery(smsc.Port, o => new SmppGatewayOptions
        {
            Host = o.Host, Port = o.Port, SystemId = o.SystemId, Password = o.Password,
            SourceAddress = o.SourceAddress, Timeout = TimeSpan.FromMilliseconds(500),
        });

        var started = DateTime.UtcNow;

        var failure = await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct));

        Assert.Contains("did not answer in time", failure.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task An_smsc_that_is_not_there_fails_as_a_delivery_failure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var delivery = Delivery(port);

        await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message("123456"), Ct));
    }

    [Fact]
    public void Smpp_is_used_when_both_kinds_of_gateway_are_set()
    {
        using var delivery = new ContactDelivery(
            new DeliveryOptions
            {
                Sms = new SmsGatewayOptions { Url = "https://gateway.test/send" },
                Smpp = new SmppGatewayOptions { Host = "smsc.test", SystemId = "a", SourceAddress = "3000" },
            },
            isDevelopment: false,
            NullLogger<ContactDelivery>.Instance);

        Assert.True(delivery.IsAvailable(ContactChannel.Sms));
    }

    [Fact]
    public void An_smpp_gateway_without_an_account_or_a_sender_is_not_configured()
    {
        Assert.False(new SmppGatewayOptions { Host = "smsc.test", SourceAddress = "3000" }.IsConfigured);
        Assert.False(new SmppGatewayOptions { Host = "smsc.test", SystemId = "a" }.IsConfigured);
    }

    [Theory]
    [InlineData((byte)4, "International")]
    [InlineData((byte)0, "Local")]
    public void A_setting_that_means_nothing_stops_the_start(byte dataCoding, string format) =>
        Assert.Throws<InvalidOperationException>(() => new ContactDelivery(
            new DeliveryOptions
            {
                Smpp = new SmppGatewayOptions
                {
                    Host = "smsc.test", SystemId = "a", SourceAddress = "3000",
                    DataCoding = dataCoding, DestinationFormat = format,
                },
            },
            isDevelopment: false,
            NullLogger<ContactDelivery>.Instance));

    // ======================================================================= the stand-in SMSC

    private sealed record Pdu(uint Command, uint Status, uint Sequence, byte[] Body);

    /// <summary>Just enough of an SMSC: answers a bind, a submit and an unbind, and keeps them.</summary>
    private sealed class FakeSmsc : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task<List<Pdu>> _conversation;

        public FakeSmsc()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _conversation = Task.Run(RunAsync);
        }

        public int Port { get; }

        public uint BindStatus { get; init; }

        public uint SubmitStatus { get; init; }

        public bool Silent { get; init; }

        public bool CheckLinkBeforeAnswering { get; init; }

        public Task<List<Pdu>> ReceivedAsync() => _conversation;

        public void Dispose() => _listener.Stop();

        private async Task<List<Pdu>> RunAsync()
        {
            var received = new List<Pdu>();

            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();

                while (true)
                {
                    var pdu = await ReadAsync(stream);
                    received.Add(pdu);

                    if (Silent)
                    {
                        continue;
                    }

                    if (pdu.Command == BindTransmitter)
                    {
                        await WriteAsync(stream, BindTransmitter | 0x80000000, BindStatus, pdu.Sequence,
                            [.. Encoding.ASCII.GetBytes("FAKE"), 0]);
                    }
                    else if (pdu.Command == SubmitSm)
                    {
                        if (CheckLinkBeforeAnswering)
                        {
                            await WriteAsync(stream, 0x00000015, 0, 9000, []);
                            received.Add(await ReadAsync(stream));
                        }

                        await WriteAsync(stream, SubmitSm | 0x80000000, SubmitStatus, pdu.Sequence,
                            [.. Encoding.ASCII.GetBytes("MSG1"), 0]);
                    }
                    else if (pdu.Command == Unbind)
                    {
                        await WriteAsync(stream, Unbind | 0x80000000, 0, pdu.Sequence, []);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException
                or EndOfStreamException)
            {
                // The sender hung up, which is how a refusal or a timeout ends the conversation.
            }

            return received;
        }

        private static async Task<Pdu> ReadAsync(Stream stream)
        {
            var header = new byte[16];
            await stream.ReadExactlyAsync(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var body = new byte[length - 16];
            await stream.ReadExactlyAsync(body);

            return new Pdu(
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)),
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)),
                body);
        }

        private static async Task WriteAsync(
            Stream stream, uint command, uint status, uint sequence, byte[] body)
        {
            var pdu = new byte[16 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(pdu, (uint)pdu.Length);
            BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(4), command);
            BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(8), status);
            BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(12), sequence);
            body.CopyTo(pdu, 16);
            await stream.WriteAsync(pdu);
        }
    }

    private sealed class PduReader
    {
        private readonly byte[] _bytes;
        private int _at;

        public PduReader(byte[] bytes) => _bytes = bytes;

        public bool AtEnd => _at >= _bytes.Length;

        public string CString()
        {
            var end = Array.IndexOf(_bytes, (byte)0, _at);
            var text = Encoding.ASCII.GetString(_bytes, _at, end - _at);
            _at = end + 1;
            return text;
        }

        public byte Byte() => _bytes[_at++];

        public ushort UInt16()
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(_at));
            _at += 2;
            return value;
        }

        public byte[] Bytes(int count)
        {
            var value = _bytes.AsSpan(_at, count).ToArray();
            _at += count;
            return value;
        }
    }

    private sealed record SubmitFields(
        byte SourceTon, byte SourceNpi, string Source,
        byte DestinationTon, byte DestinationNpi, string Destination,
        byte DataCoding, byte[] ShortMessage, byte[]? Payload)
    {
        public static SubmitFields Read(byte[] body)
        {
            var r = new PduReader(body);
            r.CString();                                   // service_type
            var sourceTon = r.Byte();
            var sourceNpi = r.Byte();
            var source = r.CString();
            var destinationTon = r.Byte();
            var destinationNpi = r.Byte();
            var destination = r.CString();
            r.Byte();                                      // esm_class
            r.Byte();                                      // protocol_id
            r.Byte();                                      // priority_flag
            r.CString();                                   // schedule_delivery_time
            r.CString();                                   // validity_period
            r.Byte();                                      // registered_delivery
            r.Byte();                                      // replace_if_present_flag
            var dataCoding = r.Byte();
            r.Byte();                                      // sm_default_msg_id
            var shortMessage = r.Bytes(r.Byte());

            byte[]? payload = null;
            while (!r.AtEnd)
            {
                var tag = r.UInt16();
                var value = r.Bytes(r.UInt16());
                if (tag == 0x0424)
                {
                    payload = value;
                }
            }

            return new SubmitFields(sourceTon, sourceNpi, source, destinationTon, destinationNpi,
                destination, dataCoding, shortMessage, payload);
        }
    }
}
