using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Quonfig.Sdk.Transport;

/// <summary>
/// The cross-SDK size caps (qfg-6vno.8, integration-test-data chaos/supervisor-test-contract.md
/// "Rule — size caps"). Constants, not options: if a real workspace ever nears a cap, the
/// constants are raised in one ITD change and fanned out to every SDK.
/// </summary>
internal static class SizeCaps
{
    /// <summary>One SSE event (any line, and the joined multi-line <c>data:</c>): 4 MiB.</summary>
    internal const int MaxSseEventBytes = 4 * 1024 * 1024;

    /// <summary>An HTTP 200 body from <c>/api/v2/configs</c>: 16 MiB.</summary>
    internal const long MaxConfigBodyBytes = 16L * 1024 * 1024;
}

/// <summary>
/// A read-only pass-through that counts bytes and, as soon as more than <c>cap</c> bytes have
/// been read, sets <see cref="Exceeded"/> and throws <see cref="IOException"/>, so the
/// deserializer above it never buffers more than about the cap plus one read chunk. Owns (and
/// disposes) the inner stream.
/// </summary>
internal sealed class CappedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _cap;
    private long _read;

    public CappedReadStream(Stream inner, long cap)
    {
        _inner = inner;
        _cap = cap;
    }

    /// <summary>True once more than the cap was read.</summary>
    public bool Exceeded { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1835",
        Justification = "Array-based override forwards to the array-based inner overload; the Memory overload is overridden separately on net8.0.")]
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));

#if NET8_0_OR_GREATER
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
#endif

    private int Count(int n)
    {
        _read += n;
        if (_read > _cap)
        {
            Exceeded = true;
            throw new IOException(FormattableString.Invariant($"body exceeds the maximum size of {_cap} bytes"));
        }
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A byte-level SSE line reader that never buffers more than <c>maxLineBytes</c> for one line,
/// even for a line with no terminator: it stops reading as soon as the cap is passed and reports
/// <see cref="Oversized"/>. Splits on CRLF, LF or CR (the SSE spec, and what
/// <see cref="StreamReader.ReadLine()"/> did before), strips a leading UTF-8 BOM, and returns a
/// trailing unterminated line at EOF. Works on netstandard2.0 (no <c>ReadLineAsync</c> limit
/// exists there).
/// </summary>
internal sealed class BoundedLineReader
{
    private const int ChunkSize = 4096;

    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _buf = new byte[ChunkSize];
    private int _pos;
    private int _len;
    private bool _eof;
    private bool _skipLf;
    private bool _first = true;
    private byte[] _line = new byte[256];
    private int _lineLen;

    public BoundedLineReader(Stream stream, int maxLineBytes)
    {
        _stream = stream;
        _maxLineBytes = maxLineBytes;
    }

    /// <summary>True once a line passed the cap; the reader returns null from then on.</summary>
    public bool Oversized { get; private set; }

    /// <summary>The UTF-8 byte length of the line most recently returned (terminator excluded).</summary>
    public int LastLineBytes { get; private set; }

    /// <summary>
    /// The next line, or null at EOF or once <see cref="Oversized"/> is set. Stream exceptions
    /// (dispose, IO error, cancellation) propagate unchanged.
    /// </summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (Oversized) return null;
        _lineLen = 0;
        while (true)
        {
            if (_pos >= _len)
            {
                if (_eof) return _lineLen > 0 ? Finish() : null;
#if NET8_0_OR_GREATER
                int n = await _stream.ReadAsync(_buf.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
#else
                int n = await _stream.ReadAsync(_buf, 0, ChunkSize, cancellationToken).ConfigureAwait(false);
#endif
                if (n == 0)
                {
                    _eof = true;
                    continue;
                }
                _pos = 0;
                _len = n;
            }
            if (_skipLf)
            {
                _skipLf = false;
                if (_buf[_pos] == (byte)'\n')
                {
                    _pos++;
                    continue;
                }
            }
            int start = _pos;
            while (_pos < _len && _buf[_pos] != (byte)'\n' && _buf[_pos] != (byte)'\r')
            {
                _pos++;
            }
            if (!Append(start, _pos - start))
            {
                Oversized = true;
                return null;
            }
            if (_pos < _len)
            {
                if (_buf[_pos] == (byte)'\r') _skipLf = true;
                _pos++;
                return Finish();
            }
        }
    }

    private bool Append(int start, int count)
    {
        if (count == 0) return true;
        if ((long)_lineLen + count > _maxLineBytes) return false;
        if (_lineLen + count > _line.Length)
        {
            int size = _line.Length;
            while (size < _lineLen + count) size = (int)Math.Min((long)size * 2, _maxLineBytes);
            var grown = new byte[size];
            Buffer.BlockCopy(_line, 0, grown, 0, _lineLen);
            _line = grown;
        }
        Buffer.BlockCopy(_buf, start, _line, _lineLen, count);
        _lineLen += count;
        return true;
    }

    private string Finish()
    {
        int offset = 0;
        if (_first)
        {
            _first = false;
            if (_lineLen >= 3 && _line[0] == 0xEF && _line[1] == 0xBB && _line[2] == 0xBF) offset = 3;
        }
        LastLineBytes = _lineLen - offset;
        return Encoding.UTF8.GetString(_line, offset, _lineLen - offset);
    }
}
