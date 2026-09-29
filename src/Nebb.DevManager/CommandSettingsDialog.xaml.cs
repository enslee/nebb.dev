using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Nebb.DevManager;

public partial class CommandSettingsDialog : Window
{
    private readonly string repositoryPath;
    private readonly string worktreePath;
    private readonly CommandSettingsStore store;
    private readonly RepositoryCommandSettings settings;
    private readonly CommandDiscovery discovery = new();
    private readonly HashSet<(bool Override, CommandKind Kind)> changed = [];
    private readonly HashSet<SavedCommand> changedServices = [];
    private readonly Dictionary<(bool Override, CommandKind Kind), string> environmentDraft = [];
    private readonly Dictionary<SavedCommand, string> serviceEnvironmentDraft = [];
    private IReadOnlyList<CommandCandidate> defaultCandidates = [];
    private IReadOnlyList<CommandCandidate> overrideCandidates = [];
    private bool initialized;
    private bool loadingEditor;
    private bool editorChanged;
    private bool serviceSelectionUpdating;
    private bool activeOverride;
    private CommandKind activeKind;
    private SavedCommand? activeService;

    internal CommandSettingsDialog(string repositoryPath, string worktreePath,
        string? storageDirectory = null)
    {
        this.repositoryPath = Path.GetFullPath(repositoryPath);
        this.worktreePath = Path.GetFullPath(worktreePath);
        store = new CommandSettingsStore(this.repositoryPath, storageDirectory);
        settings = store.Load();
        InitializeComponent();
        LocationText.Text = $"저장소: {this.repositoryPath}\n워크트리: {this.worktreePath}";
        ScopeChoice.SelectedIndex = 0;
        KindChoice.SelectedIndex = 0;
        foreach (var box in new[] { NameBox, CommandBox, ArgumentsBox, WorkingDirectoryBox, EnvironmentBox })
            box.TextChanged += (_, _) => { if (!loadingEditor) editorChanged = true; };
        initialized = true;
        LoadEditor();
        Loaded += async (_, _) => await RefreshCandidatesAsync();
    }

    private CommandPair CurrentPair => activeOverride
        ? settings.OverrideFor(worktreePath)
        : settings.Default;

    private SavedCommand? CurrentSaved => activeKind == CommandKind.Service
        ? activeService : CurrentPair.Get(activeKind);
    private IReadOnlyList<CommandCandidate> CurrentCandidates =>
        activeOverride ? overrideCandidates : defaultCandidates;
    private IReadOnlyList<SavedCommand> CurrentServices => activeOverride
        ? settings.EffectiveServices(worktreePath) : settings.DefaultServices;
    private List<SavedCommand>? OwnServices => activeOverride
        ? settings.ServiceOverrideFor(worktreePath) : settings.DefaultServices;

    private List<SavedCommand> EditableServices()
    {
        if (!activeOverride) return settings.DefaultServices;
        if (settings.ServiceOverrideFor(worktreePath) is { } existing) return existing;
        var copy = settings.CopyDefaultServicesFor(worktreePath);
        foreach (var service in copy) changedServices.Add(service);
        return copy;
    }

    private void SetCurrent(SavedCommand? value)
    {
        if (activeKind == CommandKind.Service) throw new InvalidOperationException("서비스는 개별 목록에서 관리합니다.");
        CurrentPair.Set(activeKind, value);
        changed.Add((activeOverride, activeKind));
        environmentDraft.Remove((activeOverride, activeKind));
        LoadEditor();
    }

    private void CaptureEditor()
    {
        if (!editorChanged || CurrentSaved is not { } command) return;
        command.Name = NameBox.Text.Trim();
        command.Command = CommandBox.Text.Trim();
        command.Arguments = ArgumentsBox.Text.Trim();
        command.WorkingDirectory = WorkingDirectoryBox.Text.Trim();
        if (activeKind == CommandKind.Service)
        {
            serviceEnvironmentDraft[command] = EnvironmentBox.Text;
            changedServices.Add(command);
        }
        else
        {
            environmentDraft[(activeOverride, activeKind)] = EnvironmentBox.Text;
            changed.Add((activeOverride, activeKind));
        }
        editorChanged = false;
    }

