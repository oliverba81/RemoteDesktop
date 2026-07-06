using RemoteDesktop.Host.Native;
using RemoteDesktop.Shared.Protocol;
using static RemoteDesktop.Host.Native.NativeMethods;

namespace RemoteDesktop.Host.Input;

/// <summary>
/// Injects remote input on the host via SendInput. Printable characters arrive as
/// Unicode code units and are injected with KEYEVENTF_UNICODE (layout independent);
/// modifiers/shortcuts arrive as virtual-key codes. Tracks pressed modifiers so a
/// disconnect or focus loss can release them (no stuck Ctrl/Alt).
/// </summary>
public sealed class InputInjector
{
    private Rectangle _bounds = new(0, 0,
        Math.Max(1, GetSystemMetrics(SM_CXSCREEN)), Math.Max(1, GetSystemMetrics(SM_CYSCREEN)));

    /// <summary>The desktop rectangle the viewer's coordinates refer to (the
    /// captured monitor). Movement is mapped from it onto the virtual desktop.</summary>
    public void SetCaptureBounds(Rectangle bounds)
    {
        if (bounds.Width > 0 && bounds.Height > 0) _bounds = bounds;
    }

    private static readonly ushort[] ModifierVks =
    {
        0xA0, 0xA1, // L/R Shift
        0xA2, 0xA3, // L/R Control
        0xA4, 0xA5, // L/R Alt (Menu)
        0x5B, 0x5C, // L/R Win
    };

    public void Mouse(InputMousePayload p)
    {
        var inputs = new List<INPUT>(2);

        // The viewer normalizes to the captured monitor; re-normalize onto the
        // virtual desktop so non-primary monitors receive input too.
        (ushort nx, ushort ny) = MapToVirtualDesktop(p.NormX, p.NormY);
        inputs.Add(MouseInput(nx, ny, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));

        uint flags = 0;
        uint mouseData = 0;
        if (p.Buttons.HasFlag(MouseButtonFlags.LeftDown)) flags |= MOUSEEVENTF_LEFTDOWN;
        if (p.Buttons.HasFlag(MouseButtonFlags.LeftUp)) flags |= MOUSEEVENTF_LEFTUP;
        if (p.Buttons.HasFlag(MouseButtonFlags.RightDown)) flags |= MOUSEEVENTF_RIGHTDOWN;
        if (p.Buttons.HasFlag(MouseButtonFlags.RightUp)) flags |= MOUSEEVENTF_RIGHTUP;
        if (p.Buttons.HasFlag(MouseButtonFlags.MiddleDown)) flags |= MOUSEEVENTF_MIDDLEDOWN;
        if (p.Buttons.HasFlag(MouseButtonFlags.MiddleUp)) flags |= MOUSEEVENTF_MIDDLEUP;
        if (p.Buttons.HasFlag(MouseButtonFlags.Wheel))
        {
            flags |= MOUSEEVENTF_WHEEL;
            mouseData = unchecked((uint)p.WheelDelta);
        }
        if (flags != 0)
            inputs.Add(MouseInput(nx, ny, mouseData, flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));

        Send(inputs);
    }

    private (ushort, ushort) MapToVirtualDesktop(ushort normX, ushort normY)
    {
        double px = _bounds.X + normX / 65535.0 * _bounds.Width;
        double py = _bounds.Y + normY / 65535.0 * _bounds.Height;
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var nx = (ushort)Math.Clamp((px - vx) * 65535.0 / vw, 0, 65535);
        var ny = (ushort)Math.Clamp((py - vy) * 65535.0 / vh, 0, 65535);
        return (nx, ny);
    }

    public void Keyboard(InputKeyboardPayload p)
    {
        INPUT input;
        if (p.IsUnicode)
        {
            uint flags = KEYEVENTF_UNICODE | (p.IsDown ? 0 : KEYEVENTF_KEYUP);
            input = KeyInput(0, p.Value, flags);
        }
        else
        {
            uint flags = p.IsDown ? 0 : KEYEVENTF_KEYUP;
            if (p.IsExtended) flags |= KEYEVENTF_EXTENDEDKEY;
            ushort scan = MapVirtualKey(p.Value, MAPVK_VK_TO_VSC);
            input = KeyInput(p.Value, scan, flags);
        }
        Send(new[] { input });
    }

    /// <summary>Releases every modifier — called on disconnect / focus loss so a
    /// half-finished chord never leaves a modifier stuck down on the host.</summary>
    public void ReleaseAllModifiers()
    {
        var inputs = new List<INPUT>(ModifierVks.Length);
        foreach (ushort vk in ModifierVks)
        {
            ushort scan = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            inputs.Add(KeyInput(vk, scan, KEYEVENTF_KEYUP));
        }
        Send(inputs);
    }

    private static INPUT MouseInput(ushort nx, ushort ny, uint mouseData, uint flags) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT { dx = nx, dy = ny, mouseData = mouseData, dwFlags = flags }
        }
    };

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags }
        }
    };

    private static void Send(IReadOnlyList<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var array = inputs as INPUT[] ?? inputs.ToArray();
        SendInput((uint)array.Length, array, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
}
