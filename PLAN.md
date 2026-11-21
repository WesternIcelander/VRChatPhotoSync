# VRChat PhotoSync — Implementation Plan

A small Windows (.NET) app that watches a VRChat camera roll folder and uploads new photos to an Immich server. VRCX starts it when VRChat starts and closes it when VRChat exits. A small window shows how many photos were uploaded this session and the name of the most recent one.

The photo folder is read from VRChat's own config, so it is never stored twice. The server is set in our config file. This means the tool can be given to someone else with a similar setup.

---

> Paths and server addresses in this plan are examples (`D:\Photos\VRChat`, `https://immich.example.com`). The developer's real values are in `CLAUDE.local.md`, which is not committed.

## Research summary

Checked against the Immich OpenAPI spec (`open-api/immich-openapi-specs.json`, v3.4.0) and a live Immich 3.4.2 server (`GET <server>/api/server/version`).

### Auth
- Send the header `x-api-key: <key>` on every request. The user creates the key in the Immich web UI: profile picture (top right) → Account Settings → API Keys. The direct link is `<server root>/user-settings?isOpen=api-keys`, for example `https://immich.example.com/user-settings?isOpen=api-keys`.
- The API base URL ends in `/api`, for example `https://immich.example.com/api`.
- Minimum key permission: **`asset.upload`**. If the optional album feature is used, the key also needs **`album.read`**, **`album.create`** and **`albumAsset.create`**.

### Endpoints used

| Purpose | Request | Permission | Notes |
|---|---|---|---|
| Is the server reachable? | `GET /api/server/ping` | none | Returns `{"res":"pong"}` |
| Is the key valid, and what can it do? | `GET /api/api-keys/me` | none (only a valid key) | Returns the key's `permissions` array (`"all"` or a list) |
| Upload | `POST /api/assets` (multipart/form-data) | `asset.upload` | See below |
| Check many files before uploading (backfill) | `POST /api/assets/bulk-upload-check` | `asset.upload` | Body: `{"assets":[{"id":"<client id, e.g. path>","checksum":"<sha1 hex or base64>"}]}`. Result items: `{id, action:"accept"\|"reject", reason?:"duplicate"\|"unsupported-format", assetId?, isTrashed?}` |
| Find album | `GET /api/albums?name=<exact name>` | `album.read` | Returns an array |
| Create album | `POST /api/albums` `{"albumName": "..."}` | `album.create` | 201 → album with `id` |
| Add to album | `PUT /api/albums/{id}/assets` `{"ids":["<assetId>"]}` | `albumAsset.create` | Returns `[{id, success, error?}]`. `error: "duplicate"` means the asset is already in the album and is fine |
| Move albums etc. to a replacement (optional) | `PUT /api/assets/copy` `{"sourceId","targetId"}` | `asset.copy` | 204. Copies albums, favourite, shared links, stack and sidecar |
| Trash the old version (optional) | `DELETE /api/assets` `{"ids":[...]}` | `asset.delete` | 204. Goes to trash unless `force: true`, which is never sent |

### `POST /api/assets` details
- Optional header: `x-immich-checksum: <sha1>`. The server can use it to detect a duplicate before it reads the whole file.
- Multipart fields (`AssetMediaCreateDto`):
  - **Required:** `assetData` (file), `fileCreatedAt`, `fileModifiedAt`. The dates are ISO-8601 with a timezone, for example `2024-11-23T19:17:27.652+00:00`. The Python sample sends Python's default `str(datetime)`, which has no `T` and no timezone; do not copy that format.
  - Optional: `filename`, `isFavorite` (bool), `visibility` (`archive|timeline|hidden|locked`), `sidecarData`, `duration`, `livePhotoVideoId`, `metadata`.
  - `deviceAssetId` and `deviceId` are **no longer in the API**. Do not send them, even though older guides do.
- Responses: **201** `{"id": "...", "status": "created"}` or **200** `{"id": "...", "status": "duplicate"}`. A duplicate counts as success, so do not retry it.

