// Mouse and keyboard from the iPad: the page sends small binary messages over the WebSocket,
// Decode() turns them into InputEvents (pure, unit tested) and Apply() replays them on this PC
// with SendInput. Only a fixed set of keys is accepted; no Windows key, no shortcuts.
//
//   1 MOVE    x u16, y u16                 position on the shared screen, 0..65535
//   2 BUTTON  button u8 (0 left, 1 right, 2 middle), down u8 (1/0), x u16, y u16
//   3 WHEEL   dy i16, dx i16               120 = one notch, positive = up / right
//   4 TEXT    UTF-8 text                   typed with the iPad keyboard
//   5 KEY     code u8                      one of AllowedKeys (pressed and released)
//   6 MOVEBY  dx i16, dy i16               a mouse on the TV: moves the pointer like a real mouse
//                                          (across every screen, Windows' pointer speed applies)
//   7 BUTTONHERE button u8, down u8        a button press where the pointer is now
//   8 CLICKAT button u8, down u8, x u16, y u16   the TV's own pointer: press at that spot; the
//                                          PC's pointer goes there and, when the last button is
//                                          let go, back to where the PC's user had it
//   9 WHEELAT dy i16, dx i16, x u16, y u16 the same for the wheel

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TVCast
{
    enum InputKind { Move = 1, Button = 2, Wheel = 3, Text = 4, Key = 5, MoveBy = 6, ButtonHere = 7, ClickAt = 8, WheelAt = 9 }

    sealed class InputEvent
    {
        public InputKind Kind;
        public int X, Y;            // 0..65535
        public int Button;          // 0 left, 1 right, 2 middle
        public bool Down;
        public int WheelY, WheelX;
        public string Text;
        public byte Key;
    }

    static class RemoteInput
    {
        public const int MaxMessage = 512;

        /// <summary>virtual-key codes the page may send: editing and navigation only</summary>
        public static readonly byte[] AllowedKeys = {
            0x08 /*Backspace*/, 0x09 /*Tab*/, 0x0D /*Enter*/, 0x1B /*Esc*/, 0x20 /*Space*/,
            0x21 /*PgUp*/, 0x22 /*PgDn*/, 0x23 /*End*/, 0x24 /*Home*/,
            0x25 /*Left*/, 0x26 /*Up*/, 0x27 /*Right*/, 0x28 /*Down*/, 0x2E /*Delete*/ };

        /// <summary>null for anything malformed or not allowed</summary>
        public static InputEvent Decode(byte[] m, int n)
        {
            if (m == null || n < 1 || n > MaxMessage) return null;
            switch (m[0])
            {
                case 1:
                    if (n != 5) return null;
                    return new InputEvent { Kind = InputKind.Move, X = U16(m, 1), Y = U16(m, 3) };
                case 2:
                    if (n != 7 || m[1] > 2 || m[2] > 1) return null;
                    return new InputEvent { Kind = InputKind.Button, Button = m[1], Down = m[2] == 1, X = U16(m, 3), Y = U16(m, 5) };
                case 3:
                    if (n != 5) return null;
                    return new InputEvent { Kind = InputKind.Wheel, WheelY = Clamp((short)U16(m, 1)), WheelX = Clamp((short)U16(m, 3)) };
                case 4:
                    if (n < 2) return null;
                    string t;
                    try { t = new UTF8Encoding(false, true).GetString(m, 1, n - 1); }
                    catch (ArgumentException) { return null; }
                    foreach (char c in t) if (char.IsControl(c)) return null; // Enter etc. come as KEY
                    return new InputEvent { Kind = InputKind.Text, Text = t };
                case 5:
                    if (n != 2 || Array.IndexOf(AllowedKeys, m[1]) < 0) return null;
                    return new InputEvent { Kind = InputKind.Key, Key = m[1] };
                case 6:
                    if (n != 5) return null;
                    // one message moves at most 2000 pixels (a fast flick of a mouse is a few hundred)
                    return new InputEvent { Kind = InputKind.MoveBy, X = Step((short)U16(m, 1)), Y = Step((short)U16(m, 3)) };
                case 7:
                    if (n != 3 || m[1] > 2 || m[2] > 1) return null;
                    return new InputEvent { Kind = InputKind.ButtonHere, Button = m[1], Down = m[2] == 1 };
                case 8:
                    if (n != 7 || m[1] > 2 || m[2] > 1) return null;
                    return new InputEvent { Kind = InputKind.ClickAt, Button = m[1], Down = m[2] == 1, X = U16(m, 3), Y = U16(m, 5) };
                case 9:
                    if (n != 9) return null;
                    return new InputEvent { Kind = InputKind.WheelAt, WheelY = Clamp((short)U16(m, 1)), WheelX = Clamp((short)U16(m, 3)), X = U16(m, 5), Y = U16(m, 7) };
            }
            return null;
        }

        static int U16(byte[] m, int o) { return m[o] << 8 | m[o + 1]; }

        // one message scrolls at most 10 notches
        static int Clamp(int v) { return Math.Max(-1200, Math.Min(1200, v)); }

        static int Step(int v) { return Math.Max(-2000, Math.Min(2000, v)); }

        // ---- replay with SendInput ------------------------------------------------------------

        [StructLayout(LayoutKind.Explicit, Size = 40)] // INPUT on x64
        struct INPUT
        {
            [FieldOffset(0)] public uint type;
            // MOUSEINPUT
            [FieldOffset(8)] public int dx;
            [FieldOffset(12)] public int dy;
            [FieldOffset(16)] public uint mouseData;
            [FieldOffset(20)] public uint mouseFlags;
            // KEYBDINPUT (same union)
            [FieldOffset(8)] public ushort vk;
            [FieldOffset(10)] public ushort scan;
            [FieldOffset(12)] public uint keyFlags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        const uint MOUSE = 0, KEYBOARD = 1;
        const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010,
            MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, WHEEL = 0x0800, HWHEEL = 0x1000, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
        const uint KEYUP = 0x0002, UNICODE = 0x0004, EXTENDED = 0x0001;

        static readonly bool[] held = new bool[3]; // left, right, middle pressed by the iPad

        // the screen being shown, in desktop pixels (width 0 = not set: the primary screen)
        static volatile int[] target = new int[4];

        /// <summary>the shown screen; touches and clicks land on it (also a second, virtual one)</summary>
        public static void SetTarget(int left, int top, int width, int height)
        {
            target = new[] { left, top, width, height };
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        /// <summary>
        /// A point on the shown screen (0..65535 across it) as SendInput's absolute coordinates over
        /// the whole virtual desktop (MOUSEEVENTF_VIRTUALDESK: 0..65535 from its left/top to its
        /// right/bottom pixel). Pure arithmetic, unit-tested.
        /// </summary>
        internal static int[] ToVirtualDesk(int x, int y, int[] screen, int vLeft, int vTop, int vWidth, int vHeight)
        {
            double px = screen[0] + x / 65535.0 * (screen[2] - 1), py = screen[1] + y / 65535.0 * (screen[3] - 1);
            int nx = (int)Math.Round((px - vLeft) * 65535.0 / Math.Max(1, vWidth - 1));
            int ny = (int)Math.Round((py - vTop) * 65535.0 / Math.Max(1, vHeight - 1));
            return new[] { Math.Max(0, Math.Min(65535, nx)), Math.Max(0, Math.Min(65535, ny)) };
        }

        /// <summary>the event's position for SendInput and the flags that go with it</summary>
        static int[] Where(InputEvent e, out uint flags)
        {
            int[] t = target;
            flags = ABSOLUTE;
            if (t[2] <= 0 || t[3] <= 0) return new[] { e.X, e.Y }; // the primary screen
            flags = ABSOLUTE | VIRTUALDESK;
            // SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN
            return ToVirtualDesk(e.X, e.Y, t, GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        }

        /// <summary>lets go of every button the iPad still holds (the viewer left mid-drag)</summary>
        public static void ReleaseButtons()
        {
            for (int b = 0; b < 3; b++)
            {
                if (!held[b]) continue;
                held[b] = false;
                Send(Mouse(b == 0 ? LEFTUP : b == 1 ? RIGHTUP : MIDDLEUP, 0, 0, 0));
            }
            PutBack(); // a TV drag cut off: the PC's user gets the pointer back
        }

        /// <summary>Replays one message on the screen being shared (see Where).</summary>
        public static void Apply(InputEvent e)
        {
            uint at;
            int[] p;
            switch (e.Kind)
            {
                case InputKind.Move:
                    // phone / iPad: the PC's pointer is only borrowed while a finger presses (a
                    // click, a drag) and then goes back to where the PC's user had it; a bare move
                    // just remembers the spot (for the wheel), so the pointer doesn't wander off
                    lastX = e.X; lastY = e.Y;
                    if (!(held[0] || held[1] || held[2])) break;
                    p = Where(e, out at);
                    Send(Mouse(MOVE | at, p[0], p[1], 0));
                    break;
                case InputKind.Button:
                    uint f = e.Button == 0 ? (e.Down ? LEFTDOWN : LEFTUP)
                           : e.Button == 1 ? (e.Down ? RIGHTDOWN : RIGHTUP)
                           : (e.Down ? MIDDLEDOWN : MIDDLEUP);
                    if (e.Down && !(held[0] || held[1] || held[2]) && !saved) saved = GetCursorPos(out savedAt);
                    held[e.Button] = e.Down;
                    lastX = e.X; lastY = e.Y;
                    // noted before the click is sent: Windows may activate the window at once
                    Clicked(true);
                    p = Where(e, out at);
                    Send(Mouse(MOVE | at, p[0], p[1], 0), Mouse(f | at, p[0], p[1], 0));
                    if (!e.Down && !(held[0] || held[1] || held[2])) { Clicked(true); PutBack(); }
                    break;
                case InputKind.MoveBy:
                    // relative, like a real mouse: Windows applies its pointer speed and the
                    // pointer crosses from the shown screen to the others and back
                    Send(Mouse(MOVE, e.X, e.Y, 0));
                    break;
                case InputKind.ButtonHere:
                    uint h = e.Button == 0 ? (e.Down ? LEFTDOWN : LEFTUP)
                           : e.Button == 1 ? (e.Down ? RIGHTDOWN : RIGHTUP)
                           : (e.Down ? MIDDLEDOWN : MIDDLEUP);
                    held[e.Button] = e.Down;
                    Send(Mouse(h, 0, 0, 0));
                    if (!e.Down) Clicked(false);
                    break;
                case InputKind.ClickAt:
                    // the TV's own pointer: borrow the PC's pointer for the click, then give it back
                    bool anyHeld = held[0] || held[1] || held[2];
                    if (e.Down && !anyHeld) saved = GetCursorPos(out savedAt);
                    uint press = e.Button == 0 ? (e.Down ? LEFTDOWN : LEFTUP)
                           : e.Button == 1 ? (e.Down ? RIGHTDOWN : RIGHTUP)
                           : (e.Down ? MIDDLEDOWN : MIDDLEUP);
                    held[e.Button] = e.Down;
                    Clicked(true); // before the click: Windows may activate the window at once
                    p = Where(e, out at);
                    Send(Mouse(MOVE | at, p[0], p[1], 0), Mouse(press | at, p[0], p[1], 0));
                    if (!e.Down && !(held[0] || held[1] || held[2])) { Clicked(true); PutBack(); }
                    break;
                case InputKind.WheelAt:
                    bool keep = held[0] || held[1] || held[2];
                    if (!keep) saved = GetCursorPos(out savedAt);
                    p = Where(e, out at);
                    Send(Mouse(MOVE | at, p[0], p[1], 0));
                    if (e.WheelY != 0) Send(Mouse(WHEEL, 0, 0, e.WheelY));
                    if (e.WheelX != 0) Send(Mouse(HWHEEL, 0, 0, e.WheelX));
                    if (!keep) PutBack();
                    break;
                case InputKind.Wheel:
                    // where the finger last was, not where the PC's own pointer is
                    bool inDrag = held[0] || held[1] || held[2];
                    if (!inDrag && !saved) saved = GetCursorPos(out savedAt);
                    p = Where(new InputEvent { X = lastX, Y = lastY }, out at);
                    Send(Mouse(MOVE | at, p[0], p[1], 0));
                    if (e.WheelY != 0) Send(Mouse(WHEEL, 0, 0, e.WheelY));
                    if (e.WheelX != 0) Send(Mouse(HWHEEL, 0, 0, e.WheelX));
                    if (!inDrag) PutBack();
                    break;
                case InputKind.Text:
                    if (!ActiveWindowOnShownScreen()) { Dropped++; break; }
                    foreach (char c in e.Text) Send(Key(0, c, UNICODE), Key(0, c, UNICODE | KEYUP));
                    break;
                case InputKind.Key:
                    if (!ActiveWindowOnShownScreen()) { Dropped++; break; }
                    // arrows, Home/End, PgUp/PgDn and Delete are extended keys
                    uint ext = (e.Key >= 0x21 && e.Key <= 0x28) || e.Key == 0x2E ? EXTENDED : 0;
                    Send(Key(e.Key, '\0', ext), Key(e.Key, '\0', ext | KEYUP));
                    break;
            }
        }

        // the last click from the remote device (Environment.TickCount) and whether the PC's
        // pointer was put back after it (the TV's own pointer)
        static volatile int lastClickAt;
        static volatile bool lastClickPutBack, anyClick;

        static void Clicked(bool putBack)
        {
            lastClickAt = Environment.TickCount;
            lastClickPutBack = putBack;
            anyClick = true;
        }

        /// <summary>the shown screen (left, top, width, height); width 0 = the primary screen</summary>
        public static int[] Target { get { return target; } }

        /// <summary>
        /// A window that became active just now was started by the remote device's click (taskbar,
        /// Start, a shortcut): true within {@code ms} of that click while the PC's pointer is
        /// still on the shown screen, or after a TV own-pointer click (the pointer went back).
        /// </summary>
        /// <summary>ms since the remote device's last click (int.MaxValue if none)</summary>
        public static int SinceRemoteClick()
        {
            return anyClick ? unchecked(Environment.TickCount - lastClickAt) : int.MaxValue;
        }

        public static bool JustClickedRemotely(int ms, bool pointerOnShownScreen)
        {
            if (!anyClick || unchecked(Environment.TickCount - lastClickAt) > ms) return false;
            return pointerOnShownScreen || lastClickPutBack;
        }

        /// <summary>typed text / keys thrown away because the active window was on another screen</summary>
        public static volatile int Dropped;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        /// <summary>
        /// Text from the TV / phone / iPad is typed only into a window on the screen that device
        /// shows: with the main screen's window active (the PC's user typing there) it is dropped,
        /// so the two keyboards don't write into each other's windows.
        /// </summary>
        static bool ActiveWindowOnShownScreen()
        {
            IntPtr w = GetForegroundWindow();
            if (w == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(w, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi)) return true;
            int[] t = target;
            if (t[2] <= 0 || t[3] <= 0) t = new[] { 0, 0, GetSystemMetrics(0), GetSystemMetrics(1) }; // the primary screen
            return SameScreen(new[] { mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom }, t);
        }

        /// <summary>the shown screen (left, top, width, height) has its centre on that monitor
        /// (left, top, right, bottom); pure, unit-tested</summary>
        internal static bool SameScreen(int[] monitor, int[] shown)
        {
            int cx = shown[0] + shown[2] / 2, cy = shown[1] + shown[3] / 2;
            return cx >= monitor[0] && cx < monitor[2] && cy >= monitor[1] && cy < monitor[3];
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

        // where the PC's user had the pointer before the TV borrowed it (ClickAt / WheelAt)
        static POINT savedAt;
        static int lastX = 32768, lastY = 32768; // the phone / iPad finger's last spot (0..65535)
        static bool saved;

        static void PutBack()
        {
            if (!saved) return;
            saved = false;
            System.Threading.Thread.Sleep(15); // let the click reach the window under the TV's spot first
            SetCursorPos(savedAt.X, savedAt.Y);
        }

        static INPUT Mouse(uint flags, int x, int y, int data)
        {
            return new INPUT { type = MOUSE, dx = x, dy = y, mouseData = (uint)data, mouseFlags = flags };
        }

        static INPUT Key(ushort vk, char c, uint flags)
        {
            return new INPUT { type = KEYBOARD, vk = vk, scan = c, keyFlags = flags };
        }

        static void Send(params INPUT[] inputs)
        {
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
