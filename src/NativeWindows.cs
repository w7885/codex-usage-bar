using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace CodexQuota
{
    public sealed class CodexWindowInfo
    {
        public IntPtr Handle { get; internal set; }
        public int ProcessId { get; internal set; }
        public int Left { get; internal set; }
        public int Top { get; internal set; }
        public int Right { get; internal set; }
        public int Bottom { get; internal set; }
        public string WindowClass { get; internal set; }
        public string ExecutablePath { get; internal set; }
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public Rect PhysicalBounds { get { return new Rect(Left, Top, Width, Height); } }
    }

    // All coordinates exposed by this class are physical desktop pixels.
    public static class NativeWindows
    {
        private const int GwlExStyle = -20;
        private const long WsExNoActivate = 0x08000000L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly HwndSourceHook NoActivateHook = HandleOverlayMessage;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int count);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLong64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLong64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")] private static extern uint NativeGetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")] private static extern IntPtr NativeSetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);

        public static IntPtr GetForegroundTopLevelWindow()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = GetAncestor(foreground, 2); // GA_ROOT
            return root == IntPtr.Zero ? foreground : root;
        }

        public static bool TryGetForegroundCodexWindow(out CodexWindowInfo info)
        {
            IntPtr root = GetForegroundTopLevelWindow();
            // Owned menus/dialogs stay attached to the main Codex window.
            IntPtr ownerRoot = root == IntPtr.Zero ? IntPtr.Zero : GetAncestor(root, 3);
            if (ownerRoot != IntPtr.Zero && TryGetCodexWindow(ownerRoot, out info)) return true;
            return TryGetCodexWindow(root, out info);
        }

        public static IList<CodexWindowInfo> GetCodexWindows()
        {
            var windows = new List<CodexWindowInfo>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr ignored)
            {
                CodexWindowInfo info;
                if (TryGetCodexWindow(hwnd, out info)) windows.Add(info);
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        public static bool TryGetCodexWindow(IntPtr hwnd, out CodexWindowInfo info)
        {
            info = null;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
            NativeRect rect;
            using (var dpiScope = new PhysicalPixelScope())
            {
                if (!GetWindowRect(hwnd, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
            }
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (!String.Equals(className.ToString(), "Chrome_WidgetWin_1", StringComparison.Ordinal)) return false;
            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            string executablePath = GetExecutablePath(processId);
            if (!IsCodexExecutablePath(executablePath)) return false;
            info = new CodexWindowInfo
            {
                Handle = hwnd, ProcessId = unchecked((int)processId), Left = rect.Left, Top = rect.Top,
                Right = rect.Right, Bottom = rect.Bottom, WindowClass = className.ToString(), ExecutablePath = executablePath
            };
            return true;
        }

        public static bool IsCodexExecutablePath(string path)
        {
            return !String.IsNullOrEmpty(path)
                && path.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0
                && path.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetExecutablePath(uint processId)
        {
            IntPtr process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process == IntPtr.Zero) return null;
            try
            {
                var path = new StringBuilder(32768);
                int length = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
            }
            finally { CloseHandle(process); }
        }

        public static uint GetDpiForWindow(IntPtr hwnd)
        {
            try { uint dpi = NativeGetDpiForWindow(hwnd); if (dpi > 0) return dpi; }
            catch (EntryPointNotFoundException) { }
            try
            {
                uint dpiX, dpiY;
                IntPtr monitor = MonitorFromWindow(hwnd, 2);
                if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 && dpiX > 0) return dpiX;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            IntPtr dc = GetDC(hwnd);
            if (dc == IntPtr.Zero) return 96;
            try { int dpi = GetDeviceCaps(dc, 88); return dpi > 0 ? (uint)dpi : 96; }
            finally { ReleaseDC(hwnd, dc); }
        }

        public static Rect GetMonitorWorkArea(IntPtr hwnd)
        {
            using (var dpiScope = new PhysicalPixelScope())
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                IntPtr monitor = MonitorFromWindow(hwnd, 2);
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                return new Rect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            }
        }

        public static void AttachNoActivate(Window window)
        {
            if (window == null) throw new ArgumentNullException("window");
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            Action attach = delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                long style = IntPtr.Size == 8 ? GetWindowLong64(hwnd, GwlExStyle).ToInt64() : GetWindowLong32(hwnd, GwlExStyle);
                style = (style | WsExNoActivate | WsExToolWindow) & ~WsExAppWindow;
                if (IntPtr.Size == 8) SetWindowLong64(hwnd, GwlExStyle, new IntPtr(style));
                else SetWindowLong32(hwnd, GwlExStyle, unchecked((int)style));
                HwndSource source = HwndSource.FromHwnd(hwnd);
                if (source != null) source.AddHook(NoActivateHook);
            };
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) attach();
            else window.SourceInitialized += delegate { attach(); };
        }

        private static IntPtr HandleOverlayMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmMouseActivate) { handled = true; return new IntPtr(MaNoActivate); }
            return IntPtr.Zero;
        }

        public static bool SetOverlayBounds(Window window, Rect physicalBounds)
        {
            if (window == null) throw new ArgumentNullException("window");
            if (physicalBounds.IsEmpty || physicalBounds.Width <= 0 || physicalBounds.Height <= 0) return false;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            using (var dpiScope = new PhysicalPixelScope())
            {
                return SetWindowPos(hwnd, HwndTopmost, (int)Math.Round(physicalBounds.Left), (int)Math.Round(physicalBounds.Top),
                    Math.Max(1, (int)Math.Round(physicalBounds.Width)), Math.Max(1, (int)Math.Round(physicalBounds.Height)),
                    SwpNoActivate | SwpNoOwnerZOrder);
            }
        }

        public static bool SetOverlayBounds(Window window, int left, int top, int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            return SetOverlayBounds(window, new Rect(left, top, width, height));
        }

        private sealed class PhysicalPixelScope : IDisposable
        {
            private readonly IntPtr previous;
            public PhysicalPixelScope()
            {
                try
                {
                    previous = NativeSetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
                    if (previous == IntPtr.Zero) previous = NativeSetThreadDpiAwarenessContext(new IntPtr(-3));
                }
                catch (EntryPointNotFoundException) { previous = IntPtr.Zero; }
            }
            public void Dispose()
            {
                if (previous != IntPtr.Zero) NativeSetThreadDpiAwarenessContext(previous);
            }
        }
    }
}