### Where VRChat saves photos
- VRChat's own settings are in `%LocalAppData%Low\VRChat\VRChat\config.json`, which is `C:\Users\<user>\AppData\LocalLow\...`. Get the base folder from the Known Folder `FOLDERID_LocalAppDataLow` (`SHGetKnownFolderPath`). If that fails, use `%LOCALAPPDATA%\..\LocalLow`.
- Relevant keys (from the user's file):
  ```json
  "picture_output_folder": "D:\\Photos\\VRChat",
  "picture_output_split_by_date": true
  ```
- `picture_output_folder`: if the key is missing or empty, or the file doesn't exist, the default is **`<the user's Pictures folder>\VRChat`**.
  - Don't assume `%USERPROFILE%\Pictures`. Pictures can be moved (folder properties → Location, or OneDrive backup). On the development machine it has been moved to a different drive, so `%USERPROFILE%\Pictures` would be wrong there.
  - Look up the real location with `Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)`, which uses the Known Folder `FOLDERID_Pictures` and follows moves. Then add `VRChat`.
  - Only if that returns an empty string, fall back to `%USERPROFILE%\Pictures`.
- `picture_output_split_by_date` (default **true** if missing):
  - true → new photos go into a `yyyy-MM` subfolder.
  - false → new photos go straight into the root.
  - It **only affects newly taken photos**. An existing camera roll can mix both layouts, for example root files from a period when the setting was off, and month folders from before or after.
- The file belongs to VRChat. Read it **read-only** with `FileShare.ReadWrite | FileShare.Delete` and never write to it. Parse it leniently (`AllowTrailingCommas`, `JsonCommentHandling.Skip`). If it is unreadable or invalid, log a warning and use the defaults.
- VRChat reads this file when it starts. VRCX starts PhotoSync at about the same moment, so reading it when PhotoSync starts is enough. It is still cheap to also watch the file and switch folders if it changes.

### VRChat camera roll layout (seen in `D:\Photos\VRChat`)
- There is one subfolder per month (`2024-11\`, `2025-08\`, …). VRChat creates new month folders, and with `split_by_date` off, new photos go to the root instead. So the watcher must be **recursive** and must cover the root as well. That handles both settings, so correctness never depends on the flag.
- File names look like `VRChat_2024-11-23_19-17-27.652_1920x1080.png`. The timestamp in the name is local time, accurate to the millisecond. Use it as the capture time.
- The root also holds files that are not photos (for example `PrtScn Screenshots.lnk`). These must be ignored.
- VRChat also keeps **other people's content** in three top-level subfolders of the same root, each with its own `yyyy-MM` subfolders. These must **never** be uploaded:
  - `Prints\` (1,645 files): prints other people shared, for example `Prints\2024-12\<user>_2024-12-24_01-01-38.031_prnt_<uuid>.png`.
  - `Stickers\` (2,128 files): for example `<user>_<date>_inv_<uuid>.png`.
  - `Emoji\` (900 files): for example `<user>_inv_<uuid>_<anim>animationStyle….png`.
- The user's own photos (including their own prints) all match `VRChat_yyyy-MM-dd_HH-mm-ss.fff_<W>x<H>.<ext>`, either in the root or in a `yyyy-MM` folder.
- **Multi-layer photos:** a multi-layer photo saves the normal combined image, plus up to 3 layer files next to it with a suffix before `.png`: `_Environment`, `_Player` and `_UI`. Example from disk:
  ```
  VRChat_2026-11-21_21-03-08.102_3840x2160.png              (combined, always uploaded)
  VRChat_2026-11-21_21-03-08.102_3840x2160_Environment.png
  VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png
  VRChat_2026-11-21_21-03-08.102_3840x2160_UI.png
  ```
  A layer file is detected by the name ending in `_Environment.png`, `_Player.png` or `_UI.png` (ignoring case). Whether to upload layer files is a setting, `UploadLayers`.
- **VRCX rewrites the PNG after VRChat saves it,** adding metadata (players in the instance, world info). Measured on the 432 photos in the camera roll taken since Sept 2025:
  - VRChat itself writes an `iTXt` chunk `XML:com.adobe.xmp`. VRCX adds an **`iTXt` chunk with the keyword `Description`** (a JSON blob).
  - 375 have both chunks, 51 have only the XMP chunk (VRCX didn't add anything, for example today's multi-layer set, all 4 files), and 5 have only `Description`.
  - Last-write minus creation time: under 1 s for 404 files, 1–5 s for 27, and at most about 1.2 s for every file with `Description`. The only outlier, 16.9 s, is a `- Copy.png` the user edited by hand. So VRCX normally rewrites within about 1 s. How long it can take in the worst case is unknown (for example while VRCX is busy, or starting up).
  - Not every photo gets the chunk (VRCX may not be running, or the user may not use VRCX at all). So "wait until `Description` appears" must have a timeout.
- **How VRCX does it** (from VRCX source, `vrcx-team/VRCX@master`):
  - **Detection:** VRCX reads VRChat's output log once a second (`LogWatcher.cs`, `Thread.Sleep(1000)`) and looks for `[VRC Camera] Took screenshot to: <path>`. It doesn't use a FileSystemWatcher.
  - **Multi-layer photos write no such log line,** so VRCX **never** adds metadata to them (a comment in `LogWatcher.cs` says so). This matches today's 4-file set and the missing `Took screenshot to` lines in today's VRChat logs.
  - **Writing** (`ScreenshotHelper.WriteVRCXMetadata` → `PNGFile.WriteChunk`):
    - It adds an `iTXt` chunk with keyword `Description` (JSON) **in place**, inserted before `IDAT`.
    - The file is opened with `FileShare.ReadWrite`. VRCX buffers the tail, calls `SetLength`, and writes the chunk and tail back. There is no temp file and no retry.
    - ⇒ **A reader can see a half-rewritten file.** Hash and upload from a snapshot, then check it didn't change (see the design decision below).
  - **Rules:** only for `VRChat_*.png`.
  - **Settings:**
    - `VRCX_screenshotHelper` adds metadata, **on by default**.
    - `VRCX_screenshotHelperModifyFilename` is **off by default**. When on, it **renames** the file to `VRChat_<timestamp>_<WxH>_<worldId>.png` (`File.Move`) *before* writing the chunk. The user's VRCX has never changed either setting, so the defaults apply, and no `*wrld_*` files exist on disk.
  - **Expected delay:** VRChat writes the log line, VRCX checks the log within about 1 s, then the frontend writes the chunk. This matches the ≤ 1.2 s measured.
- **Live measurement** (2026-11-21, with VRChat and VRCX running, polling every 100 ms):
  ```
  21:34:55.333 created  VRChat_2026-11-21_21-34-55.168_3840x2160.png          (complete PNG at +80 ms, no Description)
  21:34:56.327 changed  … +1,001 ms after create, +201 bytes                  → Description present
  21:35:02.970 created  VRChat_2026-11-21_21-35-02.420_3840x2160.png          (multi-layer: combined photo first)
  21:35:03.119 created  …_Environment.png  (+150 ms)
  21:35:03.320 created  …_Player.png       (+350 ms)     (no _UI file in this set; no Description on any file)
  ```
  - VRCX finishes about 1 s after the photo is saved.
  - In a multi-layer set the **combined photo comes first**, and its layers follow within about 0.4 s. A set can have **fewer than 3 layers**.
  - VRChat's log has `Took screenshot to:` only for the normal photo.
- **Immich has no "replace original" endpoint** in 3.4 (only `PUT /api/assets/copy`, which copies albums, favourite and stack from one asset to another, permission `asset.copy`; and `DELETE /api/assets` (to trash), permission `asset.delete`). Uploading too early therefore means either a second asset or a replace done by hand (see the design decision below).
- **Upload rule (decided):** only files whose name starts with `VRChat_` and ends in `.png` (ignoring case), outside the three excluded folders. Nothing else is uploaded.
  - **Exact VRChat names only** is a setting, `StrictFileNames`, **on by default**. When it is on, the full name must also match the exact pattern VRChat (and VRCX) produce:
    ```
    ^VRChat_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.\d{3}_\d+x\d+(_(Environment|Player|UI)|_(wrld_[0-9a-f-]{36})?)?\.png$   (ignore case)
    ```
    This skips copies people make by hand, such as `VRChat_2025-12-25_21-08-42.649_3840x2160 - Copy.png`. When it is off, any `VRChat_*.png` is accepted.
  - VRCX's "modify filename" rename is `File.Move` to `{name}_{worldId}.png`. **Observed 2026-11-21:**
    - With VRCX signed into a different account from the VRChat client, `world.id` in the metadata was empty, and the file became `VRChat_2026-11-21_21-39-01.718_3840x2160_.png`: a **bare trailing `_`**.
    - The monitor log shows the original name appear, then the rename about 200 ms later (same creation time) with `Description` already added.
    - So the pattern must allow a bare `_` as well as `_<worldId>`.
    - **Confirmed** with the matching account: `VRChat_2026-11-21_21-41-26.711_3840x2160_wrld_12345678-90ab-4cde-8f01-234567890abc.png`. The original name appeared, then the rename came about 350 ms later with `Description` already present. The regex above is final.
    - So with renaming on, the watcher sees `Created(original)` followed by `Renamed(original → …_wrld_….png)` within about 0.5 s. The settle wait follows the rename (step 4), so the renamed file uploads once, under its final name.
  - Side effect: with a bare `_` allowed, `2025-04\VRChat_2025-04-20_03-28-00.594_3840x2160_.png` (a hand-edited copy) also passes the strict filter. The name alone can't tell it apart from a VRCX rename with no world, so this is accepted.
  - VRChat only ever saves PNG and has no file-format option. The 5 `VRChat_2025-08-…_3840x2160.jpg` files on disk are copies the user made by hand to get smaller files. Each one has a PNG original: four have a PNG with the same name, and `…01-54-22.987_3840x2160.jpg` is a smaller copy of `…01-54-22.987_7680x4320.png`. Skipping them is correct, because the originals are uploaded.

### VRChat prints (to be excluded by default)
- A print is a polaroid-style image: the photo with a white border and a caption strip at the bottom ("Captured By <user>", date, world name).
- Prints are always **2048×1440**, and the size also appears in the file name (`..._2048x1440.png`).
- `print_mask.png` (2048×1440) shows the two places where pixels other than white can appear. Measured from the mask:
  - **Photo area:** x 64–1983, y 69–1148 (1920×1080).
  - **Caption strip:** x 0–2047, y 1159–1435.
  - Everything else (308,224 px) is the border and is **pure white**.
- This was checked against all 69 local `*_2048x1440.png` files (`D:\Photos\VRChat`, 2024-12 → 2026-11). **All 69 are prints, and every border pixel is exactly RGB 255,255,255.** There is no real 2048×1440 non-print to test against, so tests must use made-up negative examples.
- Note: one of the example paths given, `2026-10/VRChat_2026-11-04_01-48-06.885_...`, doesn't exist. The file is in `2026-11/`.

### VRCX integration
- **App Launcher** (from VRCX source `AutoAppLaunchManager.cs`, `ProcessMonitor.cs`):
  - **Shortcuts:** VRCX launches the shortcuts (`.lnk`, or Steam `.url`) in `%AppData%\VRCX\startup\` (always), plus `startup\desktop\` (when SteamVR isn't running) or `startup\vr\` (when it is). The user's install already has this folder layout. To add PhotoSync, put a shortcut to the exe (with `--minimized` if wanted) in `%AppData%\VRCX\startup\`. The App Launcher settings in VRCX have a button that opens this folder.
  - **When it launches:** VRCX checks for `vrchat` once a second. It launches only when VRChat starts **after** VRCX. With "run process once" (on by default), it skips an app that is already running.
  - **Settings:** `VRCX_enableAppLauncher`, `VRCX_enableAppLauncherAutoClose` and `VRCX_enableAppLauncherRunProcessOnce` are all **on by default**.
  - **When VRChat exits:** VRCX **hard-kills** the launched process tree (`Process.Kill()`). Its source has the `CloseMainWindow()` path commented out ("breaks some apps").
  - **With several VRChat clients** (tested by the user with another launched app, 2026-11-21):
    - Closing the *second* client does **not** kill the launched apps.
    - Closing the *first* client (the one that triggered the launch) **kills** them, and VRCX then **launches them again straight away** because a client is still running.
    - So PhotoSync can be killed and restarted in the middle of a session while VRChat keeps running and taking photos.
  - ⇒ PhotoSync gets **no** chance to shut down cleanly when VRChat exits. All state must be saved after every step, writes must be atomic, and a killed upload is simply redone at the next start (the server's checksum handles duplicates).

---

## Design decisions

- **Stack:** .NET 10 (SDK 10.0.402 is installed), **WinForms**, `net10.0-windows`. WinForms is the lightest way to get a tiny window. There are no NuGet dependencies beyond the BCL (`HttpClient`, `System.Text.Json`, `FileSystemWatcher`, `System.Threading.Channels`).
- **Photo folder:** **not stored in our config.** It is read from VRChat's `config.json` every time the app starts (see above). `--folder <path>` on the command line overrides it for that run only (useful for testing with a scratch folder).
- **Config file:** `config.json` stored **next to the exe**, so the app is portable. If that folder can't be written to, fall back to `%APPDATA%\VRChatPhotoSync\config.json`. **Users never need to edit this file by hand.** Every setting can be changed in the app. On first run, or when the server/key is missing or invalid, the **Immich server** window opens automatically. Command-line overrides (`--folder`, `--server`, `--minimized`) make VRCX setups flexible.
- **API key storage:** stored in `config.json`, encrypted with Windows DPAPI (`ProtectedData`, CurrentUser scope). That way the key is never kept as plain text and does nothing if the file is copied to another machine. A shared copy of the tool must never include anyone's key.
- **State file:** `state.json` lives next to the config. It holds `lastScanUtc` and a set of uploaded files (relative path + size + mtime → Immich asset id). It is written atomically (temp file + replace) after every upload, so a kill loses nothing.
- **Pipeline:** watcher and startup scan → `Channel<string>` queue → one upload worker. Each file goes through: wait until it is fully written → layer/print checks → wait for VRCX metadata → SHA1 → POST → record in state → update UI.
- **Duplicates:** the local state skips files that were already uploaded. The server's checksum check handles the rest, so re-uploading is always safe.
- **Startup catch-up:** scan for files newer than `lastScanUtc`. On the very first run, `lastScanUtc` = now, so the app does **not** upload the whole old library by surprise.
- **Existing photos:** the app asks about existing photos **when the API key is first entered**. Nothing can upload before a key exists anyway. The Immich server window shows a "Also upload my N existing photos" checkbox (unticked by default) only while no key has been saved yet. If it is ticked, Save starts a full backfill using `bulk-upload-check`. After that, "Upload existing photos…" in the ⋯ menu does the same thing on demand.
- **Waiting for VRCX metadata:** once a file is fully written, check its PNG chunk list (read only the chunk headers, skipping the image data).
  - If an `iTXt` `Description` chunk is present, upload straight away.
  - If not, re-check every 1 s until the chunk appears, or the file changes and then stays the same for 2 s, or **`VrcxWaitSeconds`** (default **10**) runs out. Then upload whatever is there.
  - The setting is in Advanced, and 0 turns waiting off. 10 s is 10× the ~1 s measured, and keeps the delay short for people who don't run VRCX. The late-edit safety net below covers anything slower.
  - Files found by the startup catch-up or backfill that are more than 2 minutes old skip the wait.
  - **Multi-layer sets skip the wait**, because VRCX never adds metadata to them. A layer file never waits. A combined photo stops waiting as soon as any `_Environment`/`_Player`/`_UI` file with the same base name exists. Measured: the combined photo is written first and its first layer follows about 150 ms later, so a multi-layer combined photo waits well under 1 s.
  - **Snapshot before hashing:** VRCX rewrites in place, so read the settled file into memory in one go (≤ about 50 MB even at 7680×4320), noting size and mtime before and after. Hash and upload **from that copy**. If the size or mtime changed during the read, settle again. If the file changes after upload, the re-upload path handles it.
  - **Renames:** with VRCX's "modify filename" setting on, the file is renamed (`…_<worldId>.png`) and then edited. A `Renamed` event moves any pending wait or state entry to the new path, so it isn't treated as a new photo.
- **Changes after upload** (VRCX was later than the timeout): handle `Changed` events for files already in state. If their size or modified time differs from the state entry, wait until the file is stable, then upload the new version.
  - Then, if the key has `asset.copy` + `asset.delete`, call `PUT /assets/copy` (old → new, keeping albums and favourite), then `DELETE /assets` `{"ids":[old]}` (to trash, not forced, so it can be recovered).
  - If those permissions are missing, keep both assets and log a warning. Test connection points this out as "optional: asset.copy, asset.delete to replace photos VRCX edits late".
  - Update state to the new asset id. This is the safety net, and it should rarely be needed.
- **Layer files:** setting `UploadLayers` (default **false**, meaning only the combined photo is uploaded). It is a checkbox on the main window, saved right away, and works like "Skip prints": it applies to files queued from now on and to "Upload existing photos…". Skipped layers are recorded in state as `skipped: "layer"`.
- **Album:** optional. If an album name is set, each uploaded photo is added to that album, and the album is created if it doesn't exist. If it is empty, photos are uploaded without an album.
- **Window behaviour:** settings decide what the close and minimize buttons do (see step 6). A tray icon is always available, so both "to tray" options work.
- **Distribution:** one standalone single-file exe (no .NET install needed).
- **Print detection:** a quick check first, then a pixel check.
  1. Is the size 2048×1440? Use the file name, or the PNG `IHDR` header (bytes 16–23) if the name has no size. If not, it is **not a print**, and nothing is decoded. This handles almost every photo.
  2. Only then, decode the image (`System.Drawing.Bitmap` + `LockBits`; this is fine because the app is Windows-only) and check every pixel outside the two mask rectangles. It is a print if ≥ 99.9% of border pixels have every channel ≥ 240. The tolerance allows for compression or future small changes. Real prints currently score 100%.
  - The mask rectangles are built-in constants taken from `print_mask.png`. The PNG is also committed under `docs/` for reference. If VRChat changes the layout later, the constants are the only thing to update.
  - Setting `ExcludePrints` (default **true**) appears as a **checkbox on the main window** and is saved to config right away. Skipped prints are recorded in state (`skipped: "print"`). Turning the box off does not upload past prints by itself. "Upload existing photos…" does that, and it follows the current setting.
- **Single instance:** a named mutex. A second launch brings the existing window to the front and then exits.
  - A killed process leaves the mutex **abandoned**. `WaitOne` then throws `AbandonedMutexException`, which must be treated as "acquired". Otherwise the immediate relaunch after a VRCX kill would think another copy is running and exit.
  - Kill-then-relaunch can overlap by a moment, so wait up to about 5 s for the mutex before giving up.
- **Kill and immediate relaunch** (VRCX with several clients, see above):
  - Startup must be fast, and it must resume cleanly: pending files are found again by the catch-up scan, and a half-finished upload is simply redone (the server's checksum check handles the duplicate).
  - **Session counters carry over:** `state.json` stores `session {startUtc, uploaded, duplicates, printsSkipped, layersSkipped, lastFile}` plus a `heartbeatUtc` that is refreshed every 30 s and after each upload. On start, if the last heartbeat was under 2 min ago, the app continues that session. Otherwise it starts a new one at 0. This way, killing the first client doesn't reset the "uploaded this session" count while you keep playing in the second.
  - Window position and minimized/tray state are saved in config, so a relaunch looks the same.

---

## Implementation steps

### 1. Project setup
- [x] Create a solution `VRChatPhotoSync.sln` with `src/VRChatPhotoSync` (WinForms, `net10.0-windows`, `<Nullable>enable</Nullable>`, `<UseWindowsForms>true</UseWindowsForms>`) and `tests/VRChatPhotoSync.Tests` (xUnit).
- [x] Extend `.gitignore` (it already ignores `CLAUDE.local.md`, `sample-photos/`, `config.json`, `state.json`, logs) with `bin/`, `obj/`, `publish/`, `*.user`, `.vs/`. Add `.editorconfig`.
- [x] Add an app icon and set `AssemblyName`, `Product` and `Version`.
- [x] Make the first git commit of the skeleton.

### 2. Configuration
- [ ] `AppConfig` record (no photo-folder field; that comes from VRChat): `ServerUrl`, `ApiKeyProtected`, `AlbumName?` (empty = no album), `Visibility` (default `timeline`), `ExcludePrints` (default true), `UploadLayers` (default false), `StrictFileNames` (default true), `VrcxWaitSeconds` (default 10, 0 = off), `CloseButtonAction` (`Exit` | `HideToTray`, default `Exit`), `MinimizeButtonAction` (`Taskbar` | `Tray`, default `Taskbar`), `StartMinimized` (default false). There is no file-type setting, because the upload rule is fixed (`VRChat_*.png`).
- [ ] `ConfigStore`: load and save JSON (exe dir, then `%APPDATA%` fallback). Validate that the URL is absolute http/https.
- [ ] Normalise `ServerUrl`: trim any trailing `/`, and add `/api` if it is missing, so that `https://immich.example.com/` becomes `https://immich.example.com/api`.
- [ ] `SecretProtector`: DPAPI encrypt/decrypt of the API key (base64 in JSON).
- [ ] `VRChatSettings.Load()`:
  - Find `LocalLow\VRChat\VRChat\config.json` (Known Folder API, then the `%LOCALAPPDATA%\..\LocalLow` fallback).
  - Read it read-only and parse it leniently.
  - Return `PictureFolder` (`picture_output_folder`, or `Path.Combine(GetFolderPath(SpecialFolder.MyPictures), "VRChat")` if it is missing, empty or the file is missing; inject the Pictures lookup so tests can fake it), passed through `Environment.ExpandEnvironmentVariables` + `Path.GetFullPath`.
  - Also return `SplitByDate` (default true) and `Source` (`"VRChat config"` / `"default"` / `"--folder"`) to show in the UI and log.
  - Never throw. Log and use the defaults instead.
