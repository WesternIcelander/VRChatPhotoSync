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

How this list works:
- **Build items** are `- [ ]` checkboxes. Each one is a single piece of work that can be tested on its own, including its unit tests. An agent can finish it without VRChat or the user. The plain sub-bullets under each item are its spec.
- Each item is done when `dotnet build` and `dotnet test` pass. Commit and push per item (see CLAUDE.md).
- **Manual verification** (the last section) uses `- ☐` instead of checkboxes, so the run-plan workflow skips it. Those checks need the user, VRChat, VRCX or a GUI, and are done together after the build items.

### A. Foundations (done)
- [x] **Project setup.** Solution `VRChatPhotoSync.sln`, with `src/VRChatPhotoSync` (WinForms, `net10.0-windows`, nullable on) and `tests/VRChatPhotoSync.Tests` (xUnit). `.gitignore` extended with build/IDE folders. `.editorconfig`, app icon, and `AssemblyName`/`Product`/`Version` set.
- [x] **Configuration**, with tests:
  - `AppConfig` record: `ServerUrl`, `ApiKeyProtected`, `AlbumName?`, `Visibility` (default `timeline`), `ExcludePrints` (default true), `UploadLayers` (false), `StrictFileNames` (true), `VrcxWaitSeconds` (10, 0 = off), `CloseButtonAction` (`Exit`/`HideToTray`), `MinimizeButtonAction` (`Taskbar`/`Tray`), `StartMinimized`.
  - `ConfigStore`: exe dir, then `%APPDATA%` fallback; URL validation.
  - `ServerUrl` normalisation (adds `/api`).
  - `SecretProtector` (DPAPI).
  - `VRChatSettings.Load()`: the picture folder from VRChat's config, or the default from the Known Folder `MyPictures` + `VRChat`; also `SplitByDate` and `Source`; never throws.
  - Command-line options `--folder`, `--server`, `--minimized`, `--settings`.
  - `config.example.json`.
- [x] **ImmichClient core**, with tests: a shared `HttpClient` (base address, `x-api-key`, `Accept`, 5 min timeout, User-Agent), `PingAsync()` and `GetMyApiKeyAsync()` (permission warnings).

### B. Immich client
- [x] **Upload + JSON source generation.** `UploadAsync(content, fileName, sha1, createdAt, modifiedAt)`:
  - Multipart with `assetData` (`image/png`, from a caller-supplied stream or byte buffer, so the worker can upload from a snapshot).
  - Fields `fileCreatedAt`/`fileModifiedAt` (ISO-8601 `"o"` with offset), `filename`, `isFavorite=false`, and `visibility` when it is not `timeline`.
  - Header `x-immich-checksum: <sha1 hex>`.
  - Result mapping: 201 → `Created(id)`; 200 → `Duplicate(id)`; 401/403 → `AuthError`; 400 → `Rejected(body)`; 5xx, timeout or network error → `Transient`.
  - Move all DTOs to a `System.Text.Json` source-generated context.
  - Tests use a fake `HttpMessageHandler`: field names, date format, headers, and every status mapping.
- [x] **Bulk check, albums and replace helpers**, with fake-handler tests:
  - `BulkUploadCheckAsync(items)`, batched at about 500.
  - `GetOrCreateAlbumAsync(name)` (`GET albums?name=`, else `POST albums`; id cached until the name changes).
  - `AddToAlbumAsync(albumId, ids)` (`error: "duplicate"` = success).
  - `CopyAssetAsync(source, target)` (`PUT assets/copy`).
  - `TrashAssetsAsync(ids)` (`DELETE assets`, never `force`).
  - `GetServerVersionAsync()` (`GET server/version`, for the settings window).
- [x] **Integration tests** (`[Trait("Category","Integration")]`) against a real server:
  - Read `serverUrl`/`apiKey` from git-ignored `secrets.local.json` at the repo root, or from `IMMICH_TEST_URL`/`IMMICH_TEST_API_KEY`. **Skip** when neither is set. Never print the key.
  - Cover: ping, `api-keys/me`, server version, and upload of a **generated** PNG (unique pixels per run).
  - Upload the same bytes again and expect `duplicate`. Run the bulk check on those bytes and expect reject/duplicate.
  - Album create/find/add on a uniquely named test album.
  - Copy and trash, if permitted.
  - Trash every test asset at the end (if `asset.delete`). Leave the test album, and log its name.

