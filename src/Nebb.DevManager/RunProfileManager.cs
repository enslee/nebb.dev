using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nebb.DevManager;

internal enum RunItemStatus
{
    Idle, Starting, WaitingReady, Running, Completed, Failed, Stopping, Stopped
}

internal enum RunProfileStatus
{
    Stopped, Starting, Running, PartiallyRunning, Failed, Stopping
}

internal sealed record RunItemState(string ItemId, RunItemStatus Status, string? Reason,
    int? ProcessId, int? ExitCode, string? InstanceId);

internal sealed record RunInstanceRecord(string InstanceId, string WorktreePath,
    string ProfileId, string ItemId, int? ProcessId, DateTime StartedUtc,
    DateTime? EndedUtc, RunItemStatus Status, int? ExitCode, string? Reason);

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
        public Task StdoutTask { get; set; } = Task.CompletedTask;
        public Task StderrTask { get; set; } = Task.CompletedTask;
        public Task? MonitorTask { get; set; }
        public bool Ready { get; set; }
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
            if (instances.TryGetValue(key, out var active)) return View(active.Record);
        var record = ReadLatest(worktreePath, profile.Id, item.Id);
        if (record is null) return new(item.Id, RunItemStatus.Idle, null, null, null, null);
        if (record.Status is RunItemStatus.Starting or RunItemStatus.WaitingReady or
            RunItemStatus.Running or RunItemStatus.Stopping)
            record = record with { Status = RunItemStatus.Stopped,
                Reason = "이전 앱 실행이 종료되었습니다." };
        return View(record);
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

    public void ValidateSettingsTransition(RepositoryCommandSettings settings)
    {
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
        await StopAllAsync(worktreePath, profile);
        await RunAllAsync(worktreePath, profile);
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
        IEnumerable<RunItem> targets)
    {
        var targetIds = targets.Select(item => item.Id).ToHashSet();
        var byId = profile.Items.ToDictionary(item => item.Id);
        var tasks = new Dictionary<string, Task<bool>>();
        Task<bool> Start(RunItem item)
        {
            if (tasks.TryGetValue(item.Id, out var current)) return current;
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
            DateTime.UtcNow, RunItemStatus.Idle, null, "선행 항목이 준비되지 않아 시작하지 않았습니다.");
        var instance = new Instance { Key = key, JobName = JobName(key), Folder = folder,
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
        var instance = new Instance
        {
            Key = key, JobName = JobName(key), Folder = folder, Item = item,
            ProfileName = profile.Name,
            Record = new(instanceId, Path.GetFullPath(worktreePath), profile.Id, item.Id,
                null, DateTime.UtcNow, null, RunItemStatus.Starting, null, null)
        };
        lock (sync) instances[key] = instance;
        Save(instance);
        try
        {
            var cwd = Path.GetFullPath(Path.Combine(worktreePath, item.WorkingDirectory));
            var launcher = await WriteLauncherAsync(item, cwd, runFolder);
            var readyFile = Path.Combine(runFolder, "launch.ready");
            var gateScript = Path.Combine(runFolder, "gate.cmd");
            await File.WriteAllTextAsync(gateScript,
                "@echo off\r\nset /a attempts=0\r\n:wait\r\n" +
                $"if exist \"{readyFile}\" goto run\r\n" +
                "set /a attempts+=1\r\nif %attempts% GEQ 30 exit /b 1\r\n" +
                "ping -n 2 127.0.0.1 >nul\r\ngoto wait\r\n:run\r\n" +
                launcher + "\r\nexit /b %errorlevel%\r\n");
            var start = new ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(gateScript);
            foreach (var entry in item.Environment) start.Environment[entry.Name] = entry.Value;
            var job = WindowsRunJob.Create(instance.JobName);
            var process = Process.Start(start) ?? throw new InvalidOperationException("프로세스를 시작할 수 없습니다.");
            instance.Process = process;
            WindowsRunJob.Assign(job, process);
            if (!WindowsRunJob.IsRunning(instance.JobName))
                throw new InvalidOperationException("Windows Job Object가 실행 프로세스를 보유하지 못했습니다.");
            Set(instance, RunItemStatus.WaitingReady, process.Id);
            instance.StdoutTask = PumpAsync(process.StandardOutput, Path.Combine(runFolder, "stdout.log"));
            instance.StderrTask = PumpAsync(process.StandardError, Path.Combine(runFolder, "stderr.log"));
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
            if (instance.Process?.HasExited == true)
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

    private static async Task PumpAsync(StreamReader reader, string path)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite, 4096, useAsync: true);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
            await writer.WriteLineAsync(line);
    }

    private async Task MonitorAsync(Instance instance)
    {
        var process = instance.Process!;
        await process.WaitForExitAsync();
        await Task.WhenAll(instance.StdoutTask, instance.StderrTask);
        var status = instance.Record.Status == RunItemStatus.Stopping
            ? RunItemStatus.Stopped
            : instance.Item.Lifecycle == RunLifecycle.OneShot && process.ExitCode == 0
                ? RunItemStatus.Completed
                : process.ExitCode == 0 && instance.Ready
                    ? RunItemStatus.Stopped : RunItemStatus.Failed;
        Set(instance, status, exitCode: process.ExitCode,
            reason: status == RunItemStatus.Failed
                ? $"종료 코드 {process.ExitCode}" : instance.Record.Reason);
    }

    private static async Task<bool> WaitReadyAsync(Instance instance)
    {
        var item = instance.Item;
        if (item.Readiness.Type == RunReadinessKind.Process)
        {
            await Task.Delay(300);
            if (instance.Process!.HasExited)
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
            if (instance.Process!.HasExited)
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
        var key = Key(worktreePath, profile.Id, itemId);
        Instance? instance;
        lock (sync) instances.TryGetValue(key, out instance);
        if (instance is null || !IsActive(instance.Record.Status)) return;
        Set(instance, RunItemStatus.Stopping);
        WindowsRunJob.Terminate(instance.JobName);
        if (instance.MonitorTask is not null) await instance.MonitorTask;
        else Set(instance, RunItemStatus.Stopped);
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
                    RunItemStatus.Stopped ? DateTime.UtcNow : null,
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
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RunInstanceRecord>(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private string ItemFolder(string worktreePath, string profileId, string itemId) =>
        Path.Combine(stateRoot, Hash(worktreePath), Hash(profileId), Hash(itemId));

    private string Key(string worktreePath, string profileId, string itemId) =>
        $"{Hash(repositoryPath)}.{Hash(worktreePath)}.{Hash(profileId)}.{Hash(itemId)}";

    private static string JobName(string key) => $"Local\\Nebb.DevManager.Profile.{key}";
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..20].ToLowerInvariant();
    private static RunItemState View(RunInstanceRecord record) =>
        new(record.ItemId, record.Status, record.Reason, record.ProcessId,
            record.ExitCode, record.InstanceId);
    private static bool IsActive(RunItemStatus status) => status is
        RunItemStatus.Starting or RunItemStatus.WaitingReady or RunItemStatus.Running or
        RunItemStatus.Stopping;
}
