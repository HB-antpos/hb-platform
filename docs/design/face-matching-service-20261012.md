# 门店人脸比对服务设计（腾讯云 IAI）

设计日期：2026-10-12；**第五版 2026-10-13**（纳入用户的六项决定：第一期 2 家店（已定 QLD Orion）、先用腾讯云后续可改自建、顾客人脸去重要做、不受欢迎人员名单（原「黑名单」）要做、匹配结果显示相似度百分比、腾讯云使用广州地域（中国大陆），见第 13、14 节）。交付范围：设计方案；**未实施业务代码、数据库迁移或生产配置**。

上游已上线：人脸接收服务 P0/P1（#724、#726）、摄像机管理后台（#727）、查看抓拍图片（#737）。本文在此基础上设计「比对」。

> 本文中的合规内容是调研信息整理，**不是法律意见**。标注【待律师确认】的点，必须在对应功能对真实顾客上线前由律师确认。

## 1. 设计结论

1. **比对引擎用腾讯云人脸识别（IAI，托管 API），账号为现有中国站账号，地域按用户 2026-10-13 的决定用广州 `ap-guangzhou`（中国大陆）**。地域是**配置项，不写死**（`FaceMatch:Tencent:Region`）。原计划的新加坡地域在该账号下**未开通**（探测返回「计费状态未知」，属账号层面），暂不可用；将来可改新加坡、国际站账号或自建，只改配置。不自建 InsightFace：其预训练模型仅限非商业研究，商用须书面授权。
   - **广州在中国大陆：每张送去比对的人脸（包括没匹配上的顾客）都会在大陆处理，受中国法律管辖，合同争议归深圳法院。这是比新加坡更重的跨境披露**【待律师确认】，要写进告示、隐私政策与员工同意书。
2. **比对前置到接收端的处理器里，并且「非匹配即丢弃」**：匹配在人脸服务处理每条抓拍时完成；没匹配上的图片、原始报文立即删除，不入 COS、不留数据库事件，只累加计数。这是 OAIC 2026-07 指南的核心要求，也和当前「全部留存再慢慢删」的试点行为相反。
3. **按门店分模式，且服务端强制前置条件**：`Off` / `Capture`（现在的试点：全部留存，只允许拍摄知情同意的内部人员）/ `Match`（生产：匹配后丢弃）。门店进入 `Match` 前，必须记录隐私影响评估编号、门口告示安装日期、律师确认人（NSW 门店还要员工监控通知日期），缺一项后端拒绝切换。
4. **三个用途分开处理**：
   - **员工排除**：有明确路径（自愿同意），先做。
   - **顾客人脸去重客流**：**按用户决定纳入第一期**。调研认为对顾客做人脸去重没有已知的合法路径（对标 7-Eleven 2021），所以设计成「独立闸门」：对真实顾客运行前，必须记录单独的律师确认、隐私影响评估和门口告示，缺一后端拒绝打开；数据只在当天、当店的访客库里存在，次日删除，只留计数（第 8 节）。
   - **不受欢迎人员名单提示**：**按用户决定与第一期同时开发，但默认关闭、「暗发布」**——代码上线，任何门店都不能打开，直到该门店记录了不受欢迎人员名单专项的律师确认与评估（第 14 节）。只提示「疑似」，不处置，必须有人工复核（2026-10-13 起一个人就够，原为两人）；入名单有硬条件（严重事件、证据、批准、到期），不是每个小偷都能入。
5. **最小权限与两套凭据**：接收端只持有「搜索」权限的腾讯云子账号；员工库和不受欢迎人员名单库的增删由主 API 持有另一套「管理」权限的子账号，互相隔离。

## 2. 约束与已知事实

| 项目 | 事实 | 来源 |
|---|---|---|
| 服务器 | 2 vCPU / 7.4GB / 无 GPU，已承载生产业务；不适合自建模型 | 服务器实测 |
| 量级 | 30 店 × 每店每天约 1000–3000 次抓拍（每月 90 万–270 万） | 估算 |
| 摄像机 | 海康 DS-2CD3726FWDA3/F-IZS，固件 V5.7.4；只在检测到人脸时推送，没有心跳；推送的是已裁剪人脸（约 592×560）加背景图（1280×848） | 摄像机网页与真实推送 |
| 现有留存 | 原始报文 1 天、COS 人脸图 7 天、数据库事件行**无清理** | 2026-10-12 已收紧 |
| 腾讯云账号 | 中国站账号（appid 以 13 开头）；**广州、上海地域已开通可用；新加坡、雅加达地域报「计费状态未知」未开通**（2026-10-13 探测，新加坡请求编号 35c0ba78-3f98-4933-bb63-da7b466d9243）；主协议适用中国大陆法律 | 探测与调研 |

## 3. 合规设计要求（来自 OAIC 指南与裁定的整理）

依据：OAIC《Facial recognition technology: a guide to assessing the privacy risks》（2026-07-29 更新）；Bunnings 案 ART 复审（2026-02）；Kmart 裁定（2025，仍在复审）；7-Eleven（2021）。

| 要求 | 设计落实 |
|---|---|
| 告示不等于同意；员工同意须自愿、知情、具体 | 员工登记走「同意记录」，单独签署，拒绝或撤回无不利后果，提供不登记者的替代办法（第 7 节） |
| 非匹配者数据与模板立即删除；内存也算「收集」 | `Match` 模式非匹配即丢弃（第 5 节）；但**调用腾讯云本身就是一次对外披露**，见第 9 节风险 |
| 必须有隐私影响评估（PIA）、门口告示、隐私政策提及 | 门店模式的前置条件记录这三项；设计文档不替代 PIA |
| 不受欢迎人员名单：必要性与相称性、人工复核、名单准入与退出规则 | 不受欢迎人员名单条目必须有证据、批准人、到期与复核日期；命中只给店员「疑似」提示 |
| 准确性与偏差 | 试点用本地数据标定阈值；腾讯云无 3.0 库搜索的官方误识率表，不能照搬 |
| 访问控制与审计 | 看图、入名单、命中处理都要持久审计（本设计新增审计表） |
| 跨境披露 | 与腾讯云的合同条款、告示与隐私政策写明数据在新加坡，【待律师确认】 |
| NSW 门店员工监控 | 员工登记前至少 14 天书面通知（NSW Workplace Surveillance Act，条文原文未核实）【待律师确认】 |

## 4. 总体架构

