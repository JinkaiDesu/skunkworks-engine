using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static Skunkworks.System.ConPtyNative;
namespace Skunkworks.System
{
    // One shell generation. Only the transport worker starts/resizes/disposes it.
    // Reader and writer are independent: a child that stops reading cannot freeze output or Unity.
    internal sealed class ShellProcess : IDisposable
    {
        private readonly object inputGate = new object();
        private readonly Queue<string> inputQueue = new Queue<string>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private FileStream input, output;
        private IntPtr console, process, job, writerHandle;
        private Thread reader, writer;
        private volatile bool stopping, closed;
        private int queued;
        private string error = "";
        private readonly Action<char[], int> receive;
        public int ProcessId { get; private set; }
        public string Error { get { lock (inputGate) return error; } }

        public ShellProcess(string executable, string directory, string environment, int columns, int rows, Action<char[], int> receive, string arguments = "")
        {
            this.receive = receive;
            if (!File.Exists(executable)) throw new FileNotFoundException("Shell executable not found: " + executable, executable);
            IntPtr readIn = IntPtr.Zero, writeIn = IntPtr.Zero, readOut = IntPtr.Zero, writeOut = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero, env = IntPtr.Zero, childThread = IntPtr.Zero;
            bool initialized = false;
            try
            {
                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = false };
                Check(CreatePipe(out readIn, out writeIn, ref sa, 0));
                Check(CreatePipe(out readOut, out writeOut, ref sa, 0));
                HResult(CreatePseudoConsole(new COORD((short)columns, (short)rows), readIn, writeOut, 0, out console));
                Close(ref readIn); Close(ref writeOut);
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size)); initialized = true;
                Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                var startup = new STARTUPINFOEX { lpAttributeList = attributes };
                startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                // Explicit null std handles prevent inheriting a redirected host console.
                // ConPTY supplies the child's actual console handles (also used by Windows Terminal).
                startup.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES
                env = Marshal.StringToHGlobalUni(environment);
                job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero); Check(job != IntPtr.Zero);
                var limits = new JOB_EXTENDED_LIMIT(); limits.BasicLimitInformation.LimitFlags = 0x00002000; // KILL_ON_JOB_CLOSE
                Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JOB_EXTENDED_LIMIT>()));
                PROCESS_INFORMATION info;
                // Suspended until in our job: descendants cannot escape startup ownership.
                Check(CreateProcessW(executable, new StringBuilder(Quote(executable) + (arguments.Length == 0 ? "" : " " + arguments)), IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000 | 0x00000400 | 0x00000004, env, directory, ref startup, out info));
                process = info.hProcess; childThread = info.hThread; ProcessId = checked((int)info.dwProcessId);
                Check(AssignProcessToJobObject(job, process));
                input = new FileStream(new SafeFileHandle(writeIn, true), FileAccess.Write, 4096, false); writeIn = IntPtr.Zero;
                output = new FileStream(new SafeFileHandle(readOut, true), FileAccess.Read, 8192, false); readOut = IntPtr.Zero;
                reader = new Thread(ReadLoop) { IsBackground = true, Name = "Skunkworks terminal reader" };
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "Skunkworks terminal writer" };
                reader.Start(); writer.Start();
                Check(ResumeThread(childThread) != uint.MaxValue);
            }
            catch { Dispose(); throw; }
            finally
            {
                Close(ref childThread); Close(ref readIn); Close(ref writeIn); Close(ref readOut); Close(ref writeOut);
                if (initialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
            }
        }
        public static string Quote(string value)
        {
            var b = new StringBuilder("\""); int slash = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slash++; continue; }
                b.Append('\\', c == '"' ? slash * 2 + 1 : slash); slash = 0; b.Append(c);
            }
            return b.Append('\\', slash * 2).Append('"').ToString();
        }
        public bool TryExit(out int code)
        {
            code = 0;
            uint wait = WaitForSingleObject(process, 0);
            if (wait == 258) return false;
            Check(wait == 0);
            uint exit; Check(GetExitCodeProcess(process, out exit)); code = unchecked((int)exit); return true;
        }
        public void Resize(int columns, int rows) => HResult(ResizePseudoConsole(console, new COORD((short)columns, (short)rows)));
        public void Send(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (inputGate)
            {
                if (stopping) throw new InvalidOperationException("Shell is stopping");
                if (text.Length > 65536 - queued) throw new InvalidOperationException("Terminal input queue is full");
                inputQueue.Enqueue(text); queued += text.Length; wake.Set();
            }
        }
        private void WriteLoop()
        {
            try
            {
                IntPtr copy; Check(DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), out copy, 0, false, 2));
                lock (inputGate) writerHandle = copy;
                while (!stopping)
                {
                    string text = null;
                    lock (inputGate) if (inputQueue.Count != 0) { text = inputQueue.Dequeue(); queued -= text.Length; }
                    if (text == null) { wake.WaitOne(25); continue; }
                    byte[] bytes = Encoding.UTF8.GetBytes(text); input.Write(bytes, 0, bytes.Length); input.Flush();
                }
            }
            catch (Exception failure) { if (!stopping) SetError(failure); }
            finally { lock (inputGate) { Close(ref writerHandle); wake.Dispose(); } }
        }
        private void ReadLoop()
        {
            var bytes = new byte[8192]; var chars = new char[8192]; Decoder decoder = Encoding.UTF8.GetDecoder();
            try
            {
                while (!closed)
                {
                    uint available;
                    if (!PeekNamedPipe(output.SafeFileHandle, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero))
                    {
                        int code = Marshal.GetLastWin32Error();
                        if (code == 109 || code == 232) break; // EOF is not a process-exit signal.
                        throw new Win32Exception(code);
                    }
                    if (available == 0) { Thread.Sleep(5); continue; }
                    int count = output.Read(bytes, 0, Math.Min(bytes.Length, (int)Math.Min(available, int.MaxValue)));
                    if (count == 0) break;
                    int decoded = decoder.GetChars(bytes, 0, count, chars, 0, false);
                    receive(chars, decoded);
                }
                int final = decoder.GetChars(new byte[0], 0, 0, chars, 0, true);
                if (final != 0) receive(chars, final);
            }
            catch (Exception failure) { if (!stopping) SetError(failure); }
        }
        private void SetError(Exception failure) { lock (inputGate) error = failure.Message; }
        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            // All of this runs off Unity. Keep draining output during ClosePseudoConsole.
            Close(ref job);
            if (process != IntPtr.Zero) TerminateProcess(process, 0);
            lock (inputGate)
            {
                if (writerHandle != IntPtr.Zero) CancelSynchronousIo(writerHandle);
                if (writer != null && writer.IsAlive) { try { wake.Set(); } catch (ObjectDisposedException) { } }
            }
            if (writer != null) writer.Join(2000);
            input?.Dispose(); input = null;
            if (console != IntPtr.Zero) { ClosePseudoConsole(console); console = IntPtr.Zero; }
            closed = true;
            if (reader != null) reader.Join(2000);
            output?.Dispose(); output = null;
            Close(ref process);
            if (writer == null) wake.Dispose();
            ProcessId = 0;
        }
        private static void Close(ref IntPtr handle) { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        private static void HResult(int result) { if (result != 0) Marshal.ThrowExceptionForHR(result); }
    }
}
