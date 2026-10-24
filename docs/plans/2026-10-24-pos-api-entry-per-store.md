# 收银端三条入口（EdgeOne / AWS 悉尼 / 直连）+ 后台按分店设置首选 方案

日期：2026-10-24 · 状态：**已实现，待提交 / 部署**（第 3 版：按已确认的决定落地，见第 11 节）

## 1. 目标与边界

- 收银端（WPF、手持、iPad）访问 POS API 有**三条入口**，都回源到同一个新加坡后端：

  | 代号 | 域名 | 线路 | 特点 |
  |---|---|---|---|
  | `edge` | `pos.hotbargain.vip` | 腾讯 EdgeOne → 新加坡 | 多节点；按请求量计费；空闲后回源连接变冷 |
  | `aws` | `pos-syd.hotbargain.vip`（新） | AWS 悉尼边缘机 `hb-edge-syd` → 新加坡 | 有定时预热、冷连接约 0.31 秒；与腾讯完全独立的网络；单机 |
  | `direct` | `hotbargain.vip` | 直连新加坡源站 | 无中间层；个别运营商到新加坡丢包或不通 |

- 后台可以按**分店**选**首选入口**（三选一）。其余两条自动作为备用，**任何模式都保留自动故障转移**。
- 设备身份不变：本地库分区、激活 / 恢复数据、开通码分区仍按规范入口 `hotbargain.vip` 计算。三个域名都登记为同一别名组，切换入口只换传输层最后一步的 host，**不触发换服务器流程**。
- 入口名单**写死在客户端**（凭据只会发往编译时确定的域名），后台只下发「选哪个」。所以加第三条线**必须发版**。
- 员工移动端（`apps/mobile`）本期不动。

## 2. 现状（已核实）

| 项 | 现状 |
|---|---|
| WPF 入口组 | `ApiEndpointAliases.GroupHosts = ["pos.hotbargain.vip", "hotbargain.vip"]`；`ApiTransportSelector` 是两候选状态机（`ActiveIndex` 0/1），冷却 2/4/8/16/30 分钟 |
| 手持 / iPad 入口组 | `packages/pos-api-client/src/transport-failover.ts` 的 `TRANSPORT_GROUP` 同上，`reportFailure` 写死只认 `EDGE_API_HOST` 失败；状态只存内存 |
| 三端都会定期调的接口 | 只有 `GET api/v1/stores/current/receipt-profile/sync`（约 60 秒一次） |
| 源站识别 EdgeOne | 新加坡 nginx `0.eo-client-ip.conf`：EdgeOne 回源带共享密钥头 `X-EO-Origin-Auth`，密钥对才采信 `EO-Client-IP` |
| AWS 边缘机 `hb-edge-syd`（10-24 只读核对） | t3.small、2 核、内存可用约 1.37 GB、磁盘余 16 GB、负载 0；固定 IP 13.211.103.232；nginx 已有 `upstream sg_origin`（直连 43.160.208.238:443，keepalive 64 / 50s，SNI 与 Host = hotbargain.vip）与 `hb-edge-warm.timer`（每 25 秒预热）；默认站点只在 80 端口、只允许管理员 IP；443 只有 `rd.hotbargain.vip`（RustDesk WSS）；证书只有 `rd.hotbargain.vip`；RustDesk hbbs/hbbr 两个容器运行中 |
| 10-21 实测（Mac，澳洲） | health 冷连接：直连 0.79~0.92 秒、AWS 边缘 0.31 秒、EdgeOne 冷 0.72 秒 / 持续流量约 20 秒后 0.24 秒；稳态读库三者基本持平（悉尼→新加坡本身约 245 ms） |

## 3. 服务器端：开通 AWS 第三条线路（不需要发版，可先上线、先验证）

### 3.1 AWS 边缘机 `hb-edge-syd`

1. DNSPod 加 `pos-syd.hotbargain.vip` A → 13.211.103.232，TTL 60。
2. Let's Encrypt 证书（复用 `rd-acme.conf` 的 HTTP-01 webroot 做法；续期钩子 reload nginx）。WPF 诊断规则本来就认 Let's Encrypt。
3. 新建 `/etc/nginx/conf.d/pos-syd.conf`（443，按 `server_name` 精确匹配，不碰 `hb-edge.conf` 与 `rd-*.conf`）：
   - 只放行 `location ^~ /pos-api/` → `proxy_pass https://sg_origin`，其余路径 404；
   - `proxy_set_header Host hotbargain.vip`、`proxy_ssl_server_name on`、`proxy_ssl_name hotbargain.vip`、校验源站证书；
   - `proxy_http_version 1.1` + 清空 `Connection`（复用长连接池）；
   - `proxy_read_timeout 600s` / `proxy_send_timeout 600s`（目录全量同步、长报表）；`client_max_body_size` 与源站一致；
   - 回源带 `X-HB-Edge-Auth: <48 位 hex 密钥>` 与 `X-HB-Edge-Client-IP: $remote_addr`，并**清掉**客户端自带的同名头；
   - 访问日志带 `uct/uht/urt` 计时（沿用现有 `log_format`）。
