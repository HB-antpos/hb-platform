# 人脸抓拍接收服务（face-ingest）运维手册（P0 接收落盘 + P1 解析入库）

## 1. 用途与边界

门店海康摄像机（「应用仓库 → 人脸抓拍」）通过报警主机 / HTTP 监听，主动用 HTTPS 把抓拍事件推到
`https://hotbargain.vip/face-ingest/hik/v1/<token>`。

P0 只做一件事：**校验摄像机 token → 把请求体逐字节原样落盘并写一份元数据 → 立刻回 200**。目的是到现场抓真实报文。

P1（第 13 节）在不改变接收端点行为的前提下，加了一个**后台处理器**：宽容解析海康 multipart → 存图（本地或专用私有 COS）→
写主库 `FaceCamera` / `FaceCaptureEvent` / `FaceCaptureImage` 三张表（受限账号直连）。第 1–12 节描述的接收、落盘、清理行为在 P1 里不变。

P0 **不做**：

- 不解析 multipart，不入库，不接 COS；
- 不做人脸比对、黑名单、告警推送（这些都还没做）；
- 不接中心日志（日志只进容器控制台，由 docker json-file 轮转）。

| 项目 | 值 |
| --- | --- |
| 源码 | `services/backend/BlazorApp.FaceIngest`（不进 `BlazorApp.sln`，不引用 Api / Shared；P1 可选直连主库，见第 13 节） |
| compose | `services/backend/BlazorApp.FaceIngest/docker-compose.face-ingest.yml`，项目名 `hbweb_face_ingest` |
| 容器 / 镜像 | `hb-face-ingest` / `hb-face-ingest:local` |
| 端口 | 宿主机 `127.0.0.1:5004` → 容器 `5004`，公网只经 Nginx |
| 公网入口 | `https://hotbargain.vip/face-ingest/`（Nginx 片段 `scripts/ops/nginx/hotbargain-face-ingest.location.conf`） |
| 健康检查 | `GET /face-ingest/health` → `{"status":"healthy","captureEnabled":…,"storedFileCount":…,"storedBytes":…,"cameraSource":"json\|database","cameraSourceReady":…}` |

## 2. 隐私要求（部署前必读）

- 人脸图像属于澳洲隐私法下的**敏感信息**。P0 **仅限试点门店**，原始报文**保留 7 天**后自动删除。
- 取回的报文只放在本机受限目录（见第 9 节），不得上传 COS / 网盘 / 同步盘，不得发到聊天、邮件、工单，用完删除。
- 本地调试产生的 `App_Data/FaceIngest/` 已被 `services/backend/.gitignore` 忽略，任何情况下都不要强制提交。
- 摄像机 token 等同密码：只在配置摄像机时出现一次，不写进 Git、聊天、工单；服务端只存它的 SHA-256。
- 门店是否需要张贴采集告示，由业务方 / 法务按公司隐私政策确认后再开启抓取。

## 3. 服务器目录与 `.env`（首次部署）

| 用途 | 宿主机路径 | 容器内路径 |
| --- | --- | --- |
| 源码与 compose（结构同 `services/backend` 的子集） | `/www/HBWeb/BackEnd/face-ingest/BlazorApp.FaceIngest/` | — |
| `.env`、备份 | `/www/HBWeb/BackEnd/face-ingest/.env`、`…/backups/` | — |
| 原始报文 | `/www/HBWeb/face-ingest-data` | `/app/App_Data/FaceIngest` |
| 摄像机配置 `cameras.json` | `/www/HBWeb/face-ingest-config` | `/app/config`（只读） |

硬性约束：

- 存储目录**不得**放在 `/www/HBWeb/BackEnd/master-Vite/` 下：主后端部署对它做 `rsync --delete`，会被整目录删掉。
- 存储目录**不得**用 `/lhcos-data`：那是 COS 挂载（`public_bucket=1`），不能放敏感数据。
- `.env` **不得**放进 `BlazorApp.FaceIngest/`：升级时对该目录做 `rsync --delete`。
- 存储与配置目录属主必须是 `1000:1000`、权限 `700`（容器内 `appuser` 的 uid 是 1000）；用 `install -d`，不要用裸 `mkdir`。

在服务器上执行：

```bash
# 确认 5004 未被占用（应输出「5004 未占用」）
sudo ss -ltnpH 'sport = :5004' | grep . && echo "5004 已被占用，先排查" || echo "5004 未占用"

sudo install -d -m 755 /www/HBWeb/BackEnd/face-ingest /www/HBWeb/BackEnd/face-ingest/backups
sudo install -d -o 1000 -g 1000 -m 700 /www/HBWeb/face-ingest-data
sudo install -d -o 1000 -g 1000 -m 700 /www/HBWeb/face-ingest-config

cd /www/HBWeb/BackEnd/face-ingest
if [ -e .env ]; then echo ".env 已存在：不要覆盖，用 sudo nano .env 逐行修改"; else
sudo tee .env >/dev/null <<'EOF'
FACE_INGEST_STORAGE_HOST_PATH=/www/HBWeb/face-ingest-data
FACE_INGEST_CONFIG_HOST_PATH=/www/HBWeb/face-ingest-config
FACE_INGEST_CAPTURE_ENABLED=false
EOF
fi
sudo chmod 600 .env
```

`.env` 变量（不含任何密钥）：

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `FACE_INGEST_STORAGE_HOST_PATH` | 必填 | 原始报文宿主机目录 |
| `FACE_INGEST_CONFIG_HOST_PATH` | 必填 | `cameras.json` 所在宿主机目录（整个目录只读挂载） |
| `FACE_INGEST_CAPTURE_ENABLED` | `false` | 是否落盘；关闭时鉴权通过照样回 200，但请求体读掉丢弃 |
| `FACE_INGEST_RETENTION_DAYS` | `7` | 保留天数 |
| `FACE_INGEST_MAX_TOTAL_BYTES` | `5368709120`（5 GB） | 已存 `.bin` + `.json` 总量上限 |
| `FACE_INGEST_MAX_FILE_COUNT` | `100000` | 已存文件数上限：`.bin`、`.json` 各算 1 个（10 万个文件约 5 万条报文），与 `/health` 的 `storedFileCount` 同口径 |
| `FACE_INGEST_MIN_FREE_DISK_BYTES` | `10737418240`（10 GB） | 存储所在盘剩余低于此值即停止落盘 |

单请求上限固定 16 MB（与 Nginx `client_max_body_size 16m` 对齐），清理周期固定 10 分钟，都不开放 `.env` 覆盖。

## 4. 摄像机 token 与 `cameras.json`

### 4.1 生成 token

每台摄像机一个 token。在服务器上生成，避免 token 在别处落地：

```bash
TOKEN=$(openssl rand -hex 24)
printf '%s\n' "$TOKEN"                               # 48 位十六进制，只给现场配置摄像机的人看这一次
printf %s "$TOKEN" | sha256sum | cut -d' ' -f1      # 这一串写进 cameras.json 的 TokenSha256
unset TOKEN
```

- 摄像机 URL 为 `/face-ingest/hik/v1/<token>`，共 68 个字符，在海康 URL 字段 128 字符上限内。
- token 丢了无法从哈希反推，只能重新生成并替换哈希。本机是 macOS 时用 `shasum -a 256` 代替 `sha256sum`。
- 少数固件 URL 不能带路径参数、只能走 Basic 认证（`httpAuthenticationMethod` 为 `base64`）：URL 填 `/face-ingest/hik/v1`，
  用户名随意，密码填 token。部分固件密码最长 16 位，此时这台摄像机改用 `openssl rand -hex 8` 生成 16 位 token。
- 只能带 query 的固件：URL 填 `/face-ingest/hik/v1?token=<token>`（海康会在后面追加 `&macAddress=…`，服务端会忽略）。

### 4.2 `cameras.json` 格式

只放 `Cameras`；开关、容量等其他配置只在 `.env` 里改，避免两处配置互相覆盖。下面全是**假数据**：

```json
{
  "FaceIngest": {
    "Cameras": [
      {
        "CameraId": "test01-entrance-01",
        "StoreCode": "TEST01",
        "TokenSha256": "0000000000000000000000000000000000000000000000000000000000000000",
        "Enabled": true,
        "Note": "示例：TEST01 门店正门人脸抓拍机"
      },
      {
        "CameraId": "selftest",
        "StoreCode": "SELFTEST",
        "TokenSha256": "1111111111111111111111111111111111111111111111111111111111111111",
        "Enabled": false,
        "Note": "公网自测专用，自测时临时改 true，测完改回 false"
      }
    ]
  }
}
```

校验规则（服务启动时不合法会直接启动失败；运行中改成不合法则继续用上一份合法配置并记 Error）：

- `CameraId`：小写字母、数字、横线，首字符不能是横线，最长 64，不可重复（它会进文件路径）；
- `StoreCode`：非空，最长 32，只允许字母、数字、`_`、`-`；
- `TokenSha256`：64 位十六进制，不可重复；
- `Enabled` 缺省为 `true`；只能写 JSON 布尔值 `true` / `false`（写成 `1`、`"yes"`、`null` 会被判为不合法）；设为 `false` 后该摄像机的推送立刻返回 401。

### 4.3 新增 / 修改摄像机（不重启容器）

配置目录用轮询监视，改完几秒内生效，不需要重启。**必须先写临时文件、校验通过再 `mv` 原子替换**，不要直接编辑 `cameras.json`：
运行中文件若是坏 JSON（写了一半、少个括号），这次改动不会被采纳，服务继续用上一份配置，并记一条 Error（`cameras.json could not be loaded`）；
此时要尽快把文件修好：坏文件在位期间，下一次容器重启就会启动失败；
容器重启时 `cameras.json` 是坏 JSON 或字段校验不通过，服务会直接启动失败（容器反复重启，`docker logs` 里有 `Unhandled exception`），所有推送都到不了；
`cameras.json` 存在但容器用户 uid 1000 读不到（例如用 root 恢复成 `root:root 600`）时同样启动失败（`UnauthorizedAccessException`），
而运行中换进一个读不到的文件既不生效也不记任何日志，要等下次重启才暴露，所以替换前必须核对读权限（见下面的校验命令）；
事后补救只 `chown` / `chmod` 不会触发热加载，还要 `touch` 一次（见本节末尾）。
另外，删掉 `cameras.json` 或把 `Cameras` 清空是合法配置，会立刻让所有摄像机 401。

首次（还没有 `cameras.json`）：建一个属主 1000、权限 600 的空文件，粘贴 4.2 节格式的内容：

```bash
CONFIG_DIR=/www/HBWeb/face-ingest-config
sudo install -o 1000 -g 1000 -m 600 /dev/null "$CONFIG_DIR/cameras.json.new"
sudo nano "$CONFIG_DIR/cameras.json.new"
```

以后修改：先备份，再复制一份编辑（`cp -p` 保留属主和权限）：

```bash
CONFIG_DIR=/www/HBWeb/face-ingest-config
sudo cp -p "$CONFIG_DIR/cameras.json" "$CONFIG_DIR/cameras.json.bak-$(date +%Y%m%d%H%M%S)"
sudo cp -p "$CONFIG_DIR/cameras.json" "$CONFIG_DIR/cameras.json.new"
sudo nano "$CONFIG_DIR/cameras.json.new"
```

校验并替换（先确认容器用户 uid 1000 读得到新文件，再校验内容；任一步失败都不会替换）。
`sudo python3` 以 root 运行，查不出读权限问题，所以读权限单独用 uid 1000 检查：

```bash
CONFIG_DIR=/www/HBWeb/face-ingest-config
sudo stat -c '%u:%g %a %n' "$CONFIG_DIR/cameras.json.new"   # 应为 1000:1000 600
{ sudo -u '#1000' test -r "$CONFIG_DIR/cameras.json.new" \
    || { echo "uid 1000 读不到 cameras.json.new：先 sudo chown 1000:1000 并 sudo chmod 600，再重跑本命令"; false; }; } \
  && sudo python3 - "$CONFIG_DIR/cameras.json.new" <<'PY' && sudo mv "$CONFIG_DIR/cameras.json.new" "$CONFIG_DIR/cameras.json" && echo "已替换 cameras.json"
import json, re, sys
cams = json.load(open(sys.argv[1], encoding='utf-8-sig'))['FaceIngest']['Cameras']
errors = []
for c in cams:
    cid = c.get('CameraId') or ''
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', cid):
        errors.append(f'CameraId 非法: {cid!r}')
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,32}', c.get('StoreCode') or ''):
        errors.append(f'StoreCode 非法: {cid!r}')
    if not re.fullmatch(r'[0-9a-fA-F]{64}', c.get('TokenSha256') or ''):
        errors.append(f'TokenSha256 不是 64 位十六进制: {cid!r}')
    if not isinstance(c.get('Enabled', True), bool):
        errors.append(f'Enabled 只能是 true / false: {cid!r}')
ids = [c.get('CameraId') for c in cams]
hashes = [(c.get('TokenSha256') or '').lower() for c in cams]
if len(set(ids)) != len(ids):
    errors.append('CameraId 重复')
if len(set(hashes)) != len(hashes):
    errors.append('TokenSha256 重复')
print('\n'.join(errors) if errors else f'校验通过，共 {len(cams)} 台')
sys.exit(1 if errors else 0)
PY
```

替换后看一眼日志：应出现一条配置重新加载（`trigger=reload`）的记录，摄像机数量与预期一致；
出现 `reload rejected`（字段校验不通过）或 `cameras.json could not be loaded`（JSON 语法错误）的 Error，说明新配置被拒、仍在用旧配置；
十几秒后两者都没有，说明文件改动没被监听到（例如改到了别的目录），或者新文件 uid 1000 读不到（这种情况不记任何日志，下次重启会直接起不来；用上面的 `stat` / `test -r` 核对），同样仍在用旧配置：

```bash
sudo docker logs --since 2m hb-face-ingest 2>&1 | tail -20
```

注意：读不到时**只改属主/权限不会触发热加载**。轮询只比较文件修改时间（mtime），`chown` / `chmod` 不改 mtime，
服务会一直用旧配置直到下次重建容器（停用某台摄像机的改动也不会生效）。修好属主和权限后必须再 `touch` 一次，
然后重新看日志，确认出现 `trigger=reload` 且摄像机数量正确：

```bash
CONFIG_DIR=/www/HBWeb/face-ingest-config
sudo chown 1000:1000 "$CONFIG_DIR/cameras.json" && sudo chmod 600 "$CONFIG_DIR/cameras.json"
sudo touch "$CONFIG_DIR/cameras.json"
sleep 15; sudo docker logs --since 1m hb-face-ingest 2>&1 | grep -E 'trigger=reload|reload rejected|could not be loaded'
```

## 5. 部署、升级与回滚

**每条 compose 命令都必须带 `-p hbweb_face_ingest --env-file .env`。禁止 `--remove-orphans`、`down -v`、`docker system prune`。**
本服务与主后端、收银后端完全独立，部署它不应改变 `hb-platform-vite-api` 和 `hbpos-api` 的容器 ID。

### 5.1 部署前记录（服务器）

```bash
cd /www/HBWeb/BackEnd/face-ingest
TS=$(date +%Y%m%d%H%M%S)
REASON=upgrade   # 本次部署的简短标识，只能用小写字母、数字、点、横线，例如 pr720
echo "TS=$TS REASON=$REASON"

# 记录另外两个 API 的容器 ID，部署后核对没变
sudo docker ps --filter name=hb-platform-vite-api --filter name=hbpos-api --format '{{.ID}} {{.Names}} {{.Status}}'

# 源码备份（首次部署没有源码，会跳过）
[ -d BlazorApp.FaceIngest ] && sudo tar -C /www/HBWeb/BackEnd/face-ingest \
  -czf "/www/HBWeb/BackEnd/face-ingest/backups/source-$REASON-$TS.tar.gz" BlazorApp.FaceIngest

# 给正在运行的镜像打回滚标签（按容器实际镜像 ID，不按 :local 名字）
if RUNNING_IMAGE=$(sudo docker inspect hb-face-ingest --format '{{.Image}}' 2>/dev/null); then
  sudo docker tag "$RUNNING_IMAGE" "hb-face-ingest:rollback-$REASON-$TS" \
    && echo "ROLLBACK_TAG=hb-face-ingest:rollback-$REASON-$TS"
else
  echo "没有 hb-face-ingest 容器，跳过回滚标签（首次部署属正常）"
fi
```

### 5.2 上传源码（本机，仓库根目录）

只同步 `BlazorApp.FaceIngest/` 这一个目录；`--delete` 的范围也只到这里，不会碰服务器上的 `.env` 和 `backups/`。

```bash
: "${HOTBARGAIN_SSH_KEY:?先 export HOTBARGAIN_SSH_KEY=登录 ubuntu@hotbargain.vip 的私钥路径}"
git status --short --branch
git log -1 --pretty=format:'%h %s%n'

# 预演：确认删除项与变更清单只涉及本次改动的文件
rsync -azc --delete --omit-dir-times --rsync-path="sudo rsync" -n --itemize-changes \
  --exclude 'bin' --exclude 'obj' --exclude 'App_Data' --exclude 'appsettings.Development.json' \
  -e "ssh -i '$HOTBARGAIN_SSH_KEY'" \
  services/backend/BlazorApp.FaceIngest/ \
  ubuntu@hotbargain.vip:/www/HBWeb/BackEnd/face-ingest/BlazorApp.FaceIngest/

# 预演结果符合预期后，去掉 -n --itemize-changes 正式上传
rsync -azc --delete --omit-dir-times --rsync-path="sudo rsync" \
  --exclude 'bin' --exclude 'obj' --exclude 'App_Data' --exclude 'appsettings.Development.json' \
  -e "ssh -i '$HOTBARGAIN_SSH_KEY'" \
  services/backend/BlazorApp.FaceIngest/ \
  ubuntu@hotbargain.vip:/www/HBWeb/BackEnd/face-ingest/BlazorApp.FaceIngest/
```

### 5.3 构建并启动（服务器）

首次部署前先完成第 3 节目录与 `.env`。还没有摄像机时可以先不建 `cameras.json`：服务照常启动，所有推送返回 401。

