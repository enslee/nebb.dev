using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            Worktrees = new GitWorktreeService(repository.Path);
            CommandStore = new CommandSettingsStore(repository.Path);
            ProfileManager = new RunProfileManager(repository.Path);
            Git = new GitStatusService(repository.Path, BaseBranch);
            GitActions = new GitActionService(repository.Path, BaseBranch);
        }

        public RepositoryEntry Repository { get; }
        public string DisplayName => Repository.DisplayName;
        public string BaseBranch { get; }
        public GitWorktreeService Worktrees { get; }
        public CommandSettingsStore CommandStore { get; }
        public RunProfileManager ProfileManager { get; }
        public GitStatusService Git { get; }
        public GitActionService GitActions { get; }
    }

    private sealed record FilterChoice(string DisplayName, string? RepositoryPath);

    private sealed class WorktreeRow : INotifyPropertyChanged
    {
        private string? activityStatus;

        public WorktreeRow(RepositoryContext context, Worktree worktree,
            GitWorktreeState gitState,
            IReadOnlyList<RunProfile>? profiles = null, RunProfile? selectedProfile = null)
        {
            Context = context;
            Worktree = worktree;
            GitState = gitState;
            Profiles = profiles ?? [];
            SelectedProfile = selectedProfile;
        }

        public RepositoryContext Context { get; }
        public Worktree Worktree { get; }
        public GitWorktreeState GitState { get; }
        public IReadOnlyList<RunProfile> Profiles { get; }
        public RunProfile? SelectedProfile { get; }
        public bool HasRunnableProfile => SelectedProfile?.Items.Any(item => item.Enabled) == true;
        public int ActiveProfiles => Profiles.Count(profile =>
            Context.ProfileManager.HasActiveItems(Path, profile));
        public bool AnyPartialProfile => Profiles.Any(profile =>
            Context.ProfileManager.GetProfileStatus(Path, profile) == RunProfileStatus.PartiallyRunning);
        public bool AnyFailedProfile => Profiles.Any(profile =>
            Context.ProfileManager.GetProfileStatus(Path, profile) == RunProfileStatus.Failed);
        public string RepositoryName => Context.Repository.Name;
        public string Branch => Worktree.Branch;
        public string Path => Worktree.Path;
        public string ServerStatus => activityStatus ?? (Profiles.Count == 0 ? "미설정" :
            AnyPartialProfile ? "일부 실행" : ActiveProfiles > 0 ? $"실행 중 {ActiveProfiles}" :
            AnyFailedProfile ? "실패" : "중지");
        public Brush IndicatorBrush => activityStatus is not null ? Brushes.DarkOrange :
            ActiveProfiles > 0 ? Brushes.ForestGreen : Brushes.Gray;
        public string IndicatorText => ActiveProfiles > 0 ? "●" : "—";
        public string Commit => GitState.Commit;
        public string CommitTime => GitState.CommitTime;
        public string WorkingTree => GitState.WorkingTree;
        public string PushStatus => GitState.PushStatus;
        public string MainMergeStatus => GitState.MainMergeStatus;
        public IReadOnlyList<int> Ports => Context.ProfileManager.ListInstances()
            .Where(view => view.IsRunning && view.Record.WorktreePath.Equals(Path,
                StringComparison.OrdinalIgnoreCase)).SelectMany(view => view.Ports)
            .Distinct().Order().ToArray();
        public string WebAddress => Ports.Count > 0 ? $"http://127.0.0.1:{Ports[0]}" : "—";
        public string ApiAddress => Ports.Count > 1 ? $"http://127.0.0.1:{Ports[1]}" : "—";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetActivity(string? value)
        {
            if (activityStatus == value) return;
            activityStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndicatorBrush)));
        }
    }

    private sealed record RunItemRow(RunItem Item, RunItemState State)
    {
        public string Display => $"{(Item.Enabled ? "●" : "○")} {Item.Name} · " +
            (State.Status == RunItemStatus.WaitingReady ? "Waiting Ready" : State.Status.ToString()) +
            (string.IsNullOrWhiteSpace(State.Reason) ? "" : $" · {State.Reason}");
    }

    private sealed record ProcessRow(RepositoryContext Context, ProcessInstanceView View,
        string WorktreeDisplay)
    {
        public string InstanceId => View.Record.InstanceId;
        public bool CanControl => View.IsRunning;
        public bool CanRestart => View.Record.Status == RunItemStatus.Running;
        public string Display
        {
            get
            {
                var record = View.Record;
                var ports = View.Ports.Count == 0 ? "" :
                    "  :" + string.Join(", :", View.Ports);
                var elapsed = View.IsRunning
                    ? $"{Math.Max(0, (int)(DateTime.UtcNow - record.StartedUtc).TotalMinutes)}m"
                    : "";
                var exit = record.ExitCode is int code ? $" ({code})" : "";
                return $"{record.ItemName}  ·  {record.ProfileName}  ·  " +
                    $"{record.Status}{exit}{ports}  {elapsed}" +
                    (string.IsNullOrWhiteSpace(record.Reason) ? "" : $"  ·  {record.Reason}");
            }
        }
    }

    private readonly RepositoryCatalog catalog;
    private readonly List<RepositoryContext> contexts;
    private readonly List<WorktreeRow> allRows = [];
    private readonly ObservableCollection<WorktreeRow> rows = [];
    private readonly ObservableCollection<FilterChoice> filters = [];
    private readonly ObservableCollection<ProcessRow> processRows = [];
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer runTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? repositoryError;
    private bool filterUpdating;
    private bool busy;
    private string? selectedProcessId;
    private bool closeApproved;
    private bool closeHandling;
    private bool profileChoiceUpdating;

    internal MainWindow(RepositoryCatalog catalog, string? selectedRepository = null)
    {
        this.catalog = catalog;
        contexts = catalog.Repositories.Select(repository => new RepositoryContext(repository)).ToList();
        InitializeComponent();
        WorktreeGrid.ItemsSource = rows;
        RepositoryFilter.ItemsSource = filters;
        ProcessRepositoryChoice.ItemsSource = contexts;
        ProcessRepositoryChoice.SelectedItem = contexts.FirstOrDefault(context =>
            context.Repository.Path.Equals(selectedRepository, StringComparison.OrdinalIgnoreCase))
            ?? contexts.FirstOrDefault();
        ProcessList.ItemsSource = processRows;
        CollectionViewSource.GetDefaultView(processRows).GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ProcessRow.WorktreeDisplay)));
        LogStreamChoice.SelectedIndex = 0;
        RebuildFilter(selectedRepository);
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        runTimer.Tick += (_, _) =>
        {
            if (WorktreeGrid.SelectedItem is WorktreeRow row &&
                RunProfileChoice.SelectedItem is RunProfile profile)
                RefreshRunPanel(row, profile);
            if (selectedProcessId is not null && processRows.FirstOrDefault(value =>
                    value.InstanceId == selectedProcessId) is { } selected)
                LogText.Text = selected.Context.ProfileManager.ReadInstanceLog(selected.View.Record,
                    LogStreamChoice.SelectedIndex == 1);
        };
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            refreshTimer.Start();
            runTimer.Start();
        };
        Closed += (_, _) => { refreshTimer.Stop(); runTimer.Stop(); };
        Closing += MainWindow_Closing;
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
            var refreshed = new List<WorktreeRow>();
            var errors = new List<string>();
            foreach (var context in contexts)
            {
                try
                {
                    var worktrees = await context.Worktrees.ListWorktreesAsync();
                    var gitStates = await context.Git.GetStatesAsync(worktrees);
                    RepositoryCommandSettings? commandSettings = null;
                    try
                    {
                        PixPeekLegacyMigration.Ensure(context.Repository.Path, worktrees,
                            context.CommandStore);
                        commandSettings = context.CommandStore.Load();
                        context.ProfileManager.PruneHistory();
                    }
                    catch (Exception error)
                    {
                        errors.Add($"{context.Repository.Name} 명령 설정: {error.Message}");
                    }
                    for (var index = 0; index < worktrees.Count; index++)
                    {
                        var profiles = commandSettings?.EffectiveProfiles(worktrees[index].Path);
                        var selectedProfile = commandSettings?.SelectedProfile(worktrees[index].Path);
                        refreshed.Add(new WorktreeRow(context, worktrees[index],
                            gitStates[index], profiles, selectedProfile));
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
            RefreshProcesses();
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
            ProcessRepositoryChoice.ItemsSource = null;
            ProcessRepositoryChoice.ItemsSource = contexts;
            ProcessRepositoryChoice.SelectedItem = contexts.First(context =>
                context.Repository.Path.Equals(repository.Path, StringComparison.OrdinalIgnoreCase));
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

    private async void RunProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (busy || WorktreeGrid.SelectedItem is not WorktreeRow row) return;
        try
        {
            if (new RunProfilesDialog(row.Context.Repository.Path, row.Path,
                runManager: row.Context.ProfileManager)
                { Owner = this }.ShowDialog() == true) await RefreshAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Run Profile 설정을 열 수 없습니다",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RunProfileChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (profileChoiceUpdating || busy || WorktreeGrid.SelectedItem is not WorktreeRow row ||
            RunProfileChoice.SelectedItem is not RunProfile profile) return;
        try
        {
            var settings = row.Context.CommandStore.Load();
            settings.SelectedProfiles[row.Path] = profile.Id;
            row.Context.CommandStore.Save(settings);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "프로필 선택 실패", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task StartRowAsync(WorktreeRow row)
    {
        await RunProfileActionAsync(row, start: true);
    }

    private async Task StopRowAsync(WorktreeRow row)
    {
        await RunProfileActionAsync(row, start: false);
    }

    private async Task RunProfileActionAsync(WorktreeRow row, bool start)
    {
        var profile = RunProfileChoice.SelectedItem as RunProfile ?? row.SelectedProfile;
        if (busy || profile is null ||
            start && !profile.Items.Any(item => item.Enabled)) return;
        if (start && !ConfirmPortConflicts(row, profile)) return;
        busy = true;
        row.SetActivity(start ? "시작 중" : "종료 중");
        ToggleButtons(false);
        UpdateSelection();
        try
        {
            if (start)
            {
                if (row.Context.ProfileManager.HasActiveItems(row.Path, profile))
                    await row.Context.ProfileManager.RestartAllAsync(row.Path, profile);
                else await row.Context.ProfileManager.RunAllAsync(row.Path, profile);
            }
            else await row.Context.ProfileManager.StopAllAsync(row.Path, profile);
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

    private bool ConfirmPortConflicts(WorktreeRow row, RunProfile profile,
        string? itemId = null)
    {
        var conflicts = contexts.SelectMany(context =>
            context.ProfileManager.FindPortConflicts(row.Path, profile, itemId)
                .Select(conflict => $"{context.Repository.Name} / {conflict}"))
            .Distinct().ToArray();
        return conflicts.Length == 0 || new RunPortConflictDialog(conflicts)
            { Owner = this }.ShowDialog() == true;
    }

    private async Task RunItemActionAsync(Func<WorktreeRow, RunProfile, RunItem, Task> action)
    {
        if (busy || WorktreeGrid.SelectedItem is not WorktreeRow row ||
            (RunProfileChoice.SelectedItem as RunProfile ?? row.SelectedProfile) is not { } profile ||
            RunItemList.SelectedItem is not RunItemRow selected) return;
        busy = true;
        ToggleButtons(false);
        try { await action(row, profile, selected.Item); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "실행 항목 제어 실패", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally { busy = false; await RefreshAsync(); }
    }

    private async void StartItem_Click(object sender, RoutedEventArgs e) =>
        await RunItemActionAsync(async (row, profile, item) =>
        {
            if (ConfirmPortConflicts(row, profile, item.Id))
                await row.Context.ProfileManager.StartItemAsync(row.Path, profile, item.Id);
        });

    private async void RestartItem_Click(object sender, RoutedEventArgs e) =>
        await RunItemActionAsync(async (row, profile, item) =>
        {
            if (ConfirmPortConflicts(row, profile, item.Id))
                await row.Context.ProfileManager.RestartItemAsync(row.Path, profile, item.Id);
        });

    private async void StopItem_Click(object sender, RoutedEventArgs e) =>
        await RunItemActionAsync((row, profile, item) =>
            row.Context.ProfileManager.StopItemAsync(row.Path, profile, item.Id));

    private void RunItemList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRunItemSelection();

    private void LogStreamChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRunItemSelection();

    private void ProcessRepositoryChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshProcesses();

    private void RefreshProcesses()
    {
        if (ProcessRepositoryChoice?.SelectedItem is not RepositoryContext context) return;
        try
        {
            var branchByPath = allRows.Where(row => row.Context == context)
                .ToDictionary(row => row.Path, row => row.Branch,
                    StringComparer.OrdinalIgnoreCase);
            var snapshots = context.ProfileManager.ListInstances();
            processRows.Clear();
            foreach (var snapshot in snapshots)
            {
                var path = snapshot.Record.WorktreePath;
                var branch = branchByPath.GetValueOrDefault(path) ?? System.IO.Path.GetFileName(path);
                processRows.Add(new ProcessRow(context, snapshot, $"{branch}  ·  {path}"));
            }
            ProcessCountText.Text = $"{snapshots.Count(view => view.IsRunning)} running";
            ProcessStopAllButton.IsEnabled = !busy && snapshots.Any(view => view.IsRunning);
            ProcessRestartAllButton.IsEnabled = !busy && snapshots.Any(view =>
                view.Record.Status == RunItemStatus.Running &&
                view.Record.Lifecycle == RunLifecycle.LongRunning);
            ProcessClearHistoryButton.IsEnabled = !busy && snapshots.Any(view => !view.IsRunning);
        }
        catch (Exception error)
        {
            ProcessCountText.Text = "프로세스 상태 확인 실패: " + error.Message;
        }
    }

    private void ProcessLogs_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProcessRow row) return;
        selectedProcessId = row.InstanceId;
        LogText.Text = row.Context.ProfileManager.ReadInstanceLog(row.View.Record,
            LogStreamChoice.SelectedIndex == 1);
    }

    private async Task ProcessActionAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        ToggleButtons(false);
        try { await action(); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "프로세스 제어 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { busy = false; await RefreshAsync(); }
    }

    private async void ProcessRestart_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ProcessRow row)
            await ProcessActionAsync(() => row.Context.ProfileManager.RestartInstanceAsync(
                row.InstanceId, row.Context.CommandStore.Load()));
    }

    private async void ProcessStop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ProcessRow row)
            await ProcessActionAsync(() =>
                row.Context.ProfileManager.StopInstanceAsync(row.InstanceId));
    }

    private async void ProcessKill_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ProcessRow row)
            await ProcessActionAsync(() =>
                row.Context.ProfileManager.StopInstanceAsync(row.InstanceId, force: true));
    }

    private async void ProcessStopAll_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessRepositoryChoice.SelectedItem is RepositoryContext context)
            await ProcessActionAsync(() =>
                context.ProfileManager.StopRunningAsync());
    }

    private async void ProcessRestartAll_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessRepositoryChoice.SelectedItem is RepositoryContext context)
            await ProcessActionAsync(() =>
                context.ProfileManager.RestartRunningAsync(context.CommandStore.Load()));
    }

    private void ProcessClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessRepositoryChoice.SelectedItem is not RepositoryContext context) return;
        if (MessageBox.Show(this, $"{context.Repository.Name}의 종료 이력과 로그를 삭제할까요?",
                "이력 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        context.ProfileManager.ClearHistory();
        RefreshProcesses();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        ShutdownEntry[] running;
        try
        {
            running = contexts.SelectMany(context => context.ProfileManager.ListInstances()
                .Where(view => view.IsRunning)
                .Select(view => new ShutdownEntry(context.Repository.Name,
                    context.ProfileManager, view))).ToArray();
        }
        catch (Exception error)
        {
            e.Cancel = true;
            MessageBox.Show(this, error.Message, "프로세스 상태 확인 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (running.Length == 0) return;
        e.Cancel = true;
        if (closeHandling) return;
        var dialog = new ProcessShutdownDialog(running) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        closeHandling = true;
        try
        {
            foreach (var entry in dialog.SelectedEntries)
                await entry.Manager.StopInstanceAsync(entry.Instance.Record.InstanceId);
            closeApproved = true;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message + "\n남아 있는 프로세스를 확인한 뒤 다시 닫아 주세요.",
                "프로세스 종료 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshProcesses();
        }
        finally { closeHandling = false; }
    }

    private async Task RestartRowAsync(WorktreeRow row) =>
        await RunProfileActionAsync(row, start: true);

    private void OpenRow(WorktreeRow row)
    {
        if (busy || row.Ports.Count == 0) return;
        var port = row.Ports[0];
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true });
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
        ((MenuItem)menu.Items[0]).Header = "Run All / Restart All";
        ((MenuItem)menu.Items[2]).Header = "Stop All";
        ((MenuItem)menu.Items[0]).IsEnabled = !busy && row.HasRunnableProfile;
        ((MenuItem)menu.Items[1]).IsEnabled = !busy && CanRestart(row);
        ((MenuItem)menu.Items[2]).IsEnabled = !busy && row.SelectedProfile is { } profile &&
            row.Context.ProfileManager.HasActiveItems(row.Path, profile);
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
        var manager = row.Context.Worktrees;
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

    private static bool CanOpen(WorktreeRow row) => row.Ports.Count > 0;

    private static bool CanRestart(WorktreeRow row) => row.SelectedProfile is { } profile &&
        row.Context.ProfileManager.HasActiveItems(row.Path, profile);

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
        RunProfilesButton.IsEnabled = !busy && WorktreeGrid.SelectedItem is WorktreeRow;
        RepositoryFilter.IsEnabled = !busy;
        FetchButton.IsEnabled = !busy && contexts.Count > 0;
        if (WorktreeGrid.SelectedItem is not WorktreeRow row)
        {
            DetailsText.Text = contexts.Count == 0
                ? "상단의 저장소 추가로 로컬 Git 저장소를 등록하세요."
                : repositoryError ?? "워크트리를 선택하세요.";
            LogText.Text = "";
            RunProfileLabel.Visibility = Visibility.Collapsed;
            RunProfileChoice.Visibility = Visibility.Collapsed;
            RunItemsPanel.Visibility = Visibility.Collapsed;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            OpenButton.IsEnabled = false;
            return;
        }

        RunProfileLabel.Visibility = Visibility.Visible;
        RunProfileChoice.Visibility = Visibility.Visible;
        RunItemsPanel.Visibility = Visibility.Visible;
        profileChoiceUpdating = true;
        RunProfileChoice.ItemsSource = row.Profiles;
        RunProfileChoice.SelectedItem = row.SelectedProfile;
        profileChoiceUpdating = false;
        var selectedProfile = row.SelectedProfile;
        var summary = selectedProfile is null ? "Run Profile이 없습니다. 프로필 설정에서 추가하세요." :
            $"{selectedProfile.Name} · {ProfileStatusText(row.Context.ProfileManager.GetProfileStatus(row.Path, selectedProfile))}";
        DetailsText.Text = $"{row.Context.Repository.Name} · {row.Path}\n{summary}\n{row.GitState.Details}";
        if (selectedProfile is not null) RefreshRunPanel(row, selectedProfile);
        else RunItemList.ItemsSource = null;
        if (repositoryError is not null) DetailsText.Text += $"\n저장소 확인 실패: {repositoryError}";
        StartButton.IsEnabled = !busy && row.HasRunnableProfile;
        StopButton.IsEnabled = !busy && row.SelectedProfile is { } selected &&
            row.Context.ProfileManager.HasActiveItems(row.Path, selected);
        OpenButton.IsEnabled = !busy && CanOpen(row);
    }

    private void RefreshRunPanel(WorktreeRow row, RunProfile profile)
    {
        var previousId = (RunItemList.SelectedItem as RunItemRow)?.Item.Id;
        var items = profile.Items.Select(item => new RunItemRow(item,
            row.Context.ProfileManager.GetItemState(row.Path, profile, item))).ToArray();
        RunItemList.ItemsSource = items;
        RunItemList.SelectedItem = items.FirstOrDefault(item => item.Item.Id == previousId)
            ?? items.FirstOrDefault();
        var active = row.Context.ProfileManager.HasActiveItems(row.Path, profile);
        StartButton.Content = active ? "Restart All" : "Run All";
        StopButton.Content = "Stop All";
        StartButton.IsEnabled = !busy && profile.Items.Any(item => item.Enabled);
        StopButton.IsEnabled = !busy && active;
        UpdateRunItemSelection();
    }

    private void UpdateRunItemSelection()
    {
        if (selectedProcessId is not null && processRows.FirstOrDefault(value =>
                value.InstanceId == selectedProcessId) is { } process)
        {
            LogText.Text = process.Context.ProfileManager.ReadInstanceLog(process.View.Record,
                LogStreamChoice.SelectedIndex == 1);
            return;
        }
        if (WorktreeGrid.SelectedItem is not WorktreeRow row ||
            (RunProfileChoice.SelectedItem as RunProfile ?? row.SelectedProfile) is not { } profile ||
            RunItemList.SelectedItem is not RunItemRow selected)
        {
            StartItemButton.IsEnabled = false;
            RestartItemButton.IsEnabled = false;
            StopItemButton.IsEnabled = false;
            return;
        }
        var status = selected.State.Status;
        var active = status is RunItemStatus.Starting or RunItemStatus.WaitingReady or
            RunItemStatus.Running or RunItemStatus.Stopping;
        StartItemButton.IsEnabled = !busy && selected.Item.Enabled && !active;
        RestartItemButton.IsEnabled = !busy && selected.Item.Enabled && active;
        StopItemButton.IsEnabled = !busy && active;
        LogText.Text = row.Context.ProfileManager.ReadLog(row.Path, profile, selected.Item,
            LogStreamChoice.SelectedIndex == 1);
    }

    private static string ProfileStatusText(RunProfileStatus status) => status switch
    {
        RunProfileStatus.PartiallyRunning => "Partially Running",
        _ => status.ToString()
    };

    private void ToggleButtons(bool enabled)
    {
        LocalModelButton.IsEnabled = enabled;
        CommandSettingsButton.IsEnabled = enabled;
        RunProfilesButton.IsEnabled = enabled;
        RunProfileChoice.IsEnabled = enabled;
        StartItemButton.IsEnabled = enabled;
        RestartItemButton.IsEnabled = enabled;
        StopItemButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        OpenButton.IsEnabled = enabled;
        FetchButton.IsEnabled = enabled;
        AddRepositoryButton.IsEnabled = enabled;
        RepositoryFilter.IsEnabled = enabled;
    }
}