### C. File handling (pure logic, no watcher yet)
- [x] **File names and filter**, with tests:
  - `VrcFileName`: `TryParseTimestamp` (local time from the name; falls back to the file times), `TryParseSize` (`_<W>x<H>`), `IsLayer` (`_Environment`/`_Player`/`_UI`, ignoring case), and `BaseName` (the name without the layer suffix, for finding siblings).
  - `PhotoFilter.Accepts(root, path, strictNames)`:
    - The name must be `VRChat_*.png` (ignoring case).
    - When `strictNames` is on, it must match the exact regex in the research section (allowing a layer suffix, a bare `_`, or `_wrld_<uuid>`).
    - Reject anything whose first segment under the root is `Prints`, `Stickers` or `Emoji` (ignoring case; keep the folder list as a constant).
    - Reject hidden and temp files.
  - `PhotoFilter.EnumerateCandidates(root)`: walk recursively **including the root**, skipping the excluded folders without listing them.
  - Test cases (all from the plan): layer names, `vrchat_…PNG`, `.jpg`, `.lnk`, excluded folders, root vs month folder, strict on/off, `- Copy.png` (rejected), the trailing-`_` copy (accepted), `_wrld_<uuid>` (accepted), `(1).png`/`_edited.png`/missing milliseconds/layer+world (rejected).
- [x] **PNG helpers**, with generated-PNG tests:
  - `ImageSize.TryRead(path)` (IHDR without decoding).
  - `PngChunks.HasVrcxMetadata(path)`: an `iTXt`/`tEXt` chunk with keyword `Description`. Open with `FileShare.ReadWrite | FileShare.Delete`. A truncated or invalid PNG → "not ready", no exception.
  - A test helper that inserts a `Description` chunk into a PNG (used later to simulate VRCX).
- [x] **Print detection**, with generated-fixture tests:
  - Move `print_mask.png` to `docs/`.
  - `PrintLayout` constants: photo (64,69)–(1983,1148), caption (0,1159)–(2047,1435), inclusive.
  - `PrintDetector.IsPrint(path)`: only 2048×1440 (checked via the name or IHDR first). Otherwise `Bitmap` + `LockBits`, and border pixels with min channel ≥ 240 must make up ≥ 99.9%, with early exit.
  - Tests: a made-up print → true; full-canvas noise → false; tinted border (230) → false; a 1920×1080 image → false without decoding; a few stray border pixels → true.
- [x] **Upload state**, with tests:
  - `UploadState` / `state.json` next to the config. Contents:
    - `pictureFolder`
    - `lastScanUtc`
    - an entry map `relativePath → {size, mtimeUtc, sha1, assetId, skipped?: "print"|"layer"}`
    - `session {startUtc, uploaded, duplicates, failed, printsSkipped, layersSkipped, lastFile}` and `heartbeatUtc`
  - Atomic save (write temp, then `File.Replace`/move). A corrupt or missing file → a fresh state plus a log warning.
  - Helpers: `IsUnchanged(path, size, mtime)`, `MoveEntry(oldPath, newPath)` (for renames), and `ResolveSession(now)` (continue if the heartbeat is under 2 min old, else start a new session).
  - Tests: round-trip, atomic save, corrupt file, session continue (30 s) vs new (10 min), and a folder change → treated as a first run.

### D. Watching and settling
- [x] **Folder watcher**, with tests where feasible (temp folders):
  - `FolderWatcher` on the root: `IncludeSubdirectories`, `NotifyFilter = FileName | LastWrite | Size`, 64 KB buffer.
  - Raises debounced `FileAppeared(path)` (from Created/Changed) and `FileRenamed(old, new)`.
  - On `Error`: recreate the watcher and raise `ResyncNeeded`.
  - Missing folder: report a `Waiting for <folder> to exist…` state, poll every 30 s, start when it appears, and never create it.
  - `VRChatConfigWatcher`: watch VRChat's `config.json` directory (debounced). Re-run `VRChatSettings.Load()`, and raise `PictureFolderChanged` when the folder changes. Never write to that file.
