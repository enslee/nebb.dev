using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nebb.DevManager;

internal static class WindowsRunJob
{
    private const uint QueryAndTerminate = 0x0004 | 0x0008;
    private const uint KillOnClose = 0x00002000;
    // Keep each job handle open until stop or app exit; npm can outlive its launcher.
    private static readonly Dictionary<string, SafeFileHandle> HeldJobs = new(StringComparer.Ordinal);

    static WindowsRunJob() => AppDomain.CurrentDomain.ProcessExit += (_, _) => TerminateAll();

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

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
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
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass,
        out Accounting information, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
        ref ExtendedLimits information, int length);

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
            var limits = new ExtendedLimits
                { BasicLimitInformation = new BasicLimits { LimitFlags = KillOnClose } };
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "실행 프로세스 종료 정책을 설정할 수 없습니다.");
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

    public static void TerminateAll()
    {
        string[] names;
        lock (HeldJobs) names = HeldJobs.Keys.ToArray();
        foreach (var name in names)
        {
            try { Terminate(name); }
            catch { /* Continue stopping the remaining run commands during shutdown. */ }
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
