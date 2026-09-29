using Nebb.DevManager;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

if (args.Length == 3 && args[0] == "--start-run-fixture")
{
    var worktreePath = args[1];
    var command = new SavedCommand
    {
        Name = "Restart fixture", Command = Path.Combine(worktreePath, "apps", "desktop", "long-run.cmd"),
        WorkingDirectory = "apps/desktop"
    };
    await new CommandRunManager(Path.GetDirectoryName(worktreePath)!, args[2])
        .SwitchAsync(worktreePath, command);
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
    Put("apps/desktop/package.json", """{"scripts":{"dev":"vite","tauri":"tauri"},"devDependencies":{"@tauri-apps/cli":"2.0.0"}}""");
    Put("apps/desktop/src-tauri/tauri.conf.json", "{}");
    Put("tauri-cli/package.json", """{"packageManager":"pnpm@10.0.0","scripts":{"tauri":"tauri"},"dependencies":{"@tauri-apps/cli":"2.0.0"}}""");
    Put("tauri-config-only/package.json", """{"scripts":{"tauri":"tauri"}}""");
    Put("tauri-config-only/src-tauri/tauri.conf.json", "{}");
    Put("tauri-cargo/package.json", """{"scripts":{"tauri":"tauri"}}""");
    Put("tauri-cargo/src-tauri/Cargo.toml", "[dependencies]\ntauri = { version = '2' }\n");
    Put("tauri-no-evidence/package.json", """{"scripts":{"dev":"vite","tauri":"tauri"}}""");
    Put("tauri-no-script/package.json", """{"scripts":{"dev":"vite"}}""");
    Put("tauri-no-script/src-tauri/tauri.conf.json", "{}");
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
    var tauri = candidates.Single(item => item.RuleId == "node.tauri-app" &&
        item.WorkingDirectory == "apps/desktop");
    Expect(tauri.Name == "Tauri App" && tauri.Command == "npm" &&
        tauri.Arguments == "run tauri dev" && tauri.Priority == CandidatePriority.Recommended &&
        tauri.Confidence == CandidateConfidence.High &&
        tauri.SourceDescription.Contains("apps/desktop/package.json + apps/desktop/src-tauri/tauri.conf.json"),
        "Tauri app candidate or evidence missing");
    Expect(candidates.Any(item => item.RuleId == "node.package-script" &&
        item.WorkingDirectory == "apps/desktop" && item.Name == "Frontend Only" &&
        item.Arguments == "run dev" && item.Priority == CandidatePriority.Normal),
        "Frontend-only Vite candidate lost");
    Expect(candidates.Count(item => item.RuleId == "node.tauri-app") == 4 &&
        candidates.Any(item => item.WorkingDirectory == "tauri-cli" &&
            item.Command == "pnpm" && item.SupportingEvidence == "@tauri-apps/cli (package.json)") &&
        candidates.Any(item => item.WorkingDirectory == "tauri-config-only" &&
            item.SupportingEvidence == "tauri-config-only/src-tauri/tauri.conf.json") &&
        candidates.Any(item => item.WorkingDirectory == "tauri-cargo" &&
            item.SupportingEvidence == "tauri-cargo/src-tauri/Cargo.toml"),
        "Tauri dependency, config, and Cargo evidence detection");
    Expect(!candidates.Any(item => item.RuleId == "node.tauri-app" &&
        item.WorkingDirectory.StartsWith("tauri-no-", StringComparison.OrdinalIgnoreCase)),
        "Tauri candidate without both a script and project evidence");
    Expect(candidates.Any(item => item.WorkingDirectory == "tauri-no-evidence" &&
        item.Name == "dev"), "Unconfirmed Tauri project was relabeled");
    File.Delete(Path.Combine(root, "apps", "desktop", "src-tauri", "tauri.conf.json"));
    var fallback = discovery.Discover(root);
    Expect(fallback.Any(item => item.Id == tauri.Id &&
        item.SupportingEvidence == "@tauri-apps/cli (package.json)"),
        "Tauri candidate ID changed with supporting evidence");
    Put("apps/desktop/src-tauri/tauri.conf.json", "{}");
    var configOnly = candidates.Single(item => item.RuleId == "node.tauri-app" &&
        item.WorkingDirectory == "tauri-config-only");
    var savedTauri = SavedCommand.FromCandidate(configOnly);
    savedTauri.Arguments = "custom dev arguments";
    File.Delete(Path.Combine(root, "tauri-config-only", "src-tauri", "tauri.conf.json"));
    Expect(CommandDiscovery.SourceMissing(savedTauri, discovery.Discover(root), CommandKind.Run) &&
        savedTauri.Arguments == "custom dev arguments", "Missing Tauri source changed saved command");
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
            .ThenByDescending(item => item.Priority)
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

    if (OperatingSystem.IsWindows())
    {
        var runOne = Path.Combine(root, "run-one");
        var runTwo = Path.Combine(root, "run-two");
        var runFolders = new[] { runOne, runTwo }.Select(path => Path.Combine(path, "apps", "desktop")).ToArray();
        foreach (var folder in runFolders)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "long-run.cmd"),
                "@echo off\r\necho %RUN_MARKER%:%CD%\r\npowershell.exe -NoProfile -Command \"Start-Sleep -Seconds 30\"\r\n");
        }
        var npmAvailable = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Any(folder => File.Exists(Path.Combine(folder, "npm.cmd"))) == true;
        if (npmAvailable) File.WriteAllText(Path.Combine(runFolders[1], "package.json"),
            """{"scripts":{"dev":"node -e \"console.log('npm-dev-ready');setInterval(()=>{},1000)\""}}""");
        var runManager = new CommandRunManager(root, Path.Combine(root, "run-state"));
        SavedCommand RunFor(string folder, string marker) => new()
        {
            Name = "Desktop", Command = Path.Combine(folder, "long-run.cmd"),
            WorkingDirectory = "apps/desktop",
            Environment = [new EnvironmentEntry("RUN_MARKER", marker)]
        };
        var secondCommand = npmAvailable ? new SavedCommand
        {
            Name = "Desktop npm", Command = "npm", Arguments = "run dev",
            WorkingDirectory = "apps/desktop"
        } : RunFor(runFolders[1], "two");
        var runStore = new CommandSettingsStore(root, Path.Combine(root, "run-settings"));
        var runSettings = runStore.Load();
        runSettings.Default.Run = RunFor(runFolders[0], "one");
        runSettings.OverrideFor(runTwo).Run = secondCommand;
        runStore.Save(runSettings);
        var persistedRuns = runStore.Load();
        var runThree = Path.Combine(root, "run-three");
        try
        {
            await runManager.SwitchAsync(runOne, persistedRuns.Effective(runOne, CommandKind.Run)!);
            Expect(runManager.GetState(runOne).IsRunning, "Run command did not stay active");
            var restartedManager = new CommandRunManager(root, Path.Combine(root, "run-state"));
            Expect(restartedManager.GetState(runOne).IsRunning, "Run state was not restored");
            for (var attempt = 0; attempt < 20 && !runManager.ReadRecentLog(runOne).Contains("one:"); attempt++)
                await Task.Delay(100);
            Expect(runManager.ReadRecentLog(runOne).Contains($"one:{runFolders[0]}",
                StringComparison.OrdinalIgnoreCase), "Working directory, environment, or log was not applied");

            try { await runManager.SwitchAsync(runTwo, new SavedCommand
                { Name = "Invalid", Command = "cmd.exe", WorkingDirectory = "missing" }); }
            catch (ArgumentException) { /* Keep the current command after validation fails. */ }
            Expect(runManager.GetState(runOne).IsRunning, "Invalid run stopped the previous worktree");

            try { await runManager.SwitchAsync(runTwo, persistedRuns.Effective(runTwo, CommandKind.Run)!); }
            catch (Exception error)
            {
                throw new Exception($"Run failed: {error.Message} Log: {runManager.ReadRecentLog(runTwo)}", error);
            }
            Expect(!runManager.GetState(runOne).IsRunning && runManager.GetState(runTwo).IsRunning,
                "Switch did not stop the previous worktree");
            Expect(WindowsRunJob.IsRunning(CommandRunManager.JobName(runTwo)),
                "Job disappeared after start");
            if (npmAvailable)
            {
                for (var attempt = 0; attempt < 50 &&
                    !runManager.ReadRecentLog(runTwo).Contains("npm-dev-ready"); attempt++)
                    await Task.Delay(100);
                Expect(runManager.ReadRecentLog(runTwo).Contains("npm-dev-ready"),
                    $"npm run dev did not execute through the command runner: {runManager.ReadRecentLog(runTwo)}");
                Expect(WindowsRunJob.IsRunning(CommandRunManager.JobName(runTwo)), "Job disappeared after npm spawned");
            }
            await restartedManager.StopAsync(runTwo);
            Expect(!runManager.GetState(runTwo).IsRunning, "Stop did not terminate the run command");

            var thirdFolder = Path.Combine(runThree, "apps", "desktop");
            Directory.CreateDirectory(thirdFolder);
            File.WriteAllText(Path.Combine(thirdFolder, "long-run.cmd"),
                "@echo off\r\npowershell.exe -NoProfile -Command \"Start-Sleep -Seconds 60\"\r\n");
            var childStart = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            childStart.ArgumentList.Add(typeof(CommandRunManager).Assembly.Location);
            childStart.ArgumentList.Add("--start-run-fixture");
            childStart.ArgumentList.Add(runThree);
            childStart.ArgumentList.Add(Path.Combine(root, "run-state"));
            using (var child = Process.Start(childStart)!)
            {
                await child.WaitForExitAsync();
                Expect(child.ExitCode == 0, "Fixture start process failed");
            }
            Expect(!runManager.GetState(runThree).IsRunning,
                "Run from previous process survived Dev Manager exit");

            var quick = new SavedCommand
                { Name = "Quick", Command = "cmd.exe", Arguments = "/d /c exit 0", WorkingDirectory = "apps/desktop" };
            await runManager.SwitchAsync(runTwo, quick);
            Expect(!runManager.GetState(runTwo).IsRunning, "Completed command is still marked running");
            await runManager.SwitchAsync(runTwo, quick);
        }
        finally
        {
            foreach (var worktreePath in new[] { runOne, runTwo, runThree })
                if (runManager.GetState(worktreePath).IsRunning) await runManager.StopAsync(worktreePath);
        }
    }

    var legacyDirectory = Path.Combine(root, "legacy-settings");
    var legacyStore = new CommandSettingsStore(root, legacyDirectory);
    var legacyPath = Path.Combine(legacyDirectory, Directory.GetFiles(Path.Combine(root, "settings"), "*.json")
        .Select(Path.GetFileName).Single()!);
    Directory.CreateDirectory(legacyDirectory);
    File.WriteAllText(legacyPath, System.Text.Json.JsonSerializer.Serialize(new
    {
        Version = 1, RepositoryPath = root,
        Default = new CommandPair { Run = new SavedCommand
            { Name = "Old API", Command = "dotnet", Arguments = "run" } },
        Worktrees = new Dictionary<string, CommandPair>
        {
            [Path.Combine(root, "legacy-worktree")] = new CommandPair
            {
                Run = new SavedCommand { Name = "Old branch", Command = "npm",
                    Arguments = "run dev" }
            }
        }
    }));
    var migrated = legacyStore.Load();
    Expect(migrated.DefaultProfiles.Single().Items.Single().Command == "dotnet" &&
        migrated.Default.Run is null &&
        migrated.EffectiveProfiles(Path.Combine(root, "legacy-worktree"))
            .Single().Items.Single().Command == "npm", "Legacy Run migration");
    legacyStore.Save(migrated);
    Expect(File.Exists(legacyPath + ".v1.bak") && legacyStore.Load().Version == 2,
        "Legacy backup or version upgrade");

    var profile = new RunProfile { Name = "Mobile Development" };
    var step = new RunItem { Name = "Migration", Kind = RunItemKind.ShellScript,
        Lifecycle = RunLifecycle.OneShot, Script = "Write-Output 'step-out'; [Console]::Error.WriteLine('step-err')" };
    profile.Items.Add(step);
    var server = new RunItem { Name = "API", Kind = RunItemKind.ShellScript,
        Script = "Start-Sleep -Seconds 30", DependsOn = [step.Id] };
    profile.Items.Add(server);
    var app = new RunItem { Name = "Flutter", Kind = RunItemKind.ShellScript,
        Script = "Write-Output 'flutter-started'; Start-Sleep -Seconds 30", DependsOn = [server.Id] };
    profile.Items.Add(app);
    var snapshots = new RepositoryCommandSettings { RepositoryPath = root,
        DefaultProfiles = [profile] };
    var branchRoot = Path.Combine(root, "profile-branch");
    Directory.CreateDirectory(branchRoot);
    var copy = snapshots.CopyDefaultProfilesFor(branchRoot);
    copy[0].Items[0].Name = "Branch migration";
    Expect(snapshots.DefaultProfiles[0].Items[0].Name == "Migration" &&
        snapshots.EffectiveProfiles(branchRoot)[0].Items[0].Name == "Branch migration",
        "Profile override must be a snapshot");
    snapshots.ResetProfileOverride(branchRoot);
    Expect(snapshots.EffectiveProfiles(branchRoot)[0].Items[0].Name == "Migration",
        "Profile override reset");
    var cycle = profile.Copy();
    cycle.Items[0].DependsOn.Add(cycle.Items[2].Id);
    try { RunProfileValidator.Validate(cycle, root); throw new Exception("Cycle accepted"); }
    catch (ArgumentException) { }
    var external = new RunProfile { Name = "External", Items = [new RunItem
    {
        Name = "Outside", Command = "cmd.exe", WorkingDirectory = Path.GetTempPath()
    }] };
    Expect(RunProfileValidator.Validate(external, root).Count == 1,
        "External path should warn, not fail");

    if (OperatingSystem.IsWindows())
    {
        using var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        Expect(ListeningPorts.Owners(port).Contains(Environment.ProcessId),
            "Listening port PID lookup");
        reserve.Stop();
        server.Script = $"$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, {port}); $listener.Start(); Write-Output 'api-ready'; Start-Sleep -Seconds 30";
        server.Readiness = new RunReadiness { Type = RunReadinessKind.Port, Port = port,
            TimeoutSeconds = 10 };
        var runner = new RunProfileManager(root, Path.Combine(root, "profile-state"));
        try
        {
            await runner.RunAllAsync(root, profile);
            Expect(runner.GetItemState(root, profile, step).Status == RunItemStatus.Completed,
                "One-shot completion missing");
            Expect(runner.GetItemState(root, profile, server).Status == RunItemStatus.Running &&
                runner.GetItemState(root, profile, app).Status == RunItemStatus.Running &&
                runner.GetProfileStatus(root, profile) == RunProfileStatus.Running,
                "Port dependency did not start dependent process");
            try
            {
                runner.ValidateSettingsTransition(new RepositoryCommandSettings
                    { RepositoryPath = root });
                throw new Exception("Active profile removal accepted");
            }
            catch (InvalidOperationException) { }
            Expect(runner.ReadLog(root, profile, step, false).Contains("step-out") &&
                runner.ReadLog(root, profile, step, true).Contains("step-err"),
                "stdout and stderr were not separated");
            for (var attempt = 0; attempt < 30 &&
                !runner.ReadLog(root, profile, app, false).Contains("flutter-started"); attempt++)
                await Task.Delay(100);
            Expect(runner.ReadLog(root, profile, app, false).Contains("flutter-started"),
                "Dependent log missing");
            try { await runner.RunAllAsync(root, profile); throw new Exception("Duplicate run accepted"); }
            catch (InvalidOperationException) { }
            var conflict = new RunProfile { Name = "Other profile", Items = [new RunItem
            {
                Name = "Other API", Kind = RunItemKind.ShellScript, Script = server.Script,
                Readiness = server.Readiness.Copy()
            }] };
            Expect(runner.FindPortConflicts(root, conflict).Any(value =>
                value.Contains("Mobile Development / API")), "Known port conflict missing");
            await runner.RunAllAsync(root, conflict);
            Expect(runner.GetItemState(root, conflict, conflict.Items[0]).Status == RunItemStatus.Failed,
                "Run Anyway must not accept another Job's port");
            var parallel = new RunProfile { Name = "Parallel", Items = [new RunItem
            {
                Name = "Worker", Kind = RunItemKind.ShellScript,
                Script = "Start-Sleep -Seconds 30"
            }] };
            await runner.RunAllAsync(root, parallel);
            Expect(runner.HasActiveItems(root, profile) && runner.HasActiveItems(root, parallel),
                "Different profiles could not run simultaneously");
            await runner.StopAllAsync(root, parallel);
            await runner.StopItemAsync(root, profile, server.Id);
            Expect(ListeningPorts.Owners(port).Count == 0 &&
                runner.GetItemState(root, profile, app).Status == RunItemStatus.Running &&
                runner.GetProfileStatus(root, profile) == RunProfileStatus.PartiallyRunning,
                "Stopping a dependency stopped its dependent");
            await runner.StopAllAsync(root, profile);
            Expect(!runner.HasActiveItems(root, profile), "Stop All left a process running");
            await runner.RestartAllAsync(root, profile);
            Expect(runner.GetItemState(root, profile, step).Status == RunItemStatus.Completed,
                "Restart All did not rerun the completed step");
        }
        finally { await runner.StopAllAsync(root, profile); }
        var failed = new RunProfile { Name = "Failed steps", Items = [new RunItem
        {
            Name = "Fail", Kind = RunItemKind.ShellScript, Lifecycle = RunLifecycle.OneShot,
            Script = "exit 5"
        }] };
        failed.Items.Add(new RunItem { Name = "Blocked", Kind = RunItemKind.ShellScript,
            Script = "Start-Sleep -Seconds 10", DependsOn = [failed.Items[0].Id] });
        await runner.RunAllAsync(root, failed);
        Expect(runner.GetItemState(root, failed, failed.Items[0]).Status == RunItemStatus.Failed &&
            runner.GetItemState(root, failed, failed.Items[1]).Reason?.Contains("선행") == true &&
            runner.GetProfileStatus(root, failed) == RunProfileStatus.Failed,
            "Failed step did not block dependent");

        Put("scripts/check.ps1", "Write-Output file-powershell");
        Put("scripts/check.cmd", "@echo off\r\necho file-cmd\r\n");
        var scripts = new RunProfile { Name = "Shell types", Items =
        [
            new RunItem { Name = "cmd inline", Kind = RunItemKind.ShellScript,
                Lifecycle = RunLifecycle.OneShot, Shell = RunShell.Cmd,
                Script = "@echo off\r\necho cmd-out\r\necho cmd-err 1>&2" },
            new RunItem { Name = "PowerShell file", Kind = RunItemKind.ShellScript,
                Lifecycle = RunLifecycle.OneShot, ScriptSource = RunScriptSource.File,
                Script = "scripts/check.ps1", Shell = RunShell.Cmd },
            new RunItem { Name = "cmd file", Kind = RunItemKind.ShellScript,
                Lifecycle = RunLifecycle.OneShot, ScriptSource = RunScriptSource.File,
                Script = "scripts/check.cmd", Shell = RunShell.PowerShell }
        ] };
        await runner.RunAllAsync(root, scripts);
        Expect(scripts.Items.All(item => runner.GetItemState(root, scripts, item).Status ==
            RunItemStatus.Completed), "Shell execution did not complete");
        Expect(runner.ReadLog(root, scripts, scripts.Items[0], false).Contains("cmd-out") &&
            runner.ReadLog(root, scripts, scripts.Items[0], true).Contains("cmd-err") &&
            runner.ReadLog(root, scripts, scripts.Items[1], false).Contains("file-powershell") &&
            runner.ReadLog(root, scripts, scripts.Items[2], false).Contains("file-cmd"),
            "Shell selection or output capture failed");
        var processFolder = Path.Combine(root, "apps", "mobile");
        Directory.CreateDirectory(processFolder);
        var processProfile = new RunProfile { Name = "Process", Items = [new RunItem
        {
            Name = "cwd/env", Command = "cmd.exe",
            Arguments = "/d /c echo %RUN_MARKER%:%CD%",
            WorkingDirectory = "apps/mobile", Lifecycle = RunLifecycle.OneShot,
            Environment = [new EnvironmentEntry("RUN_MARKER", "marker")]
        }] };
        await runner.RunAllAsync(root, processProfile);
        Expect(runner.GetItemState(root, processProfile, processProfile.Items[0]).Status ==
            RunItemStatus.Completed && runner.ReadLog(root, processProfile,
                processProfile.Items[0], false).Contains($"marker:{processFolder}",
                StringComparison.OrdinalIgnoreCase), "Process working directory or environment failed");

        if (Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Any(path => File.Exists(Path.Combine(path, "npm.cmd"))) == true)
        {
            Put("apps/mobile/package.json",
                """{"scripts":{"dev":"node -e \"console.log('profile-npm-ready');setInterval(()=>{},1000)\""}}""");
            var npmProfile = new RunProfile { Name = "npm process tree", Items = [new RunItem
            {
                Name = "npm", Command = "npm", Arguments = "run dev",
                WorkingDirectory = "apps/mobile"
            }] };
            await runner.RunAllAsync(root, npmProfile);
            for (var attempt = 0; attempt < 50 &&
                !runner.ReadLog(root, npmProfile, npmProfile.Items[0], false)
                    .Contains("profile-npm-ready"); attempt++) await Task.Delay(100);
            Expect(runner.HasActiveItems(root, npmProfile) &&
                runner.ReadLog(root, npmProfile, npmProfile.Items[0], false)
                    .Contains("profile-npm-ready"), "npm child did not stay under profile run");
            await runner.StopAllAsync(root, npmProfile);
            Expect(!runner.HasActiveItems(root, npmProfile), "npm process tree was not stopped");
        }

        using var externalListener = new TcpListener(IPAddress.Loopback, 0);
        externalListener.Start();
        var externalPort = ((IPEndPoint)externalListener.LocalEndpoint).Port;
        var externalProfile = new RunProfile { Name = "Docker-like", Items = [new RunItem
        {
            Name = "External listener", Kind = RunItemKind.ShellScript,
            Script = "Start-Sleep -Seconds 30",
            Readiness = new RunReadiness { Type = RunReadinessKind.Port,
                Port = externalPort, TimeoutSeconds = 2, AllowExternalPort = true }
        }] };
        await runner.RunAllAsync(root, externalProfile);
        Expect(runner.GetItemState(root, externalProfile, externalProfile.Items[0]).Status ==
            RunItemStatus.Running, "External port option did not become ready");
        await runner.StopAllAsync(root, externalProfile);
        externalListener.Stop();
        var timeoutProfile = new RunProfile { Name = "Timeout", Items = [new RunItem
        {
            Name = "Never listens", Kind = RunItemKind.ShellScript,
            Script = "Start-Sleep -Seconds 30",
            Readiness = new RunReadiness { Type = RunReadinessKind.Port,
                Port = externalPort, TimeoutSeconds = 1 }
        }] };
        await runner.RunAllAsync(root, timeoutProfile);
        Expect(runner.GetItemState(root, timeoutProfile, timeoutProfile.Items[0]).Status ==
            RunItemStatus.Failed, "Port timeout did not fail");
    }

    Console.WriteLine($"PASS: {candidates.Count} candidates, profile migration/snapshot/dependencies/jobs/logs");
}
finally
{
    var basePath = Path.Combine(Path.GetTempPath(), "NebbCommandTests") + Path.DirectorySeparatorChar;
    if (root.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
