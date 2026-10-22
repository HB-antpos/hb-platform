# .vip RustDesk 维护

> **两台主机的状态不同，别混用本目录的 Compose：**
> - 新加坡生产（`hotbargain.vip`，`/www/rustdesk-server`）正在运行的是历史部署，且 hbbs 是自编译补丁镜像（见下“迁移”一节），**不要**在那台机器上直接 `up` 本目录的 `compose.yml`，否则 `-r` 会被改成 `rd.hotbargain.vip:21117`，而 DNS 与 AWS 新机此时可能还没就绪。
> - 本目录的 `compose.yml` 现在描述的是**目标部署**：只用于 AWS 悉尼边缘机（官方镜像，`-r rd.hotbargain.vip:21117`）。

## 当前部署（新加坡，迁移完成前）

- Compose 项目：`rustdesk-server`，服务目录：`/www/rustdesk-server`。
- `rustdesk-server` 运行 `hbbs`，`rustdesk-relay` 运行 `hbbr`。
- 官方镜像固定 `1.1.16` 及 `compose.yml` 中的摘要。
- 进程工作目录为 `/root`，宿主机 `/www/rustdesk-server/data` 必须挂载到该目录。
- TCP：21115、21116、21117；UDP：21116。Web 客户端端口不发布。
- 客户端固定 `1.4.9`，文件 `rustdesk-1.4.9-x86_64.exe`，大小 24472432 字节，SHA-256：`eaedeb0088e687bf46f7c46a9c6ea5493ce51f3134dfd6acbedb47b5b9136274`。

## 迁移到专用域名 `rd.hotbargain.vip`（AWS 悉尼边缘机）

目标：RustDesk 不再占用业务主域名 `hotbargain.vip`，改由 `rd.hotbargain.vip` 解析到 AWS 悉尼 `hb-edge-syd`（EIP `13.211.103.232`），hbbs/hbbr 的 21115-21117 直接绑定公网，**不经任何反代**，保住真实源 IP 与 UDP 打洞；WebSocket 入口（控制端用）另走 nginx 443，见下文。主域名之后可自由切到边缘或 EdgeOne。

**与迁移前的差异**

- 新机使用本目录 `compose.yml` 的**官方镜像**（固定摘要）。迁移前新加坡生产跑的是自编译补丁镜像 `hb-rustdesk-server:1.1.16-wss-registration-20260908`（只为 WSS 注册兼容）。**WSS 不能省**：见下文「控制端必须有 WSS 入口」——边缘机用 `compose.wss.yml` + nginx 443 提供 `/ws/id`、`/ws/relay`（先前「现网 WSS 只有管理员出口 IP 在用 → 不需要」的判断是错的，那正是管理员控制端在用）。迁移前新加坡的自编译补丁镜像仍只为 WSS **注册**兼容，设备端（被控）不走 WSS，边缘机官方镜像即可。
- `hbbs` 的 `-r` 改为 `rd.hotbargain.vip:21117`。
- **必须沿用同一对密钥和同一个 `db_v2.sqlite3`**：设备配置里的 `key=` 与 RustDesk ID 都不用变，WPF 对账也要求公钥不变。

**服务端准备（不影响现网）**

1. 新机安全组放行 TCP 21115-21117、UDP 21116（SSH 仍只对管理员 IP 开放）。
2. 在新加坡对 `/www/rustdesk-server/data/db_v2.sqlite3` 用 `sqlite3 … ".backup"` 取一致性快照（库为 WAL 模式，不要直接拷贝主文件），连同 `id_ed25519`、`id_ed25519.pub` 传到新机同路径；私钥不进 Git、不进日志，传输后核对 `.pub` 内容一致。
3. **先**在 DNSPod 添加 `rd` 的 A 记录到 `13.211.103.232`（专业版 TTL 可设 60，不走代理），并确认解析生效（`dig +short rd.hotbargain.vip`）。**必须先于启动 hbbs**：hbbs 启动时会解析 `-r` 里的主机名，解析不到就**静默丢弃**，日志只有 `relay-servers=[]`，没有任何报错，客户端拿不到中继地址（新加坡生产的日志是 `relay-servers=["hotbargain.vip:21117"]`）。
4. 新机 `docker compose -f compose.yml up -d`，**检查 `docker logs rustdesk-server` 里有 `relay-servers=["rd.hotbargain.vip:21117"]`**；若是 `[]`，说明启动时解析失败，确认 DNS 后重启 `rustdesk-server` 容器。同时核对日志里的 `Key:` 与 `id_ed25519.pub` 内容一致。
5. 跑下面的协议级端到端检查，10 项全部通过才算服务端就绪。
6. 新机做 EBS 快照并安排周期快照：RustDesk 有密钥与注册库，不再是可随时重建的无状态节点。

**控制端必须有 WSS 入口（`/ws/id`、`/ws/relay`）**

