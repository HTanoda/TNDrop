using System;
using System.Collections.Generic;
using TNDrop.Core;

/// <summary>
/// v1.3 Task B review fix: BlobNaming.FriendlyImageFileName is the ONE resolution for what a
/// converted image blob's file name is. Pure -- no WPF, no disk I/O (the caller decides what
/// "taken" means via the isTaken callback) -- so these run as plain Facts.
/// </summary>
public class BlobNamingTests
{
    [Fact]
    public void FriendlyImageFileName_uses_the_base_word_and_a_local_timestamp()
    {
        var utc = new DateTime(2026, 8, 22, 6, 4, 33, DateTimeKind.Utc);
        var expectedLocal = utc.ToLocalTime();

        var name = BlobNaming.FriendlyImageFileName(utc, _ => false);

        Assert.Equal($"スクリーンショット {expectedLocal:yyyy-MM-dd HH.mm.ss}.png", name);
        Assert.DoesNotContain(':', name); // ':' is illegal in a Windows file name
    }

    [Fact]
    public void FriendlyImageFileName_treats_a_local_CreatedAtUtc_as_already_local()
    {
        // Defensive: every real caller stores UTC, but the function must not double-convert if
        // ever handed a non-UTC DateTime.
        var local = new DateTime(2026, 8, 22, 15, 4, 33, DateTimeKind.Local);

        var name = BlobNaming.FriendlyImageFileName(local, _ => false);

        Assert.Equal($"スクリーンショット {local:yyyy-MM-dd HH.mm.ss}.png", name);
    }

    [Fact]
    public void FriendlyImageFileName_appends_a_suffix_on_a_single_collision()
    {
        var utc = new DateTime(2026, 8, 22, 6, 4, 33, DateTimeKind.Utc);
        var baseNamePng = BlobNaming.FriendlyImageFileName(utc, _ => false);

        var name = BlobNaming.FriendlyImageFileName(utc, candidate => candidate == baseNamePng);

        var expectedBase = baseNamePng[..^".png".Length];
        Assert.Equal($"{expectedBase} (2).png", name);
    }

    [Fact]
    public void FriendlyImageFileName_increments_the_suffix_until_a_free_name_is_found()
    {
        var utc = new DateTime(2026, 8, 22, 6, 4, 33, DateTimeKind.Utc);
        var baseNamePng = BlobNaming.FriendlyImageFileName(utc, _ => false);
        var expectedBase = baseNamePng[..^".png".Length];

        var taken = new HashSet<string>
        {
            baseNamePng,
            $"{expectedBase} (2).png",
            $"{expectedBase} (3).png",
        };

        var name = BlobNaming.FriendlyImageFileName(utc, candidate => taken.Contains(candidate));

        Assert.Equal($"{expectedBase} (4).png", name);
    }

    [Fact]
    public void FriendlyImageFileName_throws_for_a_null_isTaken_callback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BlobNaming.FriendlyImageFileName(DateTime.UtcNow, null!));
    }
}
