using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamOptimizer
{
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int val, int size);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool DestroyIcon(IntPtr h);

        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;
        public const uint PROCESS_SUSPEND_RESUME = 0x0800, PROCESS_QUERY_INFO = 0x0400, PROCESS_SET_QUOTA = 0x0100;
        public const uint WM_CLOSE = 0x0010;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

        /// <summary>Returns pid -> list of top-level visible "app" windows (handle) with titles.</summary>
        public static Dictionary<int, List<IntPtr>> VisibleWindows(Dictionary<int, string> firstTitle)
        {
            Dictionary<int, List<IntPtr>> map = new Dictionary<int, List<IntPtr>>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, 4) != IntPtr.Zero) return true; // owned window
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                int len = GetWindowTextLength(h); if (len == 0) return true;
                StringBuilder sb = new StringBuilder(len + 1); GetWindowText(h, sb, sb.Capacity);
                uint pid; GetWindowThreadProcessId(h, out pid);
                List<IntPtr> list;
                if (!map.TryGetValue((int)pid, out list)) { list = new List<IntPtr>(); map[(int)pid] = list; }
                list.Add(h);
                if (firstTitle != null && !firstTitle.ContainsKey((int)pid)) firstTitle[(int)pid] = sb.ToString();
                return true;
            }, IntPtr.Zero);
            return map;
        }

        public static bool Suspend(int pid) { return Nt(pid, true); }
        public static bool Resume(int pid) { return Nt(pid, false); }
        static bool Nt(int pid, bool suspend)
        {
            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, (uint)pid);
            if (h == IntPtr.Zero) return false;
            try { return (suspend ? NtSuspendProcess(h) : NtResumeProcess(h)) >= 0; }
            finally { CloseHandle(h); }
        }
    }
}