- [x] **Readiness and VRCX settle**, with fake-clock/probe tests:
  - `FileReadiness.WaitUntilReadyAsync`: can be opened and the size is stable across about 500 ms. Give up after about 60 s (the caller re-queues).
  - `VrcxSettle.WaitAsync(path, timeout)`. Return straight away for: a layer file, an existing layer sibling, `HasVrcxMetadata`, timeout 0, or a file more than 2 min old.
  - Otherwise poll every 1 s until: the chunk appears, **a layer sibling appears**, the file changes and stays the same for 2 s, the file is renamed (follow the new path), or the timeout runs out.
  - Many files can be settled at once.
  - Tests: every exit path, including a sibling at 150 ms and a chunk at 1 s.

### E. Upload pipeline
- [x] **Upload queue and worker**, with fake-client tests:
  - `UploadQueue`: a `Channel<string>`, de-duplicated by path.
  - `UploadWorker` pipeline per file:
    1. Filter.
    2. Readiness.
    3. Layer skip (`UploadLayers` off → record `skipped:"layer"`).
    4. Print skip (`ExcludePrints` on → record `skipped:"print"`; a detection exception → upload anyway).
    5. Settle (several files can wait at once).
    6. Skip if unchanged in state.
    7. **Snapshot**: read into memory, with size/mtime checked before and after. If it changed, settle again.
    8. SHA1 of the snapshot.
    9. Upload from the snapshot.
    10. Add to the album if one is set.
    11. Save state.
    12. Raise events for the UI (uploaded, duplicate, skipped, failed, status text).
  - Retry `Transient` with backoff (5 s, 15 s, 1 min, 5 min, then every 5 min) without losing order. `AuthError` pauses the queue until `Resume()`. `Rejected` → log and mark it failed.
  - Reads the settings live, so checkbox changes apply to the next file.
  - Tests: layer/print on/off, detection-exception upload, unchanged skip, retry/backoff, auth pause/resume, snapshot change → re-settle, album add.
- [x] **Renames and late VRCX edits**, with tests:
  - `FileRenamed`: if the old path is pending or settling, switch to the new path. If it is in state, call `MoveEntry`. Never upload the short-lived original name.
  - A file in state that changed (size/mtime differ) → re-upload. If the result is `created` with a new id: when `api-keys/me` grants `asset.copy` and `asset.delete` (or `all`), copy old → new, then trash old; otherwise log "kept both versions". Update state.
  - It doesn't count as a new upload; show `Updated <file> (VRCX metadata)` instead.
  - Tests: rename during settle (the `_wrld_` and bare `_` forms), rename after upload, re-upload with and without permissions, a `duplicate` result → no copy or trash.
- [x] **Catch-up scan and backfill**, with temp-folder tests:
  - Startup catch-up: walk the candidates (root + months). Enqueue files where `max(CreationTimeUtc, LastWriteTimeUtc) > lastScanUtc − 1 min` that aren't in state, oldest first. Then set `lastScanUtc` = start.
  - First run, or a picture folder different from the one in state → `lastScanUtc` = now (no mass upload).
  - `ResyncNeeded` and `PictureFolderChanged` trigger a catch-up.
  - Backfill (Upload existing photos): all candidates minus prints/layers per the settings → SHA1 → bulk check → enqueue `accept`, and record `reject` as already present. Report progress (`n/N`).
  - `CountExistingAsync()` for the first-setup checkbox (applies the same rules).
  - Tests: files in the root and month folders, copied-in old-mtime files are found, excluded folders are skipped, a folder change doesn't trigger a mass upload, backfill uses the bulk check.
- [x] **Logging.**
  - Rolling `logs\photosync-YYYYMMDD.log` next to the config, kept 7 days.
  - Record start/stop (with the picture folder and its source), each queued file, skip reasons, settle wait time, upload result + asset id, retries and errors.
  - The API key is **never** logged. Add a test that runs a fake upload and greps the log for the key.
  - Wire the logger through the existing components.

