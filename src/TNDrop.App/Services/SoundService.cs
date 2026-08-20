using System;
using System.IO;
using System.Media;

namespace TNDrop.Services;

/// <summary>
/// Plays the app's three short cue sounds without blocking the calling thread.
///
/// <c>SoundPlayer.PlaySync</c> (the original implementation) blocks the calling thread for the
/// clip's full duration (roughly 40-80ms here). <see cref="PlayCapture"/> is called straight off
/// the clipboard-capture path on the WPF UI thread, so a synchronous play would stall the app --
/// including the very indicator flash meant to happen at the same moment -- for that long on
/// every single copy. <c>SoundPlayer.Play()</c> queues playback on its own worker thread and
/// returns immediately instead. Its one requirement is that the backing stream must stay open
/// for as long as playback needs it, which is why each WAV is loaded once into a stream that
/// lives for the lifetime of this service, rather than the more RAM-frugal "new MemoryStream per
/// play call" pattern the old PlaySync version used (that pattern is unsafe with Play(): the
/// stream would already be disposed by the time the worker thread got to it).
/// </summary>
public sealed class SoundService
{
    private const string Module = "sound";

    private readonly Func<bool> _enabled;
    private readonly MemoryStream _captureStream;
    private readonly MemoryStream _deleteStream;
    private readonly MemoryStream _toggleStream;
    private readonly SoundPlayer _capturePlayer;
    private readonly SoundPlayer _deletePlayer;
    private readonly SoundPlayer _togglePlayer;

    public SoundService(Func<bool> enabled)
    {
        _enabled = enabled ?? (() => true);

        _captureStream = new MemoryStream(SoundSynth.SineSweepWav(1800, 900, 60));
        _deleteStream = new MemoryStream(SoundSynth.SineSweepWav(1400, 250, 80));
        _toggleStream = new MemoryStream(SoundSynth.SineSweepWav(900, 1200, 40));

        _capturePlayer = new SoundPlayer(_captureStream);
        _deletePlayer = new SoundPlayer(_deleteStream);
        _togglePlayer = new SoundPlayer(_toggleStream);

        // Parse each WAV once up front (off the hot path -- this runs during app startup, not
        // during a capture) so the first PlayXxx() call doesn't pay that cost inline.
        TryLoad(_capturePlayer, "capture");
        TryLoad(_deletePlayer, "delete");
        TryLoad(_togglePlayer, "toggle");
    }

    public void PlayCapture() => Play(_capturePlayer, "capture");

    public void PlayDelete() => Play(_deletePlayer, "delete");

    public void PlayToggle() => Play(_togglePlayer, "toggle");

    private void Play(SoundPlayer player, string soundName)
    {
        if (!_enabled())
        {
            return;
        }

        try
        {
            player.Play();
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"Failed to play {soundName} sound: {ex.Message}");
        }
    }

    private static void TryLoad(SoundPlayer player, string soundName)
    {
        try
        {
            player.Load();
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"Failed to load {soundName} sound: {ex.Message}");
        }
    }
}