```bash
cd /www/HBWeb/BackEnd/face-ingest
sudo docker compose -p hbweb_face_ingest --env-file .env \
  -f BlazorApp.FaceIngest/docker-compose.face-ingest.yml config --quiet && echo "compose 配置 OK"

sudo docker compose -p hbweb_face_ingest --env-file .env \
  -f BlazorApp.FaceIngest/docker-compose.face-ingest.yml up -d --build

# 等待健康（healthcheck 每 30 秒一次，通常 30–60 秒变 healthy）
for i in $(seq 1 30); do
  health=$(sudo docker inspect hb-face-ingest --format '{{.State.Health.Status}}' 2>/dev/null || true)
  echo "health[$i]=$health"
  [ "$health" = healthy ] && break
  sleep 3
done
curl -fsS http://127.0.0.1:5004/health; echo
sudo docker logs --since 5m hb-face-ingest 2>&1 | tail -30

# 核对另外两个 API 的容器 ID 与 5.1 记录的一致
sudo docker ps --filter name=hb-platform-vite-api --filter name=hbpos-api --filter name=hb-face-ingest \
  --format '{{.ID}} {{.Names}} {{.Status}}'
```

### 5.4 Nginx（首次部署，或片段有改动时）

把 `scripts/ops/nginx/hotbargain-face-ingest.location.conf` 的 `location` 块放进宝塔 vhost 的 443 `server { }` 块里（与 `/pos-api/` 片段相邻即可）：

```bash
sudo cp -a /www/server/panel/vhost/nginx/www.hotbargain.vip.conf \
  "/www/HBWeb/BackEnd/face-ingest/backups/www.hotbargain.vip.conf.$(date +%Y%m%d%H%M%S)"
sudo nano /www/server/panel/vhost/nginx/www.hotbargain.vip.conf
sudo nginx -t && sudo systemctl reload nginx
```

`nginx -t` 失败时不要 reload，用上面的备份恢复。

### 5.5 公网自测（本机，不要在服务器上做）

```bash
# 1) 健康检查，期望 200
curl -sS -o /dev/null -w '%{http_code}\n' https://hotbargain.vip/face-ingest/health

# 2) 错误 token，期望 401
curl -sS -o /dev/null -w '%{http_code}\n' -X POST --data-binary 'probe' \
  https://hotbargain.vip/face-ingest/hik/v1/not-a-real-token

# 4) 漏写 /face-ingest 前缀，期望 404（Nginx /hik/ 兜底块，不进访问日志）
curl -sS -o /dev/null -w '%{http_code}\n' -X POST --data-binary 'probe' \
  https://hotbargain.vip/hik/v1/not-a-real-token

# 5) 明文 HTTP，期望 403（片段拒绝明文）或 301（站点开了强制 HTTPS）；401 / 200 说明明文能进到服务，片段与仓库不一致
curl -sS -o /dev/null -w '%{http_code}\n' -X POST --data-binary 'probe' \
  http://hotbargain.vip/face-ingest/hik/v1/not-a-real-token
```

第 3 条需要一个可用的 token：在 `cameras.json` 里把 `selftest` 的哈希换成新生成 token 的哈希并设 `Enabled: true`（第 4 节），测完改回 `false`。
样本仿照海康人脸抓拍报文：字面量 boundary、part 头带 `Content-Length`、filename 后多一个分号、二进制 JPEG 段，专门用来暴露宝塔 WAF 误拦。

```bash
# 3) 正确 token + 仿海康二进制 multipart，期望 200
read -rs FACE_INGEST_TEST_TOKEN   # 粘贴 selftest 的 token 后回车：不回显、不进 shell 历史
PROBE_DIR=$(mktemp -d)
PROBE_FILE="$PROBE_DIR/probe.bin"
{
  printf -- '--boundary\r\n'
  printf 'Content-Disposition: form-data; name="faceCapture"\r\n'
  printf 'Content-Type: application/json; charset="UTF-8"\r\n'
  printf 'Content-Length: 27\r\n\r\n'
  printf '{"eventType":"faceCapture"}\r\n'
  printf -- '--boundary\r\n'
  printf 'Content-Disposition: form-data; name="facePicture"; filename="facePicture.jpg";\r\n'
  printf 'Content-Type: image/jpeg\r\n'
  printf 'Content-Length: 200003\r\n\r\n'
  printf '\377\330\377'
  head -c 200000 /dev/urandom
  printf '\r\n--boundary--\r\n'
} > "$PROBE_FILE"
curl -sS -o /dev/null -w '%{http_code}\n' -X POST \
  -H 'Content-Type: multipart/form-data; boundary=boundary' \
  --data-binary @"$PROBE_FILE" "https://hotbargain.vip/face-ingest/hik/v1/$FACE_INGEST_TEST_TOKEN"
rm -rf "$PROBE_DIR"; unset FACE_INGEST_TEST_TOKEN PROBE_DIR PROBE_FILE
```

结果判读：`200` 通过；`401` token 不对或 `selftest` 仍是 `Enabled: false`；`403` 用了 `http://`（片段拒绝明文），或与 `404` 一样是被宝塔 WAF 或前缀改写拦截
（给 `/face-ingest/` 加 URL 白名单，不要关整站 WAF）；`413` 超过 16 MB；`502` 容器没起来；`503` 服务端落盘失败（看磁盘与目录权限）。
`CaptureEnabled=true` 时，服务器上 `sudo find /www/HBWeb/face-ingest-data/raw -path '*/selftest/*' -mmin -10` 应能看到一对 `.bin` / `.json`。

### 5.6 回滚（服务器）

只回滚镜像（compose 文件没改过时）：

```bash
cd /www/HBWeb/BackEnd/face-ingest
sudo docker images hb-face-ingest --format 'table {{.Tag}}\t{{.ID}}\t{{.CreatedAt}}'
ROLLBACK_TAG=hb-face-ingest:$(sudo docker images hb-face-ingest --format '{{.Tag}}' | grep -m1 '^rollback-')
echo "ROLLBACK_TAG=$ROLLBACK_TAG"   # 与 5.1 打印的 ROLLBACK_TAG 核对一致再继续

# 先给失败镜像留名便于排查，再把 :local 指回回滚锚点，不重新构建
sudo docker tag hb-face-ingest:local "hb-face-ingest:failed-$(date +%Y%m%d%H%M%S)"
sudo docker tag "$ROLLBACK_TAG" hb-face-ingest:local
sudo docker compose -p hbweb_face_ingest --env-file .env \
  -f BlazorApp.FaceIngest/docker-compose.face-ingest.yml up -d --no-build --force-recreate face-ingest
```

失败的部署同时改了 compose 文件时，先恢复源码备份再执行上面的回滚：

```bash
SRC_BACKUP=$(ls -1t /www/HBWeb/BackEnd/face-ingest/backups/source-*.tar.gz | head -1)
echo "SRC_BACKUP=$SRC_BACKUP"      # 确认是 5.1 生成的那个文件
sudo tar -tzf "$SRC_BACKUP" | head
sudo rm -rf /www/HBWeb/BackEnd/face-ingest/BlazorApp.FaceIngest
sudo tar -C /www/HBWeb/BackEnd/face-ingest -xzf "$SRC_BACKUP"
```

排查完后删掉 `failed-*` 标签；`rollback-*` 标签只保留最近 2 个：`sudo docker rmi hb-face-ingest:<旧标签>`。

## 6. 开关 CaptureEnabled

默认关闭。关闭时摄像机推送照样拿到 200（避免断网续传反复重推），只是不落盘。试点开始时打开、结束或出问题时关闭：

```bash
cd /www/HBWeb/BackEnd/face-ingest
sudo sed -i 's/^FACE_INGEST_CAPTURE_ENABLED=.*/FACE_INGEST_CAPTURE_ENABLED=true/' .env   # 关闭时把 true 换成 false
sudo grep -n '^FACE_INGEST_CAPTURE_ENABLED=' .env

# 环境变量只在容器创建时读取，必须重建容器（不重新构建镜像）
sudo docker compose -p hbweb_face_ingest --env-file .env \
  -f BlazorApp.FaceIngest/docker-compose.face-ingest.yml up -d --no-build --force-recreate face-ingest
for i in $(seq 1 20); do curl -fsS http://127.0.0.1:5004/health && break; sleep 2; done; echo   # 看 captureEnabled
```

要紧急停止抓取时用这个开关，**不要直接停容器**：停容器后 Nginx 回 502，开了断网续传的摄像机会把事件缓存起来，恢复后集中重推。

切换开关、升级（5.3）、回滚（5.6）都会重建容器，期间几秒 Nginx 对推送回 502。片段里的 `error_log /dev/null crit` 保证这些 502
不会把含 token 的请求行写进站点 error log，改 vhost 时不要删这一行。重建后可以确认站点日志里没有 token（只列文件名，不打印内容）。
范围是整个 `/www/wwwlogs`，包括宝塔每天切割后放进 `history_backups/` 的 `.gz` 归档（最多保留 180 份）；
通配与查找都在 root 的 shell 里执行（`/www/wwwlogs` 要 sudo 才能读，普通用户的 shell 展不开 `*.log`）；
匹配 `/hik/v1` 不带末尾斜杠，`/hik/v1/<token>` 与 `/hik/v1?token=` 两种写法都能查到：

```bash
sudo sh -c '
find /www/wwwlogs -type f | {
  n=0; hit=0; err=0
  while IFS= read -r f; do
    n=$((n+1))
    zgrep -qF "/hik/v1" "$f"; rc=$?
    if [ "$rc" -eq 0 ]; then hit=$((hit+1)); echo "含 /hik/v1: $f"
    elif [ "$rc" -ne 1 ]; then err=$((err+1)); echo "读取失败: $f"
    fi
  done
  echo "扫描 $n 个文件，命中 $hit 个，读取失败 $err 个"
}'
```

`扫描 0 个文件` 或 `读取失败` 不为 0 说明没查成，不能当作「没有」。有命中就说明 vhost 里的片段与仓库不一致（或摄像机漏写了前缀 / 用过 HTTP，见 7.2），
按 7.2 第 1 步轮换涉事摄像机的 token，并清理命中的文件（`.gz` 归档同样要处理）。

## 7. 摄像机侧配置

### 7.1 配置步骤

1. 在门店内网用浏览器登录摄像机 Web 页面（菜单名称随固件不同，试点机型多为国内固件）。
2. **配置 → 网络 → 高级配置 → 报警主机**（Alarm Server；老固件叫 HTTP 监听 / HTTP Listening），选一个空闲条目：
   - 协议：**HTTPS**
   - 地址类型：域名；域名：**`hotbargain.vip`**
   - 端口：**443**
   - URL：**`/face-ingest/hik/v1/<token>`**（token 见第 4 节）
   - 认证方式：none（token 已在 URL 里；只有 URL 带不了 token 时才按 4.1 节改用 Basic）
   - 图片上传方式（图片数据类型）：**binary**。不能选 URL：URL 模式下图片地址指向门店内网，云端取不到。
   - 参数格式（JSON / XML）、断网续传：保持默认，把当前值记下来。
   - 保存后**重新打开页面**确认协议、域名、端口没被固件回写成 HTTP / `0.0.0.0` / 80（有机型出现过）。
     被回写成 HTTP / 80 且已经推送过的，token 已明文经过公网（Nginx 片段对明文请求回 403，「测试」会失败；站点开了强制 HTTPS 时
     请求行还会进主站访问日志），改回 HTTPS 后立刻轮换这台摄像机的 token。
   - 点「测试」，然后在服务器上看日志（见下）。
3. **人脸抓拍**（应用仓库 → 人脸抓拍）的联动方式：勾选 **上传中心**（Notify Surveillance Center）。
4. **其他事件**（移动侦测、视频遮挡、越界侦测、区域入侵、视频丢失、异常等）的「上传中心」联动**全部关掉**，否则会持续推非人脸事件（告警期间约每秒一次），占流量和磁盘。
5. 让人从镜头前走过，在服务器上确认收到：

```bash
sudo docker logs --since 10m hb-face-ingest 2>&1 | tail -30
curl -fsS http://127.0.0.1:5004/health; echo
sudo find /www/HBWeb/face-ingest-data/raw -name '*.json' -mmin -10 | tail
```

### 7.2 HTTPS 推不通时的排查思路

1. **服务器日志里有这台摄像机的记录吗？**
   - 有 401（`source=` 是 Nginx 写入的门店出口 IP；同一来源 60 秒内只记一条）：token 填错（常见是多了空格）、
     `?token=` / Basic 写法不对，或摄像机 `Enabled` 为 `false`（日志里 `reason=camera-disabled`）。
   - 有成功落盘日志：推送正常；如果 `CaptureEnabled=false`，日志会说明没有落盘。
   - **少了 `/face-ingest` 前缀**的请求根本到不了本服务，服务日志里**不会有任何记录**；Nginx 片段的 `/hik/` 兜底块会直接回 404
     （摄像机「测试」显示失败）。如果 vhost 里没有兜底块，请求落到主站：`GET` 被前端兜底回 200（「测试」看似成功）、`POST` 回 405，
     并且含 token 的路径会写进主站访问日志 `/www/wwwlogs/www.hotbargain.vip.log`。
     只要摄像机曾按漏写前缀或 HTTP 地址推送过，就立刻轮换这台摄像机的 token（第 4 节），并用第 6 节的检查命令找出含 token 的日志文件后清理
     （包括 `/www/wwwlogs/history_backups/` 下按天切割的 `.gz` 归档）。
2. **服务器完全没收到**：
   1. 用门店内网电脑执行 `curl -sS -o /dev/null -w '%{http_code}\n' https://hotbargain.vip/face-ingest/health`，确认门店出口能访问。
   2. 已登录摄像机 Web 的浏览器里打开 `http://<摄像机内网 IP>/ISAPI/Event/notification/httpHosts/capabilities`，
      看 `protocolType` 是否含 HTTPS，以及 `httpAuthenticationMethod`、`urlLen`、`uploadImagesDataType`、`httpBroken`。
   3. 怀疑 TLS（摄像机不认 Let's Encrypt 证书链、不支持 TLS 1.3 或不发 SNI）：在任意机器上确认站点 TLS 1.2 可用、证书链完整：
      `openssl s_client -connect hotbargain.vip:443 -servername hotbargain.vip -tls1_2 </dev/null 2>/dev/null | grep -E 'Protocol|Verify return code|^subject=|^issuer='`
   4. 看宝塔 WAF 拦截记录：被拦时公网自测会是 403，且服务日志里没有记录。只给 `/face-ingest/` 加 URL 白名单。
   5. 固件只支持 HTTP：P0 **不开放明文入口**（token 会明文经过公网），Nginx 片段对 `http://` 的 `/face-ingest/` 请求直接回 403，
      所以「测试」失败、服务日志没有记录也可能是协议被选成了 / 回写成 HTTP。确需临时 HTTP 抓样本时另行评估，测完立刻关闭并轮换该摄像机 token。
3. **「测试」能通但走人不推**：检查人脸抓拍应用是否启用、联动是否勾了上传中心；部分型号开了人脸抓拍后 Smart 事件不可用（互斥），以抓拍为准。

## 8. 存储格式

```text
/www/HBWeb/face-ingest-data/raw/{UTC 日期 yyyy-MM-dd}/{CameraId}/{yyyyMMddTHHmmssfffZ}_{guid}.bin
/www/HBWeb/face-ingest-data/raw/{UTC 日期 yyyy-MM-dd}/{CameraId}/{yyyyMMddTHHmmssfffZ}_{guid}.json
```

- `.bin` 是请求体逐字节原样；`.json` 是元数据，**`.json` 出现才代表这一条完整**（`.tmp` 是正在写或中断的残片）。
- 元数据字段：`schemaVersion`、`captureId`、`cameraId`、`storeCode`、`receivedAtUtc`、`durationMs`、`method`、`path`、`query`、`protocol`、
  `headers`（数组，同名多值的头保留重复项）、`remoteIpAddress`、`forwardedFor`、`realIp`、`contentType`、`declaredContentLength`、
  `bodyBytes`、`bodySha256`、`tokenSource`（`path` / `query` / `basic`）。
- token 在 `path`、`query` 里被替换为 `***`，`Authorization`、`Cookie`、`Proxy-Authorization` 头的值也被替换为 `***`。
- 这些字段记的是**经 Nginx 改写后**的请求，分析摄像机行为时注意：
  - `protocol` 恒为 `HTTP/1.1`，`Connection` 头恒为 `close`，`Host` 头是小写且不带端口；摄像机用分块编码发送时也会被改成定长，
    所以 `declaredContentLength` 总有值、看不到 `Transfer-Encoding`；`Expect`、`TE`、`Keep-Alive` 头被去掉。
  - `headers` 不保证摄像机发送时的原始顺序（只保留同名头之间的先后），不要拿头的顺序识别固件。
  - `remoteIpAddress` 是 Docker 网桥网关地址（如 `::ffff:172.18.0.1`），门店出口 IP 看 `realIp`。
  - 摄像机发来的原始值由 Nginx 放在 `X-Hik-*` 头里（都在 `headers` 中）：`X-Hik-Client-Protocol`、`X-Hik-Client-Host`、
    `X-Hik-Client-Connection`、`X-Hik-Client-Transfer-Encoding`、`X-Hik-Client-Content-Length`、`X-Hik-Client-Expect`、
    `X-Hik-Conn-Requests`（大于 1 表示复用了 keep-alive 连接）、`X-Hik-Tls-Protocol`、`X-Hik-Tls-Sni`。
    某个头**不存在**表示摄像机没发对应的头（或没发 SNI）。

## 9. 取回原始报文

在本机执行（目标目录放在仓库和任何同步盘之外）：

```bash
: "${HOTBARGAIN_SSH_KEY:?先 export HOTBARGAIN_SSH_KEY=登录 ubuntu@hotbargain.vip 的私钥路径}"
DAY=$(date -u +%F)          # UTC 日期，默认今天；取别的日期直接改成 2026-10-12 这种格式
CAM=test01-entrance-01      # cameras.json 里的 CameraId
mkdir -p "$HOME/face-ingest-samples/$DAY/$CAM" && chmod 700 "$HOME/face-ingest-samples"
rsync -az --rsync-path="sudo rsync" --exclude '*.tmp' \
  -e "ssh -i '$HOTBARGAIN_SSH_KEY'" \
  "ubuntu@hotbargain.vip:/www/HBWeb/face-ingest-data/raw/$DAY/$CAM/" \
  "$HOME/face-ingest-samples/$DAY/$CAM/"
```

