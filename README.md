# VRChat PhotoSync

A small Windows app that watches your VRChat camera roll and uploads new photos to your [Immich](https://immich.app) server as you take them.

- Runs alongside VRChat. [VRCX](https://github.com/vrcx-team/VRCX) can start it when VRChat starts and close it when VRChat exits.
- A small window shows how many photos were uploaded this session and the name of the most recent one. It can also live in the system tray.
- Finds the photo folder from VRChat's own settings, so there is nothing to configure there.
- Waits a moment for VRCX to add its metadata (world and players) before uploading, so the uploaded photo has it.
- Skips other people's content (prints, stickers and emoji they shared), and by default your own prints and multi-layer photo layers.
- Safe to kill at any time: state is saved after every step, and anything unfinished is picked up at the next start. Immich detects duplicates by checksum, so nothing is uploaded twice.

Windows only. The release is a single standalone `.exe` (no .NET install needed).

## Contents

- [Getting started](#getting-started)
- [Creating an Immich API key](#creating-an-immich-api-key)
- [Where photos come from](#where-photos-come-from)
- [What gets uploaded and what is skipped](#what-gets-uploaded-and-what-is-skipped)
- [Waiting for VRCX metadata](#waiting-for-vrcx-metadata)
- [Starting it with VRCX](#starting-it-with-vrcx)
- [Command-line options](#command-line-options)
- [Where files are kept](#where-files-are-kept)
- [Building from source](#building-from-source)

## Getting started

1. Download `VRChatPhotoSync.exe` and put it in a folder of its own, for example `C:\Tools\VRChatPhotoSync\`. The app keeps its settings, state and logs next to the exe (see [Where files are kept](#where-files-are-kept)).
2. Start it. On the first run the **Immich server** window opens:
   - **Server URL:** your Immich address, for example `https://immich.example.com`. Adding `/api` is optional.
   - **API key:** paste a key (see [below](#creating-an-immich-api-key)). Once saved, the key is never shown again. Leave the box blank later to keep the saved key.
   - **Test connection** checks that the server is reachable, that it is Immich, and that the key works and has the permissions it needs.
   - **Album** (optional): every uploaded photo is also added to this album. It is created if it doesn't exist. Leave it blank to upload without an album.
   - **Also upload my N existing photos** (first setup only, unticked by default): tick it to upload the photos already in your camera roll. Otherwise only photos taken from now on are uploaded. You can do this later with **⋯ → Upload existing photos…** in the main window. Photos already on the server are recognised and not uploaded again.
   - **Advanced:** the Immich visibility for new photos (`timeline`, `archive`, `hidden`, `locked`), how long to wait for VRCX, and the exact-names check.
3. Click **Save**. The main window shows what is happening.

You never need to edit `config.json` by hand. Everything can be changed in the app: **Immich server…** reopens the window above, and the main window has checkboxes for **Skip prints** and **Upload photo layers**, plus a **⋯** menu with *Upload existing photos…*, *Window behaviour* (what the close and minimize buttons do, start minimized), *Open log* and *Open Immich*.

## Creating an Immich API key

In the Immich web UI, click your profile picture (top right) → **Account Settings** → **API Keys** → **New API Key**. The direct link is:

```
<server>/user-settings?isOpen=api-keys
```

for example `https://immich.example.com/user-settings?isOpen=api-keys`. The **How do I get an API key?** link in the app opens this page for the server you entered.

A dedicated key with only the permissions below is recommended:

| Permission | Needed for |
|---|---|
| `asset.upload` | **Required.** Uploading photos and checking for duplicates. |
| `album.read`, `album.create`, `albumAsset.create` | Only if you set an album. |
| `asset.copy`, `asset.delete` | Optional. See [late VRCX edits](#waiting-for-vrcx-metadata). |

A key with "all" permissions works too. **Test connection** lists anything that is missing.

The key is stored in `config.json` encrypted with Windows DPAPI for your Windows user. It is never written as plain text, never logged, and is useless if the file is copied to another account or PC. If you give the app to someone else, give them the exe only, not your `config.json`.

## Where photos come from

The photo folder is not set in this app. It is read from VRChat's own settings file, `%LocalAppData%Low\VRChat\VRChat\config.json` (key `picture_output_folder`), every time the app starts, and again if that file changes. The app only reads this file and never writes to it.

- If VRChat has no custom folder set, VRChat's default is used: `VRChat` inside your Windows **Pictures** folder (it follows the folder if you have moved Pictures to another drive or to OneDrive).
- The whole folder is watched, including subfolders. It doesn't matter whether VRChat's "split by month" option (`yyyy-MM` subfolders) is on or off, or if it was changed at some point.
- If the folder doesn't exist yet, the app waits for it to appear. It never creates it.
- The Immich server window shows the folder in use and where it came from. Use `--folder` to watch a different folder for one run.

## What gets uploaded and what is skipped

Only your own VRChat photos are uploaded: PNG files whose name starts with `VRChat_`.

| Content | What happens |
|---|---|
| Your photos, e.g. `VRChat_2024-11-23_19-17-27.652_1920x1080.png` | Uploaded. The capture time comes from the file name. |
| Anything in the `Prints\`, `Stickers\` and `Emoji\` subfolders | **Never uploaded.** VRChat keeps the prints, stickers and emoji other people shared with you there. |
| Your own prints (the polaroid-style 2048×1440 images with a white border and caption) | Skipped while **Skip prints** is ticked (the default). |
| Multi-layer photo layers (`…_Environment.png`, `…_Player.png`, `…_UI.png`) | Skipped unless **Upload photo layers** is ticked. The combined photo is always uploaded. |
| Hand-made copies such as `VRChat_… - Copy.png`, `… (1).png` or `…_edited.png` | Skipped while **Exact VRChat names only** (Advanced) is on, the default. Turn it off to accept any `VRChat_*.png`. |
| Files renamed by VRCX's "modify filename" option (`…_wrld_<id>.png`) | Uploaded once, under the final name. |
| JPGs, shortcuts, other files | Ignored. VRChat only saves PNG. |

Changing a checkbox applies to files from then on. It doesn't upload photos that were skipped earlier: use **⋯ → Upload existing photos…** for that, which follows the current settings.

When the app starts, it also catches up on photos taken while it wasn't running (since it last ran). The very first run doesn't upload your old photos unless you ask it to.

## Waiting for VRCX metadata

VRCX adds a block of metadata (world, players in the instance) to each photo about a second after VRChat saves it, by rewriting the file. To upload the photo with that metadata, PhotoSync waits briefly before uploading:

- If the metadata is already there, the photo uploads straight away.
- Otherwise it checks once a second, for up to **10 seconds** (Advanced → VRCX wait; 0 turns waiting off). If you don't use VRCX, each photo simply uploads after that wait.
- Multi-layer photos don't wait, because VRCX never adds metadata to them.
- Older photos found when catching up or uploading existing photos don't wait either.

If VRCX edits a photo after it was already uploaded (for example because VRCX was slow), the new version is uploaded too. If the key has `asset.copy` and `asset.delete`, the new version takes over the old one's albums and favourite, and the old one is moved to the Immich trash (not deleted permanently). Without those permissions both versions are kept and the log says so.

**Restarting VRCX while VRChat is running:** when VRCX starts, it re-reads the current VRChat log and adds its metadata again to every photo taken in that VRChat session, including photos that already have it. Because VRCX has only just started, this extra block usually has no world or player details. PhotoSync sees those photos change and replaces each one once, as above. Nothing is lost, but expect a few re-uploads (and trashed copies) after restarting VRCX mid-session.

## Starting it with VRCX

VRCX's **App Launcher** can start PhotoSync whenever VRChat starts:

1. Create a shortcut to `VRChatPhotoSync.exe`. To start it minimized, add `--minimized` to the shortcut's target, for example `"C:\Tools\VRChatPhotoSync\VRChatPhotoSync.exe" --minimized`.
2. Put the shortcut in `%AppData%\VRCX\startup\`. The App Launcher settings in VRCX have a button that opens this folder.
   - Shortcuts in `startup\` itself are launched every time.
   - Shortcuts in `startup\desktop\` are launched only in desktop mode (SteamVR not running), and those in `startup\vr\` only in VR mode.
3. Make sure the App Launcher is enabled in VRCX (it is by default).

Things to know:

- **VRChat must start after VRCX.** VRCX launches the apps when it sees VRChat start. If VRChat is already running when VRCX opens, nothing is launched.
- **When VRChat exits, VRCX kills PhotoSync** (if "auto close" is on, the default). It is a hard kill with no chance to shut down cleanly. That is fine: the app saves its state after every step, and anything that wasn't finished is uploaded at the next start.
- **Several VRChat clients:** closing a second client doesn't affect PhotoSync. Closing the first client (the one that triggered the launch) kills PhotoSync, and VRCX starts it again straight away because a client is still running. PhotoSync picks up where it left off, and the "uploaded this session" count carries on.
- Only one copy runs at a time. Starting it again brings the existing window to the front.

You can of course also start PhotoSync yourself, or put a shortcut in Windows' Startup folder.

## Command-line options

| Option | Effect |
|---|---|
| `--folder <path>` | Watch this folder instead of VRChat's photo folder. Useful for testing with a scratch folder. |
| `--server <url>` | Use this Immich server instead of the saved one. |
| `--minimized` | Start minimized (to the taskbar or tray, following the minimize setting). |
| `--settings` | Open the Immich server window at startup. |

Options apply to that run only and are never saved. `--name=value` works as well as `--name value`, and option names ignore case.

## Where files are kept

Everything the app writes goes in the folder next to the exe, so the app is portable. If that folder can't be written to (for example under `C:\Program Files`), it uses `%APPDATA%\VRChatPhotoSync\` instead.

| File | Contents |
|---|---|
| `config.json` | Settings: server, encrypted API key, album, checkboxes, window position. See [`config.example.json`](config.example.json). |
| `state.json` | Which files were uploaded or skipped (with their Immich ids), when the folder was last scanned, and the session counters. Deleting it makes the next start behave like a first run (no mass upload). |
| `logs\photosync-YYYYMMDD.log` | One log per day, kept for 7 days. **⋯ → Open log** opens today's. The API key is never logged. |

The app never writes to your camera roll or to VRChat's settings.

## Building from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test
```

Run it against a scratch folder while developing:

```powershell
dotnet run --project src\VRChatPhotoSync -- --folder D:\Temp\PhotoSyncTest
```

Build the single-file, self-contained exe (about 50 MB) into `publish\`:

```powershell
powershell -File tools\publish.ps1
```

### Releases

Pushing a tag `v<version>` runs `.github/workflows/release.yml` on GitHub Actions. It builds, runs the tests, publishes the exe the same way as `tools/publish.ps1`, and attaches `VRChatPhotoSync.exe` to a GitHub release for the tag. The tag must match `<Version>` in `src/VRChatPhotoSync/VRChatPhotoSync.csproj` (for example `v0.1.0`), so bump that first. A version with a suffix (`0.2.0-beta.1`) becomes a pre-release. The integration tests run only if the repository secrets `IMMICH_TEST_URL` and `IMMICH_TEST_API_KEY` are set.

### Integration tests

The tests tagged `Category=Integration` talk to a real Immich server. They are skipped unless a server is configured, either with the environment variables `IMMICH_TEST_URL` and `IMMICH_TEST_API_KEY`, or with a git-ignored `secrets.local.json` at the repository root:

```json
{ "serverUrl": "https://immich.example.com", "apiKey": "<your test key>" }
```

They upload generated test images and move them to the trash afterwards. Use a test account or a dedicated key. To run only the unit tests:

```powershell
dotnet test --filter "Category!=Integration"
```

### Project layout

- `src/VRChatPhotoSync/`: the app (WinForms, `net10.0-windows`, no NuGet dependencies).
- `tests/VRChatPhotoSync.Tests/`: xUnit tests. They generate their own test images.
- `tools/publish.ps1`: release build. `.github/workflows/release.yml`: builds and releases the exe on a `v*` tag. `tools/watch-vrcx.ps1`: measures when VRCX adds its metadata to new photos.
- `docs/print_mask.png`: the print layout used to detect prints.
- `PLAN.md`: the design notes and research behind the app.
