// When casting from a second (virtual) screen stops, the windows left on it can't be seen any
// more: the virtual monitor stays switched on in Windows. MoveHome() puts every normal window
// from that screen onto the main one, at the same relative spot; maximized windows stay
// maximized, minimized ones come back there when restored.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TVCast
{
    static class WindowMover
    {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT minPosition, maxPosition;
            public RECT normalPosition;
        }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;

        /// <summary>
        /// Moves the windows whose (normal) position is on the screen (left, top, width, height) to
        /// the main screen's work area. Returns how many were moved.
        /// </summary>
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        /// <summary>what MoveHome did: windows moved, windows Windows refused to move (programs
        /// started as administrator), and one line per window for the log</summary>
        public class HomeResult
        {
            public int Moved, Refused;
            public readonly List<string> Log = new List<string>();
        }

        /// <summary>
        /// Where a window really is: a minimized one by its normal (restored) position, every other
        /// one by its actual rectangle. A maximized or snapped window's normal position can still be
        /// on the main screen while the window itself is on the other one. Pure, unit-tested.
        /// </summary>
        internal static int[] WhereItIs(int showCmd, int[] actual, int[] normal)
        {
            return showCmd == 2 /* SW_SHOWMINIMIZED */ ? normal : actual;
        }

        /// <summary>
        /// Moves the windows that are on the screen (left, top, width, height) to the main screen's
        /// work area, at the same relative spot; maximized ones are maximized again there, minimized
        /// ones come back there when restored.
        /// </summary>
        public static HomeResult MoveHome(int left, int top, int width, int height)
        {
            System.Drawing.Rectangle w = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            int[] from = { left, top, width, height }, to = { w.Left, w.Top, w.Width, w.Height };
            var found = new List<IntPtr>();
            EnumWindows((h, l) => { found.Add(h); return true; }, IntPtr.Zero);
            var result = new HomeResult();
            foreach (IntPtr h in found)
            {
                try
                {
                    if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) continue;
                    if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) continue;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) continue; // hidden store apps
                    var p = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                    RECT a;
                    if (!GetWindowPlacement(h, ref p) || !GetWindowRect(h, out a)) continue;
                    RECT r = p.normalPosition;
                    // normalPosition is in work-area coordinates (from the main screen's work area)
                    int[] normal = { r.Left + w.Left, r.Top + w.Top, r.Right - r.Left, r.Bottom - r.Top };
                    int[] actual = { a.Left, a.Top, a.Right - a.Left, a.Bottom - a.Top };
                    if (!CentreOn(WhereItIs(p.showCmd, actual, normal), from)) continue;
                    var title = new System.Text.StringBuilder(80);
                    GetWindowText(h, title, 80);
                    bool ok;
                    string how;
                    if (p.showCmd == 2) // minimized: only its restored position moves
                    {
                        int[] n = MapRect(normal, from, to);
                        p.normalPosition = new RECT { Left = n[0] - w.Left, Top = n[1] - w.Top, Right = n[0] + n[2] - w.Left, Bottom = n[1] + n[3] - w.Top };
                        ok = SetWindowPlacement(h, ref p);
                        how = "minimized";
                    }
                    else if (p.showCmd == 3) // maximized
                    {
                        // Moving a maximized window with SetWindowPos makes Windows maximize it on the
                        // screen it lands on. SetWindowPlacement only changes the restored position
                        // and leaves it where it is, and restoring first let Chrome put it back on the
                        // screen it was maximized on (both tested with Chrome).
                        int[] n = CentreOn(normal, from) ? MapRect(normal, from, to) : normal; // its restored spot, on the main screen
                        ok = SetWindowPos(h, IntPtr.Zero, n[0], n[1], n[2], n[3], 0x14 /* SWP_NOZORDER | SWP_NOACTIVATE */);
                        GetWindowPlacement(h, ref p);
                        if (ok && p.showCmd != 3) ShowWindow(h, 3 /* SW_MAXIMIZE */);
                        // the restored position too, or a click on its taskbar button (minimize,
                        // restore) sends it back to the other screen
                        GetWindowPlacement(h, ref p);
                        p.normalPosition = new RECT { Left = n[0] - w.Left, Top = n[1] - w.Top, Right = n[0] + n[2] - w.Left, Bottom = n[1] + n[3] - w.Top };
                        p.flags = 0;
                        p.showCmd = 3;
                        SetWindowPlacement(h, ref p);
                        how = "maximized";
                    }
                    else // normal or snapped to a side: move what is shown
                    {
                        int[] n = MapRect(actual, from, to);
                        ok = SetWindowPos(h, IntPtr.Zero, n[0], n[1], n[2], n[3], 0x14 /* SWP_NOZORDER | SWP_NOACTIVATE */);
                        how = "normal";
                    }
                    if (ok) result.Moved++; else result.Refused++;
                    result.Log.Add("\"" + title + "\" " + how + (ok ? " moved home" : " refused (" + Marshal.GetLastWin32Error() + ")"));
                }
                catch (Exception) { } // a window that went away meanwhile
            }
            return result;
        }

        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        static WinEventProc watching; // kept alive while hooked

        /// <summary>calls {@code on} (on this, the UI thread) whenever another window becomes the
        /// active one; returns the hook for Unwatch</summary>
        public static IntPtr WatchForeground(Action<IntPtr> on)
        {
            watching = (hook, ev, hwnd, obj, child, thread, time) => { if (obj == 0) on(hwnd); };
            return SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, watching, 0, 0, 0 /* out of context */);
        }

        public static void Unwatch(IntPtr hook) { if (hook != IntPtr.Zero) UnhookWinEvent(hook); }

        // ---- the PC's own mouse clicks (not the ones CastLink injects for the phone / TV) ----
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        static LowLevelProc clicks; // kept alive while hooked
        static volatile int realClickAt;
        static volatile bool anyRealClick;
        static int realX, realY;

        /// <summary>notes every real (not injected) mouse button press on this PC; returns the hook</summary>
        public static IntPtr WatchRealClicks()
        {
            clicks = (code, wParam, lParam) =>
            {
                int msg = wParam.ToInt32();
                // WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN
                if (code >= 0 && (msg == 0x201 || msg == 0x204 || msg == 0x207))
                {
                    var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if ((m.flags & 1) == 0) // LLMHF_INJECTED not set: the PC's own mouse
                    {
                        realX = m.pt.X; realY = m.pt.Y;
                        realClickAt = Environment.TickCount;
                        anyRealClick = true;
                    }
                }
                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            };
            return SetWindowsHookEx(14 /* WH_MOUSE_LL */, clicks, GetModuleHandle(null), 0);
        }

        public static void UnwatchClicks(IntPtr hook) { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); }

        /// <summary>ms since the PC's own last click (int.MaxValue if none) and where it was</summary>
        public static int SinceRealClick(out System.Drawing.Point at)
        {
            at = new System.Drawing.Point(realX, realY);
            return anyRealClick ? unchecked(Environment.TickCount - realClickAt) : int.MaxValue;
        }

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder b, int max);

        /// <summary>
        /// Puts one top-level window onto the screen (left, top, width, height), at the same
        /// relative spot it had on its own screen; a maximized one is restored, moved and
        /// maximized again there. Returns what happened, for the log.
        /// </summary>
        public static string MoveToScreen(IntPtr h, int left, int top, int width, int height)
        {
            h = GetAncestor(h, 2 /* GA_ROOT */);
            if (h == IntPtr.Zero) return "no window";
            var title = new System.Text.StringBuilder(80);
            GetWindowText(h, title, 80);
            string who = "\"" + title + "\"";
            if (!IsWindowVisible(h)) return who + " not visible";
            if (title.Length == 0) return who + " no title yet";
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return who + " tool window";
            var p = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(h, ref p)) return who + " no placement";
            RECT r = p.normalPosition;
            int[] now = { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
            int[] shown = { left, top, width, height };
            string at = " at " + now[0] + "," + now[1] + " " + now[2] + "x" + now[3] + " show " + p.showCmd;
            if (CentreOn(now, shown)) return who + at + " already on the shown screen";
            var cx = new System.Drawing.Point(now[0] + now[2] / 2, now[1] + now[3] / 2);
            System.Drawing.Rectangle fw = System.Windows.Forms.Screen.FromPoint(cx).WorkingArea;
            System.Drawing.Rectangle tw = System.Windows.Forms.Screen.FromRectangle(
                new System.Drawing.Rectangle(left, top, width, height)).WorkingArea;
            int[] n = MapRect(now, new[] { fw.Left, fw.Top, fw.Width, fw.Height }, new[] { tw.Left, tw.Top, tw.Width, tw.Height });
            bool maximized = p.showCmd == 3; // SW_SHOWMAXIMIZED
            if (maximized) { ShowWindow(h, 9 /* SW_RESTORE */); GetWindowPlacement(h, ref p); }
            p.normalPosition = new RECT { Left = n[0], Top = n[1], Right = n[0] + n[2], Bottom = n[1] + n[3] };
            p.flags = 0;
            p.showCmd = 1; // SW_SHOWNORMAL: move it as a normal window first
            bool ok = SetWindowPlacement(h, ref p);
            if (maximized) ShowWindow(h, 3 /* SW_MAXIMIZE */);
            return who + at + (ok ? " moved to " + n[0] + "," + n[1] : " move refused (" + Marshal.GetLastWin32Error() + ")")
                + (maximized ? ", maximized again" : "");
        }

        /// <summary>the rectangle's centre lies on the screen (both left, top, width, height)</summary>
        internal static bool CentreOn(int[] r, int[] screen)
        {
            int cx = r[0] + r[2] / 2, cy = r[1] + r[3] / 2;
            return cx >= screen[0] && cx < screen[0] + screen[2] && cy >= screen[1] && cy < screen[1] + screen[3];
        }

        /// <summary>
        /// The same relative spot on the other screen; a window larger than it is shrunk to fit and
        /// the result always lies fully inside. Pure, unit-tested.
        /// </summary>
        internal static int[] MapRect(int[] r, int[] from, int[] to)
        {
            int w = Math.Min(r[2], to[2]), h = Math.Min(r[3], to[3]);
            double fx = from[2] > r[2] ? (r[0] - from[0]) / (double)(from[2] - r[2]) : 0;
            double fy = from[3] > r[3] ? (r[1] - from[1]) / (double)(from[3] - r[3]) : 0;
            fx = Math.Max(0, Math.Min(1, fx));
            fy = Math.Max(0, Math.Min(1, fy));
            int x = to[0] + (int)Math.Round(fx * (to[2] - w)), y = to[1] + (int)Math.Round(fy * (to[3] - h));
            return new[] { x, y, w, h };
        }
    }
}
