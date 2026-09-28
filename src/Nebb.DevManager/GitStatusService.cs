using System.Diagnostics;
using System.Globalization;

namespace Nebb.DevManager;

internal enum MainMergeState { Baseline, Same, Merged, Unmerged, Unavailable }

internal sealed record GitWorktreeState(
    string Commit, string Subject, DateTimeOffset? CommittedAt,
    int ChangedFiles, int Staged, int Unstaged, int Untracked,
    string? Upstream, string? RemoteBranch, int? Ahead, int? Behind,
    MainMergeState MainMerge, string? Error)
{
    public string CommitTime => CommittedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    public string WorkingTree => Error is not null ? "확인 실패" :
        ChangedFiles == 0 ? "깨끗함" : $"변경 {ChangedFiles}";

    public string PushStatus => Error is not null ? "확인 실패" :
        RemoteBranch is null ? "분리 HEAD" :
        Ahead is null || Behind is null ? "미게시" :
        Ahead > 0 && Behind > 0 ? $"분기 ↑{Ahead} ↓{Behind}" :
        Ahead > 0 ? $"미푸시 {Ahead}" :
        Behind > 0 ? $"원격 앞섬 {Behind}" : "동기화";

    public string MainMergeStatus => Error is not null ? "확인 실패" : MainMerge switch
    {
        MainMergeState.Baseline => "기준",
        MainMergeState.Same => "main과 동일",
        MainMergeState.Merged => "병합됨",
        MainMergeState.Unmerged => "미병합",
        _ => "확인 불가"
    };

    public string Details => Error is not null ? $"Git 상태 확인 실패: {Error}" :
        $"커밋 {Commit} · {Subject}\n" +
        $"커밋 시각: {CommittedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") ?? "—"}\n" +
        $"작업 변경: {WorkingTree} (스테이징 {Staged}, 미스테이징 {Unstaged}, 미추적 {Untracked})\n" +
        $"원격: {RemoteBranch ?? "없음"} · {PushStatus}\n" +
        $"main 병합: {MainMergeStatus} (origin/main의 커밋 기준)" +
        (Upstream is not null && Upstream != RemoteBranch ? $" · 추적 설정 {Upstream}" : "");

    public static GitWorktreeState Failed(string message) =>
        new("—", "", null, 0, 0, 0, 0, null, null, null, null,
            MainMergeState.Unavailable, message);
}

internal sealed class GitStatusService(string repositoryPath)
{
    public async Task<GitWorktreeState[]> GetStatesAsync(IReadOnlyList<Worktree> worktrees)
    {
        try
        {
            var refs = await RunGitAsync(repositoryPath, true, "for-each-ref", "--format=%(refname)",
                "refs/remotes/origin");
            var remoteRefs = refs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            return await Task.WhenAll(worktrees.Select(item => GetStateAsync(item, remoteRefs)));
        }
        catch (Exception error)
        {
            return worktrees.Select(_ => GitWorktreeState.Failed(error.Message)).ToArray();
        }
    }

    public async Task FetchOriginAsync() =>
        await RunGitAsync(repositoryPath, false, "fetch", "--prune", "origin");

    private static async Task<GitWorktreeState> GetStateAsync(Worktree worktree, HashSet<string> remoteRefs)
    {
        try
        {
            var statusTask = RunGitAsync(worktree.Path, true, "status", "--porcelain=v2", "--branch",
                "--untracked-files=normal");
            var commitTask = RunGitAsync(worktree.Path, true, "log", "-1", "--format=%h%x09%s%x09%cI");
            var remoteBranch = worktree.Branch is "(detached)" or "(unknown)" ? null :
                $"origin/{worktree.Branch}";
            var remoteRef = remoteBranch is null ? null : $"refs/remotes/{remoteBranch}";
            var hasRemoteBranch = remoteRef is not null && remoteRefs.Contains(remoteRef);
            var compareTask = hasRemoteBranch
                ? RunGitAsync(worktree.Path, true, "rev-list", "--left-right", "--count",
                    $"HEAD...{remoteRef}") : Task.FromResult("");
            var hasMain = remoteRefs.Contains("refs/remotes/origin/main");
            var mainCompareTask = hasMain && worktree.Branch != "main"
                ? RunGitAsync(worktree.Path, true, "rev-list", "--left-right", "--count",
                    "HEAD...refs/remotes/origin/main") : Task.FromResult("");

            var results = await Task.WhenAll(statusTask, commitTask, compareTask, mainCompareTask);
            var status = results[0];
            var commit = results[1];
            var compare = hasRemoteBranch ? results[2] : null;
            var commitParts = commit.TrimEnd('\r', '\n').Split('\t', 3);
            if (commitParts.Length != 3 ||
                !DateTimeOffset.TryParse(commitParts[2], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var committedAt))
                throw new InvalidOperationException("커밋 시각을 읽을 수 없습니다.");
            var staged = 0;
            var unstaged = 0;
            var untracked = 0;
            var changed = 0;
            string? upstream = null;
            foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                {
                    upstream = line["# branch.upstream ".Length..].TrimEnd('\r');
                    continue;
                }
                if (line.StartsWith('#')) continue;
                changed++;
                if (line.StartsWith("? ", StringComparison.Ordinal))
                {
                    untracked++;
                    continue;
                }
                if (line.Length < 4) continue;
                if (line[2] != '.') staged++;
                if (line[3] != '.') unstaged++;
            }

            int? ahead = null;
            int? behind = null;
            if (compare is not null)
            {
                (ahead, behind) = ParseCommitCounts(compare);
            }
            var mainMerge = worktree.Branch == "main" ? MainMergeState.Baseline :
                !hasMain ? MainMergeState.Unavailable : MainMergeStatusFor(results[3]);
            return new GitWorktreeState(commitParts[0], commitParts[1], committedAt,
                changed, staged, unstaged, untracked, upstream, remoteBranch, ahead, behind,
                mainMerge, null);
        }
        catch (Exception error)
        {
            return GitWorktreeState.Failed(error.Message);
        }
    }

    private static MainMergeState MainMergeStatusFor(string compare)
    {
        var (ahead, behind) = ParseCommitCounts(compare);
        return ahead > 0 ? MainMergeState.Unmerged :
            behind > 0 ? MainMergeState.Merged : MainMergeState.Same;
    }

    private static (int Ahead, int Behind) ParseCommitCounts(string compare)
    {
        var counts = compare.Split(['\t', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length != 2 || !int.TryParse(counts[0], out var ahead) ||
            !int.TryParse(counts[1], out var behind))
            throw new InvalidOperationException("원격 커밋 수를 읽을 수 없습니다.");
        return (ahead, behind);
    }

    private static async Task<string> RunGitAsync(string directory, bool readOnly, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (readOnly) start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git을 실행할 수 없습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Trim() is { Length: > 0 } message
                ? message : $"Git 명령이 실패했습니다 (코드 {process.ExitCode}).");
        return output;
    }
}
