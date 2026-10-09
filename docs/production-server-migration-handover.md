# hotbargain.vip 生产服务器迁移交接文档

## 1. 文档目的

本文用于迁移 `hotbargain.vip` 当前生产服务器，覆盖：

- Web 前端静态站点
- 主后端 `hb-platform-vite-api`（端口 `5002`）
- 收银后端 `hbpos-api`（端口 `5003`）
- Nginx、TLS、Docker network、运行日志
- 数据库连接、第三方服务密钥和 DataProtection key ring
- 上线验证、考勤二维码验收与回滚

本文只记录路径和变量名，不保存任何密钥、密码、token 或数据库连接串值。

## 2. 当前生产拓扑

| 组件 | 宿主机位置 | 运行单元 | 内部端口 | 公网入口 |
| --- | --- | --- | --- | --- |
| Web 前端 | `/www/wwwroot/www.hotbargain.vip` | Nginx 静态文件 | 无 | `https://hotbargain.vip/` |
| 主后端 | `/www/HBWeb/BackEnd/master-Vite` | `hb-platform-vite-api` | `5002` | `/api/` |
| 收银后端 | `/www/HBWeb/BackEnd/hbpos-api` | `hbpos-api` | `5003` | `/pos-api/` |
| 旧前端容器 | Docker `hb-platform-frontend` | 遗留服务 | `8080` | 只保留，不作为新站主入口 |
| 旧后端 | Docker `hb-platform-api` | 遗留服务 | `5001` | 迁移时不要误当主后端 |

生产 Nginx 配置：

```text
/www/server/panel/vhost/nginx/www.hotbargain.vip.conf
```

关键代理关系：

```text
/             -> /www/wwwroot/www.hotbargain.vip
/api/         -> 127.0.0.1:5002
/pos-api/     -> 127.0.0.1:5003
```

## 3. 必须迁移的文件与目录

### 3.1 前端

```text
/www/wwwroot/www.hotbargain.vip/
/www/wwwroot/www.hotbargain.vip/.user.ini
/www/server/panel/vhost/nginx/www.hotbargain.vip.conf
```

前端静态文件也可以从固定 Git 提交重新构建，但 `.user.ini` 和生产 Nginx 配置必须单独保留。

### 3.2 主后端

```text
/www/HBWeb/BackEnd/master-Vite/.env
/www/HBWeb/BackEnd/master-Vite/docker-compose.yml
/www/HBWeb/BackEnd/master-Vite/data-protection-keys/
<ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH 指向的目录>            # 考勤二维码密钥目录，与收银后端共用，见第 4.2 节
<LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH 指向的目录>  # Linkly 终端凭据密钥目录，与收银后端共用，见第 4.3 节
```

后两个目录的宿主机路径由 `.env` 中的同名变量决定，路径在不同服务器上可以不同，但**主后端和收银后端的变量必须指向同一个目录**。

主后端源码建议从已确认的 Git commit 重新同步和构建，不要把服务器上的 `bin/`、`obj/`、`node_modules/` 当成迁移资产。

### 3.3 收银后端

```text
/www/HBWeb/BackEnd/hbpos-api/.env
/www/HBWeb/BackEnd/hbpos-api/apps/pos-wpf/docker-compose.hotbargain.yml
```

收银后端的**全局** DataProtection ring（收银员授权票据、挂单密文等）使用独立的 Docker 命名卷 `hbpos-api-data-protection-keys`，
**不再**挂载主后端的 `data-protection-keys`。收银后端与主后端只共享下面两个专用目录：

```text
<ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH 指向的目录>
<LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH 指向的目录>
```

`hbpos-api-data-protection-keys` 卷随容器重建保留；迁移服务器时需要连卷一起备份恢复，否则已保存的挂单密文无法解开。

### 3.4 TLS 证书

当前 TLS 由宿主机 Nginx 使用，证书目录位于：

```text
/www/server/panel/vhost/cert/www.hotbargain.vip/
```

迁移方式二选一：

1. 使用加密通道迁移现有 `fullchain.pem` 和对应私钥，并严格保留权限。
2. 在新服务器重新签发证书，确认成功后再切换 DNS。

TLS 私钥不得复制进 Git、Docker 镜像、前端目录或应用容器。

