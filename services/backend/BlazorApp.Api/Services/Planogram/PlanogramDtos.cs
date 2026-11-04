namespace BlazorApp.Api.Services.Planogram;

/// <summary>商品陈列图接口的业务错误码（附录 A）。</summary>
public static class PlanogramErrorCodes
{
    /// <summary>字段不合法；<c>details</c> 给 <see cref="PlanogramFieldError"/> 列表。</summary>
    public const string Validation = "PLANOGRAM_VALIDATION";

    public const string NotFound = "PLANOGRAM_NOT_FOUND";

    /// <summary>型号编码与已有型号重复（货架型号、挂钩型号各自内唯一）。</summary>
    public const string CodeDuplicate = "PLANOGRAM_CODE_DUPLICATE";

    /// <summary>型号已被方案（或商品参数）引用，不能删除，只能停用。</summary>
    public const string ModelInUse = "PLANOGRAM_MODEL_IN_USE";

    /// <summary>商品陈列参数已被方案引用，不能清除（批 2）。</summary>
    public const string SpecInUse = "PLANOGRAM_SPEC_IN_USE";

    /// <summary>货架型号已被方案引用，外形 / 孔阵字段被锁定，不能修改（批 2）；要改请复制为新型号。</summary>
    public const string ModelLocked = "PLANOGRAM_MODEL_LOCKED";

    /// <summary>保存时 rowVersion 与库里不一致：方案已被别人（或另一个窗口）改过，请刷新后再保存。</summary>
    public const string Stale = "PLANOGRAM_STALE";

    /// <summary>方案已归档，不可保存；先恢复为草稿。</summary>
    public const string Archived = "PLANOGRAM_ARCHIVED";

    /// <summary>只有「草稿且从未发布」的方案才能删除。</summary>
    public const string NotDeletable = "PLANOGRAM_NOT_DELETABLE";

    // ---- 批 3（附录 C）

    /// <summary>发布时按存库内容复算仍有 error 级冲突；<c>details</c> 是冲突列表（<see cref="ConflictDto"/>）。</summary>
    public const string HasConflicts = "PLANOGRAM_HAS_CONFLICTS";

    /// <summary>方案没有任何明细，不能发布。</summary>
    public const string Empty = "PLANOGRAM_EMPTY";

    /// <summary>当前版本号已经有发布快照（未改动再次发布，或并发发布时唯一索引冲突）。</summary>
    public const string AlreadyPublished = "PLANOGRAM_ALREADY_PUBLISHED";

    /// <summary>导入文件里的货架 / 挂钩型号编码或商品货号在本环境解析不到；<c>details</c> 是 <see cref="PlanogramImportUnresolvedDto"/> 列表。</summary>
    public const string ImportUnresolved = "PLANOGRAM_IMPORT_UNRESOLVED";

    /// <summary>
    /// 附录 C 之外新增：历史版本的 <c>LayoutJson</c> 读不出来（被改坏 / 不是快照格式）。
    /// 明确报错而不是抛 500，前端提示「该版本快照已损坏」即可。
    /// </summary>
    public const string SnapshotInvalid = "PLANOGRAM_SNAPSHOT_INVALID";

    // ---- 附录 D：商品陈列参数批量修改

    /// <summary>批量修改的目标超过 2000 个商品；<c>details</c> 是 <see cref="PlanogramBatchLimitDetails"/>（带匹配数）。</summary>
    public const string BatchTooMany = "PLANOGRAM_BATCH_TOO_MANY";

    /// <summary>批量修改的目标为空，或按筛选没有匹配到任何商品。</summary>
    public const string BatchEmpty = "PLANOGRAM_BATCH_EMPTY";

    /// <summary>
    /// 批量执行时发生并发冲突：创建撞了主键 / 唯一约束（别人同时创建了同一货号的参数），
    /// 或更新的实际影响行数少于应更新行数（别人同时清除了参数）。整批已回滚，请重新预览后再执行。
    /// </summary>
    public const string BatchConflict = "PLANOGRAM_BATCH_CONFLICT";
}

