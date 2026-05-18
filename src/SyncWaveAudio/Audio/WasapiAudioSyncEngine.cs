using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SyncWaveAudio.Models;
using SyncWaveAudio.Services;

namespace SyncWaveAudio.Audio;

public sealed class WasapiAudioSyncEngine(
    AppSettings settings,
    ILoggerFactory loggerFactory,
    ILogger<WasapiAudioSyncEngine> logger) : IAudioSyncEngine, IDisposable
{
    private readonly object _gate = new();
    private readonly List<DeviceAudioSink> _sinks = [];
    private WasapiLoopbackCapture? _capture;
    private Timer? _snapshotTimer;
    private float _peakLeft;
    private float _peakRight;
    private bool _disposed;

    // Capture pipeline metrics
    private readonly Stopwatch _captureStopwatch = new();
    private long _totalCaptureCallbacks;
    private long _totalBytesCaptured;
    private double _lastCallbackIntervalMs;
    private long _lastCallbackTicks;

    public event EventHandler<SyncSnapshot>? SnapshotReady;
    public event EventHandler<SyncLogEntry>? LogEmitted;

    public bool IsRunning { get; private set; }
    private double _anchorLatencyMs;
    private double _captureDevicePeriodMs;

    public Task StartAsync(IReadOnlyList<AudioDeviceInfo> devices, AudioDeviceInfo anchorDevice, CancellationToken cancellationToken = default)
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
        _anchorLatencyMs = anchorDevice.EstimatedLatencyMs;
        _totalCaptureCallbacks = 0;
        _totalBytesCaptured = 0;
        _lastCallbackTicks = 0;

        using var enumerator = new MMDeviceEnumerator();
        var captureEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // FIX Bug 5: Measure capture device period for pipeline latency compensation
        try
        {
            _captureDevicePeriodMs = captureEndpoint.AudioClient.DefaultDevicePeriod / 10_000.0;
        }
        catch
        {
            _captureDevicePeriodMs = 10.0;
        }

        _capture = new WasapiLoopbackCapture(captureEndpoint);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                logger.LogError(args.Exception, "WASAPI loopback capture stopped unexpectedly.");
                EmitLog(LogCategory.Capture, "Capture stopped unexpectedly: " + args.Exception.Message, Models.LogLevel.Error);
            }
        };

        var format = _capture.WaveFormat;
        logger.LogInformation("=== SYNC SESSION START ===");
        logger.LogInformation("Anchor device: {Anchor} (latency={LatencyMs:F1}ms)", anchorDevice.FriendlyName, _anchorLatencyMs);
        logger.LogInformation("Capture device period: {PeriodMs:F1}ms", _captureDevicePeriodMs);
        logger.LogInformation("Capture format: {Rate}Hz, {Ch}ch, {Encoding}, {Bits}bit", format.SampleRate, format.Channels, format.Encoding, format.BitsPerSample);
        logger.LogInformation("Buffer settings: default={DefaultMs}ms, min={MinMs}ms, driftInterval={DriftMs}ms",
            settings.DefaultBufferMilliseconds, settings.MinimumBufferMilliseconds, settings.DriftCorrectionIntervalMilliseconds);

        EmitLog(LogCategory.System, $"Session started — anchor={anchorDevice.FriendlyName}, capturePeriod={_captureDevicePeriodMs:F1}ms, buffer={settings.DefaultBufferMilliseconds}ms");

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = enumerator.GetDevice(device.Id);
            var sinkLogger = loggerFactory.CreateLogger($"SyncWaveAudio.Sink.{device.FriendlyName}");
            var sink = new DeviceAudioSink(device, endpoint, format, settings.DefaultBufferMilliseconds, sinkLogger, settings.EnableDebugLogging);
            sink.OnLog = entry => LogEmitted?.Invoke(this, entry);

            var effectiveDelay = CalculateAnchorDelay(device);
            sink.Prime(effectiveDelay, settings.DefaultBufferMilliseconds);
            _sinks.Add(sink);
            device.Status = effectiveDelay > 0 ? "Anchor aligned" : "Monitoring";

            logger.LogInformation("Relay device: {Device} — estimatedLatency={LatencyMs:F1}ms, effectiveDelay={DelayMs:F1}ms, manualDelay={ManualMs:F1}ms",
                device.FriendlyName, device.EstimatedLatencyMs, effectiveDelay, device.ManualDelayMs);
            EmitLog(LogCategory.Device, $"Relay: {device.FriendlyName} — latency={device.EstimatedLatencyMs:F1}ms, delay={effectiveDelay:F1}ms", deviceName: device.FriendlyName);
        }

        foreach (var sink in _sinks)
        {
            sink.Start();
        }

        _captureStopwatch.Restart();
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
        // Track capture callback timing
        var nowTicks = _captureStopwatch.ElapsedTicks;
        if (_lastCallbackTicks > 0)
        {
            _lastCallbackIntervalMs = (nowTicks - _lastCallbackTicks) * 1000.0 / Stopwatch.Frequency;
        }
        _lastCallbackTicks = nowTicks;
        _totalCaptureCallbacks++;
        _totalBytesCaptured += args.BytesRecorded;

        AnalyzePeak(args.Buffer, args.BytesRecorded, _capture?.WaveFormat);

        lock (_gate)
        {
            if (_sinks.Count == 0)
            {
                return;
            }

            // Log capture callback details periodically
            if (settings.EnableDebugLogging && _totalCaptureCallbacks % 200 == 0)
            {
                logger.LogDebug("[Capture] Callback #{N} — bytes={Bytes}, interval={IntervalMs:F1}ms, totalCaptured={TotalMB:F2}MB",
                    _totalCaptureCallbacks, args.BytesRecorded, _lastCallbackIntervalMs,
                    _totalBytesCaptured / (1024.0 * 1024.0));
            }

            foreach (var sink in _sinks)
            {
                var snapshot = sink.Snapshot();
                var effectiveDelay = CalculateAnchorDelay(snapshot.EstimatedLatencyMs, snapshot.ManualDelayMs);
                sink.Enqueue(args.Buffer, args.BytesRecorded, effectiveDelay, settings.DefaultBufferMilliseconds);
            }
        }
    }

    private double CalculateAnchorDelay(AudioDeviceInfo device)
    {
        return CalculateAnchorDelay(device.EstimatedLatencyMs, device.ManualDelayMs);
    }

    private double CalculateAnchorDelay(double relayLatencyMs, double manualDelayMs)
    {
        // FIX Bug 5: Subtract capture pipeline latency from anchor delay calculation
        // The capture process itself adds _captureDevicePeriodMs of latency
        var adjustedAnchorLatency = Math.Max(0, _anchorLatencyMs - _captureDevicePeriodMs);
        return Math.Max(0, adjustedAnchorLatency - relayLatencyMs + manualDelayMs);
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
        var deviceStates = _sinks.Select(sink => sink.Snapshot()).ToList();

        // Compute aggregate sync health
        double maxDrift = 0;
        double sumDrift = 0;
        foreach (var state in deviceStates)
        {
            var absDrift = Math.Abs(state.DriftMs);
            maxDrift = Math.Max(maxDrift, absDrift);
            sumDrift += absDrift;
        }
        var avgDrift = deviceStates.Count > 0 ? sumDrift / deviceStates.Count : 0;

        // Health: exponential decay — 100% at 0ms, ~82% at 5ms, ~61% at 10ms, ~22% at 30ms
        // Grace period: first 30 callbacks (initial stabilization) always show 95%+
        int healthPercent;
        if (deviceStates.Count == 0)
        {
            healthPercent = 100;
        }
        else if (_totalCaptureCallbacks < 30)
        {
            // Grace period — buffer is stabilizing, don't penalize
            healthPercent = (int)Math.Clamp(95 - (maxDrift * 0.3), 70, 100);
        }
        else
        {
            // Exponential: health = 100 * e^(-drift/20)
            healthPercent = (int)Math.Clamp(100 * Math.Exp(-maxDrift / 20.0), 0, 100);
        }

        // Update debug logging flag on sinks
        foreach (var sink in _sinks)
        {
            sink.SetDebugLogging(settings.EnableDebugLogging);
        }

        var snapshot = new SyncSnapshot(
            DateTimeOffset.Now,
            deviceStates,
            _peakLeft,
            _peakRight,
            IsRunning,
            Math.Round(maxDrift, 1),
            Math.Round(avgDrift, 1),
            healthPercent,
            Math.Round(_lastCallbackIntervalMs, 1),
            _totalCaptureCallbacks,
            _totalBytesCaptured);

        SnapshotReady?.Invoke(this, snapshot);
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
            _captureStopwatch.Stop();

            logger.LogInformation("=== SYNC SESSION STOPPED === totalCallbacks={Callbacks}, totalCaptured={MB:F2}MB",
                _totalCaptureCallbacks, _totalBytesCaptured / (1024.0 * 1024.0));
            EmitLog(LogCategory.System, $"Session stopped — {_totalCaptureCallbacks} callbacks, {_totalBytesCaptured / (1024.0 * 1024.0):F2}MB captured");
        }
    }

    private void EmitLog(LogCategory category, string message, Models.LogLevel level = Models.LogLevel.Info, string deviceName = "")
    {
        LogEmitted?.Invoke(this, new SyncLogEntry
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal();
    }
}
