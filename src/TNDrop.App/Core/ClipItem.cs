using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TNDrop.Core;

public enum ClipKind { Text, Link, Image, Files }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool Pinned { get; set; }
    public string? Text { get; set; }             // Text/Link の内容
    public List<string> Paths { get; set; } = new(); // Files: フルパス列 (1 個 = 単独ファイルカード)
    public string? ImageFile { get; set; }        // Image: blobs 内ファイル名 (フルパスでない)
    public string? ThumbFile { get; set; }        // Image: サムネイルファイル名
    public ulong ContentHash { get; set; }        // 重複判定用 FNV-1a 64bit

    /// <summary>SINGLE RESOLUTION for "is this item a multi-file stack" -- Kind==Files with 2+
    /// paths. <see cref="TNDrop.UI.CardViewModel.IsStack"/> and
    /// <c>ShelfViewModel.Contribution</c> both read this instead of each re-deriving the same
    /// Kind/Paths.Count check, so the UI's stack affordances and the badge counts can never
    /// disagree about which cards are stacks. Always reads live from <see cref="Paths"/> --
    /// never cached -- so it stays correct across in-place mutation (TryMergeFiles/SplitFile
    /// both mutate Paths on the existing ClipItem instance rather than replacing it).</summary>
    /// <remarks>[JsonIgnore] (v1.3 review fix M2): a computed property, not persisted state --
    /// ItemStore's JsonSerializerOptions has no property-selection policy of its own, so without
    /// this it would round-trip through items.dat as a spurious "IsStack" field. Read-only so
    /// deserialization already ignores any such field on load; this only stops it from being
    /// written in the first place.</remarks>
    [JsonIgnore]
    public bool IsStack => Kind == ClipKind.Files && Paths.Count > 1;
}