```
摄像机 ──HTTPS──> Nginx ──> 人脸接收服务（接收端，P0）
                              │ 校验 token → 原样落盘 → 立刻 200
                              ▼
                       处理器（P1，后台）
                         解析 → 质量闸门 → 【按门店模式】
                              │
            ┌─────────────────┼───────────────────────┐
            ▼                 ▼                       ▼
        Off（丢弃）    Capture（现状：入库+存图）   Match（本设计）
                                                      │
                          腾讯云 IAI（ap-singapore）  ◄┘  SearchFacesReturnsByGroup
                                                      │
                  ┌───────────────┬───────────────────┴──────────┐
                  ▼               ▼                              ▼
             员工命中          命中                       无命中
          只累加「员工」计数   存图+建复核任务+发通知          只累加「非员工」计数
          图片与原始报文删除   （人工复核，图保留 7 天）       图片与原始报文立即删除
```

- **匹配放在处理器里**（而不是另起一个服务读库再匹配），因为数据在这里就已经在手、可以当场丢弃；另起服务就必须把图片先放到某处，等于增加留存。
- **主 API 管理员面**：员工登记、同意记录、不受欢迎人员名单条目、门店模式与前置条件、复核任务、审计都在主 API 与 Web 后台完成；人脸服务通过受限数据库账号读门店模式、写命中事件与计数。
- **腾讯云凭据**：
  - 接收端：CAM 子账号，只授 `SearchFacesReturnsByGroup`（及必要时 `DetectFace`），资源限定到员工库、不受欢迎人员名单库；
  - 主 API：另一个子账号，授 `CreateGroup`、`CreatePerson`、`CreateFace`、`DeletePerson`、`DeleteFace`、`GetPersonList` 等；
  - 两者都不授 `DeleteGroup` 以外的高危管理动作；密钥只经服务器 `.env`。
- 抽象接口：`IFaceMatcher`（搜索）与 `IFaceLibrary`（员工库、不受欢迎人员名单库增删），腾讯云实现可替换（AWS Rekognition 悉尼区是调研推荐的数据留澳备选）。

## 5. 处理流程与数据最小化

**质量闸门（免费，本地做）**：尺寸、清晰度、人脸占比不达标的抓拍，不调用腾讯云，直接计数后丢弃。腾讯云要求短边至少 64px、建议人脸不小于 80px；`NoFaceInPhoto` 仍计费。紧裁剪小图送检失败时，可用背景图按原坐标扩边重裁再试一次，**不要直接送整张背景图**（其中可能有多人，会搜错脸）。

**调用方式**：每张人脸只发**一次** `SearchFacesReturnsByGroup`，`GroupIds` 同时带上 `staff` 与（该店启用不受欢迎人员名单时）`unwelcome`，用 Base64 上传（人脸小图约 80–190KB，远低于 5MB 上限，也不依赖 COS 私有地址可达）。Region 显式传 `ap-singapore`，接入域名用 `iai.ap-singapore.tencentcloudapi.com`，不用就近解析域名。

**降本**（中国站文档：按调用次数计费时每个接口组默认 50 QPS 且不可调整；国际站的 QPS 配额未证实。我们平均约 1 次/秒，起步够用）：
1. 两个库合并成一次调用（比各搜一次省一半）。
2. 本地质量闸门先过滤，失败不上传。
3. 同一摄像机短时间内相同请求体哈希的重复报文不再搜。
4. 员工命中后，同一摄像机 N 秒内的相邻抓拍不再搜（N 在试点中定，起步 30 秒）。

**命中后的处理**：

| 结果 | 动作 | 保留 |
|---|---|---|
| 员工命中 | 当日该店员工计数 +1 | 不留图、不留原始报文；只留计数 |
| 命中（分数 ≥ 名单阈值） | 建「待复核」任务，存人脸图（COS，7 天），通过通知平台（#718）给该店有权限的人发「疑似」提示 | 复核结束后 N 天删（起步 7 天，由律师定）；条目与复核结论长期留痕 |
| 无命中、质量达标，且开启顾客去重 | 当日该店访客库里新建一人，「唯一访客」计数 +1 | 访客库次日删除；图片与原始报文立即删除，不留事件行 |
| 命中当日访客库 | 「重复抓拍」计数 +1，不新建人 | 图片与原始报文立即删除 |
| 无命中且未开启去重，或质量不达标 | 当日该店「非员工」或「低质量」计数 +1 | 立即删除图片与原始报文，不留事件行 |
| 腾讯云调用失败 | 处理器退避重试；超过重试上限的**丢弃并记失败计数** | 不因调用失败而长期留存生物数据 |

**当前的 `Capture` 模式保留**：现在 1042 的测试摄像机仍需要「全部留存并能在后台看图」来联调。它只允许用在**只拍知情同意的内部人员**的门店，由前置条件字段 `AllowedSubjects` 标明；切到 `Match` 后，看图功能只对命中有效。

## 6. 数据模型（新增，需独立版本号迁移）

> 本节是第一版的概念模型。**第一期的具体表结构以第 13.3 节为准**（两处字段有出入时以 13.3 为准）；不受欢迎人员名单相关的表（`FaceUnwelcomeEntry`、`FaceMatchTask`）留到第 2 期再建。

所有表在 HBweb，时间一律 UTC。

**FaceStoreProfile**（每店一行，决定处理模式）
- `StoreCode` PK、`Mode`（0 Off、1 Capture、2 Match）、`StaffExclusionEnabled`、`UnwelcomeListEnabled`
- `AllowedSubjects`（Capture 模式必填：说明拍摄对象，例如「仅内部测试员工」）
- 前置条件：`PiaReference`、`SignageInstalledOn`、`StaffNoticeDate`（NSW 必填）、`LegalSignoffBy`、`LegalSignoffAt`
- `UpdatedAtUtc`、`UpdatedBy`
- **服务端校验**：`Mode=Match` 或 `UnwelcomeListEnabled=true` 时，上述前置字段缺项则拒绝保存；`UnwelcomeListEnabled` 另需 `LegalSignoffAt` 晚于不受欢迎人员名单专项评估日期。人脸服务启动与每 30 秒刷新读取，失败时**按 Off 处理**（宁可不识别，也不在配置读不到时放开）。

**FaceConsentRecord**（员工同意记录）
- `ConsentId` PK、`UserGuid`、`ConsentVersion`（同意书版本）、`SignedAtUtc`、`Method`（纸质扫描/电子签）、`EvidenceObjectKey`（同意书扫描件，存在私有桶独立前缀）、`NswNoticeDate`、`WithdrawnAtUtc`、`WithdrawnReason`
- 撤回后：立即对应 `DeletePerson`、删登记照，记录保留（证明同意与撤回的时间线）。

