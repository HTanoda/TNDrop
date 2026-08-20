using System;
using System.Collections.Generic;

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
}
