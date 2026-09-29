using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nebb.DevManager;

internal sealed class NodeScriptRule : IDetectionRule
{
    public string Id => "node.package-script";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        using var json = CandidateRules.Json(scan, project.Manifest);
        if (json is null || !json.RootElement.TryGetProperty("scripts", out var scripts) ||
            scripts.ValueKind != JsonValueKind.Object) yield break;
        var manager = CandidateRules.NodePackageManager(scan, project, json.RootElement);
        var tauriApp = TauriRule.SupportingEvidence(scan, project, json.RootElement) is not null;
        foreach (var script in scripts.EnumerateObject())
        {
            var kind = CandidateRules.KindFor(script.Name);
            if (kind is null || script.Value.ValueKind != JsonValueKind.String) continue;
            var name = tauriApp && script.Name.Equals("dev", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(script.Value.GetString()!, @"^\s*vite(?:\s|$)", RegexOptions.IgnoreCase)
                ? "Frontend Only" : script.Name;
            yield return CandidateRules.Make(project, Id, script.Name, name,
                manager, $"run {script.Name}", kind.Value, CandidateConfidence.High);
        }
    }
}

internal sealed class TauriRule : IDetectionRule
{
    public string Id => "node.tauri-app";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        using var json = CandidateRules.Json(scan, project.Manifest);
        if (json is null) yield break;
        var evidence = SupportingEvidence(scan, project, json.RootElement);
        if (evidence is null) yield break;
        yield return CandidateRules.Make(project, Id, "tauri.dev", "Tauri App",
            CandidateRules.NodePackageManager(scan, project, json.RootElement),
            "run tauri dev", CommandKind.Run, CandidateConfidence.High,
            priority: CandidatePriority.Recommended, supportingEvidence: evidence);
    }

    internal static string? SupportingEvidence(RepositoryScan scan, ProjectUnit project, JsonElement package)
    {
        if (!package.TryGetProperty("scripts", out var scripts) ||
            scripts.ValueKind != JsonValueKind.Object ||
            !scripts.TryGetProperty("tauri", out var script) ||
            script.ValueKind != JsonValueKind.String ||
            !script.GetString()!.Trim().Equals("tauri", StringComparison.OrdinalIgnoreCase)) return null;

        var config = Path.Combine(project.Root, "src-tauri", "tauri.conf.json");
        if (scan.Has(config)) return RepositoryScan.Normalize(config);

        var cargo = Path.Combine(project.Root, "src-tauri", "Cargo.toml");
        if (scan.Has(cargo) && HasCargoDependency(scan.Read(cargo)))
            return RepositoryScan.Normalize(cargo);

        foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies" })
            if (package.TryGetProperty(section, out var dependencies) &&
                dependencies.ValueKind == JsonValueKind.Object &&
                dependencies.TryGetProperty("@tauri-apps/cli", out _))
                return "@tauri-apps/cli (package.json)";
        return null;
    }

    private static bool HasCargoDependency(string content)
    {
        var section = "";
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.Trim('[', ']');
                if (section.Equals("dependencies.tauri", StringComparison.OrdinalIgnoreCase) ||
                    section.EndsWith(".dependencies.tauri", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if ((section.Equals("dependencies", StringComparison.OrdinalIgnoreCase) ||
                      section.EndsWith(".dependencies", StringComparison.OrdinalIgnoreCase)) &&
                     Regex.IsMatch(line, @"^tauri\s*=")) return true;
        }
        return false;
    }
}

