using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nebb.DevManager;

public partial class RunProfilesDialog : Window
{
    private readonly string repositoryPath;
    private readonly string worktreePath;
    private readonly CommandSettingsStore store;
    private readonly RepositoryCommandSettings settings;
    private readonly RunProfileManager? runManager;
    private RunProfile? activeProfile;
    private RunItem? activeItem;
    private bool loading;
    private bool activeOverride;
    private int candidateRefreshVersion;
    private Point dragOrigin;

    internal RunProfilesDialog(string repositoryPath, string worktreePath,
        string? storageDirectory = null, RunProfileManager? runManager = null)
    {
        this.repositoryPath = Path.GetFullPath(repositoryPath);
        this.worktreePath = Path.GetFullPath(worktreePath);
        store = new CommandSettingsStore(this.repositoryPath, storageDirectory);
        settings = store.Load();
        this.runManager = runManager;
        InitializeComponent();
        LocationText.Text = $"저장소: {this.repositoryPath} · 워크트리: {this.worktreePath}";
        ScopeChoice.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            try
            {
                var candidates = await Task.Run(() => new CommandDiscovery().Discover(this.repositoryPath));
                CandidateChoice.ItemsSource = candidates.Where(item => item.Kind == CommandKind.Run).ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(this, error.Message, "후보 탐지 실패", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        RefreshProfiles();
    }

    private bool IsOverride => activeOverride;
    private bool IsInherited => IsOverride && !settings.ProfileOverrides.ContainsKey(worktreePath);
    private IReadOnlyList<RunProfile> CurrentProfiles => IsOverride
        ? settings.EffectiveProfiles(worktreePath) : settings.DefaultProfiles;
    private List<RunProfile> EditableProfiles => IsOverride
        ? settings.ProfileOverrides[worktreePath] : settings.DefaultProfiles;

    private void CaptureEditor()
    {
        if (loading || IsInherited) return;
        if (activeProfile is not null) activeProfile.Name = ProfileNameBox.Text.Trim();
        if (activeItem is null) return;
        activeItem.Enabled = EnabledBox.IsChecked == true;
        activeItem.Name = NameBox.Text.Trim();
        activeItem.Kind = KindChoice.SelectedIndex == 1 ? RunItemKind.ShellScript : RunItemKind.Process;
        activeItem.Lifecycle = LifecycleChoice.SelectedIndex == 0
            ? RunLifecycle.OneShot : RunLifecycle.LongRunning;
        activeItem.Command = CommandBox.Text.Trim();
        activeItem.Arguments = ArgumentsBox.Text.Trim();
        activeItem.WorkingDirectory = WorkingDirectoryBox.Text.Trim();
        activeItem.ScriptSource = ScriptSourceChoice.SelectedIndex == 1
            ? RunScriptSource.File : RunScriptSource.Inline;
        activeItem.Shell = ShellChoice.SelectedIndex == 1 ? RunShell.Cmd : RunShell.PowerShell;
        activeItem.Script = activeItem.ScriptSource == RunScriptSource.File
            ? ScriptBox.Text.Trim() : ScriptBox.Text;
        activeItem.Environment = EnvironmentBox.Text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                var separator = line.IndexOf('=');
                return separator < 0 ? new EnvironmentEntry(line + "=", "") :
                    new EnvironmentEntry(line[..separator], line[(separator + 1)..]);
            }).ToList();
        activeItem.DependsOn = DependsList.SelectedItems.Cast<RunItem>()
            .Select(item => item.Id).ToList();
        activeItem.Readiness.Type = ReadinessChoice.SelectedIndex == 1
            ? RunReadinessKind.Port : RunReadinessKind.Process;
        activeItem.Readiness.Port = int.TryParse(PortBox.Text, out var port) ? port : null;
        activeItem.Readiness.TimeoutSeconds = int.TryParse(TimeoutBox.Text, out var timeout)
            ? timeout : 0;
        activeItem.Readiness.AllowExternalPort = ExternalPortBox.IsChecked == true;
    }

    private void RefreshProfiles(string? preferredId = null)
    {
        loading = true;
        var profiles = CurrentProfiles.ToArray();
        ProfileList.ItemsSource = profiles;
        activeProfile = profiles.FirstOrDefault(profile => profile.Id == preferredId)
            ?? profiles.FirstOrDefault();
        ProfileList.SelectedItem = activeProfile;
        ScopeStateText.Text = IsInherited
            ? "저장소 기본 목록을 상속합니다. 복사하면 이후 기본값 변경은 반영되지 않습니다."
            : IsOverride ? "Worktree Override: 기본 목록에서 분리된 snapshot" : "Repository defaults";
        CopyButton.Visibility = IsOverride ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.Visibility = IsOverride ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = IsInherited;
        ResetButton.IsEnabled = !IsInherited;
        var editable = !IsInherited;
        AddProfileButton.IsEnabled = editable;
        RemoveProfileButton.IsEnabled = editable;
        AddProcessButton.IsEnabled = editable;
        AddScriptButton.IsEnabled = editable;
        RemoveItemButton.IsEnabled = editable;
        AddCandidateButton.IsEnabled = editable;
        RefreshItems();
        loading = false;
    }

