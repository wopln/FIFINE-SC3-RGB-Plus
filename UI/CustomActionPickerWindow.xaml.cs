using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SC3RGBController.Services.Actions;
using SC3RGBController.Services.Apps;

namespace SC3RGBController.UI;

/// <summary>
/// "Choose what Custom A does" dialog. Set Owner, call ShowDialog(); when it returns true read
/// <see cref="SelectedAction"/> (persist with Serialize()) and <see cref="SelectedDisplayName"/> (row text).
/// </summary>
public partial class CustomActionPickerWindow : Window
{
    private const string PlayingSection = "Playing audio now";
    private const string AllAppsSection = "All apps";

    private enum Category { OpenApp, AppAudio, Microphone, Media }

    private readonly string _buttonLabel;
    private readonly CustomButtonAction _current;
    private readonly Func<IReadOnlyList<(string ProcessName, string DisplayName)>>? _getAudioApps;
    private readonly InstalledAppCatalog _catalog = InstalledAppCatalog.Shared;

    private readonly ObservableCollection<PickerAppItem> _launchItems = [];
    private readonly ObservableCollection<PickerAppItem> _audioItems = [];
    private readonly ListCollectionView _launchView;
    private readonly ListCollectionView _audioView;
    private IReadOnlyList<InstalledApp> _installed = [];
    private bool _appsLoaded;

    // Current choice per category. Each category keeps its own state so switching tabs never loses work.
    private PickerAppItem? _launchSelection;
    private (CustomButtonAction Action, string Display)? _launchOverride; // browsed program, or a saved app not found in the list
    private PickerAppItem? _audioSelection;
    private (string ProcessName, string Display)? _audioOverride;     // picked program, or a saved process not in the list
    private CustomActionKind _audioKind = CustomActionKind.AppMuteToggle;
    private int _audioStep = CustomButtonAction.DefaultStep;
    private CustomActionKind? _mediaKind;
    private int _mediaStep = CustomButtonAction.DefaultStep;
    private string? _pendingLaunchParsingName;
    private string? _pendingAudioProcess;

    private bool _syncing;           // suppresses handlers while code changes selection/filters
    private string _lastSummary = string.Empty;