- [ ] Parse command-line options `--folder <path>` (overrides the VRChat folder for this run only), `--server <url>`, `--minimized`, `--settings` (force the dialog). Command-line values override the file for that run only.
- [ ] Ship `config.example.json` with placeholder values (no real paths, URLs or keys).

### 3. Immich API client
- [ ] `ImmichClient`: one `HttpClient` with a base address, the `x-api-key` header, `Accept: application/json`, a generous timeout for uploads (for example 5 min), and User-Agent `VRChatPhotoSync/<version>`.
- [ ] `PingAsync()` → `GET server/ping`.
- [ ] `GetMyApiKeyAsync()` → `GET api-keys/me`, which returns the permissions. Warn if neither `all` nor `asset.upload` is present, and warn about the album permissions when an album is set.
- [ ] `UploadAsync(path, sha1, createdAt, modifiedAt)`:
  - `MultipartFormDataContent` with `StreamContent` for `assetData` (stream from disk, do not load into memory). Set the file name and `Content-Type: image/png`.
  - Form fields: `fileCreatedAt`, `fileModifiedAt` (ISO-8601 `"o"` format with offset), `filename`, `isFavorite=false`, and `visibility` when it is not the default.
  - Header `x-immich-checksum: <sha1 hex>`.
  - Map 201 → `Created(id)` and 200 → `Duplicate(id)`. Map 401/403 → `AuthError` (do not retry; show it in the UI). 400 → `Rejected` (do not retry; log the body). 5xx, timeouts and network errors → `Transient` (retry).
