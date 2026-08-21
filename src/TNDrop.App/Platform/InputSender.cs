using System;
using System.Runtime.InteropServices;
using TNDrop.Services;

namespace TNDrop.Platform;

/// <summary>
/// Synthetic keyboard input and the two pieces of live Win32 state the click-to-paste rule needs
/// (v1.2 Task H): who owns the foreground window, and whether any physical modifier key is held.
/// <para>Separate from <see cref="WindowStyles"/> because nothing here is about a window of ours --
/// it reads and drives the SYSTEM input state. The decision of whether to call
/// <see cref="SendCtrlV"/> at all is not made here either: that is
/// <see cref="TNDrop.UI.ClickPaste.ShouldPasteOnClick"/>, which is pure and unit-tested.</para>
/// </summary>
public static class InputSender
{
    private const string Module = "InputSender";

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_V = 0x56;

    /// <summary>The high bit of GetAsyncKeyState's return: "the key is down right now".</summary>
    private const int KeyDownMask = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    /// <summary>
    /// The INPUT union. All three members are declared even though only the keyboard one is ever
    /// written: SendInput validates cbSize against the real sizeof(INPUT), and MOUSEINPUT is the
    /// largest member (40 bytes total on x64 vs 32 with only KEYBDINPUT). Dropping the unused
    /// members would make every call fail with ERROR_INVALID_PARAMETER.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// True when the window currently in the foreground belongs to this process.
    /// <para>Compared by PROCESS id, not by HWND against our own windows: TNDrop owns several
    /// top-level windows (shelf, trigger band, indicator, settings, the stack flyout's popup) and
    /// an HWND list would have to be kept in step with every one of them. Fails CLOSED -- an
    /// unreadable foreground reports true, i.e. "do not paste" -- because the cost of a wrong
    /// "false" is a keystroke fired into an unknown window.</para>
    /// </summary>
    public static bool IsOwnProcessForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                // No foreground window at all (a secure desktop, a transition): there is nothing
                // to paste into.
                return true;
            }

            if (GetWindowThreadProcessId(hwnd, out var pid) == 0)
            {
                FileLogger.Instance?.Warn(Module,
                    $"GetWindowThreadProcessId failed (Win32 error {Marshal.GetLastWin32Error()}); assuming own process");
                return true;
            }

            return pid == (uint)Environment.ProcessId;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"could not read the foreground window: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// True when any physical Ctrl, Shift, Alt or Win key is held right now.
    /// <para>GetAsyncKeyState rather than WPF's <c>Keyboard.Modifiers</c>: the shelf is
    /// WS_EX_NOACTIVATE and does not own the keyboard focus, so WPF's view of the modifier state is
    /// whatever it last saw in an input event delivered to US -- which, for a user holding Shift in
    /// another app, is nothing. This reads the real hardware state regardless of who has focus.
    /// Fails CLOSED (reports true) for the same reason as
    /// <see cref="IsOwnProcessForeground"/>.</para>
    /// </summary>
    public static bool AnyModifierDown()
    {
        try
        {
            return IsDown(VK_CONTROL) || IsDown(VK_SHIFT) || IsDown(VK_MENU)
                || IsDown(VK_LWIN) || IsDown(VK_RWIN);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"could not read the modifier key state: {ex.Message}");
            return true;
        }
    }

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & KeyDownMask) != 0;

    /// <summary>
    /// Sends one Ctrl+V to whatever window has the keyboard focus system-wide, as four INPUT
    /// records in a SINGLE SendInput call.
    /// <para>One call, not four: SendInput guarantees the batch is not interleaved with any other
    /// thread's synthetic or physical input, so the target can never observe a half-typed
    /// combination (a Ctrl that never comes up is the failure that leaves the whole desktop in a
    /// modified state).</para>
    /// <para>Failures are logged and swallowed. The clipboard write has already succeeded by the
    /// time this runs, so a refused SendInput degrades to exactly the v1.1 behavior -- the content
    /// is on the clipboard and the user pastes it themselves -- and must not surface as an error.
    /// UIPI blocks synthetic input into an elevated window from a non-elevated process; that is a
    /// legitimate refusal, not a bug to report to the user.</para>
    /// </summary>
    public static void SendCtrlV()
    {
        try
        {
            var inputs = new[]
            {
                KeyInput(VK_CONTROL, down: true),
                KeyInput(VK_V, down: true),
                KeyInput(VK_V, down: false),
                KeyInput(VK_CONTROL, down: false),
            };

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                FileLogger.Instance?.Warn(Module,
                    $"SendInput delivered {sent} of {inputs.Length} events (Win32 error {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"could not send the paste keystroke: {ex.Message}");
        }
    }

    private static INPUT KeyInput(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = down ? 0 : KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = UIntPtr.Zero,
            },
        },
    };
}