核对完整性（`bodyBytes` 与 `bodySha256` 都要对上）：

```bash
python3 - "$HOME/face-ingest-samples" <<'PY'
import hashlib, json, pathlib, sys
bad = 0
for meta in sorted(pathlib.Path(sys.argv[1]).rglob('*.json')):
    if meta.name.endswith('.parsed.json'):   # P1 不连库时的解析摘要，不是报文元数据
        continue
    m = json.loads(meta.read_text(encoding='utf-8'))
    body = meta.with_suffix('.bin').read_bytes()
    ok = len(body) == m['bodyBytes'] and hashlib.sha256(body).hexdigest() == m['bodySha256']
    bad += not ok
    print('OK ' if ok else 'BAD', meta.name, m.get('method'), m.get('contentType'), m['bodyBytes'])
print('不一致条数:', bad)
PY
```

用完删除（变量为空时命令会直接报错，不会误删上级目录）：

```bash
rm -rf "$HOME/face-ingest-samples/${DAY:?}/${CAM:?}"                                              # 本机
ssh -i "$HOTBARGAIN_SSH_KEY" ubuntu@hotbargain.vip "sudo rm -rf '/www/HBWeb/face-ingest-data/raw/${DAY:?}/${CAM:?}'"   # 服务器，需要立刻删时
```

服务器上删除后，存储统计会在下一轮清理（10 分钟内）校正。

## 10. 容量估算与清理

- 单条报文约 0.1–0.6 MB（公开抓包里人脸单图约 165 KB，ANPR 双图约 0.5 MB），元数据约 2 KB。
- 粗估（未证实）：每店每天 1–3 千条，即每店每天约 0.15–1.2 GB；30 家店每天 5–35 GB。服务器只有一块 80 GB 系统盘，所以 P0 只做少量试点门店。
- 默认护栏：已存总量 5 GB、10 万个文件（约 5 万条报文）、所在盘剩余低于 10 GB，任一触发就丢弃新报文（仍回 200）并节流记 Warning。
  一家高峰门店约 4 天就会顶到 5 GB，之后新报文被丢弃，所以要按第 9 节及时取回、删除已取回的目录。
- 自动清理每 10 分钟一轮（启动后先跑一轮）：删除超过保留天数的报文、超过 1 小时的 `.tmp` 残片和空目录，并重新校正统计。
  清理跑在容器里：容器停止或起不来期间自动清理暂停，恢复后启动的第一轮会补删超期报文；容器被删除（第 12 节下线）后就不会再清理。
- 调大上限前先看盘：剩余空间必须明显高于 `FACE_INGEST_MIN_FREE_DISK_BYTES`，给主后端、收银后端和 Docker 构建留余量；改 `.env` 后按第 6 节方式重建容器。
- 容器日志 json-file `50m × 5`，最多约 250 MB。

```bash
df -h /www
sudo du -sh /www/HBWeb/face-ingest-data
curl -fsS http://127.0.0.1:5004/health; echo
sudo docker logs --since 1h hb-face-ingest 2>&1 | grep -cE '(^|[[:space:]])warn:'   # 近 1 小时 Warning 条数（401、丢弃都记 Warning）
sudo docker images hb-face-ingest                                     # 旧 rollback 标签只留最近 2 个
```

## 11. 现场待确认清单

P1 解析要依赖这些结论，现场抓包时逐项确认并记录：

- [ ] 先 `GET /ISAPI/Event/notification/httpHosts/capabilities`，看 `protocolType` 有没有 HTTPS、认证方式有哪些、`uploadImagesDataType`、`httpBroken`、`urlLen`。
- [ ] HTTPS 加 Let's Encrypt 能不能推通。
- [ ] 「应用仓库 → 人脸抓拍」推出来的 eventType、part 名称、图片张数。
- [ ] 是否 keep-alive 复用连接（看元数据 `headers` 里的 `X-Hik-Conn-Requests` 是否大于 1、`X-Hik-Client-Connection` 的值，不要看 `Connection`，见第 8 节）、是否会并发。
- [ ] 故意回 500 或延迟 30 秒，观察重试间隔和超时（P0 服务没有故障注入开关；可短暂停容器让 Nginx 回 502 观察重试，延迟场景留到 P1 或本地抓包服务）。
- [ ] Test 按钮实际发出的请求（方法、路径、是否带 body）。
- [ ] 推送模式下有没有心跳。

## 12. 停用

1. 暂停抓取：按第 6 节把 `FACE_INGEST_CAPTURE_ENABLED` 改回 `false`。
2. 完全下线：先在摄像机上关掉「上传中心」联动或清空报警主机条目，再从宝塔 vhost 删掉 `/face-ingest/` 块（`nginx -t` 后 reload），最后只删本项目容器：

```bash
cd /www/HBWeb/BackEnd/face-ingest
sudo docker compose -p hbweb_face_ingest --env-file .env \
  -f BlazorApp.FaceIngest/docker-compose.face-ingest.yml rm -s -f face-ingest
```

3. 删除容器后自动清理不再运行，存储目录里的人脸报文不会再自动过期。需要的样本按第 9 节取回后**立即删除**，
   最晚不超过最后一次抓取后 7 天（第 2 节承诺的保留期）。先核对目录，再只删 `raw/` 下的内容：

```bash
sudo grep -n '^FACE_INGEST_STORAGE_HOST_PATH=' /www/HBWeb/BackEnd/face-ingest/.env   # 应为下面这个目录
DATA_DIR=/www/HBWeb/face-ingest-data
sudo du -sh "$DATA_DIR/raw" && sudo ls "$DATA_DIR/raw" | head                     # 应只看到 yyyy-MM-dd 日期目录
sudo find "${DATA_DIR:?}/raw" -mindepth 1 -delete
sudo find "${DATA_DIR:?}/raw" -mindepth 1 | wc -l                                  # 应为 0
```

   本机按第 9 节取回的样本照常「用完删除」。`cameras.json` 与 `.env` 保留到确认不再启用为止。

## 13. P1：解析、入库与私有 COS

### 13.1 数据流与边界

```text
摄像机 → Nginx → 接收端点（P0 逻辑不变：鉴权 → 原样落盘 raw/ → 立刻 200）
                     ↓（完全解耦，处理慢 / 库挂 / COS 挂都不影响回 200）
         后台处理器 CaptureProcessingService（每 5 秒一轮，每轮最多 50 条，串行）
           找 raw/ 下有 .json、没有 .processed / .failed、.retry 已到期的报文
           → 宽容解析（JSON / XML / 裸 XML、不规范 multipart，失败也不丢：记 ParseStatus / ParseNote）
           → 存图（COS 开：私有桶；COS 关：本地 images/）
           → 写库（配了连接串：事务内写事件 + 图片行，按 CaptureId / ImageId 幂等；没配：写本地 .parsed.json 摘要）
           → 写 .processed 标记
         清理任务（P0 已有）：按保留期删 raw/，并一起删处理标记与本地 images/
```

P1 **不做**：人脸比对、特征提取、客流聚类、黑名单、通知推送、管理后台页面；主 API 只建表，不读写这三张表。

| 摄像机来源 | 条件 | 行为 |
| --- | --- | --- |
| `json` | `FACE_INGEST_DB_CONNECTION` 为空 | 与 P0 完全相同：`cameras.json` 热更新 |
| `database` | `FACE_INGEST_DB_CONNECTION` 非空 | 以 `FaceCamera` 表为准（**不与 cameras.json 合并**），每 30 秒刷新；启动后到第一次读表成功之前，接收端点对所有请求回 **503**（摄像机断网续传缓存后重推，不会当成 401 丢数据）；之后刷新失败保留上一份快照，只记 Error；非法行（编号 / 门店 / 哈希格式错、哈希重复）只跳过该行并记 Error |

`/health` 的 `cameraSource` 显示当前来源，`cameraSourceReady=false` 表示数据库来源还没加载成功（此时 `status` 仍是 `healthy`，容器不会被重启）。

### 13.2 部署顺序（首次启用入库）

1. **主 API 建表**：按主后端发布流程用候选镜像跑一次显式迁移（`--schema=migrate`，见 `docs/production-server-migration-handover.md`「显式迁移」），
   确认迁移清单里有 `20261013.001-face-capture`（新建 `FaceCamera`、`FaceCaptureEvent`、`FaceCaptureImage`，只建表、主 API 不读写）。
2. **建受限账号**：13.3。
3. **把摄像机登记进 `FaceCamera`**：13.5（先登记再切换，否则切换后所有摄像机 401）。
4. **写 `.env`**：13.4。
5. **重建 face-ingest**：按第 5 节部署（需要重新构建镜像，P1 新增了 SqlClient 依赖），然后核对：

```bash
curl -fsS http://127.0.0.1:5004/health; echo        # cameraSource=database，cameraSourceReady=true
sudo docker logs --since 5m hb-face-ingest 2>&1 | grep -E 'Face ingest started|camera registry loaded|processor started'
```

`cameraSourceReady` 一直是 `false`：看日志里 `camera registry refresh from database failed` 的异常（连不上、登录失败、没权限）。
期间摄像机收到 503 会缓存重推；短时间修不好就把 `FACE_INGEST_DB_CONNECTION` 清空、按第 6 节方式重建容器，退回 cameras.json。

不入库、只想先看解析效果：保持 `FACE_INGEST_DB_CONNECTION` 为空即可，处理器照样解析、存本地图，并在报文旁写 `.parsed.json` 摘要（13.7）。

### 13.3 建受限数据库账号（DBA 手工执行，不由代码执行）

脚本：`services/backend/SqlScripts/face-ingest-db-login.sql`（幂等）。建 login + user `face_ingest_app`，**只**授：

| 表 | 权限 |
| --- | --- |
| `dbo.FaceCamera` | `SELECT`、`UPDATE(LastSeenAtUtc)`（只能改「最近推送时间」一列） |
| `dbo.FaceCaptureEvent` | `SELECT`、`INSERT` |
| `dbo.FaceCaptureImage` | `SELECT`、`INSERT` |

```bash
# 只用字母数字的随机密码（脚本把它放进 N'…' 字面量，含单引号会出错）；不回显、不进 shell 历史
FACE_INGEST_DB_PASSWORD=$(openssl rand -base64 36 | tr -dc 'A-Za-z0-9' | head -c 32); export FACE_INGEST_DB_PASSWORD
export MAIN_DATABASE=HBweb
sqlcmd -S <主库地址> -U <管理员账号> -I -b -i services/backend/SqlScripts/face-ingest-db-login.sql
# 立刻把密码写进服务器 .env（13.4），然后：
unset FACE_INGEST_DB_PASSWORD
```

脚本最后两段查询应只列出 6 行权限、且角色列表为空。密码**只**进服务器 `.env`；需要轮换时 `ALTER LOGIN [face_ingest_app] WITH PASSWORD = N'…'`，
改 `.env` 后按第 6 节方式重建容器。

### 13.4 `.env` 新变量（都有默认值，未配置是合法状态）

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `FACE_INGEST_PROCESSING_ENABLED` | `true` | 后台处理器开关；`false` 只接收落盘（P0 行为） |
| `FACE_INGEST_DB_CONNECTION` | 空 | **密钥**。受限账号连接串，空 = 不连库、摄像机走 cameras.json |
| `FACE_INGEST_COS_ENABLED` | `false` | 图片是否上私有 COS；关闭时写本地 `images/` |
| `FACE_INGEST_COS_BUCKET` / `FACE_INGEST_COS_REGION` | 空 | 专用私有桶（形如 `name-appid`）与地域（如 `ap-singapore`） |
| `FACE_INGEST_COS_SECRET_ID` / `FACE_INGEST_COS_SECRET_KEY` | 空 | **密钥**。只授 PutObject 的 CAM 子账号，**不得**复用 `TENCENT_*` 主密钥 |

连接串示例（全是假值，按实际改；`TrustServerCertificate` 按主库证书情况决定）：

```text
FACE_INGEST_DB_CONNECTION=Server=db.example.invalid,1433;Database=HBweb;User ID=face_ingest_app;Password=<只写在服务器 .env>;Encrypt=True;TrustServerCertificate=True;Connect Timeout=10
```

启动校验：连接串格式非法、`FACE_INGEST_COS_ENABLED=true` 但桶 / 地域 / 两个密钥缺任何一个 → 容器起不来，`docker logs` 只报键名、不打印值；
连接串格式对但连不上 → 照常启动，接收端点 503（13.1）。处理节奏（`ProcessingIntervalSeconds=5`、`ProcessingBatchSize=50`、`ProcessingMaxAttempts=10`）、
摄像机表刷新周期（30 秒）、COS 键前缀（`face-ingest`）与是否上传原始报文（`UploadRawBody=false`）沿用 appsettings 默认，不开放 `.env` 覆盖。

### 13.5 从 `cameras.json` 迁到 `FaceCamera` 表

`FaceCamera` 的字段与 `cameras.json` 一一对应（`TokenSha256` 原样照抄，**token 不用重发给摄像机**）；规则与 4.2 节相同：
`CameraId` 小写字母数字与连字符、`StoreCode` 1–32 位 `[A-Za-z0-9_-]`、`TokenSha256` 64 位十六进制且不重复。
用管理员账号执行（`face_ingest_app` 没有 INSERT 权限，这是有意的）：

```sql
-- 假数据示例：把 cameras.json 里每台摄像机照抄一行
INSERT INTO dbo.FaceCamera (CameraId, StoreCode, TokenSha256, Enabled, Note, DeviceModel, MacAddress)
VALUES (N'test01-entrance-01', N'S0000', '0000000000000000000000000000000000000000000000000000000000000000', 1,
        N'试点门店入口（示例）', N'DS-2CD3726FWDA3/F-IZS', N'00:00:5e:00:53:00');

-- 停用 / 恢复（30 秒内生效，不用重启容器）
UPDATE dbo.FaceCamera SET Enabled = 0, UpdatedAtUtc = SYSUTCDATETIME() WHERE CameraId = N'test01-entrance-01';

-- 核对：每台摄像机最近一次推送时间（处理器至多每 5 分钟更新一次）
SELECT CameraId, StoreCode, Enabled, LastSeenAtUtc FROM dbo.FaceCamera ORDER BY CameraId;
```

切换后 `cameras.json` 不再参与鉴权，但仍会被加载与校验（坏 JSON 照样让容器起不来），保留原文件即可，便于回退。

### 13.6 开启私有 COS（前置由用户在腾讯云控制台完成）

1. **新建专用私有桶**（建议与服务器同区 `ap-singapore`），访问权限「私有读写」；不要复用主桶 / 图片桶（主桶默认公开读）。
2. **CAM 子账号**：只授 `cos:PutObject`（确需核对时再加 `cos:HeadObject`），资源限定 `qcs::cos:<region>:uid/<appid>:<bucket>/face-ingest/*`；
   不给 GetObject / DeleteObject / List。服务端每次上传都在签名里带 `x-cos-acl: private`，即使桶权限被误改也不会公开单个对象。
3. **生命周期规则**：给 `face-ingest/images/`（与开启了原始报文上传时的 `face-ingest/raw/`）配过期删除，天数按隐私要求定（与本地保留期一致或更短）。
4. 写 `.env` 的五个 `FACE_INGEST_COS_*` 后按第 6 节方式重建容器；日志 `Face ingest started … imageStorage=cos` 即生效。

对象键：`face-ingest/images/{StoreCode}/{yyyy}/{MM}/{dd}/{CameraId}/{CaptureId}_{PartIndex}.jpg`（日期为 UTC 接收日）；
图片上 COS 后本地不再写 `images/`，原始报文仍只留本地 `raw/` 按保留期清理（排障依据）。上传判定：HTTP 200 且带 ETag，否则按 13.7 重试。

### 13.7 处理状态与排查

每条报文旁（同目录、同名 + 后缀）的旁路标记：

| 文件 | 含义 |
| --- | --- |
| `{name}.processed` | 处理成功（终态）：`parseStatus`、`faceCount`、`imageCount`、`imageStorage`、`databaseWritten`、`duplicateOfCaptureId` |
| `{name}.retry` | 失败待重试：`attempts`、`nextAttemptUtc`、`lastError`（截断 300 字）。退避 30 秒起翻倍、单次最长 6 小时 |
| `{name}.failed` | 放弃（终态）：重试满 10 次（`permanent=false`），或元数据损坏不可能成功（`permanent=true`）。原始报文保留到保留期 |
| `{name}.parsed.json` | 只在不连库时写：与库表列一一对应的解析摘要，便于离线看解析效果 |

`ParseStatus`：`0 Parsed`、`1 PartiallyParsed`（报文截断、有 faces[] 没人脸图、只有图没有事件体等，细节看 `ParseNote`）、
`2 Unparsed`（空 body、无法识别；原始报文仍在）、`3 NotFaceEvent`（VMD / videoloss 等，也入库便于统计流量）。
解析失败**不重试**（重试也一样），只有存图 / 写库 / 读文件失败才重试。

```bash
DATA=/www/HBWeb/face-ingest-data
sudo find "$DATA/raw" -name '*.retry' | wc -l                     # 待重试
sudo find "$DATA/raw" -name '*.failed' | head                     # 已放弃
sudo find "$DATA/raw" -name '*.retry' -exec sudo cat {} + | grep lastError | sort | uniq -c | sort -rn | head   # 失败原因分布
sudo docker logs --since 1h hb-face-ingest 2>&1 | grep -E 'capture processed' | tail -5
sudo docker logs --since 1h hb-face-ingest 2>&1 | grep -E 'processing failed|refresh from database failed|LastSeenAtUtc update failed'
```

想让 `.failed` 的报文重新处理（例如修好库权限后）：删掉对应的 `.failed`（保留 `.json` / `.bin`），下一轮就会重做；写库按 `CaptureId` / `ImageId` 幂等，不会重复插入。

重复报文（断网续传、持续告警）照样入库，`DuplicateOfCaptureId` 指向同一摄像机 24 小时内同一请求体哈希的最早一条。

### 13.8 存储口径