### F. User interface
- [x] **Main window**:
  - Small fixed `MainForm` (about 360×160). It shows:
    - the status line (watching / waiting for folder / waiting for VRCX (N) / uploading / retrying in Ns / key rejected / not set up)
    - "Uploaded this session: N" (plus duplicates and failures if any)
    - "Last: <file>" (ellipsis, tooltip; click → select it in Explorer)
    - the queue length when > 0
  - Checkboxes ☑ Skip prints (+ count) and ☐ Upload photo layers (+ count when > 0). Both save to config immediately.
  - Buttons **Immich server…** and **Open folder**, and a ⋯ menu:
    - Upload existing photos…
    - Window behaviour ▸ (close: Exit/Hide to tray; minimize: Taskbar/Tray; Start minimized)
    - Open log
    - Open Immich
  - Title `PhotoSync — N uploaded`.
  - All updates go through `BeginInvoke`.
  - Keep the presentation logic in a testable `MainViewModel` (status text, counters) with unit tests. The form only binds to it.
- [x] **Immich server window** (`ServerSettingsForm`), as specified in the design section (layout in "Reference: settings window layout" at the end):
  - URL (accepts with or without `/api`).
  - API key: password box + Show toggle. A saved key is never shown again; blank = keep. Trim whitespace.
  - "How do I get an API key?" link → `<server root>/user-settings?isOpen=api-keys` (tooltip with the manual route; Immich docs if no URL yet), plus the list of required permissions.
  - Test connection in the background: can't reach / not Immich / key rejected / missing `asset.upload` / ✔ version + key name, plus the album and optional copy/delete permission notes.
  - Album field.
  - First-setup-only "Also upload my N existing photos" (counted in the background, unticked by default).
  - Advanced: visibility, VRCX wait seconds, exact-names checkbox.
  - Read-only photo folder with its source, plus an "Open VRChat config folder" link.
  - Save: validate, auto-test, "Save anyway?" if the test fails, write config, restart the client, resume after an auth pause, start the backfill if it was ticked.
  - Put the validation/test-result logic in a testable class, with unit tests against a fake client.
- [x] **Tray and window behaviour**:
  - `NotifyIcon` always present (tooltip shows the count; double-click restores; menu has Show/Exit).
  - `WndProc` override: only `WM_SYSCOMMAND`/`SC_CLOSE` follows `CloseButtonAction`. A plain `WM_CLOSE` and session end always exit. `SC_MINIMIZE` follows `MinimizeButtonAction`.
  - `--minimized`/`StartMinimized` start minimized according to the minimize setting.
  - Window position saved in config.
  - Test: send `SC_CLOSE` and `WM_CLOSE` to a form with `HideToTray` (STA test) → hides vs exits.

### G. App lifecycle and packaging
- [x] **App lifecycle wiring**, with tests:
  - `Program.Main` order:
    1. Single-instance mutex. `AbandonedMutexException` = acquired; wait up to about 5 s; otherwise signal the running copy to show its window, then exit.
    2. Global exception handlers (log + status line).
    3. Load config, VRChat settings and state; resolve the session.
    4. Open the server window if not set up.
    5. Start the client, watchers, catch-up and worker.
    6. Heartbeat every 30 s and after uploads.
    7. `Application.Run`.
  - Graceful close (user exit or Windows shutdown only; VRCX kills): stop intake, give the current upload up to about 10 s, save state.
  - Tests: an abandoned mutex (a child process acquires the mutex and is killed, then the next acquire succeeds), and session continuation after a simulated kill.
