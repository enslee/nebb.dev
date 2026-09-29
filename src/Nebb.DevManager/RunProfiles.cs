using System.IO;

namespace Nebb.DevManager;

internal enum RunItemKind { Process, ShellScript }
internal enum RunLifecycle { OneShot, LongRunning }
internal enum RunReadinessKind { Process, Port }
internal enum RunShell { PowerShell, Cmd }
internal enum RunScriptSource { Inline, File }

internal sealed class RunReadiness
{
    public RunReadinessKind Type { get; set; } = RunReadinessKind.Process;
    public int? Port { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public bool AllowExternalPort { get; set; }
    public RunReadiness Copy() => new()
    {
        Type = Type, Port = Port, TimeoutSeconds = TimeoutSeconds,
        AllowExternalPort = AllowExternalPort
    };
}

internal sealed class RunItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? CandidateId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public RunItemKind Kind { get; set; }
    public RunLifecycle Lifecycle { get; set; } = RunLifecycle.LongRunning;
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = ".";
    public List<EnvironmentEntry> Environment { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
    public RunReadiness Readiness { get; set; } = new();
    public RunShell Shell { get; set; } = RunShell.PowerShell;
    public RunScriptSource ScriptSource { get; set; } = RunScriptSource.Inline;
    public string Script { get; set; } = "";

    public string Display => $"{Name} · {(Enabled ? Lifecycle.ToString() : "Disabled")}";
    public RunItem Copy() => new()
    {
        Id = Id, CandidateId = CandidateId, Name = Name, Enabled = Enabled,
        Kind = Kind, Lifecycle = Lifecycle,
        Command = Command, Arguments = Arguments, WorkingDirectory = WorkingDirectory,
        Environment = [.. Environment], DependsOn = [.. DependsOn],
        Readiness = (Readiness ?? new RunReadiness()).Copy(), Shell = Shell,
        ScriptSource = ScriptSource, Script = Script
    };
}

internal sealed class RunProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<RunItem> Items { get; set; } = [];
    public string Display => $"{Name} ({Items.Count}개 항목)";

    public RunProfile Copy() => new() { Id = Id, Name = Name,
        Items = Items.Select(item => item.Copy()).ToList() };

    public static RunProfile FromLegacy(SavedCommand command, string id) => new()
    {
        Id = id, Name = "기본 실행",
        Items = [new RunItem
        {
            Id = "legacy-run", CandidateId = command.CandidateId,
            Name = command.Name, Command = command.Command,
            Arguments = command.Arguments, WorkingDirectory = command.WorkingDirectory,
            Environment = [.. command.Environment]
        }]
    };
}

internal static class RunProfileValidator
{
    public static IReadOnlyList<string> Validate(RunProfile profile, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("프로필의 ID와 이름을 입력하세요.");
        var warnings = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var item in profile.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name))
                throw new ArgumentException("항목 ID와 이름은 비어 있지 않고 중복되지 않아야 합니다.");
            if (string.IsNullOrWhiteSpace(item.WorkingDirectory))
                throw new ArgumentException($"{item.Name}: 작업 폴더를 입력하세요.");
            var cwd = Path.GetFullPath(Path.Combine(root, item.WorkingDirectory));
            if (!Directory.Exists(cwd)) throw new ArgumentException($"{item.Name}: 작업 폴더가 없습니다: {cwd}");
            if (!Inside(root, cwd)) warnings.Add($"{item.Name}: 작업 폴더가 워크트리 밖입니다: {cwd}");
            if (item.Kind == RunItemKind.Process && string.IsNullOrWhiteSpace(item.Command))
                throw new ArgumentException($"{item.Name}: 명령을 입력하세요.");
            if (item.Kind == RunItemKind.ShellScript)
            {
                if (string.IsNullOrWhiteSpace(item.Script))
                    throw new ArgumentException($"{item.Name}: 스크립트 또는 파일 경로를 입력하세요.");
                if (item.ScriptSource == RunScriptSource.File)
                {
                    var script = Path.GetFullPath(Path.Combine(cwd, item.Script));
                    if (!File.Exists(script)) throw new ArgumentException($"{item.Name}: 스크립트 파일이 없습니다: {script}");
                    if (!new[] { ".ps1", ".bat", ".cmd" }.Contains(Path.GetExtension(script),
                            StringComparer.OrdinalIgnoreCase))
                        throw new ArgumentException($"{item.Name}: .ps1, .bat, .cmd 파일만 사용할 수 있습니다.");
                    if (!Inside(root, script)) warnings.Add($"{item.Name}: 스크립트 파일이 워크트리 밖입니다: {script}");
                }
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in item.Environment)
                if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Name,
                        "^[A-Za-z_][A-Za-z0-9_]*$") || !names.Add(entry.Name))
                    throw new ArgumentException($"{item.Name}: 환경변수 이름이 잘못되었거나 중복됩니다.");
            item.Readiness ??= new RunReadiness();
            if (item.Readiness.TimeoutSeconds is < 1 or > 3600 ||
                item.Lifecycle == RunLifecycle.LongRunning &&
                item.Readiness.Type == RunReadinessKind.Port &&
                item.Readiness.Port is not (>= 1 and <= 65535))
                throw new ArgumentException($"{item.Name}: 포트 또는 대기 시간이 올바르지 않습니다.");
        }
        var byId = profile.Items.ToDictionary(item => item.Id);
        foreach (var item in profile.Items.Where(item => item.Enabled))
            foreach (var dependency in item.DependsOn)
                if (!byId.TryGetValue(dependency, out var target) || !target.Enabled || dependency == item.Id)
                    throw new ArgumentException($"{item.Name}: 의존 항목이 없거나 비활성화되어 있습니다.");
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(RunItem item)
        {
            if (visited.Contains(item.Id)) return;
            if (!visiting.Add(item.Id)) throw new ArgumentException("항목 의존성에 순환이 있습니다.");
            foreach (var id in item.DependsOn) Visit(byId[id]);
            visiting.Remove(item.Id);
            visited.Add(item.Id);
        }
        foreach (var item in profile.Items.Where(item => item.Enabled)) Visit(item);
        return warnings;
    }

    private static bool Inside(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
