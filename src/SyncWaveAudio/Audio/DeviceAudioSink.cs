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
    private readonly object _gate = new();
    private readonly byte[] _silenceChunk;
    private readonly AudioEndpointVolume _endpointVolume;
    private double _lastEndpointVolume = -1;
    private double _smoothedTargetDelayMs;
    private bool _disposed;

    public DeviceAudioSink(AudioDeviceInfo deviceInfo, MMDevice endpoint, WaveFormat format, int baseBufferMs)
    {
        _deviceInfo = deviceInfo;
        _format = format;
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(Math.Max(750, baseBufferMs * 4)),
            DiscardOnBufferOverflow = true
        };

        _silenceChunk = new byte[Math.Max(format.AverageBytesPerSecond / 100, format.BlockAlign)];
        _endpointVolume = endpoint.AudioEndpointVolume;
        _output = new WasapiOut(endpoint, AudioClientShareMode.Shared, false, Math.Max(20, baseBufferMs / 2));
        _output.Init(_buffer);
    }

    public string DeviceId => _deviceInfo.Id;
    public int BufferedMilliseconds => (int)_buffer.BufferedDuration.TotalMilliseconds;
    public double EffectiveDelayMs { get; private set; }
    public double DriftMs { get; private set; }

    public void Prime(double effectiveDelayMs, int baseBufferMs)
    {
        EffectiveDelayMs = Math.Max(0, effectiveDelayMs);
        _smoothedTargetDelayMs = EffectiveDelayMs;
        var totalPrimeMs = baseBufferMs + EffectiveDelayMs;
        AddSilence(MillisecondsToBytes(totalPrimeMs));
    }

    public void Start() => _output.Play();

    public void Stop()
    {
        _output.Stop();
        _buffer.ClearBuffer();
    }

    public void Enqueue(byte[] source, int count, double effectiveDelayMs, int baseBufferMs)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            EffectiveDelayMs = SmoothDelay(effectiveDelayMs);
            ApplyEndpointVolume();
            var sampleGain = (float)Math.Max(1.0, _deviceInfo.Volume);
            var processed = AudioSampleProcessor.Apply(source, count, _format, sampleGain, _deviceInfo.Mono);
            _buffer.AddSamples(processed, 0, processed.Length);
            CorrectDrift(baseBufferMs + EffectiveDelayMs);
        }
    }

    private double SmoothDelay(double requestedDelayMs)
    {
        requestedDelayMs = Math.Max(0, requestedDelayMs);
        var delta = requestedDelayMs - _smoothedTargetDelayMs;
        var step = Math.Clamp(delta, -2.5, 2.5);
        _smoothedTargetDelayMs += step;
        return Math.Round(_smoothedTargetDelayMs, 2);
    }

    public DeviceSyncState Snapshot()
    {
        return new DeviceSyncState(
            _deviceInfo.Id,
            _deviceInfo.EstimatedLatencyMs,
            _deviceInfo.ManualDelayMs,
            EffectiveDelayMs,
            DriftMs,
            BufferedMilliseconds,
            _deviceInfo.Status);
    }

    private void CorrectDrift(double targetMs)
    {
        var bufferedMs = _buffer.BufferedDuration.TotalMilliseconds;
        DriftMs = Math.Round(bufferedMs - targetMs, 2);

        if (DriftMs > 24)
        {
            var trimBytes = MillisecondsToBytes(Math.Min(8, DriftMs / 2));
            if (trimBytes > 0)
            {
                var scratch = new byte[trimBytes];
                _buffer.Read(scratch, 0, scratch.Length);
            }
        }
        else if (DriftMs < -18)
        {
            AddSilence(MillisecondsToBytes(Math.Min(6, Math.Abs(DriftMs) / 2)));
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
        var endpointVolume = Math.Clamp(_deviceInfo.Volume, 0.0, 1.0);
        if (Math.Abs(endpointVolume - _lastEndpointVolume) < 0.01)
        {
            return;
        }

        _endpointVolume.MasterVolumeLevelScalar = (float)endpointVolume;
        _lastEndpointVolume = endpointVolume;
    }

    private int MillisecondsToBytes(double milliseconds)
    {
        var bytes = (int)(_format.AverageBytesPerSecond * milliseconds / 1000.0);
        return bytes - bytes % _format.BlockAlign;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output.Dispose();
    }
}
