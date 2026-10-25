using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using AoForge.Models;
using AoForge.Services;

namespace AoForge.ViewModels;

public enum PrimaryAction { None, Install, Update, Repair, TakeOver }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class OptionVm : Observable
{
    bool _isChecked;

    public OptionVm(AddonOption option, bool isChecked)
    {
        Option = option;
        _isChecked = isChecked;
    }

    public AddonOption Option { get; }
    public string Label => Option.Label;
    public string Description => Option.Description;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

// One release in an addon's patch notes.
public sealed class ReleaseNoteVm : Observable
{
    bool _isExpanded, _isNew, _isInstalled;

    public ReleaseNoteVm(GhRelease release, bool isLatest)
    {
        Release = release;
        IsLatest = isLatest;
    }

    public GhRelease Release { get; }
    public string Tag => Release.TagName;
    public string Title => string.IsNullOrWhiteSpace(Release.Name) ? Release.TagName : Release.Name;
    public string Date => DateTime.TryParse(Release.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";
    public string Body => string.IsNullOrWhiteSpace(Release.Body) ? "No notes for this release." : Release.Body.Replace("\r\n", "\n").Trim();
    public bool IsLatest { get; }

    // "New" (newer than the installed one), "Installed", "Latest" or "Prerelease"; null for none.
    public string Badge => _isNew ? "New" : _isInstalled ? "Installed" : IsLatest ? "Latest" : Release.Prerelease ? "Prerelease" : null;
    public bool HasBadge => Badge != null;

    // Set by the user (the expander's two-way binding); then the default no longer applies.
    public bool IsExpanded
    {
        get => _isExpanded;
        set { Toggled = true; Set(ref _isExpanded, value); }
    }

    public bool Toggled { get; private set; }

    public void SetDefaultExpanded(bool expanded)
    {
        if (!Toggled)
            Set(ref _isExpanded, expanded, nameof(IsExpanded));
    }

    public bool IsNew
    {
        get => _isNew;
        set { if (Set(ref _isNew, value)) { Raise(nameof(Badge)); Raise(nameof(HasBadge)); } }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set { if (Set(ref _isInstalled, value)) { Raise(nameof(Badge)); Raise(nameof(HasBadge)); } }
    }
}

public sealed class AddonVm : Observable
{
    static readonly Color[] IconColors = {
        Color.FromRgb(0xE8, 0x6A, 0x33), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81),
        Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF5, 0x9E, 0x0B),
    };

    AddonStatus _status = new() { State = AddonState.NotInstalled };
    List<GhRelease> _releases = new();
    GhRelease _release;
    string _releaseError;
    string _busyText;

    public AddonVm(AddonDef def)
    {
        Def = def;
        int hash = def.Id.Aggregate(17, (h, c) => h * 31 + c);
        IconBrush = new SolidColorBrush(IconColors[(hash & 0x7fffffff) % IconColors.Length]);
        IconBrush.Freeze();
        Options = new ObservableCollection<OptionVm>(def.OptionList.Select(o => new OptionVm(o, o.Default)));
    }

