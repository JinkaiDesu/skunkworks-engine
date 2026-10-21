using System;
using System.Text;
using System.Runtime.InteropServices;
namespace Skunkworks.System
{
    internal static class ConPtyNative
    {
        [StructLayout(
            LayoutKind.Sequential
        )]
        internal struct COORD
        {
            public short X;
            public short Y;
            public COORD(
                short x,
                short y
            )
            {
                X =
                    x;
                Y =
                    y;
            }
        }
        [StructLayout(
            LayoutKind.Sequential
        )]
        internal struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(
                UnmanagedType.Bool
            )]
            public bool bInheritHandle;
        }
        [StructLayout(
            LayoutKind.Sequential,
            CharSet = CharSet.Unicode
        )]
        internal struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }
        [StructLayout(
            LayoutKind.Sequential
        )]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }
        [StructLayout(
            LayoutKind.Sequential
        )]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }
        [DllImport(
            "kernel32.dll",
            SetLastError = true
        )]
        internal static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            uint nSize
        );
        [DllImport(
            "kernel32.dll",
            SetLastError = true
        )]
        internal static extern bool SetHandleInformation(
            IntPtr hObject,
            uint dwMask,
            uint dwFlags
        );
        [DllImport(
            "kernel32.dll"
        )]
        internal static extern int CreatePseudoConsole(
            COORD size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC
        );
        [DllImport(
            "kernel32.dll"
        )]
        internal static extern int ResizePseudoConsole(
            IntPtr hPC,
            COORD size
        );
        [DllImport(
            "kernel32.dll"
        )]
        internal static extern void ClosePseudoConsole(
            IntPtr hPC
        );
        [DllImport(
            "kernel32.dll",
            SetLastError = true
        )]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize
        );
        [DllImport(
            "kernel32.dll",
            SetLastError = true
        )]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize
        );
        [DllImport(
            "kernel32.dll"
        )]
        internal static extern void DeleteProcThreadAttributeList(
            IntPtr lpAttributeList
        );
        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true
        )]
        internal static extern bool CreateProcessW(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation
        );
        [DllImport(
            "kernel32.dll",
            SetLastError = true
        )]
        internal static extern bool TerminateProcess(
            IntPtr hProcess,
            uint uExitCode
        );
        [DllImport(
            "kernel32.dll",
            SetLastError = true
        )]
        internal static extern bool CloseHandle(
            IntPtr hObject
        );
        [StructLayout(LayoutKind.Sequential)] internal struct JOB_BASIC_LIMIT
        {
            internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IO_COUNTERS
        { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct JOB_EXTENDED_LIMIT
        {
            internal JOB_BASIC_LIMIT BasicLimitInformation;
            internal IO_COUNTERS IoInfo;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JOB_EXTENDED_LIMIT info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool PeekNamedPipe(Microsoft.Win32.SafeHandles.SafeFileHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr remaining);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelSynchronousIo(IntPtr thread);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DuplicateHandle(IntPtr process, IntPtr handle, IntPtr target, out IntPtr copy, uint access, bool inherit, uint options);    }
}
