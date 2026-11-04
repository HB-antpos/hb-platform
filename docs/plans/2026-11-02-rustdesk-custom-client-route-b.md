# 自建定制版 RustDesk（B 路线）+ 远程组件免弹窗安装 / 升级 方案

日期：2026-11-02 · 状态：**方案已定稿（第 3 版：按批 0～1 实施结果修正，见第 8 节实施记录）**；批 0、批 1 已完成，WPF 升级流程见「批 2～4 的 WPF 部分」

## 1. 目标与边界

- 门店收银机上的 RustDesk 由公司完全管控：**不再出现「版本更新」横幅**，不会自己升级，员工点不到「更新」，也改不了服务器。
- RustDesk 升级改为**由我们主动发起**：目标版本由中心下发，安装包由公司 CI 构建、公司签名，收银空闲时静默完成，不弹 UAC。
- 远程维护的首次安装、配置、对账、清理同样走免 UAC 代理（第 7 节），和本方案共用一套 SYSTEM 任务。
- **不改**：应用名仍叫 `RustDesk`（服务名、`Program Files\RustDesk`、配置目录都不变，现有 helper / 状态服务探针 / 对账逻辑的路径全部沿用）；服务端继续用现有开源 hbbs/hbbr（`hotbargain.vip:21116/21117`，`rd.hotbargain.vip` WSS）；不买 RustDesk Pro。
- 不在本方案内：手持 / iPad / Mac 的远程维护；RustDesk 密码策略变更；地址簿与 iOS 公司登录（见 `2026-09-06-rustdesk-ios-company-login.md`）。

## 2. 已核实的事实（RustDesk 1.4.9 源码，`rustdesk/rustdesk@1.4.9` + `hbb_common@7e1c392`）

| 项 | 事实 | 出处 |
|---|---|---|
| 定制客户端加载 | 启动时读 exe 同目录 `custom.txt`：base64 → **ed25519 验签** → JSON。公钥写死为 `5Qbwsde3unUcJBtrx9ZkvUmwFNoExHzpryHuPUdqlWM=`，私钥只有 RustDesk 官方持有 | `src/common.rs` `load_custom_client()` / `read_custom_client()` |
| JSON 结构 | `app-name`（可选，**我们不设**）、`default-settings`（可改的默认值）、`override-settings`（**锁死**，界面变灰、`set_option` 不落盘）、其余顶层键 → `HARD_SETTINGS` | 同上 |
| 键名映射 | `default-settings` / `override-settings` 内的键按 `KEYS_DISPLAY_SETTINGS` / `KEYS_LOCAL_SETTINGS` / `KEYS_SETTINGS` / `KEYS_BUILDIN_SETTINGS` 分发；不认识的键四类都写 | `read_custom_client_advanced_settings()` |
| 更新横幅 | `enable-check-update`（本地配置 `RustDesk_local.toml`，按用户），`enable-*` 默认开，只有 `N` 才关 | `common.rs` `check_software_update()`；`option2bool()` |
| 后台自动更新 | `allow-auto-update`（服务配置），`allow-*` 默认关；每天检查一次，无连接时下载安装 | `src/updater.rs` `check_update()` |
| 手动「更新」 | `check_update(manually=true)` 跳过 `allow-auto-update`，没有开关；只能靠关横幅让入口消失 | 同上 |
| 自解压外壳 | 官方 exe = `libs/portable` 外壳：把 `generate.py` 打包的**整个 Release 目录**解到 `%LOCALAPPDATA%\rustdesk\`，再 spawn 内层 `rustdesk.exe --silent-install` 后立刻退出 | `libs/portable/generate.py`、`src/main.rs` |
| 安装脚本 | `XCOPY "<内层 exe 所在目录>" "<Program Files\RustDesk>" /E`：**同目录的 `custom.txt` 会一并复制进安装目录** | `src/platform/windows.rs` `copy_raw_cmd()` |
| 上游构建 | `windows-2022`，Rust 1.75、Flutter 3.24.5、LLVM 15.0.6、vcpkg `120deac3…`；产物 `rustdesk-<ver>-x86_64.exe` 由 `generate.py` 打包 `flutter/build/windows/x64/runner/Release/` | `.github/workflows/flutter-build.yml` |
| 我方 helper | `TrustedRustDeskSha256` / `TrustedRustDeskSize` 写死官方 1.4.9；对账只换服务器，不比版本；`ManagedOptions` 三项是「我们接管」的证据 | `apps/pos-wpf/src/Hbpos.RemoteMaintenance.Setup/WindowsRemoteMaintenanceInstaller.cs` |
| 我方中心 | `RemoteMaintenance:RustdeskArtifact`（Version / FileName / Path / Sha256 / SizeBytes），文件放 `ArtifactRootPath`（容器内 `/app/App_Data/RemoteMaintenance/artifacts`），`GET api/remote-maintenance/artifacts/rustdesk` 下发 | `services/backend/BlazorApp.Api/Services/RemoteMaintenanceService.cs` |
| 我方 WPF 安装包 | 当前**未做 Authenticode 签名**（smoke build 只检查 `NotSigned` 状态） | `.github/workflows/wpf-inno-smoke-build.yml` |

结论：**fork 只需改一行常量（验签公钥）**，其余全部靠我们自己签的 `custom.txt` 实现，和 Pro 定制客户端的能力等价。

## 3. 总体架构

```
公司私钥（离线保管）
   │ 签名
   ▼
