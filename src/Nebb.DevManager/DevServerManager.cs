using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nebb.DevManager;

internal sealed record Worktree(string Path, string Branch);
internal sealed record SessionRecord(string WorktreePath, int ProcessId, DateTime StartedUtc,
    int WebPort, int ApiPort, string DataDirectory, string LogPath);
internal sealed record ProcessInfo(int ProcessId, int ParentProcessId, string Name,
    string? CommandLine, string? ExecutablePath);
internal sealed record ListenerInfo(string LocalAddress, int LocalPort, int OwningProcess);
internal sealed record ProcessSnapshot(List<ProcessInfo> Processes, List<ListenerInfo> Listeners)
{
    public static ProcessSnapshot Empty { get; } = new([], []);
}
internal sealed record WorktreeState(string Status, int? WebPort, int? ApiPort,
    bool Managed, bool HasProcesses, string? LogPath);

internal sealed class PixPeekPortConflictException(int processId, string executablePath, DateTime startedUtc)
    : InvalidOperationException($"7878 포트를 다른 PixPeek 서버가 사용 중입니다 (PID {processId}).")
{
    public int ProcessId { get; } = processId;
    public string ExecutablePath { get; } = executablePath;
    public DateTime StartedUtc { get; } = startedUtc;
}

internal sealed class DevServerManager
{
    public const int PwaPort = 7878;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string repositoryPath;
    private readonly string stateDirectory;

    public DevServerManager(string repositoryPath)
    {
        this.repositoryPath = Path.GetFullPath(repositoryPath);
        stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixPeek", "DevManager");
    }

    public string RepositoryPath => repositoryPath;