- 处理标记只有几百字节，**不计入** `/health` 的 `storedFileCount` / `storedBytes`（P0 容量护栏口径不变），由清理任务与报文同一保留期删除。
- COS 关闭时本地图片在 `images/{UTC 日期}/{CameraId}/{CaptureId}_{PartIndex}.jpg`，**同样不计入**上述统计，但和原始报文在同一块盘上：
  图片约等于原始报文再存一份，盘占用接近翻倍。写图前复用 `FACE_INGEST_MIN_FREE_DISK_BYTES` 护栏，剩余空间不足时该条进入重试而不是把盘写满。
  图片按保留期删除（写入时间或整个日期目录过期即删，处理延迟时最多比原始报文多留一天）。试点盘紧时建议尽早开 COS。
- 第 9 节的 rsync 会把 `.processed` 等标记一并取回（无害，完整性脚本已跳过 `.parsed.json`）；本地 `images/` 不在 `raw/` 下，需要时单独取，同样按第 2 节「用完删除」。

### 13.9 回退

- 只停处理：`FACE_INGEST_PROCESSING_ENABLED=false` 后按第 6 节重建容器，接收不受影响，未处理的报文留到重新打开后补处理（仍受保留期约束）。
- 退回 cameras.json：清空 `FACE_INGEST_DB_CONNECTION`，重建容器；已入库数据保留在三张表里。
- 关 COS：`FACE_INGEST_COS_ENABLED=false`，之后的图片写本地；已上 COS 的对象由生命周期规则过期。
- 整体回到 P0 镜像：按 5.6 回滚；P1 留下的标记与 `images/` 不影响 P0 接收，但 P0 的清理任务不认识它们：
  `.processed` / `.retry` / `.failed` 会留在 `raw/` 的日期目录里（目录因此删不掉；`.parsed.json` 会被 P0 当元数据计入统计并按期删除），
  `images/` 不会过期，需要手工按保留期删除。
- 完全下线（第 12 节）时，除 `raw/` 外同样要删除 `"${DATA_DIR:?}/images"` 下的内容（人脸图）。

## 14. 摄像机管理后台（Web）

第 13.5 节的手工 SQL 登记摄像机，现在可以在 Web 后台完成：新增、编辑、停用 / 启用、重新生成 token、查看最近抓拍记录（表格只有元数据；**图片是敏感生物识别信息，只有另外被授予「查看人脸抓拍图片」权限的账号才能按需查看，见 14.8**）。

### 14.1 入口与权限

| 项目 | 值 |
| --- | --- |
| 页面 | Web 后台 → 系统 → **人脸抓拍摄像机**，路由 `/system/face-cameras` |
| 权限码 | `FaceCapture.Cameras.Manage`（中文名「管理人脸抓拍摄像机」，分类「系统管理」） |
| 谁能用 | 管理员（Admin 角色靠角色放行）；其他账号只有被管理员在「角色管理」里显式授予该码后才可见。**不属于任何默认角色模板**，店长 / 经理默认看不到 |
| 接口 | `/api/react/v1/face-cameras`（列表 / 新增 / 修改 / `{cameraId}/rotate-token` / `{cameraId}/captures`），所有响应 `Cache-Control: no-store`；没有删除接口，摄像机只能停用。查看图片接口另见 14.8 |
| 配置 | 摄像机完整地址的域名前缀默认 `https://hotbargain.vip`；主 API 如需覆盖，配置 `FaceCapture:PublicBaseUrl`（必须是 `https` 且不带路径，否则回退默认值） |

**前提**：后台改的是 `FaceCamera` 表。人脸接收服务必须已按 13.2 / 13.5 切到 `database` 来源（`/health` 的 `cameraSource=database`、`cameraSourceReady=true`）才会读这张表；
还在 `json` 来源时，后台里的新增、停用都**不会生效**，摄像机仍按 `cameras.json` 鉴权。

### 14.2 权限入库（部署后手工执行一次）

权限定义不会随启动自动入库，不执行的话「角色管理」里看不到该权限，授权保存时也会被静默丢弃（管理员本人不受影响）。
脚本：`services/backend/BlazorApp.Api/Data/Migrations/20261013_AddFaceCameraManagePermission.sql`。

- 幂等：先查重，已存在同名权限不重复插入；若同名权限已被停用则报错中止，需人工核对；只登记权限，**不授予任何角色或用户**；时间用 UTC，`CreatedBy` 写 `Migration_20261013_FaceCameraManage`。
- 目标库必须是 `HBweb`（脚本开头会校验库名，不是则中止）。由管理员账号手工执行，例如（连接信息自行填写，口令不要写进命令行历史）：

```bash
sqlcmd -S <主库地址> -d HBweb -U <管理员账号> -I -b -i services/backend/BlazorApp.Api/Data/Migrations/20261013_AddFaceCameraManagePermission.sql
```

- 执行后核对：脚本末尾会回读，应看到 `InsertedPermissions` 为 1（重复执行为 0）和一行 `FaceCapture.Cameras.Manage`、`IsDeleted=0`。
- 回滚：按 `CreatedBy = N'Migration_20261013_FaceCameraManage'` 精确定位这一行；先在「角色管理」取消该权限已授予的账号，再把这一行 `IsDeleted` 置 1（停用权限定义）。**不要按权限码批量删除**，也不要物理删除。
  回滚后接口对非管理员返回 403，管理员仍可访问（Admin 角色放行），如需连管理员也不可用须回滚代码。

### 14.3 操作说明

1. **新增摄像机**：右上角「新增摄像机」→ 填摄像机编号（小写字母 / 数字 / 横线，不以横线开头，最长 64，建议 `<门店>-<位置>-<序号>`，如 `test01-entrance-01`）、
   选门店（必须是真实存在且未删除的门店）、型号、MAC、备注。提交成功后弹出**一次性地址框**：摄像机完整地址 `https://hotbargain.vip/face-ingest/hik/v1/<token>`。
   - **这个地址只显示这一次**：库里只存 token 的 SHA-256，关闭弹窗后任何人（包括管理员）都无法再看到，丢了只能重新生成。
   - 复制后直接到摄像机上配置（第 7 节）；不要发聊天、邮件、工单，也不要截图保存。
2. **重新生成 token（轮换）**：行内「重新生成 token」→ 确认。旧 token 立即从库里替换掉（接收服务最多 30 秒内停止接受旧 token），
   摄像机必须用新地址重新配置才能继续推送。适用于：token 泄露或怀疑泄露、摄像机被回写成 HTTP 推送过（见 7.1）、更换设备、地址丢失。
3. **停用 / 启用**：行内开关。停用后接收服务对该摄像机的推送回 401（最多 30 秒后生效），已入库的数据保留；启用后恢复。**没有删除**：停用即下线。
4. **编辑**：可改门店、型号、MAC、备注；摄像机编号不可改（它会进文件路径与历史记录）。改门店只影响之后的新抓拍，已入库事件保留原门店编码。
5. **查看最近抓拍**：行内「最近抓拍」打开抽屉，按接收时间倒序，每页 20 条（最多 50），表格只显示接收时间、摄像机事件时间、事件类型、人脸数、解析状态 / 备注、是否重复报文；最多翻到第 100 页。
   表格本身不加载任何图片；有「查看人脸抓拍图片」权限的账号每行多一列「图片」，点「查看图片」才按需读取（14.8）。

### 14.4 状态与统计口径

- **近期有推送**（接口字段仍叫 `online`，契约未改名，只是页面不再叫「在线」）：摄像机已启用，且 `LastSeenAtUtc` 距现在不超过 10 分钟（恰好 10 分钟仍算）。人脸接收服务收到有效推送后更新 `LastSeenAtUtc`（首次推送立即写入，之后对同一摄像机至多每 5 分钟更新一次），所以时间戳最多比真实最近推送晚 5 分钟，10 分钟窗口留足了余量。
- **暂无推送**：已启用但超过 10 分钟没有推送，或从未推送过。**已停用**优先于「暂无推送」显示。
- **「暂无推送」不代表断电或断网**：海康摄像机在推送模式下**只有检测到人脸才推送**，没有人经过镜头时什么都不发，也没有心跳；夜间、店内无人时长时间「暂无推送」是正常现象。页面上「暂无推送」标签的悬停提示和顶部说明写的是同一个意思。
- **怎么确认摄像机真的在工作**：在镜头前走一下，几秒内应出现新的抓拍记录（刷新「最近抓拍」抽屉，或看该摄像机状态变成「近期有推送」、近 24 小时抓拍数增加）。走过后仍然没有新记录，再按 7.2 节排查网络 / 配置，或在后台重新生成 token 后重配。
- 近 24 小时统计（以服务器当前时间往前 24 小时）：抓拍事件数（含重复报文）、人脸数（**排除**重复报文）、解析失败数（`ParseStatus=2`）；「最近事件」不限 24 小时。
- 查询只走 `IX_FaceCaptureEvent_Camera_Received` 的按摄像机区间查找，固定 3 条 SQL，不随摄像机数量增加；页面打开时查一次，没有自动轮询，请不要用脚本高频刷新该接口。

### 14.5 生效延迟

人脸接收服务每 **30 秒**从 `FaceCamera` 表重读摄像机列表，因此后台的新增、停用、启用、轮换、改门店**最多 30 秒后**在接收端生效；页面顶部有同样的提示。
轮换后的 30 秒内旧 token 可能仍被接受，新 token 也可能还会 401（接收服务还没刷新），属于预期，等待后重试即可，不要连续重复轮换。

### 14.6 现场配置摄像机（新增或轮换之后）

1. 在后台新增（或轮换）摄像机，复制一次性地址，**等 30 秒**再测试。
2. 在门店内网按第 7.1 节配置：报警主机协议 HTTPS、域名 `hotbargain.vip`、端口 443、URL 填地址里 `/face-ingest/hik/v1/<token>` 的路径部分（68 个字符，在海康 URL 字段 128 字符上限内），图片上传方式 binary，人脸抓拍联动勾选「上传中心」，其他事件联动全部关闭。
3. 点摄像机上的「测试」，再让人从镜头前走过；回到后台刷新，该摄像机应变为「近期有推送」（首次推送后很快），近 24 小时抓拍数增加，「最近抓拍」抽屉里能看到记录。
4. 不通时按 7.2 节排查；怀疑地址填错或泄露，回到后台重新生成 token 再配置一遍。

### 14.7 审计与日志

- 新增、修改、启用、停用、轮换都会写一条后台审计日志（类别 `BlazorApp.Api.Features.FaceCameras.FaceCameraAdminService`，消息以「人脸摄像机后台审计」开头），含动作、摄像机编号、门店、操作人 Id 与用户名、UTC 时间和变更字段名；**不含 token，也不含 token 哈希**。
- 这些日志级别是 Information：进主 API 容器日志（`docker logs`），默认**不进**中心日志（中心日志最低级别 Warning）。需要长期留痕时另行建审计表（尚未做）。
- 查看图片的审计另见 14.8（成功读取是 Information，被拒绝的尝试是 Warning，后者会进中心日志）。
- 哈希唯一约束冲突（概率极低）会记一条 Warning，只含摄像机编号与重试次数，并自动换 token 重试至多 3 次，仍冲突则返回 409，页面提示稍后重试。

### 14.8 查看抓拍图片（敏感生物识别信息）

最近抓拍抽屉里可以按需查看抓拍的人脸图与背景图。图片在专用私有 COS 桶里，**浏览器永远拿不到 COS 地址**：主 API 用服务端凭据读 COS，再把图片字节转发给已登录且有权限的账号。

**权限与接口**

| 项目 | 值 |
| --- | --- |
| 权限码 | `FaceCapture.Images.View`（中文名「查看人脸抓拍图片」，分类「系统管理」） |
| 谁能用 | 管理员（Admin 角色靠角色放行）；其他账号必须被管理员在「角色管理」里**显式授予**。与 `FaceCapture.Cameras.Manage` **分开授予**：管理摄像机**不**自动包含查看图片，只有查看权限没有管理权限也进不了该页面（页面入口仍是管理权限） |
| 列表 | `GET /api/react/v1/face-cameras/{cameraId}/captures`：只在调用者同时持有查看权限时，每条抓拍才带 `images` 元数据（图片编号、种类、存储位置、宽高、大小、人脸框、评分，**不含对象键和任何地址**）并返回 `canViewImages=true`；否则 `images` 恒为空数组、`canViewImages=false`，页面不显示任何图片入口 |
| 内容 | `GET /api/react/v1/face-cameras/{cameraId}/captures/{captureId}/images/{imageId}/content`：对象键**只来自数据库**，客户端只传三个编号；`imageId → captureId → cameraId` 任一不一致都是同一个 404；响应只有图片字节，`Content-Type` 只会是 `image/jpeg` / `image/png`，带 `Cache-Control: no-store`、`Pragma: no-cache`、`X-Content-Type-Options: nosniff`、`Content-Disposition: inline`，不带 ETag 和任何 COS 响应头 |

| 状态码 | 错误码 | 含义 / 页面提示 |
| --- | --- | --- |
| 403 | `FACE_IMAGE_FORBIDDEN` | 没有查看权限 |
| 404 | `FACE_IMAGE_NOT_FOUND` | 编号不存在或归属不一致（不区分，不泄露存在性） |
| 404 | `FACE_IMAGE_OBJECT_MISSING` | 库里有记录但 COS 里没有对象（已被生命周期清理等） |
| 409 | `FACE_IMAGE_NOT_IN_COS` | 图片仍在接收服务本地磁盘（`Storage=local`），页面提示「图片仍在接收服务本地，暂不可在后台查看」 |
| 415 | `FACE_IMAGE_TYPE_UNSUPPORTED` | 库里的类型不在白名单，或字节头与声明类型不符 |
| 502 | `FACE_IMAGE_TOO_LARGE` | 超过 5MB 上限（库里声明的大小、COS 声明的长度、实际读到的字节三处都卡） |
| 502 | `FACE_IMAGE_STORE_DENIED` / `FACE_IMAGE_STORE_TIMEOUT` / `FACE_IMAGE_STORE_ERROR` | COS 拒绝（401/403）/ 超时（总时限 15 秒）/ 其他失败；错误信息不含桶名、密钥、签名、地址 |
| 503 | `FACE_IMAGE_STORE_NOT_CONFIGURED` | 读取尚未配置，页面提示「图片读取尚未配置」 |

**配置（服务器 `master-Vite/.env`，主 API 的 compose 环境变量；全部可选，未配置是合法状态，不会让部署失败）**

| 变量 | 说明 |
| --- | --- |
| `FACE_CAPTURE_COS_BUCKET` / `FACE_CAPTURE_COS_REGION` | 专用私有桶（与第 13.4 节 `FACE_INGEST_COS_BUCKET` / `_REGION` 是同一个桶，如 `hb-face-ingest-1300114625` / `ap-singapore`）。**没有默认值，也绝不回退到主桶**；任一为空或格式不合法 → 接口返回 503 |
| `FACE_CAPTURE_COS_SECRET_ID` / `FACE_CAPTURE_COS_SECRET_KEY` | **密钥**，可选。必须成对配置；两个都不配时**回退使用主 API 已有的 `TENCENT_SECRET_ID` / `TENCENT_SECRET_KEY`**（桶与区域仍只用上面两个变量）；只配一个视为配置错误（503），不会混用两套密钥 |

- **推荐做法**：新建一个**只授 `cos:GetObject`、资源限定 `qcs::cos:<region>:uid/<appid>:<bucket>/face-ingest/images/*`** 的 CAM 子账号，把它的密钥写进 `FACE_CAPTURE_COS_SECRET_ID/KEY`。这样即使密钥泄露也只能读图片目录，不能读原始报文、不能写、不能删；
  不推荐长期用主密钥回退（主密钥权限大得多），回退只是为了先跑通。该子账号与第 13.6 节只授 PutObject 的接收端子账号是两个不同的账号，**不要**给接收端子账号加 GetObject。
- 签名：主 API 用 `q-sign-algorithm=sha1` 的请求头签名（`Authorization`，只签 `host`，有效期 5 分钟，起点回拨 60 秒容忍时钟偏差），不生成也不外泄带签名的 URL；不跟随重定向。
- 改完 `.env` 后按主 API 的常规方式重建 `hb-api` 容器生效。需要紧急关闭查看：清空 `FACE_CAPTURE_COS_BUCKET` 并重建容器（接口随即返回 503），或在「角色管理」收回权限。

**权限入库（部署后手工执行一次）**：脚本 `services/backend/BlazorApp.Api/Data/Migrations/20261013_AddFaceCaptureImagesViewPermission.sql`，规则与 14.2 完全相同（幂等、目标库必须是 `HBweb`、只登记权限、**不授予任何角色或用户**、`CreatedBy` 写 `Migration_20261013_FaceCaptureImagesView`）：

```bash
sqlcmd -S <主库地址> -d HBweb -U <管理员账号> -I -b -i services/backend/BlazorApp.Api/Data/Migrations/20261013_AddFaceCaptureImagesViewPermission.sql
```

- 执行后核对：应看到 `InsertedPermissions` 为 1（重复执行为 0）和一行 `FaceCapture.Images.View`、`IsDeleted=0`。不执行的话「角色管理」里看不到该权限、授权保存时被静默丢弃（管理员本人不受影响）。
- 回滚：按 `CreatedBy = N'Migration_20261013_FaceCaptureImagesView'` 精确定位这一行；先在「角色管理」取消该权限已授予的账号，再把这一行 `IsDeleted` 置 1。**不要按权限码批量删除，也不要物理删除。** 回滚后仅管理员（Admin 角色放行）还能看图，如需连管理员也不可看须回滚代码或清空 `FACE_CAPTURE_COS_BUCKET`。

**页面行为与隐私要求**

- 抽屉里每行只有有权限且该行有图片时才出现「查看图片」；图片都还在接收服务本地的行显示灰色「图片在本地」。**列表不加载任何图片、没有缩略图、没有预加载**；点「查看图片」才打开弹框并按顺序逐张读取（背景图 → 人脸图）。
- 弹框顶部固定提示：「这是敏感的生物识别信息。查看会被记录，请勿截图外传。」背景图上用人脸框标出人脸位置（归一化坐标按比例，像素坐标按背景图原始宽高换算）。
- 浏览器端：图片以 Blob → `objectURL` 显示，弹框关闭、切换抓拍、页面卸载时立即撤销并清空引用；不写 `localStorage` / `sessionStorage` / 地址栏 / 控制台。
- 使用要求：仅限有业务需要的管理员查看；不得截图、下载、转发；定期核对被授予该权限的账号清单，离职 / 调岗及时收回。图片保留期由 COS 生命周期规则决定（13.6），到期后查看返回「图片文件不存在或已被清理」。

