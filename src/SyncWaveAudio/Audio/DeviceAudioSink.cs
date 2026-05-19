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
    private double _bufferOffsetMs;
    private bool _disposed;
    private bool _debugLogging;

    // Per-device waveform ring buffer
    private const int WaveformSize = 64;
    private readonly float[] _waveformRing = new float[WaveformSize];
    private int _waveformWritePos;

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
        var safeMinimum = (_maxChunkMs * 1.5) + _outputLatencyMs;
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

            var chunkMs = BytesToMilliseconds(processed.Length);
            if (chunkMs > _maxChunkMs) _maxChunkMs = chunkMs;
            else _maxChunkMs = Math.Max(10.0, _maxChunkMs * 0.999); // Slow decay

            var target = TargetMs(baseBufferMs);
            var capacityMs = _buffer.BufferDuration.TotalMilliseconds;
            if (BufferedMilliseconds + chunkMs > capacityMs)
            {
                _totalOverflows++;
                var trimBytes = MillisecondsToBytes(BufferedMilliseconds + chunkMs - target);
                TrimBufferedAudio(trimBytes);
                EmitLog(LogCategory.Buffer, $"OVERFLOW #{_totalOverflows} — buf={BufferedMilliseconds}ms + {BytesToMilliseconds(count):F0}ms > {capacityMs:F0}ms", _deviceInfo.FriendlyName, Models.LogLevel.Warning);
            }

            _buffer.AddSamples(processed, 0, processed.Length);
            CaptureWaveform(processed);

            GuardReservoir(target);

            // Periodic stats to UI (every 50 enqueues = ~500ms)
            if (_totalEnqueues % 50 == 0)
            {
                EmitLog(LogCategory.Drift, $"#{_totalEnqueues} buf={BufferedMilliseconds}ms target={target:F0}ms offset={_bufferOffsetMs:F1}ms guardDrift={DriftMs:F1}ms trim={_totalTrimmedBytes}B pad={_totalSilenceBytes}B", _deviceInfo.FriendlyName);
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
            _deviceInfo.Id, _deviceInfo.FriendlyName, _deviceInfo.EstimatedLatencyMs, _deviceInfo.ManualDelayMs,
            EffectiveDelayMs, DriftMs, BufferedMilliseconds, _deviceInfo.Status,
            GetWaveformSnapshot(),
            _totalTrimmedBytes, _totalSilenceBytes, _totalOverflows, _bufferOffsetMs);
    }

    private void CaptureWaveform(byte[] processed)
    {
        if (_format.Encoding != NAudio.Wave.WaveFormatEncoding.IeeeFloat || _format.BitsPerSample != 32) return;
        var totalSamples = processed.Length / sizeof(float);
        var ch = _format.Channels;
        var step = Math.Max(1, totalSamples / (ch * 16));
        for (var i = 0; i < totalSamples; i += step * ch)
        {
            var s = BitConverter.ToSingle(processed, i * sizeof(float));
            _waveformRing[_waveformWritePos % WaveformSize] = s;
            _waveformWritePos++;
        }
    }

    private float[] GetWaveformSnapshot()
    {
        var snap = new float[WaveformSize];
        var pos = _waveformWritePos;
        for (var i = 0; i < WaveformSize; i++)
            snap[i] = _waveformRing[(pos + i) % WaveformSize];
        return snap;
    }

    /// <summary>
    /// Maximum ms to trim or pad in a single callback to avoid audible artifacts.
    /// At 48kHz stereo float, 5ms = ~1920 bytes — imperceptible.
    /// </summary>
    private const double MaxCorrectionPerCallbackMs = 5.0;

    /// <summary>
    /// Minimum dead-zone floor in ms. The actual dead-zone is dynamic
    /// based on _maxChunkMs to absorb natural WASAPI buffer oscillation.
    /// </summary>
    private const double MinDeadZoneMs = 15.0;

    /// <summary>
    /// Emergency threshold: if buffer deviation exceeds this, allow larger corrections.
    /// </summary>
    private const double EmergencyThresholdMs = 100.0;

    private void GuardReservoir(double targetMs)
    {
        var bufferedMs = _buffer.BufferedDuration.TotalMilliseconds;
        _bufferOffsetMs = Math.Round(bufferedMs - targetMs, 2);

        // Measurement bias correction: GuardReservoir is called right after Enqueue
        // adds a full chunk (~60ms at typical WASAPI callback intervals). The buffer
        // is therefore systematically elevated by ~chunkMs/2 above the true average.
        // Subtract this bias so the dead-zone is centered on the expected post-enqueue level.
        var biasMs = _maxChunkMs * 0.5;
        var deviationMs = bufferedMs - (targetMs + biasMs);

        // Dynamic dead-zone: only needs to absorb callback interval jitter (±10ms)
        // and output drain timing variation, not the systematic chunk bias (handled above).
        var deadZoneMs = Math.Max(MinDeadZoneMs, _maxChunkMs * 0.35);

        // Dead-zone: buffer is within normal oscillation range — no correction needed
        if (Math.Abs(deviationMs) <= deadZoneMs)
        {
            DriftMs = 0;
            if (_consecutiveDriftCorrections > 0)
                EmitLog(LogCategory.Drift, $"LOCKED - buffer stable after {_consecutiveDriftCorrections} corrections", _deviceInfo.FriendlyName);
            _consecutiveDriftCorrections = 0;
            return;
        }

        // Only correct the excess beyond the dead-zone edge
        if (deviationMs > 0)
        {
            var excessMs = deviationMs - deadZoneMs;
            var maxTrim = MaxCorrectionPerCallbackMs;
            if (excessMs > EmergencyThresholdMs)
            {
                maxTrim = Math.Min(15.0, excessMs * 0.20);
                if (_consecutiveDriftCorrections <= 3 || _consecutiveDriftCorrections % 50 == 0)
                    EmitLog(LogCategory.Drift, $"HIGH BUFFER TRIM {maxTrim:F1}ms buf={bufferedMs:F1}ms expected={targetMs + biasMs:F1}ms excess={excessMs:F1}ms", _deviceInfo.FriendlyName, Models.LogLevel.Warning);
            }

            var trimMs = Math.Min(maxTrim, excessMs * 0.15);
            trimMs = Math.Max(1.0, trimMs);
            var trimBytes = MillisecondsToBytes(trimMs);
            TrimBufferedAudio(trimBytes);

            DriftMs = Math.Round(excessMs, 2);
            _consecutiveDriftCorrections++;
        }
        else
        {
            var shortfallMs = -deviationMs - deadZoneMs;
            if (shortfallMs <= 0)
            {
                DriftMs = 0;
                return;
            }

            var maxPad = MaxCorrectionPerCallbackMs;
            if (shortfallMs > EmergencyThresholdMs)
            {
                maxPad = Math.Min(15.0, shortfallMs * 0.20);
                if (_consecutiveDriftCorrections <= 3 || _consecutiveDriftCorrections % 50 == 0)
                    EmitLog(LogCategory.Drift, $"LOW BUFFER PAD {maxPad:F1}ms buf={bufferedMs:F1}ms expected={targetMs + biasMs:F1}ms shortfall={shortfallMs:F1}ms", _deviceInfo.FriendlyName, Models.LogLevel.Warning);
            }

            var padMs = Math.Min(maxPad, shortfallMs * 0.15);
            padMs = Math.Max(1.0, padMs);
            var padBytes = MillisecondsToBytes(padMs);
            AddSilence(padBytes);
            _totalSilenceBytes += padBytes;

            DriftMs = Math.Round(-shortfallMs, 2);
            _consecutiveDriftCorrections++;
        }
    }

    private void TrimBufferedAudio(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        var alignedBytes = bytes - bytes % _format.BlockAlign;
        if (alignedBytes <= 0)
        {
            return;
        }

        var scratch = new byte[alignedBytes];
        var read = _buffer.Read(scratch, 0, scratch.Length);
        _totalTrimmedBytes += read;
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
