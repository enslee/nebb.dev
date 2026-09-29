using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nebb.DevManager;

internal sealed record CommandRunSession(
    string WorktreePath, int ProcessId, DateTime StartedUtc, string Name);

internal sealed record CommandRunState(string Status, bool IsRunning, string? Name);

internal sealed class CommandRunManager
{
    private readonly string stateDirectory;

    public CommandRunManager(string repositoryPath, string? storageDirectory = null)
    {
        var root = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nebb", "DevManager", "command-runs");
        stateDirectory = Path.Combine(root, IdFor(repositoryPath));
    }

    public CommandRunState GetState(string worktreePath)
    {
        var session = ReadSession(worktreePath);
        var running = WindowsRunJob.IsRunning(JobName(worktreePath)) ||
                      session is not null && IsAlive(session);
        return new CommandRunState(running ? "실행 중" : session is null ? "중지" : "종료됨",
            running, session?.Name);
    }

    public async Task SwitchAsync(string worktreePath, SavedCommand command)
    {
        var environment = string.Join('\n', command.Environment.Select(item =>
            $"{item.Name}={item.Value}"));
        CommandSettingsValidator.Validate(command, worktreePath, environment);

        Directory.CreateDirectory(stateDirectory);
        foreach (var file in Directory.GetFiles(stateDirectory, "*.json"))
        {
            var session = ReadSessionFile(file);
            if (session is not null && GetState(session.WorktreePath).IsRunning)
                await StopAsync(session.WorktreePath);
        }
        if (WindowsRunJob.IsRunning(JobName(worktreePath))) await StopAsync(worktreePath);

        var runDirectory = RunDirectory(worktreePath);
        Directory.CreateDirectory(runDirectory);
        var logPath = Path.Combine(runDirectory, "run.log");
        var scriptPath = Path.Combine(runDirectory, "run.cmd");
        var readyPath = Path.Combine(runDirectory, "run.ready");
        var folder = Path.GetFullPath(Path.Combine(worktreePath, command.WorkingDirectory));
        var executable = ResolveExecutable(command.Command.Trim().Trim('"'), folder,
            command.Environment.FirstOrDefault(item => item.Name.Equals("PATH",
                StringComparison.OrdinalIgnoreCase))?.Value ?? Environment.GetEnvironmentVariable("PATH"));
        await File.WriteAllTextAsync(logPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {command.Name} ({worktreePath}) 시작{Environment.NewLine}");
        if (File.Exists(readyPath)) File.Delete(readyPath);
        // Wait until the launcher belongs to the job before it can spawn npm or other children.
        await File.WriteAllTextAsync(scriptPath,
            "@echo off\r\n" +
            "set /a attempts=0\r\n" +
            ":wait\r\n" +
            $"if exist \"{readyPath}\" goto run\r\n" +
            "set /a attempts+=1\r\n" +
            "if %attempts% GEQ 30 exit /b 1\r\n" +
            "ping -n 2 127.0.0.1 >nul\r\n" +
            "goto wait\r\n" +
            ":run\r\n" +
            $"call \"{executable}\" {command.Arguments} >> \"{logPath}\" 2>&1\r\n" +
            "exit /b %errorlevel%\r\n");

        var start = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(scriptPath);
        foreach (var entry in command.Environment) start.Environment[entry.Name] = entry.Value;
        var job = WindowsRunJob.Create(JobName(worktreePath));
        Process started;
        try
        {
            started = Process.Start(start) ??
                throw new InvalidOperationException("설정한 명령을 시작할 수 없습니다.");
        }
        catch
        {
            WindowsRunJob.Terminate(JobName(worktreePath));
            throw;
        }
        using var process = started;
        try
        {
            WindowsRunJob.Assign(job, process);
            if (!WindowsRunJob.IsRunning(JobName(worktreePath)))
                throw new InvalidOperationException("실행 프로세스 그룹을 다시 열 수 없습니다.");
            var session = new CommandRunSession(Path.GetFullPath(worktreePath), process.Id,
                process.StartTime.ToUniversalTime(), command.Name);
            var temporary = SessionPath(worktreePath) + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(session));
            File.Move(temporary, SessionPath(worktreePath), true);
            await File.WriteAllTextAsync(readyPath, "ready");
        }
        catch
        {
            try { WindowsRunJob.Terminate(JobName(worktreePath)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            throw;
        }
        await Task.Delay(300);
        if (process.HasExited && process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{command.Name} 명령이 종료 코드 {process.ExitCode}로 끝났습니다. 실행 로그를 확인하세요.");
    }

    public async Task StopAsync(string worktreePath)
    {
        var session = ReadSession(worktreePath);
        if (!WindowsRunJob.Terminate(JobName(worktreePath)))
        {
            if (session is null || !IsAlive(session))
                throw new InvalidOperationException("이 워크트리에서 Dev Manager가 실행한 명령이 없습니다.");
            using var process = Process.GetProcessById(session.ProcessId);
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
        }
        await WaitForLogReleaseAsync(Path.Combine(RunDirectory(worktreePath), "run.log"));
        if (File.Exists(SessionPath(worktreePath))) File.Delete(SessionPath(worktreePath));
    }

    public string ReadRecentLog(string worktreePath)
    {
        var path = Path.Combine(RunDirectory(worktreePath), "run.log");
        if (!File.Exists(path)) return "Dev Manager에서 시작한 명령의 로그가 없습니다.";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-Math.Min(24_000, stream.Length), SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return "로그를 읽는 중입니다."; }
    }

    private CommandRunSession? ReadSession(string worktreePath)
    {
        var session = ReadSessionFile(SessionPath(worktreePath));
        return session is not null && Path.GetFullPath(session.WorktreePath).Equals(
            Path.GetFullPath(worktreePath), StringComparison.OrdinalIgnoreCase) ? session : null;
    }

    private static CommandRunSession? ReadSessionFile(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CommandRunSession>(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private static bool IsAlive(CommandRunSession session)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            return !process.HasExited &&
                Math.Abs((process.StartTime.ToUniversalTime() - session.StartedUtc).TotalSeconds) < 2;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private string RunDirectory(string worktreePath) =>
        Path.Combine(stateDirectory, IdFor(worktreePath));

    private string SessionPath(string worktreePath) =>
        Path.Combine(stateDirectory, $"{IdFor(worktreePath)}.json");

    internal static string JobName(string worktreePath) =>
        $"Local\\Nebb.DevManager.Run.{IdFor(worktreePath)}";

    private static async Task WaitForLogReleaseAsync(string logPath)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None);
                return;
            }
            catch (FileNotFoundException) { return; }
            catch (IOException) { await Task.Delay(100); }
        }
        throw new IOException("명령의 하위 프로세스가 아직 실행 로그를 사용 중입니다. 잠시 후 다시 확인하세요.");
    }

    internal static string ResolveExecutable(string command, string workingDirectory, string? searchPath)
    {
        var commandExtension = Path.GetExtension(command);
        if (commandExtension.Length > 0 &&
            !commandExtension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !commandExtension.Equals(".bat", StringComparison.OrdinalIgnoreCase)) return command;
        var locations = Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) ||
                        command.Contains(Path.AltDirectorySeparatorChar)
            ? new[] { workingDirectory }
            : new[] { workingDirectory }.Concat((searchPath ?? "").Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim('"')));
        var extensions = commandExtension.Length > 0
            ? new[] { "" }
            : new[] { ".exe", ".cmd", ".bat", ".com" };
        foreach (var location in locations)
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(location, command + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        return command;
    }

    private static string IdFor(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..20]
        .ToLowerInvariant();
}
