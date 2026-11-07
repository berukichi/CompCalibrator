using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CompCalibrator.App.Services;

/// <summary>既定の出力へ試聴の音を流す。出力の形式に合わせて線形補間で変換する。</summary>
public sealed class PreviewPlayer : IDisposable
{
    private WasapiOut? _output;

    public Task PlayAsync(float[] samples, int sampleRate) => Task.Run(() =>
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mix = device.AudioClient.MixFormat;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);

        var frames = (int)(samples.Length * (long)mix.SampleRate / sampleRate);
        var data = new float[frames * mix.Channels];
        var step = sampleRate / (double)mix.SampleRate;
        for (var frame = 0; frame < frames; frame++)
        {
            var position = frame * step;
            var index = (int)position;
            var next = Math.Min(index + 1, samples.Length - 1);
            var value = samples[index] + (samples[next] - samples[index]) * (float)(position - index);
            for (var channel = 0; channel < mix.Channels; channel++)
            {
                data[frame * mix.Channels + channel] = value;
            }
        }

        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        output.Init(new RawSourceWaveStream(new MemoryStream(bytes), format));
        output.Play();
        _output = output;
    });

    public void Stop()
    {
        var output = _output;
        _output = null;
        if (output is null)
        {
            return;
        }
        output.Stop();
        output.Dispose();
    }

    public void Dispose() => Stop();
}
