# KFC 补货信号报告（Uncle Bills，本地供应商 257）

每周一、周五早 8:00，本机的 Claude Code 定时任务 `kfc-uncle-bills-mon-fri` 会生成一份网页报告，内容包括：

- KFC 商品的畅销榜
- 进销累计
- 高价大件
- 未来 7/14 天预测
- 分店缺口与调拨建议
- 无进货记录的门店

报告发布在 <https://hotbargain.vip/reports/kfc-uncle-bills/>。打开需要登录主站，并按门店授权。

**脚本只读**：全程只执行 SELECT（`READ UNCOMMITTED`），不写任何库。系统没有调拨功能，调拨来源只是建议。

## 文件

| 文件 | 作用 |
|---|---|
| `analyze_kfc.py` | 分析脚本：读取销量、进货单、主档，计算预测与缺口，生成站点包，可选上传服务器 |
| `report_template.html` | 页面外壳：按权限加载数据、门店切换、分页、页签按需渲染、图片懒加载、中英切换 |
| `run.sh` | 定时任务入口：先从 `origin/main` 同步上面两个文件和自身，再运行脚本 |
| `gate/index.html` | 续期中转页：令牌过期时先续期再回到报告，续期失败才去登录页 |
| `gate/forbidden.html` | 无权限说明页 |

定时任务目录 `~/.claude/scheduled-tasks/kfc-uncle-bills-mon-fri/` 里只放本机副本，**以仓库 `origin/main` 为准**。`run.sh` 每次运行前都会同步，拉取失败时沿用上一份，并输出 `SYNC_WARN=`。

## 运行

```bash
# 定时任务实际执行的命令
~/.claude/scheduled-tasks/kfc-uncle-bills-mon-fri/run.sh --out-dir ~/Documents/HB-Reports/kfc-uncle-bills --upload-server

# 手动生成（不上传），或指定运行日
python3 analyze_kfc.py --out-dir /tmp/kfc-report --as-of 2026-09-23

# 回测：把历史日期当作运行日，对比预测 14 天与实际销量
python3 analyze_kfc.py --backtest 2025-10-06,2025-11-03,2025-12-01
```

**运行依赖**：

- 本机装有 `pymssql`、`requests`、`Pillow`。
- 连接串读取主检出的 `services/backend/BlazorApp.Api/appsettings.Development.json`，这个文件被 gitignore。
- 上传服务器用本机的 `hotbargain.vip.pem`。

**退出码**：

- `0`：成功。
- `2`：本机已生成，但上传失败，stderr 里有 `UPLOAD_FAILED=`。
- 其它：分析失败。

## 输出（`--out-dir`）

```
site/                       ← 整个目录用 rsync --delete 同步到服务器 /www/HBWeb/reports/kfc-uncle-bills/
  index.html                  页面外壳（约 110KB），只带门店名单与生成时间，不含业务数据
  data/all.json               全部门店视图（全链口径）
  data/store-{门店}.json       单店视图，每家启用门店一份
  img/t/{key}.webp            缩略图（72px），懒加载
  img/l/{key}.webp            放大图（480px），点开才下载
image_cache/                 图片缓存（只存 WebP；下载失败的图 7 天内不重试）
runs/{日期}/report.json | rows.csv
latest_report.json           结构为 views.all.* / views.store-XXXX.*
latest_meta.json             全部门店视图的摘要，定时任务汇报用
```

**口径要点**（完整公式见页面「附注」页签）：

- **全部门店视图**：按全链口径统计，包括已停用门店去年的销量，否则同比会失真。
- **单店视图**：只统计本店。页面标注「本店同比」，只作展示，不参与预测。
- **到货后余量**：近 180 天内所有批次到货量之和，减去自第一批到货日起的销量（多次到货都计入；近 180 天没到货的退回只看最近一批），不是货架库存。HQ 库（HOT_HQ_CLOUD）已下线，不再查询 HQ 账面库存。
- **门店所在州**：Store 表没有州字段，取地址里最后一个 4 位邮编换算州（与考勤公众假日同步 `PublicHolidaySyncHelper` 同一做法）；地址没写邮编时按门店 `TimeZoneId` 判断；都不行显示「未知」并在运行警告里列出店号。
- **有货门店（按州）**：「分店缺口与调拨」每行列出该商品到货后余量 ≥ 1 的其它门店（含仓库，不含测试店），按州分组、缺货店所在州排前；点行展开每家店的余量与「28 天后富余」（余量 − 预测 28 天）。数据按商品存一份在各视图的 `stock_holders` 里，单店文件也包含外店余量（与「调拨来源」已显示外店店名的口径一致）。调拨来源的推荐算法不变。
- **节日主题筛选**：页头「全部 / 万圣节 / 圣诞节 / 其他」作用于所有页签的表格，纯前端按英文商品名关键词归类（`report_template.html` 的 `HALLOWEEN_RE` / `CHRISTMAS_RE`，先判万圣节），数据里没有商品分类字段，归类仅供参考；选择记在本浏览器。KPI 与摘要不随筛选变化。
- **进货明细匹配**：依次按商品编码 → 门店商品编码 → 唯一货号 → 唯一条码。