4. 预热：现有 `hb-edge-warm.timer` 已经让 `sg_origin` 长连接保持热，无需改。
5. 安全组 443 本来就对全网开放；SSH 仍只允许管理员 IP。

### 3.2 新加坡源站

- `0.eo-client-ip.conf` 增加第二个可信来源：`X-HB-Edge-Auth` 密钥正确 **且** `$remote_addr = 13.211.103.232` → 采信 `X-HB-Edge-Client-IP` 为真实客户端 IP。
- 同一处 map 计算 **`$hb_entry`**（`edge` / `aws` / `direct`），以 `X-HB-Entry` 头**覆盖**传给 POS API（客户端自带的同名头无效）。POS API 只认这个头判断「实际入口」。
- 核对新加坡主机防火墙 / fail2ban 白名单 13.211.103.232：否则一次误封会让所有走 AWS 的店同时掉线（会自动回退，但失去这条线）。
- 站点文件由宝塔管理，面板改站点设置可能重写；回滚 = 恢复备份文件 + `nginx -t && nginx -s reload`。

### 3.3 上线前验证（客户端还没认这个域名，不影响任何门店）

- `curl https://pos-syd.hotbargain.vip/pos-api/api/v1/health` 冷 / 热计时；与 EdgeOne、直连同一时段对比。
- 源站 `pos-real-ip.log` 中该请求记录为真实 IP、`trusted=1`、`entry=aws`；伪造密钥 / 伪造 `X-HB-Entry` 均被忽略。
- 用 WPF「测试连接」（**只点测试，别保存**）从门店机测一次。

## 4. 数据与下发

### 4.1 数据

`dbo.Store` 加列（主 API 正式迁移 `--schema=migrate`，**先于代码上线**）：

| 列 | 类型 | 含义 |
|---|---|---|
| `PosApiEntryMode` | `nvarchar(16) NULL` | `NULL` = 默认（EdgeOne 首选）；`edge` / `aws` / `direct` |
| `PosApiEntryUpdatedAt` | `datetime2 NULL` | 最近修改时间（UTC） |
| `PosApiEntryUpdatedBy` | `nvarchar(64) NULL` | 最近修改人 |

`dbo.POSM_设备注册信息表` 加 `LastApiEntry`（`edge` / `aws` / `direct`）、`LastApiEntryAt`（`DeviceRuntimeStatusSchemaInitializer` 启动补列）。

### 4.2 下发：搭 `receipt-profile/sync` 的车

- `StoreReceiptProfileSyncDto` 末尾加可选字段 `ApiEntryMode`（带默认值，旧客户端忽略）。
- 每次直接读 `dbo.Store` 现值，**与小票发布版本无关**，`Changed=false` 也照样带上 → 改设置不用发布小票，约 1 分钟生效，**请求量零增加**。

### 4.3 回显实际入口

- POS API 在 `receipt-profile/sync`（三端）与 `runtime-status`（WPF）里读 `X-HB-Entry` 写 `LastApiEntry`。节流：入口变化或距上次超过 10 分钟才写库。
- 只用于后台展示，不参与鉴权。

## 5. 客户端

### 5.1 入口顺序规则（三端一致，写成纯函数并单测）

- 候选顺序 = **首选** + 其余按固定优先级 **`aws` → `edge` → `direct`** 排列：

  | 首选 | 顺序 | 理由 |
  |---|---|---|
  | `edge`（默认） | edge → aws → direct | EdgeOne 出问题先换另一条也绕开坏线路的 AWS |
  | `aws` | aws → edge → direct | 同上 |
  | `direct` | direct → aws → edge | 直连坏了先走 AWS（基本不花钱），最后才 EdgeOne（按请求计费） |

- 未知值 / 字段缺失 → 保持当前设置；从没收到过 → 按 `edge`（= 现在的行为）。

### 5.2 选择器从「两候选」改成「多候选」

