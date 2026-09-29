using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nebb.DevManager;

internal static class WindowsRunJob
{
    private const uint QueryAndTerminate = 0x0004 | 0x0008;
    // The launched root also holds an inheritable handle so its named job can be reopened.
    private static readonly Dictionary<string, SafeFileHandle> HeldJobs = new(StringComparer.Ordinal);

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint access, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass,
        out Accounting information, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryInformationJobObject")]
    private static extern bool QueryProcessIds(SafeFileHandle job, int infoClass,
        IntPtr information, int length, IntPtr returnLength);

    public static SafeFileHandle Create(string name)
    {
        lock (HeldJobs)
        {
            if (HeldJobs.TryGetValue(name, out var old))
            {
                if (ActiveProcesses(old) > 0)
                    throw new InvalidOperationException("이미 실행 중인 프로세스 그룹이 있습니다.");
                old.Dispose();
                HeldJobs.Remove(name);
            }
            var job = CreateJobObject(IntPtr.Zero, name);
            if (job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "실행 프로세스 그룹을 만들 수 없습니다.");
            if (ActiveProcesses(job) > 0)
            {
                job.Dispose();
                throw new InvalidOperationException("이미 실행 중인 프로세스 그룹이 있습니다.");
            }
            HeldJobs[name] = job;
            return job;
        }
    }

    public static void Assign(SafeFileHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "실행 프로세스를 그룹에 연결할 수 없습니다.");
    }

    public static bool IsRunning(string name)
    {
        lock (HeldJobs)
            if (HeldJobs.TryGetValue(name, out var held)) return ActiveProcesses(held) > 0;
        using var job = OpenJobObject(QueryAndTerminate, false, name);
        return !job.IsInvalid && ActiveProcesses(job) > 0;
    }

    public static IReadOnlyList<int>? ProcessIds(string name)
    {
        lock (HeldJobs)
            if (HeldJobs.TryGetValue(name, out var held)) return ReadProcessIds(held);
        using var job = OpenJobObject(QueryAndTerminate, false, name);
        return job.IsInvalid ? null : ReadProcessIds(job);
    }

    private static IReadOnlyList<int> ReadProcessIds(SafeFileHandle job)
    {
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts followed by ULONG_PTR entries.
        var capacity = 16;
        while (true)
        {
            var size = 8 + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryProcessIds(job, 3, buffer, size, IntPtr.Zero))
                {
                    var count = Marshal.ReadInt32(buffer, 4);
                    var result = new int[count];
                    for (var index = 0; index < count; index++)
                        result[index] = IntPtr.Size == 8
                            ? checked((int)Marshal.ReadInt64(buffer, 8 + index * 8))
                            : Marshal.ReadInt32(buffer, 8 + index * 4);
                    return result;
                }
                var error = Marshal.GetLastWin32Error();
                if (error != 234 || capacity >= 16384)
                    throw new Win32Exception(error, "프로세스 그룹 목록을 읽을 수 없습니다.");
                capacity *= 2;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public static bool ContainsProcess(string name, int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            lock (HeldJobs)
                if (HeldJobs.TryGetValue(name, out var held))
                    return IsProcessInJob(process.Handle, held, out var inside) && inside;
            using var job = OpenJobObject(QueryAndTerminate, false, name);
            return !job.IsInvalid && IsProcessInJob(process.Handle, job, out var member) && member;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public static bool Terminate(string name)
    {
        SafeFileHandle? held;
        lock (HeldJobs)
        {
            HeldJobs.TryGetValue(name, out held);
            if (held is not null) HeldJobs.Remove(name);
        }
        if (held is not null)
        {
            using (held)
            {
                if (ActiveProcesses(held) == 0) return false;
                Terminate(held);
                return true;
            }
        }
        using var job = OpenJobObject(QueryAndTerminate, false, name);
        if (job.IsInvalid || ActiveProcesses(job) == 0) return false;
        Terminate(job);
        return true;
    }

    public static void ReleaseIfEmpty(string name)
    {
        lock (HeldJobs)
        {
            if (!HeldJobs.TryGetValue(name, out var job) || ActiveProcesses(job) != 0) return;
            HeldJobs.Remove(name);
            job.Dispose();
        }
    }

    public static void Terminate(SafeFileHandle job)
    {
        if (!TerminateJobObject(job, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "실행 프로세스 그룹을 종료할 수 없습니다.");
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var information,
                Marshal.SizeOf<Accounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "실행 프로세스 상태를 읽을 수 없습니다.");
        return information.ActiveProcesses;
    }
}
