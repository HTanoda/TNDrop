using System;
using System.Threading;
using Xunit;
using TNDrop.Platform;

namespace TNDrop.Tests;

public class ShutdownSignalTests
{
    // Local\ prefix matches the production event (App.ShutdownRequestEventName); a per-test GUID
    // keeps parallel test runs from colliding on the same named kernel object.
    private static string UniqueEventName() => "Local\\TNDrop_Test_ShutdownSignal_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Set_from_a_second_handle_invokes_the_callback()
    {
        var eventName = UniqueEventName();
        using var fired = new ManualResetEventSlim(false);
        using var signal = new ShutdownSignal(eventName, () => fired.Set());

        // Simulates the installer: it never holds the handle ShutdownSignal created, only opens
        // the same named event and signals it.
        using var external = EventWaitHandle.OpenExisting(eventName);
        external.Set();

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "callback did not fire within timeout");
    }

    [Fact]
    public void Dispose_stops_further_callbacks()
    {
        var eventName = UniqueEventName();
        var callCount = 0;
        using var fired = new ManualResetEventSlim(false);
        var signal = new ShutdownSignal(eventName, () =>
        {
            Interlocked.Increment(ref callCount);
            fired.Set();
        });

        using var external = EventWaitHandle.OpenExisting(eventName);
        external.Set();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "callback did not fire before Dispose");

        signal.Dispose();
        fired.Reset();

        // Same external handle keeps the named kernel object alive after ShutdownSignal's own
        // handle is closed, so this Set is a real signal with nobody left registered to see it.
        external.Set();
        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(500)), "callback fired after Dispose");
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Dispose_is_safe_to_call_twice()
    {
        var eventName = UniqueEventName();
        var signal = new ShutdownSignal(eventName, () => { });

        signal.Dispose();
        var ex = Record.Exception(() => { signal.Dispose(); });
        Assert.Null(ex);
    }
}
