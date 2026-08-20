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
}