**FaceEnrollment**（员工登记）
- `EnrollmentId` PK、`UserGuid`、`ConsentId` FK、`TencentPersonId`、`GroupKey`（`staff`）、`EnrolledAtUtc`、`EnrolledBy`、`Status`（Active/Withdrawn）
- 登记照另存私有桶（腾讯云不能导出人脸），仅同意有效期内保留，撤回即删。

**FaceUnwelcomeEntry**（不受欢迎人员名单条目）
- `EntryId` PK、`TencentPersonId`、`ReasonCategory`（枚举，不收自由文本中的个人隐私细节）、`EvidenceRef`（事件记录编号，不放图片）、`ApprovedBy`、`ApprovedAtUtc`、`ReviewDueAtUtc`、`ExpiresAtUtc`（**必填**，上限起步 12 个月）、`StoreScope`（可限定门店）、`Status`
- 到期自动停用并 `DeletePerson`；提供申诉与更正的受理字段（`DisputeNote`）。

**FaceMatchTask**（命中的复核任务；员工命中不建任务）
- `TaskId` PK、`StoreCode`、`CameraId`、`CapturedAtUtc`、`EntryId`、`Score`、`ImageObjectKey`（仅此类保留图）、`ReviewStatus`（Pending/Confirmed/Dismissed）、`ReviewedBy`、`ReviewedAtUtc`、`ImageDeleteAtUtc`
- 命中提示文案固定为「**疑似**匹配，需要人工确认」；确认流程至少两人（店员加经理）；不得自动拒绝服务。

**FaceTrafficBucket**（只有计数，无生物信息）
- `StoreCode`、`BucketStartUtc`（小时）、`CaptureCount`、`StaffMatchCount`、`NonStaffCount`、`LowQualityCount`、`ApiFailureCount`、PK 为 `(StoreCode, BucketStartUtc)`。

**FaceAuditLog**（持久审计，取代「只在容器日志里」）
- `AuditId`、`AtUtc`、`ActorId`、`ActorName`、`Action`（看图、登记、撤回、入不受欢迎人员名单、复核、改门店模式…）、`StoreCode`、`TargetRef`、`Result`；不含图片、对象键、密钥。保留期由律师定，起步 2 年。

**数据库保留策略**：现有 `FaceCaptureEvent` / `FaceCaptureImage` 无清理任务。`Capture` 模式的行，按 7 天与 COS 图片同步清理；`Match` 模式不再写这两张表（除命中）。清理任务写进本设计的第一期。

## 7. 员工登记与同意（自愿路径）

1. 在后台「人脸管理」新增「员工登记」：选员工，上传 3–5 张登记照，**必须先有有效的同意记录**才能提交。
2. 同意书要点（交律师定稿）：用途仅限客流统计中排除员工，**不用于考勤、绩效或其他用途**；自愿，不同意或撤回无任何不利后果；离职或撤回即删除；告知数据发送到腾讯云新加坡地域；与劳动合同分开单独签署。
3. NSW 门店员工：登记前至少 14 天书面通知；同意记录里保存通知日期，不满 14 天后端拒绝。
4. 不愿登记的员工：他们仍会被摄像机拍到并按「非员工」计数。提供替代办法（例如把员工区域排除出拍摄范围、或员工统计用考勤数据近似修正），避免变相强制。
5. 撤回入口：员工本人在个人信息页申请，门店经理确认后立即删除；操作进审计表。

## 8. 客流统计：顾客人脸去重（按用户决定纳入第一期，带独立闸门）

**风险说明（不因决定而消失）**：调研认为对顾客做人脸去重没有已知的合法路径——OAIC 对 7-Eleven（2021）认定用人脸特征给顾客去重统计，属于不「合理必要」的敏感信息收集，并命令销毁；例外条款只覆盖严重威胁与违法活动，不覆盖「数人头」；门口告示不等于同意。【待律师确认】这件事在你们的做法下能否成立、需要什么条件。用户已决定要做，设计据此把风险压到最小，并用闸门保证「没有律师确认就不会对真实顾客运行」。

**设计**

1. **数据生命周期**：每店每天一个访客库 `v{店}_{yyyyMMdd}`（按门店本地营业日）；不跨天、不跨店、不与任何身份关联；库里的人只有腾讯云生成的 PersonId，没有姓名或任何业务标识；门店本地午夜之后 2 小时内整库删除，只留计数。
2. **一次调用判三种情况**：`SearchFacesReturnsByGroup` 同时搜 `staff` 与当日访客库。命中员工 → 员工计数；命中访客 → 重复抓拍；都没命中且质量达标 → `CreatePerson` 进访客库并计「唯一访客」；质量不达标 → 低质量计数。
3. **口径**：「唯一访客数」= 当日访客库里新建的人数，是**估算值**：摄像机角度、距离、遮挡会造成「同一人被认成两人」（偏高）或「两人认成一人」（偏低）。页面展示时标注「估算」。
4. **串行与一致性**：同一家店的处理必须串行（现有处理器本身是串行），避免相邻两条抓拍同时判成「新访客」而重复建人。腾讯云新建人员后立刻搜索是否可见，**官方未说明**，试点必须实测；有延迟时用「同一摄像机 N 秒内的抓拍合并」补偿，并把补偿写进口径说明。
5. **闸门（后端强制）**：`TrafficDedupEnabled` 打开需要同时具备：`PiaReference`、`SignageInstalledOn`（告示文案明确写出「使用人脸识别技术统计客流」）、`DedupLegalSignoffBy/At`（针对去重这一用途的律师确认，与不受欢迎人员名单的确认分开）。在没有这些之前，去重只能在 `Capture` 模式下对**内部知情人员**验证。
6. **退路**：律师结论不可做时，关闭这个开关；员工排除和「抓拍次数」口径不受影响。
7. **非人脸备选**（若最终不能做人脸去重）：摄像机自带的区域或越线人数统计（该型号是否支持待现场确认）、门口计数传感器、POS 交易笔数近似。

## 9. 风险与待确认事项

