using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nebb.DevManager;

internal enum RunItemStatus
{
    Idle, Starting, WaitingReady, Running, Completed, Failed, Stopping, Stopped,
    Exited, Unknown
}

internal enum RunProfileStatus
{
    Stopped, Starting, Running, PartiallyRunning, Failed, Stopping
}

internal sealed record RunItemState(string ItemId, RunItemStatus Status, string? Reason,
    int? ProcessId, int? ExitCode, string? InstanceId);

internal sealed record ProcessInstanceView(RunInstanceRecord Record, IReadOnlyList<int> Ports)
{
    public bool IsRunning => Record.Status is RunItemStatus.Starting or
        RunItemStatus.WaitingReady or RunItemStatus.Running or RunItemStatus.Stopping;
}

internal sealed record RunInstanceRecord(string InstanceId, string WorktreePath,
    string ProfileId, string ItemId, int? ProcessId, DateTime StartedUtc,
    DateTime? EndedUtc, RunItemStatus Status, int? ExitCode, string? Reason)
{
    public string? JobName { get; init; }
    public string RepositoryPath { get; init; } = "";
    public string RepositoryId { get; init; } = "";
    public string WorktreeId { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string ItemName { get; init; } = "";
    public RunLifecycle Lifecycle { get; init; } = RunLifecycle.LongRunning;
    public string Command { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string CommandLine { get; init; } = "";
    public string[] EnvironmentNames { get; init; } = [];
    public string EncryptedEnvironment { get; init; } = "";
    public int[] DetectedPorts { get; init; } = [];
    public string LogSessionId => InstanceId;
}

internal sealed class RunProfileManager
{
    private sealed class Instance
    {
        public required string Key { get; init; }
        public required string JobName { get; init; }
        public required string Folder { get; init; }
        public required RunInstanceRecord Record { get; set; }
        public required RunItem Item { get; init; }
        public string ProfileName { get; init; } = "";
        public Process? Process { get; set; }
        public Task? MonitorTask { get; set; }
        public bool Ready { get; set; }
        public bool Reconnected { get; set; }
    }

    private readonly string stateRoot;
    private readonly string repositoryPath;
    private readonly Dictionary<string, Instance> instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    public RunProfileManager(string repositoryPath, string? storageDirectory = null)
    {
        this.repositoryPath = Path.GetFullPath(repositoryPath);
        var root = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nebb", "DevManager", "profile-runs");
        stateRoot = Path.Combine(root, Hash(this.repositoryPath));
    }

    public RunItemState GetItemState(string worktreePath, RunProfile profile, RunItem item)
    {
        var key = Key(worktreePath, profile.Id, item.Id);
        lock (sync)
            if (instances.TryGetValue(key, out var active)) return View(Reconcile(active));
        var record = ReadLatest(worktreePath, profile.Id, item.Id);
        if (record is null) return new(item.Id, RunItemStatus.Idle, null, null, null, null);
        var instance = new Instance { Key = key, JobName = record.JobName ?? "",
            Folder = ItemFolder(worktreePath, profile.Id, item.Id), Record = record,
            Item = item, ProfileName = profile.Name };
        lock (sync) instances[key] = instance;
        return View(Reconcile(instance));
    }

    private RunInstanceRecord Reconcile(Instance instance)
    {
        var record = instance.Record;
        if (!IsActive(record.Status) || instance.MonitorTask is not null) return record;
        if (string.IsNullOrWhiteSpace(record.JobName))
        {
            Set(instance, RunItemStatus.Stopped, reason: "이전 버전의 실행 기록입니다.");
            return instance.Record;
        }
        IReadOnlyList<int>? members;
        try { members = WindowsRunJob.ProcessIds(record.JobName); }
        catch (Win32Exception error)
        {
            Set(instance, RunItemStatus.Unknown, reason: error.Message);
            return instance.Record;
        }
        if (members is null || members.Count == 0)
        {
            WindowsRunJob.ReleaseIfEmpty(record.JobName);
            var exitCodePath = Path.Combine(instance.Folder, record.InstanceId, "exit.code");
            var exitCode = File.Exists(exitCodePath) &&
                int.TryParse(File.ReadAllText(exitCodePath).Trim(), out var code) ? code : (int?)null;
            var status = record.Status == RunItemStatus.Stopping ? RunItemStatus.Stopped :
                record.Lifecycle == RunLifecycle.OneShot
                    ? exitCode == 0 ? RunItemStatus.Completed : RunItemStatus.Failed
                    : RunItemStatus.Exited;
            Set(instance, status, exitCode: exitCode,
                reason: exitCode is null ? "프로세스가 종료되었습니다. 종료 코드는 확인할 수 없습니다." : null);
            return instance.Record;
        }
        if (record.ProcessId is int rootPid && members.Contains(rootPid) && !instance.Reconnected)
        {
            try
            {
                using var root = Process.GetProcessById(rootPid);
                var sameStart = Math.Abs((root.StartTime.ToUniversalTime() - record.StartedUtc)
                    .TotalSeconds) < 2;
                var sameExecutable = string.Equals(root.MainModule?.FileName,
                    record.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                var commandLine = ProcessCommandLine.Read(rootPid);
                var sameCommand = string.Equals(commandLine, record.CommandLine,
                    StringComparison.OrdinalIgnoreCase);
                if (!sameStart || !sameExecutable || !sameCommand)
                {
                    Set(instance, RunItemStatus.Unknown,
                        reason: "Stale/Detached: 시작 시각, 실행 파일 또는 명령줄이 일치하지 않습니다.");
                    return instance.Record;
                }
                instance.Reconnected = true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                Set(instance, RunItemStatus.Unknown, reason: "Stale/Detached: " + error.Message);
                return instance.Record;
            }
        }
        else if (record.ProcessId is int pid && !members.Contains(pid) && !instance.Reconnected)
        {
            try
            {
                using var root = Process.GetProcessById(pid);
                Set(instance, RunItemStatus.Unknown,
                    reason: "Stale/Detached: 기록된 PID가 다른 프로세스에 사용 중입니다.");
                return instance.Record;
            }
            catch (ArgumentException) { /* Root exited; its children may still be in the named job. */ }
            instance.Reconnected = true;
        }
        if (record.Status is RunItemStatus.Starting or RunItemStatus.WaitingReady or RunItemStatus.Stopping)
            Set(instance, RunItemStatus.Running);
        return instance.Record;
    }

    public RunProfileStatus GetProfileStatus(string worktreePath, RunProfile profile)
    {
        var statuses = profile.Items.Where(item => item.Enabled)
            .Select(item => GetItemState(worktreePath, profile, item).Status).ToArray();
        if (statuses.Any(status => status == RunItemStatus.Stopping)) return RunProfileStatus.Stopping;
        if (statuses.Any(status => status is RunItemStatus.Starting or RunItemStatus.WaitingReady))
            return RunProfileStatus.Starting;
        var running = statuses.Any(status => status == RunItemStatus.Running);
        if (running && statuses.All(status => status is RunItemStatus.Running or RunItemStatus.Completed))
            return RunProfileStatus.Running;
        if (running) return RunProfileStatus.PartiallyRunning;
        if (statuses.Any(status => status == RunItemStatus.Failed)) return RunProfileStatus.Failed;
        return RunProfileStatus.Stopped;
    }

    public bool HasActiveItems(string worktreePath, RunProfile profile) =>
        profile.Items.Any(item => IsActive(GetItemState(worktreePath, profile, item).Status));

    public IReadOnlyList<ProcessInstanceView> ListInstances()
    {
        if (!Directory.Exists(stateRoot)) return [];
        var records = new Dictionary<string, RunInstanceRecord>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(stateRoot, "instance.json",
                     SearchOption.AllDirectories))
        {
            var record = ReadRecord(file);
            if (record is not null) records[record.InstanceId] = record;
        }
        foreach (var file in Directory.EnumerateFiles(stateRoot, "latest.json",
                     SearchOption.AllDirectories))
        {
            var record = ReadRecord(file);
            if (record is null) continue;
            var key = Key(record.WorktreePath, record.ProfileId, record.ItemId);
            Instance instance;
            lock (sync)
            {
                if (!instances.TryGetValue(key, out instance!))
                {
                    var item = new RunItem { Id = record.ItemId, Name = record.ItemName,
                        Lifecycle = record.Lifecycle };
                    instance = new Instance { Key = key, JobName = record.JobName ?? "",
                        Folder = Path.GetDirectoryName(file)!, Record = record, Item = item,
                        ProfileName = record.ProfileName };
                    instances[key] = instance;
                }
            }
            records[record.InstanceId] = Reconcile(instance);
        }
        var allPorts = ListeningPorts.All();
        var recentEnded = records.Values.Where(record => !IsActive(record.Status))
            .OrderByDescending(record => record.StartedUtc).Take(20)
            .Select(record => record.InstanceId).ToHashSet(StringComparer.Ordinal);
        return records.Values.OrderByDescending(record => IsActive(record.Status))
            .ThenByDescending(record => record.StartedUtc)
            .Where(record => IsActive(record.Status) || recentEnded.Contains(record.InstanceId))
            .Select(record =>
            {
                var members = IsActive(record.Status) && record.JobName is not null
                    ? WindowsRunJob.ProcessIds(record.JobName) : null;
                int[] ports = members is null ? record.DetectedPorts : allPorts
                    .Where(pair => members.Contains(pair.ProcessId))
                    .Select(pair => pair.Port).Distinct().Order().ToArray();
                if (!record.DetectedPorts.SequenceEqual(ports))
                {
                    record = record with { DetectedPorts = ports };
                    var key = Key(record.WorktreePath, record.ProfileId, record.ItemId);
                    lock (sync)
                        if (instances.TryGetValue(key, out var current) &&
                            current.Record.InstanceId == record.InstanceId)
                        {
                            current.Record = record;
                            Save(current);
                        }
                }
                return new ProcessInstanceView(record, ports);
            }).ToArray();
    }

    public async Task StopInstanceAsync(string instanceId, bool force = false)
    {
        var record = ListInstances().Select(view => view.Record)
            .FirstOrDefault(value => value.InstanceId == instanceId);
        if (record is null || !IsActive(record.Status)) return;
        var key = Key(record.WorktreePath, record.ProfileId, record.ItemId);
        Instance? instance;
        lock (sync) instances.TryGetValue(key, out instance);
        if (instance is not null && instance.Record.InstanceId == instanceId)
            await StopInstanceAsync(instance, force);
    }

    public async Task RestartInstanceAsync(string instanceId, RepositoryCommandSettings settings)
    {
        var record = ListInstances().Select(view => view.Record)
            .FirstOrDefault(value => value.InstanceId == instanceId);
        if (record is null || record.Status != RunItemStatus.Running) return;
        var profile = settings.EffectiveProfiles(record.WorktreePath)
            .FirstOrDefault(value => value.Id == record.ProfileId)
            ?? throw new InvalidOperationException("현재 설정에서 프로필을 찾을 수 없습니다.");
        if (!profile.Items.Any(value => value.Id == record.ItemId))
            throw new InvalidOperationException("현재 설정에서 실행 항목을 찾을 수 없습니다.");
        await RestartItemAsync(record.WorktreePath, profile, record.ItemId);
    }

    public async Task RestartRunningAsync(RepositoryCommandSettings settings)
    {
        var running = ListInstances().Select(view => view.Record)
            .Where(record => record.Status == RunItemStatus.Running &&
                record.Lifecycle == RunLifecycle.LongRunning)
            .GroupBy(record => (record.WorktreePath, record.ProfileId)).ToArray();
        var targets = running.Select(group =>
            (group.Key.WorktreePath, Profile: settings.EffectiveProfiles(group.Key.WorktreePath)
                .FirstOrDefault(value => value.Id == group.Key.ProfileId)
                ?? throw new InvalidOperationException(
                    $"{group.First().ProfileName} 프로필이 현재 설정에 없어 재시작할 수 없습니다.")))
            .ToArray();
        foreach (var target in targets)
            await RestartAllAsync(target.WorktreePath, target.Profile);
    }

    public async Task StopRunningAsync()
    {
        var running = ListInstances().Where(view => view.IsRunning).ToArray();
        await Task.WhenAll(running.Select(view => StopInstanceAsync(view.Record.InstanceId)));
    }

    public void ClearHistory() => ProcessHistory.Clean(stateRoot, clearAll: true);

    public void PruneHistory() => ProcessHistory.Clean(stateRoot, clearAll: false);

    public void ValidateSettingsTransition(RepositoryCommandSettings settings)
    {
        _ = ListInstances();
        lock (sync)
            foreach (var instance in instances.Values.Where(value => IsActive(value.Record.Status)))
            {
                var profile = settings.EffectiveProfiles(instance.Record.WorktreePath)
                    .FirstOrDefault(value => value.Id == instance.Record.ProfileId);
                if (profile?.Items.Any(item => item.Id == instance.Record.ItemId) != true)
                    throw new InvalidOperationException(
                        $"{instance.ProfileName} / {instance.Item.Name}이(가) 실행 중입니다. " +
                        "이 항목이나 프로필을 제거하기 전에 종료하세요.");
            }
    }

    public IReadOnlyList<string> FindPortConflicts(string worktreePath, RunProfile profile,
        string? targetItemId = null)
    {
        _ = ListInstances();
        var targets = new HashSet<string>();
        if (targetItemId is not null)
        {
            var byId = profile.Items.ToDictionary(item => item.Id);
            void Include(string id)
            {
                if (!targets.Add(id) || !byId.TryGetValue(id, out var item)) return;
                foreach (var dependency in item.DependsOn) Include(dependency);
            }
            Include(targetItemId);
        }
        var wanted = profile.Items.Where(item => item.Enabled &&
            (targetItemId is null || targets.Contains(item.Id) &&
                (item.Id == targetItemId || GetItemState(worktreePath, profile, item).Status
                    is not (RunItemStatus.Running or RunItemStatus.Completed))) &&
            item.Lifecycle == RunLifecycle.LongRunning &&
            item.Readiness.Type == RunReadinessKind.Port && item.Readiness.Port is not null).ToArray();
        var result = new List<string>();
        lock (sync)
            foreach (var item in wanted)
                foreach (var existing in instances.Values.Where(instance =>
                    IsActive(instance.Record.Status) &&
                    instance.Item.Readiness.Type == RunReadinessKind.Port &&
                    instance.Item.Readiness.Port == item.Readiness.Port &&
                    instance.Key != Key(worktreePath, profile.Id, item.Id)))
                    result.Add($"포트 {item.Readiness.Port}: {existing.ProfileName} / {existing.Item.Name}");
        return result.Distinct().ToArray();
    }

    public async Task RunAllAsync(string worktreePath, RunProfile profile)
    {
        RunProfileValidator.Validate(profile, worktreePath);
        if (HasActiveItems(worktreePath, profile))
            throw new InvalidOperationException("이 프로필이 이미 실행 중입니다. Restart All을 사용하세요.");
        await StartItemsAsync(worktreePath, profile, profile.Items.Where(item => item.Enabled));
    }

    public async Task RestartAllAsync(string worktreePath, RunProfile profile)
    {
        RunProfileValidator.Validate(profile, worktreePath);
        var targets = profile.Items.Where(item => item.Enabled &&
            item.Lifecycle == RunLifecycle.LongRunning &&
            GetItemState(worktreePath, profile, item).Status == RunItemStatus.Running).ToArray();
        await Task.WhenAll(targets.Select(item => StopItemAsync(worktreePath, profile, item.Id)));
        await StartItemsAsync(worktreePath, profile, targets, startDependencies: false);
    }

    public async Task StartItemAsync(string worktreePath, RunProfile profile, string itemId)
    {
        RunProfileValidator.Validate(profile, worktreePath);
        var item = profile.Items.SingleOrDefault(candidate => candidate.Id == itemId && candidate.Enabled)
            ?? throw new ArgumentException("실행할 항목이 없습니다.");
        await StartItemsAsync(worktreePath, profile, [item]);
    }

    public async Task RestartItemAsync(string worktreePath, RunProfile profile, string itemId)
    {
        await StopItemAsync(worktreePath, profile, itemId);
        await StartItemAsync(worktreePath, profile, itemId);
    }

    private async Task StartItemsAsync(string worktreePath, RunProfile profile,
        IEnumerable<RunItem> targets, bool startDependencies = true)
    {
        var targetIds = targets.Select(item => item.Id).ToHashSet();
        var byId = profile.Items.ToDictionary(item => item.Id);
        var tasks = new Dictionary<string, Task<bool>>();
        Task<bool> Start(RunItem item)
        {
            if (tasks.TryGetValue(item.Id, out var current)) return current;
            if (!startDependencies && !targetIds.Contains(item.Id))
            {
                var existing = GetItemState(worktreePath, profile, item).Status;
                return Task.FromResult(existing is RunItemStatus.Running or RunItemStatus.Completed);
            }
            var task = StartCore(item);
            tasks.Add(item.Id, task);
            return task;
        }
        async Task<bool> StartCore(RunItem item)
        {
            var dependencies = await Task.WhenAll(item.DependsOn.Select(id => Start(byId[id])));
            if (dependencies.Any(success => !success))
            {
                MarkBlocked(worktreePath, profile, item);
                return false;
            }
            var status = GetItemState(worktreePath, profile, item).Status;
            if (status == RunItemStatus.Running ||
                status == RunItemStatus.Completed && !targetIds.Contains(item.Id)) return true;
            return await LaunchAsync(worktreePath, profile, item);
        }
        await Task.WhenAll(targets.Select(Start));
    }

    private void MarkBlocked(string worktreePath, RunProfile profile, RunItem item)
    {
        var key = Key(worktreePath, profile.Id, item.Id);
        var folder = ItemFolder(worktreePath, profile.Id, item.Id);
        var record = new RunInstanceRecord(Guid.NewGuid().ToString("N"),
            Path.GetFullPath(worktreePath), profile.Id, item.Id, null, DateTime.UtcNow,
            DateTime.UtcNow, RunItemStatus.Idle, null, "선행 항목이 준비되지 않아 시작하지 않았습니다.")
            { ProfileName = profile.Name, ItemName = item.Name, Lifecycle = item.Lifecycle };
        var instance = new Instance { Key = key, JobName = "", Folder = folder,
            Record = record, Item = item, ProfileName = profile.Name };
        lock (sync) instances[key] = instance;
        Save(instance);
    }

    private async Task<bool> LaunchAsync(string worktreePath, RunProfile profile, RunItem item)
    {
        var key = Key(worktreePath, profile.Id, item.Id);
        var folder = ItemFolder(worktreePath, profile.Id, item.Id);
        var instanceId = Guid.NewGuid().ToString("N");
        var runFolder = Path.Combine(folder, instanceId);
        Directory.CreateDirectory(runFolder);
        var cwd = Path.GetFullPath(Path.Combine(worktreePath, item.WorkingDirectory));
        var jobName = JobName(instanceId);
        var gateScript = Path.Combine(runFolder, "gate.cmd");
        var executablePath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System), "cmd.exe");
        var instance = new Instance
        {
            Key = key, JobName = jobName, Folder = folder, Item = item,
            ProfileName = profile.Name,
            Record = new(instanceId, Path.GetFullPath(worktreePath), profile.Id, item.Id,
                null, DateTime.UtcNow, null, RunItemStatus.Starting, null, null)
            {
                JobName = jobName, ProfileName = profile.Name, ItemName = item.Name,
                RepositoryPath = repositoryPath, RepositoryId = Hash(repositoryPath),
                WorktreeId = Hash(worktreePath),
                Lifecycle = item.Lifecycle, Command = item.Command, Arguments = item.Arguments,
                WorkingDirectory = cwd, ExecutablePath = executablePath,
                CommandLine = WindowsRunProcess.CommandLineFor(gateScript),
                EnvironmentNames = item.Environment.Select(entry => entry.Name).ToArray(),
                EncryptedEnvironment = EnvironmentSnapshot.Protect(item.Environment)
            }
        };
        lock (sync) instances[key] = instance;
        Save(instance);
        try
        {
            var launcher = await WriteLauncherAsync(item, cwd, runFolder);
            var readyFile = Path.Combine(runFolder, "launch.ready");
            await File.WriteAllTextAsync(gateScript,
                "@echo off\r\nset /a attempts=0\r\n:wait\r\n" +
                $"if exist \"{readyFile}\" goto run\r\n" +
                "set /a attempts+=1\r\nif %attempts% GEQ 30 exit /b 1\r\n" +
                "ping -n 2 127.0.0.1 >nul\r\ngoto wait\r\n:run\r\n" +
                launcher + "\r\nset code=%errorlevel%\r\n" +
                $"echo %code% > \"{Path.Combine(runFolder, "exit.code")}\"\r\n" +
                "exit /b %code%\r\n");
            var job = WindowsRunJob.Create(instance.JobName);
            var process = WindowsRunProcess.Start(gateScript, cwd, item.Environment,
                Path.Combine(runFolder, "stdout.log"), Path.Combine(runFolder, "stderr.log"), job);
            instance.Process = process;
            if (!WindowsRunJob.IsRunning(instance.JobName))
                throw new InvalidOperationException("Windows Job Object가 실행 프로세스를 보유하지 못했습니다.");
            instance.Record = instance.Record with { StartedUtc = process.StartTime.ToUniversalTime() };
            Set(instance, RunItemStatus.WaitingReady, process.Id);
            instance.MonitorTask = MonitorAsync(instance);
            await File.WriteAllTextAsync(readyFile, "ready");
            if (item.Lifecycle == RunLifecycle.OneShot)
            {
                await instance.MonitorTask;
                instance.Ready = instance.Record.Status == RunItemStatus.Completed;
                return instance.Ready;
            }
            var ready = await WaitReadyAsync(instance);
            if (!ready)
            {
                if (IsActive(instance.Record.Status)) WindowsRunJob.Terminate(instance.JobName);
                if (instance.MonitorTask is not null) await instance.MonitorTask;
                Set(instance, RunItemStatus.Failed, reason: instance.Record.Reason ??
                    "준비 조건을 만족하지 못했습니다.");
                return false;
            }
            if (!WindowsRunJob.IsRunning(instance.JobName))
            {
                if (instance.MonitorTask is not null) await instance.MonitorTask;
                return false;
            }
            instance.Ready = true;
            Set(instance, RunItemStatus.Running);
            return true;
        }
        catch (Exception error)
        {
            try { WindowsRunJob.Terminate(instance.JobName); } catch { /* Preserve the start error. */ }
            if (instance.MonitorTask is not null)
                try { await instance.MonitorTask; } catch { /* Preserve the start error. */ }
            Set(instance, RunItemStatus.Failed, reason: error.Message);
            return false;
        }
    }

    private static async Task<string> WriteLauncherAsync(RunItem item, string cwd, string runFolder)
    {
        if (item.Kind == RunItemKind.Process)
        {
            var executable = CommandRunManager.ResolveExecutable(item.Command.Trim().Trim('"'), cwd,
                item.Environment.FirstOrDefault(entry => entry.Name.Equals("PATH",
                    StringComparison.OrdinalIgnoreCase))?.Value ?? Environment.GetEnvironmentVariable("PATH"));
            return $"call \"{executable}\" {item.Arguments}";
        }
        var script = item.ScriptSource == RunScriptSource.File
            ? Path.GetFullPath(Path.Combine(cwd, item.Script))
            : Path.Combine(runFolder, item.Shell == RunShell.PowerShell ? "inline.ps1" : "inline.cmd");
        if (item.ScriptSource == RunScriptSource.Inline)
            await File.WriteAllTextAsync(script, item.Script);
        var shell = item.ScriptSource == RunScriptSource.File
            ? Path.GetExtension(script).Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                ? RunShell.PowerShell : RunShell.Cmd
            : item.Shell;
        return shell == RunShell.PowerShell
            ? $"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{script}\" {item.Arguments}"
            : $"call \"{script}\" {item.Arguments}";
    }

    private async Task MonitorAsync(Instance instance)
    {
        var process = instance.Process!;
        await process.WaitForExitAsync();
        while (WindowsRunJob.IsRunning(instance.JobName)) await Task.Delay(250);
        var status = instance.Record.Status == RunItemStatus.Stopping
            ? RunItemStatus.Stopped
            : instance.Item.Lifecycle == RunLifecycle.LongRunning
                ? RunItemStatus.Exited
                : process.ExitCode == 0 ? RunItemStatus.Completed : RunItemStatus.Failed;
        Set(instance, status, exitCode: process.ExitCode,
            reason: status == RunItemStatus.Failed
                ? $"종료 코드 {process.ExitCode}" : instance.Record.Reason);
        WindowsRunJob.ReleaseIfEmpty(instance.JobName);
        process.Dispose();
    }

    private static async Task<bool> WaitReadyAsync(Instance instance)
    {
        var item = instance.Item;
        if (item.Readiness.Type == RunReadinessKind.Process)
        {
            await Task.Delay(300);
            if (!WindowsRunJob.IsRunning(instance.JobName))
            {
                instance.Record = instance.Record with { Reason = "프로세스가 준비 전에 종료되었습니다." };
                return false;
            }
            return true;
        }
        var port = item.Readiness.Port!.Value;
        var deadline = DateTime.UtcNow.AddSeconds(item.Readiness.TimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!WindowsRunJob.IsRunning(instance.JobName))
            {
                instance.Record = instance.Record with { Reason = "프로세스가 포트를 열기 전에 종료되었습니다." };
                return false;
            }
            var owners = ListeningPorts.Owners(port);
            if (owners.Any(pid => item.Readiness.AllowExternalPort ||
                WindowsRunJob.ContainsProcess(instance.JobName, pid))) return true;
            await Task.Delay(250);
        }
        instance.Record = instance.Record with { Reason = $"포트 {port} 준비 시간 초과" };
        return false;
    }

    public async Task StopItemAsync(string worktreePath, RunProfile profile, string itemId)
    {
        var item = profile.Items.FirstOrDefault(value => value.Id == itemId);
        if (item is null) return;
        _ = GetItemState(worktreePath, profile, item);
        var key = Key(worktreePath, profile.Id, itemId);
        Instance? instance;
        lock (sync) instances.TryGetValue(key, out instance);
        if (instance is null || !IsActive(instance.Record.Status)) return;
        await StopInstanceAsync(instance, force: false);
    }

    private async Task StopInstanceAsync(Instance instance, bool force)
    {
        if (!IsActive(instance.Record.Status)) return;
        Set(instance, RunItemStatus.Stopping);
        if (!force && instance.Record.ProcessId is int rootPid &&
            WindowsRunJob.ContainsProcess(instance.JobName, rootPid))
            ConsoleStopSignal.TrySendBreak(rootPid);
        if (!force)
            for (var attempt = 0; attempt < 32 && WindowsRunJob.IsRunning(instance.JobName); attempt++)
                await Task.Delay(250);
        if (WindowsRunJob.IsRunning(instance.JobName)) WindowsRunJob.Terminate(instance.JobName);
        for (var attempt = 0; attempt < 32 && WindowsRunJob.IsRunning(instance.JobName); attempt++)
            await Task.Delay(250);
        if (WindowsRunJob.IsRunning(instance.JobName))
            throw new IOException("프로세스 그룹이 종료되지 않았습니다.");
        if (instance.MonitorTask is not null) await instance.MonitorTask;
        else Set(instance, RunItemStatus.Stopped);
    }

    public Task KillItemAsync(string worktreePath, RunProfile profile, string itemId)
    {
        var item = profile.Items.FirstOrDefault(value => value.Id == itemId);
        if (item is null) return Task.CompletedTask;
        _ = GetItemState(worktreePath, profile, item);
        var key = Key(worktreePath, profile.Id, itemId);
        lock (sync)
            return instances.TryGetValue(key, out var instance)
                ? StopInstanceAsync(instance, force: true) : Task.CompletedTask;
    }

    public async Task StopAllAsync(string worktreePath, RunProfile profile)
    {
        await Task.WhenAll(profile.Items.Select(item => StopItemAsync(worktreePath, profile, item.Id)));
    }

    public string ReadLog(string worktreePath, RunProfile profile, RunItem item, bool stderr)
    {
        var record = GetItemState(worktreePath, profile, item);
        if (record.InstanceId is null) return "실행 로그가 없습니다.";
        var path = Path.Combine(ItemFolder(worktreePath, profile.Id, item.Id),
            record.InstanceId, stderr ? "stderr.log" : "stdout.log");
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-Math.Min(24_000, stream.Length), SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string ReadInstanceLog(RunInstanceRecord record, bool stderr)
    {
        var path = Path.Combine(ItemFolder(record.WorktreePath, record.ProfileId, record.ItemId),
            record.InstanceId, stderr ? "stderr.log" : "stdout.log");
        if (!File.Exists(path)) return "로그가 없습니다.";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-Math.Min(24_000, stream.Length), SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Set(Instance instance, RunItemStatus status, int? pid = null,
        int? exitCode = null, string? reason = null)
    {
        lock (sync)
        {
            instance.Record = instance.Record with
            {
                Status = status, ProcessId = pid ?? instance.Record.ProcessId,
                ExitCode = exitCode ?? instance.Record.ExitCode,
                EndedUtc = status is RunItemStatus.Completed or RunItemStatus.Failed or
                    RunItemStatus.Stopped or RunItemStatus.Exited ? DateTime.UtcNow : null,
                Reason = reason
            };
            Save(instance);
        }
    }

    private void Save(Instance instance)
    {
        Directory.CreateDirectory(instance.Folder);
        var runFolder = Path.Combine(instance.Folder, instance.Record.InstanceId);
        Directory.CreateDirectory(runFolder);
        var json = JsonSerializer.Serialize(instance.Record);
        File.WriteAllText(Path.Combine(runFolder, "instance.json"), json);
        File.WriteAllText(Path.Combine(instance.Folder, "latest.json"), json);
    }

    private RunInstanceRecord? ReadLatest(string worktreePath, string profileId, string itemId)
    {
        var path = Path.Combine(ItemFolder(worktreePath, profileId, itemId), "latest.json");
        return ReadRecord(path);
    }

    private static RunInstanceRecord? ReadRecord(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RunInstanceRecord>(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private string ItemFolder(string worktreePath, string profileId, string itemId) =>
        Path.Combine(stateRoot, Hash(worktreePath), Hash(profileId), Hash(itemId));

    private string Key(string worktreePath, string profileId, string itemId) =>
        $"{Hash(repositoryPath)}.{Hash(worktreePath)}.{Hash(profileId)}.{Hash(itemId)}";

    private static string JobName(string instanceId) => $"Local\\Nebb.DevManager.Profile.{instanceId}";
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..20].ToLowerInvariant();
    private static RunItemState View(RunInstanceRecord record) =>
        new(record.ItemId, record.Status, record.Reason, record.ProcessId,
            record.ExitCode, record.InstanceId);
    private static bool IsActive(RunItemStatus status) => status is
        RunItemStatus.Starting or RunItemStatus.WaitingReady or RunItemStatus.Running or
        RunItemStatus.Stopping;
}
