using System.Diagnostics;

namespace Nebb.DevManager;

internal sealed class GitActionService(string repositoryPath, string baseBranch)
{
    private sealed record GitResult(int ExitCode, string Output, string Error)
    {
        public string Message => string.IsNullOrWhiteSpace(Error) ? Output.Trim() : Error.Trim();
    }

    public async Task<string> GetPendingChangesAsync(Worktree worktree) =>
        (await RunRequiredAsync(worktree.Path, "status", "--porcelain=v1", "--untracked-files=all")).Output;

    public async Task CommitAsync(Worktree worktree, string expectedChanges, string message)
    {
        ValidateWorkBranch(worktree);
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("커밋 메시지를 입력하세요.");
        await EnsureCheckedOutAsync(worktree);
        var currentChanges = await GetPendingChangesAsync(worktree);
        if (currentChanges != expectedChanges)
            throw new InvalidOperationException("확인하는 동안 변경 파일 목록이 달라졌습니다. 다시 확인하고 실행하세요.");
        if (currentChanges.Length == 0)
            throw new InvalidOperationException("커밋할 변경 사항이 없습니다.");

        await RunRequiredAsync(worktree.Path, "add", "--all");
        await RunRequiredAsync(worktree.Path, "commit", "-m", message.Trim());
    }

    public async Task PushAsync(Worktree worktree)
    {
        ValidateBranch(worktree);
        await EnsureCheckedOutAsync(worktree);
        await EnsureCleanAsync(worktree);
        await RunRequiredAsync(repositoryPath, "fetch", "--prune", "origin");
        await PushBranchAsync(worktree);
    }

    public async Task EnsureMergeReadyAsync(Worktree worktree, Worktree baseWorktree)
    {
        ValidateWorkBranch(worktree);
        if (baseWorktree.Branch != baseBranch)
            throw new InvalidOperationException($"{baseBranch} 워크트리를 찾을 수 없습니다.");
        await EnsureCheckedOutAsync(worktree);
        await EnsureBaseReadyAsync(baseWorktree);
        await RunRequiredAsync(repositoryPath, "fetch", "--prune", "origin");
        var remoteBase = await RunAsync(repositoryPath, "show-ref", "--verify", "--quiet",
            $"refs/remotes/origin/{baseBranch}");
        if (remoteBase.ExitCode != 0)
            throw new InvalidOperationException($"origin/{baseBranch} 브랜치를 찾을 수 없습니다.");
    }

    public async Task MergeIntoBaseAsync(Worktree worktree, Worktree baseWorktree)
    {
        await EnsureMergeReadyAsync(worktree, baseWorktree);
        await EnsureCleanAsync(worktree);

        var remoteBranch = $"refs/remotes/origin/{worktree.Branch}";
        var remoteExists = await RunAsync(worktree.Path, "show-ref", "--verify", "--quiet", remoteBranch);
        if (remoteExists.ExitCode == 0 && !await IsAncestorAsync(worktree.Path, remoteBranch, "HEAD"))
            throw new InvalidOperationException("원격 작업 브랜치에 로컬에 없는 커밋이 있습니다. 먼저 차이를 확인하세요.");
        if (remoteExists.ExitCode != 0 && remoteExists.ExitCode != 1)
            throw new InvalidOperationException(remoteExists.Message);

        await MergeIntoBranchAsync(worktree, $"refs/remotes/origin/{baseBranch}");
        await MergeIntoBranchAsync(worktree, $"refs/heads/{baseBranch}");
        await EnsureBaseReadyAsync(baseWorktree);

        await PushBranchAsync(worktree);
        await EnsureBaseReadyAsync(baseWorktree);
        try
        {
            await RunRequiredAsync(baseWorktree.Path, "merge", "--ff-only", worktree.Branch);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"작업 브랜치는 푸시됐지만 로컬 {baseBranch} 병합에 실패했습니다. {error.Message}", error);
        }
        try
        {
            await RunRequiredAsync(baseWorktree.Path, "push", "origin",
                $"{baseBranch}:refs/heads/{baseBranch}");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"로컬 {baseBranch} 병합은 완료됐지만 origin/{baseBranch} 푸시에 실패했습니다. {error.Message}", error);
        }
    }

    private static async Task MergeIntoBranchAsync(Worktree worktree, string reference)
    {
        if (await IsAncestorAsync(worktree.Path, reference, "HEAD")) return;
        var result = await RunAsync(worktree.Path, "merge", "--ff", "--no-edit", reference);
        if (result.ExitCode == 0) return;
        var mergeHead = await RunAsync(worktree.Path, "rev-parse", "--verify", "-q", "MERGE_HEAD");
        if (mergeHead.ExitCode == 0)
        {
            var abort = await RunAsync(worktree.Path, "merge", "--abort");
            if (abort.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{reference} 병합에 실패했고 충돌 정리도 실패했습니다. 워크트리를 확인하세요. {abort.Message}");
        }
        throw new InvalidOperationException(
            $"{reference}를 작업 브랜치에 반영하지 못했습니다. 충돌을 확인하세요. {result.Message}");
    }

    private static async Task<bool> IsAncestorAsync(string directory, string ancestor, string descendant)
    {
        var result = await RunAsync(directory, "merge-base", "--is-ancestor", ancestor, descendant);
        if (result.ExitCode is 0 or 1) return result.ExitCode == 0;
        throw new InvalidOperationException(result.Message);
    }

    private static async Task PushBranchAsync(Worktree worktree)
    {
        var branch = worktree.Branch;
        await RunRequiredAsync(worktree.Path, "push", "--set-upstream", "origin",
            $"HEAD:refs/heads/{branch}");
    }

    private static async Task EnsureBaseReadyAsync(Worktree baseWorktree)
    {
        await EnsureCheckedOutAsync(baseWorktree);
        await EnsureCleanAsync(baseWorktree);
    }

    private static async Task EnsureCheckedOutAsync(Worktree worktree)
    {
        var branch = (await RunRequiredAsync(worktree.Path, "symbolic-ref", "--quiet", "--short", "HEAD"))
            .Output.Trim();
        if (branch != worktree.Branch)
            throw new InvalidOperationException("선택한 워크트리의 브랜치가 바뀌었습니다. 새로고침 후 다시 실행하세요.");
    }

    private static async Task EnsureCleanAsync(Worktree worktree)
    {
        if ((await RunRequiredAsync(worktree.Path, "status", "--porcelain=v1", "--untracked-files=all"))
            .Output.Length != 0)
            throw new InvalidOperationException($"{worktree.Branch} 워크트리에 미커밋 변경이 있습니다.");
    }

    private static void ValidateBranch(Worktree worktree)
    {
        if (worktree.Branch is "(detached)" or "(unknown)")
            throw new InvalidOperationException("브랜치가 연결되지 않은 워크트리에서는 Git 메뉴를 사용할 수 없습니다.");
    }

    private void ValidateWorkBranch(Worktree worktree)
    {
        ValidateBranch(worktree);
        if (worktree.Branch == baseBranch)
            throw new InvalidOperationException($"{baseBranch}에서 직접 커밋하거나 병합할 수 없습니다.");
    }

    private static async Task<GitResult> RunRequiredAsync(string directory, params string[] arguments)
    {
        var result = await RunAsync(directory, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments[0]} 실패: {result.Message}");
        return result;
    }

    private static async Task<GitResult> RunAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git을 실행할 수 없습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitResult(process.ExitCode, await outputTask, await errorTask);
    }
}
