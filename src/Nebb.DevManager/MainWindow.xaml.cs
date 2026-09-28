using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nebb.DevManager;

public partial class MainWindow : Window
{
    private enum GitAction { Commit, Merge, Push }

    private sealed class WorktreeRow : INotifyPropertyChanged
    {
        private string? activityStatus;

        public WorktreeRow(Worktree worktree, WorktreeState state, GitWorktreeState gitState)
        {
            Worktree = worktree;
            State = state;
            GitState = gitState;
        }

        public Worktree Worktree { get; }
        public WorktreeState State { get; }
        public GitWorktreeState GitState { get; }
        public string Branch => Worktree.Branch;
        public string Path => Worktree.Path;
        public string ServerStatus => activityStatus ?? (State.Status == "시작 중" ? "전환 중" : State.Status);
        public Brush IndicatorBrush => activityStatus is not null || State.Status == "시작 중" ? Brushes.DarkOrange :
            State.HasProcesses ? Brushes.ForestGreen : Brushes.Gray;
        public string IndicatorText => "●";
        public string Commit => GitState.Commit;
        public string CommitTime => GitState.CommitTime;
        public string WorkingTree => GitState.WorkingTree;
        public string PushStatus => GitState.PushStatus;
        public string MainMergeStatus => GitState.MainMergeStatus;
        public string WebAddress => State.WebPort is int port ? $"http://127.0.0.1:{port}" : "—";
        public string ApiAddress => State.ApiPort is int port ? $"http://127.0.0.1:{port}" : "—";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetActivity(string? value)
        {
            if (activityStatus == value) return;
            activityStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndicatorBrush)));
        }
    }

    private readonly DevServerManager manager;
    private readonly GitStatusService git;
    private readonly GitActionService gitActions;
    private readonly ObservableCollection<WorktreeRow> rows = [];
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool busy;

    public MainWindow(string repositoryPath)
    {
        InitializeComponent();
        manager = new DevServerManager(repositoryPath);
        git = new GitStatusService(manager.RepositoryPath);
        gitActions = new GitActionService(manager.RepositoryPath);
        WorktreeGrid.ItemsSource = rows;
        Title += $" — {manager.RepositoryPath}";
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            refreshTimer.Start();
        };
        Closed += (_, _) => refreshTimer.Stop();
        UpdateSelection();
    }

    private async Task RefreshAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            var selectedPath = (WorktreeGrid.SelectedItem as WorktreeRow)?.Path;
            var worktrees = await manager.ListWorktreesAsync();
            var snapshotTask = manager.InspectAsync();
            var gitTask = git.GetStatesAsync(worktrees);
            await Task.WhenAll(snapshotTask, gitTask);
            var snapshot = await snapshotTask;
            var gitStates = await gitTask;
            rows.Clear();
            for (var index = 0; index < worktrees.Count; index++)
                rows.Add(new WorktreeRow(worktrees[index], manager.GetState(worktrees[index], snapshot),
                    gitStates[index]));
            WorktreeGrid.SelectedItem = rows.FirstOrDefault(row =>
                row.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
                ?? rows.FirstOrDefault();
            UpdateSelection();
        }
        catch (Exception error)
        {
            DetailsText.Text = $"상태 확인 실패: {error.Message}";
        }
        finally
        {
            busy = false;
            UpdateSelection();
        }
    }

    private async Task<bool> RunActionAsync(WorktreeRow selected,
        Func<Worktree, ProcessSnapshot, Task> action, string? activityStatus = null)
    {
        if (busy) return false;
        busy = true;
        selected.SetActivity(activityStatus);
        ToggleButtons(false);
        UpdateSelection();
        try
        {
            var snapshot = await manager.InspectAsync();
            try
            {
                await action(selected.Worktree, snapshot);
            }
            catch (PixPeekPortConflictException conflict)
            {
                if (new PortConflictDialog(conflict) { Owner = this }.ShowDialog() != true)
                    return false;
                await manager.TerminateConflictingPixPeekAsync(conflict);
                await action(selected.Worktree, await manager.InspectAsync());
            }
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "PixPeek Dev Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            busy = false;
            await RefreshAsync();
            selected.SetActivity(null);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true;
        ToggleButtons(false);
        try
        {
            await git.FetchOriginAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"원격 Git 정보를 갱신하지 못했습니다: {error.Message}",
                "PixPeek Dev Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy = false;
            await RefreshAsync();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeGrid.SelectedItem is WorktreeRow row) await StartRowAsync(row);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeGrid.SelectedItem is WorktreeRow row) await StopRowAsync(row);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeGrid.SelectedItem is WorktreeRow row) OpenRow(row);
    }

    private async Task StartRowAsync(WorktreeRow row)
    {
        if (!await RunActionAsync(row, manager.StartAsync, activityStatus: "전환 중")) return;
        try { OpenPwa(); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "PixPeek Dev Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StopRowAsync(WorktreeRow row) =>
        await RunActionAsync(row, (worktree, _) => manager.StopAsync(worktree));

    private async Task RestartRowAsync(WorktreeRow row) =>
        await RunActionAsync(row, manager.RebuildAndRestartAsync, activityStatus: "재시작 중");

    private void OpenRow(WorktreeRow row)
    {
        if (busy || row.State.WebPort is not int port) return;
        if (row.State.Managed && port == DevServerManager.PwaPort) OpenPwa();
        else Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true });
    }

    private void OpenPwa()
    {
        if (ChromePwaLauncher.Open()) return;
        MessageBox.Show(this,
            "PixPeek을 Chrome에서 열었습니다. 주소창의 설치 아이콘으로 앱을 한 번 설치하면 다음부터 실행 버튼이 설치된 PWA를 엽니다.",
            "PixPeek PWA 설치", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void WorktreeGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        var cell = WorktreeGrid.CurrentCell;
        if (cell.Item is not WorktreeRow || cell.Column is null) return;
        var value = cell.Column.OnCopyingCellClipboardContent(cell.Item)?.ToString();
        if (value is null) return;
        try { Clipboard.SetText(value); }
        catch (ExternalException)
        {
            MessageBox.Show(this, "클립보드를 사용 중입니다. 잠시 후 다시 시도하세요.",
                "PixPeek Dev Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void WorktreeRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: WorktreeRow row }) WorktreeGrid.SelectedItem = row;
    }

    private void WorktreeGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.PreviewMouseRightButtonDown -= WorktreeRow_PreviewMouseRightButtonDown;
        e.Row.PreviewMouseRightButtonDown += WorktreeRow_PreviewMouseRightButtonDown;
        var menu = new ContextMenu { DataContext = e.Row.Item };
        menu.Opened += WorktreeContextMenu_Opened;
        var switchItem = new MenuItem { Header = "전환" };
        switchItem.Click += ContextSwitch_Click;
        menu.Items.Add(switchItem);
        var restartItem = new MenuItem { Header = "리빌드 후 재시작" };
        restartItem.Click += ContextRestart_Click;
        menu.Items.Add(restartItem);
        var stopItem = new MenuItem { Header = "종료" };
        stopItem.Click += ContextStop_Click;
        menu.Items.Add(stopItem);
        var openItem = new MenuItem { Header = "웹 열기" };
        openItem.Click += ContextOpen_Click;
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        var commitItem = new MenuItem { Header = "커밋" };
        commitItem.Click += ContextCommit_Click;
        menu.Items.Add(commitItem);
        var mergeItem = new MenuItem { Header = "병합" };
        mergeItem.Click += ContextMerge_Click;
        menu.Items.Add(mergeItem);
        var pushItem = new MenuItem { Header = "푸시" };
        pushItem.Click += ContextPush_Click;
        menu.Items.Add(pushItem);
        e.Row.ContextMenu = menu;
    }

    private void WorktreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not WorktreeRow row) return;
        ((MenuItem)menu.Items[0]).IsEnabled = !busy;
        ((MenuItem)menu.Items[1]).IsEnabled = !busy && CanRestart(row);
        ((MenuItem)menu.Items[2]).IsEnabled = !busy && row.State.HasProcesses;
        ((MenuItem)menu.Items[3]).IsEnabled = !busy && CanOpen(row);
        ((MenuItem)menu.Items[5]).IsEnabled = !busy && CanCommit(row);
        ((MenuItem)menu.Items[6]).IsEnabled = !busy && CanMerge(row);
        ((MenuItem)menu.Items[7]).IsEnabled = !busy && CanPush(row);
    }

    private async void ContextSwitch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is WorktreeRow row) await StartRowAsync(row);
    }

    private async void ContextStop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is WorktreeRow row) await StopRowAsync(row);
    }

    private async void ContextRestart_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is WorktreeRow row) await RestartRowAsync(row);
    }

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is WorktreeRow row) OpenRow(row);
    }

    private async void ContextCommit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is WorktreeRow row)
            await RunGitActionAsync(row, GitAction.Commit);
    }

    private async void ContextMerge_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is WorktreeRow row)
            await RunGitActionAsync(row, GitAction.Merge);
    }

    private async void ContextPush_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is WorktreeRow row)
            await RunGitActionAsync(row, GitAction.Push);
    }

    private async Task RunGitActionAsync(WorktreeRow row, GitAction action)
    {
        if (busy) return;
        busy = true;
        ToggleButtons(false);
        DetailsText.Text = $"{row.Branch} Git 작업 준비 중...";
        var committed = false;
        try
        {
            Worktree? mainWorktree = null;
            if (action == GitAction.Merge)
            {
                mainWorktree = (await manager.ListWorktreesAsync())
                    .FirstOrDefault(item => item.Branch == "main") ??
                    throw new InvalidOperationException("main 워크트리가 없습니다. 원래 저장소를 main으로 전환하세요.");
                await gitActions.EnsureMergeReadyAsync(row.Worktree, mainWorktree);
            }

            var changes = await gitActions.GetPendingChangesAsync(row.Worktree);
            if (action == GitAction.Commit && changes.Length == 0)
                throw new InvalidOperationException("커밋할 변경 사항이 없습니다.");

            if (changes.Length != 0)
            {
                if (row.Branch == "main")
                    throw new InvalidOperationException("main에서 직접 커밋할 수 없습니다.");
                var actionName = action switch
                {
                    GitAction.Merge => "병합",
                    GitAction.Push => "푸시",
                    _ => "커밋"
                };
                var dialog = new GitCommitDialog(row.Branch, actionName, changes) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                await gitActions.CommitAsync(row.Worktree, changes, dialog.CommitMessage);
                committed = true;
            }
            else if (action != GitAction.Commit)
            {
                var prompt = action == GitAction.Merge
                    ? $"{row.Branch}를 main에 병합하고 작업 브랜치와 main을 origin에 푸시합니다. 계속할까요?"
                    : $"{row.Branch}를 origin에 푸시합니다. 계속할까요?";
                if (MessageBox.Show(this, prompt, "Git 작업 확인", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }

            switch (action)
            {
                case GitAction.Commit:
                    MessageBox.Show(this, $"{row.Branch}에 로컬 커밋을 만들었습니다.",
                        "Git 커밋", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case GitAction.Push:
                    DetailsText.Text = $"{row.Branch} 원격 푸시 중...";
                    await gitActions.PushAsync(row.Worktree);
                    MessageBox.Show(this, $"{row.Branch}를 origin에 푸시했습니다.",
                        "Git 푸시", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case GitAction.Merge:
                    DetailsText.Text = $"{row.Branch}를 main에 병합하고 푸시 중...";
                    await gitActions.MergeIntoMainAsync(row.Worktree, mainWorktree!);
                    MessageBox.Show(this, $"{row.Branch}를 main에 병합하고 origin/main에 푸시했습니다.",
                        "Git 병합", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }
        catch (Exception error)
        {
            var prefix = committed && action != GitAction.Commit
                ? "로컬 커밋은 완료됐지만 후속 작업에 실패했습니다.\n" : "";
            MessageBox.Show(this, prefix + error.Message, "Git 작업 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy = false;
            await RefreshAsync();
        }
    }

    private static bool CanOpen(WorktreeRow row) =>
        row.State.WebPort is not null && row.State.Status is ("실행 중" or "외부 실행");

    private static bool CanRestart(WorktreeRow row) =>
        row.State.Status is "실행 중" or "외부 실행";

    private static bool HasWorkBranch(WorktreeRow row) =>
        row.GitState.Error is null && row.Branch is not ("main" or "(detached)" or "(unknown)");

    private static bool CanCommit(WorktreeRow row) =>
        HasWorkBranch(row) && row.GitState.ChangedFiles > 0;

    private static bool CanMerge(WorktreeRow row) =>
        HasWorkBranch(row) &&
        (row.GitState.ChangedFiles > 0 || row.GitState.MainMerge == MainMergeState.Unmerged);

    private static bool CanPush(WorktreeRow row) =>
        row.GitState.Error is null && row.Branch is not ("(detached)" or "(unknown)") &&
        (row.Branch != "main" || row.GitState.ChangedFiles == 0);

    private void WorktreeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelection();

    private void UpdateSelection()
    {
        if (WorktreeGrid.SelectedItem is not WorktreeRow row)
        {
            DetailsText.Text = "워크트리를 선택하세요.";
            LogText.Text = "";
            ToggleButtons(false);
            return;
        }

        var origin = row.ServerStatus == "재시작 중" ? "선택한 워크트리를 리빌드하고 재시작 중" :
            row.ServerStatus == "전환 중" ? "선택한 워크트리로 전환 중" :
            row.State.Managed ? "Dev Manager에서 실행 중" :
            row.State.HasProcesses ? "다른 터미널에서 실행 중" : "중지";
        DetailsText.Text = $"{row.Path}\n{origin} · 실행/전환을 누르면 기존 PixPeek 서버를 종료하고 선택한 워크트리의 Full 개발 PWA를 실행합니다.\n{row.GitState.Details}";
        LogText.Text = row.State.HasProcesses && !row.State.Managed
            ? "다른 터미널에서 시작한 서버의 로그는 여기서 수집하지 않습니다."
            : manager.ReadRecentLog(row.Worktree);
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = !busy && row.State.HasProcesses;
        OpenButton.IsEnabled = !busy && CanOpen(row);
        FetchButton.IsEnabled = !busy;
    }

    private void ToggleButtons(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        OpenButton.IsEnabled = enabled;
        FetchButton.IsEnabled = enabled;
    }
}
