using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AoForge.Models;
using AoForge.Services;
using AoForge.ViewModels;
using Microsoft.Win32;

namespace AoForge;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    readonly Settings _settings = Settings.Load();
    readonly GitHubReleases _github = new();
    readonly ObservableCollection<AddonVm> _addons = new();
    readonly StringBuilder _log = new();
    readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly string _logFile = Path.Combine(AppPaths.DataDir, "AoForge.log");

    InstallEngine _engine;
    AddonIndex _index;
    bool _gameRunning;
    bool _busy;
    string _error;
    string _search = "";
    bool _installedTab;
    AddonVm _selected;
    bool _removeUserFiles;
    GhRelease _selfRelease; // a newer AoForge release, if there is one
    // While AoForge is open it checks for a newer AoForge every 2 minutes (addons are checked at
    // start and with Check for updates). An unchanged answer ("304 Not Modified", see
    // GitHubReleases) doesn't count against GitHub's hourly limit.
    readonly DispatcherTimer _selfCheckTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    bool _selfCanReplace;   // the exe's folder is writable, so the update can be applied in place

    public MainWindow()
    {
        InitializeComponent();
        View = CollectionViewSource.GetDefaultView(_addons);
        View.Filter = o => o is AddonVm a && a.Matches(_search) && (!_installedTab || a.IsPresent);
        DataContext = this;
        _gameTimer.Tick += (_, _) => UpdateGameRunning();
        Loaded += async (_, _) => await StartAsync();
        CreateTray();
    }

    // --- bound state --------------------------------------------------------------------------

    public event PropertyChangedEventHandler PropertyChanged;
    void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public ICollectionView View { get; }

    public string ClientDisplay => _engine?.ClientDir ?? "Choose your client folder…";
    public string ClientTooltip => (_engine != null ? _engine.ClientDir + "\n\n" : "") + "Click to choose which Anarchy Online client to manage.";

    public bool IsIdle => !_busy;
    public bool CanModify => _engine != null && !_busy && !_gameRunning;

    public string Banner =>
        _engine == null ? "Choose the Anarchy Online client folder (the one with AnarchyOnline.exe) to manage its addons."
        : _gameRunning ? "Anarchy Online is running from this folder. Close the game to install, update or remove addons."
        : _error;

    public bool HasBanner => !string.IsNullOrEmpty(Banner);
    public bool BannerIsError => _engine != null && !_gameRunning && _error != null;

    public bool IsInstalledTab
    {
        get => _installedTab;
        set { _installedTab = value; View.Refresh(); RaiseAll(); }
    }

    public bool IsBrowseTab
    {
        get => !_installedTab;
        set => IsInstalledTab = !value;
    }

    public string InstalledTabText => $"Installed ({_addons.Count(a => a.IsPresent)})";
    public string BrowseTabText => $"All addons ({_addons.Count})";

    public string Search
    {
        get => _search;
        set { _search = value ?? ""; View.Refresh(); Raise(nameof(IsEmpty)); Raise(nameof(EmptyText)); }
    }

    public bool IsEmpty => !View.Cast<object>().Any();
    public string EmptyText => _index == null ? "Loading the addon list…"
        : _installedTab && string.IsNullOrWhiteSpace(_search) ? "No addons installed yet. Find some under All addons."
        : "No addons match your search.";

    public AddonVm Selected
    {
        get => _selected;
        set { _selected = value; Raise(); Raise(nameof(HasSelection)); Raise(nameof(HasNoSelection)); }
    }

    public bool HasSelection => _selected != null;
    public bool HasNoSelection => _selected == null;

    public bool RemoveUserFiles
    {
        get => _removeUserFiles;
        set { _removeUserFiles = value; Raise(); }
    }

    IEnumerable<AddonVm> Updatable => _addons.Where(a => a.State == AddonState.UpdateAvailable && a.FindAsset() != null);
    public bool CanUpdateAll => CanModify && Updatable.Any();
    public string UpdateAllText => Updatable.Count() is var n && n > 0 ? $"Update all ({n})" : "Update all";

    public bool HasSelfUpdate => _selfRelease != null;
    public string SelfUpdateText => _selfRelease == null ? null
        : $"AoForge {_selfRelease.TagName} is available (you have v{SelfUpdater.Current.ToString(3)}).";
    public string SelfUpdateButtonText => _selfCanReplace ? "Update AoForge" : "Download";

    public string LogText => _log.ToString();
    public string LastLogLine { get; private set; } = "Activity";

    void Log(string line)
    {
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.Invoke(() => Log(line));
            return;
        }
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        _log.AppendLine(stamped);
        LastLogLine = line;
        Raise(nameof(LogText));
        Raise(nameof(LastLogLine));
        LogBox.ScrollToEnd();
        try {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd} {stamped}{Environment.NewLine}");
        } catch (IOException) {
        }
    }

    // --- startup ------------------------------------------------------------------------------

    async Task StartAsync()
    {
        Log(Environment.GetCommandLineArgs().Contains("--updated") ? $"AoForge {AppPaths.Version} (just updated)" : $"AoForge {AppPaths.Version}");
        string client = ClientLocator.IsClientDir(_settings.Selected) ? _settings.Selected : null;
        if (client == null) {
            var found = ClientLocator.FindCandidates();
            foreach (string f in found)
                Remember(f);
            client = found.FirstOrDefault();
        }
        if (client != null)
            SelectClient(client, refresh: false);
        _gameTimer.Start();

        await LoadIndexAsync();
        await RefreshAsync();
        _selfCheckTimer.Tick += async (_, _) => {
            if (!_busy)
                await CheckSelfUpdateAsync(quiet: true); // logs and shows the bar only for a new version
        };
        _selfCheckTimer.Start();
    }

    // quiet: the timer's check, which doesn't log failures (offline, GitHub busy); it tries again soon.
    async Task CheckSelfUpdateAsync(bool quiet = false)
    {
        try {
            var release = await _github.GetLatestAsync(SelfUpdater.Repo);
            bool newer = release != null && SelfUpdater.IsNewer(release.TagName, SelfUpdater.Current)
                && release.Assets?.Any(a => a.Name == SelfUpdater.AssetName) == true;
            if (newer && _selfRelease?.TagName != release.TagName) {
                Log($"AoForge {release.TagName} is available.");
                NotifyTray($"AoForge {release.TagName} is available. Click to open AoForge and update.");
            }
            _selfRelease = newer ? release : null;
            if (newer)
                _selfCanReplace = SelfUpdater.CanReplace(SelfUpdater.ExePath);
        } catch (Exception e) {
            if (!quiet)
                Log($"Couldn't check for a new AoForge: {e.Message}");
        }
        RaiseAll();
    }

    async void OnSelfUpdate(object sender, RoutedEventArgs e)
    {
        var release = _selfRelease;
        if (release == null || _busy)
            return;
        string exe = SelfUpdater.ExePath;
        if (!_selfCanReplace) {
            // e.g. under Program Files: let the browser download it instead
            OpenUrl(release.HtmlUrl);
            return;
        }

        SetBusy(true);
        try {
            var asset = release.Assets.First(a => a.Name == SelfUpdater.AssetName);
            Log($"Downloading AoForge {release.TagName}…");
            string downloaded = await _github.DownloadAsync(asset, null);
            SelfUpdater.Verify(downloaded, release.TagName);
            SelfUpdater.Apply(downloaded, exe);
            Log($"Installed AoForge {release.TagName}; restarting.");
            SelfUpdater.Restart(exe);
            Application.Current.Shutdown();
        } catch (Exception ex) {
            Log($"Updating AoForge failed: {ex.Message}");
            MessageBox.Show(this, $"{ex.Message}\n\nYou can download the new version from the release page instead.",
                "Update AoForge", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false);
        }
    }

    void OnSelfUpdateNotes(object sender, RoutedEventArgs e) => OpenUrl(_selfRelease?.HtmlUrl);

    async Task LoadIndexAsync()
    {
        string source = IndexArgument() ?? AppPaths.IndexUrl;
        try {
            string text = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await _github.GetStringAsync(source)
                : File.ReadAllText(source);
            _index = Json.Parse<AddonIndex>(text);
            Log($"Addon list: {_index.Addons.Count} addons from {source}");
        } catch (Exception e) {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("AoForge.index.json");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _index = Json.Parse<AddonIndex>(ms.ToArray());
            Log($"Couldn't load the online addon list ({e.Message}); using the built-in one.");
        }
        _addons.Clear();
        foreach (var def in _index.Addons.Where(d => d.Id != null && d.Name != null))
            _addons.Add(new AddonVm(def));
        Selected = _addons.FirstOrDefault();
        RaiseAll();
    }

    static string IndexArgument()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--index");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Fetches the latest release of every addon and recomputes their status.
    async Task RefreshAsync()
    {
        if (_index == null)
            return;
        SetBusy(true);
        _error = null;
        try {
            var selfCheck = CheckSelfUpdateAsync();
            await Task.WhenAll(_addons.Select(async a => {
                try {
                    a.Release = await _github.GetLatestAsync(a.Def.Repo);
                    a.ReleaseError = null;
                } catch (Exception e) {
                    a.ReleaseError = e.Message;
                    Log($"{a.Name}: couldn't check for releases: {e.Message}");
                }
            }));
            await selfCheck;
            UpdateStatuses();
            int updates = Updatable.Count();
            Log(updates > 0 ? $"{updates} update(s) available."
                : _addons.Any(a => a.State is AddonState.Installed) ? "Everything is up to date."
                : $"Checked {_addons.Count} addons.");
        } finally {
            SetBusy(false);
        }
    }

    void UpdateStatuses()
    {
        foreach (var a in _addons) {
            if (_engine == null) {
                a.Status = new AddonStatus { State = AddonState.NotInstalled };
                continue;
            }
            try {
                a.Status = _engine.GetStatus(a.Def, a.LatestTag);
                var rec = _engine.Find(a.Id);
                if (rec != null)
                    a.LoadOptions(rec.OptionList);
            } catch (Exception e) {
                Log($"{a.Name}: {e.Message}");
            }
        }
        View.Refresh();
        RaiseAll();
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        RaiseAll();
    }

    void UpdateGameRunning()
    {
        bool running = _engine != null && GameGuard.IsGameRunning(_engine.ClientDir);
        if (running == _gameRunning)
            return;
        _gameRunning = running;
        if (!running)
            UpdateStatuses(); // the launcher or game may have changed files
        RaiseAll();
    }

    // --- client folder ------------------------------------------------------------------------

    void Remember(string dir)
    {
        if (!_settings.ClientList.Contains(dir, StringComparer.OrdinalIgnoreCase))
            _settings.ClientList.Add(dir);
    }

    void SelectClient(string dir, bool refresh = true)
    {
        try {
            _engine = new InstallEngine(dir);
        } catch (Exception e) {
            MessageBox.Show(this, $"Couldn't read AoForge's records in {dir}:\n{e.Message}", "AoForge", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Remember(_engine.ClientDir);
        _settings.Selected = _engine.ClientDir;
        TrySaveSettings();
        Log($"Client folder: {_engine.ClientDir}");
        _gameRunning = GameGuard.IsGameRunning(_engine.ClientDir);
        if (refresh)
            UpdateStatuses();
        RaiseAll();
    }

    void TrySaveSettings()
    {
        try {
            _settings.Save();
        } catch (Exception e) {
            Log($"Couldn't save settings: {e.Message}");
        }
    }

    void OnClientButton(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ClientButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (string dir in _settings.ClientList.Where(ClientLocator.IsClientDir)) {
            var item = new MenuItem { Header = dir, IsChecked = string.Equals(dir, _engine?.ClientDir, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => SelectClient(dir);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Browse for AnarchyOnline.exe…" };
        browse.Click += (_, _) => BrowseForClient();
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    void BrowseForClient()
    {
        var dlg = new OpenFileDialog {
            Title = "Find AnarchyOnline.exe in your client folder",
            Filter = "Anarchy Online|AnarchyOnline.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            SelectClient(Path.GetDirectoryName(dlg.FileName));
    }

    // --- actions ------------------------------------------------------------------------------

    async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AddonVm a) {
            Selected = a;
            await InstallAsync(a, a.Primary);
        }
    }

    async void OnReinstall(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AddonVm a)
            await InstallAsync(a, PrimaryAction.Repair);
    }

    async void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        foreach (var a in Updatable.ToList())
            if (!await InstallAsync(a, PrimaryAction.Update))
                break;
    }

    async Task<bool> InstallAsync(AddonVm a, PrimaryAction action)
    {
        if (!CheckCanModify())
            return false;
        var asset = a.FindAsset();
        if (asset == null) {
            MessageBox.Show(this, $"{a.Name}'s latest release has no download AoForge recognises.", "AoForge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var options = a.SelectedOptions();
        var conflicts = _engine.FindConflicts(a.Def, options, _index.Addons);
        if (conflicts.Count > 0) {
            string text = string.Join("\n\n", conflicts) + "\n\nContinue?";
            if (MessageBox.Show(this, text, $"Install {a.Name}", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
        }
        if (action == PrimaryAction.TakeOver) {
            string text = $"{a.Name} is already in this client folder, installed by hand. AoForge will replace it with {a.LatestTag} and keep it updated from now on. Your settings stay.";
            if (MessageBox.Show(this, text, $"Manage {a.Name}", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return false;
        }

        SetBusy(true);
        string verb = action switch { PrimaryAction.Update => "Updating", PrimaryAction.Repair => "Repairing", _ => "Installing" };
        try {
            Log($"{verb} {a.Name} {a.LatestTag}…");
            a.BusyText = "Downloading…";
            var progress = new Progress<double>(p => a.BusyText = $"Downloading {p:P0}");
            string zip = await _github.DownloadAsync(asset, progress);
            a.BusyText = $"{verb}…";
            var engine = _engine;
            var defs = _index.Addons.ToList();
            await Task.Run(() => engine.Install(a.Def, a.LatestTag, zip, options, line => Log($"  {line}"), defs));
            TryDelete(zip);
            Log($"{a.Name} {a.LatestTag} is installed.");
            a.BusyText = null;
            UpdateStatuses();
            if (a.HasNotes && action is PrimaryAction.Install or PrimaryAction.TakeOver)
                MessageBox.Show(this, a.Notes, $"{a.Name} is installed", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        } catch (Exception ex) {
            a.BusyText = null;
            Log($"{a.Name}: {ex.Message}");
            UpdateStatuses();
            MessageBox.Show(this, ex.Message, $"{verb} {a.Name} failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        } finally {
            SetBusy(false);
        }
    }

    async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AddonVm a || !CheckCanModify())
            return;
        string extra = RemoveUserFiles ? " Its settings and logs will be deleted too." : " Its settings and logs are kept.";
        if (MessageBox.Show(this, $"Uninstall {a.Name}?{extra}", "AoForge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        SetBusy(true);
        bool removeUserFiles = RemoveUserFiles;
        try {
            Log($"Uninstalling {a.Name}…");
            a.BusyText = "Removing…";
            var engine = _engine;
            await Task.Run(() => engine.Uninstall(a.Def, removeUserFiles, line => Log($"  {line}")));
            Log($"{a.Name} is uninstalled.");
        } catch (Exception ex) {
            Log($"{a.Name}: {ex.Message}");
            MessageBox.Show(this, ex.Message, $"Uninstall {a.Name} failed", MessageBoxButton.OK, MessageBoxImage.Error);
        } finally {
            a.BusyText = null;
            foreach (var o in a.Options)
                o.IsChecked = o.Option.Default;
            UpdateStatuses();
            SetBusy(false);
        }
    }

    bool CheckCanModify()
    {
        UpdateGameRunning();
        if (_engine == null) {
            BrowseForClient();
            return false;
        }
        if (_gameRunning) {
            MessageBox.Show(this, "Close Anarchy Online first: its files are in use while it runs.", "AoForge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return !_busy;
    }

    void OnOpenHomepage(object sender, RoutedEventArgs e) => OpenUrl(_selected?.Homepage);

    static void OpenUrl(string url)
    {
        if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    static void TryDelete(string path)
    {
        try {
            File.Delete(path);
        } catch (IOException) {
        }
    }
}