## 4. DataProtection key ring（三套，用途不同，丢失后果不同）

| 编号 | 用途 | 主后端挂载 | 收银后端挂载 | 是否必须同一目录 |
| --- | --- | --- | --- | --- |
| 4.1 | 主后端全局 ring（SMTP 密码密文等） | `./data-protection-keys` → `/app/App_Data/DataProtectionKeys` | 不使用 | 否，仅主后端 |
| 4.2 | 考勤二维码签名密钥 | `${ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH}` → `/app/App_Data/AttendanceQrDataProtectionKeys` | 同一变量、同一容器路径 | **是** |
| 4.3 | Linkly 终端凭据（密码、配对 secret） | `${LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH}` → `/app/App_Data/LinklyCloudCredentialDataProtectionKeys` | 同一变量、同一容器路径 | **是** |

收银后端自己的全局 ring 是独立命名卷 `hbpos-api-data-protection-keys`（见 3.3），不属于上表的共享项。

### 4.1 主后端全局 ring

```yaml
# services/backend/docker-compose.yml
volumes:
  - ./data-protection-keys:/app/App_Data/DataProtectionKeys
```

### 4.2 考勤二维码 ring（主后端 ↔ 收银后端）

两个 `.env` 写入同一个宿主机路径：

```dotenv
ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH=<同一个宿主机目录>
```

丢失或两侧目录不一致时出现：

```text
ATTENDANCE_QR_KEY_DECRYPT_FAILED
```

### 4.3 Linkly 终端凭据 ring（主后端 ↔ 收银后端）

主后端（Web 管理端）录入终端密码时加密；收银后端配对、发起交易时解密。两侧靠**目录里的同一批 key 文件**互通，所以：

```dotenv
# 主后端 .env 与收银后端 .env 必须写同一个宿主机路径
LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH=<同一个宿主机目录>
```

compose 里这个变量用 `${...:?required}` 声明，**只检查变量非空，不检查目录里有没有 key**。
迁移后如果目录是空的，API 启动时会悄悄生成一套新 key，库里已有的密文就全部解不开。症状：

- 收银后端日志出现 Error：`Linkly Cloud credential key ring mismatch ... missingKeyId=<GUID>`（`missingKeyId` 是密文引用、但目录里找不到的那把 key）；
- 门店 POS 的 Linkly 请求返回 `409 LINKLY_CLOUD_TERMINAL_CREDENTIAL_KEY_RING_MISMATCH`，提示“不要重新录入密码，检查 Admin 与 POS 的密钥目录”；
- 终端列表整体显示 `NeedsRepair`，而 Web 管理端仍显示终端“就绪”（它只看数据库列，不验证 POS 能否解密）。

**此时在后台重新录入密码没有用**：新密文仍由主后端的那套 key 加密，收银后端照样解不开。正确做法是把旧 key 文件恢复进该目录，并确认两个容器挂载的是同一个目录。

### 4.4 共同的迁移要求（4.1–4.3 都适用）

- 必须复制完整 key ring，不能只复制最新文件。
- 保留文件名、内容、时间和访问权限。
- 两个容器都必须能够读取；需要生成新 key 时还必须能够写入。
- **必须在启动 API 之前恢复**，不得在新服务器启动 API 后才补 key ring，否则容器可能先生成一套不兼容的新密钥。
- 不得删除旧 key。旧 key 仍用于解密数据库中的历史密文（SMTP 密码、考勤二维码签名密钥、Linkly 终端凭据）。
- 部署后必须做一次连接测试，见第 9.8 节。

## 5. 环境变量与敏感配置清单

`.env` 文件只放在服务器上，建议权限为 `0600`。Docker Compose 使用 `--env-file .env` 注入变量，不需要把 `.env` 挂载到容器内。

### 5.1 主后端 `.env`

数据库：

```text
CONNECTION_STRING_DEFAULT
CONNECTION_STRING_STORE_HQ
CONNECTION_STRING_SALES
CONNECTION_STRING_POSTGRES
CONNECTION_STRING_POSM
CONNECTION_STRING_SALES_RECORD
```

认证与日志：

