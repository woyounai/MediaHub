# MediaHub

中文 | English below

MediaHub 是 Windows 局域网文件管理工具。WinFsp 将索引中的原文件映射到统一的只读 SMB 共享，文件不复制、不创建硬链接或符号链接。网页提供索引、分类、名称、描述、监控和播放管理。

## 安装与启动

1. 在服务器安装 [WinFsp 官方运行时](https://winfsp.dev/rel/)。手机、电视等客户端无需安装。
2. 源码编译需安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。当前本机构建依赖 .NET 10 ASP.NET Core 和 Windows Desktop 运行时；安装 SDK 会包含所需运行环境。只有明确标为 self-contained 的发布包无需另装 .NET。
3. 在项目根目录执行：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-and-run.ps1
   ```

   接受管理员提示。脚本关闭旧 MediaHub，编译到 `artifacts/verified` 并启动。构建时需能找到已安装的 `WinFsp/bin/winfsp-msil.dll`。
4. 打开 `http://127.0.0.1:8765/`，输入 `%LOCALAPPDATA%\MediaHub\api.key` 中的 API Key。不要向他人发送此密钥。
5. 右上角可切换中文 / English，浏览器会记住选择。手机端隐藏 SMB 和开机启动入口。

## 索引和管理

- 浏览服务器文件，点击文件或文件夹的“索引”；文件夹支持递归。索引只记录原文件。
- 每行“操作”菜单提供名称、显示名称、描述、分类、播放、下载和文件操作。
- **真实文件名**会重命名原文件，保存前需确认；**显示名称**只改变目录展示，留空恢复真实文件名；描述独立保存。
- 勾选多个条目可以批量分类或删除索引。删除索引移除虚拟入口，保留原文件；“删除原文件到回收站”会操作真实文件。
- 文件夹监控可添加多个目录，变化后更新；“同步”可手动扫描。繁忙目录可能丢失监控事件，必要时手动同步。

## SMB 共享和多用户

1. 在网页“SMB 共享管理”创建唯一共享，默认名 `mediahub`。后端使用 WinFsp 挂载中的 `mediahub` 子目录，不能将任意真实目录作为聚合共享。
2. 在“共享用户与权限”选择已有 Windows 用户，设置“允许读取”或“禁止访问”，点击“应用到 SMB 和 WinFsp”。两层权限同步生效；会短暂断开连接，需要重连。
3. 客户端连接 `\\服务器IP\mediahub`，用户名使用 `服务器电脑名\本地用户名`，密码为该账户密码，不是 Windows Hello PIN。
4. 无密码微软账户可另建专用本地账户。在管理员终端执行 `net user mediahub * /add`，按提示设置密码，再在网页授权。不要将它加入管理员组。
5. 当前 SMB 为只读。源文件由服务进程读取，该进程必须拥有原文件访问权限。勿向不可信用户开放含私密文件的索引。

默认挂载根目录：`C:\ProgramData\MediaHub\Share`；共享子目录为 `mediahub`。可在启动前通过 `MEDIAHUB_VFS_MOUNT` 调整空挂载目录，通过 `MEDIAHUB_PORT` 调整网页端口。

## 播放

- “播放（本地播放器）”为当前浏览设备生成 12 小时有效的 HTTP 串流地址。安卓可唤起兼容播放器，iOS 可打开 VLC；电脑可下载 M3U 后用关联播放器打开，也可复制串流地址。
- “远程 DLNA 播放”扫描服务器所在局域网，选择接收设备后发送播放请求。电视需启用 DLNA，且能访问服务器端口。
- HTTP 和 DLNA 直接读取原文件，支持 HTTP 分段读取，不经过 SMB。无转码，播放能力取决于客户端。

## 停止、备份和排障

- 双击托盘图标打开网页；右键“退出 MediaHub”停止服务并卸载 WinFsp。共享配置保留，重启程序后可重连。
- 虚拟文件系统目前随 MediaHub 进程运行，不是独立安装的 Windows 服务。
- 数据库和权限配置在 `%LOCALAPPDATA%\MediaHub`。退出程序后备份整个目录；原文件另行备份。
- 安卓“拒绝访问”：核对账户和密码，在网页同时授权 SMB/WinFsp，清理旧连接后重连。
- 无法看到共享：确认程序运行、WinFsp 挂载成功；Windows 专用网络允许“文件和打印机共享”。网页/API 需要放行所用端口，默认 TCP 8765。不要将 SMB 或管理端口转发到公网。
- 依赖安全检查：`dotnet list src/MediaHub/MediaHub.csproj package --vulnerable --include-transitive`。

---

## English — Installation

MediaHub manages original Windows files and projects indexed files into one read-only SMB share using WinFsp. Files are not copied or linked.

1. Install the official [WinFsp runtime](https://winfsp.dev/rel/) on the server only.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build. Framework-dependent builds require ASP.NET Core and Windows Desktop runtimes; the SDK supplies them. Only explicitly self-contained releases include their own .NET runtime.
3. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-and-run.ps1` in the repository root. Accept elevation. This stops the previous process, publishes to `artifacts/verified`, then starts MediaHub.
4. Open `http://127.0.0.1:8765/`. Enter the API key from `%LOCALAPPDATA%\MediaHub\api.key`.
5. Use the top-right language button to switch Chinese / English. The setting is remembered locally.

## English — Usage

- Browse and index existing server files or folders. Indexing does not move originals.
- Use each row's Actions menu to edit the actual filename, display name, description and category, or play/download/manage the file. Editing an actual filename renames the original; editing the display name does not.
- Select rows for batch category changes or index removal. Removing an index removes its virtual entry but keeps the original. Recycling a file affects the original.
- Add multiple folder watches; changes update the catalogue. Use Sync for a manual scan.
- In SMB management, create the single `mediahub` share. Select Windows accounts, choose Allow read or Deny access, then apply to both SMB and WinFsp. Clients must reconnect after permission updates.
- Connect to `\\SERVER-IP\mediahub` with `SERVER-NAME\username` and the Windows account password, not a PIN. Passwordless Microsoft account users can create a dedicated local account with `net user mediahub * /add`, then grant it access in MediaHub.
- Local playback provides a temporary HTTP URL, Android app launch, iOS VLC support and desktop M3U playlists. DLNA scans the server LAN and sends playback to the selected receiver. No transcoding is provided.
- Exiting through the tray stops the server and unmounts WinFsp. Restart MediaHub to restore access. The filesystem currently shares the application lifecycle rather than running as a separate Windows service.

Default mount: `C:\ProgramData\MediaHub\Share`, with a `mediahub` subdirectory exported through SMB. `MEDIAHUB_VFS_MOUNT` and `MEDIAHUB_PORT` override the mount directory and HTTP port. Back up `%LOCALAPPDATA%\MediaHub` while the application is stopped, and back up original files separately. Use trusted LANs; do not expose SMB or management ports to the internet.
