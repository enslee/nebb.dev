using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nebb.DevManager;

internal enum CommandKind { Run, Test, Service }
internal enum CandidateConfidence { Low, Medium, High }

internal sealed record CommandCandidate(
    string Id, string Name, string Command, string Arguments, string WorkingDirectory,
    CommandKind Kind, string Stack, string SourceFile, string RuleId,
    CandidateConfidence Confidence)
{
    public string Display => $"{Name}  ·  {Command} {Arguments}  ·  {WorkingDirectory}  ·  {SourceFile}  ·  {RuleId}  ·  {Confidence}";
    public string ProjectGroup => CandidateGrouping.ProjectPath(WorkingDirectory);
}

internal static class CandidateGrouping
{
    public static string ProjectPath(string workingDirectory)
    {
        var parts = RepositoryScan.Normalize(workingDirectory).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] == ".") return ".";
        return parts.Length > 1 && new[] { "apps", "src", "crates", "packages", "tools" }
            .Contains(parts[0], StringComparer.OrdinalIgnoreCase)
            ? $"{parts[0]}/{parts[1]}" : parts[0];
    }

    public static string Title(string path) => path == "." ? "저장소 루트" :
        $"{char.ToUpperInvariant(Path.GetFileName(path)[0])}{Path.GetFileName(path)[1..]}  ({path})";
}

internal sealed record EnvironmentEntry(string Name, string Value);

internal sealed class SavedCommand
{
    public string? CandidateId { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = ".";
    public List<EnvironmentEntry> Environment { get; set; } = [];
    public string Display => $"{Name}  ·  {WorkingDirectory}";

    public SavedCommand Copy() => new()
    {
        CandidateId = CandidateId,
        Name = Name,
        Command = Command,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
        Environment = [.. Environment]
    };

    public static SavedCommand FromCandidate(CommandCandidate candidate) => new()
    {
        CandidateId = candidate.Id,
        Name = candidate.Name,
        Command = candidate.Command,
        Arguments = candidate.Arguments,
        WorkingDirectory = candidate.WorkingDirectory
    };
}

internal sealed class CommandPair
{
    public SavedCommand? Run { get; set; }
    public SavedCommand? Test { get; set; }

    public SavedCommand? Get(CommandKind kind) => kind switch
    {
        CommandKind.Run => Run,
        CommandKind.Test => Test,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public void Set(CommandKind kind, SavedCommand? command)
    {
        switch (kind)
        {
            case CommandKind.Run: Run = command; break;
            case CommandKind.Test: Test = command; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}

internal sealed class RepositoryCommandSettings
{
    public int Version { get; set; } = 1;
    public string RepositoryPath { get; set; } = "";
    public CommandPair Default { get; set; } = new();
    public Dictionary<string, CommandPair> Worktrees { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SavedCommand> DefaultServices { get; set; } = [];
    public Dictionary<string, List<SavedCommand>> ServiceOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public SavedCommand? Effective(string worktreePath, CommandKind kind) =>
        Worktrees.GetValueOrDefault(Path.GetFullPath(worktreePath))?.Get(kind) ?? Default.Get(kind);

    public CommandPair OverrideFor(string worktreePath)
    {
        var path = Path.GetFullPath(worktreePath);
        if (!Worktrees.TryGetValue(path, out var value)) Worktrees[path] = value = new CommandPair();
        return value;
    }

    public IReadOnlyList<SavedCommand> EffectiveServices(string worktreePath) =>
        ServiceOverrides.GetValueOrDefault(Path.GetFullPath(worktreePath)) ?? DefaultServices;

    public List<SavedCommand>? ServiceOverrideFor(string worktreePath) =>
        ServiceOverrides.GetValueOrDefault(Path.GetFullPath(worktreePath));

    public List<SavedCommand> CopyDefaultServicesFor(string worktreePath)
    {
        var copy = DefaultServices.Select(item => item.Copy()).ToList();
        ServiceOverrides[Path.GetFullPath(worktreePath)] = copy;
        return copy;
    }

    public void ResetServiceOverride(string worktreePath) =>
        ServiceOverrides.Remove(Path.GetFullPath(worktreePath));
}

internal sealed class CommandSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string repositoryPath;
    private readonly string filePath;

    public CommandSettingsStore(string repositoryPath, string? storageDirectory = null)
    {
        this.repositoryPath = Path.GetFullPath(repositoryPath);
        var directory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nebb", "DevManager", "command-settings");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(this.repositoryPath.ToUpperInvariant()));
        filePath = Path.Combine(directory, $"{Convert.ToHexString(hash)[..20].ToLowerInvariant()}.json");
    }

    public RepositoryCommandSettings Load()
    {
        if (!File.Exists(filePath)) return Empty();
        var loaded = JsonSerializer.Deserialize<RepositoryCommandSettings>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidDataException("명령 설정 파일이 비어 있습니다.");
        if (loaded.Version != 1 || !Path.GetFullPath(loaded.RepositoryPath).Equals(
                repositoryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("명령 설정 파일의 버전 또는 저장소 경로가 일치하지 않습니다.");
        loaded.Default ??= new CommandPair();
        loaded.Worktrees = new Dictionary<string, CommandPair>(
            loaded.Worktrees ?? [], StringComparer.OrdinalIgnoreCase);
        loaded.DefaultServices ??= [];
        loaded.ServiceOverrides = new Dictionary<string, List<SavedCommand>>(
            loaded.ServiceOverrides ?? [], StringComparer.OrdinalIgnoreCase);
        return loaded;
    }

    public void Save(RepositoryCommandSettings settings)
    {
        settings.Version = 1;
        settings.RepositoryPath = repositoryPath;
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, filePath, true);
    }

    private RepositoryCommandSettings Empty() => new() { RepositoryPath = repositoryPath };
}

internal static class CommandSettingsValidator
{
    public static void Validate(SavedCommand command, string root, string environmentText)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.Command))
            throw new ArgumentException("이름과 명령을 입력하세요.");
        if (string.IsNullOrWhiteSpace(command.WorkingDirectory) ||
            Path.IsPathRooted(command.WorkingDirectory))
            throw new ArgumentException("작업 폴더를 워크트리 기준 상대 경로로 입력하세요.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var folder = Path.GetFullPath(Path.Combine(fullRoot, command.WorkingDirectory));
        if (!folder.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !folder.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("작업 폴더는 워크트리 안에 있어야 합니다.");
        if (!Directory.Exists(folder)) throw new ArgumentException($"작업 폴더가 없습니다: {folder}");
        var entries = new List<EnvironmentEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in environmentText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var separator = line.IndexOf('=');
            var name = separator < 0 ? line : line[..separator];
            if (separator < 0 || !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") || !names.Add(name))
                throw new ArgumentException("환경변수는 중복 없이 한 줄에 NAME=value 형식으로 입력하세요.");
            entries.Add(new EnvironmentEntry(name, line[(separator + 1)..]));
        }
        command.Environment = entries;
    }
}
