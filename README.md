# AoForge

AoForge is an addon manager for the Anarchy Online client: browse addons, then install, update and remove them with one click.

- **Addons:** finds your client folder from the Project Rubi-Ka launcher's settings, or lets you browse to `AnarchyOnline.exe`.
- **Install, update, uninstall:** downloads each addon's latest release from GitHub and follows its install recipe, e.g. keeping the game's original `randy31.dll` for ao-vk.
- **Undo:** records every change in `AoForge\installed.json` inside the client folder, so uninstalling puts things back exactly as they were. Files that weren't the addon's are backed up first.
- **Repair:** notices when the game's launcher puts its own file back over an addon, and offers to fix it.
- **Safe while playing:** won't touch anything while the game is running.
- **Takes over hand installs:** recognises addons you installed by hand and can manage them from then on.

## Addons
| Addon | What it does |
|---|---|
| [ao-vk](https://github.com/wannabuh/ao-vk) | Vulkan renderer: higher frame rates, FSR upscaling, new sky and ground. Optionally with [AOReloaded](https://github.com/wannabuh/AOReloaded). |
| [Clarity](https://github.com/bgf122/Clarity) | Floating combat text, nameplates, and a settings tab. |
| [MerkUI](https://github.com/MerkXL/MerkUI) | A whole new interface: bars, buff trackers, action bars, minimap, themes. Takes the place of AOReloaded. |

The list lives in [index.json](index.json). AoForge reads it from this repo each time it starts. To add an addon, open a pull request with its entry.

## Install
1. Download `AoForge.exe` from [Releases](../../releases). It needs no install: put it anywhere and run it. It runs on Windows 10 and 11 (.NET Framework 4.8, which Windows already has).
2. **SmartScreen.** Windows may say "Windows protected your PC", because the exe isn't code-signed. Choose **More info → Run anyway**.
3. **Updates.** AoForge keeps itself up to date: when a new version is out, a bar at the top offers **Update AoForge**. It downloads the new exe, swaps it in and restarts. If AoForge sits in a folder it can't write to, such as Program Files, the button opens the download page instead. While it's open, AoForge checks for a new version of itself every 2 minutes. It checks your addons at start and when you press **Check for updates**.
4. **Tray.** Minimizing AoForge moves it to the notification area by the clock, where it keeps checking for updates and tells you when a new version is out. Click the icon to open it; right-click for Check for updates and Exit. Closing the window exits AoForge.

## Where it keeps things
- `<client folder>\AoForge\installed.json`: what AoForge installed. Backups of replaced files sit next to it.
- `%APPDATA%\AoForge\`: settings, the release cache and `AoForge.log`.

## Adding an addon to index.json
```json
{
  "id": "my-addon",
  "name": "My Addon",
  "author": "you",
  "summary": "One line for the list.",
  "description": "A paragraph for the details pane.",
  "repo": "you/my-addon",
  "license": "MIT",
  "assetPattern": "^my-addon-.*\\.zip$",
  "steps": [
    { "action": "copy", "from": "my-addon.dll", "to": "my-addon.dll" }
  ],
  "detect": [ { "file": "my-addon.dll", "productName": "My Addon" } ],
  "userFiles": [ "my-addon.ini", "my-addon*.log" ],
  "notes": "Shown once after installing."
}
```
- **Steps:**
  - `copy` takes `from`, a path inside the release zip, where a single top-level folder is ignored, and copies it to `to`, a path in the client folder.
  - `copyTree` copies every file under the zip folder `from` into the folder `to` (`""` = the client folder itself). Uninstalling also removes the folders it leaves empty.
  - `preserveOriginal` renames the game's `file` to `as` once and puts it back on uninstall.
  - A step with `"option": "<id>"` runs only when that entry in `options` is ticked.
- **Shared files:** if a file belongs to another installed addon (for example `version.dll`, which both AOReloaded and MerkUI use), installing takes it over. The other addon stops claiming it, and the option that installed it is switched off.
- **Paths** must stay inside the client folder. AoForge refuses anything else.
- **detect** recognises a copy installed by hand: a `file`, optionally with its `productName` or with text it `contains`. Its version comes from the file's version resource, or from `versionPattern`, a regex whose first group is the version, applied to the file's text.
- **userFiles** are deleted only if the user asks for that when uninstalling.
- **minAoForge:** the oldest AoForge that can install the entry. Older ones show "Needs AoForge x.y.z". AoForge refuses install steps it doesn't know.

## Building
```
dotnet build AoForge.sln -c Release
dotnet test AoForge.sln -c Release
```
Requirements: the .NET SDK and the .NET Framework 4.8 targeting pack (both come with Visual Studio). Pushing a `v*` tag builds a release on GitHub Actions.

For testing, `AoForge.exe --index <path or URL>` uses another addon list.

## Licence
MIT, see [LICENSE](LICENSE). AoForge downloads addons from their authors' own GitHub releases; each addon has its own licence.

AoForge is a fan project. It is not affiliated with or endorsed by Funcom. Anarchy Online is a trademark of Funcom.
