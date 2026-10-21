using System;
using System.Collections;
using System.Threading;
namespace Skunkworks.System
{

    // Called by Main on Unity's thread. The transport never calls Unity APIs.
    public static class TerminalHost
    {
        private static TerminalTransport transport;
        public static ShellState State => transport == null ? ShellState.Stopped : transport.State;
        public static int ShellProcessId => transport == null ? 0 : transport.ProcessId;
        // A player without a terminal configuration has no shell: the terminal stays stopped and says why.
        private static string idle = "Terminal stopped";
        public static string Diagnostic => transport == null ? idle : transport.Diagnostic;
        public static string StartupState => transport == null ? "None" : transport.StartupState;
        public static string StartupDiagnostic => transport == null ? "" : transport.StartupDiagnostic;
        public static TerminalControl Controller { get; } = new TerminalControl();
        public static string Snapshot() => transport == null ? "" : transport.Latest.Screen;
        public static string Start()
        {
            if (transport != null) return "terminal already started";
            // Capture installation/env values once on Unity's thread.
            var launch = SystemPaths.Shell;
            if (launch.Executable.Length == 0) return idle = "No terminal configuration; set SKUNK_TERMINAL_CONFIG";
            transport = new TerminalTransport(launch.Executable, SystemPaths.UserRoot,
                SystemPaths.ChildEnvironment(), SystemPaths.Log, launch.Arguments, launch.StartupVariable, launch.StatusVariable);
            try { TerminalView.Start(transport); }
            catch { transport.Dispose(); transport = null; throw; }
            transport.Restart();
            return "terminal started";
        }
        public static string Restart()
        {
            if (transport == null) return Start();
            transport.Restart(); return "shell restart requested";
        }
        public static string StopShell()
        {
            transport?.StopShell(); return "shell stop requested";
        }
        public static string Stop()
        {
            TerminalView.Stop();
            TerminalTransport old = transport; transport = null; old?.Dispose();
            return "terminal stopped";
        }
        public static string Text(string text) { if (transport == null) throw new InvalidOperationException("Terminal stopped"); transport.Send(text); return "ok"; }
        public static string Send(string terminalBytes) => Text(terminalBytes);
        public static string Resize(int columns, int rows) { transport?.RequestSize(columns, rows); return "resize requested"; }
        public static string History() => transport == null ? "" : transport.History();

    }
    public sealed class TerminalControl
    {
        public bool Visible => TerminalView.Visible;
        public bool InputOwned => TerminalView.InputOwned;
        public int Columns => TerminalView.Columns;
        public int Rows => TerminalView.Rows;
        public int PointSize => TerminalView.PointSize;
        public float CellWidth => TerminalView.CellWidth;
        public float CellHeight => TerminalView.CellHeight;
        public string Show() => TerminalView.Show();
        public string Hide() => TerminalView.Hide();
        public string Focus() => TerminalView.Focus();
        public string Blur() => TerminalView.Blur();
        public string SetBounds(float x, float y, float width, float height) => TerminalView.SetBounds(x, y, width, height);
        public string RegisterGameplayMap(UnityEngine.InputSystem.InputActionMap map) => TerminalView.RegisterGameplayMap(map);
        public string UnregisterGameplayMap(UnityEngine.InputSystem.InputActionMap map) => TerminalView.UnregisterGameplayMap(map);
        public string SetScale(float scale) => TerminalView.SetScale(scale);
        public string SetFontFile(string path) => TerminalView.SetFontFile(path);
        public string SetFallbackFontFiles(string[] paths) => TerminalView.SetFallbackFontFiles(paths);
        public string ResetFont() => TerminalView.ResetFont();
        public ShellState State => TerminalHost.State;
        public int ShellProcessId => TerminalHost.ShellProcessId;
        public string Diagnostic => TerminalHost.Diagnostic;
        public string StartupState => TerminalHost.StartupState;
        public string StartupDiagnostic => TerminalHost.StartupDiagnostic;
        public string Snapshot() => TerminalHost.Snapshot();
        public string Restart() => TerminalHost.Restart();
        public string StopShell() => TerminalHost.StopShell();
        public string Text(string text) => TerminalHost.Text(text);
        public string Resize(int columns, int rows) => TerminalHost.Resize(columns, rows);
        public string History() => TerminalHost.History();
        public string Status() => State + (Diagnostic.Length == 0 ? "" : ": " + Diagnostic);
    }
}
