# MediaHub NAS — Windows source + GitHub Actions

> Source project for building a self-contained Windows x64 EXE. This repository is an early functional foundation, not a certified NAS product. Build and validate on a Windows 10 test machine before placing important data behind it.

## What this version implements

- ASP.NET Core web server and browser UI.
- API-key authentication for `/api/*`.
- SQLite persistent file index.
- Browse local fixed/removable ready drives and directories.
- Index files/folders without moving originals.
- Search indexed files.
- Upload files to a selected server folder (or Documents\MediaHub Inbox).
- Download original files.
- Create folders; rename, move and copy original files.
- Delete files/folders to the Windows Recycle Bin using Windows Shell APIs.
- Remove index records separately without touching originals.
- FileSystemWatcher folder monitoring with automatic indexing.
- Windows startup registration (current user).
- SMB share listing, create and remove using native Windows SMB PowerShell cmdlets; creating/removing shares requires running MediaHub elevated.
- Activity log.

## Hybrid aggregation model

MediaHub's web catalogue indexes original files in place. It does not move originals into a central directory and does not create symbolic links as the primary data model.

For SMB, create shares against real existing directories. Windows SMB exposes those real paths and applies the existing NTFS ACLs. A single SMB share that transparently merges arbitrary folders from different drives is **not** implemented: doing that reliably requires a filesystem virtualization layer/driver or actual data relocation. This build deliberately avoids pretending symlinks are universally traversable by Android/TV SMB clients.

## Build using GitHub Actions

1. Create a new GitHub repository.
2. Upload the contents of this source bundle to the repository root.
3. Commit and push to `main` (or `master`).
4. Open **Actions → Build MediaHub Windows EXE**.
5. Wait for the workflow to complete.
6. Download the `MediaHub-win-x64` artifact from the workflow run.
7. Extract `MediaHub.exe` and `wwwroot` together. Do not separate the EXE from the `wwwroot` folder.

To create a versioned GitHub Release, push a tag such as `v0.1.0`. The workflow attaches `MediaHub-win-x64.zip` to that release.

Build target: .NET 10, win-x64, self-contained, single-file apphost. `wwwroot` is copied alongside the executable as static UI content. The executable includes the .NET runtime and SQLite native library bundle.

## Run on Windows 10

- Recommended: Windows 10 version 22H2 / build 19045, x64, fully patched.
- Run `MediaHub.exe`.
- It prints the local URL, LAN URL, API key, and database location; it attempts to open the browser on the server.
- On another LAN device, open `http://SERVER-LAN-IP:8765/` and enter the API key.
- If Windows Firewall blocks access, add an inbound TCP 8765 rule for the Private profile only.
- Do not port-forward 8765 or SMB TCP 445 to the public internet.

The API key is stored at `%LOCALAPPDATA%\MediaHub\api.key`. The SQLite database is `%LOCALAPPDATA%\MediaHub\mediahub.db`.

## SMB setup

- Create a normal local Windows account first (for example `media`) using Windows Settings / Computer Management.
- Run MediaHub.exe with **Run as administrator** only when creating or deleting SMB shares.
- Create a share for a real existing folder and supply account `COMPUTERNAME\media`.
- The share permission is set to Read or Change. Existing NTFS ACLs still apply; share permission and NTFS permission combine to determine effective access.
- This app does not automatically weaken NTFS ACLs. If the account lacks NTFS access, Windows will deny access even if the SMB share grants it.
- Verify with a second Windows PC first, then Android/Kodi/TV client.
- SMB1 stays disabled.

## File safety

- File delete sends the original item to the Windows Recycle Bin. Recycle Bin behavior may vary for removable/network volumes; test on each drive type.
- Move/rename are real operations on original files. Cross-volume move copies to the destination first and then sends the source to the Recycle Bin; if source recycling fails, both copies are retained and the API reports the condition.
- Copy recursively skips reparse points/symlinks.
- Folder indexing skips reparse points to avoid loops.
- The server can access any ready local drive. Reparse points/symlinks are rejected for file operations to reduce path escape risk. Keep the API key private; anyone with it can browse and modify files through the API.
- Run only on a trusted LAN. This early build uses HTTP, not HTTPS; do not use over untrusted Wi-Fi or the public internet.
- Back up the SQLite database and important originals.

## Current limitations / known gaps

- This is an initial functional foundation. It has not been compiled or tested on the user's Windows 10 machine from this environment.
- A GitHub Actions build is required to produce the EXE.
- SMB account creation is not automated; use an existing Windows account.
- SMB share creation/removal requires elevated execution. Share permissions are set, but NTFS ACLs are not changed automatically.
- Drive-root destructive operations are blocked; symbolic links/junctions are rejected by the path guard. No unified cross-volume SMB filesystem view is provided; use multiple real SMB shares or a future filesystem virtualization driver.
- No user/role management beyond a single API key yet.
- Watcher events can be lost during heavy bursts; perform a manual rescan after large transfers.
- The browser drag/drop upload sends the dropped local file to the server-side selected destination; it does not expose the client's original path. The browser cannot add a client-side file as a server-side index entry without uploading it.
- Windows Recycle Bin support must be verified on each target volume.
- No transcoding/media metadata scraper is included.

## API overview

All `/api/*` endpoints except `/api/health` require header `X-MediaHub-Key`.

- `GET /api/health`
- `GET /api/config`
- `GET /api/items?search=&category=`
- `GET /api/browse?path=C%3A%5C`
- `POST /api/index` JSON `{ "path": "D:\\Videos", "recursive": true }`
- `DELETE /api/index` JSON `{ "path": "D:\\Videos" }` — index only, originals untouched
- `POST /api/files/upload` multipart form fields `file`, optional `directory`
- `GET /api/files/download?path=...`
- `POST /api/files/folder` JSON `{ "parent": "D:\\Videos", "name": "New Folder" }`
- `POST /api/files/rename` JSON `{ "path": "...", "newName": "..." }`
- `POST /api/files/move` JSON `{ "path": "...", "destinationDirectory": "..." }`
- `POST /api/files/copy` JSON `{ "path": "...", "destinationDirectory": "..." }`
- `DELETE /api/files` JSON `{ "path": "..." }` — sends original to Recycle Bin
- `GET /api/smb/shares`
- `POST /api/smb/shares` JSON `{ "name":"Videos", "path":"D:\\Videos", "account":"PC\\media", "access":"Change" }`
- `DELETE /api/smb/shares/{name}`
- `GET/POST /api/automation/watches`
- `DELETE /api/automation/watches/{id}`
- `POST /api/automation/startup` JSON `{ "enabled": true }`
- `GET /api/logs?take=100`

## License

Choose a license before publishing publicly. Do not add a license you do not intend to grant.