    private void RefreshItems(string? preferredId = null)
    {
        loading = true;
        var items = activeProfile?.Items.ToArray() ?? [];
        ItemList.ItemsSource = items;
        activeItem = items.FirstOrDefault(item => item.Id == preferredId) ?? items.FirstOrDefault();
        ItemList.SelectedItem = activeItem;
        LoadEditor();
        loading = false;
    }

    private void LoadEditor()
    {
        loading = true;
        var editable = !IsInherited;
        ProfileNameBox.Text = activeProfile?.Name ?? "";
        ProfileNameBox.IsEnabled = editable && activeProfile is not null;
        foreach (var control in EditorPanel.Children.OfType<Control>())
            if (control != ProfileNameBox) control.IsEnabled = editable && activeItem is not null;
        EnabledBox.IsEnabled = editable && activeItem is not null;
        NameBox.IsEnabled = editable && activeItem is not null;
        KindChoice.IsEnabled = editable && activeItem is not null;
        LifecycleChoice.IsEnabled = editable && activeItem is not null;
        ProcessPanel.IsEnabled = editable && activeItem is not null;
        ScriptPanel.IsEnabled = editable && activeItem is not null;
        ArgumentsBox.IsEnabled = editable && activeItem is not null;
        WorkingDirectoryBox.IsEnabled = editable && activeItem is not null;
        EnvironmentBox.IsEnabled = editable && activeItem is not null;
        DependsList.IsEnabled = editable && activeItem is not null;
        ReadinessPanel.IsEnabled = editable && activeItem is not null;
        EnabledBox.IsChecked = activeItem?.Enabled ?? false;
        NameBox.Text = activeItem?.Name ?? "";
        KindChoice.SelectedIndex = activeItem?.Kind == RunItemKind.ShellScript ? 1 : 0;
        LifecycleChoice.SelectedIndex = activeItem?.Lifecycle == RunLifecycle.OneShot ? 0 : 1;
        CommandBox.Text = activeItem?.Command ?? "";
        ArgumentsBox.Text = activeItem?.Arguments ?? "";
        WorkingDirectoryBox.Text = activeItem?.WorkingDirectory ?? "";
        EnvironmentBox.Text = activeItem is null ? "" : string.Join(Environment.NewLine,
            activeItem.Environment.Select(entry => $"{entry.Name}={entry.Value}"));
        ScriptSourceChoice.SelectedIndex = activeItem?.ScriptSource == RunScriptSource.File ? 1 : 0;
        ShellChoice.SelectedIndex = activeItem?.Shell == RunShell.Cmd ? 1 : 0;
        ScriptBox.Text = activeItem?.Script ?? "";
        ReadinessChoice.SelectedIndex = activeItem?.Readiness.Type == RunReadinessKind.Port ? 1 : 0;
        PortBox.Text = activeItem?.Readiness.Port?.ToString() ?? "";
        TimeoutBox.Text = (activeItem?.Readiness.TimeoutSeconds ?? 60).ToString();
        ExternalPortBox.IsChecked = activeItem?.Readiness.AllowExternalPort ?? false;
        DependsList.ItemsSource = activeProfile?.Items.Where(item => item != activeItem).ToArray();
        if (activeItem is not null)
            foreach (var item in DependsList.Items.Cast<RunItem>())
                if (activeItem.DependsOn.Contains(item.Id)) DependsList.SelectedItems.Add(item);
        UpdateEditorVisibility();
        loading = false;
    }

    private void UpdateEditorVisibility()
    {
        var script = KindChoice.SelectedIndex == 1;
        ProcessPanel.Visibility = script ? Visibility.Collapsed : Visibility.Visible;
        ScriptPanel.Visibility = script ? Visibility.Visible : Visibility.Collapsed;
        ScriptLabel.Text = ScriptSourceChoice.SelectedIndex == 1
            ? "스크립트 파일 경로 (작업 폴더 기준)" : "인라인 스크립트";
        ScriptBox.AcceptsReturn = ScriptSourceChoice.SelectedIndex != 1;
        ScriptBox.Height = ScriptSourceChoice.SelectedIndex == 1 ? 28 : 100;
        ShellChoice.IsEnabled = !IsInherited && activeItem is not null &&
            ScriptSourceChoice.SelectedIndex != 1;
        ReadinessPanel.Visibility = LifecycleChoice.SelectedIndex == 0
            ? Visibility.Collapsed : Visibility.Visible;
        PortPanel.Visibility = ReadinessChoice.SelectedIndex == 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScopeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        CaptureEditor();
        activeOverride = ScopeChoice.SelectedIndex == 1;
        activeItem = null;
        activeProfile = null;
        RefreshProfiles();
        _ = RefreshCandidatesAsync();
    }

