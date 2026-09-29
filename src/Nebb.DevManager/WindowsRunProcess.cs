using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nebb.DevManager;

internal static class WindowsRunProcess
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint DuplicateSameAccess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr ReservedBytes;
        public IntPtr StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string workingDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess,
        bool inheritHandle, uint options);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        ref SecurityAttributes securityAttributes, uint creation, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    public static Process Start(string scriptPath, string workingDirectory,
        IReadOnlyList<EnvironmentEntry> overrides, string stdoutPath, string stderrPath,
        SafeFileHandle job)
    {
        using var stdout = File.OpenHandle(stdoutPath, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite);
        using var stderr = File.OpenHandle(stderrPath, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite);
        var attributes = new SecurityAttributes
            { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        using var stdin = CreateFile("NUL", 0x80000000, 0x00000003, ref attributes,
            3, 0, IntPtr.Zero);
        if (stdin.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        foreach (var handle in new[] { stdout, stderr })
            if (!SetHandleInformation(handle.DangerousGetHandle(), HandleFlagInherit,
                    HandleFlagInherit)) throw new Win32Exception(Marshal.GetLastWin32Error());

        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var arguments = new StringBuilder(CommandLineFor(scriptPath));
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles | StartfUseShowWindow,
            ShowWindow = 0,
            StdInput = stdin.DangerousGetHandle(),
            StdOutput = stdout.DangerousGetHandle(),
            StdError = stderr.DangerousGetHandle()
        };
        var variables = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!,
                StringComparer.OrdinalIgnoreCase);
        foreach (var entry in overrides) variables[entry.Name] = entry.Value;
        var block = string.Join('\0', variables.OrderBy(entry => entry.Key,
            StringComparer.OrdinalIgnoreCase).Select(entry => $"{entry.Key}={entry.Value}")) + "\0\0";
        var environment = Marshal.StringToHGlobalUni(block);
        ProcessInformation native = default;
        try
        {
            if (!CreateProcess(command, arguments, IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateNewProcessGroup | CreateUnicodeEnvironment,
                    environment, workingDirectory, ref startup, out native))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "프로세스를 시작할 수 없습니다.");
            var process = Process.GetProcessById(native.ProcessId);
            try
            {
                WindowsRunJob.Assign(job, process);
                // The process owns an inheritable handle too, so the named job can be
                // reopened after Nebb exits. Duplicating avoids a global inheritance race.
                if (!DuplicateHandle(GetCurrentProcess(), job.DangerousGetHandle(),
                        native.Process, out _, 0, true, DuplicateSameAccess))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "프로세스에 Job Object 핸들을 전달할 수 없습니다.");
                if (ResumeThread(native.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "프로세스를 재개할 수 없습니다.");
                return process;
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }
        finally
        {
            if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
            if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
            Marshal.FreeHGlobal(environment);
            SetHandleInformation(stdout.DangerousGetHandle(), HandleFlagInherit, 0);
            SetHandleInformation(stderr.DangerousGetHandle(), HandleFlagInherit, 0);
        }
    }

    public static string CommandLineFor(string scriptPath)
    {
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        return $"\"{command}\" /d /s /c \"\"{scriptPath}\"\"";
    }
}