RustDesk 1.4.x 控制端在配置了 key 时，对 hbbs/hbbr 的**原生 TCP** 连接会先做密钥交换（`secure_tcp`），而 hbbs/hbbr 1.1.x 连上后不会先发数据，客户端只能等到超时，报 `Failed to secure tcp: deadline has elapsed`。WebSocket 路径会跳过这一步，所以管理员的控制端（Mac / Windows，1.4.9）一直是走 `wss://<host>/ws/id`、`/ws/relay`（443）才连得上；Mac 日志里自 9 月 28 日起没有一次原生 TCP 成功。设备端（被控）不需要 WSS。边缘机缺这个入口时，打开「使用 WebSocket」会报 `Connection refused (os error 61)`，关掉则报 `secure tcp` 超时。

部署（已在 `hb-edge-syd` 上线，文件在本目录）：

1. `compose.wss.yml`：与 `compose.yml` 合并运行 `docker compose -f compose.yml -f compose.wss.yml up -d`，hbbs 的 21118、hbbr 的 21119 **只绑 `127.0.0.1`**，不对公网发布。重建约 10 秒，设备自动重新注册；重建后核对 `Key:` 不变、`relay-servers=["rd.hotbargain.vip:21117"]`，先用 `sqlite3 … ".backup"` 取库快照。
2. `edge-nginx/rd-acme.conf`：80 端口 `rd.hotbargain.vip` 站点，只放行 `/.well-known/acme-challenge/`（默认站点 `hb-edge.conf` 仍只允许管理员 IP，不改）。
3. 证书：`certbot certonly --webroot -w /var/www/acme -d rd.hotbargain.vip --register-unsafely-without-email`，并放 `/etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh`（`systemctl reload nginx`）；`certbot renew --dry-run` 验证。
4. `edge-nginx/rd-wss.conf`：443 终止 TLS，`= /ws/id` → `127.0.0.1:21118`，`= /ws/relay` → `127.0.0.1:21119`（超时 3600 秒，空闲的远程桌面不会被 nginx 切断），其余一律 404。
5. 安全组放行 TCP 443（和 80，证书续期用）。
6. 验证：`curl -i --http1.1 https://rd.hotbargain.vip/ws/id -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ=='` 应返回 `101 Switching Protocols`（`/ws/relay` 同理）。普通 GET `/ws/id` 返回 502 属正常（上游只接受 WebSocket）。
7. 控制端使用：ID/中继服务器填 `rd.hotbargain.vip`（含端口 21116/21117），**打开「使用 WebSocket」**，Key 为 `id_ed25519.pub` 的内容。`e2e_check.py` 测的是原生 TCP 协议路径，不覆盖 WSS。

**协议级端到端检查（`e2e_check.py`）**

不改任何真实客户端配置，直接按官方协议对服务器做检查：库副本是否生效、UDP 注册、打洞时下发的设备公钥是否带有效的服务器签名（用设备配置里的公钥验签，证明私钥迁移正确）、下发的中继地址、hbbr 中继配对与转发。

```bash
python3 scripts/ops/rustdesk/e2e_check.py --host rd.hotbargain.vip \
  --key-file <id_ed25519.pub 的本地副本> --real-id <库里已有的真实设备 ID>
```

- `--key` / `--key-file` 只用**公钥**，不要传私钥。`--real-id` 取库里确实存在的设备 ID（例如 `rustdesk --get-id` 得到的运维机 ID），用来验证库副本。
- 检查会在服务器库里写入一条 id 以 `e2e` 开头的合成 peer，它不会成为公司设备，但请在结束后清理（脚本末尾会打印命令）：`sudo sqlite3 /www/rustdesk-server/data/db_v2.sqlite3 "delete from peer where id like 'e2e%';"`，再重启 `rustdesk-server` 容器清掉内存里的合成 peer，并再次确认 `relay-servers` 日志。
- 脚本的纯函数部分（protobuf 编码、帧格式、ed25519 验签，含 RFC 8032 官方测试向量）有单元测试 `test_e2e_check.py`，随 PR CI 运行。

**切换与回滚**

- 切换只改主 API 的 `.env`：`REMOTE_MAINTENANCE_ID_SERVER=rd.hotbargain.vip:21116`、`REMOTE_MAINTENANCE_RELAY_SERVER=rd.hotbargain.vip:21117`，再按仅后端流程重建。**必须先发布带对账的 WPF 新版并确认新旧两台服务器同时在线**；旧版 WPF 不会对账，新装也会因白名单只认旧域名而失败。**没有按设备灰度的开关**：灰度靠 WPF 更新策略范围（先只发测试店 1042，在它身上翻开关，其余设备仍是旧版 WPF，不受影响），切换前先用一台真机在不改中心配置的情况下手工验证新服务器可达。
- 已升级的设备下次启动自动对账（见 `docs/remote-maintenance-contract.md`）。过渡期设备分居两台服务器，控制端一次只能连其中一个，窗口越短越好。
- 回滚：把 `.env` 改回 `hotbargain.vip:21116/21117` 重建主 API（**不能靠 `REMOTE_MAINTENANCE_ENABLED=false`**：关闭后 `prepare` 直接 DISABLED，设备不会改回），已对账设备下次收银员登录后自动改回；回退 WPF 版本前必须先改回 `.env` 并等设备收敛；新加坡容器与数据在全部设备收敛并观察 1–2 天之前不要停、不要删。
- 全部收敛后再下线新加坡 hbbs/hbbr，并撤掉主站 nginx 的 `include nginx-wss.conf`（生产变更，单独授权）。**撤之前先确认所有控制端都已改用 rd 并打开「使用 WebSocket」**。
- 重新生成免配置下载包：`prepare-client.py --server-host rd.hotbargain.vip …`，替换 artifacts 目录与 manifest。

