using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Nebb.DevManager;

internal sealed record RepositoryEntry(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(
        System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
    public string DisplayName => $"{Name} — {Path}";
    public bool IsPixPeek => PixPeekRepository.IsPixPeekRepository(Path);
}

internal sealed class RepositoryCatalog
{
    private static readonly string DefaultPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nebb", "DevManager", "repositories.json");
    private static readonly string LegacyPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nebb", "DevManager", "pixpeek-repository.txt");

    private readonly string storagePath;
    private readonly List<RepositoryEntry> repositories = [];

    public RepositoryCatalog(string? storagePath = null) => this.storagePath = storagePath ?? DefaultPath;

    public IReadOnlyList<RepositoryEntry> Repositories => repositories;

    public void Load()
    {
        if (File.Exists(storagePath))
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(storagePath)) ?? [];
            foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
                AddStored(Path.GetFullPath(path));
        }

        if (File.Exists(LegacyPath))
        {
            var legacy = File.ReadAllText(LegacyPath).Trim();
            if (PixPeekRepository.IsPixPeekRepository(legacy)) Add(legacy);
        }
        if (repositories.Count == 0)
        {
            var current = PixPeekRepository.FindInParents(Environment.CurrentDirectory);
            if (current is not null) Add(current);
        }
    }

    public RepositoryEntry Add(string folder)
    {
        var root = ResolvePrimaryWorktree(folder);
        var existing = repositories.FirstOrDefault(item =>
            item.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var repository = new RepositoryEntry(root);
        repositories.Add(repository);
        try { Save(); }
        catch
        {
            repositories.Remove(repository);
            throw;
        }
        return repository;
    }

    private void AddStored(string path)
    {
        if (!repositories.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            repositories.Add(new RepositoryEntry(path));
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        File.WriteAllText(storagePath,
            JsonSerializer.Serialize(repositories.Select(item => item.Path).ToArray(),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ResolvePrimaryWorktree(string folder)
    {
        var directory = Path.GetFullPath(folder);
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"폴더를 찾을 수 없습니다: {directory}");
        if (RunGit(directory, "rev-parse", "--is-inside-work-tree").Trim() != "true")
            throw new InvalidOperationException("Git 작업 폴더를 선택하세요.");
        var worktrees = RunGit(directory, "worktree", "list", "--porcelain");
        var first = worktrees.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("worktree ", StringComparison.Ordinal));
        if (first is null)
            throw new InvalidOperationException("Git 저장소의 워크트리를 찾을 수 없습니다.");
        return Path.GetFullPath(first["worktree ".Length..].TrimEnd('\r'));
    }

    private static string RunGit(string directory, params string[] arguments)
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
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Trim() is { Length: > 0 } message
                ? message : "Git 저장소를 확인할 수 없습니다.");
        return output;
    }
}