```text
JWT_KEY
JWT_ISSUER
JWT_AUDIENCE
CENTER_LOG_HBWEB_RV_KEY_SHA256
CENTER_LOG_HBPOS_API_KEY_SHA256
```

EAS webhook：

```text
EAS_WEBHOOK_SECRET
EAS_WEBHOOK_ALLOWED_ACCOUNT_NAME
EAS_WEBHOOK_ALLOWED_PROJECT_NAME
```

共享密钥目录（必须与收银后端 `.env` 中的同名变量指向同一个目录）：

```text
ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH
LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH
```

第三方服务：

```text
DEEPSEEK_API_KEY
TENCENT_SECRET_ID
TENCENT_SECRET_KEY
TENCENT_BUCKET_NAME
TENCENT_REGION
TENCENT_IMAGE_BUCKET_NAME
TENCENT_IMAGE_REGION
```

### 5.2 收银后端 `.env`

```text
CONNECTION_STRING_DEFAULT
CONNECTION_STRING_POSM
CENTER_LOG_HBPOS_API_KEY
ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH
LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH
LINKLY_CLOUD_PRODUCTION_NOTIFICATION_BEARER
LINKLY_CLOUD_SANDBOX_NOTIFICATION_BEARER
LINKLY_CLOUD_PRODUCTION_POS_VENDOR_ID
LINKLY_CLOUD_SANDBOX_POS_VENDOR_ID
SQUARE_WEBHOOK_SIGNATURE_KEY_PRODUCTION
SQUARE_WEBHOOK_SIGNATURE_KEY_SANDBOX
```

### 5.3 前端构建密钥

`VITE_CENTER_LOG_KEY` 属于前端构建期变量。它会在构建时进入产物，不是容器运行时挂载文件。必须通过受控 CI/CD 环境注入，不要写入仓库或迁移文档。

## 6. 可以重新创建的资源

以下内容通常不需要从旧服务器逐文件复制：

- Docker 镜像：在新服务器从固定源码重新构建。
- `bin/`、`obj/`、`node_modules/`：重新生成。
- 前端 `dist/`：可以从固定 commit 重新构建。
- Docker network `hb-network`：在新服务器重新创建。
- 健康检查和普通运行缓存：容器启动后重新生成。

日志卷可以按审计要求归档，但不应作为启动新服务的前置依赖：

```text
hb-api-logs
hbpos-api-logs
```

## 7. 数据库迁移边界

先确认迁移属于哪一种：

### 7.1 只迁移应用服务器

如果 SQL Server、PostgreSQL 和对象存储仍使用原服务：

- 不执行数据库恢复。
- 保持连接串不变。
- 更新数据库防火墙、IP allowlist 或 VPN 路由，允许新服务器访问。
- 在切换 DNS 前测试所有数据库连接。

### 7.2 同时迁移数据库

数据库迁移必须有单独的 DBA 方案，至少包含：

- 一致性备份和恢复点
- 停写窗口或增量同步
- 登录、用户、权限和证书迁移
- SQL Agent/定时任务迁移
- 回滚点和数据校验

禁止只复制数据库文件后直接启动生产 API。

## 8. 新服务器准备

建议先完成：

1. 安装 Docker Engine、Docker Compose、Nginx、`curl`、`rsync`。
2. 创建与旧服务器一致的部署目录。
3. 创建 Docker network：

```bash
sudo docker network inspect hb-network >/dev/null 2>&1 \
  || sudo docker network create hb-network
```

4. 开放公网 `80/443`。
5. `5002/5003` 只供本机 Nginx 代理；迁移验证完成后通过防火墙限制公网直连。
6. 确认新服务器可以访问数据库、腾讯云、EAS、SMTP、Linkly 和 Square。
7. 迁移前 24-48 小时降低 DNS TTL。

## 9. 推荐迁移顺序

### 9.1 冻结发布源

记录：

```bash
git rev-parse HEAD
git status --short --branch
```

生产迁移必须使用明确 commit。不要从包含未提交业务修改的工作树直接迁移。

### 9.2 备份旧服务器

备份至少包含：