- [ ] `BulkUploadCheckAsync(items)` for backfill. Send batches of about 500.
- [ ] Album helpers: `GetOrCreateAlbumAsync(name)` (cache the id for the session) and `AddToAlbumAsync(albumId, assetIds)`. Treat `error: "duplicate"` as success.
- [ ] Replace helpers: `CopyAssetAsync(sourceId, targetId)` → `PUT assets/copy`, and `TrashAssetsAsync(ids)` → `DELETE assets` (never `force`). These are used only for photos VRCX edits late, and only if `api-keys/me` shows `asset.copy` and `asset.delete` (or `all`).
- [ ] JSON DTOs with `System.Text.Json` source generation (keeps a trimmed single-file build working).

### 4. File detection
- [ ] `PhotoFilter`:
  - Accept only files whose **name starts with `VRChat_` and ends with `.png`** (ignoring case). When `StrictFileNames` is on, the name must also match the exact VRChat pattern (see the research section). Read the setting each time, so a change applies to the next file.
  - **Reject anything under the top-level `Prints\`, `Stickers\` or `Emoji\` folders.** These folders hold other users' content. The name rule already excludes those files, but the folder check stays as a second safeguard.
  - `VrcFileName.IsLayer(name)`: true if the name, without `.png`, ends with `_Environment`, `_Player` or `_UI` (ignoring case). When `UploadLayers` is off, layer files are skipped. Check this in the worker rather than the watcher, so the decision follows the current setting and is recorded in state. Compare the first path segment relative to the root, ignoring case. Keep the folder list as a constant, in case VRChat adds more.
  - Ignore `.lnk`, hidden and temp files, and anything outside the watch root.
  - Apply the same filter to watcher events, the catch-up scan and the backfill. Skip those folders entirely while scanning; don't list them and then filter.
- [ ] `VrcFileName.TryParseTimestamp(name)`: regex `^VRChat_(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2}\.\d{3})_` → local `DateTimeOffset`. Fall back to the file's `CreationTime` and `LastWriteTime`.
- [ ] `FolderWatcher`: `FileSystemWatcher` on the root with `IncludeSubdirectories = true`, `NotifyFilter = FileName | LastWrite | Size`, and `InternalBufferSize = 64 KB`. Handle `Created` and `Renamed` (to an accepted extension). Debounce duplicate events per path.
- [ ] Handle the watcher's `Error` event (buffer overflow, or the folder was removed): recreate the watcher and trigger a catch-up scan.
- [ ] Missing picture folder: this is normal for a new user whose default `Pictures\VRChat` doesn't exist until their first photo. Don't create it. Show `Waiting for <folder> to exist…`, check every 30 s, and start the watcher and catch-up scan as soon as it appears.
- [ ] Watch VRChat's `config.json` (its own small `FileSystemWatcher` on that directory, debounced). If `picture_output_folder` changes, log it, update the UI, and restart the folder watcher on the new path, then run a catch-up scan there.
- [ ] `FileReadiness.WaitUntilReadyAsync(path)`: poll until the file can be opened with `FileShare.None`/read and its size has not changed between two checks about 500 ms apart. Give up after about 60 s and re-queue.
- [ ] `PngChunks.HasVrcxMetadata(path)`: walk the PNG chunks (8-byte signature, then length+type, seeking past the data) and return true if there is an `iTXt` or `tEXt` chunk whose keyword is `Description`. Open with `FileShare.ReadWrite | FileShare.Delete` so the check never blocks VRCX's rewrite. Treat a truncated or invalid PNG as "not ready".
- [ ] `VrcxSettle.WaitAsync(path, timeout)`:
  - Return straight away if `HasVrcxMetadata`, if the timeout is 0, or if the file is more than 2 min old.
  - Otherwise poll every 1 s. Return when the chunk appears, or the file changes (size/mtime) and stays the same for 2 s, or the timeout runs out.
  - Run the settle wait for several files at once (a few photos taken in a row shouldn't each wait 30 s one after another). Only the upload itself is single-file. Show `Waiting for VRCX metadata… (N)` in the status line.
- [ ] Handle `Changed` events too (the watcher already has `NotifyFilter.LastWrite | Size`). For a path that is already in state with a different size or mtime, enqueue it as a **re-upload** (see step 5). For paths not in state, `Changed` just debounces into the normal queue.

### 4b. Print detection
- [ ] Move `print_mask.png` to `docs/print_mask.png`. Add `PrintLayout` constants: canvas 2048×1440, photo rectangle (64,69)–(1983,1148), caption rectangle (0,1159)–(2047,1435), all inclusive.
- [ ] `ImageSize.TryRead(path)`: read the PNG `IHDR` width and height without decoding. Use it when the file name has no `_<W>x<H>` part, or to double-check the name.
- [ ] `PrintDetector.IsPrint(path)`:
  - Return false straight away unless the size is 2048×1440.
  - Otherwise decode with `Bitmap`, use `LockBits` (32bppArgb), and copy the pixels in one go.
  - Count the border pixels outside both rectangles that have min(R,G,B) ≥ 240.
  - It is a print if the white share is ≥ 99.9%. Exit early once enough non-white pixels make that impossible.
- [ ] In `UploadWorker`, first skip layer files when `UploadLayers` is off. This check is by name only, so it is cheap. Record `skipped: "layer"` and increment a "layers skipped" counter.
- [ ] Call the print check in `UploadWorker` after the readiness check and before the SHA1, only when `ExcludePrints` is on. A print → record `skipped: "print"` in state, increment the session's "prints skipped" counter, and log it. Don't upload.
- [ ] If detection fails (corrupt or locked file), log it and **upload anyway**. Failing toward uploading means no real photo is ever lost.

### 5. Upload queue and state
- [ ] `UploadState` (`state.json`): `lastScanUtc`, plus a map of `relativePath → {size, mtimeUtc, sha1, assetId}`. Use atomic save (`File.Replace` / write temp then move).
- [ ] `UploadQueue`: an unbounded `Channel<string>`, with a `HashSet` so the same path is never queued twice.
- [ ] `UploadWorker` (one consumer): readiness → layer/print checks → **VRCX settle wait** → skip if already in state with the same size and mtime → SHA1 (streamed, *after* the settle wait so the hash matches the final file) → upload → add to album if set → save state → raise `Uploaded(fileName, status)`.
- [ ] Re-upload path (the file changed after it was uploaded): upload the new version. If it is `created`, and the old asset id differs, and the key has `asset.copy` + `asset.delete`: copy old → new, then trash old. Otherwise log "kept both versions". Update the state entry. Don't count it as a new upload in the session counter; show `Updated <file> (VRCX metadata)` instead.
- [ ] Retry transient failures with exponential backoff (5 s, 15 s, 1 min, 5 min, then every 5 min). Keep the item at the front of the queue so order is kept. Pause the queue on `AuthError` until the settings change.
- [ ] Startup catch-up: enumerate the folder recursively, **including the root**, for accepted files where `max(CreationTimeUtc, LastWriteTimeUtc) > lastScanUtc − 1 min` and that are not already in state. Using the creation time as well catches files that were copied or moved in, because a copy keeps the old modified time but gets a new creation time. Enqueue them oldest first, then set `lastScanUtc` = start time. Always scan the whole tree rather than relying on `SplitByDate`. The setting only describes where *new* photos go, and a full listing of a few thousand files takes milliseconds.
- [ ] Record the picture folder in `state.json` and store state paths relative to it. If the folder differs from last run (the user changed VRChat's setting), log it and treat the new folder's catch-up as a first run (`lastScanUtc` = now). This avoids a surprise mass upload, and "Upload existing photos…" can still be used.
- [ ] Optional full backfill command: enumerate all files → SHA1 → `bulk-upload-check` → enqueue only `accept` results. Record `reject/duplicate` ones in state so they are skipped later. Show progress in the window.

### 6. UI (WinForms)
- [ ] `MainForm`: a small fixed window (about 360×140), not resizable. It shows:
  - Status line: `Watching D:\Photos\VRChat` / `Uploading…` / `Retrying in 15s (server unreachable)` / `API key rejected — click "Immich server…"` / `Not set up — click "Immich server…"`.
  - **Uploaded this session: N** (also show duplicates skipped and failures if any).
  - **Last: VRChat_2025-08-31_12-58-01.123_1920x1080.png** (truncated with an ellipsis, full name in a tooltip; clicking opens it in Explorer).
  - Queue length when it is greater than 0.
  - **☑ Skip prints** checkbox (bound to `ExcludePrints`, saved immediately; affects files queued from now on), and a "Prints skipped: N" count next to it.
  - **☐ Upload photo layers** checkbox (bound to `UploadLayers`, saved immediately). Its tooltip says "Also upload the _Environment, _Player and _UI files from multi-layer photos". A "Layers skipped: N" count is shown when N > 0.
  - Buttons: **Immich server…** (opens the connection window below), **Open folder**, and a ⋯ menu:
    - Upload existing photos…
    - Window behaviour ▸ with two groups of radio items, both saved immediately:
      - "Close button: Exit app / Hide to tray".
      - "Minimize button: Taskbar / Tray".
      - Plus a checkable "Start minimized" item.
    - Open log
    - Open Immich
- [ ] Marshal all UI updates through `SynchronizationContext` / `BeginInvoke`. Workers never touch controls.
- [ ] `ServerSettingsForm` ("Immich server" window): a modal dialog opened by the **Immich server…** button, and automatically when no server is set up. Layout:
  ```
  ┌ Immich server ─────────────────────────────────────┐
  │ Server URL: [https://immich.example.com            ] │
  │ API key:    [••••••••••••••••••••••••••] [Show]     │
  │             How do I get an API key? (link)        │
  │ [Test connection]  ✔ Connected — Immich 3.4.2,     │
  │                      key can upload                │
  │ Album:      [VRChat              ] (optional)      │
  │ ☐ Also upload my 2,431 existing photos  (first     │
  │   setup only)                                      │
  │ ▸ Advanced (visibility, VRCX wait, exact names)    │
  │ Photo folder: D:\Photos\VRChat (from VRChat)        │
  │                               [Save]   [Cancel]    │
  └────────────────────────────────────────────────────┘
  ```
  - **Server URL:** filled in from config. Accepts `https://host`, `https://host/` or `https://host/api`, and stores the normalised `/api` form, but shows the address as the user typed it.
  - **API key:** a password box with a Show toggle.
    - When a key is already saved, the box is blank, with the hint text "(saved — leave blank to keep)". The saved key is never shown again or copied back into the box.
    - A new key is DPAPI-encrypted when saved.
    - Paste is trimmed of whitespace.
  - **"How do I get an API key?"** opens the server's API-key settings page, `<server root>/user-settings?isOpen=api-keys`, in the browser. Use the server root, not the `/api` URL. The link's tooltip also gives the manual route: profile picture (top right) → Account Settings → API Keys. If no URL has been entered yet, it opens the Immich docs page instead. Below it, the needed permissions are listed: `asset.upload`, plus the album permissions if an album is set.
  - **Test connection:** runs in the background with the button disabled. It calls `server/ping` then `api-keys/me` and shows the result inline, one line each:
    - Can't reach server.
    - Not an Immich server (no `pong`).
    - Key rejected (401).
    - Key missing `asset.upload`.
    - ✔ Connected (with the server version from `server/version`, and the key's name).
  - **Save:** checks that the URL is valid. It runs Test connection automatically if that hasn't been done yet. If the test fails, it asks "Save anyway?", because the server may just be offline. Then it writes the config and restarts the Immich client. If uploads were paused by an auth error, it resumes them.
  - **Album** (optional): blank → no album. When it is set, Test connection also checks for the `album.read`, `album.create` and `albumAsset.create` permissions. When it is changed, the cached album id is cleared.
  - **"Also upload my N existing photos"**: shown only when **no API key has been saved yet**, meaning first setup.
    - N is the number of files that pass the filter, leaving out prints (if Skip prints is on) and layer files (if Upload photo layers is off). It is counted in the background when the window opens.
    - It is unticked by default.
    - If it is ticked when Save succeeds, a full backfill is queued (step 5). If it is unticked, only new photos from now on are uploaded.
  - **Advanced** (collapsed by default):
    - Visibility.
    - "Wait for VRCX metadata up to [10] s" (0 = don't wait).
    - **☑ Only upload files with exact VRChat names**, bound to `StrictFileNames`. Its tooltip says "Skips edited or copied files such as 'VRChat_… - Copy.png'".
  - **Photo folder:** shown read-only (for example `D:\Photos\VRChat (from VRChat settings, split by month)`) with an "Open VRChat config folder" link. There is no folder picker, because the folder comes from VRChat.
- [ ] Tray icon (`NotifyIcon`): always present while the app runs. Its tooltip shows the session count, double-click restores the window, and its menu has Show / Exit.
- [ ] Window behaviour settings:
  - **Minimize button:** `Taskbar` → a normal minimize. `Tray` → hide the window, leaving the tray icon.
  - **Close button:** `Exit` → exit. `HideToTray` → hide the window; exiting is done from the tray menu.
  - **Important:** only the user's **title-bar X** may be redirected to the tray. Override `WndProc`, and treat `WM_SYSCOMMAND` / `SC_CLOSE` (the X button or Alt+F4) as the "close button". A plain `WM_CLOSE` from another process (VRCX, `taskkill` without `/F`) or Windows shutting down **always exits**. Otherwise "Hide to tray" would stop VRCX from closing the app when VRChat exits.
  - `--minimized` or `StartMinimized` starts the window minimized, to the taskbar or tray depending on the minimize setting.
- [ ] Set the window title to show the count (for example `PhotoSync — 3 uploaded`) so it can be read from the taskbar.

### 7. App lifecycle
- [ ] `Program.Main`: single-instance mutex → load config → show settings if needed → start client, watcher, catch-up scan, and worker → `Application.Run(MainForm)`.
- [ ] Note that VRCX **kills** the process, so the graceful path only applies when the user exits or Windows shuts down. Correctness must never depend on it. On graceful close (`FormClosing`, `WM_CLOSE`, or `SessionEnding`), stop accepting new files and give the upload in progress up to about 10 s to finish, then exit. Everything else is picked up by the next catch-up scan.
- [ ] Mutex: treat `AbandonedMutexException` as acquired, and wait up to about 5 s for the mutex before handing off to the running copy.
- [ ] Session restore: load `session` from state, continue it if `heartbeatUtc` is under 2 min old, and refresh the heartbeat every 30 s and after each upload (atomic write).
- [ ] Global exception handlers (`Application.ThreadException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) write to the log and show the error in the status line instead of crashing.

### 8. Logging
- [ ] A simple rolling text log (`logs\photosync-YYYYMMDD.log` next to the config, keep 7 days). Record start/stop, every queued file, the result and asset id for each upload, retries and errors. **Never log the API key.**

### 9. Tests
- [ ] `VrcFileName` parsing (valid names, odd resolutions, names that aren't VRChat names). The timestamp and size must also parse from layer names (`…_3840x2160_Player.png`).
- [ ] `VrcFileName.IsLayer`: true for `_Environment`, `_Player`, `_UI` and `_ui`. False for the combined photo, and for a name that only contains `Player` elsewhere.
- [ ] `UploadWorker`: layer files are skipped when `UploadLayers` is off and uploaded when it is on. The combined photo is always uploaded.
- [ ] `VRChatSettings` (with the config path injectable, using temp files):
  - A full file gives the folder and split value.
  - The key is missing → `<fake Pictures>\VRChat`, split = true. With a fake Pictures folder somewhere other than `%USERPROFILE%`, check that the moved location is used.
  - The Pictures lookup returns an empty string → `%USERPROFILE%\Pictures\VRChat`.
  - The file is missing → defaults.
  - Invalid JSON, trailing commas, or comments → no exception, defaults where needed.
  - `picture_output_split_by_date: false` is read correctly.
  - A path with environment variables is expanded.
- [ ] Catch-up scan finds new files in the root **and** in month subfolders.
- [ ] `ServerUrl` normalisation (`https://x/`, `https://x/api`, `https://x/api/`).
- [ ] `PhotoFilter`:
  - Accepts `VRChat_…png` and `vrchat_…PNG`, in the root and in month folders.
  - Rejects `.jpg`, names without the `VRChat_` start, and `.lnk` and temp files.
  - Rejects `Prints\…`, `stickers\…` and `Emoji\…` at the top level, even when the name starts with `VRChat_`.
  - With `StrictFileNames` on:
    - Accepted: normal photos, `_Environment`/`_Player`/`_UI` layers, `_wrld_<uuid>` names, a bare trailing `_` (a VRCX rename with no world), and 7680x4320 / 2048x1440 sizes.
    - Rejected: `… - Copy.png`, `… (1).png`, `…_edited.png`, a missing milliseconds part, and a layer *and* a world ID together.
    - With it off, all of those `VRChat_*.png` names are accepted.
- [ ] Window behaviour: a `WM_SYSCOMMAND`/`SC_CLOSE` with `HideToTray` hides the window, while a plain `WM_CLOSE` always exits.
- [ ] `ImmichClient` against a fake `HttpMessageHandler`: check the multipart field names and date format, the headers (`x-api-key`, `x-immich-checksum`), and the 201/200/401/400/500 mapping.
- [ ] `UploadState` round-trip and atomic save.
- [ ] Integration tests (`[Trait("Category","Integration")]`) against a real server: ping, `api-keys/me`, upload of a **generated** PNG (then the same bytes again → `duplicate`), and album create/add. They read `serverUrl`/`apiKey` from the git-ignored `secrets.local.json` at the repo root, or from the `IMMICH_TEST_URL`/`IMMICH_TEST_API_KEY` environment variables, and are **skipped** when neither is set (so CI and other contributors don't need a server). Trash the test assets afterwards (`DELETE /api/assets`) if the key has `asset.delete`.
- [ ] `PrintDetector`, using fixtures **generated in the test** (no real photos committed, because real prints include the user's name and world):
  - A made-up print: a white 2048×1440 canvas, random noise in the photo area, and black "text" pixels in the caption strip → print.
  - Noise over the whole canvas at 2048×1440 → not a print.
  - The made-up print with the white border tinted to 230 → not a print.
  - A 1920×1080 image → not a print, without being decoded.
  - A few stray non-white border pixels (under the tolerance) → still a print.
- [ ] `ImageSize.TryRead` on generated PNGs of a few sizes.
- [ ] `PngChunks.HasVrcxMetadata`: generated PNGs with no text chunks, XMP only, `iTXt Description`, `tEXt Description`, and a truncated file. No exceptions.
- [ ] `VrcxSettle` with a fake clock and file probe: returns straight away for layer files and when a layer sibling exists, returns straight away when the chunk is there, returns when it appears at about 1 s, returns when a layer sibling appears at about 150 ms, returns at the timeout when it never appears, and returns after the file changes and stays the same. Old files skip the wait.
- [ ] Re-upload: with copy/delete permission → upload, copy, trash and update state. Without it → upload, keep both, and log a warning. A `duplicate` result (identical bytes) → no copy or delete.
- [ ] `UploadWorker`: a print is skipped when `ExcludePrints` is on and uploaded when it is off. A detection exception still uploads.
- [ ] `UploadWorker` with a fake client: retries transient errors, skips files already uploaded, pauses on auth errors.
- [ ] Single instance: a test that acquires the mutex in a child process and kills it, then checks that the next acquire succeeds (abandoned mutex = acquired).
- [ ] Session restore: a heartbeat 30 s old → the session continues. A heartbeat 10 min old → a new session.

### 10. Manual end-to-end check against a real Immich server
> Real test files: the photos taken for this plan on 2026-11-21 (the multi-layer sets, the `_` and `_wrld_…` renames) and the prints have been **removed from `D:\Photos\VRChat`**. Copies are in `sample-photos\2026-09…2026-11\` (git-excluded and temporary; see CLAUDE.md). Copy files from there into the scratch folder. Paths below like `2026-11/…` refer to `sample-photos\2026-11\…`.

- [ ] Create a **dedicated API key** with only `asset.upload` (plus album permissions if used).
- [ ] Start the app with no `--folder` and check that it shows `D:\Photos\VRChat (from VRChat settings)`.
- [ ] Point the app at a scratch folder with `--folder` (not the real camera roll). Copy in one VRChat PNG and check that it appears in Immich with the right capture date and file name, and that the window count = 1.
- [ ] Copy the same file again under a new name and check that the result is a `duplicate` and the count doesn't go up as an upload.
- [ ] Copy in a large file slowly (or hold it open) and check that the app waits for the write to finish before uploading.
- [ ] Create a new month subfolder and drop a file in it; check that it is detected. Then drop one straight into the root (as `split_by_date: false` would) and check that it is detected too.
- [ ] Start the folder lookup against a temporary VRChat config path that doesn't exist, and check that it falls back to `<GetFolderPath(MyPictures)>\VRChat` (the moved Pictures folder, not `%USERPROFILE%\Pictures`) and waits for that folder to exist. Never change the real VRChat `config.json` for this test.
- [ ] Copy in a real print (for example `2026-11/VRChat_2026-11-01_22-28-32.600_2048x1440.png`) with "Skip prints" ticked, and check that it is skipped and counted. Untick the box, copy in another print, and check that it uploads.
- [ ] Run the detector over every local `*_2048x1440.png` (69 files; all should be detected as prints) and a sample of normal photos (all should be detected as not prints).
- [ ] Kill the process during an upload, add more files (copied in, so they have old modified times), restart, and check that the catch-up scan uploads them all.
- [ ] With the app watching the real `D:\Photos\VRChat`, check in the log that nothing under `Prints\`, `Stickers\` or `Emoji\` is queued.
- [x] Run the strict-name filter over every `VRChat_*.png` on disk (not in the excluded folders). Done 2026-11-21 with the first version of the pattern: 611 files, 2 rejected, both hand-edited copies:
  - `2025-12\VRChat_2025-12-25_21-08-42.649_3840x2160 - Copy.png`
  - `2025-04\VRChat_2025-04-20_03-28-00.594_3840x2160_.png`

  No real VRChat photo failed the pattern. Since a bare trailing `_` is now allowed (VRCX renames), the 2025-04 copy passes as well. This is a known limitation: the name can't tell it apart from a VRCX rename. Use both names as test cases (`- Copy` rejected, `_` accepted).
- [x] Re-run with the final pattern (2026-11-21): 614 files, 1 rejected (the `- Copy.png`). The `_` and `_wrld_<uuid>` renames pass.
- [ ] With VRCX "modify filename" on, take a photo with the app running. Check that exactly one asset is uploaded, under the `_wrld_…` name, with `Description` present, and that the short-lived original name is never uploaded.
- [ ] In the Immich server window, check Test connection with: the correct URL and key (✔), a wrong key (key rejected), a URL that doesn't exist (can't reach server), and a website that isn't Immich (not an Immich server). Check that saving with the key box blank keeps the existing key.
- [ ] First setup with "Also upload my existing photos" ticked, pointed at a scratch folder with a few old photos: check that they upload. Repeat with it unticked and check that only new photos upload. Check that the checkbox doesn't appear once a key is saved.
- [ ] Copy the 4-file multi-layer set (`VRChat_2026-11-21_21-03-08.102_3840x2160*.png`) into the scratch folder with "Upload photo layers" unticked, and check that 1 file uploads and 3 are skipped. Tick it, copy in another set, and check that all 4 upload.
- [ ] Set an album name and check that photos land in it, and that the album is created if it is missing. Clear the name and check that uploads go in with no album.
- [ ] With "Close button: Hide to tray" set, check that the X button hides to the tray, and that `taskkill /im VRChatPhotoSync.exe` (no `/F`) and VRCX closing VRChat still exit the app.
- [ ] Stop network access or use a wrong URL, and check that it retries and recovers. Use a wrong key and check that the auth error shows in the UI.
- [ ] Take a real photo in VRChat with the app watching `D:\Photos\VRChat` and VRCX running. Check in the log how long the wait for VRCX's `Description` chunk took. Download the asset from Immich and check that its `Description` chunk is present (meaning the final version was uploaded).
- [ ] Take a photo with VRCX closed and check that it uploads after the 10 s timeout. Then simulate a late VRCX edit by adding a `Description` chunk to the file with a test helper, and check that the replace runs: new asset in the same album, old asset in the trash.
- [x] Measure the VRCX delay and the multi-layer file order live (done 2026-11-21, before implementation; see the research summary). A copy of the monitor script is saved as `tools/watch-vrcx.ps1` for re-measuring later.
- [ ] Take a multi-layer photo with the app running, and check that the combined photo uploads within about 1 s (no 10 s wait) and that the layers follow the "Upload photo layers" setting.

### 11. Packaging and distribution
- [ ] `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`, so the person receiving it does not need the .NET runtime installed. This standalone exe is the only build that is shipped. Consider `-p:EnableCompressionInSingleFile=true` to shrink it. Note that WinForms doesn't support trimming, so expect about 70 MB.
- [ ] Check that the published exe runs from a fresh folder with no config, opens the Immich server window on first run, and is fully set up through the UI with no file editing. Check that it then uploads, and that the key in `config.json` is encrypted rather than plain text.
- [ ] Write `README.md`: what it does, how to create an Immich API key (and which permissions), first-run setup, the fact that the photo folder comes from VRChat's own settings (change it in VRChat, not here), how to add it in VRCX (a shortcut in `%AppData%\VRCX\startup\`, see step 12), the command-line options, and where the config, state and logs are kept.
- [ ] Optional: a GitHub Actions workflow that builds and attaches the single-file exe to a release.

### 12. VRCX hookup
- [ ] Put a shortcut to the published exe (with `--minimized` if wanted) in `%AppData%\VRCX\startup\`. Start VRChat **after** VRCX and check that PhotoSync launches. Exit VRChat and check that PhotoSync is killed, that `state.json` is intact, and that the next start catches up anything left pending.
- [ ] With two VRChat clients: close the first one and check that PhotoSync is killed and relaunched. Check that it starts cleanly (no "already running" exit), that the session count carries on from before, and that a photo taken around the kill is still uploaded.
- [ ] README: document the shortcut folder (and `desktop\` / `vr\`), that VRChat must start after VRCX, and that VRCX's App Launcher and auto-close are on by default.

---

## Possible later additions (out of scope for v1)
- Choose an album per world, using the world name in VRCX's `Description` JSON chunk (already read for the settle check) or the VRChat log.
- Put VRCX's player list or world name into the Immich description or tags.
- A "pause uploads" toggle (for example while on a metered connection).
- Delete or move the local file after a confirmed upload (off by default).