**审计（目前只有容器日志，没有持久化审计表）**

- 每次**成功读取**记一条 Information、每次**被拒绝的尝试**（无权限、编号不存在或归属不一致、图片在本地、未配置、COS 失败等）记一条 Warning，类别 `BlazorApp.Api.Features.FaceCameras.FaceCaptureImageService`，消息以「人脸抓拍图片查看审计」开头，字段：`Result`（成功为 `ok`，否则为上表错误码）、`ActorId`、`ActorName`、`CameraId`、`CaptureId`、`ImageId`、`Bytes`、`AtUtc`（UTC）。
  **日志里没有对象键、COS 地址、桶名、密钥、签名**。检索方式：`docker logs <hb-api 容器> 2>&1 | grep "人脸抓拍图片查看审计"`。
- Information 级别默认不进中心日志，只在容器日志里，容器重建后会丢；Warning 会进中心日志。**生物信息查看建议后续补持久化审计表**（谁、何时、看了哪张），目前不具备长期追溯能力，权限授予要从严。

## 16. 人脸比对：管理端

设计依据：`docs/design/face-matching-service-20261012.md` 第 13、14、15 节。本节只讲**主 API + Web 后台**这一侧（门店档案与闸门、员工同意与登记、客流统计、不受欢迎人员名单、复核、后台任务）；
人脸接收服务里的「按档案模式搜索、建访客、写计数」是另一侧，见接收端自己的章节。**整套功能默认全部关闭**：没有门店档案、没有腾讯云管理凭据时，新代码上线不改变任何现有行为（摄像机后台、查看抓拍图片、Capture 试点都照旧）。

> 合规提醒（不是法律意见）：这些合规记录只是**记录**，不能替代法律判断。对真实顾客运行（Match 模式、顾客去重、不受欢迎人员名单）前，隐私影响评估、门口告示、律师确认、跨境披露审查必须真实完成；
> 默认处理地域是中国大陆（`ap-guangzhou`），每张送检人脸（包括没匹配上的顾客）都在大陆处理，这是比新加坡更重的跨境披露【待律师确认】。
>
> **2026-10-13 起这些合规记录从「阻塞闸门」改为「警告」**（用户明确决定，风险由用户承担）：Match 下缺 PIA、告示、律师确认、跨境审查、NSW 员工通知、去重 / 名单专项记录，**不再阻止保存和启用**，
> 但缺口始终可见（后台红色警告、门店列表「合规记录未齐」标记、审计 `profile.update` 的 `warnings=N`、接收端 Warning 日志）。**法律上，这些记录对真实顾客运行前仍然必须完成；警告不是豁免。**

### 16.1 入口、权限与接口

| 项目 | 值 |
| --- | --- |
| 页面 | Web 后台 → 系统 → **人脸比对**，路由 `/system/face-match`；页签按权限显示：门店档案、员工同意与登记、客流统计、不受欢迎人员名单、复核任务、误报统计、审计 |
| 接口 | `/api/react/v1/face-match/...`，所有响应 `Cache-Control: no-store`；图片只经服务端转发（`Content-Type` 只会是 `image/jpeg` / `image/png`，`nosniff`），浏览器永远拿不到 COS 地址 |
| 管理员 | Admin 角色靠角色放行；下面五个权限**都不挂任何角色模板**，其他账号只有被管理员在「角色管理」里显式授予才可见，五个码互相独立 |

| 权限码 | 能做什么 |
| --- | --- |
| `FaceMatch.Manage` | 门店档案与闸门、员工同意（含同意书扫描件）、员工登记与撤回、审计查询 |
| `FaceMatch.Traffic.View` | 客流统计页（只有计数，没有任何生物信息）；非管理员只能看自己所属门店 |
| `FaceMatch.Unwelcome.Manage` | 提交、查看、撤回不受欢迎人员名单条目（含参考照片上传）、录入申诉说明 |
| `FaceMatch.Unwelcome.Approve` | 批准条目；非管理员的批准人必须不同于提交人，**管理员一个人批准即可生效**（可批准自己提交的条目） |
| `FaceMatch.Unwelcome.Review` | 收到命中提示、人工复核（一个人就够）、**服务端转发查看命中图与参考照片**（每次查看写持久审计）；非管理员只能处理自己所属门店的任务 |

接口一览（`GET /config` 任一权限可读；`unwelcome/entries` 列表需 Manage 或 Approve；`unwelcome/stats` 需三个名单权限任一）：

| 方法与路径 | 说明 |
| --- | --- |
| `GET /config` | 功能开关（`enabled` / `disabledReason`）、当前处理地域、私有桶是否已配置、`highBandScore`、本账号权限标志 |
| `GET /profiles`、`GET/PUT /profiles/{storeCode}`、`POST /profiles/{storeCode}/check` | 门店档案；PUT 校验阻塞项（409）并报告合规记录警告；check 只算「哪些项阻塞 / 哪些记录有警告」不保存 |
| `GET /staff?storeCode=` | 该门店的启用员工（登记用候选） |
| `GET/POST /consents`、`POST /consents/{id}/withdraw` | 同意记录（multipart，文件字段 `scan`）与撤回 |
| `GET/POST /enrollments`、`POST /enrollments/{id}/withdraw` | 员工登记（multipart，文件字段 `photos`，3–5 张）与撤回 |
| `GET /traffic?store&from&to` | 客流（按门店本地营业日；单日带分小时），**唯一访客数为估算** |
| `GET /audit` | 持久审计查询 |
| `GET/POST /unwelcome/entries`、`POST …/{id}/approve`、`…/withdraw`、`…/dispute-note` | 名单条目 |
| `GET /unwelcome/tasks`、`POST …/{id}/review`、`GET …/{id}/image`、`GET …/{id}/reference/{index}` | 复核任务、一人复核、命中图、条目参考照片 |
| `GET /unwelcome/stats` | 误报统计（按条目与相似度区间的驳回率） |

### 16.2 配置变量（服务器 `master-Vite/.env`，主 API 的 compose 环境变量）

**全部带 `:-` 默认值，没有这些变量的主 API 部署不会失败。**

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `FACE_MATCH_TENCENT_REGION` | `ap-guangzhou` | 腾讯云人脸识别处理地域，只允许 `ap-guangzhou` / `ap-shanghai` / `ap-singapore`（精确匹配，区分大小写）。**缺省、为空或不在白名单 → 功能整体关闭，绝不回退到别的地域** |
| `FACE_MATCH_ADMIN_SECRET_ID` / `FACE_MATCH_ADMIN_SECRET_KEY` | 空 | **密钥**。腾讯云 CAM「管理子账号」的密钥。缺任何一个或格式非法 → 功能整体关闭；**绝不回退到 `TENCENT_SECRET_*` 主密钥**，也与接收端搜索账号的密钥（`FACE_MATCH_TENCENT_SECRET_ID/KEY`）不是同一个 |
| `FACE_MATCH_CAPTURE_RETENTION_DAYS` | `7` | `FaceCaptureEvent` / `FaceCaptureImage` 行的保留天数（后台任务③）；小于 1 视为 7 |
| `FACE_MATCH_ENROLLMENT_QUALITY_CONTROL` | `2` | 员工登记照 / 名单参考照片的腾讯云质量控制等级（0 不控制 … 4 很高）；非法值回到 2 |
| `FACE_MATCH_UNWELCOME_HIGH_BAND_SCORE` | `90` | 复核页「较高」档位的相似度下限（0–100）。**试点标定前的偏严占位值**，标定后改这里；页面本身不写死阈值 |
| `FACE_MATCH_BACKGROUND_ENABLED` | `true` | 后台任务总开关（紧急停用用）；功能关闭时各任务本来就是空转 |

「功能关闭」时的表现：`GET /config` 返回 `enabled=false` 与原因（`region-missing` / `region-not-allowed` / `credentials-missing` / `credentials-invalid`）；
登记、批准入名单等需要调用腾讯云的接口返回 **503 `FACE_MATCH_DISABLED`**；Web 页面仍可查看，写入按钮禁用；后台任务不发任何腾讯云请求。
**改地域**会让所有门店已保存的跨境披露审查记录被清空（16.3 / 16.7 任务⑦；不再关闭档案，缺口显示为警告），须重新审查。

**私有对象存储（同意书扫描件、员工登记照、名单参考照片、命中图的读取）**沿用 `FaceCapture:Cos`（`FACE_CAPTURE_COS_BUCKET` / `_REGION` / `_SECRET_ID` / `_SECRET_KEY`，见 14.8）：
桶与区域没有默认值、绝不回退主桶；密钥缺省时回退 `TENCENT_SECRET_*`（这是 COS 的既有做法，与 IAI 管理凭据无关）。未配置 → 上传类与看图类接口返回 **503 `FACE_MATCH_STORE_NOT_CONFIGURED`**。
本功能的对象键前缀：

| 前缀 | 内容 | 需要的 COS 权限 |
| --- | --- | --- |
| `face-match/consent/{consentId}/` | 同意书扫描件（撤回同意时**保留**，作为同意与撤回的证据） | PutObject |
| `face-match/enrollment/{enrollmentId}/` | 员工登记照（撤回即删） | PutObject、DeleteObject |
| `face-match/unwelcome/{entryId}/` | 名单条目参考照片（撤回 / 到期即删） | PutObject、DeleteObject、GetObject（批准时读出发往腾讯云、复核页并排显示） |
| 命中图（接收端写入，键由接收端决定） | 命中抓拍，保留期默认 7 天 | GetObject（复核页）、DeleteObject（到期删除） |

> **注意：14.8 推荐的「只授 `cos:GetObject` 于 `face-ingest/images/*`」的专用只读子账号不够用**。要么给它**追加**一条语句
> `cos:PutObject`、`cos:DeleteObject`、`cos:GetObject`，资源限定 `qcs::cos:<region>:uid/<appid>:<bucket>/face-match/*`（命中图若不在 `face-match/` 下，再追加它所在前缀的 GetObject / DeleteObject），
> 要么单独新建一个主 API 专用子账号。**不要**给接收端的「只写」子账号加 Get / Delete。命中图的键必须是安全字符的相对路径，
> 且不会落在上面 `consent` / `enrollment` / `unwelcome` 三个前缀下（后台删除与看图都会拒绝，防止借命中任务读写别的照片）。

### 16.3 门店档案与启用闸门（后端强制）

模式：`0 Off`（默认）、`1 Capture`（只拍知情同意的内部人员，必填 `AllowedSubjects`）、`2 Match`（生产）。档案不存在或读取失败，接收端一律按 `Off`。

闸门报告（`GET /profiles/{storeCode}`、`POST .../check`、保存响应与 409 的 `details`）每个 facet 有两个列表：**`missing` = 阻塞保存的项**（2026-10-13 起**目前没有任何项会进入 `missing`**，保留给将来再出现的硬性前提），**`warnings` = 不阻塞但必须始终可见的提示**（合规记录缺口，以及下面表里的结构 / 功能性问题）；
报告另有 `canSave`（只看 `missing`）与 `hasWarnings`；门店列表每行有 `gateWarningCount`（各 facet 警告条数之和，同一字段被多个 facet 要求时分别计数）。

**下列结构性 / 功能性问题原来是阻塞项，现在也只给警告（用户 2026-10-13 决定「有问题只给提示」），保存照常成功：**

| 要启用的东西 | 提示项（`warnings`），仍会显示但不拦截 |
| --- | --- |
| `Capture` | `allowedSubjects`（必须声明只拍知情的内部人员）；`EvaluateMatch`（标定评估）只允许在 Capture 下（`evaluateMatchRequiresCapture`） |
| 员工排除 `StaffExclusionEnabled` | 模式不是 Off（`modeOff`），且该门店成员中存在有效的同意与登记（`activeEnrollment`） |
| 顾客去重 `TrafficDedupEnabled` | 模式不是 Off（`modeOff`） |
| 不受欢迎人员名单 `UnwelcomeListEnabled` | 模式不是 Off（`modeOff`）；Capture 下另需 `allowedSubjects`（由 capture facet 强制） |

字段格式 / 取值校验（400 `FACE_MATCH_FIELD_INVALID`）不变。

**变为警告（不阻塞保存和启用，进入 `warnings`；2026-10-13 用户决定）：**

| facet | 条件 | 警告项（`warnings`） |
| --- | --- | --- |
| `match` | `Mode=Match` | `piaReference`、`signageInstalledOn`、`legalSignoffBy`、`legalSignoffAt`；**跨境披露审查**：`crossBorderRegion`、`regionMismatch`（审查地域 ≠ 当前配置地域，或功能未开启）、`crossBorderReviewRef`、`crossBorderReviewedBy`、`crossBorderReviewedAt`；**NSW 门店另有** `staffNoticeDate`、`staffNoticeTooRecent`（员工监控通知日期距今不足 14 天） |
| `trafficDedup` | `Mode=Match` | `dedupLegalSignoffBy`、`dedupLegalSignoffAt` |
| `unwelcome` | `Mode=Match` | `unwelcomeAssessmentRef`、`unwelcomeLegalSignoffBy`、`unwelcomeLegalSignoffAt`、`piaReference`、`signageInstalledOn`、`staffTrainingDate`、`privacyContact`，以及跨境审查全套 |

- **Capture 下这些记录本来就不要求，也没有警告**：只处理知情的内部人员（已填 `AllowedSubjects`）时，去重和名单不要求专项记录，用于上线前用内部员工照片做端到端测试（#761）；切到 Match 时警告重新计算。
- **保存时的留痕**：保存成功且带警告时，审计 `profile.update` 的 `Result` 写 `mode=N;warnings=M`（`M` 为各 facet 警告之和，最坏约 22；列最长 32 字符）；没有警告保持 `mode=N`；被阻塞项拒绝仍是 `gate-not-met`。
- **页面**：每个被请求的 facet 下另有一块红色提示「还有 N 项合规记录未补齐。不会阻止启用，但对真实顾客运行前必须完成」；有警告时保存按钮旁常驻红字；门店概览表在「已启用的功能」旁显示红色标记「合规记录未齐（N）」。
- **跨境披露审查的地域应当等于当前配置的处理地域**（不等 → `regionMismatch` 警告）。配置地域被改动后：档案页显示审查无效；后台任务⑦把所有地域不符的档案**只清空审查记录**（地域、审查编号、审查人、审查时间）并写审计 `profile.region-invalidated`（`Result=review-cleared`）——
  **不再强制关闭任何档案**，`Mode` 与各开关原样保留，该店的跨境审查显示为未补齐的警告，须重新审查并补录。
- 关闭永远可以：保存 `Off`（以及把开关取消）不需要任何前置条件。
- 门店时区：档案 → 门店表 → 州推导（NSW→Sydney、QLD→Brisbane）→ `Australia/Sydney`；用于客流按本地营业日归日、访客库午夜删除。

### 16.4 员工同意与登记

1. Web「员工同意与登记」→ 选门店与员工 → 记录同意：同意书版本、签署时间、方式（纸质必须上传扫描件 pdf/jpg/png ≤10MB；电子签可不传）。NSW 员工必须填书面通知日期且**距今满 14 天**，不满拒绝。
2. 登记：选有效（未撤回）同意，上传 **3–5 张** jpg/png（单张 ≤5MB，按字节头判定类型）。每个员工同时只能有一条有效登记（库里过滤唯一索引兜底）。
   流程：创建 `staff` 人员库（幂等）→ 登记照存私有桶 → 腾讯云建人（`PersonName` 只是登记编号，**不含姓名 / 员工编号**）→ 追加其余照片 → 写库 → 审计。任一步失败会回滚（DeletePerson + 删已上传照片），不留半成品。
3. 撤回登记 / 撤回同意：先 `DeletePerson`，成功后标记已撤回并删登记照。**腾讯云删不掉时登记保持有效并如实报错（502 `FACE_MATCH_WITHDRAW_PENDING`），不会谎报已撤回**；
   撤回同意时同意**先**标记撤回，再逐个处理该同意下的登记，处理不了的页面显示「待删除」，后台任务⑥会继续重试（凭据缺失时同样）。
4. 不愿登记的员工仍会被拍到并计为「非员工」，需要给替代办法（见设计第 7 节）；撤回无任何不利后果。

### 16.5 不受欢迎人员名单（原「黑名单」，默认关闭、暗发布）

- **事件类别是枚举**：`ViolenceOrThreat`、`OrganisedOrRepeatTheft`、`Other`；**没有「一般偷窃」**。`Other` 必须填写说明；非管理员批准时需要**两位不同的批准人**（都不能是提交人），**管理员一个人批准即可**（2026-10-13 用户决定，见下）。
- 证据引用必填（事件记录编号或警方报告编号，不填个人隐私细节）；到期日必填，**上限 12 个月**；到期前 30 天提醒复核；生效门店至少一个，非管理员只能为自己所属门店提交。
- 流程：提交（Manage）→ 参考照片存私有桶 `face-match/unwelcome/{entryId}/`，**此时不会调用腾讯云** → 批准（Approve；非管理员须提交人 ≠ 批准人，**管理员一个人批准即可生效，可批准自己提交的条目，自批 / 单人批准在审计里记 `active;self-approved` / `active;admin-solo`**；命中后的复核另行规定（见 16.6，现在一个人就够）**不受影响**）→
  最后一位批准人通过时才读出参考照片、创建 `unwelcome` 人员库（幂等）并建人，条目变 Active。**批准要求至少一个生效门店已开启名单（`UnwelcomeListEnabled`，只看这个开关；专项评估 / 律师确认等记录缺口在门店档案里是警告，不在批准时拦截），否则 409 `FACE_MATCH_UNWELCOME_NOT_ENABLED`——开关没开，照片不会发往腾讯云。**
- 撤回 / 到期：**先把状态改成 Withdrawn / Expired**（接收端只用 Active 条目，立即停止匹配），再 `DeletePerson` 并删参考照片；删不干净的（条目上 `cleanupPending=true`）由后台任务④继续重试，参考照片键清空即清理完成。
- 申诉 / 更正：条目上有「申诉说明」（`dispute-note`）；被识别者查询或更正须在 30 天内处理，固定流程由门店 / 法务制定。