    public async Task<List<Worktree>> ListWorktreesAsync()
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("worktree");
        start.ArgumentList.Add("list");
        start.ArgumentList.Add("--porcelain");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git을 실행할 수 없습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());

        var result = new List<Worktree>();
        string? path = null;
        string? branch = null;
        foreach (var line in (output + "\n").Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.StartsWith("worktree ", StringComparison.Ordinal))
                path = value["worktree ".Length..];
            else if (value.StartsWith("branch refs/heads/", StringComparison.Ordinal))
                branch = value["branch refs/heads/".Length..];
            else if (value == "detached")
                branch = "(detached)";
            else if (value.Length == 0 && path is not null)
            {
                result.Add(new Worktree(Path.GetFullPath(path), branch ?? "(unknown)"));
                path = null;
                branch = null;
            }
        }
        return result;
    }

    public async Task<ProcessSnapshot> InspectAsync()
    {
        // The script is constant: worktree paths are matched in .NET, never interpolated into PowerShell.
        const string script = """
            $procs = @(Get-CimInstance Win32_Process | Where-Object {
              $_.Name -in @('node.exe', 'PixPeek.Server.exe') -and
              ($_.CommandLine -match 'vite[\\/]bin[\\/]vite|PixPeek.Server|concurrently[\\/]dist[\\/]bin[\\/]concurrently')
            } | Select-Object ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath)
            $ports = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
              Select-Object LocalAddress, LocalPort, OwningProcess)
            @{ Processes = $procs; Listeners = $ports } | ConvertTo-Json -Depth 4 -Compress
            """;
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("PowerShell을 실행할 수 없습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        var result = JsonSerializer.Deserialize<ProcessSnapshot>(output, JsonOptions);
        return result ?? ProcessSnapshot.Empty;
    }

    public WorktreeState GetState(Worktree worktree, ProcessSnapshot snapshot)
    {
        var (web, api, runner) = FindProcesses(worktree, snapshot);
        var session = ReadSession(worktree);
        var managed = session is not null && IsSessionAlive(session);
        var webPort = PortFor(web, snapshot) ?? PortFor(api, snapshot) ??
            (managed ? session!.WebPort : null);
        var apiPort = PortFor(api, snapshot) ?? (managed ? session!.ApiPort : null);
        var hasProcesses = web is not null || api is not null || runner is not null || managed;
        var status = api is not null
            ? managed ? "실행 중" : "외부 실행"
            : managed ? "시작 중" : hasProcesses ? "일부 실행" : "중지";
        return new WorktreeState(status, webPort, apiPort, managed, hasProcesses,
            session?.LogPath);
    }

    public async Task StartAsync(Worktree worktree, ProcessSnapshot snapshot)
    {
        var worktrees = await ListWorktreesAsync();
        var pixPeekProcesses = worktrees.SelectMany(item => WorktreeProcesses(item, snapshot))
            .Select(process => process.ProcessId).ToHashSet();
        ThrowIfUnexpectedPortOwner(snapshot, pixPeekProcesses);

        var worktreeId = IdFor(worktree.Path);
        var runDirectory = Path.Combine(stateDirectory, "Runs", worktreeId);
        var mainWorktree = worktrees.FirstOrDefault(item => item.Branch == "main") ??
            throw new InvalidOperationException("main 워크트리를 찾을 수 없습니다.");
        var dataDirectory = Path.Combine(mainWorktree.Path, ".pixpeek-m1-data");
        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(dataDirectory);
        var logPath = Path.Combine(runDirectory, "server.log");
        var buildScriptPath = Path.Combine(runDirectory, "build.cmd");
        var scriptPath = Path.Combine(runDirectory, "run.cmd");
        await File.WriteAllTextAsync(logPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {worktree.Branch} ({worktree.Path}) 시작\n");
        await File.WriteAllTextAsync(buildScriptPath,
            "@echo off\r\n" +
            "if not exist \"node_modules\\.bin\\tsc.cmd\" goto install\r\n" +
            "if not exist \"node_modules\\.bin\\vite.cmd\" goto install\r\n" +
            "goto build\r\n" +
            ":install\r\n" +
            $"call npm.cmd ci >> \"{logPath}\" 2>&1\r\n" +
            "if errorlevel 1 exit /b 1\r\n" +
            ":build\r\n" +
            $"call npm.cmd run build >> \"{logPath}\" 2>&1\r\n");

        using (var build = StartScript(worktree.Path, buildScriptPath))
        {
            await build.WaitForExitAsync();
            if (build.ExitCode != 0)
                throw new InvalidOperationException($"PWA 빌드에 실패했습니다. 로그를 확인하세요: {logPath}");
        }

        snapshot = await InspectAsync();
        pixPeekProcesses = worktrees.SelectMany(item => WorktreeProcesses(item, snapshot))
            .Select(process => process.ProcessId).ToHashSet();
        ThrowIfUnexpectedPortOwner(snapshot, pixPeekProcesses);
        foreach (var item in worktrees.Where(item => GetState(item, snapshot).HasProcesses))
            await StopAsync(item);

        ThrowIfUnexpectedPortOwner(await InspectAsync(), []);

        await File.WriteAllTextAsync(scriptPath,
            "@echo off\r\n" +
            $"call npm.cmd run start >> \"{logPath}\" 2>&1\r\n");

        var start = ScriptStartInfo(worktree.Path, scriptPath);
        start.Environment["PIXPEEK_PORT"] = PwaPort.ToString();
        start.Environment["PIXPEEK_FULL"] = "1";
        start.Environment["PIXPEEK_DATA_DIR"] = dataDirectory;
        start.Environment["PIXPEEK_WEB_DIR"] = Path.Combine(worktree.Path, "dist");
        start.Environment.Remove("PIXPEEK_LISTEN_URL");
        start.Environment.Remove("PIXPEEK_TEAM_MODE");
        start.Environment.Remove("PIXPEEK_ROOT");
        var process = Process.Start(start) ?? throw new InvalidOperationException("개발 서버를 시작할 수 없습니다.");
        var session = new SessionRecord(worktree.Path, process.Id, process.StartTime.ToUniversalTime(),
            PwaPort, PwaPort, dataDirectory, logPath);
        await File.WriteAllTextAsync(SessionPath(worktree), JsonSerializer.Serialize(session, JsonOptions));
        try
        {
            await WaitUntilReadyAsync(process);
        }
        catch (Exception error)
        {
            if (!process.HasExited) await KillAsync(process.Id);
            var sessionPath = SessionPath(worktree);
            if (File.Exists(sessionPath)) File.Delete(sessionPath);
            throw new InvalidOperationException($"Full 개발 서버가 준비되지 않았습니다: {error.Message} 로그: {logPath}", error);
        }
        finally { process.Dispose(); }
    }

    public async Task RebuildAndRestartAsync(Worktree worktree, ProcessSnapshot snapshot)
    {
        if (GetState(worktree, snapshot).Status is not ("실행 중" or "외부 실행"))
            throw new InvalidOperationException("이 워크트리의 서버가 실행 중이 아닙니다. 실행/전환을 사용하세요.");
        await StartAsync(worktree, snapshot);
    }

    public async Task TerminateConflictingPixPeekAsync(PixPeekPortConflictException conflict)
    {
        var snapshot = await InspectAsync();
        var listener = snapshot.Listeners.FirstOrDefault(item => item.LocalPort == PwaPort);
        if (listener is null) return;
        var owner = snapshot.Processes.FirstOrDefault(item => item.ProcessId == listener.OwningProcess);
        if (listener.OwningProcess != conflict.ProcessId || !IsPixPeekServer(owner) ||
            !string.Equals(owner!.ExecutablePath, conflict.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("7878 포트 점유 프로세스가 바뀌었습니다. 새로고침 후 다시 시도하세요.");

        using var process = Process.GetProcessById(conflict.ProcessId);
        if (process.StartTime.ToUniversalTime() != conflict.StartedUtc)
            throw new InvalidOperationException("7878 포트 점유 프로세스가 바뀌었습니다. 새로고침 후 다시 시도하세요.");
        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await process.WaitForExitAsync(timeout.Token);

        if ((await InspectAsync()).Listeners.Any(item => item.LocalPort == PwaPort))
            throw new InvalidOperationException("7878 포트가 계속 사용 중입니다. 새로고침 후 점유 프로세스를 확인하세요.");
    }

    public async Task StopAsync(Worktree worktree)
    {
        var session = ReadSession(worktree);
        var managed = session is not null && IsSessionAlive(session);
        var snapshot = await InspectAsync();
        var processes = WorktreeProcesses(worktree, snapshot).ToArray();
        if (!managed && processes.Length == 0)
            throw new InvalidOperationException("이 워크트리에 실행 중인 PixPeek 서버가 없습니다.");
        if (managed) await KillAsync(session!.ProcessId);

        // The web/API processes can outlive their npm or dotnet launcher.
        // Match their executable paths to this worktree before stopping them.
        snapshot = await InspectAsync();
        processes = WorktreeProcesses(worktree, snapshot).ToArray();
        foreach (var process in processes.OrderByDescending(item =>
                     item.CommandLine?.Contains("concurrently", StringComparison.OrdinalIgnoreCase) == true))
            await KillAsync(process.ProcessId);

        var file = SessionPath(worktree);
        if (File.Exists(file)) File.Delete(file);

        if (WorktreeProcesses(worktree, await InspectAsync()).Any())
            throw new InvalidOperationException("일부 PixPeek 서버 프로세스가 남아 있습니다. 새로고침 후 다시 종료하세요.");
    }

    public string ReadRecentLog(Worktree worktree)
    {
        var path = ReadSession(worktree)?.LogPath ??
            Path.Combine(stateDirectory, "Runs", IdFor(worktree.Path), "server.log");
        if (!File.Exists(path)) return "관리 앱에서 시작한 서버의 로그가 없습니다.";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-Math.Min(24_000, stream.Length), SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return "로그를 읽는 중입니다."; }
    }

    private static (ProcessInfo? Web, ProcessInfo? Api, ProcessInfo? Runner) FindProcesses(
        Worktree worktree, ProcessSnapshot snapshot)
    {
        var processes = WorktreeProcesses(worktree, snapshot).ToArray();
        var web = processes.FirstOrDefault(p =>
            p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) &&
            p.CommandLine?.Contains("vite", StringComparison.OrdinalIgnoreCase) == true);
        var api = processes.FirstOrDefault(p =>
            p.Name.Equals("PixPeek.Server.exe", StringComparison.OrdinalIgnoreCase));
        var runner = processes.FirstOrDefault(p =>
            p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) &&
            p.CommandLine?.Contains("concurrently", StringComparison.OrdinalIgnoreCase) == true);
        return (web, api, runner);
    }

    private static IEnumerable<ProcessInfo> WorktreeProcesses(Worktree worktree, ProcessSnapshot snapshot)
    {
        var nodeModules = Path.Combine(worktree.Path, "node_modules");
        var serverBin = Path.Combine(worktree.Path, "src", "PixPeek.Server", "bin");
        return snapshot.Processes.Where(p =>
            (p.Name.Equals("PixPeek.Server.exe", StringComparison.OrdinalIgnoreCase) &&
             p.CommandLine?.Contains(serverBin, StringComparison.OrdinalIgnoreCase) == true) ||
            (p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) &&
             p.CommandLine?.Contains(nodeModules, StringComparison.OrdinalIgnoreCase) == true &&
             (p.CommandLine.Contains("vite", StringComparison.OrdinalIgnoreCase) ||
              p.CommandLine.Contains("concurrently", StringComparison.OrdinalIgnoreCase))));
    }

    private static int? PortFor(ProcessInfo? process, ProcessSnapshot snapshot) =>
        process is null ? null : snapshot.Listeners.FirstOrDefault(p =>
            p.OwningProcess == process.ProcessId &&
            p.LocalAddress is "127.0.0.1" or "::1")?.LocalPort;

    private static void ThrowIfUnexpectedPortOwner(ProcessSnapshot snapshot, HashSet<int> knownProcesses)
    {
        var listener = snapshot.Listeners.FirstOrDefault(item =>
            item.LocalPort == PwaPort && !knownProcesses.Contains(item.OwningProcess));
        if (listener is null) return;
        var owner = snapshot.Processes.FirstOrDefault(item => item.ProcessId == listener.OwningProcess);
        if (IsPixPeekServer(owner))
        {
            using var process = Process.GetProcessById(owner!.ProcessId);
            throw new PixPeekPortConflictException(owner.ProcessId, owner.ExecutablePath!,
                process.StartTime.ToUniversalTime());
        }
        throw new InvalidOperationException($"{PwaPort} 포트를 PixPeek 외의 프로세스가 사용 중입니다. 해당 프로그램을 확인하세요.");
    }

    private static bool IsPixPeekServer(ProcessInfo? process) =>
        process is { Name: "PixPeek.Server.exe", ExecutablePath: not null } &&
        process.ExecutablePath.Contains(
            $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}PixPeek.Server{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(process.ExecutablePath).Equals("PixPeek.Server.exe", StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo ScriptStartInfo(string worktreePath, string scriptPath)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(scriptPath);
        return start;
    }

    private static Process StartScript(string worktreePath, string scriptPath) =>
        Process.Start(ScriptStartInfo(worktreePath, scriptPath)) ??
        throw new InvalidOperationException("빌드 프로세스를 시작할 수 없습니다.");

    private static async Task WaitUntilReadyAsync(Process process)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 120 && !process.HasExited; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(ChromePwaLauncher.Address + "api/auth/me");
                if (response.IsSuccessStatusCode)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (body.RootElement.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True)
                        return;
                    throw new InvalidOperationException("선택한 워크트리의 서버가 Full 실행을 지원하지 않습니다.");
                }
            }
            catch (HttpRequestException) { /* Server is still starting. */ }
            catch (TaskCanceledException) { /* Retry a slow startup. */ }
            await Task.Delay(250);
        }
        throw new InvalidOperationException("PWA 서버가 준비되지 않았습니다.");
    }

    private static async Task KillAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { /* Already exited. */ }
        catch (InvalidOperationException) { /* Already exited. */ }
        catch (OperationCanceledException) { /* Refresh will show any remaining process. */ }
    }

    private SessionRecord? ReadSession(Worktree worktree)
    {
        var file = SessionPath(worktree);
        if (!File.Exists(file)) return null;
        try
        {
            var session = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(file), JsonOptions);
            return session is not null &&
                Path.GetFullPath(session.WorktreePath).Equals(worktree.Path, StringComparison.OrdinalIgnoreCase)
                ? session : null;
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private static bool IsSessionAlive(SessionRecord session)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            return !process.HasExited &&
                Math.Abs((process.StartTime.ToUniversalTime() - session.StartedUtc).TotalSeconds) < 2;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private string SessionPath(Worktree worktree) =>
        Path.Combine(stateDirectory, "Runs", IdFor(worktree.Path), "session.json");

    private static string IdFor(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

}