custom.txt ──┐
             ├─► CI（fork 仓库，windows-2022）──► rustdesk-<ver>-hb<N>-x86_64.exe（Authenticode 签名）
上游源码 ────┘                                        │
                                                      ▼
                        中心 appsettings RustdeskArtifact + ArtifactRootPath（版本 / 哈希 / 大小）
                                                      │ prepare / manifest
                                                      ▼
WPF helper 白名单（版本 → 哈希 / 大小，随 WPF 发布） ──► SYSTEM 代理静默 install / upgrade / reconcile
                                                      │
                                                      ▼
                        Program Files\RustDesk\{rustdesk.exe, custom.txt}  ← 锁定更新、隐藏服务器设置
```

两道独立校验保留：中心说「升到哪一版」，WPF 白名单说「允许哪些版」；任何一方单独被改都装不上包。

## 4. fork 仓库与定制内容

### 4.1 fork 改动（保持最小）

1. 仓库：`hot-bargain/rustdesk`（**私有 fork**，基于 tag `1.4.9`；分支 `hb/1.4.9`）。每次跟上游 = 新建 `hb/<ver>` 分支 cherry-pick 下面两个提交。AGPL：装在公司自有设备属内部部署，不触发源码分发义务；fork 的改动记录（公钥常量 + CI）保留在仓库历史里，需要时可随时提供。
2. `src/common.rs` `read_custom_client()`：`const KEY` 换成**公司 ed25519 公钥**（base64）。只有这一处业务改动。
3. `.github/workflows/`：删掉上游全部 job，只保留 `build-for-windows-flutter` 的 x64 分支，加两步：
   - 打包前把仓库根目录的 `hb/custom.txt` 复制到 `flutter/build/windows/x64/runner/Release/`（`generate.py` 会把它打进自解压包，安装脚本 XCOPY 进 `Program Files\RustDesk`）；
   - 产物改名 `rustdesk-<ver>-hb<N>-x86_64.exe`，**不做 Authenticode 签名**（见 4.4）；输出 `sha256` / `size` 到 job summary。CI 产物只通过 GitHub Actions artifact 下载，由发布负责人核对哈希后上传中心，不经过任何浏览器下载到门店机。
4. 不改应用名、不改图标、不改服务名、不改默认安装路径。`is_custom_client()` 判断的是 app name，保持 `RustDesk` 即可继续走我们熟悉的标准安装路径（自解压 → `--silent-install` → `--install-service`）。

### 4.2 `custom.txt` 内容（JSON，签名前）

```json
{
  "override-settings": {
    "enable-check-update": "N",
    "allow-auto-update": "N",
    "disable-change-id": "Y",
    "hide-server-settings": "Y",
    "hide-security-settings": "Y",
    "hide-proxy-settings": "Y",
    "hide-websocket-settings": "Y",
    "hide-stop-service": "Y"
  },
  "hb-build": "hb1",
  "hb-issued-at": "2026-11-02"
}
```

设计要点：
- **服务器地址不放进 custom.txt**（第 2 版写的是放 `default-settings`，实施时改掉）：
  - 放 `override-settings`：`set_option` 不落盘、读回永远是锁定值，中心换域名时对账永远失败；
  - 放 `default-settings`：RustDesk 的 `is_option_can_save()` 规定**写入值等于默认值时不落盘**，对账在两个公司地址间来回切换时会出现「写了没存」；
  - 所以服务器地址只由 helper 写入并对账，隐藏服务器设置页保证员工改不了。
- `verification-method` / `approve-mode` / `allow-only-conn-window-open` **不放进 custom.txt**：它们是 helper 判定「这是我们接管的 RustDesk」的证据（`ManagedOptions`），继续由 helper 写入并读回。
- `hide-tray` 不设：门店偶尔要看 ID。
- 顶层 `hb-build` / `hb-issued-at` 只是标记（进 `HARD_SETTINGS`，客户端不解释），方便排查。
- 改任何锁定项 = 重新签 custom.txt = 重新构建包（它被打进自解压包）。这是刻意的：锁定项的变更必须走发布流程。

### 4.3 签名与密钥

- 格式：libsodium `crypto_sign`（签名 64 字节 + 原文），base64 后写入 `custom.txt`，与 `sign::verify` 对应。实际用 Node 内置 ed25519 签名（RFC 8032 签名与 libsodium 相同），并用 PyNaCl 的 `crypto_sign_open` 独立验过。
- 密钥对在发布负责人本机生成，私钥只存在其密码管理器（不进 CI、不进仓库）；公钥只进 fork 源码。
- 签名工具 `hb/custom-client-signing.mjs` **放在 fork 仓库**（与配置、构建流程在一起；第 2 版写的放 hb-platform，实施时改掉）：`keygen` / `sign` / `verify` / `pubkey`，`sign` 前确认私钥与源码公钥是一对。CI 第一步就用源码公钥验 `custom.txt`。
- WPF helper **不验签**：.NET 8 没有内置 ed25519。安装包整体已按哈希白名单校验，随包 `custom.txt` 的哈希也就确定了，装后只比对 `custom.txt` 的 SHA256（见 5.1）。

### 4.4 不做 Authenticode 代码签名（已定）

- 不买证书。helper 用 `Process.Start` 拉起下载的包，文件由 `HttpClient` 写盘、没有 Mark-of-the-Web，不触发 SmartScreen；安装动作由 SYSTEM 代理 / 提权 helper 执行，也不经过 Explorer。
- 风险在 Defender 的**启发式误报**：未签名的 RustDesk fork 被标成 PUA / 远控工具的案例不少，且 `rustdesk.exe` 是常驻服务，误报会直接把远程维护打断。对策：
  1. 批 3 在测试店 1042 跑**满两周**，每日看 Defender 保护历史（`Get-MpThreatDetection`）与状态服务心跳；期间不推任何其他门店。
  2. 状态服务心跳补一项「RustDesk 服务是否在跑 + 可执行文件是否还在」（现有探针已读 `rustdesk.exe` 版本，只需多报存在性与服务状态），中心列表把「文件消失 / 服务停止」单独标红，这是误报隔离最直接的信号。
  3. 若 1042 两周内出现隔离：先用 Defender 排除项 `Program Files\RustDesk\rustdesk.exe`（由 SYSTEM 代理 `Add-MpPreference -ExclusionPath` 写入，随安装一起做；仅限这一个路径，不排除整个目录），再决定是否回头买证书。排除项作为备选预案写进 `upgrade` 阶段，默认**不启用**。
- 哈希白名单（5.1）是我们自己的完整性保证，不依赖 Authenticode；签名只是 Defender 信誉问题，不影响安全模型。

## 5. WPF 侧改动（`apps/pos-wpf`）

### 5.1 helper：白名单 + custom.txt 校验（`WindowsRemoteMaintenanceInstaller.cs`）

1. `TrustedRustDeskBuilds`：`安装包 SHA256 → (Version, Size, CustomClientSha256)`。现有两条：官方 `1.4.9`（不带 custom.txt，保留作回退）与 `1.4.9-hb1`。
2. `InstallAsync` / `UpgradeRustDeskAsync`：按中心下发的**哈希**查白名单（不按版本字符串，避免中心配置写法不同导致误拒），查不到在触碰本机前拒绝。
3. 安装 / 升级完成后 `EnsureCustomClientInstalledAsync`：白名单条目带 `CustomClientSha256` 时，`Program Files\RustDesk\custom.txt` 的 SHA256 必须相等，否则报配置失败（安装按 fail-closed 清理）。
4. 安装 / 对账 / 升级都尽力写 `--option allow-auto-update N`（#1124 起不读回，写失败不影响安装与对账）。**不要**把它加进 `ManagedOptions`，否则老设备会被判成未接管。

### 5.2 新增 `upgrade` 阶段（helper `Program.cs` + `WindowsRemoteMaintenanceInstaller.cs`）

前置：journal 为 `Committed`，中心 manifest 的版本 ≠ journal 的 `ClientVersion`，且目标在白名单内。流程：
1. 用户态：下载并校验新包到 `%LOCALAPPDATA%\HBPOS\RemoteMaintenance\<op>\`，写 journal `PendingUpgradeArtifactPath` + 目标版本。
2. 代理（SYSTEM）：
   - 读接管标记、确认 `ManagedOptions` 齐全（证明是我们的 RustDesk）；
   - **远程会话**：只靠用户态调度在夜间窗口 + 收银空闲时发起（7.3）。代理端「看有没有 `--cm` 子进程」没有做：.NET 读不到进程命令行（要引入 WMI），而夜间窗口里有人远程的概率很低，升级只会断开那一次连接；
   - 复制到受保护目录 → 哈希校验 → `--silent-install` → `WaitForDetachedRustDeskSetupAsync` → 确认服务存在并 `start`；
   - 升级后：`custom.txt` 哈希核对；`ManagedOptions` 读不回 = 服务配置连同密码被重置，升级侧拿不到密码，报配置失败交给人工修复；服务器与升级前不同就用 `ConfigureRustDeskAsync(password: null)` 写回，`api-server` 也按升级前的值补回；接管标记不变（服务器没变）；
   - 永久密码：升级不重设。RustDesk 安装脚本保留 `%ProgramData%`/服务配置，密码应仍在；用 `--password` 的 IPC 确认路径不可用（那会重设）。改为：升级后由状态服务下一次心跳上报 ID 与版本，中心按 ID 不变 + 版本变化记一条升级事件；运维用地址簿连一次验收。
   - journal `ClientVersion` 更新，`PendingUpgrade*` 清空。
3. 失败：不回滚（RustDesk 安装脚本不可逆），记 `UpgradeFailureCount`，状态「升级未完成」，下次对账再试；连续 3 次失败停止自动重试，设置页「修复」可手动触发。
4. 中心 `commit` 接口**不需要改**：ClientVersion 变化通过心跳（`heartbeat` 已上报版本）而不是重新 commit。

### 5.3 过渡期兜底（可先于 fork 上线，纯 C#）

- WPF 启动时若本机有受管 RustDesk：改当前用户 `%APPDATA%\RustDesk\config\RustDesk_local.toml` `[options]` 的 `enable-check-update = 'N'`（只改这一键，文件不存在则新建，失败静默）。官方 1.4.9 设备在换成定制包前靠这个压横幅。
- 对账时执行 `--option allow-auto-update N`（同 5.1 第 4 点）。

### 5.4 设置页

- 远程维护卡片增加「RustDesk 版本：1.4.9-hb1（最新）/（待升级）/（升级未完成）」；「修复」按钮在待升级时文案改「立即升级」。
- 进度文案去掉「等待 Windows 授权」，代理模式下显示「后台安装中」。

## 6. 中心 / 后端改动（`services/backend`）

1. `RemoteMaintenanceOptions.RustdeskArtifact` 不改结构；发布新包 = 上传 exe 到 `ArtifactRootPath` + 改 `Version/FileName/Path/Sha256/SizeBytes` + 重建主 API 容器。`Version` 用 `1.4.9-hb1` 这类字符串（manifest 与 journal 对比用；心跳上报的 `FileVersionInfo.ProductVersion` 仍是 `1.4.9`，两者并存）。
2. 设备管理列表：现有「RustDesk 客户端版本」列旁加「与目标一致 / 待升级 / 版本偏离」。偏离 = 心跳版本的主版本号 ≠ manifest 主版本号（能抓出已经被人点成 1.5.0 的机器）。
3. 升级事件：`heartbeat` 收到版本变化时写一条设备日志（现有 `RemoteMaintenanceDevice` 表加 `ClientVersionChangedAtUtc`，或复用操作日志）。
4. 无迁移则不需要；若加列，走 `SchemaMigrations` 显式迁移。

## 7. 免 UAC 代理（与本方案共用；可作为第一期单独交付）

### 7.1 任务注册

- Inno `[Run]`：`Hbpos.RemoteMaintenance.Setup.exe --register-agent-task`，注册「HB POS Remote Maintenance Agent」计划任务：SYSTEM、`RunLevel=Highest`、`Hidden`、`MultipleInstances=IgnoreNew`、动作固定 `{app}\Hbpos.RemoteMaintenance.Setup.exe --agent $(Arg0)`、安全描述符 `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)`；只在 `{app}` 位于 Program Files 下注册。`[UninstallRun]` 反注册。实现复用 `Hbpos.Updater/UpdateAgentTaskScheduler.cs` 的 COM 代码（抽到共享类或复制，二选一，不引入新依赖）。
- `UacHelperLauncher.RunAsync`：任务存在且动作路径 == 本机 helper → `task.Run(请求)`，轮询 `{app}\RemoteMaintenanceAgent\status\<op>.json` 拿退出码；否则退回 `runas`（旧版升级前的过渡）。

### 7.2 各阶段的信任边界（journal 变成不可信输入后）

| 阶段 | 代理接受的输入 | 必须在 SYSTEM 端完成 |
|---|---|---|
| `reconcile` | journal `PendingConfig` | 已有：白名单、公钥不变、接管标记、`ManagedOptions` 读回。**可直接迁，零改动** |
| `upgrade` | journal 目标版本 + 包路径 | 白名单哈希、接管标记、custom.txt 验签、配置读回 |
| `install` | journal `Config` + 包路径 | 哈希白名单；**无人值守密码由代理生成**，明文只存 `{app}\RemoteMaintenanceAgent\secrets\`（ACL 仅 SYSTEM/Administrators），交给用户态的是**用中心公钥加密的密码**，commit 原样透传 → **中心 commit 要支持加密密码（第二期，后端先部署）** |
| `configure` | journal token + heartbeat URL | 已有路径校验，补主机名白名单 |
| `fail-closed` | operationId | 只允许代理自己记录过的 operationId |

### 7.3 调度（已定：营业时段外 + 收银空闲）

- `RemoteMaintenanceReconcileScheduler` 保持「收银员登录后」触发对账，不受升级窗口限制（对账只换服务器地址、几秒完成）。
- `upgrade` 新增 `RemoteMaintenanceUpgradeScheduler`，**两个条件同时满足**才发起（实施时改为复用 WPF 自身夜间无人值守安装的判定，不另造一套）：
  1. **营业时段外**：`AppUpdateUnattendedInstallOptions` 的窗口，默认本机 18:00–次日 08:00，周四 20:00 起（门店营业更晚）；可用 `AppUpdate:UnattendedInstallWindow` 配置，Debug 构建默认关。收银机系统时间即门店当地时间。
     **日结后提前**（11-02 追加）：当天营业日的日结保存成功后打开 `PostDailyCloseMaintenanceWindow`，到次日 08:00 关闭；窗口打开且收银员已登出（日结后 30 秒自动登出）时也算营业时段外，WPF 自身更新同样适用。补做往日日结不提前；收银员又登录时暂停，登出后恢复。
  2. **收银空闲**：`IAppUpdateInstallSafetyGuard`（购物车为空、没有付款进行中）连续两轮（每轮 5 分钟）。
- **下载放在白天**：向中心取安装包要收银员登录票据，夜里常没人登录。所以白天登录后的对账拿到 manifest 时，若构建与本机不同且在白名单内，就后台下载校验好记进 journal；夜里只需本地交给代理，不联网。
- 失败后隔 30 分钟再试；同一构建累计失败 3 次停止自动升级，换新构建才重新给机会。
- 升级本身预计 1～2 分钟（下载在用户态提前完成，代理只做安装），即使收银员在窗口内回到收银台，RustDesk 升级也不影响收银操作，只影响远程连接。

## 8. 发布顺序

| 批次 | 内容 | 依赖 | 需要 |
|---|---|---|---|
| 0 | 私有 fork 仓库、密钥对、签名脚本、CI 跑通 1.4.9-hb1（不签名） | — | 无生产变更（**已完成**） |
| 1 | WPF：5.3 兜底 + 7.1 代理 + `reconcile` 迁代理 + 5.1 白名单（含 hb1 哈希）+ 心跳补服务 / 文件存在性 | — | WPF 建版 |
| 2 | 中心：上传 hb1 包 + 改 manifest；设备列表版本列 + 「文件消失 / 服务停止」标红 | 批 1 全量后 | 主 API 重建 + Web |
| 3 | 测试店 1042 手动「立即升级」验收：5.2 全流程 + **Defender 观察满两周**（4.4）；期间不推其他门店 | 批 2 | — |
| 4 | WPF：`upgrade` 自动调度放开（营业时段外 + 收银空闲，7.3；按门店灰度，复用 WPF 版本策略的门店范围） | 批 3 无隔离记录 | WPF 建版 |
| 5 | 后端 commit 支持加密密码 → WPF `install`/`configure`/`fail-closed` 迁代理（首次安装也免 UAC） | 批 4 | 主 API → WPF |

批 1 上线后，自动对账弹窗即消失、横幅被压住；批 2～4 完成后版本与更新完全受控；批 5 之后首次安装也不弹。

**批 1 实施记录（2026-11-02，分支 `claude/wpf-remote-component-install-ab49ff`）**

- 已做：
  - helper 新增 `--register-agent-task` / `--unregister-agent-task` / `--agent`，注册「HB POS Remote Maintenance Agent」计划任务；Inno 安装时注册、卸载时注销。
  - 代理请求只带目标服务器（白名单校验），SYSTEM 端不读写用户 journal，结果写 `{app}\RemoteMaintenanceAgent\status`。
  - `RemoteMaintenanceService` 对账优先走代理：代理可用时不受「5 次失败后停弹 UAC」上限约束，代理未受理也不退回 UAC；任务不存在时照旧走 UAC helper。手动修复里的对账同样走代理，`configure` 仍走 UAC。
  - 安装与对账都写入 `allow-auto-update = N` 并读回（不进 `ManagedOptions`）。
  - `TrustedRustDeskSha256` 改为 `TrustedRustDeskBuilds` 白名单，按中心下发的哈希查表。目前只有官方 1.4.9，hb1 哈希等批 0 出包后再加。
  - WPF 启动时对已登记（Committed）设备写当前用户 `RustDesk_local.toml` 的 `enable-check-update = 'N'`。
- 未做（顺延）：心跳上报「`rustdesk.exe` 存在性」（4.4 第 2 点）。现有探针在服务不存在时已上报 `notInstalled`、文件不存在时版本为空，可先用这两项识别；专门字段与中心标红并入批 2。
- 已知限制：`allow-auto-update` 只在安装与「中心服务器有变化」的对账时写入。服务器没变化的老设备要等下次换服务器或手动修复才会写；在此之前靠 RustDesk 默认值（关）与横幅关闭兜底。换成 hb1 后由 custom.txt 锁定。

**批 0 实施记录（2026-11-02）**

- 私有仓库 `HB-antpos/rustdesk-hb`：上游 1.4.9 快照（不带历史）→ 删掉会自动触发的 6 个上游工作流 → `src/common.rs` 验签公钥换成 `8M5+hpKPmtMuqLHns094CjBTYeuf+uN8qbe4k4jRFhk=` → `hb/` 目录与手动触发的 `hb-windows-x64.yml`。
- 首次构建约 64 分钟（无缓存）。预发布 release `1.4.9-hb1`（tag 指向构建源码 `28e3e19`）：exe SHA256 `54425f81…4884`、24,351,744 字节；内含 custom.txt SHA256 `bdc2b187…cbc5`（456 字节）。已拆包核对：105 个文件、custom.txt 与仓库逐字节一致且验签通过、`librustdesk.dll` 含公司公钥（Flutter 版验签代码在 dll，不在 358KB 的 `rustdesk.exe` 外壳里）。
- 旁支会话另加了构建产物归档腾讯云 COS 的步骤（`7a9d11f`）。

**批 2～4 的 WPF 部分（分支 `claude/rustdesk-hb1-upgrade`）**

- 白名单加入 hb1（带 custom.txt 哈希），安装与升级后核对 custom.txt。
- helper 新增 `UpgradeRustDeskAsync` 与代理 `upgrade` 请求（只带白名单哈希 + 本地包路径；SYSTEM 端拒绝 UNC / 设备路径与重解析点，复制进管理员目录后再核对大小与哈希）。
- `RemoteMaintenanceService`：白天对账时预下载待升级构建（只在代理可用时），`ApplyPendingUpgradeAsync` 只走代理、绝不弹 UAC；`RemoteMaintenanceUpgradeScheduler` 按 7.3 调度。
- WPF 安装包新增 `--sync-status-agent`：已装状态服务的机器在 WPF 升级时同步新的状态服务程序（配置不动），解决「状态服务副本永远停在首次安装时的版本」。
- 中心 manifest 切到 hb1 的前提：**所有门店的 WPF 都已带上 hb1 白名单**（旧 WPF 收到不认识的哈希会拒绝安装，新装机器会装不上）。

## 9. 验证

- 单元：helper 白名单、custom.txt 验签（含篡改 / 缺失 / 符号链接）、`allow-auto-update` 读回、`upgrade` 状态机（`Hbpos.RemoteStatus.Tests` 风格的纯逻辑测试，macOS 可跑，见 `wpf-pure-logic-tests-on-macos-temp-host` 记忆）。
- 集成（Windows 测试机 / 1042）：官方 1.4.9 → hb1 升级；hb1 → hb2（改 custom.txt）升级；升级中断（断电）后重试；中心换域名后对账仍成功（证明服务器没被 override）；界面确认横幅消失、服务器设置页隐藏、「关于」里没有更新入口。
- 回归：地址簿连接、无人值守密码仍有效、状态服务心跳版本正确。
- 安全：普通用户触发代理传任意 journal 不能装非白名单包、不能改服务器到白名单外、不能让代理重设密码。

## 10. 决策（2026-11-02 已定）

| # | 决策 | 结论 | 落点 |
|---|---|---|---|
| 1 | 代码签名证书 | **先不买**。靠哈希白名单保完整性；Defender 误报风险用 1042 两周观察 + 心跳存在性监控 + 预留排除项预案对冲 | 4.4、批 3 |
| 2 | fork 可见性 | **私有 fork**。内部部署不触发 AGPL 分发义务，改动历史留在仓库 | 4.1 |
| 3 | 自动升级窗口 | **营业时段外 + 收银空闲**，两条件同时满足；对账不受限 | 7.3 |
| 4 | 是否跟 1.5.0 | **先不跟**。进白名单前必须对 1.5.0 源码重新核对三处依赖：① 自解压外壳 spawn 内层后立即退出、退出码 0 ≠ 安装完成；② `--password` 经 IPC 要服务端确认才输出 `Done!`；③ `--config` / `--option` / `--get-id` 在 IPC 不通时静默回退到调用者本地配置。核对通过后还要在 1042 跑完整安装 + 升级 + 对账流程 | 5.1 白名单、第 12 节 |

## 11. 回滚

- 包：manifest 改回官方 `1.4.9`（白名单保留），已升级设备不自动降级（RustDesk 不支持），需要时走 `upgrade` 同样流程装官方包（白名单允许）。
- 代理：任务反注册后 `UacHelperLauncher` 自动退回 `runas`。
- custom.txt：删掉 `Program Files\RustDesk\custom.txt` 即恢复官方行为（需管理员）；helper 的 custom.txt 核对只对白名单里带 `CustomClientSha256` 的构建生效，中心 manifest 改回官方 1.4.9 后，下次夜间升级会装回官方版。

## 12. 跟进上游版本的固定流程（1.5.0 及以后）

1. 读目标 tag 的 `libs/portable/src/main.rs`、`src/platform/windows.rs`（`install_me` / `update_me` / `--silent-install`）、`src/core_main.rs`（`--password` / `--config` / `--option` / `--get-id` 分支）、`src/ipc.rs`（CLI 超时与回退逻辑），逐条对照第 10 节第 4 项的三处依赖，结论写进本文档附录。
2. 同时核对 `custom.txt` 机制未变：`read_custom_client()` 公钥常量位置、`override-settings` 语义、`KEYS_SETTINGS` 里 `allow-auto-update` / `enable-check-update` 仍存在。
3. 新建 `hb/<ver>` 分支 cherry-pick 公钥与 CI 两个提交，CI 出包，记录哈希。
4. 1042：官方 1.4.9 → 新版、hb1 → 新版两条升级路径 + 对账 + 无人值守连接，全部通过后才把新版加进 helper 白名单并发 WPF 版。
5. 中心 manifest 最后切，且先只对 1042 生效（批 4 的门店灰度机制）。

## 附录 A：1.5.0 核对记录与 hb2（2026-11-04）

**为什么跟 1.5.0**：门店要求受控端被连接时不弹连接管理窗口（CM）。截图里门店关掉 CM 或点 Disconnect 会直接断开远程（控制端报「连接错误：被对方手动关闭」）。

**隐藏 CM 的条件（1.4.9 与 1.5.0 相同）**：

- hbb_common `password_security::hide_cm()` = `approve-mode=password` 且 `verification-method=use-permanent-password` 且 `allow-hide-cm=Y`。前两项 helper 已作为 `ManagedOptions` 写入。
- 但 CM 进程启动时经 IPC 向服务端要 `hide_cm`（`flutter/lib/main.dart` `runConnectionManagerScreen()` → `cm_get_config` → `src/ipc.rs` `"hide_cm"` 分支），服务端只在 `is_pro() || is_custom_client()`（`app-name ≠ RustDesk`）时才返回值。我们不改名，所以 fork 必须去掉这道门槛；`allow-hide-cm` 不在任何 KEYS 数组里，放进 custom.txt `override-settings` 时按「不认识的键四类都写」进入 `OVERWRITE_SETTINGS`，`Config::get_option` 优先读它。
- Flutter `server_model.dart` 里按选项动态显示 / 隐藏的代码上游已注释掉，只在 CM 启动时决定一次。

**第 12 节逐条核对（`rustdesk/rustdesk@1.5.0` = `fada664`，`hbb_common@229b904`）**：

| 项 | 结论 |
|---|---|
| ① 自解压外壳 spawn 内层后立即退出 | `libs/portable` 新增 RDPKG 资源（按客户注入、免重编译），资源不存在时 `parse_package_blob` 返回空，仍走内嵌 `data.bin`；`execute()` 仍是 spawn 不等待。解压目录改为按 exe 名（`rustdesk`），与原 `APP_PREFIX` 相同 |
| ② `--password` 经 IPC 确认才输出 `Done!` | `core_main.rs` 的 `--password` / `--config` / `--option` / `--get-id` / `--silent-install` 分支与 1.4.9 逐行一致 |
| ③ IPC 不通时回退本地配置 | `get_config` / `set_config` / `set_option` / `set_permanent_password` 不变；`get_id()` 新增「IPC 返回空 ID 视为无回应、回退本地」，更稳 |
| 安装复制 | `copy_raw_cmd` 的 `XCOPY` 不变，custom.txt 仍随目录复制；`install_me` 新增「已有 MSI 安装时拒绝 EXE 覆盖」（门店都是 EXE 安装）与路径字符校验 |
| custom.txt 机制 | `read_custom_client()` 与 1.4.9 一致，公钥常量仍在 `src/common.rs`；选项键常量迁到新 crate `libs/base/src/config/keys.rs`，锁定项的键都在 |
| 构建（x64） | Rust 1.75 / Flutter 3.24.5 / LLVM 15.0.6 / FRB 1.80.1 不变；`VCPKG_COMMIT_ID` 120deac → 9e593bb；上游新增 MSI 模板步骤与我们无关；`build.py` 改动只涉及 Linux |
| 上游工作流 | 新增每周定时的 `update-webpki-roots.yml`，fork 已删 |

**1042 必须实测的新风险**：

- Kx v1（#16326，协商式密钥交换）与 WebRTC（#15684）：上游称兼容旧端，但我们的 hbbs/hbbr 是 1.1.16 + WSS 补丁版，控制端仍是 1.4.9。要测 Mac / Windows 1.4.9 控制端 → 1.5.0-hb2 受控端的原生 TCP 与 WSS 两条路、通讯录在线状态、无人值守密码。
- 1.5.0 `update_me` 注释承认覆盖升级后可能丢托盘图标直到下次启动。隐藏 CM 后托盘图标是门店唯一的「RustDesk 在运行」提示，验收要专门看。
- 隐藏 CM 后门店看不到有人在远程、也不能自己断开（新州 Workplace Surveillance Act 2005 有告知要求），是否在 WPF 加「远程维护中」提示另行决定。

**hb2 构建**（fork 分支 `hb/1.5.0`，源码 `1ee7dbb`）：

- 提交：导入上游 1.5.0 快照 → 删自动触发工作流（多删 `update-webpki-roots.yml`）→ 换公钥 → hb/ 目录与构建流程 → COS 归档两提交 → `src/ipc.rs` 去掉 hide_cm 门槛 + custom.txt 加 `allow-hide-cm=Y`（hb-build=hb2）→ 构建流程跟进 1.5.0。
- custom.txt：484 字节，SHA256 `f53058f04d05c7f29746c5fa64df81fff81b318b9d76501a93a35dc8272632b1`。
- 构建：Actions run 41785536474（约 57 分钟，vcpkg 换提交后无缓存），预发布 release `1.5.0-hb2`（tag 指向 `1ee7dbb`），COS 已归档 `rustdesk-builds/1.5.0-hb2/`。
- 安装包 `rustdesk-1.5.0-hb2-x86_64.exe`：SHA256 `212b72a745d841f7730f077ce173281fea526893945682b940c1206b6377a72c`，25,765,376 字节。
- 已拆包核对：105 个文件；custom.txt 与仓库逐字节一致，PyNaCl 验签通过；`librustdesk.dll` 含公司公钥；`rustdesk.exe` 版本资源为 1.5.0。
- WPF 白名单条目（1042 验收通过后才加）：`["212b72a7…a72c"] = new("1.5.0-hb2", 25765376, "f53058f0…32b1")`。
- 尚未在任何门店机器上安装验证。
