using System;
using Xunit;
using TNDrop.Services;

namespace TNDrop.Tests;

public class SoundSynthTests
{
    [Fact]
    public void Produces_valid_wav_header_and_length()
    {
        var wav = SoundSynth.SineSweepWav(1800, 900, 100);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        int expectedSamples = 44100 * 100 / 1000;
        Assert.Equal(44 + expectedSamples * 2, wav.Length);   // ヘッダ44B + 16bit mono
    }

    [Fact]
    public void Volume_zero_is_silent()
    {
        var wav = SoundSynth.SineSweepWav(440, 440, 20, volume: 0);
        for (int i = 44; i < wav.Length; i++)
            Assert.Equal(0, wav[i]);
    }

    [Fact]
    public void Short_duration_no_envelope_discontinuity()
    {
        // Regression: 8ms WAV should not have audible amplitude jumps from overlapping fade windows.
        // This tests the fix: fadeSamples is clamped to numSamples / 2 to prevent overlap.
        var wav = SoundSynth.SineSweepWav(440, 440, 8, volume: 0.3);

        // Extract 16-bit samples from byte array (starting after 44-byte header)
        int numSamples = 44100 * 8 / 1000;
        var samples = new short[numSamples];
        for (int i = 0; i < numSamples; i++)
        {
            int offset = 44 + i * 2;
            samples[i] = (short)(wav[offset] | (wav[offset + 1] << 8));
        }

        // Verify no adjacent-sample amplitude jump exceeds ~3000 (on 16-bit scale 0-32767).
        // This is roughly 10% of max amplitude, a sane discontinuity threshold.
        const int MaxDelta = 3000;
        for (int i = 0; i < samples.Length - 1; i++)
        {
            int delta = Math.Abs(samples[i + 1] - samples[i]);
            Assert.True(delta <= MaxDelta, $"Amplitude jump at sample {i}: {delta} > {MaxDelta}");
        }
    }
}
