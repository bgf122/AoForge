using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AoForge.Models;
using AoForge.Services;
using Xunit;

namespace AoForge.Tests;

// Runs the real index recipes against throwaway client folders.
public sealed class InstallEngineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "AoForgeTests", Guid.NewGuid().ToString("N"));
    readonly string _client;
    readonly AddonIndex _index;
    readonly List<string> _log = new();

    public InstallEngineTests()
    {
        _client = Path.Combine(_root, "client");
        Directory.CreateDirectory(_client);
        Write("AnarchyOnline.exe", "game exe");
        Write("randy31.dll", "game renderer");
        _index = Json.ReadFile<AddonIndex>(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    AddonDef AoVk => _index.Addons.Single(a => a.Id == "ao-vk");
    AddonDef Clarity => _index.Addons.Single(a => a.Id == "clarity");
    static readonly string[] WithAor = { "aoreloaded" };
    static readonly string[] NoOptions = { };

    void Write(string rel, string text) => File.WriteAllText(Path.Combine(_client, rel), text);
    string Read(string rel) => File.ReadAllText(Path.Combine(_client, rel));
    bool Exists(string rel) => File.Exists(Path.Combine(_client, rel));
    InstallEngine Engine() => new(_client);
    void Log(string s) => _log.Add(s);

    // A release zip like ao-vk's: everything inside one top-level folder.
    string Zip(string folder, params (string Name, string Text)[] files)
    {
        string path = Path.Combine(_root, $"{folder}-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files) {
            var entry = zip.CreateEntry(folder == null ? name : $"{folder}/{name}");
            using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(text);
            s.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    string AoVkZip(string ver) => Zip($"ao-vk-{ver}", ("randy31.dll", $"ao-vk {ver}"), ("version.dll", $"aor {ver}"), ("INSTALL.txt", "docs"));

    [Fact]
    public void Fresh_install_renames_original_and_copies_files()
    {
        var e = Engine();
        Assert.Equal(AddonState.NotInstalled, e.GetStatus(AoVk, "v1").State);

        e.Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);

        Assert.Equal("game renderer", Read("randy31_orig.dll"));
        Assert.Equal("ao-vk 1", Read("randy31.dll"));
        Assert.Equal("aor 1", Read("version.dll"));
        Assert.False(Exists("INSTALL.txt"));
        Assert.Equal(AddonState.Installed, Engine().GetStatus(AoVk, "v1").State);
        Assert.Equal(AddonState.UpdateAvailable, Engine().GetStatus(AoVk, "v2").State);
    }

    [Fact]
    public void Update_keeps_the_original_and_replaces_files()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        Engine().Install(AoVk, "v2", AoVkZip("2"), WithAor, Log);

        Assert.Equal("game renderer", Read("randy31_orig.dll"));
        Assert.Equal("ao-vk 2", Read("randy31.dll"));
        Assert.Equal("aor 2", Read("version.dll"));
        Assert.Equal(AddonState.Installed, Engine().GetStatus(AoVk, "v2").State);
    }

    [Fact]
    public void Uninstall_restores_the_original_and_keeps_settings()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        Write("randy-vk.ini", "my settings");

        Engine().Uninstall(AoVk, removeUserFiles: false, Log);

        Assert.Equal("game renderer", Read("randy31.dll"));
        Assert.False(Exists("randy31_orig.dll"));
        Assert.False(Exists("version.dll"));
        Assert.True(Exists("randy-vk.ini"));
        Assert.Equal(AddonState.NotInstalled, Engine().GetStatus(AoVk, "v1").State);
        Assert.Null(Engine().Find("ao-vk"));
    }

    [Fact]
    public void Uninstall_can_remove_user_files()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        Write("randy-vk.ini", "x");
        Write("randy-vk.log", "x");
        Write("randy-vk-2.log", "x");
        Write("other.log", "x");

        Engine().Uninstall(AoVk, removeUserFiles: true, Log);

        Assert.False(Exists("randy-vk.ini"));
        Assert.False(Exists("randy-vk.log"));
        Assert.False(Exists("randy-vk-2.log"));
        Assert.True(Exists("other.log"));
    }

    [Fact]
    public void Repair_after_the_launcher_restores_the_game_file()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        Write("randy31.dll", "game renderer, patched");

        var status = Engine().GetStatus(AoVk, "v1");
        Assert.Equal(AddonState.NeedsRepair, status.State);
        Assert.NotEmpty(status.Problems);

        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);

        Assert.Equal("game renderer, patched", Read("randy31_orig.dll"));
        Assert.Equal("ao-vk 1", Read("randy31.dll"));
        Assert.Equal(AddonState.Installed, Engine().GetStatus(AoVk, "v1").State);
    }

    [Fact]
    public void Uninstall_after_the_launcher_restored_the_game_file_keeps_it()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        Write("randy31.dll", "game renderer, patched");

        Engine().Uninstall(AoVk, removeUserFiles: false, Log);

        Assert.Equal("game renderer, patched", Read("randy31.dll"));
        Assert.False(Exists("randy31_orig.dll"));
    }

    [Fact]
    public void Missing_original_with_addon_in_place_refuses_without_changes()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        File.Delete(Path.Combine(_client, "randy31_orig.dll"));
        Assert.Equal(AddonState.NeedsRepair, Engine().GetStatus(AoVk, "v1").State);

        Assert.Throws<InvalidOperationException>(() => Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log));
        Assert.False(Exists("randy31_orig.dll"));
        Assert.Equal("ao-vk 1", Read("randy31.dll"));
    }

    [Fact]
    public void Turning_an_option_off_removes_its_file_on_update()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        Engine().Install(AoVk, "v2", AoVkZip("2"), NoOptions, Log);

        Assert.False(Exists("version.dll"));
        Assert.Equal("ao-vk 2", Read("randy31.dll"));
        Assert.Empty(Engine().Find("ao-vk").OptionList);
    }

    [Fact]
    public void Hand_installed_copy_is_detected_and_taken_over_without_backups()
    {
        Write("randy31_orig.dll", "game renderer");
        Write("randy31.dll", "ao-vk 0.6");
        Write("version.dll", "aor 0.6");

        var e = Engine();
        Assert.Equal(AddonState.ManualInstall, e.GetStatus(AoVk, "v1").State);
        Assert.Empty(e.FindConflicts(AoVk, WithAor, _index.Addons));

        e.Install(AoVk, "v1", AoVkZip("1"), WithAor, Log);
        Assert.Equal("game renderer", Read("randy31_orig.dll"));
        Assert.Equal("ao-vk 1", Read("randy31.dll"));

        Engine().Uninstall(AoVk, removeUserFiles: false, Log);
        Assert.Equal("game renderer", Read("randy31.dll"));
        Assert.False(Exists("version.dll"));
        Assert.False(Exists("randy31_orig.dll"));
    }

    [Fact]
    public void Hand_install_version_comes_from_the_version_resource_only()
    {
        // AoForge.exe itself has a version resource: ProductName "AoForge", its version + build metadata.
        string exe = typeof(InstallEngine).Assembly.Location;
        File.Copy(exe, Path.Combine(_client, "probe.dll"));
        var def = new AddonDef {
            Id = "probe", Name = "Probe",
            Detect = new() { new DetectRule { File = "probe.dll", ProductName = "AoForge" } },
        };
        var status = Engine().GetStatus(def, "v9");
        Assert.Equal(AddonState.ManualInstall, status.State);
        Assert.Equal(SelfUpdater.Current.ToString(3), status.DetectedVersion);

        // ao-vk is detected by randy31_orig.dll, which says nothing about the installed version.
        Write("randy31_orig.dll", "game renderer");
        var aovk = Engine().GetStatus(AoVk, "v1");
        Assert.Equal(AddonState.ManualInstall, aovk.State);
        Assert.Null(aovk.DetectedVersion);
    }

    AddonDef MerkUI => _index.Addons.Single(a => a.Id == "merkui");

    // A release zip like MerkUI's: the files to install under files/, scripts and docs beside them.
    string MerkZip(string ver) => Zip($"MerkUI-{ver}",
        ("files/version.dll", $"MerkUI dll {ver}"),
        ("files/MerkUI_LoginFrame.bmp", "bmp"),
        ("files/MerkUI_Skin/merkui_skin.txt", $"MerkUI skin {ver}-omni1"),
        ("files/MerkUI_Skin/Views/LoginWindow.xml", "<xml/>"),
        ("Install.bat", "bat"),
        ("README.md", "readme"));

    [Fact]
    public void Folder_install_copies_the_tree_and_uninstall_removes_it_with_its_folders()
    {
        Engine().Install(MerkUI, "v1.1.24", MerkZip("1.1.24"), NoOptions, Log, _index.Addons);

        Assert.Equal("MerkUI dll 1.1.24", Read("version.dll"));
        Assert.Equal("bmp", Read("MerkUI_LoginFrame.bmp"));
        Assert.Equal("<xml/>", Read(@"MerkUI_Skin\Views\LoginWindow.xml"));
        Assert.False(Exists("Install.bat"));
        Assert.False(Exists("README.md"));
        Assert.Equal(4, Engine().Find("merkui").FileList.Count);
        Assert.Equal(AddonState.Installed, Engine().GetStatus(MerkUI, "v1.1.24").State);

        Engine().Uninstall(MerkUI, removeUserFiles: false, Log);
        Assert.False(Exists("version.dll"));
        Assert.False(Directory.Exists(Path.Combine(_client, "MerkUI_Skin")));
        Assert.True(Exists("AnarchyOnline.exe"));
    }

    [Fact]
    public void Taking_over_another_addons_file_turns_its_option_off()
    {
        Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log, _index.Addons);
        var e = Engine();
        Assert.Contains(e.FindConflicts(MerkUI, NoOptions, _index.Addons), c => c.Contains("ao-vk") && c.Contains("AOReloaded"));

        e.Install(MerkUI, "v1.1.24", MerkZip("1.1.24"), NoOptions, Log, _index.Addons);

        var aovk = Engine().Find("ao-vk");
        Assert.DoesNotContain(aovk.FileList, f => f.Path == "version.dll");
        Assert.DoesNotContain("aoreloaded", aovk.OptionList);
        Assert.Equal(AddonState.Installed, Engine().GetStatus(AoVk, "v1").State); // no repair needed
        Assert.Equal("MerkUI dll 1.1.24", Read("version.dll"));

        // ao-vk's next update leaves MerkUI's version.dll alone.
        Engine().Install(AoVk, "v2", AoVkZip("2"), Engine().Find("ao-vk").OptionList, Log, _index.Addons);
        Assert.Equal("MerkUI dll 1.1.24", Read("version.dll"));
        Assert.Equal(AddonState.Installed, Engine().GetStatus(MerkUI, "v1.1.24").State);

        // Uninstalling MerkUI removes its version.dll; ao-vk stays installed without AOReloaded.
        Engine().Uninstall(MerkUI, removeUserFiles: false, Log);
        Assert.False(Exists("version.dll"));
        Assert.Equal("ao-vk 2", Read("randy31.dll"));
    }

    [Fact]
    public void Hand_installed_MerkUI_is_detected_with_its_version()
    {
        Directory.CreateDirectory(Path.Combine(_client, "MerkUI_Skin"));
        Write(@"MerkUI_Skin\merkui_skin.txt", "MerkUI skin 1.1.24-omni631");
        Write("version.dll", "MZ ... MerkUI ...");

        var status = Engine().GetStatus(MerkUI, "v1.1.25");
        Assert.Equal(AddonState.ManualInstall, status.State);
        Assert.Equal("1.1.24", status.DetectedVersion);
    }

    [Fact]
    public void An_AOReloaded_version_dll_is_not_taken_for_MerkUI()
    {
        Write("version.dll", "aor 0.1.0");
        Assert.Equal(AddonState.NotInstalled, Engine().GetStatus(MerkUI, "v1").State);
    }

    [Fact]
    public void Unknown_install_step_is_refused_before_any_change()
    {
        var def = new AddonDef {
            Id = "future", Name = "Future",
            Steps = new() { new RecipeStep { Action = "runScript", From = "x" }, new RecipeStep { Action = StepAction.Copy, From = "a.dll", To = "a.dll" } },
        };
        var ex = Assert.Throws<InvalidDataException>(() => Engine().Install(def, "v1", Zip(null, ("a.dll", "x")), NoOptions, Log));
        Assert.Contains("newer AoForge", ex.Message);
        Assert.False(Exists("a.dll"));
    }

    [Fact]
    public void Missing_folder_in_the_download_fails_before_any_change()
    {
        string zip = Zip("MerkUI-1", ("README.md", "x"));
        Assert.Throws<InvalidDataException>(() => Engine().Install(MerkUI, "v1", zip, NoOptions, Log, _index.Addons));
        Assert.False(Exists("version.dll"));
        Assert.Null(Engine().Find("merkui"));
    }

    [Fact]
    public void Foreign_file_is_reported_backed_up_and_put_back()
    {
        Write("dinput.dll", "another mod");
        var e = Engine();
        Assert.Equal(AddonState.NotInstalled, e.GetStatus(Clarity, "v0.1.0").State); // no "Clarity" ProductName
        Assert.Single(e.FindConflicts(Clarity, NoOptions, _index.Addons));

        e.Install(Clarity, "v0.1.0", Zip(null, ("dinput.dll", "clarity"), ("README.md", "r")), NoOptions, Log);
        Assert.Equal("clarity", Read("dinput.dll"));

        Engine().Uninstall(Clarity, removeUserFiles: false, Log);
        Assert.Equal("another mod", Read("dinput.dll"));
    }

    [Fact]
    public void File_owned_by_another_addon_is_a_conflict()
    {
        var other = new AddonDef {
            Id = "aoreloaded", Name = "AOReloaded",
            Steps = new() { new RecipeStep { Action = StepAction.Copy, From = "version.dll", To = "version.dll" } },
        };
        Engine().Install(other, "v9", Zip("AOReloaded", ("version.dll", "aor 9")), NoOptions, Log);

        var conflicts = Engine().FindConflicts(AoVk, WithAor, _index.Addons.Append(other));
        Assert.Contains(conflicts, c => c.Contains("AOReloaded"));
    }

    [Fact]
    public void Missing_zip_entry_fails_before_any_change()
    {
        string zip = Zip("ao-vk-1", ("version.dll", "aor 1"));
        Assert.Throws<InvalidDataException>(() => Engine().Install(AoVk, "v1", zip, WithAor, Log));
        Assert.Equal("game renderer", Read("randy31.dll"));
        Assert.False(Exists("randy31_orig.dll"));
        Assert.Null(Engine().Find("ao-vk"));
    }

    [Theory]
    [InlineData(@"..\evil.dll")]
    [InlineData("../evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"sub\..\..\evil.dll")]
    public void Paths_outside_the_client_folder_are_refused(string to)
    {
        var def = new AddonDef {
            Id = "evil", Name = "Evil",
            Steps = new() { new RecipeStep { Action = StepAction.Copy, From = "a.dll", To = to } },
        };
        Assert.Throws<InvalidDataException>(() => Engine().Install(def, "v1", Zip(null, ("a.dll", "x")), NoOptions, Log));
        Assert.False(File.Exists(Path.Combine(_root, "evil.dll")));
    }

    [Fact]
    public void Locked_file_fails_before_any_change()
    {
        using (new FileStream(Path.Combine(_client, "randy31.dll"), FileMode.Open, FileAccess.Read, FileShare.None)) {
            Assert.Throws<IOException>(() => Engine().Install(AoVk, "v1", AoVkZip("1"), WithAor, Log));
        }
        Assert.False(Exists("randy31_orig.dll"));
        Assert.Equal("game renderer", Read("randy31.dll"));
    }
}
