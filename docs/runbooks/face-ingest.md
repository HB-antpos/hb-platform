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