    private async Task RefreshCandidatesAsync()
    {
        var version = ++candidateRefreshVersion;
        try
        {
            var root = IsOverride ? worktreePath : repositoryPath;
            var candidates = await Task.Run(() => new CommandDiscovery().Discover(root));
            if (version != candidateRefreshVersion) return;
            CandidateChoice.ItemsSource = candidates.Where(item => item.Kind == CommandKind.Run).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (version == candidateRefreshVersion) CandidateChoice.ItemsSource = null;
        }
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        CaptureEditor();
        activeProfile = ProfileList.SelectedItem as RunProfile;
        RefreshItems();
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        CaptureEditor();
        activeItem = ItemList.SelectedItem as RunItem;
        LoadEditor();
    }

    private void EditorChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ScriptLabel is null) return;
        UpdateEditorVisibility();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditor();
        settings.CopyDefaultProfilesFor(worktreePath);
        RefreshProfiles(activeProfile?.Id);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        settings.ResetProfileOverride(worktreePath);
        RefreshProfiles(activeProfile?.Id);
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditor();
        var profile = new RunProfile { Name = "새 프로필" };
        EditableProfiles.Add(profile);
        RefreshProfiles(profile.Id);
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (activeProfile is null) return;
        EditableProfiles.Remove(activeProfile);
        RefreshProfiles();
    }

    private void AddItem(RunItem item)
    {
        if (activeProfile is null) return;
        CaptureEditor();
        activeProfile.Items.Add(item);
        RefreshItems(item.Id);
        ProfileList.Items.Refresh();
    }

    private void AddProcess_Click(object sender, RoutedEventArgs e) =>
        AddItem(new RunItem { Name = "새 Process" });

    private void AddScript_Click(object sender, RoutedEventArgs e) =>
        AddItem(new RunItem { Name = "새 Shell script", Kind = RunItemKind.ShellScript,
            Lifecycle = RunLifecycle.OneShot });

    private void AddCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateChoice.SelectedItem is not CommandCandidate candidate) return;
        AddItem(new RunItem { CandidateId = candidate.Id, Name = candidate.Name,
            Command = candidate.Command, Arguments = candidate.Arguments,
            WorkingDirectory = candidate.WorkingDirectory });
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (activeProfile is null || activeItem is null) return;
        CaptureEditor();
        var id = activeItem.Id;
        activeProfile.Items.Remove(activeItem);
        foreach (var item in activeProfile.Items) item.DependsOn.Remove(id);
        RefreshItems();
        ProfileList.Items.Refresh();
    }

    private void ItemList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        dragOrigin = e.GetPosition(ItemList);

    private void ItemList_MouseMove(object sender, MouseEventArgs e)
    {
        if (IsInherited || activeItem is null || e.LeftButton != MouseButtonState.Pressed ||
            (e.GetPosition(ItemList) - dragOrigin).Length < 8) return;
        DragDrop.DoDragDrop(ItemList, activeItem, DragDropEffects.Move);
    }

    private void ItemList_Drop(object sender, DragEventArgs e)
    {
        if (IsInherited || activeProfile is null || e.Data.GetData(typeof(RunItem)) is not RunItem dragged) return;
        var target = ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject)
            is ListBoxItem box ? box.DataContext as RunItem : null;
        if (target is null || target == dragged) return;
        CaptureEditor();
        activeProfile.Items.Remove(dragged);
        activeProfile.Items.Insert(activeProfile.Items.IndexOf(target), dragged);
        RefreshItems(dragged.Id);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditor();
        try
        {
            var warnings = new List<string>();
            if (settings.DefaultProfiles.Select(profile => profile.Id).Distinct().Count() !=
                settings.DefaultProfiles.Count)
                throw new ArgumentException("프로필 ID가 중복됩니다.");
            foreach (var profile in settings.DefaultProfiles)
                warnings.AddRange(RunProfileValidator.Validate(profile, repositoryPath));
            foreach (var (path, profiles) in settings.ProfileOverrides)
            {
                if (profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Count)
                    throw new ArgumentException("프로필 ID가 중복됩니다.");
                foreach (var profile in profiles)
                    warnings.AddRange(RunProfileValidator.Validate(profile, path));
            }
            if (warnings.Count > 0 && MessageBox.Show(this,
                    string.Join("\n", warnings.Distinct()) + "\n\n워크트리 밖 경로를 저장할까요?",
                    "외부 경로 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
                MessageBoxResult.Yes) return;
            runManager?.ValidateSettingsTransition(settings);
            store.Save(settings);
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, error.Message, "Run Profile 저장 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
