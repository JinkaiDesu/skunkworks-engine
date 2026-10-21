using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
namespace Skunkworks.System
{
    internal sealed class TerminalTransport : IDisposable
    {
        internal sealed class Snapshot
        {
            public readonly string Screen, Error, RichText;
            public readonly BackgroundRun[] Backgrounds;
            public readonly int X, Y;
            public readonly int Columns, Rows;
            internal readonly TerminalCell[] Cells;
            public readonly bool CursorVisible, ApplicationCursor, BracketedPaste, Alternate;
            public Snapshot(string screen, int x, int y, string error = "", bool visible = true, bool applicationCursor = false, bool bracketedPaste = false, string richText = "", BackgroundRun[] backgrounds = null, int columns = 0, int rows = 0, TerminalCell[] cells = null, bool alternate = false)
            { Screen = screen; X = x; Y = y; Error = error; CursorVisible = visible; ApplicationCursor = applicationCursor; BracketedPaste = bracketedPaste; RichText = richText; Backgrounds = backgrounds ?? new BackgroundRun[0]; Columns = columns; Rows = rows; Cells = cells ?? new TerminalCell[0]; Alternate = alternate; }
        }
        private readonly object gate = new object(), screenGate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly AnsiScreen screen = new AnsiScreen(120, 40);
        private readonly Thread worker;
        private readonly string executable, directory;
        private readonly string arguments, startupVariable, statusVariable;
        private readonly IDictionary environment;
        private ShellStartup startup;
        private string startupState = "None", startupDiagnostic = "";
        private int startupFailureRevision;
        public string StartupState { get { lock (gate) return startupState; } }
        public string StartupDiagnostic { get { lock (gate) return startupDiagnostic; } }
        public int StartupFailureRevision { get { lock (gate) return startupFailureRevision; } }
        private readonly Action<string, string> log;
        private ShellProcess shell;
        private Snapshot latest = new Snapshot("", 0, 0);
        private ShellState state = ShellState.Stopped;
        private string diagnostic = "";
        private bool stopping, wanted, finished;
        private int revision, appliedRevision, requestedColumns = 120, requestedRows = 40, appliedColumns = 120, appliedRows = 40;
        private int processId;
        public ShellState State { get { lock (gate) return state; } }
        public int ProcessId { get { lock (gate) return processId; } }
        public string Diagnostic { get { lock (gate) return diagnostic; } }
        public Snapshot Latest { get { lock (screenGate) return latest; } }
        public TerminalTransport(string executable, string directory, IDictionary environment, Action<string, string> log, string arguments = "", string startupVariable = "", string statusVariable = "")
        {
            this.executable = executable; this.directory = directory;
            this.arguments = arguments; this.startupVariable = startupVariable; this.statusVariable = statusVariable;
            this.environment = new Hashtable(environment); this.log = log;
            Feed("");
            worker = new Thread(Run) { IsBackground = true, Name = "Skunkworks shell supervisor" }; worker.Start();
        }
        internal static string EnvironmentBlock(IDictionary source)
        {
            var sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in source)
            {
                string key = entry.Key.ToString(), value = entry.Value == null ? "" : entry.Value.ToString();
                if (key.IndexOf('\0') >= 0 || value.IndexOf('\0') >= 0) throw new ArgumentException("Environment contains NUL");
                sorted[key] = value;
            }
            var b = new StringBuilder(); foreach (var entry in sorted) b.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
            if (sorted.Count == 0) b.Append('\0');
            return b.Append('\0').ToString();
        }
        public void Restart()
        {
            lock (gate)
            {
                if (stopping || finished) throw new InvalidOperationException("Terminal has stopped");
                wanted = true; revision++; state = processId == 0 ? ShellState.Starting : ShellState.Restarting;
                diagnostic = "Starting shell..."; wake.Set();
            }
        }
        public void StopShell()
        {
            lock (gate) { if (stopping || finished) return; wanted = false; revision++; wake.Set(); }
        }
        public void Send(string text)
        {
            lock (gate)
            {
                if (stopping || state != ShellState.Running || shell == null) throw new InvalidOperationException("Shell is not running; use Restart shell");
                shell.Send(text); // bounded queue only; no native I/O
            }
        }
        public void RequestSize(int columns, int rows)
        {
            AnsiScreen.ValidateSize(columns, rows);
            lock (gate) { if (stopping || finished) return; requestedColumns = columns; requestedRows = rows; wake.Set(); }
        }
        public string History() { lock (screenGate) return screen.History(); }
        public void Dispose()
        {
            lock (gate) { if (stopping || finished) return; stopping = true; wanted = false; revision++; wake.Set(); }
            // No Join/native calls on Unity's main thread. Worker owns all cleanup.
        }
        private void Run()
        {
            try
            {
                while (true)
                {
                    try
                    {
                    int current, columns, rows; bool start;
                    lock (gate) { if (stopping) return; current = revision; start = wanted; columns = requestedColumns; rows = requestedRows; }
                    if (current != appliedRevision)
                    {
                        CloseShell(); appliedRevision = current;
                        if (!start) { Notice("[shell stopped; use Restart shell]"); SetState(ShellState.Stopped, "Shell stopped"); }
                        else
                        {
                            try
                            {
                                Notice("[starting shell]");
                                startup = new ShellStartup(environment, directory, startupVariable, statusVariable);
                                lock (gate) { startupState = startup.State; startupDiagnostic = ""; }
                                var created = new ShellProcess(executable, directory, EnvironmentBlock(startup.Environment), columns, rows, Receive, arguments);
                                bool publish;
                                lock (gate)
                                {
                                    publish = current == revision && !stopping;
                                    if (publish) { shell = created; processId = created.ProcessId; state = ShellState.Running; diagnostic = ""; }
                                }
                                if (!publish) created.Dispose();
                                else { appliedColumns = columns; appliedRows = rows; SafeLog("shell started pid=" + created.ProcessId); }
                            }
                            catch (Exception failure)
                            { Notice("[shell failed] " + failure.Message + "\r\nUse Restart shell or Ctrl+Shift+F5."); SetState(ShellState.Failed, failure.Message, current); }
                        }
                    }
                    ShellProcess active; lock (gate) active = shell;
                    if (active != null)
                    {
                        if (startup != null && startup.Poll())
                        {
                            lock (gate) { startupState = startup.State; startupDiagnostic = startup.Diagnostic; if (startupState == "Failed") startupFailureRevision++; }
                            if (startup.State == "Failed") Notice("[startup program failed] " + startup.Diagnostic);
                            else SafeLog("startup program ready");
                        }
                        int exit;
                        if (active.TryExit(out exit))
                        {
                            CloseShell();
                            Notice("[shell exited: " + exit + "]\r\nUse Restart shell or Ctrl+Shift+F5.");
                            SetState(ShellState.Exited, "shell exited: " + exit, current);
                        }
                        else if (active.Error.Length != 0)
                        {
                            string failure = active.Error; CloseShell(); Notice("[terminal I/O failed] " + failure);
                            SetState(ShellState.Failed, failure, current);
                        }
                        else if (columns != appliedColumns || rows != appliedRows)
                        { active.Resize(columns, rows); appliedColumns = columns; appliedRows = rows; }
                    }
                    lock (screenGate)
                    {
                        if (screen.Columns != columns || screen.Rows != rows) { screen.Resize(columns, rows); Capture(); }
                    }
                    wake.WaitOne(25);
                    }
                    catch (Exception failure)
                    {
                        CloseShell(); Notice("[terminal failed] " + failure.Message);
                        SetState(ShellState.Failed, failure.Message);
                        wake.WaitOne(25); // keep supervisor alive for an explicit restart
                    }
                }
            }
            catch (Exception failure) { Notice("[terminal failed] " + failure.Message); SetState(ShellState.Failed, failure.Message); }
            finally
            {
                CloseShell();
                lock (gate) { finished = true; if (stopping) state = ShellState.Stopped; wake.Dispose(); }
            }
        }
        private void SetState(ShellState value, string message, int expected = -1)
        {
            lock (gate) { if (expected >= 0 && expected != revision) return; state = value; diagnostic = message; processId = 0; }
            SafeLog(value + ": " + message);
        }
        private void CloseShell()
        {
            ShellProcess old; lock (gate) { old = shell; shell = null; processId = 0; }
            try { old?.Dispose(); } catch (Exception failure) { SafeLog("Cleanup: " + failure.Message); }
            startup?.Dispose(); startup = null;
        }
        private void Receive(char[] text, int count)
        {
            string[] replies;
            lock (screenGate) { screen.Feed(text, count); Capture(); replies = screen.DrainReplies(); }
            ShellProcess active; lock (gate) active = shell;
            foreach (string reply in replies) { try { active?.Send(reply); } catch (Exception failure) { SafeLog(failure.Message); } }
        }
        private void Feed(string text) { lock (screenGate) { screen.Feed(text.ToCharArray(), text.Length); Capture(); } }
        private void Notice(string message) { lock (screenGate) { screen.Recover(message); Capture(); } SafeLog(message); }
        private void Capture()
        {
            var cells = new TerminalCell[screen.Columns * screen.Rows];
            for (int row = 0; row < screen.Rows; row++)
                for (int col = 0; col < screen.Columns; col++) cells[row * screen.Columns + col] = screen.Cell(col, row);
            latest = new Snapshot(screen.Render(), screen.CursorX, screen.CursorY, "", screen.CursorVisible,
                screen.ApplicationCursor, screen.BracketedPaste, "", null, screen.Columns, screen.Rows, cells, screen.Alternate);
        }
        private void SafeLog(string message) { try { log?.Invoke("terminal", message); } catch { } }
    }
}