- [x] **Agent-run end-to-end smoke test** (no VRChat or GUI clicks needed). Use the real server from `secrets.local.json`. Write the test `config.json` with the key DPAPI-protected by code, never echoed.
  - Run the built exe with `--folder <scratch> --minimized`, copying files from `sample-photos\` (see CLAUDE.md). Check results via the log and the Immich API:
    - one photo uploads with the right date and name; a renamed copy → duplicate
    - a slow write is waited for
    - month folder and root both detected
    - a print is skipped (and uploaded with `ExcludePrints` off)
    - a multi-layer set: 1 uploaded, 3 skipped, and no 10 s wait
    - album add
    - `taskkill /F` during an upload + more copied-in files + restart → everything caught up, and the session count continues
    - a wrong URL → retries and recovers; a wrong key → paused
    - a late `Description` chunk added with the test helper → replace (new asset in the album, old asset trashed)
    - `--folder` absent → the log shows the folder from VRChat's config (read only)
  - Also run `PrintDetector` over every `*_2048x1440.png` in `sample-photos\` (all prints) and a sample of normal photos (none). Run the strict filter over `sample-photos\` and expect only known rejects.
  - Trash all test assets afterwards. Record the results in the item's notes.
  - **Results (2026-11-21, Release build, Immich 3.4.2, dev key with `asset.upload`, `asset.copy`, `asset.delete`, `album.read`, `album.create`, `albumAsset.create`):**
    - Setup: the exe copied to a scratch folder (config, state and logs next to it); `config.json` written by a scratch helper with `SecretProtector.Protect` (key never printed; afterwards neither the key nor the wrong key appears in the log, state or config). Every test file was a copy from `sample-photos\` with an extra `tEXt` chunk holding a random id, so each checksum was new to the server and no existing asset was touched. Most runs went through a local logging reverse proxy (`--server http://localhost:<port>`) to see the multipart fields, because the key has no `asset.read`.
    - One photo (month folder): uploaded with `fileCreatedAt` = the name's timestamp, `filename` = the file name, `x-immich-checksum` = the file's SHA1. The album's `startDate` moved to the 2026-09 name date, so the server took the date from the field, not the file time. A copy under another name → `Duplicate` with the first asset's id.
    - Slow write (10 parts over 8 s): uploaded once, after the last write, with the full file's SHA1. Side note: each `Changed` event re-queued the path while it was in readiness (11 `Queued` lines, then 10 `Skipped (unchanged)`); harmless, only log noise.
    - Root and month folders: photos in the root, `2026-11\` and a `2026-10\` folder created while the app was down were all picked up.
    - Print (2048×1440): `Skipped (print)`; with `ExcludePrints` false (restart) a new print uploaded.
    - Multi-layer set (combined, then `_Environment`/`_Player`/`_UI` about 170 ms apart): combined settled with `SiblingExists after 0.0 s` and uploaded about 1.1 s after it was written; 3 × `Skipped (layer)`.
    - Album: created on the first upload and every upload/duplicate added to it.
    - `taskkill /F` while the proxy held an upload body (the upload never reached the server), then 2 more files copied in (root + a new month folder), then restart within 2 min: `Continuing the session … (5 uploaded so far)`, the catch-up queued all 3, all uploaded, the session count went on to 8.
    - Wrong URL (proxy down, connection refused): `retry 1 in 5 s`, `retry 2 in 15 s`, then the proxy came up and the next try uploaded. Wrong key: HTTP 401 → `Key rejected — uploads paused`; after restarting with the right key the catch-up uploaded the paused file.
    - Late `Description` (`PngTestFiles.InsertDescriptionChunk` after a `TimedOut after 10.0 s` upload): re-uploaded as a new asset, `PUT assets/copy` + `DELETE assets`, log `Replaced asset … (the old one is in the trash)`; bulk check: old checksum `isTrashed: true`, new one live, album count unchanged.
    - No `--folder` (separate scratch copy, unreachable server, wrong key): log shows `Picture folder: <camera roll> (from VRChat config, split by month)`, the folder treated as new (`0 queued`), and VRChat's `config.json` hash and modified time unchanged.
    - `PrintDetector` over `sample-photos\`: 10/10 `*_2048x1440.png` are prints, 0 of the other 103 PNGs. Strict filter: all 113 files accepted (the sample has no hand-made copies or JPGs; the `_` and `_wrld_` renames pass).
    - Cleanup: all 11 test assets trashed (bulk check shows every uploaded checksum `isTrashed: true`; the 3 layers and the skipped print were never uploaded). The empty album `PhotoSync smoke test 2026-11-21` is left (the key has no `album.delete`).
- [x] **Publish** a single-file self-contained build:
  - `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true` into `publish/`. No trimming, because WinForms doesn't support it.
  - Add `tools/publish.ps1`.
  - Check that the exe starts from a fresh empty folder (no config → it waits in the not-set-up state without crashing; check via the log) and exits cleanly with `taskkill`.
  - Record the size.
  - **Results (2026-11-21):** `tools/publish.ps1` → `publish\VRChatPhotoSync.exe` **49.5 MB** (51,951,463 bytes), plus `VRChatPhotoSync.pdb` (not needed to run). Copied alone into an empty scratch folder and started with `--folder <empty scratch>`: the log shows `Server: (not set); API key not set.`, `No Immich client: the server URL or API key isn't set up.`, the watcher and an empty catch-up; the Immich server window opened and the process stayed up. `config.json`, `state.json` and `logs\` were created next to the exe. Plain `taskkill /PID` → `PhotoSync stopping (exit)`, exit code 0.
    - The first run showed two `ObjectDisposedException`s on that `taskkill` (the server window's `CancellationTokenSource` was cancelled after the window had already been disposed, then disposed again by the caller's `using`). Fixed in `ServerSettingsForm` (cancel/dispose only once) with a test; the rerun logged no errors.