1. **调用腾讯云就是披露**：Match 模式下每张抓拍（含无命中者）都会发到腾讯云新加坡地域。腾讯云文档对「图片是否保存」自相矛盾（国际隐私条款称提取特征后不保留；中国站文档暗示库里留有图片，并称人员库图片丢失需要重传）。**必须书面向腾讯云确认**不保留非库内图片、不用于训练，并取得国际 DPSA；【待律师确认】跨境披露如何在告示与隐私政策里表述。
2. **适用法域**：中国站账号的主协议适用中国大陆法律、争议由深圳南山区法院管辖；用国际站账号是否更合适，需律师与用户评估。
3. **训练用途**：腾讯云国际隐私条款写明人脸特征可用于「改进和优化训练模型」，法律依据是合法利益，敏感数据靠最终用户同意覆盖，**没有退出机制的说明**。这与我们的用途限定直接冲突，需要书面确认能否关闭。
4. **腾讯云 3.0 库搜索没有官方分数与误识率对照表**。阈值只能在试点中用本地数据标定；名单阈值要明显高于官方最严档（以下为粗算，不是官方数据：取 1:1 比对表里 3.0 的 60 分约对应十万分之一，库里 200 人时单次搜索误报约 0.2%，单店一天约 6 次、30 店约 180 次）。
5. **顾客以澳洲人为主，缺乏对应人群的可引用评测**，不能假设误识率与官方一致。
6. **数据库保留缺口**：必须先补清理任务（第 6 节），否则抓拍元数据无限期累积。
7. **腾讯云不能导出库中人脸**：登记照必须由我们自己留存，撤回时两处都要删。
8. **库闲置清理**：官方称超过 1 年未调用的人员库会被删除，需要定时调用保活或监控库是否还在。
9. **顾客人脸去重是 OAIC 已有先例认定违法的做法**（见第 8 节）。系统用闸门保证没有独立的律师确认就不会对真实顾客运行，但**闸门只是记录，不能替代法律判断**。
10. **腾讯云的 `CreatePerson` 不支持按库限定权限**（CAM 资源级策略只覆盖部分接口）。接收端为了建访客，持有的子账号理论上能往任何库建人，包括员工库。补偿措施：员工库的写入只经主 API；主 API 每日对比员工库人数与有效登记数，不一致就告警并记审计。
11. **数据在中国大陆处理（用广州）**：这比新加坡多一层跨境披露：数据落在大陆、受中国法律管辖、服务商可能被要求配合调取。**【待律师确认】**：对澳洲隐私法 APP 8（我们对境外接收方的合规负责）的影响、告示与隐私政策的写法、员工同意书的写法；以及是否因此要改回新加坡或国际站。**系统用「跨境披露审查」闸门保证：没有该记录，不对真实顾客运行。**

## 10. 成本估算（调研数据，价格会变，上线前以账单为准）

| 方案 | 每月调用量（低/高） | 中国站（人民币） | 国际站（美元） |
|---|---|---|---|
| 员工排除 + 不受欢迎人员名单，一次调用同时搜两库（本设计） | 90 万 / 270 万 | 约 2,880 / 8,640 | 约 720 / 2,160 |
| 对比：两库各搜一次 | 180 万 / 540 万 | 约 5,760 / 16,200 | 约 1,440 / 3,240 |
| 对比：再加顾客去重（不做） | 在上面基础上加建人 | 约 3,840 / 11,520 | 约 960 / 2,880 |

- 单店 1 个月约 3–9 万次调用，中国站约 100–290 元（扣免费额度约 64–256 元）。
- **第一期 2 家店（含顾客去重，新访客按抓拍的 1/3 估）**：每月约 6–18 万次搜索加 2–6 万次建人，中国站约 **250–770 元/月**（国际站约 US$60–190）。
- 中国站账号调用新加坡地域时，是按人民币价目表还是国际站美元价计费、是否享受每月 1 万次免费额度，**官方未说明，未证实**，上线前以首月账单核对。

## 11. 分期实施（第二版）

| 期 | 内容 | 前置 |
|---|---|---|
| 0 | 本文评审；保持现状：1042 只拍参与测试的内部人员；补库保留清理任务；向腾讯云书面确认第 9 节 1、3 两点；腾讯云账号准备（第 13.5 节） | 用户决策 |
| 1（**2 家店**） | 引擎抽象；`FaceStoreProfile` 与前置条件校验；员工登记与同意记录；`Match` 模式：**员工排除 + 顾客人脸去重（独立闸门）**；访客库生命周期；计数桶与统计页；持久审计；在 `Capture` 模式的内部人员店做**阈值标定**；数据库保留清理 | 同意书定稿；腾讯云子账号；DB 迁移；**去重的律师确认、隐私影响评估、门口告示（对真实顾客运行前）** |
| 1（**同期暗发布**） | 不受欢迎人员名单：条目管理（严重事件类别、证据、批准、到期）、复核任务与两人确认、通知（#718）、命中图保留与清理；**上线即关闭，各门店须单独满足第 14.1 节闸门才能启用** | 开发不需要前置；**启用**需要律师确认、不受欢迎人员名单专项评估、门口告示 |
| 2 | 不受欢迎人员名单在具体门店启用与运行 | 第 14.1 节闸门 |
| 3 | 规模扩大后的自建迁移（第 13.3 节） | 触发条件见 13.3；模型授权或选型 |

## 12. 决策状态

**已决定（2026-10-13）**：第一期上线 2 家店；先用腾讯云，规模变大再改自建；顾客人脸去重要做。

**仍待确认**
1. 第一期是哪 2 家店？**已确定一家：QLD 的 Orion（库里门店编码 1013，Australia/Brisbane；另有一条地址相同的 2015 `spo`，待用户确认哪个才是）**；另一家待定（门店编码、所在州；NSW 门店员工登记要提前 14 天书面通知，QLD 目前没有专门的工作场所监控法，约束主要来自联邦隐私法）。摄像机是否已安装？
2. ~~腾讯云账号与地域~~：**已决定（2026-10-13）用中国站账号 + 广州**（新加坡在该账号下未开通）。仍待律师评估大陆地域的跨境影响（第 9 节第 11 点）；账号或地域将来可改。
3. 员工同意书由谁起草、谁收集；登记照谁拍。
4. 对真实顾客运行前，隐私影响评估、门口告示、律师确认（去重用途）由谁负责、预计何时完成。
5. 数据库里 `Capture` 模式的抓拍记录保留几天（建议 7 天）；持久审计表保留几年（建议 2 年，律师定）。
6. 不受欢迎人员名单入名单的严重事件类别与证据标准（第 14.2 节草案）由谁定稿；不受欢迎人员名单专项评估与律师确认由谁负责、何时完成。

## 13. 第一期实现规格（2026-10-13）

### 13.1 范围

- **做**：引擎抽象与腾讯云实现；门店档案与闸门；员工同意与登记；`Match` 模式（员工排除 + 顾客去重）；访客库生命周期；计数与统计页；持久审计；数据库保留清理；阈值标定（内部人员门店）。
- **不做**：基于人脸的跨天、跨店分析（命中除外，见第 14 节）；自建引擎（第 3 期）。
- **不受欢迎人员名单**：与第一期同时开发并暗发布（第 14 节）。