### 16.6 命中提示与一人复核

- 接收端命中（相似度 ≥ 名单阈值）写 `FaceMatchTask`（`NotifiedAtUtc` 为空）。**接收端不能调用通知平台**；主 API 后台任务⑥轮询 `NotifiedAtUtc IS NULL` 的任务，提交后经通知平台发给**该店持有 `FaceMatch.Unwelcome.Review` 的人**，
  `DedupeKey = unwelcome-hit:{TaskId}`，**发布在业务事务之外**（通知发布器自带事务，不能被外层事务包住）、每条单独 try/catch，失败只记日志、下一轮重试。
  文案固定且**只用英文**（通知文案规则）：标题 `Possible match alert`，正文 `Possible match (similarity NN%). The score is not the probability that this is the same person; a reviewer must check it.`，不含姓名与事件细节。暂无接收人的任务在命中图到期前继续重试；已复核结束或图片已到期的直接标记已处理。
- 复核页并排显示现场抓拍与条目参考照片，显示「相似度 NN%（不等于是同一个人的概率）」与「较高 / 一般」档位（后端按 `FACE_MATCH_UNWELCOME_HIGH_BAND_SCORE` 划分），**不显示姓名与事件细节**。
- **复核一个人就够（2026-10-13 用户决定，原设计要两人）**：任何持有 Review 权限、在门店范围内的人判断一次即结案——「是同一个人」→ `Confirmed`（命中图按原保留期保留），「不是」→ `Dismissed`，并把命中图提前到期（后台删除，只保留元数据与分数用于误报统计）。不再有第二步，也不再要求第二人不同于第一人；接口 `nextStep` 只返回 `first` / `none`，错误码 `FACE_MATCH_SAME_REVIEWER` 不再产生。
  **旧数据**：两人复核时代留下的「已一审、仍 Pending」任务，下一位复核人（含第一复核人本人）提交一次即结案，结案人写进 `SecondReviewerId`，审计 Result 为 `legacy-second:{verdict}`；新任务的审计 Result 为 `first:{verdict}`。
  **系统不自动拒绝服务、不自动通知警方**，店里怎么处置由门店流程与法务制定。命中图已被删除的任务不能再复核。
- 看图（命中图 / 参考照片）：对象键只来自数据库、门店范围内才可见（越权与不存在共用 404）、仅持有 Review 权限者；**每次查看（含被拒绝的尝试）写持久审计**，且审计写不进去就不返回图片字节。
- 误报统计：按条目的命中数 / 驳回率（已结束任务 ≥5 条且驳回率 ≥80% 提示复核该条目是否应出名单），以及按相似度每 5 分一档的驳回率，用于调阈值。

### 16.7 后台任务（`FaceMatchBackgroundService`，节拍 60 秒，可注入时间，默认安全）

| 任务 | 间隔 | 做什么 | 空转条件 |
| --- | --- | --- | --- |
| ① 访客库清理 | 5 分钟 | 门店本地营业日结束（本地次日 0 点）或接收端写的 `DeleteAfterUtc` 先到者即 `DeleteGroup`，更新 `FaceVisitGroup.DeletedAtUtc`；另每 6 小时扫描腾讯云里遗漏的过期访客库（命名 `v{门店}_{yyyyMMdd}`、早于两天、且库表登记过或门店确实存在才删，绝不碰 `staff` / `unwelcome` 或别的系统的库） | 无管理凭据不调用腾讯云；没有未删除的库时只是一条查询 |
| ② 员工库完整性 | 24 小时 | 对比腾讯云员工库人数与 `Status=Active` 的登记数，不一致写审计 `enrollment.integrity-mismatch` 并记 Warning | 无管理凭据，或没有门店启用 |
| ③ Capture 记录保留清理 | 1 小时 | 删除超过保留天数（默认 7）的 `FaceCaptureEvent` / `FaceCaptureImage` 行，分批（每轮约 1 万行）；只清理数据库行，COS 图片仍由接收端与桶生命周期清理 | **没有任何门店档案不是 Off 时不运行**（存量试点数据不会在功能未启用时被删） |
| ④ 名单条目 | 10 分钟 | 到期（Pending / Active）自动改 Expired 并 `DeletePerson` + 删参考照片；重试撤回 / 到期后没清理干净的条目；到期前 30 天经通知平台提醒提交人与批准人（`unwelcome-expiry:{EntryId}`） | 没有到期 / 待清理条目时只是查询 |
| ⑤ 命中图到期删除 | 10 分钟 | 删除 `ImageDeleteAtUtc` 已到（含已驳回提前到期）的命中图并把 `ImageObjectKey` 置空串；只删形态合法且不在管理端三个私有前缀下的键 | 私有桶未配置 |
| ⑥ 命中提示发布 + 撤回补偿 | 每个节拍 / 10 分钟 | 发布命中提示（见 16.6）；重试「同意已撤回但腾讯云删除未完成」的登记、补删已撤回登记遗留的登记照 | 命中提示只是一条索引查询 |
| ⑦ 地域改动作废审查 | 10 分钟 | 把审查地域 ≠ 当前配置地域的档案**只清空审查记录**并写审计 `profile.region-invalidated`（`Result=review-cleared`），**不再把任何档案关成 `Off`**（见 16.3） | 配置地域无效 / 功能未开启时什么都不动（配置写错不波及门店档案） |
| ⑧ 分时客流投影 | 10 分钟 | 把最近 3 个门店本地日的 `FaceTrafficBucket`（UTC 整点桶）重算成 `FaceTrafficHourly`（门店本地「日期 + 小时」，与营业额同口径，见 16.12）；进程启动后第一次成功投影之后顺带补齐一次历史 | **不调用腾讯云、不依赖管理凭据，也不依赖「有门店启用」**（功能没开启时也把已有计数桶投影出来，免得报表缺数据）；最近 4 天内没有计数桶时只是一条查询；`FaceTrafficHourly` 表不存在（迁移未执行）时记一次 Warning 后跳过，不报错 |

说明：已存在数据的**删除类补偿**（过期访客库、撤回后的清理、命中图到期）不依赖「有门店启用」，否则门店关闭后数据会无人清理；没有可删的行时它们只是廉价查询。

### 16.8 部署顺序与回滚

1. **迁移**：主 API 带 `--schema=migrate` 显式执行 `20261014.001-face-match`（建九张表：`FaceStoreProfile`、`FaceConsentRecord`、`FaceEnrollment`、`FaceVisitGroup`、`FaceTrafficBucket`、`FaceMatchEvaluation`、`FaceAuditLog`、`FaceUnwelcomeEntry`、`FaceMatchTask`；幂等，签名校验通过才登记账本）。
   **API 启动只做只读 schema 检查，不迁移；账本里没有这条迁移时主 API 启动失败（容器退出并打印诊断码），所以必须先迁移、再部署新版本的主 API**（与其他版本号迁移相同）。
   **分时客流表**：`20261016.001-face-traffic-hourly`（只建 `FaceTrafficHourly` 一张表，见 16.12）与上面同一次 `--schema=migrate` 一起执行，同样须先迁移、再部署主 API；没有新环境变量、没有新权限码、没有新接口。
2. **权限入库 SQL**（手工执行一次，规则同 14.2）：`services/backend/BlazorApp.Api/Data/Migrations/20261014_AddFaceMatchPermissions.sql`（登记五个权限码，幂等，`CreatedBy = Migration_20261014_FaceMatch`，不授予任何角色）。
   ```bash
   sqlcmd -S <主库地址> -d HBweb -U <管理员账号> -I -b -i services/backend/BlazorApp.Api/Data/Migrations/20261014_AddFaceMatchPermissions.sql
   ```
   首次执行 `InsertedPermissions` 应为 5（重复执行为 0）。回滚：按 `CreatedBy` 精确定位，先在「角色管理」取消授予，再把这几行 `IsDeleted` 置 1，**不要按码批量删除**。
3. **接收端受限账号追加授权**（手工、不由代码执行，在第 2 步迁移之后；`face-ingest-db-login.sql` 须已执行过）：`services/backend/SqlScripts/face-match-db-grants.sql`。
   ```bash
   export MAIN_DATABASE=HBweb
   sqlcmd -S <主库地址> -U <管理员账号> -I -b -i services/backend/SqlScripts/face-match-db-grants.sql
   ```
   追加的授权：`FaceStoreProfile` SELECT；`FaceTrafficBucket` SELECT / INSERT / UPDATE；`FaceVisitGroup` SELECT / INSERT / `UPDATE(VisitorCount)`；`FaceMatchEvaluation` INSERT；`FaceUnwelcomeEntry` SELECT；`FaceMatchTask` SELECT / INSERT。
   仍不授 DELETE、不授角色；`FaceConsentRecord` / `FaceEnrollment` / `FaceAuditLog` 接收端无权访问。缺表或缺用户即中止（52750 / 52751），可重复执行；回退见脚本末尾的 REVOKE 清单。
   接收端连接须保持 `QUOTED_IDENTIFIER ON`（SqlClient 默认如此；`sqlcmd` 手工验证时要加 `-I`），因为这几张表有过滤索引。
4. **部署主 API + Web**（先后端后前端；顺序与 `hotbargain-vip-deploy` 一致）。此时没有任何门店档案、没有管理凭据：页面显示「功能未开启」，一切照旧。
5. **腾讯云与 `.env`**（16.9）→ 重建 `hb-api` 容器 → `GET /config` 应返回 `enabled=true` 与所选地域。
6. 在角色管理里授权需要的账号；然后按 16.3 给试点门店逐项补齐合规记录（缺口现在只是警告，不拦截，但对真实顾客运行前法律上必须补齐）再切模式。每一步都可回退：门店档案保存 `Off` 即可立刻停止接收端对该店的一切比对。
- **回滚代码**：回滚主 API / Web 即可；九张表、`FaceTrafficHourly` 表与权限行保留无害（没有任何调用方）。紧急停用：清空 `FACE_MATCH_ADMIN_SECRET_KEY` 并重建容器（接口 503、后台任务空转），或把相关门店档案改 `Off`，或 `FACE_MATCH_BACKGROUND_ENABLED=false`。
- **nginx 请求体大小**：上传接口（登记照最多 5×5MB、名单参考照片同）整体约 26MB，经 Nginx 到 `/api/` 的路径需 `client_max_body_size` ≥ 30m（Kestrel 默认上限 30,000,000 字节）。

### 16.9 腾讯云管理子账号与凭据写入（需账号持有人在控制台完成，不要把密钥贴进对话）

1. 开通人脸识别服务并接受服务条款（账号持有人）；确认所选地域已开通（新加坡在当前中国站账号下未开通，探测返回 `ResourceUnavailable.NotExist`「计费状态未知」）。
2. 新建 CAM 子账号「主 API 管理账号」（只勾**编程访问**），绑定下面的最小权限策略（与接收端搜索账号是**两个不同**的子账号）：
   ```json
   {
     "version": "2.0",
     "statement": [
       {
         "effect": "allow",
         "action": [
           "iai:CreateGroup",
           "iai:CreatePerson",
           "iai:CreateFace",
           "iai:DeletePerson",
           "iai:DeleteGroup",
           "iai:GetPersonList",
           "iai:GetGroupList"
         ],
         "resource": "*"
       }
     ]
   }
   ```
   主 API 只调用这七个接口；不授 `DeleteFace`、`GetGroupInfo` 等其他管理动作。
3. 密钥只写服务器 `master-Vite/.env`，**输入时不回显**，写完立刻清掉变量，不进 shell 历史、聊天或工单：
   ```bash
   cp -p master-Vite/.env master-Vite/.env.bak-$(date +%Y%m%d%H%M%S)
   read -rs -p "AdminSecretId: " FM_ID; echo; read -rs -p "AdminSecretKey: " FM_KEY; echo
   printf 'FACE_MATCH_TENCENT_REGION=ap-guangzhou\nFACE_MATCH_ADMIN_SECRET_ID=%s\nFACE_MATCH_ADMIN_SECRET_KEY=%s\n' "$FM_ID" "$FM_KEY" >> master-Vite/.env
   unset FM_ID FM_KEY
   chmod 600 master-Vite/.env
   ```
   若 `.env` 里已有同名变量，先删旧行再追加（避免重复定义）；然后按主 API 的常规方式重建 `hb-api` 容器。
4. 给 14.8 / 16.2 所述的 COS 子账号追加 `face-match/*` 的 Put / Delete / Get。
5. 向腾讯云**书面**确认设计第 9 节第 1、3 点（是否保存非库内图片、是否用于训练、能否关闭），并取得数据处理协议；这些是启用真实顾客功能的前置，系统只能记录，不能替你确认。

### 16.10 审计、日志与隐私

- 持久审计表 `FaceAuditLog`（保留期由律师定，起步建议 2 年）：谁、何时、什么动作、对哪个编号、结果。动作包括：`profile.update`、`profile.region-invalidated`、`consent.create`、`consent.withdraw`、`enrollment.create`、`enrollment.withdraw`、`enrollment.integrity-mismatch`、
  `unwelcome.entry.submit` / `approve` / `withdraw` / `expire` / `dispute-note`、`unwelcome.task.review` / `view-capture` / `view-reference` / `image-delete`、`visit-group.delete`、`capture.retention-cleanup`。
  **不含图片、对象键、密钥、姓名、事件细节**；被拒绝的看图尝试 `Result` 为错误码；后台任务的操作人是 `system`。
  `profile.update` 的 `Result`：`mode=N`（无合规记录警告）、`mode=N;warnings=M`（带着 M 条警告保存，留下「缺口下启用」的痕迹）、`gate-not-met`（被阻塞项拒绝）；
  `profile.region-invalidated` 的 `Result` 固定为 `review-cleared`（只清空审查记录，不再有 `disabled`）。
- 日志（容器日志 / 中心日志）同样不含密钥、签名、完整请求体、图片、对象键或腾讯云错误原文；腾讯云调用失败只记动作、HTTP 状态、错误码与 RequestId（可向腾讯云反查）。
- 腾讯云只会收到：照片、不透明的人员库 / 人员编号（`staff`、`unwelcome`、`v{门店}_{日期}`、`st_{登记编号}`、`uw_{条目编号}`）；**没有姓名、员工编号、事件类别或证据编号**。

### 16.11 与接收端的契约（列名与取值，改动前必须双方同步）

- 表结构只由主 API 迁移 `20261014.001-face-match` 创建，列名、类型、可空性以设计 13.3 / 14.4 为准；未标可空的列都是 `NOT NULL`。主 API 额外加的列只有 `FaceUnwelcomeEntry.CategoryNote` / `SecondApprovedBy` / `SecondApprovedAtUtc`（接收端不读）。
- 数值约定：`FaceStoreProfile.Mode` 0 Off / 1 Capture / 2 Match；`FaceUnwelcomeEntry.Status` 0 Pending / **1 Active** / 2 Expired / 3 Withdrawn（**接收端只能使用 Active 条目**）；`IncidentCategory` 1 / 2 / 3；
  `FaceMatchTask.ReviewStatus` 0 Pending / 1 Dismissed / 2 Confirmed（接收端建任务时写 0，`NotifiedAtUtc` 留空，`ImageObjectKey` / `ImageDeleteAtUtc` 必填）；`FaceEnrollment.Status` 1 Active / 2 Withdrawn。
- `FaceUnwelcomeEntry.StoreScope` 是英文逗号分隔、无空格的门店编码列表（如 `1013,2015`）；接收端命中后按 `TencentPersonId`（`uw_{EntryId}`）查条目，**条目不是 Active 或本店不在 StoreScope 内则忽略命中**。
- 接收端读 `FaceStoreProfile` 时：`Mode=Match` 下员工排除、去重、名单**不再**要求 `CrossBorderRegion` 等于接收端配置的处理地域、审查三列非空、名单专项记录齐全（2026-10-13 起这些是警告）；缺失时接收端只按门店节流记一条 Warning（`match-without-legal-records`），功能照常运行。
  接收端仍保留的是功能前提：匹配可用、名单需要私有 COS、去重需要可解析的时区。主 API 的后台任务⑦只清空地域不符的审查记录，不再关闭档案。
- 访客库 `FaceVisitGroup`：`GroupId` 即腾讯云库编号（`v{门店}_{yyyyMMdd}`），`BusinessDate` 为门店本地营业日，`DeleteAfterUtc` 不得早于本地营业日结束；`DeletedAtUtc` 只由主 API 写。
- 命中图键：接收端写入 `FaceMatchTask.ImageObjectKey`，必须是安全字符的相对路径（`[A-Za-z0-9_.-/]`，不含 `..`），**不得**落在 `face-match/consent/`、`face-match/enrollment/`、`face-match/unwelcome/` 下；建议 `face-match/hit/{门店}/{yyyy}/{MM}/{dd}/{TaskId}.jpg`。

### 16.12 客流分时表与营业额对齐（`FaceTrafficHourly`）

**为什么有这张表**：接收端把客流计数按 **UTC 整点**累加进 `FaceTrafficBucket`；而营业额（POSM `sales_order.OrderTime` → 主库 `HourlySalesStatistic`）是**门店本地墙钟时间**，悉尼 / 墨尔本有夏令时，
两者直接按小时 JOIN 会整体错位 1 小时。所以主 API 的后台任务⑧把计数桶换算成「门店 + 本地日期 + 本地小时」落到 `FaceTrafficHourly`，报表可以直接按这三列 JOIN 营业额。
本节只讲这张表和它的写入；报表本身不在此范围。迁移 `20261016.001-face-traffic-hourly` 只新建这一张表，不修改 `FaceTrafficBucket` 等任何现有表；接收端不读不写它；没有新增接口、权限码、环境变量。

| 列 | 类型 | 含义 |
| --- | --- | --- |
| `StoreCode` | `nvarchar(32)` | 门店编码（与 `Store.StoreCode`、`FaceTrafficBucket.StoreCode`、营业额 `HourlySalesStatistic.BranchCode` 同一套编码） |
| `StatDate` | `date` | 门店**本地**日期（墙钟，不是 UTC） |
| `StatHour` | `tinyint` | 门店**本地**小时 0–23（`CHECK` 约束兜底） |
| `CaptureCount` / `StaffMatchCount` / `UniqueVisitorCount` / `RepeatCount` / `LowQualityCount` / `ApiFailureCount` | `int NOT NULL`，默认 0 | 与 `FaceTrafficBucket` 同名列同含义，按本地小时求和 |
| `VisitCount` | `int NOT NULL`，默认 0 | **派生列**：`max(0, CaptureCount − StaffMatchCount − LowQualityCount − ApiFailureCount)` |
| `TimeZoneId` | `nvarchar(64) NOT NULL` | 换算所用的 IANA 时区（门店之后改了时区，历史行仍能追溯当时的依据） |
| `ComputedAtUtc` | `datetime2 NOT NULL` | 该行内容最近一次写入（新建或数值 / 时区变化）的时间；内容没变的重算不会改它 |

