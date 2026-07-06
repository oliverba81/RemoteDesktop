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

        // Movement is always sent as an absolute position over the primary monitor.
        inputs.Add(MouseInput(p.NormX, p.NormY, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE));

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
            inputs.Add(MouseInput(p.NormX, p.NormY, mouseData, flags | MOUSEEVENTF_ABSOLUTE));

        Send(inputs);
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