```text
前端 webroot 与 .user.ini
两个生产 .env
完整 DataProtection key ring：主后端全局 ring、考勤二维码 ring、Linkly 凭据 ring，以及收银后端命名卷 hbpos-api-data-protection-keys
Nginx vhost 配置
TLS 证书与私钥，或重新签发所需资料
Docker Compose 文件
必要的日志归档
```

敏感备份必须加密传输和加密保存，不得写入公开对象存储或普通聊天附件。

### 9.3 恢复目录与敏感文件

先恢复三套 DataProtection key ring 和 `.env`，再启动任何容器：

```bash
sudo install -d /www/HBWeb/BackEnd/master-Vite/data-protection-keys
# 考勤、Linkly 目录的位置以两个 .env 里的同名变量为准（两个 .env 必须一致）
sudo install -d "$ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH"
sudo install -d "$LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH"
sudo chmod 600 /www/HBWeb/BackEnd/master-Vite/.env
sudo chmod 600 /www/HBWeb/BackEnd/hbpos-api/.env
```

**恢复后、启动前**确认两个共享目录里确有从旧服务器复制来的 key 文件（`key-*.xml`），而不是空目录：

```bash
ls "$ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH"/key-*.xml
ls "$LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH"/key-*.xml
```

compose 的 `:?required` 对空目录不会报错，容器会悄悄生成一套新 key，之后所有 Linkly 终端凭据都解不开。
DataProtection 目录的 owner/mode 应从旧服务器原样保留，并验证两个容器都可读写。

### 9.4 构建、迁移和部署主后端

主后端数据库结构不再随 API 常规启动自动迁移。必须按“构建候选镜像 → 数据库备份/恢复点 → 单次迁移 → 只读检查 → 启动 API → 健康及业务验证”的顺序执行。

先构建候选镜像，但暂不启动 API：

```bash
cd /www/HBWeb/BackEnd/master-Vite
sudo docker compose --env-file .env build hb-api
```

确认 DBA 已为主库和 POSM 数据库建立可验证的备份或恢复点，并记录备份标识、完成时间与恢复负责人。未确认备份可恢复时，不得执行迁移。

性能基线 schema 要求主库已启用 `ALLOW_SNAPSHOT_ISOLATION`。应用迁移只会读取并校验该数据库级选项，不会执行 `ALTER DATABASE`。DBA 必须在维护窗口先核对目标主库，必要时对经过确认的精确数据库名执行：

```sql
SELECT [name], [snapshot_isolation_state_desc]
FROM sys.databases
WHERE [name] = N'<主库名>';

ALTER DATABASE [<主库名>] SET ALLOW_SNAPSHOT_ISOLATION ON;
```

完成后再次只读查询，确认状态为 `ON`；目标库、备份或回退方式不明确时停止，不得由应用容器代为修改。

使用同一候选镜像运行一次显式迁移。`hb-api-migrate` 位于 `schema` profile，默认 `docker compose up` 不会启动它；该任务不暴露端口，也不执行 HTTP 健康检查：

```bash
sudo docker compose --env-file .env --profile schema \
  run --rm hb-api-migrate
```

迁移命令必须以退出码 `0` 结束。失败时保留日志和数据库恢复点，不要启动新 API，也不要通过重复启动 API 尝试补做迁移。

随后使用候选镜像执行一次只读 schema 检查：

```bash
sudo docker compose --env-file .env \
  run --rm --no-deps hb-api --schema=check
```

只读检查以退出码 `0` 结束后，才启动主后端；`--no-build` 保证启动的是已经迁移验证过的候选镜像：

HBWeb schema 命令的退出码固定如下，部署脚本应按此判断，不能把非零结果当作可忽略警告：

| 退出码 | 含义 |
| ---: | --- |
| `0` | 已就绪或迁移成功 |
| `2` | 命令参数错误，或常规启动仍启用了遗留自动初始化 |
| `20` | 缺少当前版本要求的迁移记录，或设备激活 schema 不兼容 |
| `22` | 数据库连接或迁移执行失败 |
| `23` | 另一个迁移任务正持有数据库迁移锁 |
| `130` | 用户取消 |

```bash
sudo docker compose --env-file .env up --no-build -d hb-api
```

等待容器健康并执行最小业务 smoke test：

```bash
sudo docker inspect hb-platform-vite-api --format '{{.State.Health.Status}}'
curl -fsS http://127.0.0.1:5002/api/health
```

