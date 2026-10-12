# 生产 Nginx 配置片段

生产站点 `hotbargain.vip` 的 Nginx 由宝塔面板管理（`/www/server/panel/vhost/nginx/www.hotbargain.vip.conf`），
完整配置含 TLS 证书路径，不放进仓库。这里只收录**业务相关、改错会静默出事**的片段，使它们有版本记录、可审查。

| 文件 | 作用 |
| --- | --- |
| `hotbargain-pos-api.location.conf` | `/pos-api/` 反向代理到收银后端；Linkly 云支付回调（按键提示、回单）依赖这条路由 |
| `hotbargain-notification-hub.location.conf` | `/api/hubs/` 通知实时通道（SignalR）透传 WebSocket 升级头；不加也能用（自动退回长轮询），加了走 WebSocket |
| `hotbargain-face-ingest.location.conf` | `/face-ingest/` 反向代理到人脸抓拍原始报文接收服务（`127.0.0.1:5004`）；门店海康摄像机推送依赖这条路由。token 在 URL 里，所以关闭 `access_log`、把 `error_log` 指到 `/dev/null`，并用 `/hik/` 兜底块挡住漏写前缀的推送；明文 HTTP 直接 `403`；不加 IP 白名单 |

## 改动后的验证

1. `sudo nginx -t && sudo systemctl reload nginx`
2. 按 `hotbargain-pos-api.location.conf` 头部注释里的 `curl` 命令做一次回调自测，期望 `200`。
3. 任意一台 POS 触发一次 `cloud-backend/health`：`CALLBACK_REACHABILITY` 检查项应为就绪。
   该检查是 POS 容器经公网地址带 bearer 对随机 sessionId 发的真实 POST，结果缓存 5 分钟（失败缓存 30 秒）。
4. 改动 `/face-ingest/` 后，从公网按 `hotbargain-face-ingest.location.conf` 头部注释做五条 `curl` 自测：
   `health` 返回 `200`、错误 token 返回 `401`、正确 token 加二进制 multipart 请求体返回 `200`（被宝塔 WAF 拦截时是 `403`）、漏写前缀返回 `404`、
   明文 `http://` 返回 `403` 或 `301`（绝不能是 `401` / `200`）。
   样本生成与 token 读入方式见 `docs/runbooks/face-ingest.md`。
