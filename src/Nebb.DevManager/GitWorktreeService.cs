using System.Diagnostics;
using System.IO;

namespace Nebb.DevManager;

internal sealed record Worktree(string Path, string Branch);

internal sealed class GitWorktreeService(string repositoryPath)
{
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
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Git을 실행할 수 없습니다.");
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
            else if (value == "detached") branch = "(detached)";
            else if (value.Length == 0 && path is not null)
            {
                result.Add(new Worktree(Path.GetFullPath(path), branch ?? "(unknown)"));
                path = null;
                branch = null;
            }
        }
        return result;
    }
}