### 13.2 引擎可替换，以及将来改自建

- 抽象接口：`IFaceMatcher`（搜索：员工库 + 当日访客库）、`IFaceLibrary`（员工库、访客库的建删，主 API 使用）、`IVisitorRegistry`（访客库的「查找或新建」语义）。腾讯云实现放在独立类里，业务逻辑不出现任何腾讯云类型。
- **腾讯云客户端**：不引入官方 SDK，用 `HttpClient` 自实现 TC3-HMAC-SHA256 签名（与现有 COS 签名同一做法，依赖最少，可用假的 HTTP 处理器测试）；签名实现必须通过腾讯云文档给出的官方签名示例向量测试。Region 从配置读取（`FaceMatch:Tencent:Region`，默认 `ap-guangzhou`），**缺省或不在白名单（`ap-guangzhou`、`ap-shanghai`、`ap-singapore`）时启动失败**，不静默回退；域名用 `iai.<region>.tencentcloudapi.com`。
- **迁移到自建要付出的代价**：
  - 员工库：用我们自己保留的登记照在新引擎里重新登记；腾讯云不能导出库中人脸，所以**登记照必须自行留存**（本设计已要求）。
  - 访客库：每天重建，不需要迁移。
  - 阈值：新引擎必须重新标定。
  - **模型许可**：自建前必须先解决。InsightFace 的预训练模型只限非商业研究，商用需书面授权（价格不公开）；SFace、AuraFace 的训练数据权利链不透明；AWS Rekognition 悉尼区是调研里数据能留在澳洲的云端方案，也可作为「改引擎」的另一选项。
- **建议的迁移触发条件**（用户可调整）：月调用量持续超过 300 万次（计费进入下一档）；或月成本超过预算；或腾讯云合规确认不能满足要求；或需要数据留在澳洲。

### 13.3 数据模型（独立版本号迁移，如 `20261014.001-face-match`，照 `FaceCaptureSchema` 的写法；时间一律 UTC）

**FaceStoreProfile**：`StoreCode nvarchar(32)` PK；`Mode tinyint`（0 Off、1 Capture、2 Match，默认 0）；`EvaluateMatch bit`（Capture 模式下评估匹配并记分数，用于标定）；`StaffExclusionEnabled bit`；`TrafficDedupEnabled bit`；`UnwelcomeListEnabled bit`（第 2 期，先建列）；`AllowedSubjects nvarchar(200)`；`PiaReference nvarchar(100)`；`SignageInstalledOn date`；`StaffNoticeDate date`；`LegalSignoffBy nvarchar(100)`、`LegalSignoffAt datetime2`；`DedupLegalSignoffBy nvarchar(100)`、`DedupLegalSignoffAt datetime2`；`CrossBorderRegion nvarchar(32)`（实际处理地域，例如 `ap-guangzhou`）、`CrossBorderReviewRef nvarchar(100)`（跨境披露审查编号）、`CrossBorderReviewedBy nvarchar(100)`、`CrossBorderReviewedAt datetime2`；`TimeZoneId nvarchar(64)`（空则取门店表）；`UpdatedAtUtc`、`UpdatedBy`。

**跨境披露审查闸门（后端强制）**：`Mode=Match`、`StaffExclusionEnabled`、`TrafficDedupEnabled`、`UnwelcomeListEnabled` 中任何一项对真实顾客生效前，都必须有 `CrossBorderRegion` 与 `CrossBorderReviewRef/By/At`；`CrossBorderRegion` 必须等于当前配置的处理地域，**地域配置被改动后，所有门店的审查记录作废，须重新审查**。`Capture` 模式（仅内部知情人员）不要求，但同意书必须写明数据在该地域处理。

**FaceConsentRecord**：`ConsentId char(32)` PK；`UserGuid nvarchar(50)`；`ConsentVersion nvarchar(32)`；`SignedAtUtc`；`Method nvarchar(16)`（paper/electronic）；`EvidenceObjectKey nvarchar(400)`；`NswNoticeDate date NULL`；`WithdrawnAtUtc NULL`；`WithdrawnReason nvarchar(200) NULL`；`CreatedAtUtc`、`CreatedBy`。

**FaceEnrollment**：`EnrollmentId char(32)` PK；`UserGuid`；`ConsentId char(32)`；`TencentPersonId nvarchar(64)`；`PhotoObjectKeysJson nvarchar(2000)`（登记照在私有桶独立前缀下的键）；`Status tinyint`（1 Active、2 Withdrawn）；`EnrolledAtUtc`、`EnrolledBy`；`WithdrawnAtUtc NULL`。按 `UserGuid` 在 `Status=1` 上建唯一过滤索引。

**FaceVisitGroup**：`GroupId nvarchar(64)` PK；`StoreCode`；`BusinessDate date`；`CreatedAtUtc`；`DeleteAfterUtc`；`DeletedAtUtc NULL`；`VisitorCount int`。

**FaceTrafficBucket**：`StoreCode`、`BucketStartUtc`（整点）联合 PK；`CaptureCount`、`StaffMatchCount`、`UniqueVisitorCount`、`RepeatCount`、`LowQualityCount`、`ApiFailureCount`，均 `int NOT NULL DEFAULT 0`。

**FaceMatchEvaluation**（仅 `Capture` 且 `EvaluateMatch` 的门店，用于标定）：`CaptureId char(32)` PK；`StaffScore decimal(9,4) NULL`；`VisitScore decimal(9,4) NULL`；`Decision tinyint`。

**FaceAuditLog**：`AuditId bigint identity` PK；`AtUtc`；`ActorId`、`ActorName`；`Action nvarchar(48)`；`StoreCode NULL`；`TargetRef nvarchar(100) NULL`；`Result nvarchar(32)`；不含图片、对象键、密钥。

**权限码**：`FaceMatch.Manage`（门店档案、同意、登记、审计）、`FaceMatch.Traffic.View`（客流统计页）。按 #727、#737 的完整登记清单做全（后端常量与种子、权限入库 SQL、移动端两份文案、Web 类型与入口规则）。

**数据库账号授权**（`face_ingest_app` 追加）：`FaceStoreProfile` SELECT；`FaceTrafficBucket` SELECT、INSERT、UPDATE；`FaceVisitGroup` SELECT、INSERT、UPDATE(`VisitorCount`)；`FaceMatchEvaluation` INSERT。仍不授 DELETE、不授角色。

### 13.4 主 API 与 Web（`/api/react/v1/face-match/...`）

