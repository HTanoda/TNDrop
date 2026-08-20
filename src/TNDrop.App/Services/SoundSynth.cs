using System;
using System.IO;

namespace TNDrop.Services;

public static class SoundSynth
{
    private const int SampleRate = 44100;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int Channels = 1;

    /// <summary>
    /// Generates a complete WAV file with a sine wave that sweeps from fromHz to toHz.
    /// Format: PCM, mono, 44.1kHz, 16-bit.
    /// Includes 5ms fade-in/out to prevent clicking.
    /// </summary>
    public static byte[] SineSweepWav(double fromHz, double toHz, int durationMs, double volume = 0.3)
    {
        int numSamples = SampleRate * durationMs / 1000;

        // Create WAV header (44 bytes)
        byte[] header = CreateWavHeader(numSamples);

        // Generate audio samples
        byte[] samples = GenerateSamples(fromHz, toHz, durationMs, numSamples, volume);

        // Combine header and samples
        byte[] wav = new byte[header.Length + samples.Length];
        Buffer.BlockCopy(header, 0, wav, 0, header.Length);
        Buffer.BlockCopy(samples, 0, wav, header.Length, samples.Length);

        return wav;
    }

    private static byte[] CreateWavHeader(int numSamples)
    {
        int dataSize = numSamples * BytesPerSample;
        int fileSize = 36 + dataSize;

        using (var ms = new MemoryStream(44))
        {
            using (var writer = new BinaryWriter(ms))
            {
                // RIFF header
                writer.Write(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(fileSize);
                writer.Write(new byte[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

                // fmt subchunk
                writer.Write(new byte[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(16); // Subchunk1Size (16 for PCM)
                writer.Write((short)1); // AudioFormat (1 for PCM)
                writer.Write((short)Channels); // NumChannels
                writer.Write(SampleRate); // SampleRate
                writer.Write(SampleRate * Channels * BytesPerSample); // ByteRate
                writer.Write((short)(Channels * BytesPerSample)); // BlockAlign
                writer.Write((short)BitsPerSample); // BitsPerSample

                // data subchunk
                writer.Write(new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                writer.Write(dataSize);
            }

            return ms.ToArray();
        }
    }

    private static byte[] GenerateSamples(double fromHz, double toHz, int durationMs, int numSamples, double volume)
    {
        byte[] samples = new byte[numSamples * BytesPerSample];

        if (volume == 0)
        {
            // Silent: all zeros
            return samples;
        }

        // Calculate fade lengths in samples (5ms = 220.5 samples at 44.1kHz)
        int fadeSamples = (int)(SampleRate * 5 / 1000);

        double phase = 0;

        for (int i = 0; i < numSamples; i++)
        {
            // Linear interpolation of frequency
            double t = (double)i / numSamples;
            double freq = fromHz + (toHz - fromHz) * t;

            // Generate sample
            double sampleValue = Math.Sin(phase);

            // Apply fading
            double fadeFactor = 1.0;
            if (i < fadeSamples)
            {
                // Fade in
                fadeFactor = (double)i / fadeSamples;
            }
            else if (i >= numSamples - fadeSamples)
            {
                // Fade out
                fadeFactor = (double)(numSamples - i) / fadeSamples;
            }

            // Apply volume and fade
            sampleValue *= volume * fadeFactor;

            // Clamp to [-1, 1]
            sampleValue = Math.Clamp(sampleValue, -1.0, 1.0);

            // Convert to 16-bit signed integer
            short sampleInt = (short)(sampleValue * short.MaxValue);

            // Write as little-endian 16-bit integer
            int offset = i * BytesPerSample;
            samples[offset] = (byte)(sampleInt & 0xFF);
            samples[offset + 1] = (byte)((sampleInt >> 8) & 0xFF);

            // Update phase for next sample
            phase += 2 * Math.PI * freq / SampleRate;
        }

        return samples;
    }
}