除健康端点外，还需通过受控账号验证至少一条已授权的登录或只读业务查询链路；不得在命令、文档或日志中输出账号凭据、连接串、token 或其他秘密。

### 9.5 部署收银后端

确认 `.env` 中的共享路径，并与主后端 `.env` 逐项比对（必须是同一个目录）：

```dotenv
ATTENDANCE_QR_DATA_PROTECTION_KEYS_HOST_PATH=<与主后端相同>
LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH=<与主后端相同>
```

启动：

```bash
cd /www/HBWeb/BackEnd/hbpos-api
sudo docker compose --env-file .env \
  -f apps/pos-wpf/docker-compose.hotbargain.yml \
  up --build -d hbpos-api
```

等待：

```bash
sudo docker inspect hbpos-api --format '{{.State.Health.Status}}'
curl -fsS http://127.0.0.1:5003/api/v1/health
```

验证两个容器的考勤、Linkly 挂载源一致（Source 必须相同）：

```bash
sudo docker inspect hb-platform-vite-api \
  --format '{{range .Mounts}}{{println .Source "->" .Destination}}{{end}}'
sudo docker inspect hbpos-api \
  --format '{{range .Mounts}}{{println .Source "->" .Destination}}{{end}}'
```

### 9.6 部署前端与 Nginx

1. 恢复或重新构建前端静态文件。
2. 保留 `.user.ini`。
3. 恢复 Nginx vhost。`/pos-api/` 的反向代理片段已纳入仓库：`scripts/ops/nginx/hotbargain-pos-api.location.conf`，改动或重建后以它为准，并注意其中的约束（前缀剥离、透传 Authorization、读超时不低于 240 秒、不加 IP 白名单）。
4. 安全迁移或重新签发 TLS 证书。
5. 运行：

```bash
sudo nginx -t
sudo systemctl reload nginx
```

### 9.7 DNS 切换前验证

使用新服务器 IP 验证域名和证书，不修改本机全局 hosts：

```bash
curl --resolve hotbargain.vip:443:NEW_SERVER_IP \
  https://hotbargain.vip/api/health
curl --resolve hotbargain.vip:443:NEW_SERVER_IP \
  https://hotbargain.vip/pos-api/api/v1/health
```

确认通过后再修改 DNS A/AAAA 记录。

### 9.8 Linkly 凭据与回调验证（部署后必做）

目录挂载一致不等于能解密，必须用真实终端做一次连接测试：

1. **凭据可解密**：在任一 POS 的设置页，对一台已配对的 Linkly 终端点 “Test Logon”（对应接口 `POST /api/v1/linkly/cloud-backend/terminals/{terminalId}/connection-test`，会让收银后端解密该终端凭据并换取 token）。
   - 通过：说明收银后端能用共享 ring 解开该终端凭据。
   - 返回 `LINKLY_CLOUD_TERMINAL_CREDENTIAL_KEY_RING_MISMATCH`，或收银后端日志出现 `key ring mismatch ... missingKeyId`：
     停止后续步骤，回到第 4.3 节，把旧 key 文件恢复进共享目录；**不要**在后台重新录入密码。
2. **回调可达**：POS 的 `cloud-backend/health` 会对随机 sessionId 经公网地址自探测，结果在检查项 `CALLBACK_REACHABILITY` 中。
   也可以手工验证（命令见 `scripts/ops/nginx/hotbargain-pos-api.location.conf` 头部注释），期望 `200`。
   - `401`：Authorization 头被代理层吞掉，或两侧 bearer 不一致；
   - `403` / `404`：被 WAF、IP 白名单或前缀改写拦截；
   - 超时或连接失败：POS 容器无法经公网域名回访自己（hairpin NAT / DNS），可能是误报，需结合 Linkly 沙箱实测判断。
3. 失败只会影响按键提示和回单，交易结果仍能靠轮询取得，所以该检查是提示项，不会让 POS 停止刷卡；但上线验收必须确认它为就绪。

## 10. 强制验收清单

基础服务：

