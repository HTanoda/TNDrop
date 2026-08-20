using System;
using System.Windows.Threading;
using TNDrop.Core;

namespace TNDrop.Services;

/// <summary>
/// Periodically purges unpinned clipboard history older than the configured
/// <see cref="AutoDeletePolicy"/>. Reads the policy through a delegate rather than a snapshot
/// value so a settings change (tray/settings UI) takes effect on the very next tick without
/// this service needing to be told about it.
/// </summary>
public sealed class AutoDeleteService : IDisposable
{
    private const string Module = "AutoDeleteService";
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(10);

    private readonly ItemStore _store;
    private readonly Func<AutoDeletePolicy> _policy;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public AutoDeleteService(ItemStore store, Func<AutoDeletePolicy> policy)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
    }

    /// <summary>Starts the 10-minute purge cycle. Idempotent.</summary>
    public void Start() => _timer.Start();

    /// <summary>
    /// Off -> null (never purge). Otherwise the age threshold below which unpinned items are
    /// kept.
    /// </summary>
    public static TimeSpan? ToAge(AutoDeletePolicy p) => p switch
    {
        AutoDeletePolicy.Off => null,
        AutoDeletePolicy.Hours1 => TimeSpan.FromHours(1),
        AutoDeletePolicy.Hours6 => TimeSpan.FromHours(6),
        AutoDeletePolicy.Hours24 => TimeSpan.FromHours(24),
        AutoDeletePolicy.Days7 => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>
    /// Runs one purge pass immediately: PurgeOlderThan + Save when anything was removed. Public
    /// for tests and for the one-shot startup purge (stale items from a previous session).
    /// Returns the number of items removed; 0 (no Save) when the policy is Off or nothing
    /// qualified.
    /// </summary>
    public int RunOnce()
    {
        var age = ToAge(_policy());
        if (age is null)
        {
            return 0;
        }

        var removed = _store.PurgeOlderThan(age.Value);
        if (removed > 0)
        {
            _store.Save();
            FileLogger.Instance?.Info(Module, $"auto-delete purged {removed} item(s)");
        }

        return removed;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            RunOnce();
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "auto-delete tick failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
