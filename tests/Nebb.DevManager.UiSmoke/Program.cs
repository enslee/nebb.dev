using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nebb.DevManager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "NebbProfileUiSmoke", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "branch");
        var storage = Path.Combine(root, "settings");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../.git/worktrees/branch");
        Directory.CreateDirectory(Path.Combine(root, "apps", "desktop", "src-tauri"));
        File.WriteAllText(Path.Combine(root, "apps", "desktop", "package.json"),
            """{"scripts":{"dev":"vite","tauri":"tauri"},"devDependencies":{"@tauri-apps/cli":"2.0.0"}}""");
        File.WriteAllText(Path.Combine(root, "apps", "desktop", "src-tauri", "tauri.conf.json"), "{}");
        Directory.CreateDirectory(Path.Combine(worktree, "apps", "desktop", "src-tauri"));
        File.WriteAllText(Path.Combine(worktree, "apps", "desktop", "package.json"),
            """{"scripts":{"dev":"vite","tauri":"tauri"},"devDependencies":{"@tauri-apps/cli":"2.0.0"}}""");
        File.WriteAllText(Path.Combine(worktree, "apps", "desktop", "src-tauri", "tauri.conf.json"), "{}");
        File.WriteAllText(Path.Combine(root, "package.json"), """{"scripts":{"test":"vitest"}}""");
        Directory.CreateDirectory(Path.Combine(root, "server"));
        File.WriteAllText(Path.Combine(root, "server", "compose.yaml"), "services:\n  db:\n    image: postgres\n");
        try
        {
            var store = new CommandSettingsStore(root, storage);
            RunProfileDialog(root, root, storage, dialog =>
            {
                Click(dialog.AddProfileButton);
                dialog.ProfileNameBox.Text = "Mobile Development";
                dialog.CandidateChoice.SelectedItem = dialog.CandidateChoice.Items
                    .OfType<CommandCandidate>().Single(item => item.Name == "Tauri App");
                Click(dialog.AddCandidateButton);
                if (dialog.CommandBox.Text != "npm" || dialog.ArgumentsBox.Text != "run tauri dev" ||
                    dialog.WorkingDirectoryBox.Text != "apps/desktop")
                    throw new Exception("Run candidate was not added to profile");
                Click(dialog.AddScriptButton);
                dialog.NameBox.Text = "Migration";
                dialog.ScriptBox.Text = "Write-Output ready";
                dialog.DependsList.SelectedIndex = 0;
                Click(dialog.SaveButton);
            });
            var saved = store.Load().DefaultProfiles.Single();
            if (saved.Name != "Mobile Development" || saved.Items.Count != 2 ||
                saved.Items[1].DependsOn.Single() != saved.Items[0].Id ||
                saved.Items[1].Kind != RunItemKind.ShellScript)
                throw new Exception("Profile editor did not persist settings");

            RunProfileDialog(root, worktree, storage, dialog =>
            {
                dialog.ScopeChoice.SelectedIndex = 1;
                if (!dialog.ScopeStateText.Text.Contains("상속"))
                    throw new Exception("Inheritance state missing");
                Click(dialog.CopyButton);
                dialog.ProfileNameBox.Text = "Branch Development";
                Click(dialog.SaveButton);
            });
            if (store.Load().EffectiveProfiles(worktree).Single().Name != "Branch Development" ||
                store.Load().DefaultProfiles.Single().Name != "Mobile Development")
                throw new Exception("Worktree snapshot override failed");
            RunProfileDialog(root, worktree, storage, dialog =>
            {
                dialog.ScopeChoice.SelectedIndex = 1;
                Click(dialog.ResetButton);
                Click(dialog.SaveButton);
            });
            if (store.Load().EffectiveProfiles(worktree).Single().Name != "Mobile Development")
                throw new Exception("Worktree override reset failed");

            RunCommandDialog(root, root, storage, dialog =>
            {
                SelectCandidate(dialog, item => item.Kind == CommandKind.Test);
                Click(dialog.ChooseCandidateButton);
                dialog.CommandBox.Text = "test-custom";
                Click(dialog.SaveButton);
            });
            if (store.Load().Default.Test?.Command != "test-custom")
                throw new Exception("Test command editor regressed");
            RunCommandDialog(root, root, storage, dialog =>
            {
                SelectCandidate(dialog, item => item.Kind == CommandKind.Service);
                Click(dialog.ChooseCandidateButton);
                Click(dialog.SaveButton);
            });
            if (store.Load().DefaultServices.Count != 1)
                throw new Exception("Service editor regressed");
            var conflictDialog = new RunPortConflictDialog(["Port 5000: Mobile Development / API"])
            {
                ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000
            };
            RunDialog(conflictDialog, () => conflictDialog.IsLoaded, () =>
            {
                if (!conflictDialog.ConflictText.Text.Contains("Mobile Development / API"))
                    throw new Exception("Port conflict details missing");
                Click(conflictDialog.RunAnywayButton);
            });
            Console.WriteLine("PASS: WPF profile candidate/editor/snapshot and test/service settings");
        }
        finally
        {
            var basePath = Path.Combine(Path.GetTempPath(), "NebbProfileUiSmoke") + Path.DirectorySeparatorChar;
            if (root.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void RunProfileDialog(string root, string worktree, string storage,
        Action<RunProfilesDialog> action)
    {
        var dialog = new RunProfilesDialog(root, worktree, storage)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        RunDialog(dialog, () => dialog.CandidateChoice.Items.Count > 0, () => action(dialog));
    }

    private static void RunCommandDialog(string root, string worktree, string storage,
        Action<CommandSettingsDialog> action)
    {
        var dialog = new CommandSettingsDialog(root, worktree, storage)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        RunDialog(dialog, () => dialog.RefreshCandidatesButton.IsEnabled &&
            dialog.CandidateTree.Items.Count > 0, () => action(dialog));
    }

    private static void RunDialog(Window dialog, Func<bool> ready, Action action)
    {
        Exception? failure = null;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (!ready())
            {
                if (DateTime.UtcNow - started < TimeSpan.FromSeconds(10)) return;
                failure = new TimeoutException("Candidate scan did not finish");
                timer.Stop(); dialog.Close(); return;
            }
            timer.Stop();
            try { action(); }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        timer.Start();
        dialog.ShowDialog();
        timer.Stop();
        if (failure is not null) throw failure;
    }

    private static void SelectCandidate(CommandSettingsDialog dialog,
        Func<CommandCandidate, bool> matches)
    {
        TreeViewItem? Find(IEnumerable<TreeViewItem> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Tag is CommandCandidate candidate && matches(candidate)) return node;
                var child = Find(node.Items.OfType<TreeViewItem>());
                if (child is not null) return child;
            }
            return null;
        }
        var item = Find(dialog.CandidateTree.Items.OfType<TreeViewItem>())
            ?? throw new Exception("Candidate tree item missing");
        item.IsSelected = true;
    }
}
