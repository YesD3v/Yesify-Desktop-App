using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Yesify.Services;

/// <summary>Plays local audio files through Windows Media Foundation + WaveOut.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private PreciseSeekReader? _reader;
    private VolumeSampleProvider? _volumeProvider;
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

    public void Play(string path)
    {
        Stop();
        // Media Foundation decodes; PreciseSeekReader makes seeking exact (see there).
        _reader = new PreciseSeekReader(() => new MediaFoundationReader(path));
        _volumeProvider = new VolumeSampleProvider(_reader.ToSampleProvider()) { Volume = Curve(_volume) };
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
        if (output != null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Stop();
            output.Dispose();
        }
        reader?.Dispose();
    }

    public void Dispose() => Stop();
}
