using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Yesify.Services;

/// <summary>Plays local audio files through Windows Media Foundation + WaveOut.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private PreciseSeekReader? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private SpectrumTap? _tap;
    private float _volume = 0.8f;

    /// <summary>Raised on the UI thread when a track plays to the end.</summary>
    public event EventHandler? TrackEnded;

    /// <summary>Raised on the UI thread when decoding or the audio device fails mid-track.</summary>
    public event EventHandler<Exception>? PlaybackFailed;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool HasTrack => _reader != null;
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    // The decoder runs ahead of the speakers by however much audio is queued in the output, and moves in
    // ~100 ms chunks. For lyrics we want what's audible right now, advancing every frame. The output knows
    // exactly how many samples it has played; together with how many we've handed it, that gives the
    // amount still in flight, measured rather than guessed. Past the output, Windows' mixer and the device
    // add a small fixed delay.
    private static readonly TimeSpan DeviceDelay = TimeSpan.FromMilliseconds(30);
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private TimeSpan _clockBase;
    private OutputCounter? _counter;

    /// <summary>Stream position of the sound coming out of the speakers right now.</summary>
    private TimeSpan AudiblePosition
    {
        get
        {
            if (_reader == null || _output == null || _counter == null) return Position;
            var (handedFrames, streamTimeAfter) = _counter.Snapshot();
            long playedFrames;
            try { playedFrames = _output.GetPosition() / _output.OutputWaveFormat.BlockAlign; }
            catch { return Position; } // output not started yet
            var inFlight = Math.Max(0, handedFrames - playedFrames) / (double)_counter.WaveFormat.SampleRate;
            var audible = streamTimeAfter - TimeSpan.FromSeconds(inFlight) - DeviceDelay;
            return audible < TimeSpan.Zero ? TimeSpan.Zero : audible;
        }
    }

    /// <summary>Continuously advancing audible position (for lyric animation).</summary>
    public TimeSpan SmoothPosition
    {
        get
        {
            if (_reader == null) return TimeSpan.Zero;
            var raw = AudiblePosition;
            if (!IsPlaying || _reader.IsSeeking)
            {
                // Paused, or silence while a seek is prepared: hold still at the target.
                _clock.Reset();
                return raw;
            }
            if (!_clock.IsRunning)
            {
                _clockBase = raw;
                _clock.Restart();
            }

            var predicted = _clockBase + _clock.Elapsed;
            var drift = (raw - predicted).TotalMilliseconds;
            if (Math.Abs(drift) > 400)
            {
                // Seek or hiccup: jump.
                _clockBase = raw;
                _clock.Restart();
                return raw;
            }
            // Otherwise follow the measured position closely, smoothing out its small steps.
            _clockBase += TimeSpan.FromMilliseconds(drift * 0.15);
            return predicted;
        }
    }

    /// <summary>
    /// Last stage before the output: counts the frames handed over and records the stream position right
    /// after each hand-over, as one consistent pair (the output reads on its own thread).
    /// </summary>
    private sealed class OutputCounter(ISampleProvider source, Func<TimeSpan> streamPosition) : ISampleProvider
    {
        private readonly object _gate = new();
        private long _frames;
        private TimeSpan _after;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = source.Read(buffer, offset, count);
            lock (_gate)
            {
                _frames += n / WaveFormat.Channels;
                _after = streamPosition();
            }
            return n;
        }

        public (long Frames, TimeSpan StreamTimeAfter) Snapshot()
        {
            lock (_gate) return (_frames, _after);
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider != null) _volumeProvider.Volume = Curve(_volume);
        }
    }

    // Perceptual volume curve so the slider feels linear.
    private static float Curve(float v) => v * v;

    /// <summary>
    /// Opens a file for <see cref="Play"/> on a background thread: Media Foundation takes up to ~160 ms to
    /// open one (the first is slowest), which used to freeze the window on every skip.
    /// </summary>
    public static Task<PreciseSeekReader> OpenAsync(string path) =>
        // Media Foundation decodes; PreciseSeekReader makes seeking exact (see there).
        Task.Run(() => new PreciseSeekReader(() => new MediaFoundationReader(path)));

    /// <summary>Plays a file opened with <see cref="OpenAsync"/>; the player owns it from here.</summary>
    public void Play(PreciseSeekReader opened)
    {
        Stop();
        _reader = opened;
        _tap = new SpectrumTap(_reader.ToSampleProvider());
        _volumeProvider = new VolumeSampleProvider(_tap) { Volume = Curve(_volume) };
        var reader = _reader;
        _counter = new OutputCounter(_volumeProvider, () => reader.CurrentTime);
        // Created on the UI thread, so PlaybackStopped is marshalled back to it.
        _output = new WaveOutEvent { DesiredLatency = 200 };
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_counter);
        _output.Play();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Ignore events from outputs we already replaced or stopped on purpose.
        if (!ReferenceEquals(sender, _output)) return;
        if (e.Exception != null) PlaybackFailed?.Invoke(this, e.Exception);
        else TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Pause() => _output?.Pause();

    public void Resume()
    {
        if (_output?.PlaybackState == PlaybackState.Paused) _output.Play();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader == null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > _reader.TotalTime) position = _reader.TotalTime;
        _reader.CurrentTime = position;
    }

    public void Stop()
    {
        var output = _output;
        var reader = _reader;
        _output = null;
        _reader = null;
        _volumeProvider = null;
        _counter = null;
        _tap = null;
        if (output != null) output.PlaybackStopped -= OnPlaybackStopped;
        if (output == null && reader == null) return;
        // Stopping the device waits for its playback thread, and the decoder can only go once it's stopped:
        // done off the UI thread so skipping a song never stalls the window.
        Task.Run(() =>
        {
            try
            {
                output?.Stop();
                output?.Dispose();
            }
            catch { /* the device is going away anyway */ }
            reader?.Dispose();
        });
    }

    /// <summary>
    /// What's coming out of the speakers right now, as <paramref name="bands"/> levels from low to high
    /// (0..1, log-spaced from 40 Hz to 16 kHz), for Home's visualizer. False when nothing is playing.
    /// Measured before the volume, so it shows the music, not the volume knob.
    /// </summary>
    public bool ReadSpectrum(float[] bands)
    {
        if (_tap is not { } tap || _output is not { PlaybackState: PlaybackState.Playing } output) return false;
        long played;
        try { played = output.GetPosition() / output.OutputWaveFormat.BlockAlign; }
        catch { return false; }
        return tap.Read(played, bands);
    }

    /// <summary>
    /// Keeps the last moment of decoded audio (mixed to mono) so the visualizer can look at exactly what the
    /// speakers are playing: the decoder runs ahead of them by the output's buffers.
    /// </summary>
    private sealed class SpectrumTap(ISampleProvider source) : ISampleProvider
    {
        private const int Ring = 1 << 15; // frames; well over the output's 200 ms of buffers
        private const int Fft = 2048, FftBits = 11;
        private readonly float[] _ring = new float[Ring];
        private long _written; // frames written since the song started
        private readonly NAudio.Dsp.Complex[] _fft = new NAudio.Dsp.Complex[Fft];
        private static readonly float[] Window = Enumerable.Range(0, Fft).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1)))).ToArray();

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = source.Read(buffer, offset, count);
            var ch = WaveFormat.Channels;
            var w = Interlocked.Read(ref _written);
            for (var i = 0; i + ch <= n; i += ch)
            {
                float sum = 0;
                for (var c = 0; c < ch; c++) sum += buffer[offset + i + c];
                _ring[(int)(w++ & (Ring - 1))] = sum / ch;
            }
            Interlocked.Exchange(ref _written, w);
            return n;
        }

        /// <summary>The spectrum of the <see cref="Fft"/> frames that end at frame <paramref name="audible"/>.</summary>
        public bool Read(long audible, float[] bands)
        {
            var written = Interlocked.Read(ref _written);
            var end = Math.Min(audible, written);
            if (end < Fft || written - end > Ring - Fft) return false;
            for (var i = 0; i < Fft; i++)
            {
                _fft[i].X = _ring[(int)((end - Fft + i) & (Ring - 1))] * Window[i];
                _fft[i].Y = 0;
            }
            NAudio.Dsp.FastFourierTransform.FFT(true, FftBits, _fft);

            var rate = WaveFormat.SampleRate;
            double lo = 40, hi = Math.Min(16000, rate / 2.0);
            for (var b = 0; b < bands.Length; b++)
            {
                // Each band spans an equal step on a log scale, at least one FFT bin wide.
                var f0 = lo * Math.Pow(hi / lo, b / (double)bands.Length);
                var f1 = lo * Math.Pow(hi / lo, (b + 1) / (double)bands.Length);
                var i0 = Math.Max(1, (int)(f0 * Fft / rate));
                var i1 = Math.Max(i0 + 1, (int)(f1 * Fft / rate));
                double peak = 0;
                for (var i = i0; i < i1 && i < Fft / 2; i++)
                    peak = Math.Max(peak, Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
                // NAudio's forward FFT divides by N; a full-scale sine peaks near 0.25 after the Hann window.
                var db = 20 * Math.Log10(peak * 4 + 1e-9);
                // Highs carry less energy: tilt so the ring isn't all bass.
                db += 3.0 * b / bands.Length * 4;
                bands[b] = (float)Math.Clamp((db + 62) / 54, 0, 1);
            }
            return true;
        }
    }

    /// <summary>On exit: stop right away, so nothing keeps playing while the app closes.</summary>
    public void Dispose()
    {
        var output = _output;
        var reader = _reader;
        _output = null;
        _reader = null;
        _volumeProvider = null;
        _counter = null;
        if (output != null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Stop();
            output.Dispose();
        }
        reader?.Dispose();
    }
}
