using System;
using System.Threading;

namespace TNDrop.Platform;

/// <summary>
/// Waits on a named, auto-reset Win32 event for an external "please exit" request (the
/// installer, closing a running instance before it overwrites files -- see the v1.2.1 installer
/// close design doc) and invokes a callback when it fires. Registered through the thread pool's
/// wait via <see cref="ThreadPool.RegisterWaitForSingleObject"/> -- no dedicated thread, no
/// polling.
/// <para>The callback runs on a THREAD POOL thread, never the WPF dispatcher thread. A callback
/// that needs to touch UI state (App.xaml.cs calls <c>Shutdown()</c>) must marshal itself onto
/// the dispatcher; this class makes no attempt to do that on the caller's behalf. That marshal
/// must be NON-BLOCKING (e.g. <c>Dispatcher.BeginInvoke</c>, not <c>Invoke</c>): <see
/// cref="Dispose"/> runs on the UI thread and takes the same <c>_gate</c> lock as the callback, so
/// a callback that blocks the thread pool thread waiting for a synchronous <c>Invoke</c> to
/// complete on the UI thread -- while the UI thread is itself blocked inside Dispose waiting for
/// this callback to finish -- deadlocks both threads.</para>
/// <para>Not thread-safe to construct/dispose concurrently from multiple threads, but <see
/// cref="Dispose"/> itself is safe to call more than once and is safe to race against an
/// in-flight callback: both take the same lock, so a callback that has already started when
/// Dispose is called is allowed to finish, but no callback that hasn't yet acquired the lock will
/// run once Dispose has set the disposed flag.</para>
/// <para>The <paramref name="eventName"/> constructor argument's initial-state semantics are
/// subtle: <see cref="EventWaitHandle"/>'s <c>initialState: false</c> argument below is IGNORED
/// whenever the constructor opens an already-existing named event rather than creating a new one
/// (Win32's <c>CreateEvent</c> silently no-ops the initial-state argument on an open-existing
/// race). The "freshly unsignaled" guarantee this class relies on therefore does not come from
/// that argument -- it comes from the named kernel object being destroyed when its last handle
/// closes (i.e. when the previous owner's process exits) and re-created from scratch, unsignaled,
/// the next time a constructor call is the first to open it.</para>
/// </summary>
public sealed class ShutdownSignal : IDisposable
{
    private readonly EventWaitHandle _handle;
    private readonly RegisteredWaitHandle _registeredWait;
    private readonly Action _onSignaled;
    private readonly object _gate = new();
    private bool _disposed;

    public ShutdownSignal(string eventName, Action onSignaled)
    {
        _onSignaled = onSignaled ?? throw new ArgumentNullException(nameof(onSignaled));

        // AutoReset: the event clears itself the moment the wait below picks it up, so a signal
        // that arrives while nobody is listening (there never should be more than one listener,
        // but a stray extra Set is harmless either way) can't re-fire on the next registration.
        _handle = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        // Infinite timeout + executeOnlyOnce: false -- this is a standing listener for the whole
        // process lifetime, not a one-shot wait. The thread pool re-arms the wait itself after
        // each callback; Dispose (via Unregister) is the only way this stops.
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _handle,
            OnWaitSignaled,
            state: null,
            timeout: Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    private void OnWaitSignaled(object? state, bool timedOut)
    {
        // Unregister (called from Dispose) only stops FUTURE waits; a callback already queued
        // for a wait that fired just before Unregister ran is not cancelled by it. This lock is
        // what actually enforces "no fire after Dispose": Dispose holds the same lock while it
        // sets _disposed, so either this callback observes _disposed == true and no-ops, or it
        // got here first and Dispose blocks until it's done before tearing the handle down.
        lock (_gate)
        {
            if (_disposed)
                return;

            _onSignaled();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _registeredWait.Unregister(null);
        _handle.Dispose();
    }
}
