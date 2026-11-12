namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// A stream whose first bytes have been looked at and are still to be read.
/// </summary>
/// <remarks>
/// What a file is has to be known before it is stored - a plain file is written as it is, a gzip
/// through a decompressor, an archive unpacked - and it is told from the first bytes
/// (<see cref="FileSignature"/>). An upload's body cannot be rewound, so the bytes looked at are
/// kept and given back first.
/// </remarks>
internal sealed class PeekedStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _head;
    private readonly int _headLength;
    private int _served;

    private PeekedStream(Stream inner, byte[] head, int headLength)
    {
        _inner = inner;
        _head = head;
        _headLength = headLength;
    }

    /// <summary>Reads up to <paramref name="count"/> bytes ahead; fewer only when the stream ends first.</summary>
    public static async Task<PeekedStream> OpenAsync(Stream inner, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inner);

        var head = new byte[count];
        var read = 0;

        // A network stream hands over what has arrived, which may be less than was asked for.
        while (read < count)
        {
            var got = await inner.ReadAsync(head.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return new PeekedStream(inner, head, read);
    }

    /// <summary>The bytes looked at. All of the stream, when it is shorter than what was asked for.</summary>
    public ReadOnlySpan<byte> Head => _head.AsSpan(0, _headLength);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var fromHead = ServeHead(buffer);
        return fromHead > 0 ? fromHead : _inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var fromHead = ServeHead(buffer.Span);
        return fromHead > 0 ? ValueTask.FromResult(fromHead) : _inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int ServeHead(Span<byte> buffer)
    {
        var left = _headLength - _served;
        if (left <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        var take = Math.Min(left, buffer.Length);
        _head.AsSpan(_served, take).CopyTo(buffer);
        _served += take;
        return take;
    }
}
