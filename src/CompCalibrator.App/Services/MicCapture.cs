using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CompCalibrator.App.Services;

public sealed record MicDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (既定)" : Name;
}

public sealed record Recording(float[] Samples, int SampleRate);

/// <summary>
/// WASAPI の共有モードで録音する。COM の都合で、デバイスの操作は MTA のスレッドで行う。
/// メーターの値はキャプチャのスレッドが書き、UI のタイマーが読む。
/// </summary>
public sealed class MicCapture : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private WaveFormat? _format;
    private List<float[]>? _channels;
    private volatile float _peak;
    private double _rmsSum;
    private int _rmsCount;

    public event Action<string>? Failed;

    public static Task<IReadOnlyList<MicDevice>> ListAsync() => Task.Run(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
        {
            using var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            defaultId = fallback.ID;
        }

        var list = new List<MicDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                list.Add(new MicDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }
        return (IReadOnlyList<MicDevice>)list;
    });

    public Task StartAsync(string deviceId) => Task.Run(() =>
    {
        StopCore();
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        var capture = new WasapiCapture(device, true, 20);
        _format = capture.WaveFormat;
        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
        capture.StartRecording();
        _capture = capture;
    });

    public void Stop() => StopCore();

    public void BeginRecording()
    {
        lock (_gate)
        {
            var channels = _format?.Channels ?? 1;
            _channels = [];
            _channelCount = channels;
        }
    }

    public void CancelRecording()
    {
        lock (_gate)
        {
            _channels = null;
        }
    }

    /// <summary>マイクが片側の入力だけに入る機器が多いので、いちばん大きいチャンネルを使う。</summary>
    public Recording? EndRecording()
    {
        List<float[]>? chunks;
        int channels;
        int rate;
        lock (_gate)
        {
            chunks = _channels;
            _channels = null;
            channels = _channelCount;
            rate = _format?.SampleRate ?? 0;
        }
        if (chunks is null || chunks.Count == 0 || rate == 0)
        {
            return null;
        }

        var total = chunks.Sum(c => c.Length);
        var interleaved = new float[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, interleaved, offset, chunk.Length);
            offset += chunk.Length;
        }

        var frames = total / channels;
        var energy = new double[channels];
        for (var i = 0; i < frames * channels; i++)
        {
            energy[i % channels] += interleaved[i] * interleaved[i];
        }
        var best = Array.IndexOf(energy, energy.Max());

        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            mono[i] = interleaved[i * channels + best];
        }
        return new Recording(mono, rate);
    }

    /// <summary>前回の呼び出しからの RMS とピークを返す。</summary>
    public (double RmsDb, double PeakDb) ReadLevel()
    {
        double sum;
        int count;
        float peak;
        lock (_gate)
        {
            sum = _rmsSum;
            count = _rmsCount;
            peak = _peak;
            _rmsSum = 0;
            _rmsCount = 0;
            _peak = 0;
        }
        var rms = count > 0 ? Math.Sqrt(sum / count) : 0;
        return (rms > 1e-6 ? 20 * Math.Log10(rms) : -120, peak > 1e-6 ? 20 * Math.Log10(peak) : -120);
    }

    private int _channelCount = 1;

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _format;
        if (format is null || e.BytesRecorded == 0)
        {
            return;
        }
        var samples = Convert(e.Buffer, e.BytesRecorded, format);

        lock (_gate)
        {
            var peak = _peak;
            foreach (var sample in samples)
            {
                var magnitude = Math.Abs(sample);
                peak = Math.Max(peak, magnitude);
                _rmsSum += sample * sample;
            }
            _peak = peak;
            _rmsCount += samples.Length;
            _channels?.Add(samples);
        }
    }

    private static float[] Convert(byte[] buffer, int bytes, WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible
                && extensible.SubFormat == NAudio.MediaFoundation.AudioSubtypes.MFAudioFormat_Float);

        if (format.BitsPerSample == 32 && isFloat)
        {
            var values = new float[bytes / 4];
            Buffer.BlockCopy(buffer, 0, values, 0, values.Length * 4);
            return values;
        }

        var size = format.BitsPerSample / 8;
        var count = bytes / size;
        var output = new float[count];
        for (var i = 0; i < count; i++)
        {
            var at = i * size;
            output[i] = format.BitsPerSample switch
            {
                16 => BitConverter.ToInt16(buffer, at) / 32768f,
                24 => ((buffer[at + 2] << 24) | (buffer[at + 1] << 16) | (buffer[at] << 8)) / 2147483648f,
                32 => BitConverter.ToInt32(buffer, at) / 2147483648f,
                _ => 0,
            };
        }
        return output;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Failed?.Invoke(e.Exception.Message);
        }
    }

    private void StopCore()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null)
        {
            return;
        }
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try
        {
            capture.StopRecording();
        }
        catch (InvalidOperationException)
        {
            // すでに止まっている。
        }
        capture.Dispose();
    }

    public void Dispose() => StopCore();
}