## 访问控制

报告文件都由 nginx 直接提供。每个请求先用 `auth_request` 调后端 `StaticReportAccessController`（`GET /api/react/v1/static-report-access/kfc-uncle-bills`）鉴权：

| 请求 | 需要 |
|---|---|
| 页面外壳、`img/` 下的图片 | 「查看 KFC 补货信号」`SalesDashboard.KfcRestockSignal.View` |
| `data/all.json` | 再加「查看 KFC 补货信号全部门店」`SalesDashboard.KfcRestockSignal.AllStores` |
| `data/store-{门店}.json` | 有全部门店权限，或该门店是账号关联的启用门店 |

- **范围接口**：页面启动时调用 `.../kfc-uncle-bills/scope`，得到可看的门店。
- **管理员**：自动拥有这两项权限。
- **授权方式**：两个权限都在「销售看板」分组里，运行时并入权限目录，不用手工入库，直接在角色管理里授予。
- **后端判定依据**：nginx 传来的原始请求地址 `X-Original-URI`。后端会先规整 `..`、编码和多余斜杠，`data/` 下不认识的文件名一律拒绝。

服务器 nginx 是手工维护的，配置在 `/www/server/panel/vhost/nginx/www.hotbargain.vip.conf`。当前生效的片段如下（443 server 块）：

```nginx
location = /reports/kfc-uncle-bills {
    return 301 /reports/kfc-uncle-bills/;
}

location ^~ /reports/kfc-uncle-bills/ {
    auth_request /__static-report-auth/kfc-uncle-bills;
    error_page 401 = @static_report_gate_kfc;
    error_page 403 /reports/_gate/forbidden.html;
    alias /www/HBWeb/reports/kfc-uncle-bills/;
    index index.html;
    expires -1;          # 用 expires 而不是 add_header，避免丢掉 server 级的 HSTS 头
}

location = /__static-report-auth/kfc-uncle-bills {
    internal;
    proxy_pass http://127.0.0.1:5002/api/react/v1/static-report-access/kfc-uncle-bills;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_set_header X-Original-URI $request_uri;   # 缺了它新后端对所有请求返回 403
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}

location @static_report_gate_kfc {
    return 302 /reports/_gate/?to=/reports/kfc-uncle-bills/;   # 写死地址，不拼接 $uri，防止换行注入
}

location ^~ /reports/_gate/ {
    alias /www/HBWeb/reports/_gate/;   # gate/ 目录的两个页面放这里
    index index.html;
    expires -1;
}
```

另外，80 端口的 server 块用 `location ^~ /reports/` 把请求 301 到 HTTPS，因为登录 Cookie 带 Secure 标记，HTTP 下鉴权永远失败。

### 改动访问规则时的上线顺序

按「站点文件 → nginx → 后端」的顺序上线：新页面在旧后端下要能工作，新后端上线前 nginx 已经把需要的信息传过去。**反过来会越权或整体 403**。

2026-09-23 拆分门店权限时的顺序：

1. 上传新格式站点。旧后端没有范围接口，返回 404，页面这时按全部门店加载；而旧后端只放行能看全店的角色，所以不会越权。
2. nginx 加 `X-Original-URI`。旧后端会忽略它。
3. 上线后端（PR #311），同时替换 `gate/forbidden.html` 的文案。

## 性能取舍

- **表格分页**：每页 50/100/200 行，所有表格共用、记在本机。**页签按需渲染**：只画当前页签。原来一打开就渲染约 3,200 行、8 万个 DOM 节点，手机明显卡顿；现在首屏约 50 行、1.5k 个节点。没有用虚拟滚动：展开的明细行高度不固定，和吸顶表头、三列冻结一起用会跳动。
- **图片**：每张一个文件，用 `loading="lazy"` 懒加载，一屏约 40 张共 50KB 左右。令牌过期后图片会 401：页面在 60 秒内只续期一次，然后带 `?r=` 重试。
- **不加浏览器缓存**：实测网络往返约 260ms，鉴权只多约 10ms，缓存只能省下重复打开时约 0.4 秒，所以没改 nginx。