- [x] **README.md**:
  - what it does
  - Immich API key creation (`<server>/user-settings?isOpen=api-keys`, or profile → Account Settings → API Keys) and the required/optional permissions
  - first-run setup
  - the photo folder comes from VRChat's settings
  - what gets uploaded and skipped (other users' Prints/Stickers/Emoji, prints, layers, exact names)
  - VRCX metadata waiting
  - VRCX App Launcher: a shortcut in `%AppData%\VRCX\startup\` (`desktop\`/`vr\`); VRChat must start after VRCX; auto-close kills the app; the several-clients behaviour
  - command-line options
  - where config, state and logs live
  - building from source

  Use example values only (public repo).
- [x] **Release workflow** (GitHub Actions): on a `v*` tag, build, test (integration tests skip without secrets) and publish, then attach the exe to a GitHub release. Validate the YAML locally (`dotnet` steps run the same as `tools/publish.ps1`). Don't create a tag.
  - **Results (2026-11-21):** `.github/workflows/release.yml` on `windows-latest` with `pwsh`: checkout, .NET 10.0.x, a check that the tag is `v` + the csproj `<Version>` (fails otherwise; a `-suffix` version becomes a pre-release), `dotnet build`/`dotnet test` in Release, `tools/publish.ps1` itself, then `gh release create <tag> publish/VRChatPhotoSync.exe --generate-notes --verify-tag` with `contents: write`. The integration tests get `IMMICH_TEST_URL`/`IMMICH_TEST_API_KEY` from repository secrets when set and skip otherwise.
    - The version comes from the csproj, not the tag: overriding it with `-p:Version=<tag>` broke `AppVersion_MatchesAssemblyVersionWithoutBuildMetadata` for any other version.
    - Local check (no YAML linter available offline): the step `run` blocks were extracted from the file and run in order in a fresh clone without `secrets.local.json`. A wrong tag (`v9.9.9`) stopped at the version check; `v0.1.0` → build OK, 716 passed / 4 integration skipped, publish 49.5 MB exe (product version `0.1.0+<commit>`). The checkout, setup-dotnet and `gh release` steps only run on GitHub; they get checked on the first real tag.

---

## Manual verification (with the user, after the build items)
These use `- ☐` so the run-plan workflow skips them; `- ✔` marks a check done. Real files come from `sample-photos\` (temporary, git-excluded).

All 12 done with the user on 2026-11-21/22, using the published exe (a 200% display, a dedicated key, a test album, VRChat and VRCX running):

- ✔ First run of the published exe from a fresh folder: the Immich server window opens. Set it up through the UI only, with a **dedicated key**. The key in `config.json` is DPAPI-encrypted (`ApiKeyProtected`, no plain key in the file).
  - **Found and fixed:** at 200% display scale the Immich server window was half as wide as its contents and its bottom rows overlapped (rows were placed with fixed 96-DPI pixel coordinates after auto-scaling). It now uses auto-sizing `TableLayoutPanel`s and the app runs as `PerMonitorV2`. In the main window, "Upload photo layers (N skipped)" was cut off, so each checkbox now has its own row.
- ✔ Test connection results: correct (✔ with the version and key name), wrong key (rejected), an address that isn't Immich. Saving with the key box blank keeps the key.
- ✔ "Also upload my N existing photos" on first setup: N = 4 (the print excluded); ticked → all 4 uploaded and counted. It is hidden once a key is saved.
- ✔ Main window: the counters, "Last" click (Explorer selects the file), Skip prints / Upload photo layers toggles, ⋯ menu items (Upload existing photos, Open log, Open Immich), Open folder, title count.
- ✔ Window behaviour: the X button with Hide to tray hides; Minimize to tray; Start minimized (starts hidden in the tray); `taskkill` (no `/F`) still exits cleanly.
- ✔ Real photo with VRCX running, watching the real camera roll: uploaded about 1.1 s after the photo, after VRCX's chunk; the uploaded SHA1 matches the final file, which has `Description`.
- ✔ Real multi-layer photo: the combined photo uploaded about 1.1 s after it was saved (`SiblingExists`, no 10 s wait); all 3 layers followed the setting (on → uploaded).
- ✔ VRCX "modify filename" on: exactly one asset, under the `_wrld_…` name; the short-lived original name was never queued.
- ✔ Photo with VRCX closed: `TimedOut after 10.0 s`, then uploaded.
- ✔ Watching the real camera roll: nothing under `Prints\`, `Stickers\` or `Emoji\` queued (the startup scan counts only the user's photos). No new files arrived in those folders during the session, so the live event case is covered by tests only.
- ✔ VRCX App Launcher: a shortcut in `%AppData%\VRCX\startup\`; starting VRChat after VRCX launched it; exiting VRChat hard-killed it (no "stopping" line), `state.json` intact, the next start caught up.
- ✔ Two VRChat clients: closing the first killed PhotoSync in the middle of an upload and VRCX relaunched it about 1 s later. It took over the abandoned mutex, continued the session, and the catch-up re-sent the photo (server: duplicate, so the first upload had completed; the photo is in Immich once).

Findings from the manual checks (no code change needed):
- **Restarting VRCX while VRChat runs** makes VRCX re-add a (mostly empty) `Description` chunk to every photo in the current VRChat log, even ones that already have one. PhotoSync then replaces those photos once (copy + trash). Documented in the README.
- **A photo uploaded just before a kill** is counted as a duplicate (not an upload) by the next run, because the killed run never recorded it. Cosmetic only.
- **Log noise:** while a file is being written, each `Changed` event queues it again, giving repeated `Queued` / `Skipped (unchanged)` lines. Harmless.
Already verified during planning (2026-11-21):
- ✔ VRCX delay (~1 s) and multi-layer file order measured live (`tools/watch-vrcx.ps1`).
- ✔ The strict-name pattern over the whole camera roll: 614 files, only `- Copy.png` rejected.
- ✔ The `_` and `_wrld_<uuid>` VRCX rename forms observed.

---

## Reference: settings window layout
```
┌ Immich server ─────────────────────────────────────┐
│ Server URL: [https://immich.example.com          ] │
│ API key:    [••••••••••••••••••••••••••] [Show]    │
│             How do I get an API key? (link)        │
│ [Test connection]  ✔ Connected — Immich 3.4.2,     │
│                      key can upload                │
│ Album:      [VRChat              ] (optional)      │
│ ☐ Also upload my 2,431 existing photos  (first     │
│   setup only)                                      │
│ ▸ Advanced (visibility, VRCX wait, exact names)    │
│ Photo folder: D:\Photos\VRChat (from VRChat)       │
│                               [Save]   [Cancel]    │
└────────────────────────────────────────────────────┘
```

## Possible later additions (out of scope for v1)
- Choose an album per world, using the world name in VRCX's `Description` JSON chunk (already read for the settle check) or the VRChat log.
- Put VRCX's player list or world name into the Immich description or tags.
- A "pause uploads" toggle (for example while on a metered connection).
- Delete or move the local file after a confirmed upload (off by default).
