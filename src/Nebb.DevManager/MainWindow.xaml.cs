using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Nebb.DevManager;

public partial class MainWindow : Window
{
    private enum GitAction { Commit, Merge, Push }

    private sealed class RepositoryContext
    {
        public RepositoryContext(RepositoryEntry repository)
        {
            Repository = repository;
            BaseBranch = GitBranchDefaults.Detect(repository.Path);
            Manager = new DevServerManager(repository.Path);
            CommandStore = new CommandSettingsStore(repository.Path);
            RunManager = new CommandRunManager(repository.Path);
            Git = new GitStatusService(repository.Path, BaseBranch);
            GitActions = new GitActionService(repository.Path, BaseBranch);
        }

        public RepositoryEntry Repository { get; }
        public string BaseBranch { get; }
        public DevServerManager Manager { get; }
        public CommandSettingsStore CommandStore { get; }
        public CommandRunManager RunManager { get; }
        public GitStatusService Git { get; }
        public GitActionService GitActions { get; }
    }

    private sealed record FilterChoice(string DisplayName, string? RepositoryPath);

    private sealed class WorktreeRow : INotifyPropertyChanged
    {
        private string? activityStatus;

        public WorktreeRow(RepositoryContext context, Worktree worktree,
            WorktreeState state, GitWorktreeState gitState, SavedCommand? runCommand = null,
            CommandRunState? runState = null)
        {
            Context = context;
            Worktree = worktree;
            State = state;
            GitState = gitState;
            RunCommand = runCommand;
            RunState = runState;
        }

        public RepositoryContext Context { get; }
        public Worktree Worktree { get; }
        public WorktreeState State { get; }
        public GitWorktreeState GitState { get; }
        public SavedCommand? RunCommand { get; }
        public CommandRunState? RunState { get; }
        public bool HasRunCommand => !string.IsNullOrWhiteSpace(RunCommand?.Command);
        public string RepositoryName => Context.Repository.Name;
        public bool IsPixPeek => Context.Repository.IsPixPeek;
        public string Branch => Worktree.Branch;
        public string Path => Worktree.Path;
        public string ServerStatus => activityStatus ?? (IsPixPeek
            ? State.Status == "시작 중" ? "전환 중" : State.Status
            : RunState?.Status ?? "중지");
        public Brush IndicatorBrush => activityStatus is not null || State.Status == "시작 중" ? Brushes.DarkOrange :
            State.HasProcesses || RunState?.IsRunning == true ? Brushes.ForestGreen : Brushes.Gray;
        public string IndicatorText => IsPixPeek || RunState?.IsRunning == true ? "●" : "—";
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

    private readonly RepositoryCatalog catalog;
    private readonly List<RepositoryContext> contexts;
    private readonly List<WorktreeRow> allRows = [];
    private readonly ObservableCollection<WorktreeRow> rows = [];
    private readonly ObservableCollection<FilterChoice> filters = [];
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private string? repositoryError;
    private bool filterUpdating;
    private bool busy;

    internal MainWindow(RepositoryCatalog catalog, string? selectedRepository = null)
    {
        this.catalog = catalog;
        contexts = catalog.Repositories.Select(repository => new RepositoryContext(repository)).ToList();
        InitializeComponent();
        WorktreeGrid.ItemsSource = rows;
        RepositoryFilter.ItemsSource = filters;
        RebuildFilter(selectedRepository);
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
        ToggleButtons(false);
        try
        {
            var selectedPath = (WorktreeGrid.SelectedItem as WorktreeRow)?.Path;
            var pixPeek = contexts.FirstOrDefault(context => context.Repository.IsPixPeek);
            var snapshot = ProcessSnapshot.Empty;
            var refreshed = new List<WorktreeRow>();
            var errors = new List<string>();
            if (pixPeek is not null)
            {
                try { snapshot = await pixPeek.Manager.InspectAsync(); }
                catch (Exception error) { errors.Add($"PixPeek 서버 상태: {error.Message}"); }
            }
            foreach (var context in contexts)
            {
                try
                {
                    var worktrees = await context.Manager.ListWorktreesAsync();
                    var gitStates = await context.Git.GetStatesAsync(worktrees);
                    RepositoryCommandSettings? commandSettings = null;
                    if (!context.Repository.IsPixPeek)
                    {
                        try { commandSettings = context.CommandStore.Load(); }
                        catch (Exception error)
                        {
                            errors.Add($"{context.Repository.Name} 명령 설정: {error.Message}");
                        }
                    }
                    for (var index = 0; index < worktrees.Count; index++)
                    {
                        var state = context.Repository.IsPixPeek
                            ? context.Manager.GetState(worktrees[index], snapshot)
                            : new WorktreeState("미설정", null, null, false, false, null);
                        var runCommand = commandSettings?.Effective(worktrees[index].Path, CommandKind.Run);
                        var runState = context.Repository.IsPixPeek ? null :
                            context.RunManager.GetState(worktrees[index].Path);
                        refreshed.Add(new WorktreeRow(context, worktrees[index], state,
                            gitStates[index], runCommand, runState));
                    }
                }
                catch (Exception error)
                {
                    errors.Add($"{context.Repository.Name}: {error.Message}");
                }
            }
            allRows.Clear();
            allRows.AddRange(refreshed);
            repositoryError = errors.Count == 0 ? null : string.Join("\n", errors);
            ApplyFilter(selectedPath);
        }
        catch (Exception error)
        {
            repositoryError = $"상태 확인 실패: {error.Message}";
        }
        finally
        {
            busy = false;
            UpdateSelection();
        }
    }

