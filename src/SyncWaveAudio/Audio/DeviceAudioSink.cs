using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SyncWaveAudio.Models;

namespace SyncWaveAudio.Audio;

public sealed class DeviceAudioSink : IDisposable
{
    private readonly AudioDeviceInfo _deviceInfo;
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;
    private readonly WaveFormat _format;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly byte[] _silenceChunk;
    private readonly AudioEndpointVolume _endpointVolume;
    private double _lastEndpointVolume = -1;
    private double _smoothedTargetDelayMs;
    private bool _disposed;
    private bool _debugLogging;

    private long _totalTrimmedBytes;
    private long _totalSilenceBytes;
    private long _totalOverflows;
    private long _totalEnqueues;
    private int _consecutiveDriftCorrections;

    public DeviceAudioSink(AudioDeviceInfo deviceInfo, MMDevice endpoint, WaveFormat format, int baseBufferMs, ILogger logger, bool debugLogging)
    {
        _deviceInfo = deviceInfo;
        _format = format;
        _logger = logger;
        _debugLogging = debugLogging;

        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(Math.Max(500, baseBufferMs * 3)),
            DiscardOnBufferOverflow = true
        };

        _silenceChunk = new byte[Math.Max(format.AverageBytesPerSecond / 100, format.BlockAlign)];
        _endpointVolume = endpoint.AudioEndpointVolume;

        // Use minimal WasapiOut latency to reduce pipeline delay
        var outputLatency = Math.Max(20, baseBufferMs / 3);
        _output = new WasapiOut(endpoint, AudioClientShareMode.Shared, false, outputLatency);
        _output.Init(_buffer);

