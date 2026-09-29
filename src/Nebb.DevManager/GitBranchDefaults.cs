using System.Diagnostics;
using System.IO;

namespace Nebb.DevManager;

internal static class GitBranchDefaults
{
    public static string Detect(string repositoryPath)
    {
        if (!Directory.Exists(repositoryPath)) return "main";
        try
        {
            return DetectAvailable(repositoryPath);
        }
        catch (Exception error) when (error is InvalidOperationException or
                     System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return "main";
        }
    }

    private static string DetectAvailable(string repositoryPath)
    {
        var remoteHead = RunGit(repositoryPath, "symbolic-ref", "--quiet", "--short",
            "refs/remotes/origin/HEAD");
        if (remoteHead is not null && remoteHead.StartsWith("origin/", StringComparison.Ordinal))
            return remoteHead["origin/".Length..];

        foreach (var candidate in new[] { "main", "master" })
            if (RunGit(repositoryPath, "show-ref", "--verify", "--quiet",
                    $"refs/remotes/origin/{candidate}") is not null ||
                RunGit(repositoryPath, "show-ref", "--verify", "--quiet",
                    $"refs/heads/{candidate}") is not null)
                return candidate;

        return "main";
    }

    private static string? RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git을 실행할 수 없습니다.");
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : null;
    }
}
