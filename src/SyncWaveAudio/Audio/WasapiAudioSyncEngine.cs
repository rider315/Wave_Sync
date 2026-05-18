using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SyncWaveAudio.Models;
using SyncWaveAudio.Services;

namespace SyncWaveAudio.Audio;

public sealed class WasapiAudioSyncEngine(
    AppSettings settings,
    ILogger<WasapiAudioSyncEngine> logger) : IAudioSyncEngine, IDisposable
{
    private readonly object _gate = new();
    private readonly List<DeviceAudioSink> _sinks = [];
    private WasapiLoopbackCapture? _capture;
    private Timer? _snapshotTimer;
    private float _peakLeft;
    private float _peakRight;
    private bool _disposed;

    public event EventHandler<SyncSnapshot>? SnapshotReady;

    public bool IsRunning { get; private set; }

    public Task StartAsync(IReadOnlyList<AudioDeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        if (devices.Any(device => device.IsDefaultOutput))
        {
            throw new InvalidOperationException("The Windows default output is the source anchor and cannot be selected as a relay output.");
        }

        if (devices.Count == 0)
        {
            throw new InvalidOperationException("Select at least one additional device. Your Windows default output is used as the source anchor.");
        }

        StopInternal();

        using var enumerator = new MMDeviceEnumerator();
        var captureEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _capture = new WasapiLoopbackCapture(captureEndpoint);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                logger.LogError(args.Exception, "WASAPI loopback capture stopped unexpectedly.");
            }
        };

        var format = _capture.WaveFormat;
        var maxLatency = devices.Max(device => device.EstimatedLatencyMs);

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = enumerator.GetDevice(device.Id);
            var sink = new DeviceAudioSink(device, endpoint, format, settings.DefaultBufferMilliseconds);
            var effectiveDelay = maxLatency - device.EstimatedLatencyMs + device.ManualDelayMs;
            sink.Prime(effectiveDelay, settings.DefaultBufferMilliseconds);
            _sinks.Add(sink);
            device.Status = "Synced";
        }

        foreach (var sink in _sinks)
        {
            sink.Start();
        }

        _capture.StartRecording();
        _snapshotTimer = new Timer(_ => PublishSnapshot(), null, 0, settings.DriftCorrectionIntervalMilliseconds);
        IsRunning = true;
        logger.LogInformation("Started synchronized playback to {Count} devices.", devices.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopInternal();
        return Task.CompletedTask;
    }

    public async Task PlayCalibrationToneAsync(IReadOnlyList<AudioDeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        if (devices.Count == 0)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signal = new SignalGenerator(48000, 2)
            {
                Gain = 0.18,
                Frequency = 1000,
                Type = SignalGeneratorType.Sin
            };
            var endpoint = enumerator.GetDevice(device.Id);
            using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, false, 40);
            output.Init(signal.ToWaveProvider());
            output.Play();
            await Task.Delay(180, cancellationToken);
            output.Stop();
            await Task.Delay(80, cancellationToken);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        AnalyzePeak(args.Buffer, args.BytesRecorded, _capture?.WaveFormat);

        lock (_gate)
        {
            if (_sinks.Count == 0)
            {
                return;
            }

            var maxLatency = _sinks
                .Select(sink => sink.Snapshot())
                .Max(state => state.EstimatedLatencyMs);

            foreach (var sink in _sinks)
            {
                var snapshot = sink.Snapshot();
                var effectiveDelay = maxLatency - snapshot.EstimatedLatencyMs + snapshot.ManualDelayMs;
                sink.Enqueue(args.Buffer, args.BytesRecorded, effectiveDelay, settings.DefaultBufferMilliseconds);
            }
        }
    }

    private void AnalyzePeak(byte[] buffer, int bytesRecorded, WaveFormat? format)
    {
        if (format is null || format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
        {
            return;
        }

        var samples = bytesRecorded / sizeof(float);
        var left = 0f;
        var right = 0f;
        for (var i = 0; i < samples; i += format.Channels)
        {
            left = Math.Max(left, Math.Abs(BitConverter.ToSingle(buffer, i * sizeof(float))));
            if (format.Channels > 1 && i + 1 < samples)
            {
                right = Math.Max(right, Math.Abs(BitConverter.ToSingle(buffer, (i + 1) * sizeof(float))));
            }
        }

        _peakLeft = left;
        _peakRight = right;
    }

    private void PublishSnapshot()
    {
        var devices = _sinks.Select(sink => sink.Snapshot()).ToList();
        SnapshotReady?.Invoke(this, new SyncSnapshot(DateTimeOffset.Now, devices, _peakLeft, _peakRight, IsRunning));
    }

    private void StopInternal()
    {
        lock (_gate)
        {
            _snapshotTimer?.Dispose();
            _snapshotTimer = null;

            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                try
                {
                    _capture.StopRecording();
                }
                catch (InvalidOperationException)
                {
                    // Capture can already be stopped during device loss.
                }
                _capture.Dispose();
                _capture = null;
            }

            foreach (var sink in _sinks)
            {
                sink.Stop();
                sink.Dispose();
            }

            _sinks.Clear();
            IsRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal();
    }
}
