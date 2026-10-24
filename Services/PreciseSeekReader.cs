using NAudio.Wave;

namespace Yesify.Services;

/// <summary>
/// Wraps a decoder so seeking lands on the exact sample.
/// <para>
/// Media Foundation can't be trusted to seek YouTube's fragmented MP4 audio: measured, it starts playing
/// up to 14 s away from the requested time while reporting the requested time, so audio and position (and
/// with it the lyrics) disagree. Seeks are therefore done without it:
/// </para>
/// <list type="bullet">
/// <item>back within the last 12 s: replayed from a buffer of recently decoded audio (instant);</item>
/// <item>forward up to 20 s: decoded through on the spot (~100 ms);</item>
/// <item>anything else: a fresh decoder works its way from the start (always exact) on a background thread
/// while silence plays and the position holds at the target, then takes over.</item>
/// </list>
/// All use of the playing decoder happens on the playback thread, inside <see cref="Read"/>.
/// </summary>
public sealed class PreciseSeekReader : WaveStream
{
    private static readonly TimeSpan InlineForward = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(12);

    private readonly Func<WaveStream> _open;
    private readonly byte[] _scratch = new byte[64 * 1024];
    private WaveStream _source;

    // _position: what's being played. _sourcePos: how far the decoder has got. Bytes in
    // [_ringStart, _sourcePos) are also kept in _ring, so jumping back into that range is just a replay.
    private long _position;
    private long _sourcePos;
    private long _ringStart;
    private readonly byte[] _ring;

    private long _pendingSeek = -1;
    private Task<WaveStream>? _prepared;
    private CancellationTokenSource? _prepareCts;
    private long _preparedTarget = -1;

    /// <param name="open">Opens a fresh decoder positioned at the start.</param>
    public PreciseSeekReader(Func<WaveStream> open)
    {
        _open = open;
        _source = open();
        var align = _source.WaveFormat.BlockAlign;
        _ring = new byte[(int)(ReplayWindow.TotalSeconds * _source.WaveFormat.AverageBytesPerSecond) / align * align];
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => _source.Length;

    /// <summary>True while a far seek is being prepared (silence is playing).</summary>
    public bool IsSeeking => Interlocked.Read(ref _pendingSeek) >= 0 || Interlocked.Read(ref _preparedTarget) >= 0;

    public override long Position
    {
        get
        {
            var pending = Interlocked.Read(ref _pendingSeek);
            if (pending >= 0) return pending;
            var preparing = Interlocked.Read(ref _preparedTarget);
            return preparing >= 0 ? preparing : Interlocked.Read(ref _position);
        }
        set
        {
            var align = WaveFormat.BlockAlign;
            Interlocked.Exchange(ref _pendingSeek, Math.Clamp(value / align * align, 0, Length));
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var target = Interlocked.Exchange(ref _pendingSeek, -1);
        if (target >= 0) StartSeek(target);

        if (_prepared != null)
        {
            if (!_prepared.IsCompleted)
            {
                Array.Clear(buffer, offset, count); // silence until the exact spot is ready
                return count;
            }
            if (_prepared.Status == TaskStatus.RanToCompletion)
            {
                _source.Dispose();
                _source = _prepared.Result;
                var at = Interlocked.Read(ref _preparedTarget);
                _sourcePos = _ringStart = at;
                Interlocked.Exchange(ref _position, at);
            }
            _prepared = null;
            Interlocked.Exchange(ref _preparedTarget, -1);
        }

        var pos = Interlocked.Read(ref _position);
        int read;
        if (pos < _sourcePos)
        {
            read = CopyFromRing(pos, buffer, offset, (int)Math.Min(count, _sourcePos - pos)); // replaying
        }
        else
        {
            read = _source.Read(buffer, offset, count);
            Remember(buffer, offset, read);
        }
        Interlocked.Add(ref _position, read);
        return read;
    }

    private void StartSeek(long target)
    {
        CancelPrepare();
        if (target >= _ringStart && target <= _sourcePos)
        {
            Interlocked.Exchange(ref _position, target); // within what's already decoded
            return;
        }

        var inlineLimit = (long)(InlineForward.TotalSeconds * WaveFormat.AverageBytesPerSecond);
        if (target > _sourcePos && target - _sourcePos <= inlineLimit)
        {
            // Short hop forward: decode through it (kept in the replay buffer too).
            while (_sourcePos < target)
            {
                var n = _source.Read(_scratch, 0, (int)Math.Min(_scratch.Length, target - _sourcePos));
                if (n <= 0) break;
                Remember(_scratch, 0, n);
            }
            Interlocked.Exchange(ref _position, _sourcePos);
            return;
        }

        Interlocked.Exchange(ref _preparedTarget, target);
        var cts = _prepareCts = new CancellationTokenSource();
        _prepared = Task.Run(() =>
        {
            var reader = _open();
            try
            {
                var scratch = new byte[64 * 1024];
                long p = 0;
                while (p < target)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var n = reader.Read(scratch, 0, (int)Math.Min(scratch.Length, target - p));
                    if (n <= 0) break;
                    p += n;
                }
                return reader;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }, cts.Token);
    }

    // Appends freshly decoded bytes to the replay buffer.
    private void Remember(byte[] data, int offset, int count)
    {
        for (var done = 0; done < count;)
        {
            var at = (int)(_sourcePos % _ring.Length);
            var n = Math.Min(count - done, _ring.Length - at);
            Buffer.BlockCopy(data, offset + done, _ring, at, n);
            done += n;
            _sourcePos += n;
        }
        _ringStart = Math.Max(_ringStart, _sourcePos - _ring.Length);
    }

    private int CopyFromRing(long from, byte[] buffer, int offset, int count)
    {
        for (var done = 0; done < count;)
        {
            var at = (int)((from + done) % _ring.Length);
            var n = Math.Min(count - done, _ring.Length - at);
            Buffer.BlockCopy(_ring, at, buffer, offset + done, n);
            done += n;
        }
        return count;
    }

    private void CancelPrepare()
    {
        if (_prepared == null) return;
        _prepareCts?.Cancel();
        // A preparation that finishes anyway still owns its decoder.
        _prepared.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); });
        _prepared = null;
        Interlocked.Exchange(ref _preparedTarget, -1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelPrepare();
            _source.Dispose();
        }
        base.Dispose(disposing);
    }
}