    private void LoadEditor()
    {
        if (!initialized) return;
        loadingEditor = true;
        var own = CurrentSaved;
        var serviceMode = activeKind == CommandKind.Service;
        var inherited = activeOverride && (serviceMode
            ? settings.ServiceOverrideFor(worktreePath) is null : own is null);
        var value = serviceMode ? own : own ?? (activeOverride ? settings.Default.Get(activeKind) : null);
        var editable = value is not null && !inherited;
        NameBox.Text = value?.Name ?? "";
        CommandBox.Text = value?.Command ?? "";
        ArgumentsBox.Text = value?.Arguments ?? "";
        WorkingDirectoryBox.Text = value?.WorkingDirectory ?? "";
        EnvironmentBox.Text = value is null ? "" :
            (serviceMode ? serviceEnvironmentDraft.GetValueOrDefault(value) :
                environmentDraft.GetValueOrDefault((activeOverride, activeKind))) ??
            string.Join(Environment.NewLine, value.Environment.Select(item => $"{item.Name}={item.Value}"));
        foreach (var box in new[] { NameBox, CommandBox, ArgumentsBox, WorkingDirectoryBox, EnvironmentBox })
            box.IsEnabled = editable;
        ClearButton.Content = serviceMode ? "서비스 제거" : activeOverride ? "Override 해제" : "선택 해제";
        ClearButton.IsEnabled = own is not null && !inherited;
        CopyDefaultButton.Visibility = activeOverride ? Visibility.Visible : Visibility.Collapsed;
        CopyDefaultButton.Content = serviceMode ? "기본 서비스 복사" : "기본값 복사해 변경";
        CopyDefaultButton.IsEnabled = activeOverride && (serviceMode
            ? settings.ServiceOverrideFor(worktreePath) is null
            : own is null && settings.Default.Get(activeKind) is not null);
        ResetServiceOverrideButton.Visibility = serviceMode && activeOverride
            ? Visibility.Visible : Visibility.Collapsed;
        ResetServiceOverrideButton.IsEnabled = serviceMode && activeOverride &&
            settings.ServiceOverrideFor(worktreePath) is not null;
        SelectedServicesGroup.Visibility = serviceMode ? Visibility.Visible : Visibility.Collapsed;
        var missing = CommandDiscovery.SourceMissing(value, CurrentCandidates, activeKind);
        StateText.Text = serviceMode
            ? inherited
                ? $"저장소 기본 서비스 목록을 상속합니다. 복사하거나 후보를 추가하면 이 워크트리만 변경됩니다.{(missing ? " 원본 후보 없음." : "")}"
                : value is null ? "선택한 서비스가 없습니다. 후보를 추가하거나 직접 입력하세요."
                : missing ? "원본 후보 없음: 저장한 서비스 명령은 유지됩니다."
                : "선택한 서비스 명령을 수정할 수 있습니다. 이 화면에서는 실행하지 않습니다."
            : value is null
                ? "선택된 명령이 없습니다. 후보를 선택하거나 직접 추가하세요."
                : inherited
                    ? $"저장소 기본값을 상속합니다.{(missing ? " 원본 후보 없음." : "")}"
                    : missing ? "원본 후보 없음: 저장한 명령은 유지됩니다."
                    : "선택된 명령을 수정할 수 있습니다. 이 화면에서는 명령을 실행하지 않습니다.";
        editorChanged = false;
        loadingEditor = false;
    }