| 接口 | 说明 | 权限 |
|---|---|---|
| `GET/PUT /profiles/{storeCode}` | 门店档案；PUT 做闸门校验：`Mode=Match` 需 PIA、告示日期、`LegalSignoff*`，NSW 门店另需 `StaffNoticeDate`；`TrafficDedupEnabled` 另需 `DedupLegalSignoff*`；`StaffExclusionEnabled` 需存在有效的同意与登记；`Capture` 模式必须填 `AllowedSubjects` | Manage |
| `POST /consents`、`POST /consents/{id}/withdraw`、`GET /consents` | 同意记录（上传同意书扫描件到私有桶独立前缀）；NSW 员工登记前通知不满 14 天拒绝 | Manage |
| `POST /enrollments`、`POST /enrollments/{id}/withdraw`、`GET /enrollments` | 员工登记（3–5 张登记照，先有有效同意）；撤回时 `DeletePerson` 并删登记照 | Manage |
| `GET /traffic?store&from&to` | 计数桶汇总：抓拍数、员工、唯一访客（估算）、重复、低质量、调用失败 | Traffic.View |
| `GET /audit` | 审计查询 | Manage |

- 后台任务：①访客库清理（门店本地午夜后 2 小时内删库，更新 `FaceVisitGroup`，并扫描遗漏的过期库）；②员工库完整性检查（每日对比员工库人数与有效登记数）；③`Capture` 模式记录的保留清理（`FaceCaptureEvent` / `FaceCaptureImage` 超过保留天数的行删除）。
- Web 页面「人脸比对」：门店档案与前置条件表单（闸门不满足时给出缺哪一项）、同意与员工登记、统计页（标注「唯一访客数为估算」）。文案放页面级消息文件。
- 主 API 持有「管理」腾讯云子账号：`CreateGroup`、`CreatePerson`、`CreateFace`、`DeletePerson`、`DeleteFace`、`DeleteGroup`、`GetPersonList`、`GetGroupInfo`、`GetGroupList`。

### 13.5 人脸服务（接收端处理器）

- 每 30 秒读取 `FaceStoreProfile`；**读取失败或没有该店档案按 `Off` 处理**。
- `Off`：不调用腾讯云；按保留策略不留生物数据。
- `Capture`：现有行为不变（全部留存，仅限内部知情人员）；`EvaluateMatch=true` 时额外调用匹配并把分数写入 `FaceMatchEvaluation`。
- `Match`：质量闸门 →（员工排除与去重按档案开关）一次 `SearchFacesReturnsByGroup` → 按第 5、8 节动作 → 更新 `FaceTrafficBucket` → **删除本地原始报文与图片，不上传 COS，不写事件行**。腾讯云调用失败时有限次退避重试，超限则丢弃并计 `ApiFailureCount`。
- 阈值配置：`FaceMatch:StaffThreshold`、`FaceMatch:VisitorThreshold`，默认取偏严的值，由标定结果设定；可按门店覆盖。
- 同一店串行处理；同一摄像机短时间相同请求体哈希不重复搜索。
- 接收端的腾讯云子账号：`SearchFacesReturnsByGroup`、`CreateGroup`、`CreatePerson`（见第 9 节第 10 点的权限限制说明）。

### 13.6 腾讯云账号准备（需用户在控制台完成，我不代做）

1. 开通人脸识别服务并接受服务条款（需要账号持有人）。
2. 新建两个 CAM 子账号（只勾「编程访问」）：**主 API 管理账号**与**接收端搜索账号**，分别绑定上面列出的最小权限策略；策略 JSON 我会在实现时给出。
3. 密钥只写进服务器 `.env`（不贴对话）：我会给出写入命令，输入时不回显。
4. 向腾讯云书面确认第 9 节第 1、3 点，并获取国际数据处理协议。

### 13.7 测试要点

- 假的腾讯云 HTTP 处理器；官方签名向量；阈值边界；`Off`/`Capture`/`Match` 三种模式的行为与数据残留（`Match` 下不得留下本地图片、原始报文、COS 对象、事件行）；闸门校验（缺项拒绝、读取失败按 `Off`）；同一店相邻两条相同抓拍只建一人；访客库删除与过期库扫描；撤回同意后腾讯云与登记照两处都删；NSW 14 天校验；审计覆盖每个动作；日志与审计不含图片、对象键、密钥。
- 时间相关用例在 `TZ=UTC` 与 `America/Los_Angeles` 各跑一遍（门店本地营业日、午夜后删库）。

## 14. 不受欢迎人员名单（原「黑名单」，按用户决定同期开发、默认关闭）

用户需求：对被确认的盗窃者等「不受欢迎的人」做人脸比对，命中时提示店员，并给出匹配相似度百分比。**2026-10-13 用户要求把「黑名单」改称「不受欢迎的人」**，文档与界面一律用「不受欢迎人员名单」，内部标识用 `Unwelcome`（库 `unwelcome`、表 `FaceUnwelcomeEntry`、权限 `FaceMatch.Unwelcome.*`）。

### 14.1 法律边界与启用闸门

- **理由必须落在「严重威胁或违法活动」**：OAIC 指南与 Bunnings 复审认可的是有限目的——打击严重零售犯罪、保护员工与顾客免受暴力、辱骂、威胁；Kmart 用于退款欺诈被认定不相称（存在更小的替代办法）。**普通的小额偷窃很可能更接近 Kmart 的情形**。【待律师确认】你们的门店与事件频次能否满足。
- **按门店自证**：OAIC 指出不同门店可能要分别评估。每个门店启用前必须记录：
  - `UnwelcomeAssessmentRef`（不受欢迎人员名单专项评估编号，要有该店的事件频次、已采取的替代措施、相称性分析）；
  - `UnwelcomeLegalSignoffBy/At`（针对不受欢迎人员名单这一用途的律师确认，与去重的确认分开）；
  - `PiaReference`、`SignageInstalledOn`（告示明确写「使用人脸识别技术识别有过严重事件的人员」）；
  - 员工培训记录日期与负责的隐私联系人。
