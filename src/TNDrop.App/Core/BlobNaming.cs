using System;
using TNDrop.Resources;

namespace TNDrop.Core;

/// <summary>
/// v1.3 Task B review fix: the ONE resolution for what human-meaningful file name a
/// converted/materialized image blob gets. Before this, a converted Image card kept its
/// capture-time GUID name (<c>ThumbnailService.SaveImage</c>'s <c>{Guid:N}.png</c>) forever --
/// visible as the card's Title, its Subtitle/tooltip (the full AppData path), and every flyout
/// row once merged into a stack. <see cref="ItemStore.ConvertImageToFileCard"/> is the only
/// caller: it renames the blob file on disk to this name at conversion time, so the file on disk,
/// the card's <c>Paths</c> entry, drag-out, and every display agree -- nothing else in the app
/// computes this name a second way.
/// </summary>
public static class BlobNaming
{
    /// <summary>
    /// "&lt;localized base word&gt; yyyy-MM-dd HH.mm.ss.png" built from
    /// <paramref name="createdAtUtc"/> converted to local time -- the user reads this name, not a
    /// machine, so it should match their clock. Colons are avoided (<c>HH.mm.ss</c>, not
    /// <c>HH:mm:ss</c>): ':' is illegal in a Windows file name.
    /// <para>Collisions are resolved by asking <paramref name="isTaken"/> -- the caller decides
    /// what "taken" means (an existing file on disk, for <see cref="ItemStore"/>'s one caller) --
    /// and appending " (2)", " (3)", ... until a free name is found. Two cards created in the same
    /// second, or two sides of the same merge converted moments apart, both resolve through this
    /// same loop, so they can never collide on disk.</para>
    /// </summary>
    public static string FriendlyImageFileName(DateTime createdAtUtc, Func<string, bool> isTaken)
    {
        if (isTaken is null)
        {
            throw new ArgumentNullException(nameof(isTaken));
        }

        var local = createdAtUtc.Kind == DateTimeKind.Utc ? createdAtUtc.ToLocalTime() : createdAtUtc;
        var baseName = $"{Strings.ScreenshotFileBaseName} {local:yyyy-MM-dd HH.mm.ss}";

        var candidate = baseName + ".png";
        var suffix = 2;

        while (isTaken(candidate))
        {
            candidate = $"{baseName} ({suffix}).png";
            suffix++;
        }

        return candidate;
    }
}