    private void RebuildFilter(string? selectedRepository)
    {
        filterUpdating = true;
        filters.Clear();
        filters.Add(new FilterChoice("모든 저장소", null));
        foreach (var repository in catalog.Repositories)
            filters.Add(new FilterChoice(repository.DisplayName, repository.Path));
        RepositoryFilter.SelectedItem = filters.FirstOrDefault(item =>
            item.RepositoryPath is not null &&
            item.RepositoryPath.Equals(selectedRepository, StringComparison.OrdinalIgnoreCase)) ?? filters[0];
        filterUpdating = false;
        ApplyFilter();
    }

    private void ApplyFilter(string? selectedWorktree = null)
    {
        var repositoryPath = (RepositoryFilter.SelectedItem as FilterChoice)?.RepositoryPath;
        rows.Clear();
        foreach (var row in allRows.Where(row => repositoryPath is null ||
                     row.Context.Repository.Path.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase)))
            rows.Add(row);
        WorktreeGrid.SelectedItem = rows.FirstOrDefault(row =>
            row.Path.Equals(selectedWorktree, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        UpdateSelection();
    }

    private async Task<bool> RunActionAsync(WorktreeRow selected,
        Func<Worktree, ProcessSnapshot, Task> action, string? activityStatus = null)
    {
        if (busy || !selected.IsPixPeek) return false;
        var manager = selected.Context.Manager;
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

    private void RepositoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!filterUpdating) ApplyFilter();
    }

