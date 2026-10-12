# 人脸抓拍原始报文接收服务（face-ingest）P0 运维手册

## 1. 用途与边界

门店海康摄像机（「应用仓库 → 人脸抓拍」）通过报警主机 / HTTP 监听，主动用 HTTPS 把抓拍事件推到
`https://hotbargain.vip/face-ingest/hik/v1/<token>`。

P0 只做一件事：**校验摄像机 token → 把请求体逐字节原样落盘并写一份元数据 → 立刻回 200**。目的是到现场抓真实报文，P1 再据此写正式解析。

P0 **不做**：

- 不解析 multipart，不入库，不接 COS；
- 不做人脸比对、黑名单、告警推送（这些都还没做）；
- 不接中心日志（日志只进容器控制台，由 docker json-file 轮转）。

| 项目 | 值 |
| --- | --- |
| 源码 | `services/backend/BlazorApp.FaceIngest`（不进 `BlazorApp.sln`，不引用 Api / Shared，不连数据库） |
| compose | `services/backend/BlazorApp.FaceIngest/docker-compose.face-ingest.yml`，项目名 `hbweb_face_ingest` |
| 容器 / 镜像 | `hb-face-ingest` / `hb-face-ingest:local` |
| 端口 | 宿主机 `127.0.0.1:5004` → 容器 `5004`，公网只经 Nginx |
| 公网入口 | `https://hotbargain.vip/face-ingest/`（Nginx 片段 `scripts/ops/nginx/hotbargain-face-ingest.location.conf`） |
| 健康检查 | `GET /face-ingest/health` → `{"status":"healthy","captureEnabled":…,"storedFileCount":…,"storedBytes":…}` |

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
