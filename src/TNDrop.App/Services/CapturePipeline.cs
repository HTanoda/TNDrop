using System;
using System.Text;
using TNDrop.Core;
using TNDrop.Platform;

namespace TNDrop.Services;

/// <summary>
/// Converts a raw <see cref="CapturedClip"/> off the clipboard into one or more
/// <see cref="ClipItem"/>s, adds them to the store (dedup against the current head) and
/// persists the store. Owns the "capture -> item -> saved" pipeline end to end so callers
/// (ClipboardMonitor.Captured handlers) only need to know whether something new landed.
/// </summary>
public sealed class CapturePipeline
{
    private readonly ItemStore _store;
    private readonly ThumbnailService _thumbs;
    private readonly Func<AppSettings> _settings;

    /// <summary>Raised once per <see cref="Process"/> call that added at least one item.</summary>
    public event Action? ItemCaptured;

    public CapturePipeline(ItemStore store, ThumbnailService thumbs, Func<AppSettings> settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _thumbs = thumbs ?? throw new ArgumentNullException(nameof(thumbs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Converts <paramref name="clip"/> to ClipItem(s), adds them to the store and saves.
    /// Returns true when at least one item was actually added (i.e. not a dedup no-op).
    /// </summary>
    public bool Process(CapturedClip clip)
    {
        if (clip is null)
        {
            return false;
        }

        // Defense in depth: ClipboardMonitor.Paused already gates capture at the source when
        // incognito mode is on, but Process itself must never persist while it's active either
        // -- a caller invoking it directly (as tests do) must not accidentally leak an item.
        if (_settings().IncognitoMode)
        {
            return false;
        }

        var added = clip.Kind switch
        {
            ClipKind.Text or ClipKind.Link => ProcessText(clip),
            ClipKind.Files => ProcessFiles(clip),
            ClipKind.Image => ProcessImage(clip),
            _ => false,
        };

        if (added)
        {
            _store.Save();
            ItemCaptured?.Invoke();
        }

        return added;
    }

    private bool ProcessText(CapturedClip clip)
    {
        if (string.IsNullOrEmpty(clip.Text))
        {
            return false;
        }

        var item = new ClipItem
        {
            Kind = clip.Kind,
            Text = clip.Text,
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(Encoding.UTF8.GetBytes(clip.Text)),
        };

        return _store.TryAdd(item);
    }

    private bool ProcessFiles(CapturedClip clip)
    {
        if (clip.Files is null || clip.Files.Length == 0)
        {
            return false;
        }

        var items = ItemStore.BuildFileItems(clip.Files, DateTime.UtcNow);

        var addedAny = false;
        foreach (var item in items)
        {
            if (_store.TryAdd(item))
            {
                addedAny = true;
            }
        }

        return addedAny;
    }

    private bool ProcessImage(CapturedClip clip)
    {
        if (clip.Image is null)
        {
            return false;
        }

        // Encode the full-size PNG exactly once: these are the bytes both hashed for dedup AND
        // (when not a duplicate) written verbatim as the blob by ThumbnailService, so the saved
        // file and the hash can never disagree with each other.
        var pngBytes = ThumbnailService.EncodePng(clip.Image);
        var hash = ItemStore.Fnv1a(pngBytes);

        // Pre-check against the current head BEFORE touching disk: TryAdd would reject this same
        // hash anyway, and SaveImage's blob + thumbnail writes are not free. Without this check,
        // copying the same image twice in a row would permanently orphan two PNG files on every
        // repeat -- nothing scans blobs/ for files no ClipItem references.
        if (_store.HeadContentHash == hash)
        {
            return false;
        }

        var (imageFile, thumbFile) = _thumbs.SaveImage(pngBytes, clip.Image);

        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            ImageFile = imageFile,
            ThumbFile = thumbFile,
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = hash,
        };

        return _store.TryAdd(item);
    }
}
