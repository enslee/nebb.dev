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
var root = Path.Combine(Path.GetTempPath(), "NebbCommandUiSmoke", Guid.NewGuid().ToString("N"));
var worktree = Path.Combine(root, "branch");
var storage = Path.Combine(root, "settings");
Directory.CreateDirectory(worktree);
File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../.git/worktrees/branch");
File.WriteAllText(Path.Combine(root, "package.json"), """{"scripts":{"dev":"vite"}}""");
File.WriteAllText(Path.Combine(worktree, "package.json"), """{"scripts":{"dev":"vite"}}""");
Directory.CreateDirectory(Path.Combine(worktree, "server"));
File.WriteAllText(Path.Combine(worktree, "server", "docker-compose.yml"),
    "services:\n  db:\n    image: postgres\n  minio:\n    image: minio\n");
Directory.CreateDirectory(Path.Combine(root, "apps", "desktop"));
File.WriteAllText(Path.Combine(root, "apps", "desktop", "package.json"),
    """{"scripts":{"dev":"vite"}}""");
Directory.CreateDirectory(Path.Combine(root, "server"));
File.WriteAllText(Path.Combine(root, "server", "docker-compose.yml"),
    "services:\n  db:\n    image: postgres\n  minio:\n    image: minio\n");

try
{
    var store = new CommandSettingsStore(root, storage);
    RunDialog(root, root, storage, dialog =>
    {
        SelectCandidate(dialog, item => item.SourceFile == "package.json" && item.Kind == CommandKind.Run);
        Click(dialog.ChooseCandidateButton);
        dialog.CommandBox.Text = "npm-custom";
        dialog.EnvironmentBox.Text = "PORT=3000";
        dialog.KindChoice.SelectedIndex = 1;
        Click(dialog.ManualButton);
        dialog.NameBox.Text = "Manual test";
        dialog.CommandBox.Text = "custom-test";
        Click(dialog.SaveButton);
    });
    if (store.Load().Default.Run?.Command != "npm-custom") throw new Exception("Default UI save failed");
    if (store.Load().Default.Test?.Command != "custom-test") throw new Exception("Manual test UI save failed");

    RunDialog(root, root, storage, dialog =>
    {
        var desktop = dialog.CandidateTree.Items.OfType<TreeViewItem>()
            .FirstOrDefault(item => item.Header.ToString()!.Contains("apps/desktop"));
        var server = dialog.CandidateTree.Items.OfType<TreeViewItem>()
            .FirstOrDefault(item => item.Header.ToString()!.Contains("server"));
        if (desktop is null || server is null || !server.Items.OfType<TreeViewItem>()
                .Any(item => item.Header.ToString() == "Services"))
            throw new Exception("Project or Services grouping missing");
        SelectCandidate(dialog, item => item.Kind == CommandKind.Service && item.Name == "Compose db");
        Click(dialog.ChooseCandidateButton);
        SelectCandidate(dialog, item => item.Kind == CommandKind.Service && item.Name == "Compose minio");
        Click(dialog.ChooseCandidateButton);
        if (dialog.SelectedServicesList.Items.Count != 2) throw new Exception("Multiple services not selected");
        dialog.SelectedServicesList.SelectedIndex = 0;
        dialog.ArgumentsBox.Text = "compose up db --custom";
        Click(dialog.SaveButton);
    });
    if (store.Load().DefaultServices.Count != 2 ||
        store.Load().DefaultServices[0].Arguments != "compose up db --custom")
        throw new Exception("Service UI save failed");

    RunDialog(root, worktree, storage, dialog =>
    {
        dialog.ScopeChoice.SelectedIndex = 1;
        Click(dialog.CopyDefaultButton);
        dialog.CommandBox.Text = "npm-branch";
        Click(dialog.SaveButton);
    });
    if (store.Load().Effective(worktree, CommandKind.Run)?.Command != "npm-branch")
        throw new Exception("Override UI save failed");

    RunDialog(root, worktree, storage, dialog =>
    {
        dialog.ScopeChoice.SelectedIndex = 1;
        Click(dialog.ClearButton);
        Click(dialog.SaveButton);
    });
    if (store.Load().Effective(worktree, CommandKind.Run)?.Command != "npm-custom")
        throw new Exception("Override UI reset failed");

    RunDialog(root, worktree, storage, dialog =>
    {
        dialog.KindChoice.SelectedIndex = 2;
        dialog.ScopeChoice.SelectedIndex = 1;
        Click(dialog.CopyDefaultButton);
        if (dialog.SelectedServicesList.Items.Count != 2) throw new Exception("Service defaults not copied");
        dialog.SelectedServicesList.SelectedIndex = 0;
        Click(dialog.ClearButton);
        Click(dialog.SaveButton);
    });
    if (store.Load().EffectiveServices(worktree).Count != 1 || store.Load().DefaultServices.Count != 2)
        throw new Exception("Service override UI save failed");

    RunDialog(root, worktree, storage, dialog =>
    {
        dialog.KindChoice.SelectedIndex = 2;
        dialog.ScopeChoice.SelectedIndex = 1;
        Click(dialog.ResetServiceOverrideButton);
        Click(dialog.SaveButton);
    });
    if (store.Load().EffectiveServices(worktree).Count != 2)
        throw new Exception("Service override UI reset failed");

    File.Delete(Path.Combine(root, "package.json"));
    File.Delete(Path.Combine(root, "server", "docker-compose.yml"));
    RunDialog(root, root, storage, dialog =>
    {
        if (!dialog.StateText.Text.Contains("원본 후보 없음", StringComparison.Ordinal))
            throw new Exception("Missing source message absent: " + dialog.StateText.Text +
                " / " + store.Load().Default.Run?.CandidateId);
        dialog.KindChoice.SelectedIndex = 2;
        if (!dialog.StateText.Text.Contains("원본 후보 없음", StringComparison.Ordinal))
            throw new Exception("Missing service source message absent");
        Click(dialog.SaveButton);
    });
    if (store.Load().Default.Run?.Command != "npm-custom")
        throw new Exception("Missing source removed saved command");
    Console.WriteLine("PASS: WPF project groups, services, selection, edit, override, reset, stale source");
}
finally
{
    var basePath = Path.Combine(Path.GetTempPath(), "NebbCommandUiSmoke") + Path.DirectorySeparatorChar;
    if (root.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
    }

static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

static void SelectCandidate(CommandSettingsDialog dialog, Func<CommandCandidate, bool> matches)
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

static void RunDialog(string root, string worktree, string storage,
    Action<CommandSettingsDialog> action)
{
    var dialog = new CommandSettingsDialog(root, worktree, storage)
    {
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000
    };
    Exception? failure = null;
    var started = DateTime.UtcNow;
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
    timer.Tick += (_, _) =>
    {
        if (!dialog.RefreshCandidatesButton.IsEnabled ||
            (dialog.CandidateTree.Items.Count == 0 && File.Exists(Path.Combine(root, "package.json"))))
        {
            if (DateTime.UtcNow - started < TimeSpan.FromSeconds(10)) return;
            failure = new TimeoutException("Candidate scan did not finish");
            timer.Stop();
            dialog.Close();
            return;
        }
        timer.Stop();
        try { action(dialog); }
        catch (Exception error) { failure = error; dialog.Close(); }
    };
    timer.Start();
    dialog.ShowDialog();
    timer.Stop();
    if (failure is not null) throw failure;
}
}