internal sealed class DotNetProjectRule : IDetectionRule
{
    public string Id => "dotnet.project";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var xml = CandidateRules.Xml(scan, project.Manifest);
        if (xml?.Root is null) yield break;
        var file = Path.GetFileName(project.Manifest);
        var sdk = xml.Root.Attribute("Sdk")?.Value ?? "";
        var runnable = sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
            CandidateRules.HasElement(xml, "OutputType", "Exe") ||
            CandidateRules.HasElement(xml, "OutputType", "WinExe");
        var test = CandidateRules.HasElement(xml, "IsTestProject", "true") ||
            xml.Descendants().Any(item => item.Name.LocalName == "PackageReference" &&
                string.Equals(item.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk",
                    StringComparison.OrdinalIgnoreCase));
        if (runnable) yield return CandidateRules.Make(project, Id, "run", $"{file} 실행",
            "dotnet", $"run --project \"{file}\"", CommandKind.Run, CandidateConfidence.Medium);
        if (test) yield return CandidateRules.Make(project, Id, "test", $"{file} 테스트",
            "dotnet", $"test \"{file}\"", CommandKind.Test, CandidateConfidence.High);
    }
}

internal sealed class PythonProjectRule : IDetectionRule
{
    public string Id => "python.project";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var isPyproject = Path.GetFileName(project.Manifest).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase);
        if (isPyproject)
        {
            var content = scan.Read(project.Manifest);
            foreach (var section in new[] { "project.scripts", "tool.poetry.scripts" })
                foreach (var script in CandidateRules.TomlSection(content, section))
                    yield return CandidateRules.Make(project, Id, section + "." + script.Key,
                        script.Key, script.Key, "", CommandKind.Run, CandidateConfidence.High);
            if (content.Contains("[tool.pytest.ini_options]", StringComparison.Ordinal) &&
                !scan.Has(Path.Combine(project.Root, "pytest.ini")))
                yield return CandidateRules.Make(project, Id, "pytest", "pytest", "python",
                    "-m pytest", CommandKind.Test, CandidateConfidence.High);
        }
        else yield return CandidateRules.Make(project, Id, "pytest", "pytest", "python",
            "-m pytest", CommandKind.Test, CandidateConfidence.High);
    }
}

internal sealed class RustProjectRule : IDetectionRule
{
    public string Id => "rust.cargo";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var content = scan.Read(project.Manifest);
        var package = content.Contains("[package]", StringComparison.Ordinal);
        var workspace = content.Contains("[workspace]", StringComparison.Ordinal);
        if (!package && !workspace) yield break;
        var defaultRun = CandidateRules.TomlValue(content, "package", "default-run");
        if (defaultRun is not null)
            yield return CandidateRules.Make(project, Id, "default-run", defaultRun,
                "cargo", $"run --bin {defaultRun}", CommandKind.Run, CandidateConfidence.High);
        else if (scan.Has(Path.Combine(project.Root, "src/main.rs")))
            yield return CandidateRules.Make(project, Id, "main", "cargo run", "cargo", "run",
                CommandKind.Run, CandidateConfidence.Medium);
        foreach (Match bin in Regex.Matches(content,
                     "(?ms)^\\[\\[bin\\]\\]\\s*(?<body>.*?)(?=^\\[|\\z)"))
        {
            var name = Regex.Match(bin.Groups["body"].Value, "(?m)^name\\s*=\\s*[\"'](?<name>[^\"']+)");
            if (name.Success && name.Groups["name"].Value != defaultRun)
            {
                var value = name.Groups["name"].Value;
                yield return CandidateRules.Make(project, Id, "bin." + value, value,
                    "cargo", $"run --bin {value}", CommandKind.Run, CandidateConfidence.High);
            }
        }
        yield return CandidateRules.Make(project, Id, "test", "cargo test", "cargo",
            workspace && !package ? "test --workspace" : "test", CommandKind.Test,
            CandidateConfidence.Medium);
    }
}

