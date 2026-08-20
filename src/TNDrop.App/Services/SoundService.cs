using System;
using System.IO;
using System.Media;

namespace TNDrop.Services;

public sealed class SoundService
{
    private readonly Func<bool> _enabled;
    private readonly byte[] _captureWav;
    private readonly byte[] _deleteWav;
    private readonly byte[] _toggleWav;

    public SoundService(Func<bool> enabled)
    {
        _enabled = enabled ?? (() => true);

        // Pre-generate and cache the three WAV files
        _captureWav = SoundSynth.SineSweepWav(1800, 900, 60);
        _deleteWav = SoundSynth.SineSweepWav(1400, 250, 80);
        _toggleWav = SoundSynth.SineSweepWav(900, 1200, 40);
    }

    public void PlayCapture()
    {
        Play(_captureWav, "capture");
    }

    public void PlayDelete()
    {
        Play(_deleteWav, "delete");
    }

    public void PlayToggle()
    {
        Play(_toggleWav, "toggle");
    }

    private void Play(byte[] wavData, string soundName)
    {
        if (!_enabled())
        {
            return;
        }

        try
        {
            using (var ms = new MemoryStream(wavData))
            {
                using (var player = new SoundPlayer(ms))
                {
                    player.PlaySync();
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("sound", $"Failed to play {soundName} sound: {ex.Message}");
        }
    }
}