    public AddonDef Def { get; }
    public string Id => Def.Id;
    public string Name => Def.Name;
    public string Author => Def.Author;
    public string ByAuthor => string.IsNullOrEmpty(Def.Author) ? "" : "by " + Def.Author;
    public string Summary => Def.Summary;
    public string Description => Def.Description;
    public string Notes => Def.Notes;
    public bool HasNotes => !string.IsNullOrEmpty(Def.Notes);
    public string License => Def.License;
    public string Homepage => Def.Homepage ?? (Def.Repo != null ? "https://github.com/" + Def.Repo : null);
    public string Initials => new string(Def.Name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
    public SolidColorBrush IconBrush { get; }
    public ObservableCollection<OptionVm> Options { get; }
    public bool HasOptions => Options.Count > 0;

    public AddonStatus Status
    {
        get => _status;
        set { _status = value; MarkPatchNotes(); RaiseAll(); }
    }

    // The repo's recent releases, newest first. The latest is the one installed and updated to;
    // all of them are listed with their patch notes.
    public List<GhRelease> Releases
    {
        get => _releases;
        set {
            _releases = value ?? new List<GhRelease>();
            _release = GitHubReleases.Latest(_releases);
            var old = PatchNotes.Where(n => n.Toggled).ToDictionary(n => n.Tag ?? "", n => n.IsExpanded);
            PatchNotes.Clear();
            foreach (var r in _releases.Where(r => !r.Draft)) {
                var note = new ReleaseNoteVm(r, r == _release);
                if (old.TryGetValue(note.Tag ?? "", out bool expanded))
                    note.IsExpanded = expanded;
                PatchNotes.Add(note);
            }
            MarkPatchNotes();
            RaiseAll();
        }
    }

    public ObservableCollection<ReleaseNoteVm> PatchNotes { get; } = new();
    public bool HasPatchNotes => PatchNotes.Count > 0;

    // Marks the installed release and the ones newer than it. The latest release and the new ones
    // are open, older ones closed, except for those the user opened or closed.
    void MarkPatchNotes()
    {
        string installed = State switch {
            AddonState.Installed or AddonState.UpdateAvailable or AddonState.NeedsRepair => _status.InstalledTag,
            AddonState.ManualInstall => _status.DetectedVersion,
            _ => null,
        };
        Version installedVersion = SelfUpdater.ParseTag(installed);
        foreach (var n in PatchNotes) {
            Version v = SelfUpdater.ParseTag(n.Tag);
            n.IsInstalled = installed != null && (SameVersion(n.Tag ?? "", installed) || (v != null && v == installedVersion));
            n.IsNew = installedVersion != null && v != null && v > installedVersion && !n.Release.Prerelease;
            n.SetDefaultExpanded(n.IsLatest || n.IsNew);
        }
    }

    public string ReleaseError
    {
        get => _releaseError;
        set { _releaseError = value; RaiseAll(); }
    }

    public string BusyText
    {
        get => _busyText;
        set { _busyText = value; RaiseAll(); }
    }

    public bool IsBusy => _busyText != null;

    // The index says this addon needs a newer AoForge than this one (e.g. a new install step).
    public bool NeedsNewerAoForge =>
        SelfUpdater.ParseTag(Def.MinAoForge) is Version min && min > SelfUpdater.Current;
    public AddonState State => _status.State;
    public bool IsPresent => State != AddonState.NotInstalled;
    public string LatestTag => _release?.TagName;

    public GhAsset FindAsset() =>
        _release?.Assets?.FirstOrDefault(a => Regex.IsMatch(a.Name ?? "", Def.AssetPattern ?? ".*", RegexOptions.IgnoreCase));

    public string VersionText => State switch {
        AddonState.UpdateAvailable => $"{_status.InstalledTag}  →  {LatestTag}",
        AddonState.Installed or AddonState.NeedsRepair => _status.InstalledTag,
        AddonState.ManualInstall => ManualVersionText,
        _ => LatestTag ?? (_releaseError != null ? "offline" : "no release yet"),
    };

    // A hand install's version is only known when its file has a version resource (Clarity does,
    // ao-vk doesn't), so never show the latest release's number as if it were installed.
    string ManualVersionText
    {
        get {
            string detected = _status.DetectedVersion;
            if (detected == null)
                return LatestTag != null ? $"version unknown  ·  latest {LatestTag}" : "version unknown";
            string tag = "v" + detected.TrimStart('v', 'V');
            return LatestTag != null && !SameVersion(tag, LatestTag) ? $"{tag}  →  {LatestTag}" : tag;
        }
    }

    static bool SameVersion(string a, string b) =>
        string.Equals(a.TrimStart('v', 'V'), b.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    public string StatusText => IsBusy ? _busyText : NeedsNewerAoForge && State == AddonState.NotInstalled ? $"Needs AoForge {Def.MinAoForge}" : State switch {
        AddonState.Installed => "Installed",
        AddonState.UpdateAvailable => "Update available",
        AddonState.ManualInstall => "Installed by hand",
        AddonState.NeedsRepair => "Needs repair",
        _ => "Not installed",
    };

    public string StatusKind => IsBusy ? "busy" : State switch {
        AddonState.Installed => "ok",
        AddonState.UpdateAvailable => "update",
        AddonState.ManualInstall => "info",
        AddonState.NeedsRepair => "warn",
        _ => "none",
    };

    public PrimaryAction Primary => State switch {
        AddonState.NotInstalled => PrimaryAction.Install,
        AddonState.UpdateAvailable => PrimaryAction.Update,
        AddonState.NeedsRepair => PrimaryAction.Repair,
        AddonState.ManualInstall => PrimaryAction.TakeOver,
        _ => PrimaryAction.None,
    };

    public string PrimaryText => Primary switch {
        PrimaryAction.Install => "Install",
        PrimaryAction.Update => "Update",
        PrimaryAction.Repair => "Repair",
        PrimaryAction.TakeOver => "Manage",
        _ => "",
    };

    public string PrimaryHint => Primary switch {
        PrimaryAction.TakeOver => $"Replace the hand-installed copy with {LatestTag ?? "the latest release"} and let AoForge update it from now on.",
        PrimaryAction.Repair => string.Join("\n", _status.Problems),
        _ => null,
    };

    public bool CanPrimary => Primary != PrimaryAction.None && !IsBusy && !NeedsNewerAoForge && FindAsset() != null;
    public bool CanUninstall => !IsBusy && State is AddonState.Installed or AddonState.UpdateAvailable or AddonState.NeedsRepair;
    public bool CanReinstall => !IsBusy && !NeedsNewerAoForge && State == AddonState.Installed && FindAsset() != null;

    public string Problems => string.Join("\n", _status.Problems);
    public bool HasProblems => _status.Problems.Count > 0;

    // Shown instead of the patch notes when there are none to show.
    public string ReleaseMessage =>
        _releaseError != null ? "Couldn't check for releases: " + _releaseError
        : PatchNotes.Count == 0 ? "No release published yet."
        : null;
    public bool HasReleaseMessage => ReleaseMessage != null;

    public List<string> SelectedOptions() => Options.Where(o => o.IsChecked).Select(o => o.Option.Id).ToList();

    public void LoadOptions(IEnumerable<string> installed)
    {
        var set = new HashSet<string>(installed);
        foreach (var o in Options)
            o.IsChecked = set.Contains(o.Option.Id);
    }

    public bool Matches(string search) =>
        string.IsNullOrWhiteSpace(search)
        || Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
        || (Summary ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
        || (Author ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
}