    public CustomActionPickerWindow(string buttonLabel, CustomButtonAction current,
        Func<IReadOnlyList<(string ProcessName, string DisplayName)>>? getAudioApps)
    {
        _buttonLabel = string.IsNullOrWhiteSpace(buttonLabel) ? "the button" : buttonLabel;
        _current = current ?? CustomButtonAction.None;
        _getAudioApps = getAudioApps;
        InitializeComponent();

        Title = $"Choose what {_buttonLabel} does";
        TitleText.Text = Title;
        AssignButton.ToolTip = $"Assign this action to {_buttonLabel}";
        AutomationProperties.SetName(AssignButton, $"Assign to {_buttonLabel}");
        ClearButton.IsEnabled = _current.Kind != CustomActionKind.None;
        if (_getAudioApps is null) RefreshAudioButton.Visibility = Visibility.Collapsed;

        _launchView = new ListCollectionView(_launchItems) { Filter = item => Matches(item, LaunchSearchBox.Text) };
        _audioView = new ListCollectionView(_audioItems) { Filter = item => Matches(item, AudioSearchBox.Text) };
        _audioView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerAppItem.Section)));
        LaunchList.ItemsSource = _launchView;
        AudioList.ItemsSource = _audioView;

        ApplyCurrent();
        Loaded += Window_Loaded;
    }

    /// <summary>The chosen action. <see cref="CustomActionKind.None"/> when the user cleared the assignment.</summary>
    public CustomButtonAction SelectedAction { get; private set; } = CustomButtonAction.None;

    /// <summary>Text for the settings row, e.g. "Open Spotify" or "Mute / unmute Discord".</summary>
    public string SelectedDisplayName { get; private set; } = CustomButtonAction.None.Describe();

    // ---------- Startup ----------

    private void ApplyCurrent()
    {
        _syncing = true;
        try
        {
            if (_current.UsesStep)
            {
                if (_current.Kind is CustomActionKind.AppVolumeUp or CustomActionKind.AppVolumeDown) _audioStep = _current.Step;
                else _mediaStep = _current.Step;
            }
            SyncStepRadios(AudioStepGroup, _audioStep);
            SyncStepRadios(MediaStepGroup, _mediaStep);

            switch (_current.Kind)
            {
                case CustomActionKind.LaunchApp when !string.IsNullOrEmpty(_current.Target):
                    _pendingLaunchParsingName = _current.Target;
                    _launchOverride = (_current, _current.Target);
                    OpenAppCategory.IsChecked = true;
                    break;
                case CustomActionKind.LaunchProgram when !string.IsNullOrEmpty(_current.Target):
                    _launchOverride = (_current, Path.GetFileNameWithoutExtension(_current.Target));
                    OpenAppCategory.IsChecked = true;
                    break;
                case CustomActionKind.AppMuteToggle or CustomActionKind.AppVolumeUp or CustomActionKind.AppVolumeDown
                    when !string.IsNullOrEmpty(_current.Target):
                    _audioKind = _current.Kind;
                    (_current.Kind switch
                    {
                        CustomActionKind.AppVolumeUp => AudioUpOption,
                        CustomActionKind.AppVolumeDown => AudioDownOption,
                        _ => AudioMuteOption
                    }).IsChecked = true;
                    _pendingAudioProcess = _current.Target;
                    _audioOverride = (_current.Target, _current.Target);
                    AppAudioCategory.IsChecked = true;
                    break;
                case CustomActionKind.MicMuteToggle:
                    MicOption.IsChecked = true;
                    MicrophoneCategory.IsChecked = true;
                    break;
                case CustomActionKind.MediaPlayPause or CustomActionKind.MediaNext or CustomActionKind.MediaPrevious
                    or CustomActionKind.SystemMuteToggle or CustomActionKind.SystemVolumeUp or CustomActionKind.SystemVolumeDown:
                    _mediaKind = _current.Kind;
                    foreach (RadioButton option in MediaOptions.Children.OfType<RadioButton>())
                        option.IsChecked = Equals(option.Tag, _current.Kind.ToString());
                    MediaCategory.IsChecked = true;
                    break;
                default:
                    OpenAppCategory.IsChecked = true;
                    break;
            }
        }
        finally { _syncing = false; }

        UpdateAudioStepVisibility();
        UpdateMediaStepVisibility();
        ShowCategory(ActiveCategory);
        UpdateChosenTexts();
        UpdateSummary();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusActiveCategory));
        try
        {
            _installed = await _catalog.GetAppsAsync();
        }
        catch
        {
            _installed = [];
        }
        _appsLoaded = true;
        PopulateLaunchItems();
        PopulateAudioItems();
    }

    private void PopulateLaunchItems()
    {
        _syncing = true;
        try
        {
            _launchItems.Clear();
            foreach (InstalledApp app in _installed) _launchItems.Add(PickerAppItem.ForInstalledApp(app, _catalog, AllAppsSection));

            if (_pendingLaunchParsingName is not null)
            {
                PickerAppItem? match = _launchItems.FirstOrDefault(i => string.Equals(i.ParsingName, _pendingLaunchParsingName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    _launchSelection = match;
                    _launchOverride = null;
                    LaunchList.SelectedItem = match;
                    LaunchList.ScrollIntoView(match);
                }
                // Otherwise the saved app is gone (or hidden): _launchOverride keeps the assignment and
                // UpdateChosenTexts says it wasn't found.
                _pendingLaunchParsingName = null;
            }
        }
        finally { _syncing = false; }
        UpdateLaunchStatus();
        UpdateChosenTexts();
        UpdateSummary();
    }

    private void PopulateAudioItems()
    {
        string? keepProcess = _audioSelection?.ProcessName ?? _pendingAudioProcess;
        string? keepSection = _audioSelection?.Section;

        IReadOnlyList<(string ProcessName, string DisplayName)> playing = [];
        if (_getAudioApps is not null)
        {
            try { playing = _getAudioApps() ?? []; }
            catch { playing = []; }
        }

        _syncing = true;
        try
        {
            _audioItems.Clear();
            HashSet<string> playingProcesses = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string processName, string displayName) in playing)
            {
                if (string.IsNullOrWhiteSpace(processName) || !playingProcesses.Add(processName)) continue;
                InstalledApp? installed = _installed.FirstOrDefault(a => string.Equals(a.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
                string name = !string.IsNullOrWhiteSpace(displayName) ? displayName : installed?.Name ?? processName;
                _audioItems.Add(PickerAppItem.ForAudioProcess(name, processName, installed, _catalog, PlayingSection));
            }
            foreach (InstalledApp app in _installed)
            {
                if (app.ProcessName is null || playingProcesses.Contains(app.ProcessName)) continue;
                _audioItems.Add(PickerAppItem.ForInstalledApp(app, _catalog, AllAppsSection));
            }

            PickerAppItem? match = null;
            if (keepProcess is not null)
            {
                IEnumerable<PickerAppItem> candidates = _audioItems.Where(i => string.Equals(i.ProcessName, keepProcess, StringComparison.OrdinalIgnoreCase));
                match = candidates.FirstOrDefault(i => i.Section == keepSection) ?? candidates.FirstOrDefault();
            }
            _audioSelection = match;
            AudioList.SelectedItem = match;
            if (match is not null)
            {
                _audioOverride = null;
                AudioList.ScrollIntoView(match);
            }
            if (_appsLoaded) _pendingAudioProcess = null;
        }
        finally { _syncing = false; }

        UpdateAudioStatus(playing.Count);
        UpdateChosenTexts();
        UpdateSummary();
    }

    // ---------- Categories ----------

    private Category ActiveCategory =>
        AppAudioCategory.IsChecked == true ? Category.AppAudio
        : MicrophoneCategory.IsChecked == true ? Category.Microphone
        : MediaCategory.IsChecked == true ? Category.Media
        : Category.OpenApp;

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _syncing) return;
        Category category = ActiveCategory;
        // The microphone page has a single option; choosing the page chooses it.
        if (category == Category.Microphone) MicOption.IsChecked = true;
        ShowCategory(category);
        UpdateSummary();
    }

    private void ShowCategory(Category category)
    {
        OpenAppPanel.Visibility = category == Category.OpenApp ? Visibility.Visible : Visibility.Collapsed;
        AppAudioPanel.Visibility = category == Category.AppAudio ? Visibility.Visible : Visibility.Collapsed;
        MicrophonePanel.Visibility = category == Category.Microphone ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = category == Category.Media ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusActiveCategory()
    {
        Control target = ActiveCategory switch
        {
            Category.AppAudio => AudioSearchBox,
            Category.Microphone => MicOption,
            Category.Media => MediaOptions.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)
                              ?? MediaOptions.Children.OfType<RadioButton>().First(),
            _ => LaunchSearchBox
        };
        target.Focus();
        Keyboard.Focus(target);
    }

    // ---------- Open an app ----------

    private void LaunchSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter(LaunchList, _launchView, _launchSelection, UpdateLaunchStatus);

    private void LaunchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        // Only a real pick changes the choice; deselection caused by filtering keeps it.
        if ((e.AddedItems.OfType<PickerAppItem>().FirstOrDefault() ?? LaunchList.SelectedItem as PickerAppItem) is not { } picked) return;
        _launchSelection = picked;
        _launchOverride = null;
        UpdateChosenTexts();
        UpdateSummary();
    }

    private void BrowseProgram_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = $"Choose a program for {_buttonLabel}",
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        _launchOverride = (new CustomButtonAction(CustomActionKind.LaunchProgram, dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
        ClearListSelection(LaunchList, ref _launchSelection);
        UpdateChosenTexts();
        UpdateSummary();
        AssignButton.Focus();
    }

    private void UpdateLaunchStatus()
    {
        if (!_appsLoaded) SetStatus(LaunchStatusText, "Loading your apps…");
        else if (_launchItems.Count == 0) SetStatus(LaunchStatusText, "Couldn't list your apps. Use Browse for a program instead.");
        else if (_launchView.IsEmpty) SetStatus(LaunchStatusText, $"No apps match “{LaunchSearchBox.Text.Trim()}”.");
        else SetStatus(LaunchStatusText, null);
    }

    // ---------- App audio ----------

    private void AudioSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter(AudioList, _audioView, _audioSelection, () => UpdateAudioStatus(null));

    private void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if ((e.AddedItems.OfType<PickerAppItem>().FirstOrDefault() ?? AudioList.SelectedItem as PickerAppItem) is not { } picked) return;
        _audioSelection = picked;
        _audioOverride = null;
        UpdateChosenTexts();
        UpdateSummary();
    }

    private void AudioKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _audioKind = AudioUpOption.IsChecked == true ? CustomActionKind.AppVolumeUp
            : AudioDownOption.IsChecked == true ? CustomActionKind.AppVolumeDown
            : CustomActionKind.AppMuteToggle;
        UpdateAudioStepVisibility();
        UpdateSummary();
    }

    private void RefreshAudio_Click(object sender, RoutedEventArgs e)
    {
        PopulateAudioItems();
    }

    private async void PickAudioProgram_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = $"Choose the app {_buttonLabel} should control",
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        string? processName = await _catalog.ResolveProcessNameAsync(dialog.FileName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            SetStatus(AudioStatusText, "That file doesn't point to a program. Choose an .exe file or a shortcut to one.");
            Announce(AudioStatusText);
            return;
        }
        PickerAppItem? listed = _audioItems.FirstOrDefault(i => string.Equals(i.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (listed is not null && _audioView.PassesFilter(listed))
        {
            AudioList.SelectedItem = listed; // SelectionChanged updates state
            AudioList.ScrollIntoView(listed);
        }
        else
        {
            _audioOverride = (processName, listed?.Name ?? Path.GetFileNameWithoutExtension(dialog.FileName));
            ClearListSelection(AudioList, ref _audioSelection);
        }
        UpdateChosenTexts();
        UpdateSummary();
        AssignButton.Focus();
    }

    private void UpdateAudioStepVisibility() =>
        AudioStepRow.Visibility = _audioKind is CustomActionKind.AppVolumeUp or CustomActionKind.AppVolumeDown ? Visibility.Visible : Visibility.Collapsed;

    private int _lastPlayingCount;

    private void UpdateAudioStatus(int? playingCount)
    {
        if (playingCount is int count) _lastPlayingCount = count;
        if (!_appsLoaded && _audioItems.Count == 0) SetStatus(AudioStatusText, "Loading your apps…");
        else if (_audioItems.Count == 0) SetStatus(AudioStatusText, "No apps found. Start your app and select Refresh, or use Pick a program.");
        else if (_audioView.IsEmpty) SetStatus(AudioStatusText, $"No apps match “{AudioSearchBox.Text.Trim()}”.");
        else SetStatus(AudioStatusText, null);

        // Without a "Playing audio now" group the user needs to know why, and how to fix it.
        SetStatus(PlayingInfoText, _getAudioApps is not null && _appsLoaded && _lastPlayingCount == 0
            ? "Nothing is playing sound right now. Start your app's audio, then select Refresh."
            : null);
    }

    // ---------- Microphone / Media ----------

    private void Option_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _syncing) return;
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out CustomActionKind kind)) _mediaKind = kind;
        UpdateMediaStepVisibility();
        UpdateSummary();
    }

    private void UpdateMediaStepVisibility() =>
        MediaStepRow.Visibility = _mediaKind is CustomActionKind.SystemVolumeUp or CustomActionKind.SystemVolumeDown ? Visibility.Visible : Visibility.Collapsed;

    private void Step_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _syncing || sender is not RadioButton { Tag: string tag } radio || !int.TryParse(tag, out int step)) return;
        if (radio.GroupName == "AudioStep") _audioStep = step;
        else _mediaStep = step;
        UpdateSummary();
    }

    private static void SyncStepRadios(Panel group, int step)
    {
        foreach (RadioButton radio in group.Children.OfType<RadioButton>())
            radio.IsChecked = radio.Tag is string tag && int.TryParse(tag, out int value) && value == step;
    }

    // ---------- Result ----------

    private CustomButtonAction? BuildAction(out string? displayName)
    {
        displayName = null;
        switch (ActiveCategory)
        {
            case Category.OpenApp:
                if (_launchSelection is { ParsingName: not null } app)
                {
                    displayName = app.Name;
                    return new CustomButtonAction(CustomActionKind.LaunchApp, app.ParsingName);
                }
                if (_launchOverride is { } launch)
                {
                    displayName = launch.Display;
                    return launch.Action;
                }
                return null;

            case Category.AppAudio:
                string? process = _audioSelection?.ProcessName ?? _audioOverride?.ProcessName;
                if (string.IsNullOrWhiteSpace(process)) return null;
                displayName = _audioSelection?.Name ?? _audioOverride?.Display;
                return new CustomButtonAction(_audioKind, process, _audioStep);

            case Category.Microphone:
                return MicOption.IsChecked == true ? new CustomButtonAction(CustomActionKind.MicMuteToggle) : null;

            case Category.Media:
                return _mediaKind is CustomActionKind kind ? new CustomButtonAction(kind, null, _mediaStep) : null;
        }
        return null;
    }

    private void UpdateSummary()
    {
        if (!IsInitialized) return;
        CustomButtonAction? action = BuildAction(out string? displayName);
        AssignButton.IsEnabled = action is not null;
        string text = action is null
            ? ActiveCategory switch
            {
                Category.OpenApp => $"Choose an app for {_buttonLabel} to open.",
                Category.AppAudio => $"Choose the app {_buttonLabel} should control.",
                _ => $"Choose an action for {_buttonLabel}."
            }
            : $"{_buttonLabel} will: {action.Describe(displayName)}";
        if (text == _lastSummary) return;
        _lastSummary = text;
        SummaryText.Text = text;
        Announce(SummaryText);
    }

    private void UpdateChosenTexts()
    {
        if (_launchSelection is null && _launchOverride is { } launch)
        {
            string detail = launch.Action.Kind == CustomActionKind.LaunchProgram && launch.Action.Target is not null
                ? $"Selected: {launch.Display} ({launch.Action.Target})"
                : $"Current: {launch.Display}" + (_appsLoaded ? " (not found in your apps)" : string.Empty);
            LaunchChosenText.Text = detail;
            LaunchChosenText.ToolTip = launch.Action.Target;
            LaunchChosenText.Visibility = Visibility.Visible;
        }
        else LaunchChosenText.Visibility = Visibility.Collapsed;

        if (_audioSelection is null && _audioOverride is { } audio)
        {
            AudioChosenText.Text = string.Equals(audio.Display, audio.ProcessName, StringComparison.OrdinalIgnoreCase)
                ? $"Selected: {audio.ProcessName}.exe"
                : $"Selected: {audio.Display} ({audio.ProcessName}.exe)";
            AudioChosenText.Visibility = Visibility.Visible;
        }
        else AudioChosenText.Visibility = Visibility.Collapsed;
    }

    private void Assign_Click(object sender, RoutedEventArgs e) => TryAssign();

    private void TryAssign()
    {
        CustomButtonAction? action = BuildAction(out string? displayName);
        if (action is null) return;
        SelectedAction = action;
        SelectedDisplayName = action.Describe(displayName);
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = CustomButtonAction.None;
        SelectedDisplayName = CustomButtonAction.None.Describe();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---------- Lists, search and keyboard ----------

    private static bool Matches(object item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || item is not PickerAppItem app) return true;
        foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (app.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (app.ProcessName?.Contains(word, StringComparison.OrdinalIgnoreCase) == true) continue;
            return false;
        }
        return true;
    }

    /// <summary>Re-filters without losing the user's choice: hiding the selected item doesn't deselect it.</summary>
    private void RefreshFilter(ListBox list, ListCollectionView view, PickerAppItem? selection, Action updateStatus)
    {
        if (!IsInitialized) return;
        _syncing = true;
        try
        {
            view.Refresh();
            // The remembered choice stays in _launchSelection/_audioSelection, but the ListBox must not keep a
            // hidden item selected: it would swallow the user's next pick (bug: couldn't replace a saved app).
            list.SelectedItem = selection is not null && view.PassesFilter(selection) ? selection : null;
        }
        finally { _syncing = false; }
        updateStatus();
    }

    private void ClearListSelection(ListBox list, ref PickerAppItem? selection)
    {
        _syncing = true;
        try { list.SelectedItem = null; }
        finally { _syncing = false; }
        selection = null;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        ListBox list = box == AudioSearchBox ? AudioList : LaunchList;
        switch (e.Key)
        {
            case Key.Escape when box.Text.Length > 0:
                box.Clear(); // first Escape clears the search, the next one closes the dialog
                e.Handled = true;
                break;
            case Key.Down:
                e.Handled = FocusList(list, selectFirstIfNone: true);
                break;
            case Key.Enter when list.SelectedItem is null || !((ListCollectionView)list.ItemsSource).PassesFilter(list.SelectedItem):
                // Enter with nothing chosen picks the best match instead of doing nothing.
                if (!((ListCollectionView)list.ItemsSource).IsEmpty)
                {
                    FocusList(list, selectFirstIfNone: true);
                    e.Handled = true;
                }
                break;
        }
    }

    private bool FocusList(ListBox list, bool selectFirstIfNone)
    {
        ListCollectionView view = (ListCollectionView)list.ItemsSource;
        if (view.IsEmpty) return false;
        object? item = list.SelectedItem is { } selected && view.PassesFilter(selected) ? selected : null;
        if (item is null && selectFirstIfNone)
        {
            item = view.GetItemAt(0);
            list.SelectedItem = item;
        }
        item ??= view.GetItemAt(0);
        list.ScrollIntoView(item);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) return container.Focus();
        return list.Focus();
    }

    /// <summary>Typing while the list has focus goes to the search box, so filtering works from anywhere.</summary>
    private void List_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text == " " || char.IsControl(e.Text[0])) return;
        TextBox box = sender == AudioList ? AudioSearchBox : LaunchSearchBox;
        box.Focus();
        box.Text += e.Text;
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
    }

    private void ListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { IsSelected: true }) return;
        e.Handled = true;
        TryAssign();
    }

    // ---------- Accessibility helpers ----------

    private static void SetStatus(TextBlock target, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }
        bool changed = target.Text != text || target.Visibility != Visibility.Visible;
        target.Text = text;
        target.Visibility = Visibility.Visible;
        if (changed) Announce(target);
    }

    private static void Announce(FrameworkElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        AutomationPeer? peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

/// <summary>Row in the picker's app lists. ToString/AutomationName give screen readers the app name.</summary>
public sealed class PickerAppItem : INotifyPropertyChanged
{
    private readonly Func<Task<ImageSource?>>? _loadIcon;
    private ImageSource? _icon;
    private bool _iconRequested;

    private PickerAppItem(string name, string? parsingName, string? processName, string section, Func<Task<ImageSource?>>? loadIcon, bool isPlaying)
    {
        Name = name;
        ParsingName = parsingName;
        ProcessName = processName;
        Section = section;
        _loadIcon = loadIcon;
        AutomationName = isPlaying ? $"{name}, playing audio" : name;
    }

    public string Name { get; }
    public string? ParsingName { get; }
    public string? ProcessName { get; }
    public string Section { get; }
    public string AutomationName { get; }

    /// <summary>Loaded on first use, so only rows that are actually shown (virtualized) fetch icons.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested && _loadIcon is not null)
            {
                _iconRequested = true;
                LoadIcon();
            }
            return _icon;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static PickerAppItem ForInstalledApp(InstalledApp app, InstalledAppCatalog catalog, string section) =>
        new(app.Name, app.ParsingName, app.ProcessName, section, () => catalog.GetIconAsync(app), isPlaying: false);

    public static PickerAppItem ForAudioProcess(string name, string processName, InstalledApp? installed, InstalledAppCatalog catalog, string section)
    {
        Func<Task<ImageSource?>> loader = installed is not null
            ? () => catalog.GetIconAsync(installed)
            : () => LoadProcessIcon(processName, catalog);
        return new(name, installed?.ParsingName, processName, section, loader, isPlaying: true);
    }

    public override string ToString() => Name;

    private async void LoadIcon()
    {
        try
        {
            ImageSource? icon = await _loadIcon!();
            if (icon is null) return;
            _icon = icon;
            OnPropertyChanged(nameof(Icon));
        }
        catch
        {
            // Icons are decorative; the fallback glyph stays.
        }
    }

    private static async Task<ImageSource?> LoadProcessIcon(string processName, InstalledAppCatalog catalog)
    {
        string? path = await Task.Run(() =>
        {
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes)
                {
                    try
                    {
                        string? file = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file)) return file;
                    }
                    catch
                    {
                        // Elevated or protected process: try the next instance.
                    }
                }
            }
            catch { }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
            return null;
        });
        return path is null ? null : await catalog.GetFileIconAsync(path);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