internal sealed class GoProjectRule : IDetectionRule
{
    public string Id => "go.module";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var goFiles = scan.Under(project.Root).Where(path => path.EndsWith(".go", StringComparison.OrdinalIgnoreCase) &&
            !NestedModule(scan, project.Root, path)).ToArray();
        foreach (var group in goFiles.Where(path => !path.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(RepositoryScan.Parent))
        {
            if (!group.Any(path => Regex.IsMatch(scan.Read(path), "(?m)^package\\s+main\\b"))) continue;
            var relative = Path.GetRelativePath(scan.Absolute(project.Root), scan.Absolute(group.Key));
            var argument = relative == "." ? "." : "./" + RepositoryScan.Normalize(relative);
            yield return CandidateRules.Make(project, Id, "main." + group.Key, $"go run {argument}",
                "go", $"run {argument}", CommandKind.Run, CandidateConfidence.Medium);
        }
        if (goFiles.Any(path => path.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)))
            yield return CandidateRules.Make(project, Id, "test", "go test", "go", "test ./...",
                CommandKind.Test, CandidateConfidence.Medium);
    }

    private static bool NestedModule(RepositoryScan scan, string root, string file)
    {
        var directory = RepositoryScan.Parent(file);
        while (directory != root && directory != ".")
        {
            if (scan.Has(Path.Combine(directory, "go.mod"))) return true;
            directory = RepositoryScan.Parent(directory);
        }
        return false;
    }
}

internal sealed class JavaProjectRule : IDetectionRule
{
    public string Id => "java.build";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var name = Path.GetFileName(project.Manifest);
        var hasTests = scan.Under(project.Root).Any(path => path.StartsWith(
            project.Root == "." ? "src/test/" : project.Root + "/src/test/",
            StringComparison.OrdinalIgnoreCase));
        if (name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase))
        {
            var xml = CandidateRules.Xml(scan, project.Manifest);
            if (xml is null) yield break;
            var plugins = xml.Descendants().Where(item => item.Name.LocalName == "plugin")
                .SelectMany(item => item.Elements().Where(child => child.Name.LocalName == "artifactId"))
                .Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (plugins.Contains("spring-boot-maven-plugin"))
                yield return CandidateRules.Make(project, Id, "spring", "Spring Boot", "mvn",
                    "spring-boot:run", CommandKind.Run, CandidateConfidence.High);
            if (plugins.Contains("exec-maven-plugin") && CandidateRules.HasElement(xml, "mainClass"))
                yield return CandidateRules.Make(project, Id, "exec", "Maven exec", "mvn",
                    "exec:java", CommandKind.Run, CandidateConfidence.High);
            if (hasTests) yield return CandidateRules.Make(project, Id, "test", "Maven test",
                "mvn", "test", CommandKind.Test, CandidateConfidence.Medium);
        }
        else
        {
            var content = scan.Read(project.Manifest);
            var wrapper = scan.Has(Path.Combine(project.Root, "gradlew.bat")) ? "gradlew.bat" : "gradle";
            var spring = content.Contains("org.springframework.boot", StringComparison.Ordinal);
            var application = Regex.IsMatch(content, "(?:id\\s*\\(?[\"']application|apply\\s+plugin:\\s*[\"']application)");
            var java = Regex.IsMatch(content, "(?:id\\s*\\(?[\"']java(?:-library)?[\"']|apply\\s+plugin:\\s*[\"']java)");
            if (spring) yield return CandidateRules.Make(project, Id, "bootRun", "Gradle bootRun",
                wrapper, "bootRun", CommandKind.Run, CandidateConfidence.High);
            if (application) yield return CandidateRules.Make(project, Id, "run", "Gradle run",
                wrapper, "run", CommandKind.Run, CandidateConfidence.High);
            if (hasTests && (spring || application || java))
                yield return CandidateRules.Make(project, Id, "test", "Gradle test", wrapper,
                    "test", CommandKind.Test, CandidateConfidence.Medium);
        }
    }
}

internal sealed class RubyTaskRule : IDetectionRule
{
    public string Id => "ruby.rake-task";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var command = scan.Has(Path.Combine(project.Root, "Gemfile")) ? "bundle" : "rake";
        foreach (Match match in Regex.Matches(scan.Read(project.Manifest),
                     "(?m)^\\s*task\\s+(?::(?<name>[A-Za-z_][\\w-]*)|[\"'](?<name>[A-Za-z_][\\w-]*)[\"']|(?<name>[A-Za-z_][\\w-]*):)"))
        {
            var name = match.Groups["name"].Value;
            var kind = CandidateRules.KindFor(name);
            if (kind is null) continue;
            yield return CandidateRules.Make(project, Id, name, $"rake {name}", command,
                command == "bundle" ? $"exec rake {name}" : name, kind.Value,
                CandidateConfidence.High);
        }
    }
}