    private async void AddRepository_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picker = new OpenFolderDialog { Title = "추가할 Git 저장소 폴더 선택" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var repository = catalog.Add(picker.FolderName);
            if (!contexts.Any(context => context.Repository.Path.Equals(
                    repository.Path, StringComparison.OrdinalIgnoreCase)))
                contexts.Add(new RepositoryContext(repository));
            RebuildFilter(repository.Path);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "저장소 추가 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true;
        ToggleButtons(false);
        try
        {
            var selectedRepository = (RepositoryFilter.SelectedItem as FilterChoice)?.RepositoryPath;
            var errors = new List<string>();
            foreach (var context in contexts.Where(context => selectedRepository is null ||
                         context.Repository.Path.Equals(selectedRepository, StringComparison.OrdinalIgnoreCase)))
            {
                try { await context.Git.FetchOriginAsync(); }
                catch (Exception error) { errors.Add($"{context.Repository.Name}: {error.Message}"); }
            }
            if (errors.Count > 0)
                MessageBox.Show(this, string.Join("\n", errors), "원격 갱신 실패",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"원격 Git 정보를 갱신하지 못했습니다: {error.Message}",
                "Dev Manager", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void LocalModel_Click(object sender, RoutedEventArgs e) =>
        new LocalModelDialog { Owner = this }.ShowDialog();

    private async void CommandSettings_Click(object sender, RoutedEventArgs e)
    {
        if (busy || WorktreeGrid.SelectedItem is not WorktreeRow row) return;
        try
        {
            if (new CommandSettingsDialog(row.Context.Repository.Path, row.Path)
                { Owner = this }.ShowDialog() == true) await RefreshAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "명령 설정을 열 수 없습니다",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartRowAsync(WorktreeRow row)
    {
        if (!row.IsPixPeek)
        {
            await RunCommandActionAsync(row, start: true);
            return;
        }
        if (!await RunActionAsync(row, row.Context.Manager.StartAsync, activityStatus: "전환 중")) return;
        try { OpenPwa(); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "PixPeek Dev Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StopRowAsync(WorktreeRow row)
    {
        if (!row.IsPixPeek) await RunCommandActionAsync(row, start: false);
        else await RunActionAsync(row, (worktree, _) => row.Context.Manager.StopAsync(worktree));
    }

    private async Task RunCommandActionAsync(WorktreeRow row, bool start)
    {
        if (busy || row.IsPixPeek || start && !row.HasRunCommand) return;
        busy = true;
        row.SetActivity(start ? "전환 중" : "종료 중");
        ToggleButtons(false);
        UpdateSelection();
        try
        {
            if (start) await row.Context.RunManager.SwitchAsync(row.Path, row.RunCommand!);
            else await row.Context.RunManager.StopAsync(row.Path);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, start ? "명령 실행 실패" : "명령 종료 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy = false;
            await RefreshAsync();
            row.SetActivity(null);
        }
    }

    private async Task RestartRowAsync(WorktreeRow row) =>
        await RunActionAsync(row, row.Context.Manager.RebuildAndRestartAsync, activityStatus: "재시작 중");

    private void OpenRow(WorktreeRow row)
    {
        if (busy || !row.IsPixPeek || row.State.WebPort is not int port) return;
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
        ((MenuItem)menu.Items[0]).IsEnabled = !busy && (row.IsPixPeek || row.HasRunCommand);
        ((MenuItem)menu.Items[1]).IsEnabled = !busy && CanRestart(row);
        ((MenuItem)menu.Items[2]).IsEnabled = !busy && (row.IsPixPeek
            ? row.State.HasProcesses : row.RunState?.IsRunning == true);
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
        var manager = row.Context.Manager;
        var gitActions = row.Context.GitActions;
        var baseBranch = row.Context.BaseBranch;
        busy = true;
        ToggleButtons(false);
        DetailsText.Text = $"{row.Branch} Git 작업 준비 중...";
        var committed = false;
        try
        {
            Worktree? baseWorktree = null;
            if (action == GitAction.Merge)
            {
                baseWorktree = (await manager.ListWorktreesAsync())
                    .FirstOrDefault(item => item.Branch == baseBranch) ??
                    throw new InvalidOperationException(
                        $"{baseBranch} 워크트리가 없습니다. 기본 저장소를 {baseBranch}로 전환하세요.");
                await gitActions.EnsureMergeReadyAsync(row.Worktree, baseWorktree);
            }

            var changes = await gitActions.GetPendingChangesAsync(row.Worktree);
            if (action == GitAction.Commit && changes.Length == 0)
                throw new InvalidOperationException("커밋할 변경 사항이 없습니다.");

            if (changes.Length != 0)
            {
                if (row.Branch == baseBranch)
                    throw new InvalidOperationException($"{baseBranch}에서 직접 커밋할 수 없습니다.");
                var actionName = action switch
                {
                    GitAction.Merge => "병합",
                    GitAction.Push => "푸시",
                    _ => "커밋"
                };
                var dialog = new GitCommitDialog(row.Branch, baseBranch, actionName, changes) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                await gitActions.CommitAsync(row.Worktree, changes, dialog.CommitMessage);
                committed = true;
            }
            else if (action != GitAction.Commit)
            {
                var prompt = action == GitAction.Merge
                    ? $"{row.Branch}를 {baseBranch}에 병합하고 작업 브랜치와 {baseBranch}를 origin에 푸시합니다. 계속할까요?"
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
                    DetailsText.Text = $"{row.Branch}를 {baseBranch}에 병합하고 푸시 중...";
                    await gitActions.MergeIntoBaseAsync(row.Worktree, baseWorktree!);
                    MessageBox.Show(this, $"{row.Branch}를 {baseBranch}에 병합하고 origin/{baseBranch}에 푸시했습니다.",
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
        row.IsPixPeek && row.State.WebPort is not null &&
        row.State.Status is ("실행 중" or "외부 실행");

    private static bool CanRestart(WorktreeRow row) =>
        row.IsPixPeek && row.State.Status is "실행 중" or "외부 실행";

    private static bool HasWorkBranch(WorktreeRow row) =>
        row.GitState.Error is null && row.Branch != row.Context.BaseBranch &&
        row.Branch is not ("(detached)" or "(unknown)");

    private static bool CanCommit(WorktreeRow row) =>
        HasWorkBranch(row) && row.GitState.ChangedFiles > 0;

    private static bool CanMerge(WorktreeRow row) =>
        HasWorkBranch(row) && row.GitState.MainMerge != MainMergeState.Unavailable &&
        (row.GitState.ChangedFiles > 0 || row.GitState.MainMerge == MainMergeState.Unmerged);

    private static bool CanPush(WorktreeRow row) =>
        row.GitState.Error is null && row.Branch is not ("(detached)" or "(unknown)") &&
        (row.Branch != row.Context.BaseBranch || row.GitState.ChangedFiles == 0);

    private void WorktreeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelection();

    private void UpdateSelection()
    {
        AddRepositoryButton.IsEnabled = !busy;
        LocalModelButton.IsEnabled = !busy;
        CommandSettingsButton.IsEnabled = !busy && WorktreeGrid.SelectedItem is WorktreeRow;
        RepositoryFilter.IsEnabled = !busy;
        FetchButton.IsEnabled = !busy && contexts.Count > 0;
        if (WorktreeGrid.SelectedItem is not WorktreeRow row)
        {
            DetailsText.Text = contexts.Count == 0
                ? "상단의 저장소 추가로 로컬 Git 저장소를 등록하세요."
                : repositoryError ?? "워크트리를 선택하세요.";
            LogText.Text = "";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            OpenButton.IsEnabled = false;
            return;
        }

        if (row.IsPixPeek)
        {
            var origin = row.ServerStatus == "재시작 중" ? "선택한 워크트리를 리빌드하고 재시작 중" :
                row.ServerStatus == "전환 중" ? "선택한 워크트리로 전환 중" :
                row.State.Managed ? "Dev Manager에서 실행 중" :
                row.State.HasProcesses ? "다른 터미널에서 실행 중" : "중지";
            DetailsText.Text = $"{row.Context.Repository.Name} · {row.Path}\n{origin} · 실행/전환을 누르면 기존 PixPeek 서버를 종료하고 선택한 워크트리의 Full 개발 PWA를 실행합니다.\n{row.GitState.Details}";
            LogText.Text = row.State.HasProcesses && !row.State.Managed
                ? "다른 터미널에서 시작한 서버의 로그는 여기서 수집하지 않습니다."
                : row.Context.Manager.ReadRecentLog(row.Worktree);
        }
        else
        {
            var command = row.RunCommand;
            var summary = row.HasRunCommand
                ? $"{row.RunState?.Status ?? "중지"} · {command!.Name}: {command.Command} {command.Arguments} · {command.WorkingDirectory}"
                : "실행 명령이 없습니다. 명령 설정에서 Run 명령을 저장하세요.";
            DetailsText.Text = $"{row.Context.Repository.Name} · {row.Path}\n{summary}\n실행/전환은 같은 저장소에서 Dev Manager가 실행한 이전 명령을 종료하고 선택한 워크트리의 Run 명령을 시작합니다.\n{row.GitState.Details}";
            LogText.Text = row.Context.RunManager.ReadRecentLog(row.Path);
        }
        if (repositoryError is not null) DetailsText.Text += $"\n저장소 확인 실패: {repositoryError}";
        StartButton.IsEnabled = !busy && (row.IsPixPeek || row.HasRunCommand);
        StopButton.IsEnabled = !busy && (row.IsPixPeek
            ? row.State.HasProcesses : row.RunState?.IsRunning == true);
        OpenButton.IsEnabled = !busy && CanOpen(row);
    }

    private void ToggleButtons(bool enabled)
    {
        LocalModelButton.IsEnabled = enabled;
        CommandSettingsButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        OpenButton.IsEnabled = enabled;
        FetchButton.IsEnabled = enabled;
        AddRepositoryButton.IsEnabled = enabled;
        RepositoryFilter.IsEnabled = enabled;
    }
}