/// <summary>校验失败时 <c>details</c> 里的一项：<c>field</c> 用请求 JSON 的字段名（camelCase），前端据此标红对应输入框。</summary>
public sealed record PlanogramFieldError(string Field, string Message);

/// <summary>
/// 货架型号。同一个 DTO 既用于响应也用于 POST / PUT 请求体：
/// 请求里 guid / referencedPlanCount / updatedAt / updatedBy 被忽略；holeCols / holeRows 缺省或为 0 时由后端自动换算。
/// </summary>
public sealed class ShelfModelDto
{
    public string Guid { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>1 单面 / 2 双面。</summary>
    public int SideType { get; set; } = 1;

    public string? Color { get; set; }
    public string? Source { get; set; }
    public int BayWidthMm { get; set; }
    public int TotalHeightMm { get; set; }
    public int BaseDepthMm { get; set; }
    public int BaseTopHeightMm { get; set; }
    public int UprightWidthMm { get; set; }
    public decimal HolePitchXMm { get; set; }
    public decimal HolePitchYMm { get; set; }
    public decimal HoleDiameterMm { get; set; }
    public decimal FirstHoleLeftMm { get; set; }
    public decimal FirstHoleTopMm { get; set; }

    /// <summary>横向孔数；请求里缺省或 0 = 自动换算。</summary>
    public int HoleCols { get; set; }

    /// <summary>纵向孔数；请求里缺省或 0 = 自动换算。</summary>
    public int HoleRows { get; set; }

    /// <summary>1 自板顶 / 2 自底板。</summary>
    public int RowNumberingFrom { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    /// <summary>被多少个陈列方案引用（只读）。</summary>
    public int ReferencedPlanCount { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>挂钩型号；请求 / 响应共用，规则同 <see cref="ShelfModelDto"/>。</summary>
public sealed class HookModelDto
{
    public string Guid { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }

    /// <summary>1 单钩 / 2 双钩。</summary>
    public int ProngCount { get; set; } = 2;

    public bool HasPriceTagArm { get; set; } = true;
    public decimal ArmLengthMm { get; set; }
    public decimal DropMm { get; set; }
    public decimal WireDiameterMm { get; set; }
    public decimal TipReserveMm { get; set; }

    /// <summary><c>hasPriceTagArm=false</c> 时三个价签字段可为 0。</summary>
    public decimal TagWidthMm { get; set; }
    public decimal TagHeightMm { get; set; }

    /// <summary>价签架底边高于挂货臂的距离，默认 10。</summary>
    public decimal TagBottomAboveArmMm { get; set; } = 10m;

    public int HoleSpanX { get; set; } = 2;
    public int HoleSpanY { get; set; } = 1;
    public decimal? LoadKg { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>被多少个陈列方案引用（只读）。</summary>
    public int ReferencedPlanCount { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>商品陈列参数；PUT 请求体与列表行里的 <c>spec</c> 共用。</summary>
public sealed class ProductDisplaySpecDto
{
    public string ProductCode { get; set; } = string.Empty;
    public int WidthMm { get; set; }
    public int HeightMm { get; set; }
    public int DepthMm { get; set; }
    public int HangHoleOffsetMm { get; set; } = 10;

    /// <summary>0 无 / 1 圆孔 / 2 欧式挂孔。</summary>
    public int HangHoleType { get; set; } = 1;

    public int? UnitWeightG { get; set; }
    public string? DefaultHookGuid { get; set; }

    /// <summary>按挂钩型号手填的每钩挂数覆盖：{ hookGuid: 挂数 }。</summary>
    public Dictionary<string, int> HookQtyOverrides { get; set; } = new();

    /// <summary>1 人工量取 / 2 供应商资料 / 3 同套装主档。</summary>
    public int Source { get; set; } = 1;

    public string? Notes { get; set; }
}

/// <summary>商品陈列参数列表行（联 Product）。</summary>
public sealed class ProductSpecListItemDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? EnglishName { get; set; }
    public string? ProductImage { get; set; }
    public decimal RetailPrice { get; set; }
    public string? CategoryName { get; set; }

    /// <summary>商品等级（A～D），未分级为 null。只读：按当前页货号小查询回填，不参与分页前的联表。</summary>
    public string? Grade { get; set; }

    /// <summary>
    /// 仓库分类完整路径（逐级祖先拼出，分隔符 " &gt; "，如「百货 &gt; 收纳 &gt; 收纳盒」）；未设仓库分类或分类已不存在时为 null。
    /// 只读：与 <see cref="Grade"/> 一样按当前页商品小查询回填，不参与分页前的联表。
    /// </summary>
    public string? WarehouseCategoryPath { get; set; }

    public bool HasSpec { get; set; }

    /// <summary>被多少个陈列方案引用（按方案明细计数，同一方案多行只算一次）。</summary>
    public int ReferencedPlanCount { get; set; }

    public ProductDisplaySpecDto? Spec { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

// ---------------------------------------------------------------------------------------------------------------
// 附录 D：商品陈列参数的分类 / 等级过滤与批量修改（接口契约见 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 D）
// ---------------------------------------------------------------------------------------------------------------

/// <summary>商品等级过滤里「未分级」的取值（其余取值为 A / B / C / D）。</summary>
public static class PlanogramGradeFilter
{
    public const string None = "none";
    public static readonly string[] Letters = { "A", "B", "C", "D" };
}

/// <summary>
/// 商品陈列参数的过滤条件。<c>GET product-specs</c> 的查询串（数组用重复键）与批量修改 <c>target.filter</c> 共用同一套字段。
/// 各条件之间是「并且」；数组内多选取并集。
/// </summary>
public sealed class ProductSpecFilterDto
{
    /// <summary>货号 / 条码 / 名称 / 英文名。</summary>
    public string? Keyword { get; set; }

    /// <summary>all（默认）/ set / unset。批量修改里照常按原值过滤。</summary>
    public string? Status { get; set; }

    /// <summary>仓库分类 GUID，含全部子分类；未设仓库分类的商品不参与。最多 200 个。</summary>
    public List<string>? WarehouseCategoryGuids { get; set; }

    /// <summary>单个本地供应商；编码 200（Hot Bargain）同时包含供应商为空的商品。</summary>
    public string? LocalSupplierCode { get; set; }

    /// <summary>本地供应商分类 GUID，含子分类，语义同商品列表的 SupplierCategoryGUIDs。最多 200 个。</summary>
    public List<string>? SupplierCategoryGuids { get; set; }

    /// <summary>只看供应商分类「未归类」的商品。</summary>
    public bool? SupplierCategoryUnassignedOnly { get; set; }

    /// <summary>商品等级：A / B / C / D / none（未分级），多选取并集；未知值报校验错误。</summary>
    public List<string>? Grades { get; set; }
}

/// <summary>批量修改的目标：<c>productCodes</c>（勾选的货号）与 <c>filter</c>（按当前筛选）二选一。</summary>
public sealed class ProductSpecBatchTargetDto
{
    public List<string>? ProductCodes { get; set; }
    public ProductSpecFilterDto? Filter { get; set; }
}

/// <summary>
/// 挂数覆盖的批量补丁：<c>mode</c> 取 replace（整体替换为 values；空对象 = 清空全部覆盖）或 merge（只覆盖给出的挂钩）。
/// values 用 JsonElement 接，类型 / 范围不合法时返回 PLANOGRAM_VALIDATION，而不是被 MVC 绑定层直接 400。
/// </summary>
public sealed class ProductSpecBatchOverridesDto
{
    public string? Mode { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement>? Values { get; set; }
}

/// <summary>
/// 批量修改请求。<c>patch</c> 只含被勾选的字段（键存在才修改）；可空字段（unitWeightG / defaultHookGuid / notes）传 null = 清空。
/// </summary>
public sealed class ProductSpecBatchRequest
{
    public ProductSpecBatchTargetDto? Target { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement>? Patch { get; set; }
    public ProductSpecBatchOverridesDto? HookQtyOverrides { get; set; }

    /// <summary>目标里未设参数的商品是否创建。</summary>
    public bool CreateMissing { get; set; }

    /// <summary>true = 完整走一遍校验与计数但不写库。</summary>
    public bool DryRun { get; set; }
}

/// <summary>批量修改里被跳过的商品的原因代码。</summary>
public static class PlanogramBatchSkipReasons
{
    /// <summary>目标里没有陈列参数，且没有勾选「创建未设参数的商品」。</summary>
    public const string NoSpec = "NO_SPEC";

    /// <summary>要创建参数，但补丁没有同时给出宽、高、厚。</summary>
    public const string MissingDimensions = "MISSING_DIMENSIONS";

    /// <summary>改后吊孔偏移大于包装高（吊孔类型非「无」时）。</summary>
    public const string OffsetExceedsHeight = "OFFSET_EXCEEDS_HEIGHT";

    /// <summary>商品不存在或已停用 / 已删除。</summary>
    public const string ProductNotFound = "PRODUCT_NOT_FOUND";

    /// <summary>改后的挂数覆盖序列化后超出库里 1000 字符的上限（跳过，不截断）。</summary>
    public const string OverridesTooLong = "OVERRIDES_TOO_LONG";
}

public sealed class ProductSpecBatchSkippedItemDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>改前改后明细里的动作。</summary>
public static class PlanogramBatchChangeActions
{
    public const string Create = "create";
    public const string Update = "update";
}

/// <summary>
/// 改前改后明细的一行（不变与跳过的商品不进这张表）。<c>before</c> / <c>after</c> 的键名同批量补丁白名单，
/// <c>hookQtyOverrides</c> 用对象。更新时只含本次实际改变的字段；新建时 <c>before</c> 为 null（始终输出该键），
/// <c>after</c> 给出宽高厚、吊孔偏移 / 类型、来源与其他已设置的字段。
/// </summary>
public sealed class ProductSpecBatchChangedItemDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;

    /// <summary>create / update（<see cref="PlanogramBatchChangeActions"/>）。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>新建时为 null；生产环境全局 WhenWritingNull，这里强制输出 <c>"before": null</c>，与契约一致。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public Dictionary<string, object?>? Before { get; set; }

    public Dictionary<string, object?> After { get; set; } = new();
}

/// <summary>
/// 批量修改结果。<c>dryRun</c> 时 created / updated / unchanged 恒为 0，计划数在 will*；真正执行时 will* 恒为 0。
/// <c>skipped</c> 是真实总数，<c>skippedItems</c> 最多返回 200 条；<c>changedTotal</c> 是真实总数，<c>changedItems</c> 最多 50 条。
/// </summary>
public sealed class ProductSpecBatchResultDto
{
    public bool DryRun { get; set; }
    public int Matched { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int WillCreate { get; set; }
    public int WillUpdate { get; set; }
    public int WillStayUnchanged { get; set; }
    public int Skipped { get; set; }
    public List<ProductSpecBatchSkippedItemDto> SkippedItems { get; set; } = new();

    /// <summary>将创建 + 将更新（或已创建 + 已更新）的真实总数。</summary>
    public int ChangedTotal { get; set; }

    /// <summary>改前改后明细：按货号排序取前 50 条，dryRun 与真正执行都返回。</summary>
    public List<ProductSpecBatchChangedItemDto> ChangedItems { get; set; } = new();
}

/// <summary>PLANOGRAM_BATCH_TOO_MANY 的 details：实际匹配数与上限。</summary>
public sealed record PlanogramBatchLimitDetails(int Matched, int Max);

// ---------------------------------------------------------------------------------------------------------------
// 批 2：陈列方案（接口契约见 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 B.2）
// ---------------------------------------------------------------------------------------------------------------

/// <summary>方案状态取值（Planogram.Status）。</summary>
public static class PlanogramStatus
{
    public const int Draft = 0;
    public const int Published = 1;
    public const int Archived = 2;
}

/// <summary>校验冲突的严重程度。</summary>
public static class PlanogramSeverity
{
    public const string Error = "error";
    public const string Warn = "warn";
}

/// <summary>冲突代码（附录 B.1，前后端一致）。</summary>
public static class PlanogramConflictCodes
{
    public const string BayOutOfRange = "BAY_OUT_OF_RANGE";
    public const string ProductSpecMissing = "PRODUCT_SPEC_MISSING";
    public const string HookModelMissing = "HOOK_MODEL_MISSING";
    public const string HoleOutOfRange = "HOLE_OUT_OF_RANGE";
    public const string HookHoleClash = "HOOK_HOLE_CLASH";
    public const string OutOfBoard = "OUT_OF_BOARD";
    public const string EdgeGap = "EDGE_GAP";
    public const string Overlap = "OVERLAP";
    public const string Overload = "OVERLOAD";
}

/// <summary>一条冲突。成对冲突（HOOK_HOLE_CLASH / OVERLAP）每个条目各报一条，<c>otherItemGuid</c> 指向对方。</summary>
public sealed class ConflictDto
{
    public string ItemGuid { get; set; } = string.Empty;
    public string? OtherItemGuid { get; set; }
    public string Code { get; set; } = string.Empty;

    /// <summary>error / warn。</summary>
    public string Severity { get; set; } = PlanogramSeverity.Error;

    public string Message { get; set; } = string.Empty;
}

/// <summary>方案统计（口径见附录 B.1 第 6 条）。</summary>
public sealed class PlanogramStatsDto
{
    /// <summary>明细条数（一钩一行）。</summary>
    public int ItemCount { get; set; }

    /// <summary>不同 ProductCode 个数。</summary>
    public int SkuCount { get; set; }

    /// <summary>Σ 每钩挂数。</summary>
    public int UnitCount { get; set; }

    /// <summary>Σ 每钩挂数 × Product.RetailPrice（两位小数）。</summary>
    public decimal RetailValue { get; set; }

    /// <summary>商品占板率（%，四舍五入取整，不计价签）。</summary>
    public int FillPercent { get; set; }

    /// <summary>有 error 级冲突的条目数（成对冲突按条目去重）。</summary>
    public int ConflictCount { get; set; }
}

/// <summary>方案列表行；同时是详情里的 <c>header</c>。</summary>
public sealed class PlanogramListItemDto
{
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string ShelfModelGuid { get; set; } = string.Empty;
    public string ShelfModelName { get; set; } = string.Empty;
    public int BayCount { get; set; }
    public int GapXMm { get; set; }
    public int GapYMm { get; set; }

    /// <summary>0 草稿 / 1 已发布 / 2 已归档。</summary>
    public int Status { get; set; }

    public int Version { get; set; }
    public int? PublishedVersion { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int ItemCount { get; set; }
    public int SkuCount { get; set; }
    public int UnitCount { get; set; }
    public int FillPercent { get; set; }
    public int ConflictCount { get; set; }

    /// <summary>
    /// 有未发布的改动：<c>status == 0 &amp;&amp; publishedVersion != null</c>。
    /// 已发布方案被保存后会回到草稿（版本号 +1），列表据此提示「有未发布改动」。
    /// </summary>
    public bool HasUnpublishedChanges { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// 方案明细（一钩一行）。同一个 DTO 既用于响应也用于 PUT 请求体：
/// 请求里 <c>specChanged</c> 被忽略；<c>guid</c> 为空时由服务端生成；<c>face</c> 恒 0、<c>itemType</c> 恒 1（二期才放开）。
/// </summary>
public sealed class PlanogramItemDto
{
    public string Guid { get; set; } = string.Empty;

    /// <summary>节号，自左起 1。超出 1..bayCount 不是请求错误，而是冲突 BAY_OUT_OF_RANGE。</summary>
    public int BayIndex { get; set; } = 1;

    public int Face { get; set; }
    public int ItemType { get; set; } = 1;
    public string ProductCode { get; set; } = string.Empty;
    public string HookModelGuid { get; set; } = string.Empty;
    public int HoleCol { get; set; }
    public int HoleRow { get; set; }

    /// <summary>每钩挂数；null = 按规则（规格覆盖 &gt; 建议值）。</summary>
    public int? PerHookQty { get; set; }

    public int SortOrder { get; set; }

    /// <summary>只读：保存时的商品尺寸快照与当前参数不一致（「参数已变」提示）。</summary>
    public bool SpecChanged { get; set; }
}

/// <summary>方案详情：设计器一次拿齐。<c>header</c> 是嵌套对象，<c>rowVersion</c> 在顶层。</summary>
public sealed class PlanogramDetailDto
{
    public PlanogramListItemDto Header { get; set; } = new();

    /// <summary>乐观并发令牌：<c>updatedAt</c> 的 ISO 8601 往返格式（7 位小数刻度），保存时原样带回。</summary>
    public string RowVersion { get; set; } = string.Empty;

    public List<PlanogramItemDto> Items { get; set; } = new();
    public ShelfModelDto ShelfModel { get; set; } = new();

    /// <summary>全部启用的挂钩型号 + 本方案引用到的停用型号。</summary>
    public List<HookModelDto> HookModels { get; set; } = new();

    /// <summary>仅方案内商品的参数行（含零售价、是否已设参数）。</summary>
    public List<ProductSpecListItemDto> ProductSpecs { get; set; } = new();

    public List<ConflictDto> Conflicts { get; set; } = new();
    public PlanogramStatsDto Stats { get; set; } = new();
}

/// <summary>新建方案请求。<c>gapXMm / gapYMm</c> 缺省取 10。</summary>
public sealed class PlanogramCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string ShelfModelGuid { get; set; } = string.Empty;

    /// <summary>1–12。</summary>
    public int BayCount { get; set; }

    public int? GapXMm { get; set; }
    public int? GapYMm { get; set; }
}

/// <summary>
/// 整体保存请求（PUT）；校验请求（POST validate）复用同一个类，<c>rowVersion</c> / <c>name</c> / <c>notes</c> 在校验时被忽略。
/// <c>gapXMm / gapYMm / items</c> 必填（缺省不当 0 处理，直接报校验错误，避免误清空）。
/// </summary>
public sealed class PlanogramSaveRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string ShelfModelGuid { get; set; } = string.Empty;
    public int BayCount { get; set; }
    public int? GapXMm { get; set; }
    public int? GapYMm { get; set; }
    public List<PlanogramItemDto>? Items { get; set; }
    public string? RowVersion { get; set; }
}

/// <summary>复制方案请求：新方案名称。</summary>
public sealed class PlanogramDuplicateRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>校验接口（不落库）的返回。</summary>
public sealed class PlanogramValidateResultDto
{
    public List<ConflictDto> Conflicts { get; set; } = new();
    public PlanogramStatsDto Stats { get; set; } = new();
}

// ---------------------------------------------------------------------------------------------------------------
// 批 3：发布、版本快照、JSON 导入导出（接口契约见同一文档附录 C）
// ---------------------------------------------------------------------------------------------------------------

/// <summary>发布请求：<c>rowVersion</c> 取自读取方案时返回的值，与保存同一套乐观并发。</summary>
public sealed class PlanogramPublishRequest
{
    public string? RowVersion { get; set; }
}

/// <summary>版本列表的一行（按版本倒序）。统计取自发布时固化的快照。</summary>
public sealed class PlanogramVersionSummaryDto
{
    public int Version { get; set; }
    public DateTime PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public int ItemCount { get; set; }
    public int SkuCount { get; set; }
    public int UnitCount { get; set; }
}

/// <summary>快照里的方案头。</summary>
public sealed class PlanogramSnapshotPlanDto
{
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public int BayCount { get; set; }
    public int GapXMm { get; set; }
    public int GapYMm { get; set; }
}

/// <summary>快照里的商品：发布时刻的名称 / 英文名 / 图片 / 零售价与陈列参数，之后主档再改也不影响历史版本。</summary>
public sealed class PlanogramSnapshotProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? EnglishName { get; set; }
    public string? ProductImage { get; set; }
    public decimal RetailPrice { get; set; }
    public ProductDisplaySpecDto Spec { get; set; } = new();
}

/// <summary>
/// 发布快照。发布时整体序列化成 <c>PlanogramVersion.LayoutJson</c>，读取历史版本时原样反序列化，
/// 以后货架 / 挂钩型号、商品参数、商品名、价格再变，历史版本读出来仍是当时的值（前端据此按当时数据重出 PDF）。
/// <c>version / publishedAt / publishedBy</c> 读取时以版本表那一行为准。
/// </summary>
public sealed class PlanogramSnapshotDto
{
    public int Version { get; set; }
    public DateTime PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public PlanogramSnapshotPlanDto Plan { get; set; } = new();
    public ShelfModelDto ShelfModel { get; set; } = new();

    /// <summary>仅方案用到的挂钩型号。</summary>
    public List<HookModelDto> HookModels { get; set; } = new();

    public List<PlanogramSnapshotProductDto> Products { get; set; } = new();

    /// <summary>发布时的明细；<c>specChanged</c> 在历史版本里没有意义，恒为 false。</summary>
    public List<PlanogramItemDto> Items { get; set; } = new();

    public PlanogramStatsDto Stats { get; set; } = new();
}

/// <summary>导出文件格式版本。</summary>
public static class PlanogramExportFormat
{
    public const int CurrentVersion = 1;
}

/// <summary>导出文件里的方案头。导入时缺省的间距按 10 处理（与新建一致）。</summary>
public sealed class PlanogramExportPlanDto
{
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public int BayCount { get; set; }
    public int? GapXMm { get; set; }
    public int? GapYMm { get; set; }
}

/// <summary>导出文件里的明细：用型号编码而不是 guid，便于跨环境搬运。</summary>
public sealed class PlanogramExportItemDto
{
    public int BayIndex { get; set; } = 1;
    public string ProductCode { get; set; } = string.Empty;
    public string HookModelCode { get; set; } = string.Empty;
    public int HoleCol { get; set; }
    public int HoleRow { get; set; }
    public int? PerHookQty { get; set; }
}

/// <summary>方案导出文件（JSON 对象，前端存为文件）。用货架 / 挂钩型号编码和商品货号，不带任何 guid。</summary>
public class PlanogramExportDto
{
    /// <summary>
    /// 文件格式版本，当前 1。不设默认值：导入时文件里缺这个字段就按 0 处理（不认识的版本），而不是悄悄当成 1；
    /// 导出时由服务端显式写入 <see cref="PlanogramExportFormat.CurrentVersion"/>。
    /// </summary>
    public int FormatVersion { get; set; }
    public DateTime ExportedAt { get; set; }

    /// <summary>导入时缺省 = 校验错误；导出恒有值。</summary>
    public PlanogramExportPlanDto? Plan { get; set; }

    public string ShelfModelCode { get; set; } = string.Empty;

    /// <summary>导入时缺省 = 校验错误（没有明细请传空数组）；导出恒有值。</summary>
    public List<PlanogramExportItemDto>? Items { get; set; }
}

/// <summary>导入请求体 = 导出文件内容，另可带 <c>name</c> 覆盖方案名称。</summary>
public sealed class PlanogramImportRequest : PlanogramExportDto
{
    public string? Name { get; set; }
}

/// <summary>导入时解析不到的编码（<c>kind</c>：shelf / hook / product）。</summary>
public sealed record PlanogramImportUnresolvedDto(string Kind, string Code);

public static class PlanogramImportKinds
{
    public const string Shelf = "shelf";
    public const string Hook = "hook";
    public const string Product = "product";
}
