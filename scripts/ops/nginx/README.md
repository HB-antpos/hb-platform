# 生产 Nginx 配置片段

生产站点 `hotbargain.vip` 的 Nginx 由宝塔面板管理（`/www/server/panel/vhost/nginx/www.hotbargain.vip.conf`），
完整配置含 TLS 证书路径，不放进仓库。这里只收录**业务相关、改错会静默出事**的片段，使它们有版本记录、可审查。

| 文件 | 作用 |
| --- | --- |
| `hotbargain-pos-api.location.conf` | `/pos-api/` 反向代理到收银后端；Linkly 云支付回调（按键提示、回单）依赖这条路由 |

## 改动后的验证

1. `sudo nginx -t && sudo systemctl reload nginx`
2. 按 `hotbargain-pos-api.location.conf` 头部注释里的 `curl` 命令做一次回调自测，期望 `200`。
3. 任意一台 POS 触发一次 `cloud-backend/health`：`CALLBACK_REACHABILITY` 检查项应为就绪。
   该检查是 POS 容器经公网地址带 bearer 对随机 sessionId 发的真实 POST，结果缓存 5 分钟（失败缓存 30 秒）。
