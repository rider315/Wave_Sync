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
    private readonly int _outputLatencyMs;
    private double _lastEndpointVolume = -1;
    private double _smoothedTargetDelayMs;
    private double _maxChunkMs = 10.0;
    private bool _disposed;
    private bool _debugLogging;

    private long _totalTrimmedBytes;
    private long _totalSilenceBytes;
    private long _totalOverflows;
    private long _totalEnqueues;
    private int _consecutiveDriftCorrections;

    /// <summary>Optional callback to route important log entries to the UI.</summary>
    public Action<SyncLogEntry>? OnLog { get; set; }

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

        // WasapiOut latency — this determines minimum viable buffer level
        _outputLatencyMs = Math.Max(20, baseBufferMs / 3);
        _output = new WasapiOut(endpoint, AudioClientShareMode.Shared, false, _outputLatencyMs);
        _output.Init(_buffer);

        EmitLog(LogCategory.Device, $"Sink created — outputLatency={_outputLatencyMs}ms, target~{TargetMs(baseBufferMs):F0}ms", deviceInfo.FriendlyName);
    }

    public string DeviceId => _deviceInfo.Id;
    public int BufferedMilliseconds => (int)_buffer.BufferedDuration.TotalMilliseconds;
    public double EffectiveDelayMs { get; private set; }
    public double DriftMs { get; private set; }
    public long TotalTrimmedBytes => _totalTrimmedBytes;
    public long TotalSilenceBytes => _totalSilenceBytes;
    public long TotalOverflows => _totalOverflows;

    public void SetDebugLogging(bool enabled) => _debugLogging = enabled;

    /// <summary>
    /// Target buffer level (peak). Must hold at least the max incoming chunk plus output latency.
    /// We dynamically track max chunk size and add a 20% safety margin.
    /// </summary>
    private double TargetMs(int baseBufferMs)
    {
        var safeMinimum = (_maxChunkMs * 1.2) + _outputLatencyMs;
        var target = Math.Max(baseBufferMs, safeMinimum);
        return target + EffectiveDelayMs;
    }

    public void Prime(double effectiveDelayMs, int baseBufferMs)
    {
        EffectiveDelayMs = Math.Max(0, effectiveDelayMs);
        _smoothedTargetDelayMs = EffectiveDelayMs;

        var primeMs = TargetMs(baseBufferMs);
        var primeBytes = MillisecondsToBytes(primeMs);
        AddSilence(primeBytes);

        EmitLog(LogCategory.Buffer, $"Primed — delay={EffectiveDelayMs:F1}ms, target={primeMs:F1}ms, buffered={BufferedMilliseconds}ms", _deviceInfo.FriendlyName);
    }

    public void Start()
    {
        _output.Play();
        EmitLog(LogCategory.Device, $"Playback started — buffered={BufferedMilliseconds}ms", _deviceInfo.FriendlyName);
    }

    public void Stop()
    {
        EmitLog(LogCategory.Device, $"Stopped — enqueues={_totalEnqueues}, trimmed={_totalTrimmedBytes}B, silence={_totalSilenceBytes}B, overflows={_totalOverflows}", _deviceInfo.FriendlyName);
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

            var chunkMs = BytesToMilliseconds(count);
            if (chunkMs > _maxChunkMs) _maxChunkMs = chunkMs;
            else _maxChunkMs = Math.Max(10.0, _maxChunkMs * 0.999); // Slow decay

            var capacityMs = _buffer.BufferDuration.TotalMilliseconds;
            if (BufferedMilliseconds + BytesToMilliseconds(count) > capacityMs)
            {
                _totalOverflows++;
                EmitLog(LogCategory.Buffer, $"OVERFLOW #{_totalOverflows} — buf={BufferedMilliseconds}ms + {BytesToMilliseconds(count):F0}ms > {capacityMs:F0}ms", _deviceInfo.FriendlyName, Models.LogLevel.Warning);
            }

            _buffer.AddSamples(processed, 0, processed.Length);

            var target = TargetMs(baseBufferMs);
            CorrectDrift(target);

            // Periodic stats to UI (every 50 enqueues = ~500ms)
            if (_totalEnqueues % 50 == 0)
            {
                EmitLog(LogCategory.Drift, $"#{_totalEnqueues} buf={BufferedMilliseconds}ms target={target:F0}ms drift={DriftMs:F1}ms trim={_totalTrimmedBytes}B pad={_totalSilenceBytes}B", _deviceInfo.FriendlyName);
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

        var absDrift = Math.Abs(DriftMs);

        // Dead zone: ±5ms — well below human auditory threshold (~10ms)
        // This prevents constant TRIM/PAD oscillation with chunky BT delivery
        if (DriftMs > 5.0)
        {
            // Ahead of target — trim excess (50% of overshoot, max 12ms)
            var trimMs = Math.Min(12, absDrift * 0.5);
            var trimBytes = MillisecondsToBytes(trimMs);
            if (trimBytes > 0)
            {
                var scratch = new byte[trimBytes];
                _buffer.Read(scratch, 0, scratch.Length);
                _totalTrimmedBytes += trimBytes;
                _consecutiveDriftCorrections++;
                if (_consecutiveDriftCorrections <= 3 || _consecutiveDriftCorrections % 50 == 0)
                    EmitLog(LogCategory.Drift, $"TRIM {trimMs:F1}ms drift={DriftMs:F1}ms buf={BufferedMilliseconds}ms", _deviceInfo.FriendlyName);
            }
        }
        else if (DriftMs < -5.0)
        {
            // Behind target — inject silence (40% of deficit, max 8ms)
            var padMs = Math.Min(8, absDrift * 0.4);
            var padBytes = MillisecondsToBytes(padMs);
            AddSilence(padBytes);
            _totalSilenceBytes += padBytes;
            _consecutiveDriftCorrections++;
            if (_consecutiveDriftCorrections <= 3 || _consecutiveDriftCorrections % 50 == 0)
                EmitLog(LogCategory.Drift, $"PAD {padMs:F1}ms drift={DriftMs:F1}ms buf={BufferedMilliseconds}ms", _deviceInfo.FriendlyName);
        }
        else
        {
            // Within ±5ms — locked and stable
            if (_consecutiveDriftCorrections > 0)
                EmitLog(LogCategory.Drift, $"LOCKED — drift={DriftMs:F1}ms after {_consecutiveDriftCorrections} corrections", _deviceInfo.FriendlyName);
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

    private void EmitLog(LogCategory category, string message, string deviceName, Models.LogLevel level = Models.LogLevel.Info)
    {
        if (_debugLogging)
            _logger.LogDebug("[Sink:{Device}] {Message}", deviceName, message);

        OnLog?.Invoke(new SyncLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Category = category,
            DeviceName = deviceName,
            Message = message
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Dispose();
    }
}