- 缺任何一项，后端拒绝打开 `UnwelcomeListEnabled`；读取档案失败按关闭处理。
- **例外（2026-10-13 用户决定，与顾客去重同一原则）**：`Capture` 模式下（只处理知情同意的内部人员，必须已填 `AllowedSubjects`）名单**不要求上述专项记录与跨境审查**，用于上线前用内部员工照片做端到端测试；接收端在 Capture 下搜名单库并建复核任务，但不写计数、不改变 Capture 的留存。**切到 `Match`（真实顾客）时全部记录重新要求**，缺了保存不了。这一放宽不改变「对真实顾客运行前必须完成评估、律师确认和告示」的要求；Capture 摄像机必须不朝向顾客，这一点由部署位置保证，系统无法检查。
- **2026-10-13 用户决定：Match 闸门全部改为非阻塞警告**（「Match 全部闸门改成警告，不再拦截」）。用户已被明确告知风险：对真实顾客运行前法律上需要这些记录，系统无法替代法律判断；风险由用户承担。含义是**把「拦截」变成「可见的警告」，不是删除检查**：
  - 缺 `PiaReference`、`SignageInstalledOn`、`LegalSignoffBy/At`、跨境披露审查（地域 / 编号 / 审查人 / 时间）、NSW 员工通知日期（含距今不足 14 天）、去重专项律师确认、名单专项记录（评估 / 律师确认 / 培训 / 隐私联系人等）时，**保存和启用不再被拒绝**；
    缺口进入闸门报告的 `warnings`（后台每个被请求的功能下红色提示，保存按钮旁常驻红字；门店列表打「合规记录未齐（N）」标记）；保存带警告时审计 `profile.update` 的 `Result` 写 `mode=N;warnings=M`。
  - 接收端不再因跨境审查缺失 / 地域不符把 Match 降级为 Capture / Off，也不再因名单专项记录不全关闭名单；只按门店节流记一条 Warning（`match-without-legal-records`）。主 API 的地域改动任务只清空审查记录并写审计，**不再强制关闭档案**。
  - **2026-10-13 起这些结构性 / 功能性前提也只给警告，不再拦截保存（用户决定「有问题只给提示」）**：Capture 应声明 `AllowedSubjects`、评估匹配只在 Capture 下生效、员工排除 / 去重 / 名单在 `Off` 下不会生效、员工排除没有有效登记时没有可排除的员工（员工库可能还不存在，搜索可能报错并计入调用失败）；字段格式 / 取值校验（400）不变；接收端仍要求匹配可用、名单需要私有 COS、去重需要可解析的时区。字段格式 / 取值校验（400）不变。
  - 批准名单条目时的 `FACE_MATCH_UNWELCOME_NOT_ENABLED` 检查只看 `UnwelcomeListEnabled` 开关，不变。
  - 本节上面「缺任何一项，后端拒绝打开 `UnwelcomeListEnabled`」与「读取档案失败按关闭处理」中，前者已被本决定取代（现为警告），后者不变。
    同理，第 8 节第 5 点、第 13.3 / 13.4 / 13.5 节中「缺记录则后端拒绝 / 接收端降级 / 配置地域改动后强制关闭」的表述，凡涉及上述合规记录的，一律以本决定为准（这些章节未逐条改写）。运维口径见 `docs/runbooks/face-ingest.md` 第 16.3、16.7、17.4 节。

### 14.2 入名单的硬条件（系统强制）

1. **事件类别必须是枚举**：`ViolenceOrThreat`（暴力或威胁员工 / 顾客）、`OrganisedOrRepeatTheft`（有组织或有记录的反复盗窃）、`Other`（须额外说明，需第二人批准）；**没有「一般偷窃」这一项**，这是把 Bunnings 与 Kmart 的区别落到系统里。
2. **证据引用必填**：事件记录编号或警方报告编号，不上传图片到条目、不填自由文本的个人隐私细节。
3. **两个人**：提交人 ≠ 批准人，批准人须具备指定权限（`FaceMatch.Unwelcome.Approve`）。**例外（2026-10-13 用户决定）：管理员一个人批准即可生效**——管理员可以批准自己提交的条目，`Other` 类别也只需一位管理员批准；非管理员规则不变。自批与单人批准在审计里留痕（`active;self-approved` / `active;admin-solo`）。这削弱了「提交与批准分人」这道防止滥用的制衡，仅限管理员账号。命中后的**复核仍然是两个人**，未放宽。
4. **到期日必填**，上限起步 12 个月；到期自动停用并 `DeletePerson`；到期前 30 天提醒复核。
5. **适用范围**：可限定到具体门店；默认只对事件发生的门店生效。
6. **申诉与更正**：条目有 `DisputeNote` 与状态；被识别者提出查询或更正时，须在 30 天内处理（用人脸检索是否在名单里，本身就会产生新的识别数据，要有固定流程和审计）。
7. **名单照片来源**：只用该事件现场抓拍或监控截图，入库到腾讯云 `unwelcome` 库；照片同时按条目有效期留存在私有桶独立前缀，条目失效即删。

### 14.3 命中与复核流程

- 接收端对每张抓拍的一次搜索同时带上 `staff`、`unwelcome` 与当日访客库。**名单阈值明显高于员工和去重的阈值**，由试点标定，默认取很严的值。
- 命中（分数 ≥ 名单阈值）：
  1. 建 `FaceMatchTask`（待复核），保存该张人脸图（私有桶，7 天）；
  2. 通过通知平台（#718）给**该店**持有 `FaceMatch.Unwelcome.Review` 权限的人发提示，文案固定：**「疑似匹配，需要人工确认」**，不显示姓名、不显示事件细节；
  3. 复核：**一个人就够（2026-10-13 用户决定，取代原先「至少两人：店员核对 + 经理确认」）**——有复核权限的人判断一次即结案，「是同一个人」→ `Confirmed`，「不是」→ `Dismissed`；旧数据里「已一审待二审」的任务由下一位复核人直接结案；
  4. **系统不自动拒绝服务、不自动通知警方**；店里怎么处置由门店流程与法务制定。
  5. **高相似度自动确认（2026-10-13 用户决定）**：相似度达到或超过门店阈值（`FaceStoreProfile.UnwelcomeAutoConfirmScore`，默认 90，0 = 关闭，Web 可设置）的命中任务由系统自动标为 `Confirmed`，不再等人工复核；**提示照发**，复核人记 `system`，自动确认的任务在误报统计里**单独计数**、不参与驳回率。「已确认」只是任务状态，不等于「是同一个人」，也不触发任何自动处置。运行细节见 runbook 16.6。
- 未命中、命中后被驳回：图片按保留策略删除，驳回的任务只保留元数据与分数用于误报统计。
- **误报监控**：按条目统计命中数、被驳回比例；某条目驳回率持续偏高，自动提示复核该条目是否应出名单。

### 14.3.1 匹配相似度百分比的展示（用户要求）

