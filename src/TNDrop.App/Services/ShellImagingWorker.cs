using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TNDrop.Services;

/// <summary>
/// One dedicated, long-lived background STA thread that runs off-UI-thread work for
/// <see cref="Platform.ShellImaging"/> (v1.3 Task C review fix).
///
/// <para><b>Why not <c>Task.Run</c>.</b> <see cref="Platform.ShellImaging"/>'s own class remarks
/// document that it is "Intended to be called from the WPF UI thread (an STA)... the COM calls
/// themselves are left to the shell's own apartment rules; nothing here promises good behaviour
/// from an MTA thread." <c>Task.Run</c>/the thread pool defaults to MTA, which is exactly the
/// apartment ShellImaging's own documentation declines to promise correct behavior under. A single
/// dedicated <see cref="ApartmentState.STA"/> thread sidesteps that question entirely rather than
/// gambling on undocumented MTA behavior from a shell COM interface
/// (<c>IShellItemImageFactory</c>).</para>
///
/// <para><b>Cache reuse.</b> <see cref="Platform.ShellImaging"/>'s own icon/thumbnail LRU caches are
/// lock-guarded and keyed by path (+ size/last-write-time for thumbnails) -- which thread calls
/// into them does not matter. A cache hit on this worker thread is exactly as fast as a cache hit
/// would have been on the UI thread, so re-opening a flyout for files already thumbnailed once
/// stays effectively instant; only a genuinely cold path pays the real shell round-trip, now off
/// the UI thread.</para>
///
/// <para>A single FIFO queue, not a thread per request: this is used for a handful of row
/// thumbnails per flyout open, not a high-throughput pipeline, so one worker thread processing
/// requests in submission order is enough -- and keeps the one COM apartment this class owns from
/// ever being touched by more than one thread at a time.</para>
/// </summary>
public static class ShellImagingWorker
{
    private const string Module = "ShellImagingWorker";

    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly object StartLock = new();
    private static Thread? _thread;

    /// <summary>
    /// Queues <paramref name="work"/> to run on the dedicated STA worker thread. Fire-and-forget
    /// from the caller's point of view: <paramref name="work"/> is responsible for marshaling its
    /// own result back to whatever thread needs it (typically the UI thread, via
    /// <c>Dispatcher.BeginInvoke</c>) -- this class has no notion of results, only of running
    /// delegates one at a time, in order, off the caller's thread.
    /// </summary>
    public static void Enqueue(Action work)
    {
        if (work is null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        EnsureStarted();
        Queue.Add(work);
    }

    private static void EnsureStarted()
    {
        if (_thread is not null)
        {
            return;
        }

        lock (StartLock)
        {
            if (_thread is not null)
            {
                return;
            }

            var thread = new Thread(RunLoop)
            {
                IsBackground = true, // must never keep the process alive on its own
                Name = "TNDrop.ShellImagingWorker",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _thread = thread;
        }
    }

    private static void RunLoop()
    {
        // GetConsumingEnumerable blocks until an item is available; this process never signals
        // CompleteAdding, so the loop -- and the thread -- simply lives for the process lifetime,
        // same as every other always-on background thread in this app (ClipboardMonitor's,
        // AutoDeleteService's timers, etc.).
        foreach (var work in Queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // A single failed row's thumbnail must never take the worker thread down --
                // every subsequent request (this row's, or any other row's, in this or a future
                // flyout open) would silently stop resolving forever.
                FileLogger.Instance?.Warn(Module, $"background shell-imaging work failed: {ex.GetType().Name}");
            }
        }
    }
}
