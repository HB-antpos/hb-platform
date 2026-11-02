# 自建定制版 RustDesk（B 路线）+ 远程组件免弹窗安装 / 升级 方案

日期：2026-11-02 · 状态：**方案已定稿（第 2 版，决策项已拍板，见第 10 节），未实现**（配套的「免 UAC 代理」见第 7 节，可独立先做）

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
  "default-settings": {
    "custom-rendezvous-server": "hotbargain.vip:21116",
    "relay-server": "hotbargain.vip:21117",
    "key": "<hbbs 公钥>"
  },
  "hb-build": "hb1",
  "hb-issued-at": "2026-11-02"
}
```

设计要点：
- **服务器地址放 `default-settings` 而不是 `override-settings`**。对账（`ReconcileServerAsync`）是通过 `--config` 改服务配置再读回校验；若服务器被 override，`set_option` 不落盘、读回永远是旧值，中心换域名时对账会一直失败。放默认值 + 隐藏服务器设置页，员工改不了，helper 仍能改。
- `verification-method` / `approve-mode` / `allow-only-conn-window-open` **不放进 custom.txt**：它们是 helper 判定「这是我们接管的 RustDesk」的证据（`ManagedOptions`），继续由 helper 写入并读回。
- `hide-tray` 不设：门店偶尔要看 ID。
- 顶层 `hb-build` / `hb-issued-at` 只是标记（进 `HARD_SETTINGS`，客户端不解释），方便排查。
- 改任何锁定项 = 重新签 custom.txt = 重新构建包（它被打进自解压包）。这是刻意的：锁定项的变更必须走发布流程。

### 4.3 签名与密钥

- 格式：libsodium `crypto_sign`（签名 64 字节 + 原文），base64 后写入 `custom.txt`，与 `sign::verify` 对应。用 Python `PyNaCl`：`SigningKey.sign(json_bytes)` 的输出就是这个格式。
- 密钥对**离线生成**，私钥只存在发布负责人的密码管理器（不进 CI、不进仓库）；公钥进 fork 源码与 WPF helper（helper 用它验 `custom.txt`，见 5.1）。
- 签名脚本 `scripts/rustdesk-custom/sign-custom.py`（放 hb-platform，不放 fork）：输入 JSON + 私钥，输出 `custom.txt`，同时打印 `hb-build`。签好的 `custom.txt` 提交到 fork 的 `hb/custom.txt`，CI 只做复制。

### 4.4 不做 Authenticode 代码签名（已定）

- 不买证书。helper 用 `Process.Start` 拉起下载的包，文件由 `HttpClient` 写盘、没有 Mark-of-the-Web，不触发 SmartScreen；安装动作由 SYSTEM 代理 / 提权 helper 执行，也不经过 Explorer。
- 风险在 Defender 的**启发式误报**：未签名的 RustDesk fork 被标成 PUA / 远控工具的案例不少，且 `rustdesk.exe` 是常驻服务，误报会直接把远程维护打断。对策：
  1. 批 3 在测试店 1042 跑**满两周**，每日看 Defender 保护历史（`Get-MpThreatDetection`）与状态服务心跳；期间不推任何其他门店。
  2. 状态服务心跳补一项「RustDesk 服务是否在跑 + 可执行文件是否还在」（现有探针已读 `rustdesk.exe` 版本，只需多报存在性与服务状态），中心列表把「文件消失 / 服务停止」单独标红，这是误报隔离最直接的信号。
  3. 若 1042 两周内出现隔离：先用 Defender 排除项 `Program Files\RustDesk\rustdesk.exe`（由 SYSTEM 代理 `Add-MpPreference -ExclusionPath` 写入，随安装一起做；仅限这一个路径，不排除整个目录），再决定是否回头买证书。排除项作为备选预案写进 `upgrade` 阶段，默认**不启用**。
- 哈希白名单（5.1）是我们自己的完整性保证，不依赖 Authenticode；签名只是 Defender 信誉问题，不影响安全模型。

## 5. WPF 侧改动（`apps/pos-wpf`）

### 5.1 helper：白名单 + custom.txt 校验（`WindowsRemoteMaintenanceInstaller.cs`）

1. `TrustedRustDeskSha256` / `TrustedRustDeskSize` 改为 `TrustedRustDeskBuilds`：`版本 → (Sha256, Size, RequiresCustomTxt)`。首批两条：官方 `1.4.9`（保留作回退）与 `1.4.9-hb1`。
2. `InstallAsync`：按 manifest 的 `Version` 查白名单；查不到直接拒绝（新错误码 `RustDeskBuildNotTrusted`）。
3. 安装 / 升级完成后新增校验 `EnsureCustomClientAsync`：`Program Files\RustDesk\custom.txt` 存在、用公司公钥验签通过、JSON 里 `override-settings.enable-check-update == "N"` 且 `allow-auto-update == "N"`。不满足 → `RustDeskCustomClientMissing`，按 fail-closed 处理。
4. 安装 / 对账都显式执行 `--option allow-auto-update N` 并读回（对官方 1.4.9 也有效，作为 custom.txt 之外的第二道保险）。**不要**把它加进 `ManagedOptions`，否则老设备会被判成未接管。

### 5.2 新增 `upgrade` 阶段（helper `Program.cs` + `WindowsRemoteMaintenanceInstaller.cs`）

前置：journal 为 `Committed`，中心 manifest 的版本 ≠ journal 的 `ClientVersion`，且目标在白名单内。流程：
1. 用户态：下载并校验新包到 `%LOCALAPPDATA%\HBPOS\RemoteMaintenance\<op>\`，写 journal `PendingUpgradeArtifactPath` + 目标版本。
2. 代理（SYSTEM）：
   - 读接管标记、确认 `ManagedOptions` 齐全（证明是我们的 RustDesk）；
   - **等无远程会话**：没有现成 CLI 查连接数，用两层判断——用户态调度只在「营业时段外 + 收银空闲」发起（7.3）；代理端再看 `rustdesk.exe` 是否有 `--server` 之外的子进程（远程会话会拉起 `rustdesk.exe --cm` / 会话进程），有则本轮放弃，下个窗口再试；
   - 复制到受保护目录 → 哈希校验 → `--silent-install` → `WaitForDetachedRustDeskSetupAsync` → 确认服务存在并 `start`；
   - 升级脚本会先卸载旧版：**逐项读回** `custom-rendezvous-server` / `relay-server` / `key` / `ManagedOptions` / `allow-auto-update`，任何一项不对就用 `ConfigureRustDeskAsync(password: null)` 修回（服务器走对账模式）；接管标记重写；`EnsureCustomClientAsync`；
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
- `upgrade` 新增 `RemoteMaintenanceUpgradeScheduler`，**两个条件同时满足**才发起：
  1. **营业时段外**：按分店时区（见 `store-timezone-policy` 记忆：昆士兰 7 店 UTC+10，其余按分店配置，未配置按地址推断）取本地时间，不在该店营业时段内。营业时段来自后台分店资料的营业时间；没配的店按 **07:00–22:00** 当营业时段。
  2. **收银空闲**：`ICashierSessionContext` 无进行中交易、无未完成的支付 / 退货 / 日结，且最近 **10 分钟**无任何收银操作。
- 兜底：WPF 刚启动且满足条件 1，也算一个窗口（门店早上开机通常在营业前）。
- 窗口内每 5 分钟检查一次条件 2；窗口外每 30 分钟检查一次条件 1。失败退避 1h / 4h / 24h，连续 3 次失败停止自动重试（5.2）。
- 升级本身预计 1～2 分钟（下载在用户态提前完成，代理只做安装），即使收银员在窗口内回到收银台，RustDesk 升级也不影响收银操作，只影响远程连接。

## 8. 发布顺序

| 批次 | 内容 | 依赖 | 需要 |
|---|---|---|---|
| 0 | 私有 fork 仓库、密钥对、签名脚本、CI 跑通 1.4.9-hb1（不签名） | — | 无生产变更 |
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
- 已知限制：`allow-auto-update` 只在安装与「中心服务器有变化」的对账时写入。服务器没变化的老设备要等下次换服务器或手动修复才会写；在此之前靠 RustDesk 默认值（关）与横幅关闭兜底。

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
- custom.txt：删掉 `Program Files\RustDesk\custom.txt` 即恢复官方行为（需管理员）；helper 的 `EnsureCustomClientAsync` 只对白名单里 `RequiresCustomTxt=true` 的版本生效。

## 12. 跟进上游版本的固定流程（1.5.0 及以后）

1. 读目标 tag 的 `libs/portable/src/main.rs`、`src/platform/windows.rs`（`install_me` / `update_me` / `--silent-install`）、`src/core_main.rs`（`--password` / `--config` / `--option` / `--get-id` 分支）、`src/ipc.rs`（CLI 超时与回退逻辑），逐条对照第 10 节第 4 项的三处依赖，结论写进本文档附录。
2. 同时核对 `custom.txt` 机制未变：`read_custom_client()` 公钥常量位置、`override-settings` 语义、`KEYS_SETTINGS` 里 `allow-auto-update` / `enable-check-update` 仍存在。
3. 新建 `hb/<ver>` 分支 cherry-pick 公钥与 CI 两个提交，CI 出包，记录哈希。
4. 1042：官方 1.4.9 → 新版、hb1 → 新版两条升级路径 + 对账 + 无人值守连接，全部通过后才把新版加进 helper 白名单并发 WPF 版。
5. 中心 manifest 最后切，且先只对 1042 生效（批 4 的门店灰度机制）。