        _logger.LogInformation("[Sink:{Device}] Created — outputLatency={OutMs}ms, capacity={CapMs}ms, format={Rate}Hz/{Ch}ch",
            _deviceInfo.FriendlyName, outputLatency, _buffer.BufferDuration.TotalMilliseconds,
            format.SampleRate, format.Channels);
    }

    public string DeviceId => _deviceInfo.Id;
    public int BufferedMilliseconds => (int)_buffer.BufferedDuration.TotalMilliseconds;
    public double EffectiveDelayMs { get; private set; }
    public double DriftMs { get; private set; }
    public long TotalTrimmedBytes => _totalTrimmedBytes;
    public long TotalSilenceBytes => _totalSilenceBytes;
    public long TotalOverflows => _totalOverflows;

    public void SetDebugLogging(bool enabled) => _debugLogging = enabled;

    /// <summary>Target buffer level in ms — kept minimal to reduce pipeline latency.</summary>
    private double TargetMs(int baseBufferMs) => Math.Max(8, baseBufferMs * 0.12) + EffectiveDelayMs;

    public void Prime(double effectiveDelayMs, int baseBufferMs)
    {
        EffectiveDelayMs = Math.Max(0, effectiveDelayMs);
        _smoothedTargetDelayMs = EffectiveDelayMs;

        var primeMs = TargetMs(baseBufferMs);
        var primeBytes = MillisecondsToBytes(primeMs);
        AddSilence(primeBytes);

        _logger.LogInformation("[Sink:{Device}] Primed — delay={DelayMs:F1}ms, prime={PrimeMs:F1}ms, buffered={BufMs}ms",
            _deviceInfo.FriendlyName, EffectiveDelayMs, primeMs, BufferedMilliseconds);
    }

    public void Start()
    {
        _output.Play();
        _logger.LogInformation("[Sink:{Device}] Started — buffered={BufMs}ms", _deviceInfo.FriendlyName, BufferedMilliseconds);
    }

    public void Stop()
    {
        _logger.LogInformation("[Sink:{Device}] Stop — enqueues={E}, trimmed={T}B, silence={S}B, overflows={O}",
            _deviceInfo.FriendlyName, _totalEnqueues, _totalTrimmedBytes, _totalSilenceBytes, _totalOverflows);
        _output.Stop();
        _buffer.ClearBuffer();
    }

    public void Enqueue(byte[] source, int count, double effectiveDelayMs, int baseBufferMs)
    {
        if (_disposed) return;

        lock (_gate)
        {
            _totalEnqueues++;
            EffectiveDelayMs = SmoothDelay(effectiveDelayMs);
            ApplyEndpointVolume();

            var sampleGain = (float)Math.Max(1.0, _deviceInfo.Volume);
            var processed = AudioSampleProcessor.Apply(source, count, _format, sampleGain, _deviceInfo.Mono);

            var capacityMs = _buffer.BufferDuration.TotalMilliseconds;
            if (BufferedMilliseconds + BytesToMilliseconds(count) > capacityMs)
            {
                _totalOverflows++;
                if (_debugLogging)
                    _logger.LogWarning("[Sink:{Device}] OVERFLOW #{N}", _deviceInfo.FriendlyName, _totalOverflows);
            }

            _buffer.AddSamples(processed, 0, processed.Length);
            CorrectDrift(TargetMs(baseBufferMs));

            if (_debugLogging && _totalEnqueues % 100 == 0)
            {
                _logger.LogDebug("[Sink:{Device}] #{N} buf={BufMs}ms target={TMs:F0}ms drift={D:F1}ms",
                    _deviceInfo.FriendlyName, _totalEnqueues, BufferedMilliseconds, TargetMs(baseBufferMs), DriftMs);
            }
        }
    }

    private double SmoothDelay(double requestedDelayMs)
    {
        requestedDelayMs = Math.Max(0, requestedDelayMs);
        var delta = requestedDelayMs - _smoothedTargetDelayMs;
        var step = Math.Clamp(delta, -12, 12);
        _smoothedTargetDelayMs += step;
        return Math.Round(_smoothedTargetDelayMs, 2);
    }

    public DeviceSyncState Snapshot()
    {
        return new DeviceSyncState(
            _deviceInfo.Id, _deviceInfo.EstimatedLatencyMs, _deviceInfo.ManualDelayMs,
            EffectiveDelayMs, DriftMs, BufferedMilliseconds, _deviceInfo.Status,
            _totalTrimmedBytes, _totalSilenceBytes, _totalOverflows);
    }

    private void CorrectDrift(double targetMs)
    {
        var bufferedMs = _buffer.BufferedDuration.TotalMilliseconds;
        DriftMs = Math.Round(bufferedMs - targetMs, 2);

        // Aggressive initial stabilization for first 50 enqueues
        var isInitial = _totalEnqueues < 50;
        var trimThreshold = isInitial ? 4.0 : 8.0;
        var padThreshold = isInitial ? -3.0 : -6.0;
        var maxTrimMs = isInitial ? 10.0 : 5.0;
        var maxPadMs = isInitial ? 8.0 : 4.0;

        if (DriftMs > trimThreshold)
        {
            var trimMs = Math.Min(maxTrimMs, DriftMs / 2);
            var trimBytes = MillisecondsToBytes(trimMs);
            if (trimBytes > 0)
            {
                var scratch = new byte[trimBytes];
                _buffer.Read(scratch, 0, scratch.Length);
                _totalTrimmedBytes += trimBytes;
                _consecutiveDriftCorrections++;
                if (_debugLogging)
                    _logger.LogDebug("[Sink:{Device}] TRIM {TMs:F1}ms drift={D:F1}ms", _deviceInfo.FriendlyName, trimMs, DriftMs);
            }
        }
        else if (DriftMs < padThreshold)
        {
            var padMs = Math.Min(maxPadMs, Math.Abs(DriftMs) / 2);
            var padBytes = MillisecondsToBytes(padMs);
            AddSilence(padBytes);
            _totalSilenceBytes += padBytes;
            _consecutiveDriftCorrections++;
            if (_debugLogging)
                _logger.LogDebug("[Sink:{Device}] PAD {PMs:F1}ms drift={D:F1}ms", _deviceInfo.FriendlyName, padMs, DriftMs);
        }
        else
        {
            _consecutiveDriftCorrections = 0;
        }
    }

    private void AddSilence(int bytes)
    {
        while (bytes > 0)
        {
            var next = Math.Min(bytes, _silenceChunk.Length);
            _buffer.AddSamples(_silenceChunk, 0, next);
            bytes -= next;
        }
    }

    private void ApplyEndpointVolume()
    {
        var vol = Math.Clamp(_deviceInfo.Volume, 0.0, 1.0);
        if (Math.Abs(vol - _lastEndpointVolume) < 0.01) return;
        _endpointVolume.MasterVolumeLevelScalar = (float)vol;
        _lastEndpointVolume = vol;
    }

    private int MillisecondsToBytes(double ms)
    {
        var bytes = (int)(_format.AverageBytesPerSecond * ms / 1000.0);
        return bytes - bytes % _format.BlockAlign;
    }

    private double BytesToMilliseconds(int bytes) => bytes * 1000.0 / _format.AverageBytesPerSecond;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Dispose();
    }
}