## 免配置客户端

`prepare-client.py` 验证官方原版 SHA-256 后，按 RustDesk 1.4.9 的 `custom_server.rs` 文件名协议封装服务器、relay 和公钥。源文件为原版 Windows x64 EXE，`--public-key` 必须指向现有 `id_ed25519.pub`，禁止使用私钥文件。`--output` 使用新的精确输出目录；已有同名客户端不会被覆盖。

生成的 EXE 保留官方二进制内容及签名，配置仅存于文件名。下载后必须保留完整文件名；改名会丢失该预配置入口。密码与 monitor token 不参与封装。这个下载客户端本身不提供设备状态上报，状态上报由 POS 安装流程部署的独立 Windows 服务完成。

服务端已生成并核对 `/www/HBWeb/remote-maintenance/artifacts/rustdesk-manifest.json` 及其对应 EXE。已验证解码后的服务器、公钥匹配当前运行服务，文件名长度 168，复制后的 SHA-256 与原版一致。实际 Windows 启动配置仍需试点验证。

官方协议依据：<https://github.com/rustdesk/rustdesk/blob/1.4.9/src/custom_server.rs>。

## 本次切换证据

2026-09-06（Brisbane）从旧 `hbbs 1.1.15` 完成切换。已发现并修正旧 Compose 挂载 `/data` 而实际数据库和密钥位于容器 `/root` 的问题。

受保护备份：`/www/backups/rustdesk-migration-20260905T214252Z`（名称采用 UTC）。目录包含原配置、原镜像标识、最终 SQLite 快照和服务器身份；不得上传到 Git 或输出文件内容。

- `sha256-stable.json` 校验可恢复的持久文件，不包含 SQLite 临时 WAL/SHM。
- `hbbs-final` 为停止旧进程后取得并通过 integrity_check/checkpoint 的快照。
- `SERVER_CUTOVER_OK` 标记新进程 TCP/UDP 监听及公钥一致性已验证。
- `addressbook-final` 保存旧通讯录最终数据；`ADDRESSBOOK_RETIRED_OK` 标记两个旧容器已移除。
- 旧通讯录配置保留为 `/www/rustdesk-addressbook/docker-compose.retired.yml`，不会被默认 Compose 自动启动。
- 本次已从外网验证 UDP 21116 RegisterPeer 响应、TCP 21115 NAT 响应，以及 TCP 21116/21117 可达。21118/21119/8088/8089 不再提供对应公网服务。
- 主 API、POS API 和旧业务容器在 RustDesk 切换期间 ID 未改变；主站、主 API、POS API 健康端点返回 200。

以上网络检查不能替代两台 Windows 设备的实际直连、中继与无人值守验收。

## 切换脚本边界

`cutover.py` 只用于这次已有服务的数据挂载修复：要求目标数据目录为空，备份完整且校验通过，当前容器 ID 与备份一致。不能作为重复执行的通用升级脚本。

脚本失败时保留失败数据，并从快照重建原版本。执行前仍需核对目标、备份、Compose 和预期影响范围。正常升级应先制作新的可恢复备份，再使用已验证镜像摘要逐服务更新。

## 回滚

1. 核对备份的 `containers.json` 与 `rollback-compose.json`，确认目标仅包含 RustDesk。保留当前数据及镜像。
2. 停止 `rustdesk-server`、`rustdesk-relay`，将当前数据另存到新的受保护目录。
3. 从 `hbbs-final` 恢复已验证的密钥和 SQLite 快照到持久目录，使用 `rollback-compose.json` 中记录的旧镜像启动 `rustdesk-server`。
4. 如需恢复旧通讯录，使用其原目录内的 `docker-compose.retired.yml` 指定服务启动；保留原数据，不从其他目录误解析相对卷路径。
5. 复核 RustDesk 公钥、进程、监听及业务健康。UFW 新增规则记录在本次变更中，只能按精确规则撤销，不能重置整个防火墙。

HBWeb/POS API/WPF 的应用发布与 RustDesk 容器切换分别验证和回滚；正常应用回退保留新增维护数据表和 DataProtection 密钥。

应用发布前已建立独立备份 `/www/backups/remote-maintenance-apps-20260905T220712Z`，包含主 API/POS 源码与 `.env`、Web 静态站点（含 `.user.ini`）、容器配置和回滚镜像标签。归档可读性及 SHA-256 已验证。该备份建立本身不代表应用已发布；应用切换须另行记录验证证据。
