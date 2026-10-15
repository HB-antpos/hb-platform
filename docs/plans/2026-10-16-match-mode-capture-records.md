# Match 模式也留抓拍记录 + Web 命中列表 + 「最近抓拍」抽屉修整（方案，待确认）

设计图：早期版本（含已作废的「不留照片」画法）只发给了用户，未入库。

> **2026-10-16 用户变更：Match 模式也要留照片。** 下文 2.A「只留记录、不留照片」作废，改为：Match 与 Capture 一样留存抓拍照片与记录（人脸小图 + 背景大图 + 原始报文，存储与 7 天保留同 Capture），入库成功后再跑比对流水线（计数、当日访客去重、名单命中）并写评估行。因此 Web 不做「不留照片」占位与「模式」列（没有迁移就无法逐行知道模式），抽屉不显示 Match 警告、只常驻「最后推送」，Off 店显示「不留记录」提示。「黑名单命中」视图、抽屉列宽修整、保留清理补删 `FaceMatchEvaluation` 照做。注意：这意味着真实顾客的照片也会留存 7 天，原设计要求的律师确认沿用「只提示不拦截」。

## 1. 起因（生产只读核实，10-16）

- 1042 档案 08:18:00（布里斯班）被 admin 改成 `Mode=2`（Match），审计 `profile.update mode=2`。
- Match 模式下接收端只累加计数桶、不写 `FaceCaptureEvent`，所以「抓拍与比对」页和摄像机「最近抓拍」抽屉都停在 08:15:53。
- 摄像机其实一直在推：`FaceCamera.LastSeenAtUtc` = 08:29:42；08:19:08 那次名单命中照常建了任务、自动确认、发了通知。
- 现有 19 条命中任务没有对应的抓拍记录（Match 期间产生的）。
- 抽屉 960 宽，7 列合计 1010 宽，「图片」列在最右边，要横向滚动才看得到。

## 2. 要做的三件事

### A. Match 模式也写抓拍记录（只留记录，不留照片）

接收端 `CaptureProcessor.ProcessMatchModeAsync` 在匹配完成后，额外写：

- 一行 `FaceCaptureEvent`：`RawStorage='none'`、`RawObjectKey=NULL`，**不写 `FaceCaptureImage` 行**、不上传 COS；其余元数据（时间、摄像机、人脸数、解析状态、请求体哈希）和 Capture 一样。
- 一行 `FaceMatchEvaluation`：判定（员工 / 新访客 / 回头客 / 质量不足）和员工、访客相似度。流水线目前只回传计数增量，要让 `MatchOutcome` 同时带回这条抓拍的判定和分数（Capture 评估路径已经在算，复用同一套）。

要点：

- **顺序是先删本地报文、再写记录**；写记录失败只记节流日志，不重试、不影响计数，也不会因此把照片留在盘上。
- 不写访客 PersonId、不写对象键；记录随现有 `CaptureRetentionDays`（默认 7 天）一起清理，不用加新任务。
- 名单命中照旧写 `FaceMatchTask` 和命中图（7 天）；结果页已经按「摄像机 + 抓拍时间」关联命中，所以新记录会自动带出命中和通知列。
- 接收端在 Capture 模式下本来就写这两张表，**不用新加数据库授权，也不用迁移**（`RawStorage` 是 nvarchar(16)，`'none'` 放得下，`MatchStatus` 没有消费方）。

> 需要你确认：这改了原设计「Match 处理完即删、不写事件行」的隐私约定——以后 Match 店每次抓拍都会留一条「几点几分、哪台摄像机、判成回头客、相似度多少」的记录（没有照片，也没有能把多次到店串起来的访客编号），保留 7 天。

### B. Web「抓拍与比对」页加视图切换

- 顶部加「全部抓拍 / 黑名单命中」分段切换，「黑名单命中」带红色未读数（口径同 App：我自己的未读命中通知）。
- **全部抓拍**（现有列表）：加「模式」列（Capture / Match）；Match 行照片位显示「不留照片」占位，有名单命中的 Match 行显示命中图缩略图；「查看」抽屉对 Match 行不出现「查看图片」。页头说明文案改成「Match 只留记录、不留照片」。
- **黑名单命中**：新组件，调 App 已在用的 `GET results/hits`、`results/hits/summary`、`POST results/hits/{taskId}/read`、`results/hits/{taskId}/image`、`notifications/read-all?type=`；状态筛选（全部 / 未读 / 待复核 / 自动确认 / 已确认 / 已驳回）+「全部标为已读」；「复核」按钮只给有 `FaceMatch.Unwelcome.Review` 的人。改动前的 19 条命中在这里能看到。
- 权限不变：都是 `FaceCapture.Images.View`，看命中图照常写审计。

### C. 摄像机「最近抓拍」抽屉

- 「图片」列挪到第 2 列；时间列去掉年份、解析状态只显示中文标签、备注列省略号截断，合计压到 960 以内，不用横向滚动。
- 后端 `GET face-cameras/{cameraId}/captures` 响应加两个字段：本店当前模式 `storeMode`、摄像机 `lastSeenAtUtc`（不是敏感信息，权限不变）。
- 店是 Match 时顶部常驻黄色提示：「当前为 Match 模式：照片处理完即删除，下面只列记录、没有照片。最后推送：…（约 5 分钟精度）」；Off 时提示「不处理、不留记录」。
- Match 行图片列显示「不留照片」，不出现「查看图片」。

## 3. 改动面

| 位置 | 改动 |
|---|---|
| `BlazorApp.FaceIngest` | `CaptureProcessor` Match 分支写记录；`FaceMatchPipeline.MatchOutcome` 带回判定与分数；测试 |
| 主 API | 结果列表 DTO 标明是否留图（由 `RawStorage='none'` 推出）；Match 行读图返回「不留照片」而不是报错；缩略图对 Match 行跳过、命中行用命中图；摄像机抓拍接口加 `storeMode` / `lastSeenAtUtc`；测试 |
| Web | `ResultsTab` 分段切换 + 模式列 + 占位；新命中列表组件；`ResultDetailDrawer` 处理 Match 行；`CapturesDrawer` 列与提示；中英文案（放页面级消息文件，顾及首屏包体预算）；契约测试 |
| App | 计划不改代码、不发 OTA；但结果 DTO 加字段会碰到 App 的跨端契约测试（字段集合比对），实现时要么同步改 App 测试，要么用现有字段推出「不留照片」——以 App 测试为准核实 |

## 4. 发布顺序（无迁移、无权限 SQL）

1. 主 API + Web 先发（能正确显示 `RawStorage='none'` 的行）；
2. 再重建 face-ingest（开始写 Match 记录）。

反过来发的话，旧 Web 会把 Match 行当成「照片读不到」。08:18 到上线这段时间的 Match 抓拍没有存过，补不回来；这段时间的命中在「黑名单命中」视图里能看到。

## 5. 验证

- 接收端：Match 写一行事件 + 一行评估、零图片行、零 COS 上传；写记录失败时本地文件照删、计数照加；重复报文不重复计数。
- 主 API：Match 行列表、读图拒绝、缩略图跳过；命中流已有测试保持绿；SQL Server 门控测试。
- Web：typecheck、`test:face-match`、`test:access`、摄像机页契约测试；本地造数目测两个视图和抽屉（960 宽不出横向滚动条）。
- 上线后：只读核对 1042 新抓拍有 `RawStorage='none'` 行、`FaceCaptureImage` 没有对应行、COS 没有新原图。
