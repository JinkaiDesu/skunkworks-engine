using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Skunkworks.System
{
    // Desktop presentation only. No shell, world, or simulation policy.
    public sealed class WindowControl : IDisposable
    {
        private readonly bool standalone;
        private IntPtr window, originalSmall, originalLarge, ownedSmall, ownedLarge;
        private string originalTitle, suppliedTitle, iconFile;
        private bool disposed;
        public WindowControl(bool standalone = true) { this.standalone = standalone; }
        public string Title
        {
            get { EnsureWindow(); return ReadTitle(window); }
            set
            {
                if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Window title must be text without NUL.");
                EnsureWindow();
                if (!SetWindowText(window, value)) throw new Win32Exception(Marshal.GetLastWin32Error());
                suppliedTitle = value; LastError = null;
            }
        }
        public string IconFile { get { return iconFile; } }
        public string LastError { get; private set; }
        public void SetIconFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
                throw new ArgumentException("Supply an absolute path to a Windows .ico file.");
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Window icons currently use Windows .ico files.");
            if (!File.Exists(path)) throw new FileNotFoundException("Window icon not found.", path);
            EnsureWindow(); ApplyIcon(path); iconFile = path; LastError = null;
        }
        public void Reset()
        {
            EnsureWindow();
            RestoreWindow();
            ReleaseIcons();
            suppliedTitle = null; iconFile = null; LastError = null;
        }
        public void Pump()
        {
            if (disposed || (suppliedTitle == null && iconFile == null)) return;
            if (window != IntPtr.Zero && IsWindow(window) && IsWindowVisible(window)) return;
            try { EnsureWindow(); LastError = null; }
            catch (Exception error) { LastError = error.Message; }
        }
        private void EnsureWindow()
        {
            if (disposed) throw new ObjectDisposedException(nameof(WindowControl));
            if (!standalone) throw new InvalidOperationException("Window customization is available in the standalone player, not the Unity Editor.");
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("Window customization currently supports Windows.");
            if (window != IntPtr.Zero && IsWindow(window) && IsWindowVisible(window)) return;
            // Mono does not reliably populate Process.MainWindowHandle in the player.
            // Resolve the visible native window belonging to this process instead.
            uint processId = GetCurrentProcessId();
            IntPtr next = IntPtr.Zero;
            EnumWindows((candidate, unused) =>
            {
                GetWindowThreadProcessId(candidate, out uint owner);
                if (owner != processId || !IsWindowVisible(candidate)) return true;
                var name = new StringBuilder(256);
                GetClassName(candidate, name, name.Capacity);
                if (name.ToString() != "UnityWndClass") return true;
                next = candidate;
                return false;
            }, IntPtr.Zero);
            if (next == IntPtr.Zero) throw new InvalidOperationException("The player window is not ready.");
            RestoreWindow(); ReleaseIcons();
            window = next;
            originalTitle = ReadTitle(window);
            originalSmall = SendMessage(window, 0x007f, IntPtr.Zero, IntPtr.Zero);
            originalLarge = SendMessage(window, 0x007f, new IntPtr(1), IntPtr.Zero);
            if (suppliedTitle != null && !SetWindowText(window, suppliedTitle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (iconFile != null) ApplyIcon(iconFile);
        }
        private void ApplyIcon(string path)
        {
            uint dpi = GetDpiForWindow(window);
            IntPtr small = LoadImage(IntPtr.Zero, path, 1, GetSystemMetricsForDpi(49, dpi), GetSystemMetricsForDpi(50, dpi), 0x10);
            if (small == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the window .ico file.");
            IntPtr large = LoadImage(IntPtr.Zero, path, 1, GetSystemMetricsForDpi(11, dpi), GetSystemMetricsForDpi(12, dpi), 0x10);
            if (large == IntPtr.Zero) { int error = Marshal.GetLastWin32Error(); DestroyIcon(small); throw new Win32Exception(error, "Cannot load the window .ico file."); }
            SendMessage(window, 0x0080, IntPtr.Zero, small);
            SendMessage(window, 0x0080, new IntPtr(1), large);
            ReleaseIcons(); ownedSmall = small; ownedLarge = large;
        }
        private void RestoreWindow()
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return;
            SendMessage(window, 0x0080, IntPtr.Zero, originalSmall);
            SendMessage(window, 0x0080, new IntPtr(1), originalLarge);
            if (originalTitle != null) SetWindowText(window, originalTitle);
        }
        private void ReleaseIcons()
        {
            if (ownedSmall != IntPtr.Zero) DestroyIcon(ownedSmall);
            if (ownedLarge != IntPtr.Zero && ownedLarge != ownedSmall) DestroyIcon(ownedLarge);
            ownedSmall = ownedLarge = IntPtr.Zero;
        }
        private static string ReadTitle(IntPtr handle)
        {
            var text = new StringBuilder(GetWindowTextLength(handle) + 1);
            GetWindowText(handle, text, text.Capacity); return text.ToString();
        }
        public void Dispose()
        {
            if (disposed) return;
            RestoreWindow(); ReleaseIcons(); window = IntPtr.Zero; disposed = true;
        }
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetWindowText(IntPtr window, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    }
}