- [ ] `hb-api-migrate` 单次运行以退出码 `0` 完成
- [ ] 主后端 `--schema=check` 以退出码 `0` 完成
- [ ] `https://hotbargain.vip/` 返回 `200`
- [ ] `https://hotbargain.vip/login` 返回 `200`
- [ ] `https://hotbargain.vip/api/health` 返回 `200`
- [ ] `https://hotbargain.vip/pos-api/api/v1/health` 返回 `200`
- [ ] `hb-platform-vite-api` 为 `healthy`
- [ ] `hbpos-api` 为 `healthy`
- [ ] Nginx 配置检查通过
- [ ] TLS 证书链和域名正确

挂载与密钥：

- [ ] 考勤二维码 ring：主后端和收银后端挂载同一个宿主机目录（容器内 `/app/App_Data/AttendanceQrDataProtectionKeys`）
- [ ] Linkly 凭据 ring：主后端和收银后端挂载同一个宿主机目录（容器内 `/app/App_Data/LinklyCloudCredentialDataProtectionKeys`）
- [ ] 上述两个目录在启动 API 前已恢复旧 key 文件（非空目录），且文件数量与旧服务器一致
- [ ] 主后端全局 ring 与收银后端命名卷 `hbpos-api-data-protection-keys` 已随迁移恢复
- [ ] 旧 DataProtection key 文件完整保留
- [ ] Linkly 终端连接测试通过（无 `KEY_RING_MISMATCH`），见第 9.8 节
- [ ] 两个 `.env` 权限正确且变量名齐全
- [ ] 日志中没有密钥、连接串或 token 泄漏

业务功能：

- [ ] Web 登录成功
- [ ] 移动端登录和 API 请求成功
- [ ] POS 设备认证成功
- [ ] Linkly 回调可达：`CALLBACK_REACHABILITY` 就绪，手工 `curl` 自测返回 `200`（第 9.8 节）
- [ ] Square 回调可达
- [ ] EAS webhook 验签成功
- [ ] 图片/对象存储访问成功

考勤二维码：

- [ ] POS 生成 `HBATE1` 五段二维码
- [ ] 移动端扫描后 resolve 成功
- [ ] 不出现 `ATTENDANCE_QR_KEY_DECRYPT_FAILED`
- [ ] 有效二维码进入定位/打卡流程
- [ ] 真实过期二维码显示“二维码已过期”
- [ ] POS 与主后端 UTC 时间差在允许范围内

## 11. 切换与回滚

### 11.1 切换原则

- 不要长时间让新旧服务器同时执行后台任务或 webhook 消费。
- DNS 传播期间监控两台服务器请求量、错误率和数据库连接数。
- 旧服务器至少保留到 DNS TTL 完全过期并完成业务验收。
- 切换窗口内不要删除旧服务器、旧容器或旧 DataProtection key ring。

### 11.2 回滚触发条件

出现以下任一情况应考虑立即回滚：

- 主后端或收银后端持续 unhealthy
- 数据库连接失败或出现数据一致性风险
- 登录、支付、订单或考勤主链路不可用
- DataProtection 解密失败（含 `ATTENDANCE_QR_KEY_DECRYPT_FAILED`、`LINKLY_CLOUD_TERMINAL_CREDENTIAL_KEY_RING_MISMATCH`）
- TLS、Nginx 路由或 webhook 大面积失败

### 11.3 回滚步骤

1. 停止新服务器对外写入，避免双写扩大。
2. 将 DNS 恢复为旧服务器地址。
3. 确认旧服务器主后端、收银后端和 Nginx 正常。
4. 验证旧服务器健康端点和考勤二维码。
5. 保留新服务器日志、容器状态和配置快照用于分析。
6. 禁止用新服务器生成的临时 DataProtection key 覆盖旧 key ring。

## 12. 迁移交接记录模板

```text
迁移日期：
负责人：
旧服务器：
新服务器：
DNS TTL：
发布 Git commit：
前端构建版本：
主后端镜像/容器：
收银后端镜像/容器：
数据库是否迁移：
DataProtection key 文件数量（主后端全局 / 考勤 / Linkly 凭据 / 收银后端卷，分别记录）：
TLS 证书到期时间：
备份位置（不得记录密码）：
切换开始时间：
切换完成时间：
验收结果：
回滚截止时间：
遗留事项：
```
