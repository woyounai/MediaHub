# MediaHub

[中文](README.md)

## Installation

### Windows package

1. Install [WinFsp](https://winfsp.dev/rel/) on the Windows server. Phones, TVs and other clients do not need it.
2. Download the `MediaHub-win-x64` build artifact from GitHub Actions and extract the entire archive to a permanent directory. This build includes the .NET runtime.
3. Double-click `MediaHub.exe` to run with normal permissions. The first launch opens the web interface; afterward, double-click the tray icon to open it.
4. Open `http://127.0.0.1:8765/`. If prompted for a key, read `%LOCALAPPDATA%\MediaHub\api.key` on the server.
5. On a phone or another computer, open `http://SERVER-IP:8765/` and enter the same API key. Use the top-right button to switch between Chinese and English.

### Install from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Install WinFsp with its Developer files selected.
3. Run this command from the repository root:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-and-run.ps1
   ```

The script stops the previous process, publishes to `artifacts/verified` and starts the app in the background. Exit an older elevated instance through its tray menu first. This build requires the .NET 10 ASP.NET Core and Windows Desktop runtimes; the SDK includes them.

## Usage

### Download center

- Open Downloads to add HTTP, HTTPS, FTP or magnet URLs, or upload a `.torrent` file up to 8 MB. Portable engines are included in the complete package; Motrix, Node.js, Docker and FFmpeg installation is unnecessary. Source startup downloads pinned versions and verifies SHA-256 checksums.
- Choose a real server folder. Each task creates a separate `MediaHub-TASK-ID` subfolder. Pause, resume, retry or remove tasks; removing cancels the task and keeps downloaded files. This initial BT integration downloads all files and stops when finished, without seeding.
- Enable automatic indexing to add completed files to the library and the existing SMB share. Incomplete, failed and cancelled temporary folders are excluded from automatic indexing. Failed indexing can be retried from the task.
- Tasks support priority, sorting by time/priority/name/speed/progress, bulk pause/resume/remove/priority changes, JSON import/export, task details and connection status. Per-task HTTP/HTTPS/SOCKS5 proxies and User-Agent/custom headers/Cookies are supported. Global settings provide a speed limit, a server-local-time schedule and a global proxy. Source URLs, proxies and headers are stored in the MediaHub database; protect its data directory.
- Download settings control concurrent tasks (1–8), connections per file (1–16), HLS segments (1–32), aria2 speed limits and scheduled limits. Queued tasks are admitted by priority. File connections depend on range support and file size; BT connections depend on peers. Speed limits do not cover HLS FFmpeg compatibility transfers.
- Completed files can be saved to the current browser device. “Open file” and “Open server folder” are shown only in a browser on the server itself. Remote browsers can inspect/copy the server path or download files to the client; webpages cannot launch arbitrary local programs on client devices.
- Direct downloads and BT retain checkpoint files. Unfinished tasks appear paused after an app restart; choose Resume to continue. Standard on-demand MPEG-TS HLS/m3u8 supports parallel AES-128 segment downloads, key rotation and cached resumption, with FFmpeg remuxing to MP4 without transcoding. Complete segments are reused for an identical playlist; changed playlists or addresses trigger a fresh download. Complex playlists such as live, fMP4, byte ranges or separate audio tracks use sequential compatibility mode and restart on resume. BT imports require selecting the torrent again; DRM, manual Key/IV, BT file selection and Motrix plugins are unsupported. SOCKS5 is unavailable in HLS FFmpeg compatibility mode. Select HLS manually for URLs without an m3u8 extension.
- Some codecs cannot be muxed into MP4; failures appear on the task. Live playlists finish only when the source ends; this version has no stop-and-save recording action.
- Status, progress and filter components are adapted from Motrix. Other screens are provided by MediaHub; this is not the complete Motrix Web UI.

### Index and manage files

- Choose Browse server files and index files or folders. Folder indexing can be recursive. Indexing does not copy or move originals.
- Upload files copies client files into the current server directory. Use the file browser to index files already on the server.
- Use each row's Actions menu to edit names, descriptions and categories, or download, copy and move files.
- Editing the actual filename renames the original. Display names and descriptions update catalogue metadata only.
- Select multiple rows to change categories or remove indexes. Removing an index keeps the original; recycling an original file affects that file.
- Add directories under Folder watches for automatic updates. Choose Sync for a manual scan.

### SMB sharing

1. Open SMB sharing on the server web interface and create one share. The default name is `mediahub`.
2. Under Share users and permissions, add existing Windows accounts, select Allow read or Deny access, then apply to SMB and WinFsp.
3. Accept the administrator prompt on the server desktop. Only creating or removing shares and changing share permissions require elevation; the main app continues running with normal permissions.
4. Connect clients to `\\SERVER-IP\mediahub` with `SERVER-NAME\username` and that account's Windows password, not a Windows Hello PIN.
5. Reconnect after permission changes. The share is read-only, and the app's Windows account must be able to read the original files.

To create a dedicated share account, run `net user mediahub * /add` in an administrator terminal, set its password, then grant access in the web interface.

### Playback

- Play locally opens a compatible player on Android, installed VLC on iPhone/iPad, or downloads an M3U playlist for the associated desktop player. You can also paste the stream URL into another player. URLs expire after 12 hours.
- Remote DLNA playback scans for receivers and sends playback to the selected device. Discovery runs on the server. Receivers must be on the server LAN, have DLNA reception enabled and be able to reach the server port.
- Playback reads original files without transcoding. Format support depends on the player or TV.

### Startup, exit and settings

- Choose Start with Windows to register a normal startup entry for the current user. Manage it in Windows Startup apps; it runs in the background after sign-in. Migration removes the old scheduled task and may require one administrator prompt.
- Set startup again after moving the app directory. SMB and startup controls are hidden on mobile.
- Double-click the tray icon to open the web interface. Choose Exit MediaHub from the tray menu to stop the app and unmount the virtual filesystem. Share configuration is retained; reconnect after restarting the app.
- Change the connection key in API Key management. Other devices must reconnect with the new key.
- The default HTTP port is `8765`; the mount directory is `C:\ProgramData\MediaHub\Share`. To override them, set these variables before starting:

  ```powershell
  $env:MEDIAHUB_PORT = '8766'
  $env:MEDIAHUB_VFS_MOUNT = 'C:\MediaHubMount'
  .\MediaHub.exe
  ```

  A custom mount directory must be empty, and the app account must be able to manage its parent directory. Reconfigure the share after changing the mount directory.

### Backups and connection problems

- Stop the app before backing up `%LOCALAPPDATA%\MediaHub`. Back up original files separately.
- If the web interface is unreachable, confirm the app is running and allow its TCP port through Windows Firewall, normally `8765`.
- If SMB is unreachable, confirm WinFsp is installed, the app is running and File and printer sharing is allowed on the Windows private network. Check the account password and web permissions, clear old connections and reconnect.
- If DLNA finds no receivers, enable reception, check that the server and receiver share a LAN, allow discovery through the firewall and scan again.
- Use a trusted LAN. Do not forward SMB or management ports to the public internet.