    private void UpdateCandidates()
    {
        CandidateTree.Items.Clear();
        foreach (var group in CurrentCandidates.GroupBy(item => item.ProjectGroup)
                     .OrderBy(item => item.Key == "." ? "" : item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var projectItem = new TreeViewItem
            {
                Header = CandidateGrouping.Title(group.Key),
                IsExpanded = true
            };
            var runs = group.Where(item => item.Kind == CommandKind.Run).ToArray();
            if (runs.Any(item => item.Priority == CandidatePriority.Recommended))
            {
                AddSubgroup(projectItem, "Recommended",
                    runs.Where(item => item.Priority == CandidatePriority.Recommended));
                AddSubgroup(projectItem, "Other",
                    runs.Where(item => item.Priority == CandidatePriority.Normal));
            }
            else foreach (var candidate in runs) projectItem.Items.Add(CandidateItem(candidate));
            AddSubgroup(projectItem, "Tests", group.Where(item => item.Kind == CommandKind.Test));
            AddSubgroup(projectItem, "Services", group.Where(item => item.Kind == CommandKind.Service));
            CandidateTree.Items.Add(projectItem);
        }
        ChooseCandidateButton.IsEnabled = false;
        RefreshSelectedServices();
        LoadEditor();
    }

    private static TreeViewItem CandidateItem(CommandCandidate candidate) => new()
    {
        Header = $"{candidate.Name}  ·  {candidate.Command} {candidate.Arguments}",
        ToolTip = $"작업 폴더: {candidate.WorkingDirectory}\n출처: {candidate.SourceDescription}\n규칙: {candidate.RuleId}\n신뢰도: {candidate.Confidence}",
        Tag = candidate
    };

    private static void AddSubgroup(TreeViewItem parent, string title,
        IEnumerable<CommandCandidate> candidates)
    {
        var items = candidates.ToArray();
        if (items.Length == 0) return;
        var group = new TreeViewItem { Header = title, IsExpanded = true };
        foreach (var candidate in items) group.Items.Add(CandidateItem(candidate));
        parent.Items.Add(group);
    }

    private void RefreshSelectedServices(SavedCommand? selected = null)
    {
        serviceSelectionUpdating = true;
        var services = CurrentServices;
        SelectedServicesList.ItemsSource = null;
        SelectedServicesList.ItemsSource = services.ToArray();
        activeService = selected is not null && services.Contains(selected) ? selected :
            activeService is not null && services.Contains(activeService) ? activeService :
            services.FirstOrDefault();
        SelectedServicesList.SelectedItem = activeService;
        serviceSelectionUpdating = false;
    }

    private void CandidateTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        ChooseCandidateButton.IsEnabled = CandidateTree.SelectedItem is TreeViewItem
            { Tag: CommandCandidate };

    private void SelectedServicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || serviceSelectionUpdating || activeKind != CommandKind.Service) return;
        var selected = SelectedServicesList.SelectedItem as SavedCommand;
        CaptureEditor();
        activeService = selected;
        serviceSelectionUpdating = true;
        SelectedServicesList.Items.Refresh();
        SelectedServicesList.SelectedItem = activeService;
        serviceSelectionUpdating = false;
        LoadEditor();
    }

    private async Task RefreshCandidatesAsync()
    {
        CaptureEditor();
        RefreshCandidatesButton.IsEnabled = false;
        StateText.Text = "명령 후보를 탐지하는 중입니다...";
        try
        {
            defaultCandidates = await Task.Run(() => discovery.Discover(repositoryPath));
            overrideCandidates = worktreePath.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase)
                ? defaultCandidates : await Task.Run(() => discovery.Discover(worktreePath));
            UpdateCandidates();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, "명령 후보 탐지 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StateText.Text = "후보를 탐지하지 못했습니다. 직접 추가는 계속 사용할 수 있습니다.";
        }
        finally { RefreshCandidatesButton.IsEnabled = true; }
    }

    private void Choice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        CaptureEditor();
        activeOverride = ScopeChoice.SelectedIndex == 1;
        activeKind = KindChoice.SelectedIndex switch
        {
            1 => CommandKind.Test,
            2 => CommandKind.Service,
            _ => CommandKind.Run
        };
        activeService = null;
        UpdateCandidates();
    }

    private async void RefreshCandidates_Click(object sender, RoutedEventArgs e) =>
        await RefreshCandidatesAsync();

    private void ChooseCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateTree.SelectedItem is not TreeViewItem { Tag: CommandCandidate candidate }) return;
        CaptureEditor();
        if (candidate.Kind != activeKind)
            KindChoice.SelectedIndex = candidate.Kind switch
            {
                CommandKind.Test => 1,
                CommandKind.Service => 2,
                _ => 0
            };
        if (candidate.Kind == CommandKind.Service)
        {
            var services = EditableServices();
            var existing = services.FirstOrDefault(item => item.CandidateId == candidate.Id);
            if (existing is null)
            {
                existing = SavedCommand.FromCandidate(candidate);
                services.Add(existing);
                changedServices.Add(existing);
            }
            activeService = existing;
            RefreshSelectedServices(existing);
            LoadEditor();
        }
        else SetCurrent(SavedCommand.FromCandidate(candidate));
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditor();
        if (activeKind == CommandKind.Service)
        {
            var service = new SavedCommand { Name = "사용자 서비스" };
            EditableServices().Add(service);
            changedServices.Add(service);
            activeService = service;
            RefreshSelectedServices(service);
            LoadEditor();
        }
        else SetCurrent(new SavedCommand { Name = "사용자 명령" });
        CommandBox.Focus();
    }

    private void CopyDefault_Click(object sender, RoutedEventArgs e)
    {
        if (!activeOverride) return;
        CaptureEditor();
        if (activeKind == CommandKind.Service)
        {
            var services = EditableServices();
            activeService = services.FirstOrDefault();
            RefreshSelectedServices();
            LoadEditor();
        }
        else if (settings.Default.Get(activeKind) is { } value) SetCurrent(value.Copy());
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditor();
        if (activeKind == CommandKind.Service)
        {
            if (activeService is null || OwnServices is not { } services) return;
            services.Remove(activeService);
            serviceEnvironmentDraft.Remove(activeService);
            activeService = null;
            RefreshSelectedServices();
            LoadEditor();
        }
        else SetCurrent(null);
    }

    private void ResetServiceOverride_Click(object sender, RoutedEventArgs e)
    {
        if (!activeOverride || activeKind != CommandKind.Service) return;
        CaptureEditor();
        settings.ResetServiceOverride(worktreePath);
        activeService = null;
        RefreshSelectedServices();
        LoadEditor();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditor();
        try
        {
            foreach (var slot in changed)
            {
                var command = (slot.Override
                    ? settings.OverrideFor(worktreePath) : settings.Default).Get(slot.Kind);
                if (command is null) continue;
                CommandSettingsValidator.Validate(command, slot.Override ? worktreePath : repositoryPath,
                    environmentDraft.GetValueOrDefault(slot) ??
                    string.Join(Environment.NewLine, command.Environment.Select(item => $"{item.Name}={item.Value}")));
            }
            foreach (var service in changedServices)
            {
                var root = settings.DefaultServices.Contains(service) ? repositoryPath :
                    settings.ServiceOverrideFor(worktreePath)?.Contains(service) == true
                        ? worktreePath : null;
                if (root is null) continue;
                CommandSettingsValidator.Validate(service, root,
                    serviceEnvironmentDraft.GetValueOrDefault(service) ??
                    string.Join(Environment.NewLine, service.Environment.Select(item => $"{item.Name}={item.Value}")));
            }
            store.Save(settings);
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, "명령 설정 저장 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