主键 `(StoreCode, StatDate, StatHour)`；另有 `IX_FaceTrafficHourly_StatDate_Store (StatDate, StoreCode)`，方便按日期区间扫全部门店。迁移的只读门禁错误号：`52820` 表缺失、`52821` 列签名不兼容、`52822` 主键 / 日期索引签名不兼容。

**口径（报表使用前必读）**

- **`VisitCount` = 非员工进店人次**：含当日重复出现的人（同一个人一天来三次算三次）。**`UniqueVisitorCount` = 该小时首次出现的当日新访客**：同一天各小时相加才是当日唯一访客数；两者口径不同，**不要相加**，也不要用 `UniqueVisitorCount` 当「进店人数」去除营业额。
- **`UniqueVisitorCount` 恒为估算值**：同一个人换角度 / 戴口罩可能被当成两个人，漏识别也会少算；当日访客库以门店本地营业日为界，跨日不去重。
- 受门店档案开关影响：**没开「顾客去重」的门店** `UniqueVisitorCount` / `RepeatCount` 恒为 0（`VisitCount` 仍有值）；**没开「员工排除」的门店** `StaffMatchCount` 恒为 0，此时 `VisitCount` 会把员工也算进去。
- **只有 `Match` 模式的门店才有计数桶，因此只有这些门店才有分时行**（`Capture` / `Off` 不写计数）。没有计数桶的门店不产生行；某小时没有任何抓拍也没有行（不补零行），报表要自己对齐营业时间。
- 保留策略：**永久保留**，没有清理任务；表里只有计数和时区，**没有任何生物信息**。数据量每店每天最多 24 行。

**夏令时（悉尼 / 墨尔本）**

- UTC 整点到本地整点用 .NET `TimeZoneInfo` 换算（不依赖服务器时区）。布里斯班无夏令时，恒为 UTC+10。
- **开始日**（如 2026-10-04，本地 02:00→03:00）：本地 02 点不存在，该日没有 `StatHour = 2` 的行，全天只有 23 个小时。
- **结束日**（如 2027-04-04，本地 03:00→02:00）：本地 02 点出现两次，两个 UTC 小时（`15:00Z` 与 `16:00Z`）的计数**相加**落在同一行 `StatHour = 2`——与营业额按 `OrderTime.Hour` 归并的口径一致，不是后写覆盖。

**重算窗口与幂等**

- 任务⑧每 10 分钟对每个最近 4 天内有计数桶的门店，重算「**最近 3 个门店本地日（含今天）**」涉及的全部小时：用窗口内全部 UTC 桶按（本地日期，本地小时）重新求和，与库里已有的行逐行对比，只写有变化的行（新增 / 整行覆盖 / 删除不再产生的行），一个事务内完成；内容没变的重算不写库。
  不依赖增量水位，所以接收端晚到的计数、门店改时区后的重算都自然正确。单个门店失败只记 Warning、下一轮重试，不影响其他门店。
- **门店改时区**：行里的 `TimeZoneId` 记当次换算所用时区。改时区后，重算窗口（最近 3 个本地日）内的行会被新时区**整体覆盖**（新时区下不再产生的行会被删除）；**窗口之外的历史行保持原样**，仍记着当时的时区，不会被追溯改写。这是有意的取舍：历史营业额已按当时的墙钟统计，不应因为之后改了门店配置而改变。
- **首次部署 / 迁移晚于计数开始**：进程启动后第一次成功投影之后，会补齐一次历史——某门店最早的计数桶比最早的分时行还早时，把缺失的日期补出来（该门店还没有任何分时行时补到今天）；已有分时行的日期不会被改写。之后不再重复。
- **API 停机超过 3 天后的中间缺口**不会被自动补，需要手工回填（下节）。

**手工回填（应急；常规情况不需要）**

`FaceTrafficHourlyProjector.RebuildAsync(storeCode?, fromDate, toDate)` 是与定期任务同一套逻辑的内部方法（区间内以重算结果为准，区间外不动，可重复执行），**按要求没有 HTTP / CLI 入口**（单元测试覆盖）。
需要在生产手工补一段历史时，用下面的 SQL（单店单时区，事务内「先删后插」，口径与应用一致：同一本地小时的多个 UTC 桶相加、`VisitCount` 夹到 0）。
`@WinZone`：悉尼 / 墨尔本用 `AUS Eastern Standard Time`，布里斯班用 `E. Australia Standard Time`；`@IanaZone` 必须是门店当前实际生效的时区（档案 → 门店表 → 州推导）。
先在测试环境或用 `SELECT` 改写预览，确认再执行；回补前备份该门店该区间的现有分时行（`SELECT … INTO` 一张临时备份表）。

<!-- sql:face-traffic-hourly-backfill -->
```sql
DECLARE @StoreCode nvarchar(32) = N'2001';
DECLARE @IanaZone  nvarchar(64) = N'Australia/Sydney';
DECLARE @WinZone   nvarchar(64) = N'AUS Eastern Standard Time';
DECLARE @FromDate  date = '2027-04-02';   -- 本地日期，含
DECLARE @ToDate    date = '2027-04-04';   -- 本地日期，含

SET XACT_ABORT ON;
BEGIN TRANSACTION;

DELETE FROM dbo.FaceTrafficHourly
WHERE StoreCode = @StoreCode AND StatDate >= @FromDate AND StatDate <= @ToDate;

INSERT dbo.FaceTrafficHourly
    (StoreCode, StatDate, StatHour, CaptureCount, StaffMatchCount, UniqueVisitorCount, RepeatCount,
     LowQualityCount, ApiFailureCount, VisitCount, TimeZoneId, ComputedAtUtc)
SELECT @StoreCode, l.StatDate, l.StatHour,
       SUM(b.CaptureCount), SUM(b.StaffMatchCount), SUM(b.UniqueVisitorCount), SUM(b.RepeatCount),
       SUM(b.LowQualityCount), SUM(b.ApiFailureCount),
       CASE WHEN SUM(b.CaptureCount) - SUM(b.StaffMatchCount) - SUM(b.LowQualityCount) - SUM(b.ApiFailureCount) > 0
            THEN SUM(b.CaptureCount) - SUM(b.StaffMatchCount) - SUM(b.LowQualityCount) - SUM(b.ApiFailureCount)
            ELSE 0 END,
       @IanaZone, SYSUTCDATETIME()
FROM dbo.FaceTrafficBucket AS b
CROSS APPLY (SELECT CONVERT(datetime2(0), (b.BucketStartUtc AT TIME ZONE 'UTC') AT TIME ZONE @WinZone) AS LocalTime) AS t
CROSS APPLY (SELECT CONVERT(date, t.LocalTime) AS StatDate, DATEPART(hour, t.LocalTime) AS StatHour) AS l
WHERE b.StoreCode = @StoreCode
  AND b.BucketStartUtc >= DATEADD(day, -1, CONVERT(datetime2, @FromDate))
  AND b.BucketStartUtc <  DATEADD(day, 2, CONVERT(datetime2, @ToDate))
  AND l.StatDate >= @FromDate AND l.StatDate <= @ToDate
GROUP BY l.StatDate, l.StatHour;

COMMIT TRANSACTION;
```

**与营业额按（门店，本地日期，小时）JOIN**

营业额的分时来源是**主库**（和 `FaceTrafficHourly` 同库）的 `dbo.HourlySalesStatistic`——已核实（代码依据）：

- 表 / 列：`HourlySalesStatistic(Date, Hour, BranchCode, BranchName, TotalAmount, TotalQuantity, OrderCount, CustomerCount, AverageOrderValue, UpdateTime)`，主键 `(Date, Hour, BranchCode)`（实体 `BlazorApp.Shared/Models/HBweb/Statistic/HourlySalesStatistic.cs`；`SalesDashboardReactService.RevenueSnapshot.SqlServer.cs` 在同一连接里同时读 `[dbo].[HourlySalesStatistic]` 与 `[dbo].[Store]`）。
- 来源与口径：`SalesStatisticsHourlyRefresher` 按 POSM `sales_order.OrderTime` 的 `.Date` / `.Hour` 分组写入——`OrderTime` 是**门店本地墙钟**（不是 UTC），所以 `Date` / `Hour` 与 `FaceTrafficHourly.StatDate` / `StatHour` 天然同口径，夏令时结束日本地 02 点的两个小时也已合并在 `Hour = 2`。
  `TotalAmount` = 支付明细金额合计，**含退款单（`Status = 3`，负数）即净额**；`OrderCount` 只数 `Status` 1 / 4 的订单。
- 门店编码对应：`sales_order.BranchCode`（缺失时经设备编码映射）就是 `Store.StoreCode`；`SalesStatisticsHourlyRefresher` 用 `Store.StoreCode` 取门店名；`BranchCode = 'ALL'` 是全店汇总行，按门店 JOIN 时必须排除（下面示例按 `<> N'ALL'` 排除）。
- 营业额统计可能没跑完（尤其今天）：以 `SalesStatisticRefreshState`（`StatisticType = 'HourlySales'`、`Date`、`Status`；`Fresh` 才算完整）为准。
- POSM 原始表（`sales_order` / `payment_detail` / `sales_order_detail`，租户 `HBPOSM`）在**另一个数据库**，跨库 JOIN 既慢又重复统计口径，报表应用主库里的 `HourlySalesStatistic`。

**未核实**（没有连生产库）：生产里 `HourlySalesStatistic.Date` 的实际列类型（实体没指定类型，按 CodeFirst 应是 `datetime`，示例用 `CONVERT(date, …)` 与范围过滤，`date` / `datetime` 都能跑）；生产里两张表 `StoreCode` / `BranchCode` 的排序规则是否一致（同库默认排序规则，应一致）；生产数据的逐店抽样对照（门店编码是否都对得上）。
下面的示例已在 SQL Server 2022 容器上对 CodeFirst 建出的 `HourlySalesStatistic` 与迁移建出的 `FaceTrafficHourly` 实测通过（`FaceMatchSqlServerTests`）。

<!-- sql:face-traffic-hourly-join -->
```sql
DECLARE @FromDate date = '2026-10-01';   -- 本地日期，含
DECLARE @ToDate   date = '2026-10-14';   -- 本地日期，含

WITH traffic AS (
    SELECT StoreCode, StatDate, StatHour, VisitCount, UniqueVisitorCount
    FROM dbo.FaceTrafficHourly
    WHERE StatDate >= @FromDate AND StatDate <= @ToDate
), sales AS (
    SELECT BranchCode AS StoreCode, CONVERT(date, [Date]) AS StatDate, [Hour] AS StatHour,
           TotalAmount, OrderCount
    FROM dbo.HourlySalesStatistic
    WHERE [Date] >= @FromDate AND [Date] < DATEADD(day, 1, @ToDate)
      AND BranchCode IS NOT NULL AND BranchCode <> N'ALL'
)
SELECT COALESCE(t.StoreCode, s.StoreCode) AS StoreCode,
       COALESCE(t.StatDate, s.StatDate)   AS StatDate,
       COALESCE(t.StatHour, s.StatHour)   AS StatHour,
       COALESCE(t.VisitCount, 0)          AS VisitCount,
       COALESCE(t.UniqueVisitorCount, 0)  AS UniqueVisitorCount,
       COALESCE(s.TotalAmount, 0)         AS TotalAmount,
       COALESCE(s.OrderCount, 0)          AS OrderCount,
       CASE WHEN t.VisitCount > 0
            THEN CONVERT(decimal(9, 4), COALESCE(s.OrderCount, 0) * 1.0 / t.VisitCount) END AS OrdersPerVisit
FROM traffic AS t
FULL OUTER JOIN sales AS s
    ON s.StoreCode = t.StoreCode AND s.StatDate = t.StatDate AND s.StatHour = t.StatHour
ORDER BY StoreCode, StatDate, StatHour;
```

读法：`FULL OUTER JOIN` 保留「有客流没成交」和「有成交没抓到客流」两种小时；`OrdersPerVisit`（订单数 / 到店人次）只是示意——`VisitCount` 含重复到店，不是转化率的严格分母。

## 17. 人脸比对：接收端

设计依据：`docs/design/face-matching-service-20261012.md` 第 5、8、13、14、15 节（**合规部分未经律师确认；2026-10-13 起设计里的各道合规闸门在 Match 下改为非阻塞警告，但对真实顾客运行前这些记录法律上仍然必须完成**）。
本节只讲接收端（`BlazorApp.FaceIngest`）：按门店档案决定每条抓拍怎么处理、调用腾讯云人脸识别（IAI）做搜索、只累加计数、不留生物数据。
门店档案、员工登记、统计页、访客库清理、名单条目与复核由**主 API / Web** 负责，不在本节。

### 17.1 一句话与默认状态

- **默认全部关闭**。没有配置腾讯云凭据、也没有任何门店档案时，接收端与 P1 的行为完全相同（含 `Capture` 式的全部留存），`/health` 的 `matcherEnabled=false`。
- 接收端点（P0）的鉴权、落盘、响应码**一概不变**；比对只发生在后台处理器里，腾讯云慢 / 挂不影响摄像机收到 200。
- 腾讯云凭据只认 `FACE_MATCH_TENCENT_SECRET_ID/KEY` 这一对；**绝不**使用 COS 密钥（`FACE_INGEST_COS_*`）、主密钥（`TENCENT_*`）或任何别的密钥顶替；缺失时匹配整体关闭。

### 17.2 三种模式的行为

门店模式来自库表 `FaceStoreProfile.Mode`（0 Off、1 Capture、2 Match），接收端每 30 秒整表读一次。

| 模式 | 调用腾讯云 | 本地报文 / 图片 | COS | 事件行（`FaceCaptureEvent/Image`） | 计数 |
| --- | --- | --- | --- | --- | --- |
| `Off` | 否 | **读完元数据即删**（`.json`、`.bin`、各类标记全删） | 不上传 | 不写 | 不写 |
| `Capture` | 否；`EvaluateMatch=1` 且匹配可用时，入库成功后**额外**搜一次，把分数写 `FaceMatchEvaluation`；**开了名单（见 17.4）时**另对每张人脸只搜 `unwelcome` 库（最低分 = 名单阈值），命中建 `FaceMatchTask` 并存命中图（私有 COS） | 与 P1 完全相同（存图、写 `.processed`，按保留期清理） | 按 P1 配置 | 照常写 | 不写 |
| `Match` | 是（见下） | 处理完（无论结果）**全部删除**，不留 `.processed` 等任何残留 | **不上传**；唯一例外是名单命中任务的人脸图 | **不写** | 只写 `FaceTrafficBucket`（整点桶累加） |

`Match` 的处理顺序（每张人脸一次 `SearchFacesReturnsByGroup`，`MaxFaceNum=1`，Base64 上传，`QualityControl=2`）：

1. 同摄像机、同请求体哈希在 `DuplicateBodyWindowSeconds`（300 秒）内重复 → 不搜索、不计数（断网续传的重复推送）。
2. 员工排除开启且该摄像机 `StaffSuppressSeconds`（30 秒）内刚命中过员工 → 直接计 `StaffMatchCount`，不搜索。
3. 质量闸门（本地免费）：人脸图最短边 < `Quality:MinEdgePixels`（80）或体积 < `Quality:MinBytes`（4096 字节）或解析不出宽高 → `LowQualityCount+1`，不调用腾讯云。
4. 一次搜索，groups 按档案开关：员工排除 → `staff`；去重 → 当日访客库 `v{店}_{yyyyMMdd}`；名单 → `unwelcome`。
5. 判定：员工命中（≥ 员工阈值）→ `StaffMatchCount+1`；否则名单命中（≥ 名单阈值，且条目未过期、门店在范围内）→ 建 `FaceMatchTask`（见 17.8）；
   去重开启时访客命中（≥ 访客阈值）→ `RepeatCount+1`，无命中 → 在当日访客库建一人并 `UniqueVisitorCount+1`、`FaceVisitGroup.VisitorCount+1`；
   没开去重 → 只有 `CaptureCount`（「非员工」= 抓拍数 − 员工 − 低质量 − 调用失败）。
6. 腾讯云调用失败：瞬时错误 / 限频指数退避重试（默认最多 3 次、起步 500ms、封顶 8s，限频起步 ×4），超限则**丢弃并 `ApiFailureCount+1`**，不因失败而留存图片。
   质量类错误（无人脸等）不重试，计低质量。账号级错误（`AuthFailure.*`、`ResourceUnavailable.*` 欠费 / 未开通 等）告警、不重试，并**暂停调用 60 秒**（期间直接丢弃并计失败）。
7. 数据库写失败（计数桶 / 访客库行 / 任务）：按处理器退避重试，最多 3 次或本地滞留超 15 分钟，之后**丢弃本条**并记 Error（`dropped after repeated failures`），不会进入 `.failed` 把图片留到保留期。

其它口径：

- **同一家店串行**（每店一把锁）：相邻两条抓拍不会同时判成「新访客」而重复建人；不同店互不阻塞。
- `CaptureCount` 按**人脸张数**计（一条报文通常只有一张）。没有人脸图的报文（移动侦测、解析失败）只丢弃，不计数。
- 访客库营业日 = 抓拍接收时刻在门店时区的本地日期；`FaceVisitGroup.DeleteAfterUtc` = 营业日次日本地 02:00 对应的 UTC。
  处理时已过该时间的**迟到积压抓拍**不再建库建人（只计抓拍数），避免重建一个没人清理的库。
- 访客 `PersonId` 由抓拍编号确定性哈希生成，重试遇到「已存在」视为成功，不会因重试多建人；访客没有姓名、性别、描述字段。
- 接收端的腾讯云客户端只允许操作 `v{店}_{yyyyMMdd}` 形式的访客库；员工库、名单库的写入在代码里被拒绝（设计第 9 节第 10 点）。