- 每个入口单独记冷却与退避（2/4/8/16/30 分钟）。请求取「顺序中第一个不在冷却的入口」；都在冷却就取冷却最早结束的。
- 首选冷却结束后，**读请求和健康探测**去试探（写请求不探路，沿用 #924/#926 的约定）；成功即恢复并清零退避。
- 并发请求迟到的失败不重复升级退避（沿用现有约定）。
- GET/HEAD 连接阶段失败**只换下一条重试一次**（WPF 建连超时 4 秒，避免一次请求拖到 12 秒）；POST/PUT 绝不重试。
- 收到新的首选 → 清零各入口冷却，下一个读请求走新首选。值持久化到本地，冷启动先用本地记住的值。

### 5.3 WPF（`apps/pos-wpf/src/Hbpos.Client.Wpf`，需建版 1.0.89）

- `ApiEndpointAliases.GroupHosts` 加 `pos-syd.hotbargain.vip`；`Canonicalize` / `SameIdentity` 自动覆盖新别名（身份仍是 `hotbargain.vip`）。
- `GetCandidates(configured, preferredMode)` 按 5.1 排序；`ApiTransportSelector` 改为按入口记状态，新增 `SetPreferredMode`。
- `ReceiptProfileSyncService` 解析 `ApiEntryMode` → 选择器 + 本地库。
- `ApiRuntimeEndpointHandler` 的 520~530 判断只对 EdgeOne 入口生效（AWS 的 nginx 回源失败返回 502/504，与源站可能返回的码重叠，不当作「边缘故障」，只靠连接失败判定）。
- 网络诊断：三条入口各测一遍。
- 测试：选择器状态机（三入口排序、冷却、探路、并发失败）、别名身份、sync 解析；改动的 6 个直接 new 处理器的测试文件保持兼容。

### 5.4 手持 / iPad（`packages/pos-api-client` + 两个 App）

- `TRANSPORT_GROUP` 加 `pos-syd.hotbargain.vip`，`parseEntryUrl` / `entryOrigins` 随之覆盖（origin 守卫放宽到三个）。
- `ApiTransportSelector` 同 5.2 改为多候选；`setPreferredMode`；`createFailoverFetch` 按顺序。
- 两个 App 的 receipt-profile 同步处理新字段，持久化到 AsyncStorage。
- iPad #969 网络检测页加第三条线，并在顶部显示后台设置的首选。
- **纯 JS**：手持 OTA；iPad 随下一个 TestFlight 包（iPad OTA 投放未开）。原生层无域名限制（#924 已核实）。

## 6. 后台（Web + 主 API）

- 位置：**设备管理页**右上角视图切换加「收银入口」。
- 主 API `Controllers/React/PosApiEntryController.cs`：
  - `GET /api/react/v1/pos-api-entry/stores`：每店设置 + 设备实际入口统计 + 版本过低台数；`DeviceRegistration.View`；
  - `PUT /api/react/v1/pos-api-entry/stores`：`{ storeGuids, mode: "edge" | "aws" | "direct" }` 批量；`DeviceRegistration.Manage`；写操作日志。
- 设备列表加「入口」列与「入口」筛选（EdgeOne / AWS 悉尼 / 直连 / 回退中）。
- 「版本过低」：WPF < 1.0.89、手持 / iPad 未带新 JS → 不认设置，**仍按旧默认走 EdgeOne**，且不会用 AWS 线路；页面单列台数。

## 7. 上线顺序

1. **服务器端 AWS 线路**（第 3 节）：DNS → 证书 → 边缘机 nginx → 源站信任与 `X-HB-Entry` → curl 与门店机「测试连接」验证。此时没有任何客户端会用它。
2. 迁移 `dbo.Store` 与设备表列。
3. 部署主 API + POS API + Web。全部分店默认 EdgeOne 首选，行为与现在相同；后台已能看到每台设备实际走 EdgeOne 还是直连。
4. 客户端：WPF 1.0.89 先只发 1042 → 后台把 1042 依次切 `aws` / `direct` / `edge`，每次 1 分钟内看实际入口跟随；手持 OTA、iPad TestFlight 同样先测试店。
5. 分批放量；对线路有问题的分店改首选。

回滚：后台改回「EdgeOne 首选」；AWS 线路出问题时客户端自动回退，也可以直接把 `pos-syd` 的 DNS 指回新加坡源站（须先给源站证书加 `pos-syd` SAN，否则只能靠客户端回退）。

## 8. 费用与容量