internal sealed class DartProjectRule : IDetectionRule
{
    public string Id => "dart.pubspec";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var content = scan.Read(project.Manifest);
        var flutter = Regex.IsMatch(content, "(?m)^\\s*flutter:\\s*$") &&
            content.Contains("sdk: flutter", StringComparison.Ordinal);
        var command = flutter ? "flutter" : "dart";
        if (flutter && scan.Has(Path.Combine(project.Root, "lib/main.dart")))
            yield return CandidateRules.Make(project, Id, "main", "Flutter run", command,
                "run", CommandKind.Run, CandidateConfidence.Medium);
        if (!flutter)
            foreach (var file in scan.Under(project.Root).Where(path => path.StartsWith(
                         project.Root == "." ? "bin/" : project.Root + "/bin/", StringComparison.OrdinalIgnoreCase) &&
                         path.EndsWith(".dart", StringComparison.OrdinalIgnoreCase)))
                yield return CandidateRules.Make(project, Id, file, Path.GetFileNameWithoutExtension(file),
                    command, "run " + Path.GetRelativePath(scan.Absolute(project.Root), scan.Absolute(file)),
                    CommandKind.Run, CandidateConfidence.Medium, sourceFile: file);
        if (scan.Under(project.Root).Any(path => path.StartsWith(
                project.Root == "." ? "test/" : project.Root + "/test/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("_test.dart", StringComparison.OrdinalIgnoreCase)))
            yield return CandidateRules.Make(project, Id, "test", $"{command} test", command,
                "test", CommandKind.Test, CandidateConfidence.Medium);
    }
}

internal sealed class ComposerScriptRule : IDetectionRule
{
    public string Id => "php.composer-script";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        using var json = CandidateRules.Json(scan, project.Manifest);
        if (json is null || !json.RootElement.TryGetProperty("scripts", out var scripts) ||
            scripts.ValueKind != JsonValueKind.Object) yield break;
        foreach (var script in scripts.EnumerateObject())
        {
            var kind = CandidateRules.KindFor(script.Name);
            if (kind is null || script.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array)) continue;
            yield return CandidateRules.Make(project, Id, script.Name, script.Name, "composer",
                "run-script " + script.Name, kind.Value, CandidateConfidence.High);
        }
    }
}

internal sealed class MakeTargetRule : IDetectionRule
{
    public string Id => "make.target";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        foreach (Match match in Regex.Matches(scan.Read(project.Manifest),
                     "(?m)^(?<name>[A-Za-z_][A-Za-z0-9_-]*):(?!=)"))
        {
            var name = match.Groups["name"].Value;
            var kind = CandidateRules.KindFor(name);
            if (kind is null) continue;
            yield return CandidateRules.Make(project, Id, name, $"make {name}", "make", name,
                kind.Value, CandidateConfidence.High);
        }
    }
}

internal sealed class ComposeServiceRule : IDetectionRule
{
    public string Id => "compose.service";

    public IEnumerable<CommandCandidate> Evaluate(RepositoryScan scan, ProjectUnit project)
    {
        var inServices = false;
        foreach (var line in scan.Read(project.Manifest).Split('\n'))
        {
            if (Regex.IsMatch(line, "^services:\\s*(?:#.*)?$")) { inServices = true; continue; }
            if (inServices && Regex.IsMatch(line, "^[^\\s#]")) inServices = false;
            if (!inServices) continue;
            var match = Regex.Match(line, "^  (?<name>[A-Za-z0-9_.-]+):\\s*(?:#.*)?$");
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            yield return CandidateRules.Make(project, Id, name, $"Compose {name}", "docker",
                $"compose -f {Path.GetFileName(project.Manifest)} up {name}",
                CommandKind.Service, CandidateConfidence.High);
        }
    }
}