- **显示的是「相似度」，不是「是同一个人的概率」**：腾讯云返回 0–100 的相似度分数；官方对 3.0 库搜索没有分数与误识率的对照表。界面显示「相似度 87%」没问题，但**必须同时写明「这是相似度，不是同一个人的概率」**，否则店员容易把 87% 理解成「87% 就是这个人」。
- **按标定后的阈值给档位**：试点标定后分「较高 / 一般」两档（低于不提示的阈值不出现）；页面同时显示相似度百分比与档位。档位阈值不照搬官方表，用本地数据标定。
- **复核页并排显示两张照片**：现场抓拍 + 该条目的参考照片（仅持有 `FaceMatch.Unwelcome.Review` 的人在复核页可见，走服务端转发与审计）。没有并排对比，人工复核就名存实亡。
- **提示文案**：「疑似匹配（相似度 87%，**不等于是同一个人的概率**），需要人工核对确认」（通知本身只用英文：`Possible match (similarity 87%). The score is not the probability that this is the same person; a reviewer must check it.`）；不显示姓名与事件细节。注意中文不要写成「不是同一个人的概率」——会被读成「不是同一个人」的概率，意思正好相反（2026-10-13 用户读成了这个意思，已统一改成「不等于是同一个人的概率」）。
- **记录**：相似度写入 `FaceMatchTask.Score`；误报统计按相似度区间看驳回比例，用于调阈值。
- **只给有权限的人看**：相似度与参考照片只在复核页与提示里出现；统计页只显示计数。

### 14.4 数据模型补充（与 13.3 同一迁移）

**FaceUnwelcomeEntry**：`EntryId char(32)` PK；`TencentPersonId nvarchar(64)`；`IncidentCategory tinyint`；`EvidenceRef nvarchar(100)`；`StoreScope nvarchar(500)`（门店编码列表，空表示仅发生门店，由应用层写具体值）；`SubmittedBy`、`SubmittedAtUtc`；`ApprovedBy`、`ApprovedAtUtc`；`ReviewDueAtUtc`；`ExpiresAtUtc`（NOT NULL）；`Status tinyint`（Pending/Active/Expired/Withdrawn）；`PhotoObjectKeysJson nvarchar(2000)`；`DisputeNote nvarchar(500) NULL`。

**FaceMatchTask**：`TaskId char(32)` PK；`StoreCode`；`CameraId`；`CapturedAtUtc`；`EntryId`；`Score decimal(9,4)`；`ImageObjectKey nvarchar(400)`（仅此类保留图）；`ReviewStatus tinyint`（Pending/Dismissed/Confirmed；2026-10-13 起一人确认即 Confirmed，原名 ConfirmedBySecondPerson，数值不变）；`FirstReviewerId`、`FirstReviewedAtUtc`；`SecondReviewerId`、`SecondReviewedAtUtc`；`ImageDeleteAtUtc`；`NotifiedAtUtc datetime2 NULL`（主 API 后台任务发出提示后写入）。

**FaceStoreProfile 追加列**：`UnwelcomeAssessmentRef`、`UnwelcomeLegalSignoffBy`、`UnwelcomeLegalSignoffAt`、`StaffTrainingDate`、`PrivacyContact`。

**权限码追加**：`FaceMatch.Unwelcome.Manage`（提交、查看条目）、`FaceMatch.Unwelcome.Approve`（批准，须与提交人不同）、`FaceMatch.Unwelcome.Review`（收到提示并参与复核，按门店授权）。

**数据库账号授权追加**（`face_ingest_app`）：`FaceUnwelcomeEntry` SELECT；`FaceMatchTask` SELECT、INSERT。

### 14.5 主 API 与 Web 追加

- 接口：`POST/GET /unwelcome/entries`、`POST /unwelcome/entries/{id}/approve`、`POST /unwelcome/entries/{id}/withdraw`、`GET /unwelcome/tasks`、`POST /unwelcome/tasks/{id}/review`（第一与第二复核人）。全部写 `FaceAuditLog`。
- Web：「不受欢迎人员名单」页面分三块：条目（提交、批准、撤回、到期提醒）、复核任务（看命中图、两人确认）、误报统计。看命中图沿用 #737 的服务端转发与审计，权限为 Review；文案中固定使用「疑似」。
- 后台任务：到期条目自动停用与 `DeletePerson`；命中任务的图片到期删除；到期前 30 天提醒。

### 14.6 风险

1. **理由不够会被认定不相称**（Kmart）。入名单的类别枚举与证据要求就是为此设计；但每个门店的事件频次是否足够，是律师与评估的问题，不是系统能判断的。
2. **误识**：库里 200 人时，即使取官方最严档，30 家店每天仍可能有数百次误报（第 9 节粗算）；所以必须人工复核、阈值要标定，且「疑似」文案不能省。
3. **顾客被错误拒绝服务或被指控的风险在门店流程里**，不在系统里；系统只给提示，不处置。需要门店处置流程与培训，并让法务确认话术。
4. **自动化决策披露**：2026-12-10 起新增的透明度要求，不受欢迎人员名单提示若影响拒绝服务可能落入，隐私政策要同步写。
5. **腾讯云训练用途、跨境披露**同第 9 节，名单照片是更敏感的数据，必须先取得腾讯云的书面确认。

## 15. 组件边界与契约（实现分工依据）

- **迁移与实体只由主 API 侧写**（`20261014.001-face-match`，含 13.3 与 14.4 的全部表与列，列名以本文为准）；人脸服务不写迁移。
- **人脸服务**（接收端处理器）：腾讯云搜索与建访客；读 `FaceStoreProfile` / `FaceUnwelcomeEntry`；写 `FaceTrafficBucket`、`FaceVisitGroup`、`FaceMatchEvaluation`、`FaceMatchTask`。**它不能调用通知平台**（通知发布器只在主 API 里）。
- **主 API**：管理接口、员工与名单的腾讯云库增删（管理子账号）、访客库清理、员工库完整性检查、保留清理；**后台任务轮询 `FaceMatchTask.NotifiedAtUtc IS NULL` 的新任务，提交后经 `INotificationPublisher` 发提示**（`DedupeKey = unwelcome-hit:{TaskId}`，发布在事务之外、try/catch 包裹、失败只记日志并下轮重试；已知陷阱见 N3 调研：不得在业务事务内调用发布器，撤回后同一键不可再发）。
- **腾讯云凭据**：接收端 `FACE_MATCH_TENCENT_SECRET_ID/KEY`（搜索、建组、建人）；主 API `FACE_MATCH_ADMIN_SECRET_ID/KEY`（库管理）；**两者缺失时功能整体关闭（不是回退主密钥）**。主 API 不得使用 `TENCENT_SECRET_*` 主密钥调用 IAI。
- **默认全部关闭**：没有任何门店档案时，处理器按 `Off`；新代码上线不改变现有 `Capture` 行为。