- AWS：t3.small 已在付费；出站流量 $0.114/GB。POS API 约 1.38 GB / 20.85 小时 → 全部走 AWS 约 50 GB/月 ≈ **$6/月**；源站回给边缘机属于入站，不收费。
- EdgeOne：基础版 ¥399/月，额度约 1,790 万次请求；全部走 EdgeOne 约 1,000 万次/月。把部分分店改为 AWS 首选可明显降低 EdgeOne 用量。
- 边缘机负载：33 台平均每秒两三个请求，TLS 与反代开销很小；t3.small 是突发型实例（基线 CPU 20%），上线后看 `CPUCreditBalance`。

## 9. 风险

- **与 RustDesk 共用一台机器**：机器宕机或维护时 POS 这条线和远程桌面同时受影响。POS 会自动回退不断线；远程桌面没有回退。需要隔离时另开一台 t3.micro（约 $10/月）专做 POS 边缘，客户端不用改（只改 DNS）。
- **单机单可用区**：只能作为三条之一，不能当唯一入口；本设计每种模式都有两条备用。
- **真实 IP**：3.2 不做的话，所有走 AWS 的店在源站看来是同一个 IP，激活限流会互相连累 —— 3.2 是上线前提。
- **生效延迟**：约 60 秒；夜间静默窗口 WPF 最长约 20 分钟。

## 10. 待确认（已全部确认，见第 11 节）

1. 新域名用 `pos-syd.hotbargain.vip` 可以吗？
2. 备用顺序按 5.1 的固定规则（`aws` → `edge` → `direct`），还是允许后台为每店单独排顺序？（建议固定，后台只选首选。）
3. 修改权限用 `DeviceRegistration.Manage` 还是 `Stores.Edit`？（建议前者。）
4. 本期是否需要按单台设备覆盖？（建议不做。）
5. AWS 线路是否要独立机器？（建议先共用，看 CPU 积分与稳定性再定。）

## 11. 落地结果（2026-10-24）

**已确认的决定**：域名 `pos-syd.hotbargain.vip`；备用顺序固定 aws → edge → direct（首选 edge 时备用 aws、直连最后兜底）；权限 `DeviceRegistration.View/Manage`；不做单台覆盖；AWS 线路先与 RustDesk 共用边缘机。另：**本机记住首选入口统一用 AsyncStorage**（用户选择，接受手持重建 APK）。

**服务器端（已上线，客户端尚未使用）**：DNS `pos-syd`（RecordId 2435906501）、边缘机 `pos-syd.conf` + Let's Encrypt（到 2027-01-21）、新加坡源站 `0.eo-client-ip.conf` 增加 AWS 可信来源与 `$hb_entry`、`/pos-api/` 加 `X-HB-Entry`。回滚办法与备份名见记忆 `aws-pos-syd-third-entry-server-side-20261024`。实测稳态：AWS 0.203s < EdgeOne 0.237s < 直连 0.269s。

**与方案的差异**
- 入口新鲜度窗口 15 分钟（POS API 每 10 分钟才写一次，取 10 分钟会闪成「未知」）。
- 版本过低的 WPF 只计入 `unsupportedCount`，不计入 `fallbackCount` / `byEntry.edge|aws|direct`（并入 `unknown`，四项之和 = 在线数）。
- WPF 网络诊断页未扩成三条（该页只测用户输入的单个地址）；设置页只读展示「首选 / 实际 / 是否回退」。
- 手持 / iPad 本机存储键统一为 `hbpos.api-entry-mode.v1`（AsyncStorage），共享包 `api-entry-mode-coordinator.ts` 承载，两个 App 各一份薄封装；读写失败静默。
- **手持原生版本与 runtimeVersion 0.1.2 → 0.1.3**（build 11 已在 0.1.2 发布且无 AsyncStorage，同 runtime 的 OTA 会让它崩溃）；iPad 保持 0.2.0（OTA 投放全关，只加 build 号）。
- `scripts/pos-shared/pos-shared-migration-state.json` 登记新增的 3 个第三方包（工作区检查规定的入口）。

**发布要求**
- 需要重建原生包：手持 APK（0.1.3）与 iPad TestFlight（0.2.0 新 build）。PR 标题须写明。
- WPF 需建 1.0.89（版本号由建版脚本传入）。
- 部署顺序：迁移（主库 `20261024.003`、POSM `20261024.004`，**先于代码**）→ POS API + 主 API → Web → 客户端灰度（先 1042）。
- 新原生包要从「同批 OTA 的 JS 基线 + 升版本提交」构建，不能直接从 main（见 mobile-native-version-bump-checklist）。
