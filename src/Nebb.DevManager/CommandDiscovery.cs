using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nebb.DevManager;

internal sealed record ProjectUnit(string Root, string Manifest, string Stack);

internal sealed class RepositoryScan
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "target", "dist", "build", ".venv", "venv",
        ".next", ".gradle", "vendor", ".idea", ".vs", "coverage"
    };

    private readonly HashSet<string> files;
    public string Root { get; }

    public RepositoryScan(string root)
    {
        Root = Path.GetFullPath(root);
        files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    (Path.GetFileName(directory).Equals(".claude", StringComparison.OrdinalIgnoreCase) &&
                     info.Name.Equals("worktrees", StringComparison.OrdinalIgnoreCase)) ||
                    File.Exists(Path.Combine(child, ".git")) ||
                    Directory.Exists(Path.Combine(child, ".git"))) continue;
                var dartSourceBin = info.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(directory, "pubspec.yaml"));
                if (!IgnoredDirectories.Contains(info.Name) || dartSourceBin) pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
                files.Add(Relative(file));
        }
    }

    public IReadOnlyList<string> Files => files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public bool Has(string relativePath) => files.Contains(Normalize(relativePath));
    public string Absolute(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    public string Read(string relativePath) => File.ReadAllText(Absolute(relativePath));
    public string Relative(string path) => Normalize(Path.GetRelativePath(Root, path));
    public IEnumerable<string> Under(string directory) => files.Where(path =>
        directory == "." || path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    public static string Normalize(string path) => path.Replace('\\', '/');
    public static string Parent(string path)
    {
        var parent = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(parent) ? "." : Normalize(parent);
    }
}

internal interface IDetectionRule
{
    string Id { get; }
    IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project);
}

internal interface ICommandDetector
{
    string Id { get; }
    bool CanHandle(string manifest);
    IEnumerable<IDetectionRule> Rules { get; }
}

internal sealed class ManifestDetector(string id, Func<string, bool> canHandle,
    params IDetectionRule[] rules) : ICommandDetector
{
    public string Id => id;
    public bool CanHandle(string manifest) => canHandle(manifest);
    public IEnumerable<IDetectionRule> Rules => rules;
}

internal static class CandidateRules
{
    public static readonly HashSet<string> RunNames = new(StringComparer.OrdinalIgnoreCase)
        { "dev", "start", "serve", "run" };
    public static readonly HashSet<string> TestNames = new(StringComparer.OrdinalIgnoreCase)
        { "test", "tests", "check", "spec" };

    public static CommandKind? KindFor(string name)
    {
        var prefix = name.Split(':', '-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        if (TestNames.Contains(prefix)) return CommandKind.Test;
        if (RunNames.Contains(prefix)) return CommandKind.Run;
        return null;
    }

    public static CommandCandidate Make(ProjectUnit project, string rule, string key,
        string name, string command, string arguments, CommandKind kind,
        CandidateConfidence confidence, string? workingDirectory = null,
        string? sourceFile = null) => new(
        $"{project.Stack}|{sourceFile ?? project.Manifest}|{rule}|{key}", name, command,
        arguments, workingDirectory ?? project.Root, kind, project.Stack,
        sourceFile ?? project.Manifest, rule, confidence);

    public static JsonDocument? Json(RepositoryScan scan, string path)
    {
        try { return JsonDocument.Parse(scan.Read(path)); }
        catch (JsonException) { return null; }
    }

    public static XDocument? Xml(RepositoryScan scan, string path)
    {
        try { return XDocument.Parse(scan.Read(path)); }
        catch (System.Xml.XmlException) { return null; }
    }

    public static bool HasElement(XDocument document, string name, string? value = null) =>
        document.Descendants().Any(item => item.Name.LocalName == name &&
            (value is null || item.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));

    public static string? TomlValue(string content, string section, string key)
    {
        var current = "";
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) current = line.Trim('[', ']');
            else if (current == section)
            {
                var match = Regex.Match(line, $"^{Regex.Escape(key)}\\s*=\\s*[\"'](?<value>[^\"']+)[\"']");
                if (match.Success) return match.Groups["value"].Value;
            }
        }
        return null;
    }

    public static IEnumerable<(string Key, string Value)> TomlSection(string content, string section)
    {
        var current = "";
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line.Trim('[', ']'); continue; }
            if (current != section) continue;
            var match = Regex.Match(line, "^(?<key>[A-Za-z_][A-Za-z0-9_-]*)\\s*=\\s*[\"'](?<value>[^\"']+)[\"']");
            if (match.Success) yield return (match.Groups["key"].Value, match.Groups["value"].Value);
        }
    }
}

internal sealed class CommandDiscovery
{
    private readonly ICommandDetector[] detectors =
    [
        new ManifestDetector("Node", name => name.Equals("package.json", StringComparison.OrdinalIgnoreCase), new NodeScriptRule()),
        new ManifestDetector(".NET", name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase), new DotNetProjectRule()),
        new ManifestDetector("Python", name => name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pytest.ini", StringComparison.OrdinalIgnoreCase), new PythonProjectRule()),
        new ManifestDetector("Rust", name => name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase), new RustProjectRule()),
        new ManifestDetector("Go", name => name.Equals("go.mod", StringComparison.OrdinalIgnoreCase), new GoProjectRule()),
        new ManifestDetector("Java", name => name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase), new JavaProjectRule()),
        new ManifestDetector("Ruby", name => name.Equals("Rakefile", StringComparison.OrdinalIgnoreCase), new RubyTaskRule()),
        new ManifestDetector("Dart", name => name.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase), new DartProjectRule()),
        new ManifestDetector("PHP", name => name.Equals("composer.json", StringComparison.OrdinalIgnoreCase), new ComposerScriptRule()),
        new ManifestDetector("Make", name => name.Equals("Makefile", StringComparison.OrdinalIgnoreCase), new MakeTargetRule()),
        new ManifestDetector("Compose", name => new[] { "compose.yaml", "compose.yml",
            "docker-compose.yaml", "docker-compose.yml" }.Contains(name, StringComparer.OrdinalIgnoreCase),
            new ComposeServiceRule())
    ];

    public IReadOnlyList<CommandCandidate> Discover(string worktreePath)
    {
        var scan = new RepositoryScan(worktreePath);
        var results = new List<CommandCandidate>();
        foreach (var manifest in scan.Files)
        {
            foreach (var detector in detectors.Where(item => item.CanHandle(Path.GetFileName(manifest))))
            {
                var project = new ProjectUnit(RepositoryScan.Parent(manifest), manifest, detector.Id);
                foreach (var rule in detector.Rules) results.AddRange(rule.Evaluate(scan, project));
            }
        }
        return results.GroupBy(item => $"{item.Kind}|{item.WorkingDirectory}|{item.Command}|{item.Arguments}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Confidence).ThenBy(item => item.Id).First())
            .OrderBy(item => item.Kind).ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.WorkingDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool SourceMissing(SavedCommand? command, IEnumerable<CommandCandidate> candidates,
        CommandKind? expectedKind = null) =>
        command?.CandidateId is { } id && !candidates.Any(item => item.Id == id &&
            (expectedKind is null || item.Kind == expectedKind));
}