### 17.3 配置变量（`.env`，全部有默认值）

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `FACE_MATCH_TENCENT_REGION` | `ap-guangzhou` | 腾讯云地域，白名单 `ap-guangzhou` / `ap-shanghai` / `ap-singapore`。**不在白名单 → 匹配整体关闭**并在启动日志报 Error，不会回退到别的地域。改地域后所有门店的跨境审查记录被主 API 清空（16.3），该店显示合规记录警告，接收端不降级 |
| `FACE_MATCH_TENCENT_SECRET_ID` / `FACE_MATCH_TENCENT_SECRET_KEY` | 空 | **密钥**。接收端「搜索」子账号 `face-match-matcher`（17.6）。缺任一 → 匹配整体关闭 |
| `FACE_MATCH_STAFF_THRESHOLD` | `85` | 员工相似度阈值（0–100，偏严） |
| `FACE_MATCH_VISITOR_THRESHOLD` | `85` | 当日访客去重阈值（偏严） |
| `FACE_MATCH_UNWELCOME_THRESHOLD` | `95` | 名单阈值（很严）。**不得低于员工 / 访客阈值**，否则匹配整体关闭 |
| `FACE_MATCH_STAFF_SUPPRESS_SECONDS` | `30` | 员工命中后同摄像机抑制窗口；`0` 关闭 |
| `FACE_MATCH_MIN_EDGE_PIXELS` / `FACE_MATCH_MIN_FACE_BYTES` | `80` / `4096` | 质量闸门 |

不开放 `.env` 覆盖、沿用 appsettings 默认的项：重试次数 / 退避（`FaceMatch:Retry:*`）、熔断时长（`FatalCooldownSeconds=60`）、档案刷新（`ProfileRefreshSeconds=30`）、
Match 处理失败上限（`MatchMaxAttempts=3`、`MatchMaxLocalAgeMinutes=15`）、命中图保留（`Unwelcome:ImageRetentionDays=7`）。
**按门店覆盖阈值**需要改配置：在 `appsettings.json` 或额外挂载的配置里写 `FaceMatch:Stores:<门店编码>:StaffThreshold` / `VisitorThreshold` / `UnwelcomeThreshold`（环境变量写法 `FaceMatch__Stores__S1013__StaffThreshold`）。

任一取值非法（阈值不在 0–100、格式错误等）→ 匹配整体关闭，原因（只含键名）记在启动日志；**不会让容器起不来**，接收落盘不受影响。

### 17.4 有效模式与功能前提（合规记录缺口只记 Warning，不再降级）

| 情形 | 有效模式 |
| --- | --- |
| 没配数据库连接串 | 与 P1 相同（无档案机制） |
| 配了数据库，档案**从未读取成功**或**没有该店档案**，且已配置腾讯云凭据 | `Off`（宁可不识别，也不在读不到配置时放开留存） |
| 同上，但**完全没配凭据**（功能未启用） | 保持 P1 现状，不改变现有门店行为 |
| 档案 `Mode=0` / 未知值 | `Off` |
| `Mode=1` | `Capture`；`EvaluateMatch=1` 且匹配可用且开了员工排除或去重 → 同时做评估，否则不评估 |
| `Mode=2` 但匹配整体不可用（缺凭据、地域非法等） | `Off`（**不是**退回全部留存） |
| `Mode=2` 且开了员工排除 / 去重 / 名单，但 `CrossBorderRegion` ≠ 当前配置地域、缺 `CrossBorderReviewRef/By/At`，或（名单开启时）缺名单专项记录（`UnwelcomeAssessmentRef`、`UnwelcomeLegalSignoffBy/At`） | **照常按 `Match` 运行，不降级、不关任何功能**；按门店节流（每店 10 分钟一条）记 Warning `match-without-legal-records`（英文消息：Match mode is running without complete legal records; allowed by configuration, must be completed before relying on it；不含任何记录内容）。2026-10-13 用户决定：合规记录缺口是警告，风险由用户承担 |
| `Mode=2` 且去重开启，但 `TimeZoneId` 为空或系统不认识 | 去重关闭，其余照常；记 Warning（接收端无权读门店表，所以档案里的 `TimeZoneId` 必须由主 API 填好） |
| `Mode=Match` 且 `UnwelcomeListEnabled`，但没开私有 COS（命中图存不下） | 名单这一块关闭；记 Warning（`unwelcome-no-cos`）——这是功能前提，不是合规记录 |
| `Mode=Capture` 且 `UnwelcomeListEnabled`（内部知情人员测试） | 要求匹配可用、`AllowedSubjects` 非空、私有 COS 可用，**不要求专项记录与跨境审查**；缺任一项名单这一块关闭并记 Warning（`capture-unwelcome-*` / `unwelcome-no-cos`），Capture 的留存不变 |

> 重要：**配置腾讯云凭据会让「没有档案的门店」从现状变成 `Off`（读完即删）**。给 1042 等仍在 `Capture` 试点的门店，必须先在 `FaceStoreProfile` 里建好 `Mode=1` 的档案，再配凭据。
> 刚启动、第一次读档案还没尝试完时，处理器会先等一轮（报文留在盘上），不会因为「还没读」就误判成 `Off`。

### 17.5 阈值标定办法（在 `Capture` 模式的内部人员门店做）

1. 档案：`Mode=1`、`AllowedSubjects` 写明「仅内部测试员工」、`EvaluateMatch=1`、`StaffExclusionEnabled=1`（以及要标定去重就 `TrafficDedupEnabled=1`，并填好 `TimeZoneId`）。员工库里要有真实登记的内部员工。
2. 评估对每条（非重复）抓拍的第一张人脸搜一次，**不带最低分**，把员工库 / 当日访客库的最高分写进 `FaceMatchEvaluation`：

| `Decision` | 含义 |
| --- | --- |
| 0 NoMatch | 没命中任何库且未开去重 |
| 1 Staff | 员工分 ≥ 员工阈值 |
| 2 RepeatVisitor | 访客分 ≥ 访客阈值（同一天同一人的重复抓拍） |
| 3 NewVisitor | 开了去重且无命中：会被当新访客（评估时同样会在当日访客库建人，次日整库删） |
| 4 LowQuality | 质量闸门不过，或腾讯云判定质量 / 无人脸（分数为空） |

3. 让已知身份的人（员工本人、同一内部人员反复经过）产生数据，按 `CaptureId` 关联 `FaceCaptureEvent` 的抓拍时间与摄像机：
   同一个人的重复抓拍里 `VisitScore` 的分布（下沿）决定访客阈值的上限，**不同人**之间的最高分（上沿）决定下限；员工同理看 `StaffScore`。
   腾讯云对 3.0 库搜索没有官方分数–误识率对照表，阈值**只能用本地数据标定**；顾客以澳洲人为主，不要照搬官方表。
4. 把标定结果写回 `FACE_MATCH_STAFF_THRESHOLD` / `VISITOR_THRESHOLD` / `UNWELCOME_THRESHOLD`（或按门店覆盖），重建容器生效。名单阈值保持明显高于另外两个。
5. 评估失败（腾讯云错误、数据库错误）只记 Warning，**不影响**原有入库与留存。

### 17.6 腾讯云子账号 `face-match-matcher` 与密钥写入（由账号持有人在控制台完成，我不代做）

1. 控制台 → 访问管理 → 用户 → 新建**自定义策略**并新建子用户 `face-match-matcher`（**只勾「编程访问」**，不要控制台登录）。策略 JSON（若控制台策略生成器给出的 action 写法不同，以控制台为准）：

```json
{
  "version": "2.0",
  "statement": [
    {
      "effect": "allow",
      "action": [
        "iai:SearchFacesReturnsByGroup",
        "iai:CreateGroup",
        "iai:CreatePerson"
      ],
      "resource": "*"
    }
  ]
}
```

   - 只给这三个动作：搜索、建当日访客库、建访客。**不要**给 `DeletePerson`、`DeleteGroup`、`DeleteFace`、`CreateFace`、`GetPersonList` 等；这些属于主 API 的**另一个**管理子账号（`FACE_MATCH_ADMIN_SECRET_ID/KEY`），两套凭据必须是两个不同的子账号。
   - `CreatePerson` 不支持按库限定资源（设计第 9 节第 10 点），所以子账号理论上能往任何库建人；补偿措施：代码只允许接收端往 `v{店}_{日期}` 库建人，员工库只经主 API 写，主 API 每日核对员工库人数。
   - 地域：IAI 的地域是调用时的参数，不是策略条件；广州 / 上海已开通，新加坡当前报「计费状态未知」（账号层面未开通）。
2. 把密钥写进服务器 `.env`，**输入时不回显、不进 shell 历史**，不要贴到对话 / 工单里：

```bash
cd /www/HBWeb/BackEnd/face-ingest
read -rs -p 'SecretId: ' FM_ID; echo
read -rs -p 'SecretKey: ' FM_KEY; echo
sudo tee -a .env >/dev/null <<EOF
FACE_MATCH_TENCENT_SECRET_ID=$FM_ID
FACE_MATCH_TENCENT_SECRET_KEY=$FM_KEY
EOF
unset FM_ID FM_KEY
sudo chmod 600 .env
sudo grep -c '^FACE_MATCH_TENCENT_SECRET_' .env       # 只核对行数应为 2，不要 cat 整个文件
```

   重复追加会产生重复行：已写过先用 `sudo nano .env` 修改，不要再追加。写完后按第 6 节的方式重建容器。
3. 生效核对：`curl -fsS http://127.0.0.1:5004/health` 应看到 `"matcherEnabled":true,"matcherRegion":"ap-guangzhou"`；
   `docker logs hb-face-ingest 2>&1 | grep 'Face match'` 应是 `Face match configured matcherEnabled=true region=…`。若是 `DISABLED despite credentials being configured: …`，按原因（地域不在白名单、密钥格式、阈值非法等）修正。
4. 上线前书面向腾讯云确认设计第 9 节第 1、3 点（不保留非库内图片、不用于训练），并取得数据处理协议；告示、隐私政策、员工同意书要写明数据在所选地域处理。

### 17.7 数据库授权与读写的表 / 列（接收端 `face_ingest_app`）

表与列由主 API 的迁移（`20261014.001-face-match`）创建，**接收端不写迁移、不增列**。`face_ingest_app` 的授权（不授 DELETE、不授角色）：

| 表 | 权限 | 接收端的用法 |
| --- | --- | --- |
| `FaceStoreProfile` | `SELECT` | 每 30 秒读：`StoreCode, Mode, EvaluateMatch, StaffExclusionEnabled, TrafficDedupEnabled, UnwelcomeListEnabled, AllowedSubjects, CrossBorderRegion, CrossBorderReviewRef, CrossBorderReviewedBy, CrossBorderReviewedAt, TimeZoneId, UnwelcomeAssessmentRef, UnwelcomeLegalSignoffBy, UnwelcomeLegalSignoffAt` |
| `FaceTrafficBucket` | `SELECT, INSERT, UPDATE` | 整点桶 UPSERT（`UPDATE … WITH (UPDLOCK, SERIALIZABLE)`，没命中再 `INSERT`，列加增量，并发安全） |
| `FaceVisitGroup` | `SELECT, INSERT, UPDATE(VisitorCount)` | 首次建组 `INSERT`（`DeleteAfterUtc` = 营业日次日本地 02:00 的 UTC），之后只 `VisitorCount = VisitorCount + n` |
| `FaceMatchEvaluation` | `INSERT` | 评估行；**只有 INSERT 没有 SELECT**，所以用直接插入并吞主键冲突实现幂等 |
| `FaceUnwelcomeEntry` | `SELECT` | 按 `TencentPersonId` 取 `EntryId, StoreScope, ExpiresAtUtc`（`Status` 的数值含义设计未规定，接收端不依赖，靠腾讯云库成员与 `ExpiresAtUtc` 判定） |
| `FaceMatchTask` | `SELECT, INSERT` | 名单命中建任务：`TaskId, StoreCode, CameraId, CapturedAtUtc, EntryId, Score, ImageObjectKey, ReviewStatus=0(Pending), ImageDeleteAtUtc`；`NotifiedAtUtc` 与复核人列留空 |

另外沿用 P1：`FaceCamera`（`SELECT`、`UPDATE(LastSeenAtUtc)`）、`FaceCaptureEvent/Image`（`SELECT, INSERT`）。

### 17.8 名单命中（暗发布，默认关闭）

- 只有档案 `UnwelcomeListEnabled=1`、并且**接收端开了私有 COS**（命中图要存私有桶）时才搜 `unwelcome` 库；Match 下名单专项记录不全只记 Warning（17.4），不阻止搜索。
- 命中（分数 ≥ 名单阈值，条目未过期，条目 `StoreScope` 为空或包含本店）：命中图存 `face-ingest/unwelcome-hits/{店}/{yyyy}/{MM}/{dd}/{摄像机}/{TaskId}.jpg`（私有 ACL；沿用 `FACE_INGEST_COS_*` 的只写 PutObject 子账号，其资源范围 `face-ingest/*` 已覆盖），
  `FaceMatchTask` 新增一行待复核（`ImageDeleteAtUtc` = 现在 + 7 天）。**接收端不发通知**（通知发布器只在主 API）；主 API 的后台任务轮询 `NotifiedAtUtc IS NULL` 的新任务后发提示，文案固定为「疑似匹配，需要人工确认」，图片按 `ImageDeleteAtUtc` 由主 API 删除。
  建议再给 `face-ingest/unwelcome-hits/` 配 COS 生命周期（7–8 天）兜底。
- 日志只记「命中任务已创建」、摄像机、门店、取整分数；不记对象键、`PersonId`、`TaskId`。

### 17.9 部署顺序

1. 主 API：显式迁移（`--schema=migrate`）建 `20261014.001-face-match` 的全部表；DBA 给 `face_ingest_app` 追加 17.7 的授权。**迁移 / 授权没做之前接收端读档案会失败**：每 5 分钟一条 Error（`Face store profile refresh failed`），行为按 17.4（未配置凭据时仍是 P1 现状）。
2. 在 `FaceStoreProfile` 为现有试点门店建 `Mode=1` 档案（17.4 的重要提示）。
3. 重新构建并重建 face-ingest（第 5 节流程）。此时未配凭据，行为不变，`/health` 多出 `matcherEnabled=false`、`matcherRegion`。
4. 完成 17.6，写凭据、重建容器，核对 `/health`。
5. 内部人员门店先做阈值标定（17.5），再由主 API 把真实门店切到 `Match`。隐私影响评估、告示、律师确认、跨境审查等记录现在只是警告、不拦截切换，但对真实顾客运行前法律上必须完成。

### 17.10 排查

| 现象 | 看什么 |
| --- | --- |
| `matcherEnabled=false` | 启动日志 `Face match …` 一行给出原因：地域缺失 / 不在白名单、SecretId/SecretKey 缺失或格式错、阈值非法。改 `.env` 后重建容器 |
| 门店抓拍被丢弃 | 日志 `Face ingest capture discarded … mode=Off reason=…`：`profiles-unavailable`（档案读不到）/ `no-profile` / `profile-off` / `match-disabled-by-configuration` |
| 档案读不到 | `Face store profile refresh failed`：连接、权限（17.7）、表是否已迁移。读不到期间已配凭据的门店按 `Off` |
| 模式被降级 / 合规记录缺口提醒 | Warning `Face match store mode degraded store=… reason=…`（模板文字沿用，监控按它过滤的不必改）。`reason`：`timezone`、`unwelcome-no-cos`、`match-disabled-by-configuration`、`capture-unwelcome-*` 是功能前提不满足（会关掉对应功能）；**`match-without-legal-records` 只是提醒——Match 在缺合规记录下照常运行，需尽快补齐**。每店每原因 10 分钟一条。旧原因 `cross-border-gate`、`unwelcome-gate`、`downgraded-*` 已不再出现 |
| 腾讯云调用失败 | Warning / Error `Face match API call failed and was dropped … code=…`（同一错误码 2 分钟一条）。账号级错误（`AuthFailure.*`、`ResourceUnavailable.*`）是 Error 并熔断 60 秒：检查密钥、策略、服务是否开通 / 欠费 |
| 计数不涨 | `Face ingest capture matched … faces= staff= unique= repeat= lowQuality= apiFailure=`（每条一行，无敏感信息）；`Face ingest Match capture dropped after repeated failures` 表示数据库写失败被丢弃 |
| 磁盘上有 Match 门店的报文 | 只应出现在处理间隔内；长期滞留说明处理器停了（`FACE_INGEST_PROCESSING_ENABLED=false`）或档案读取一直失败（启动等待） |

### 17.11 回滚

- **关闭匹配、回到只留存**：清空 `FACE_MATCH_TENCENT_SECRET_ID/KEY` 并重建容器。注意 17.4：已有档案的门店仍按档案走，`Mode=2` 的门店在匹配不可用时按 `Off`（读完即删，不留生物数据）；
  需要这些门店继续留存，先由主 API 把它们改回 `Mode=1`。
- **某家店紧急停止**：主 API 把该店 `Mode` 改为 0（30 秒内生效，之后该店抓拍读完即删）；或 `Mode=1` 恢复留存。
- **回滚代码**：用上一个镜像重建容器（第 5 节回滚流程）；新表和档案不影响旧版本，旧版本不读它们。已在腾讯云建的当日访客库由主 API 的清理任务删除；需要手工清理时用管理子账号 `DeleteGroup`，**接收端子账号没有删除权限，这是有意的**。
- 腾讯云账号侧：停用 `face-match-matcher` 子账号或其密钥即可立刻切断调用，接收端会在 60 秒熔断后持续丢弃（计 `ApiFailureCount`），不会堆积本地生物数据。

### 17.12 已知限制

- 访客「唯一数」是估算：同一人被认成两人（偏高）或两人认成一人（偏低）都会发生；腾讯云新建人员后立刻搜索是否可见官方未说明，试点必须实测，必要时用「同摄像机 N 秒内合并」补偿。
- Match 模式下计数写库失败的重试，可能让该条的「新访客」变成「重复」（重试时能搜到自己上次建的访客）：只影响极少数数据库抖动的抓拍。
- 摄像机持续推送而档案读不到（已配凭据）时，抓拍会被丢弃而不是积压；这是「宁可不识别」的设计取舍。

