using Nebb.DevManager;
using System.Text.Json.Nodes;

if (args.Length == 2 && args[0] == "--scan")
{
    var actual = new CommandDiscovery().Discover(args[1]);
    foreach (var group in actual.GroupBy(item => item.ProjectGroup)
                 .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"{group.Key}: Run={group.Count(item => item.Kind == CommandKind.Run)}, " +
            $"Test={group.Count(item => item.Kind == CommandKind.Test)}, " +
            $"Service={group.Count(item => item.Kind == CommandKind.Service)}");
    Console.WriteLine($"Total: {actual.Count}");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "NebbCommandTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Put(string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    Put("frontend/package.json", """{"scripts":{"dev":"vite","test":"vitest"}}""");
    Put("node_modules/ignored/package.json", """{"scripts":{"dev":"ignored"}}""");
    Put(".claude/worktrees/copied/package.json", """{"scripts":{"dev":"copied"}}""");
    Put("nested-worktree/.git", "gitdir: ../.git/worktrees/nested-worktree");
    Put("nested-worktree/package.json", """{"scripts":{"dev":"nested"}}""");
    Put("nested-repository/.git/HEAD", "ref: refs/heads/main");
    Put("nested-repository/package.json", """{"scripts":{"dev":"nested"}}""");
    Put("dotnet/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
    Put("dotnet/App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"1\" /></ItemGroup></Project>");
    Put("python/pyproject.toml", "[project.scripts]\nserve = 'sample.cli:main'\n[tool.pytest.ini_options]\n");
    Put("rust/Cargo.toml", "[package]\nname = 'sample'\nversion = '0.1.0'\n");
    Put("rust/src/main.rs", "fn main() {}");
    Put("go/go.mod", "module example.com/sample\n");
    Put("go/cmd/app/main.go", "package main\nfunc main() {}\n");
    Put("go/sample_test.go", "package sample\n");
    Put("maven/pom.xml", "<project><build><plugins><plugin><artifactId>spring-boot-maven-plugin</artifactId></plugin></plugins></build></project>");
    Put("maven/src/test/java/AppTest.java", "class AppTest {}");
    Put("gradle/build.gradle", "plugins { id 'application'; id 'java' }");
    Put("gradle/build.gradle.kts", "plugins { id(\"application\"); id(\"java\") }");
    Put("gradle/src/test/java/AppTest.java", "class AppTest {}");
    Put("ruby/Rakefile", "task :start do\nend\ntask :test do\nend\n");
    Put("ruby/Gemfile", "source 'https://example.invalid'");
    Put("flutter/pubspec.yaml", "dependencies:\n  flutter:\n    sdk: flutter\n");
    Put("flutter/lib/main.dart", "void main() {}");
    Put("flutter/test/main_test.dart", "void main() {}");
    Put("dart/pubspec.yaml", "name: command_tool\n");
    Put("dart/bin/command_tool.dart", "void main() {}");
    Put("php/composer.json", """{"scripts":{"start":"php server.php","test":"phpunit"}}""");
    Put("make/Makefile", "dev:\n\t@echo dev\ntest:\n\t@echo test\n");
    Put("compose/compose.yaml", "services:\n  web:\n    image: nginx\n  test:\n    image: test\n");

    var discovery = new CommandDiscovery();
    var candidates = discovery.Discover(root);
    foreach (var stack in new[] { "Node", ".NET", "Python", "Rust", "Go", "Java", "Ruby", "Dart", "PHP", "Make", "Compose" })
        Expect(candidates.Any(item => item.Stack == stack), $"Missing stack: {stack}");
    Expect(candidates.Any(item => item.Stack == "Node" && item.Kind == CommandKind.Run), "Node run missing");
    Expect(candidates.Any(item => item.Stack == "Node" && item.Kind == CommandKind.Test), "Node test missing");
    Expect(candidates.Any(item => item.Stack == "Go" && item.Kind == CommandKind.Run), "Go run missing");
    Expect(candidates.Any(item => item.Stack == "Go" && item.Kind == CommandKind.Test), "Go test missing");
    Expect(candidates.Count(item => item.Stack == "Compose" && item.Kind == CommandKind.Service) == 2,
        "Compose services were not classified separately");
    Expect(!candidates.Any(item => item.Stack == "Compose" && item.Kind == CommandKind.Run),
        "Compose service appeared as application run");
    Expect(CandidateGrouping.ProjectPath("apps/desktop/src-tauri") == "apps/desktop" &&
        CandidateGrouping.ProjectPath("src/landing") == "src/landing" &&
        CandidateGrouping.ProjectPath("crates/keeped-sync") == "crates/keeped-sync",
        "Project grouping");
    Expect(candidates.Any(item => item.Stack == "Dart" && item.Kind == CommandKind.Run &&
        item.WorkingDirectory == "dart"), "Dart bin entrypoint missing");
    Expect(!candidates.Any(item => item.SourceFile.Contains("node_modules", StringComparison.OrdinalIgnoreCase)), "Excluded directory scanned");
    Expect(!candidates.Any(item => item.SourceFile.Contains(".claude/worktrees", StringComparison.OrdinalIgnoreCase)), "Claude worktree scanned");
    Expect(!candidates.Any(item => item.SourceFile.Contains("nested-worktree", StringComparison.OrdinalIgnoreCase)), "Nested .git file boundary crossed");
    Expect(!candidates.Any(item => item.SourceFile.Contains("nested-repository", StringComparison.OrdinalIgnoreCase)), "Nested .git directory boundary crossed");
    Expect(discovery.Discover(Path.Combine(root, "nested-worktree")).Any(item => item.Stack == "Node"),
        "Selected worktree root was excluded");
    Expect(candidates.Count(item => item.Stack == "Java" && item.WorkingDirectory == "gradle" &&
        item.Arguments == "run") == 1, "Duplicate Gradle run candidate");
    foreach (var kind in new[] { CommandKind.Run, CommandKind.Test, CommandKind.Service })
    {
        var group = candidates.Where(item => item.Kind == kind).ToArray();
        Expect(group.SequenceEqual(group.OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.WorkingDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)), "Candidate order");
    }

    var store = new CommandSettingsStore(root, Path.Combine(root, "settings"));
    var settings = store.Load();
    var defaultRun = candidates.First(item => item.Stack == "Node" && item.Kind == CommandKind.Run);
    settings.Default.Run = SavedCommand.FromCandidate(defaultRun);
    settings.Default.Test = new SavedCommand { Name = "Manual test", Command = "tool", Arguments = "check" };
    settings.DefaultServices.AddRange(candidates.Where(item => item.Kind == CommandKind.Service)
        .Select(SavedCommand.FromCandidate));
    var worktree = Path.Combine(root, "other-worktree");
    Directory.CreateDirectory(worktree);
    settings.OverrideFor(worktree).Test = new SavedCommand
    {
        Name = "Override test", Command = "tool", Arguments = "check --branch",
        Environment = [new EnvironmentEntry("PORT", "9000")]
    };
    store.Save(settings);
    var loaded = store.Load();
    Expect(loaded.Effective(worktree, CommandKind.Run)?.Command == "npm", "Default run inheritance");
    Expect(loaded.Effective(worktree, CommandKind.Test)?.Arguments == "check --branch", "Test override");
    Expect(loaded.Effective(root, CommandKind.Test)?.Arguments == "check", "Default test changed");
    Expect(loaded.Effective(worktree, CommandKind.Test)?.Environment.Single().Value == "9000", "Environment persistence");
    Expect(loaded.EffectiveServices(worktree).Count == 2, "Service default inheritance");
    var serviceOverride = loaded.CopyDefaultServicesFor(worktree);
    serviceOverride[0].Arguments = "custom service args";
    Expect(loaded.EffectiveServices(worktree)[0].Arguments == "custom service args" &&
        loaded.DefaultServices[0].Arguments != "custom service args", "Service override isolation");
    loaded.ResetServiceOverride(worktree);
    Expect(loaded.EffectiveServices(worktree).Count == 2 &&
        loaded.EffectiveServices(worktree)[0].Arguments == loaded.DefaultServices[0].Arguments,
        "Service override reset");
    Expect(CommandDiscovery.SourceMissing(loaded.Default.Run, []), "Missing source not detected");
    Expect(CommandDiscovery.SourceMissing(SavedCommand.FromCandidate(candidates.First(item =>
        item.Kind == CommandKind.Service)), candidates, CommandKind.Run),
        "Reclassified service remained a run candidate");
    Expect(loaded.Default.Run?.Command == "npm", "Missing source changed saved command");
    loaded.OverrideFor(worktree).Test = null;
    Expect(loaded.Effective(worktree, CommandKind.Test)?.Arguments == "check", "Override removal");

    var validated = new SavedCommand { Name = "Custom", Command = "tool", WorkingDirectory = "frontend" };
    CommandSettingsValidator.Validate(validated, root, "PORT=3000\nMODE=dev");
    Expect(validated.Environment.Count == 2, "Environment parsing");
    void ExpectInvalid(SavedCommand command, string text, string reason)
    {
        try { CommandSettingsValidator.Validate(command, root, text); }
        catch (ArgumentException) { return; }
        throw new Exception(reason);
    }
    ExpectInvalid(new SavedCommand { Name = "Missing", WorkingDirectory = "." }, "", "Missing command accepted");
    ExpectInvalid(new SavedCommand { Name = "Outside", Command = "tool", WorkingDirectory = ".." }, "", "Outside cwd accepted");
    ExpectInvalid(new SavedCommand { Name = "Duplicate", Command = "tool" }, "PORT=1\nport=2", "Duplicate environment accepted");

    var settingsFile = Directory.GetFiles(Path.Combine(root, "settings"), "*.json").Single();
    var oldFormat = JsonNode.Parse(File.ReadAllText(settingsFile))!.AsObject();
    oldFormat.Remove("DefaultServices");
    oldFormat.Remove("ServiceOverrides");
    File.WriteAllText(settingsFile, oldFormat.ToJsonString());
    var compatible = store.Load();
    Expect(compatible.DefaultServices.Count == 0 && compatible.ServiceOverrides.Count == 0 &&
        compatible.Default.Run?.Command == "npm", "Existing settings compatibility");

    Console.WriteLine($"PASS: {candidates.Count} candidates, 11 stacks, persistence and inheritance");
}
finally
{
    var basePath = Path.Combine(Path.GetTempPath(), "NebbCommandTests") + Path.DirectorySeparatorChar;
    if (root.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
