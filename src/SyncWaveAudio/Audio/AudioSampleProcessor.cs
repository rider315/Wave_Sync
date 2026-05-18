using NAudio.Wave;

namespace SyncWaveAudio.Audio;

public static class AudioSampleProcessor
{
    public static byte[] Apply(byte[] input, int count, WaveFormat format, float volume, bool mono)
    {
        var output = new byte[count];
        Buffer.BlockCopy(input, 0, output, 0, count);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            ProcessFloat(output, count, format.Channels, volume, mono);
            return output;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            ProcessInt16(output, count, format.Channels, volume, mono);
            return output;
        }

        return output;
    }

    private static void ProcessFloat(byte[] buffer, int count, int channels, float volume, bool mono)
    {
        var samples = count / sizeof(float);
        for (var i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToSingle(buffer, i * sizeof(float)) * volume;
            WriteFloat(buffer, i, Math.Clamp(sample, -1f, 1f));
        }

        if (!mono || channels < 2)
        {
            return;
        }

        for (var frame = 0; frame < samples; frame += channels)
        {
            var left = BitConverter.ToSingle(buffer, frame * sizeof(float));
            var right = BitConverter.ToSingle(buffer, (frame + 1) * sizeof(float));
            var mixed = (left + right) * 0.5f;
            WriteFloat(buffer, frame, mixed);
            WriteFloat(buffer, frame + 1, mixed);
        }
    }

    private static void ProcessInt16(byte[] buffer, int count, int channels, float volume, bool mono)
    {
        var samples = count / sizeof(short);
        for (var i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt16(buffer, i * sizeof(short));
            var scaled = (short)Math.Clamp(sample * volume, short.MinValue, short.MaxValue);
            WriteInt16(buffer, i, scaled);
        }

        if (!mono || channels < 2)
        {
            return;
        }

        for (var frame = 0; frame < samples; frame += channels)
        {
            var left = BitConverter.ToInt16(buffer, frame * sizeof(short));
            var right = BitConverter.ToInt16(buffer, (frame + 1) * sizeof(short));
            var mixed = (short)((left + right) / 2);
            WriteInt16(buffer, frame, mixed);
            WriteInt16(buffer, frame + 1, mixed);
        }
    }

    private static void WriteFloat(byte[] buffer, int sampleIndex, float sample)
    {
        var bytes = BitConverter.GetBytes(sample);
        Buffer.BlockCopy(bytes, 0, buffer, sampleIndex * sizeof(float), sizeof(float));
    }

    private static void WriteInt16(byte[] buffer, int sampleIndex, short sample)
    {
        var bytes = BitConverter.GetBytes(sample);
        Buffer.BlockCopy(bytes, 0, buffer, sampleIndex * sizeof(short), sizeof(short));
    }
}
