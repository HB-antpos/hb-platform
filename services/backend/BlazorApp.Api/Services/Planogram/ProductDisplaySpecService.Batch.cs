using System.Text.Json;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Planogram;

/// <summary>
/// 商品陈列参数批量修改（附录 D.2）：<c>POST product-specs/batch</c>。
/// 与单个保存共用同一套字段规则（范围、吊孔偏移不能大于包装高、挂数覆盖的长度上限），
/// 区别是批量里「逐商品不合法」只跳过该商品，而「补丁本身不合法」整批不执行。
/// </summary>
public sealed partial class ProductDisplaySpecService
{
    /// <summary>一次批量修改最多处理的商品数。</summary>
    internal const int MaxBatchTargets = 2000;

    /// <summary>响应里最多返回的跳过明细条数（<c>skipped</c> 仍是真实总数）。</summary>
    internal const int MaxSkippedItems = 200;

    /// <summary>响应里最多返回的改前改后明细条数（按货号排序取前 N 条；<c>changedTotal</c> 是真实总数）。</summary>
    internal const int MaxChangedItems = 50;

    // SQL Server 单语句参数上限 2100：读取 IN 子句与写入都按 500 一批；写入再按每页 100 行拆语句
    // （更新 12 列 × 100 行 < 2100，插入 16 列 × 100 行 < 2100）。
    private const int BatchChunkSize = 500;
    private const int BatchWritePageSize = 100;

    // 批量「创建」时补丁没给的字段取值（附录 D.2：吊孔偏移缺省 10、吊孔类型缺省 2、来源缺省 1）。
    private const int BatchDefaultHangHoleOffsetMm = 10;
    private const int BatchDefaultHangHoleType = 2;
    private const int BatchDefaultSource = 1;

    // ---------------------------------------------------------------- 入口

    public async Task<ApiResponse<ProductSpecBatchResultDto>> BatchUpdateAsync(
        ProductSpecBatchRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request ??= new ProductSpecBatchRequest();

        // 1) 补丁本身不合法（未知键、类型、范围、挂数覆盖格式、目标二选一）→ 整批不执行。
        var errors = new PlanogramFieldErrors();
        var patch = ParseBatchPatch(request.Patch, errors);
        var overrides = ParseBatchOverrides(request.HookQtyOverrides, errors);
        if (request.Target?.ProductCodes is { Count: > 0 } && request.Target.Filter is not null)
        {
            errors.Add("target", "批量目标只能二选一：productCodes（勾选的货号）或 filter（按当前筛选），不能同时给");
        }

        if (errors.Any)
        {
            return errors.ToFailure<ProductSpecBatchResultDto>();
        }

        // 挂钩型号必须存在（默认挂钩 + 覆盖里的每个 hookGuid）；停用的型号也算存在，与单个保存一致。
        await ValidateBatchHooksAsync(patch, overrides, errors);
        if (errors.Any)
        {
            return errors.ToFailure<ProductSpecBatchResultDto>();
        }

        // 2) 解析目标商品（≤ 2000，空 / 超出各有专用错误码）。
        var (targets, failure) = await ResolveBatchTargetsAsync(request.Target);
        if (failure is not null)
        {
            return failure;
        }

        // 3) 逐商品计算：创建 / 更新 / 不变 / 跳过。dryRun 与真正执行走同一份计算，所以计数必然一致。
        var plan = await PlanBatchAsync(targets!, patch, overrides, request.CreateMissing);
        var result = new ProductSpecBatchResultDto
        {
            DryRun = request.DryRun,
            Matched = targets!.Count,
            Skipped = plan.Skipped.Count,
            SkippedItems = plan.Skipped.Take(MaxSkippedItems).ToList(),
            // 改前改后明细：dryRun 与真正执行都返回，内容由同一份计算得出，所以预览看到的就是执行会写的。
            ChangedTotal = plan.Changes.Count,
            ChangedItems = BuildChangedItems(plan.Changes),
        };

        if (request.DryRun)
        {
            result.WillCreate = plan.Creates.Count;
            result.WillUpdate = plan.Updates.Count;
            result.WillStayUnchanged = plan.UnchangedCount;
            return ApiResponse<ProductSpecBatchResultDto>.OK(result, "预览完成，未修改任何数据");
        }

        // 4) 真正执行：所有通过校验的商品在一个事务里写入，任何一步失败整批回滚。
        if (!await WriteBatchAsync(plan, actor))
        {
            // 计算之后、写入之前别人同时创建了同一货号的参数，或清除了要更新的参数：整批已回滚，提示重新预览而不是 500。
            return ApiResponse<ProductSpecBatchResultDto>.Error(
                "有其他人同时修改了这些商品，请重新预览后再执行", PlanogramErrorCodes.BatchConflict);
        }

        result.Created = plan.Creates.Count;
        result.Updated = plan.Updates.Count;
        result.Unchanged = plan.UnchangedCount;
        return ApiResponse<ProductSpecBatchResultDto>.OK(result, "批量修改完成");
    }

    // ---------------------------------------------------------------- 补丁解析与校验

    /// <summary>补丁里被勾选的字段；<c>null</c> / false 表示「不动」。</summary>
    private sealed class BatchPatch
    {
        public int? WidthMm { get; set; }
        public int? HeightMm { get; set; }
        public int? DepthMm { get; set; }
        public int? HangHoleOffsetMm { get; set; }
        public int? HangHoleType { get; set; }
        public int? Source { get; set; }
        public bool SetUnitWeight { get; set; }
        public int? UnitWeightG { get; set; }
        public bool SetDefaultHook { get; set; }
        public string? DefaultHookGuid { get; set; }
        public bool SetNotes { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class BatchOverridesPatch
    {
        /// <summary>true = replace（整体替换），false = merge（只覆盖给出的挂钩）。</summary>
        public bool Replace { get; set; }

        public Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 只接受白名单键；未知键、类型不对、范围不对都收进 errors（字段名用请求 JSON 里的键，与单个保存一致，不带 patch. 前缀）。
    /// 非空字段（宽高厚、吊孔偏移、吊孔类型、来源）传 null 视为校验错误；可空字段传 null = 清空。
    /// </summary>
    private static BatchPatch ParseBatchPatch(Dictionary<string, JsonElement>? raw, PlanogramFieldErrors errors)
    {
        var patch = new BatchPatch();
        if (raw is null)
        {
            return patch;
        }

        foreach (var (key, value) in raw)
        {
            switch (key)
            {
                case "widthMm":
                    patch.WidthMm = ReadRequiredInt(key, "包装宽", value, errors);
                    if (patch.WidthMm is { } width)
                    {
                        errors.IntRange(key, "包装宽", width, 1, MaxMm);
                    }

                    break;
                case "heightMm":
                    patch.HeightMm = ReadRequiredInt(key, "包装高", value, errors);
                    if (patch.HeightMm is { } height)
                    {
                        errors.IntRange(key, "包装高", height, 1, MaxMm);
                    }

                    break;
                case "depthMm":
                    patch.DepthMm = ReadRequiredInt(key, "包装厚", value, errors);
                    if (patch.DepthMm is { } depth)
                    {
                        errors.IntRange(key, "包装厚", depth, 1, MaxMm);
                    }

                    break;
                case "hangHoleOffsetMm":
                    patch.HangHoleOffsetMm = ReadRequiredInt(key, "吊孔偏移", value, errors);
                    if (patch.HangHoleOffsetMm is < 0)
                    {
                        errors.Add(key, "吊孔偏移不能为负数");
                    }

                    break;
                case "hangHoleType":
                    patch.HangHoleType = ReadRequiredInt(key, "吊孔类型", value, errors);
                    if (patch.HangHoleType is { } holeType && holeType is not (0 or 1 or 2))
                    {
                        errors.Add(key, "吊孔类型必须是 0（无）、1（圆孔）或 2（欧式挂孔）");
                    }

                    break;
                case "source":
                    patch.Source = ReadRequiredInt(key, "数据来源", value, errors);
                    if (patch.Source is { } source && source is not (1 or 2 or 3))
                    {
                        errors.Add(key, "数据来源必须是 1（人工量取）、2（供应商资料）或 3（同套装主档）");
                    }

                    break;
                case "unitWeightG":
                    patch.SetUnitWeight = true;
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        patch.UnitWeightG = null; // null = 清空（未知）
                    }
                    else if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var weight))
                    {
                        errors.Add(key, "单件重必须是整数（或 null 表示清空）");
                    }
                    else
                    {
                        patch.UnitWeightG = weight;
                        if (weight < MinUnitWeightG || weight > MaxUnitWeightG)
                        {
                            errors.Add(key, $"单件重必须在 {MinUnitWeightG}～{MaxUnitWeightG} 克之间（不填表示未知）");
                        }
                    }

                    break;
                case "defaultHookGuid":
                    patch.SetDefaultHook = true;
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        patch.DefaultHookGuid = null;
                    }
                    else if (value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(key, "默认挂钩型号必须是字符串（或 null 表示清空）");
                    }
                    else
                    {
                        // 空白字符串与单个保存一致：按清空处理。
                        var guid = value.GetString()?.Trim();
                        patch.DefaultHookGuid = string.IsNullOrEmpty(guid) ? null : guid;
                    }

                    break;
                case "notes":
                    patch.SetNotes = true;
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        patch.Notes = null;
                    }
                    else if (value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(key, "备注必须是字符串（或 null 表示清空）");
                    }
                    else
                    {
                        var notes = value.GetString()?.Trim();
                        patch.Notes = string.IsNullOrEmpty(notes) ? null : notes;
                        if (notes is { Length: > MaxNotesLength })
                        {
                            errors.Add(key, $"备注不能超过 {MaxNotesLength} 个字符");
                        }
                    }

                    break;
                default:
                    errors.Add(key, $"不支持批量修改的字段「{key}」");
                    break;
            }
        }

        return patch;
    }

    /// <summary>读取非空整数字段：null 与非整数（字符串、小数、布尔……）都是校验错误，返回 null。</summary>
    private static int? ReadRequiredInt(string field, string label, JsonElement value, PlanogramFieldErrors errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(field, $"{label}不能清空（不能为 null）");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            errors.Add(field, $"{label}必须是整数");
            return null;
        }

        return number;
    }

    /// <summary>
    /// 挂数覆盖补丁：mode 必须是 replace / merge，values 必须给出（replace 传空对象才表示清空，缺省不当清空处理，避免误清）；
    /// 每个挂数必须是 1～9999 的整数。挂钩型号是否存在由 <see cref="ValidateBatchHooksAsync"/> 查库校验。
    /// </summary>
    private static BatchOverridesPatch? ParseBatchOverrides(ProductSpecBatchOverridesDto? raw, PlanogramFieldErrors errors)
    {
        if (raw is null)
        {
            return null; // 缺省 = 不动
        }

        const string field = "hookQtyOverrides";
        var patch = new BatchOverridesPatch();
        var mode = raw.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("replace" or "merge"))
        {
            errors.Add(field, "挂数覆盖的 mode 只能是 replace（整体替换）或 merge（合并）");
        }

        patch.Replace = mode == "replace";
        if (raw.Values is null)
        {
            errors.Add(field, "挂数覆盖必须带 values（replace 模式传空对象表示清空全部覆盖）");
            return patch;
        }

        var qtyRangeReported = false;
        foreach (var (hookGuid, value) in raw.Values)
        {
            if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var qty)
                || qty < 1
                || qty > MaxHookQty)
            {
                if (!qtyRangeReported)
                {
                    errors.Add(field, $"每钩挂数必须是 1～{MaxHookQty} 之间的整数");
                    qtyRangeReported = true;
                }

                continue;
            }

            patch.Values[hookGuid] = qty;
        }

        return patch;
    }

    /// <summary>默认挂钩与覆盖里的每个 hookGuid 必须是已存在的挂钩型号（分批查库，避免 IN 参数过多）。</summary>
    private async Task ValidateBatchHooksAsync(BatchPatch patch, BatchOverridesPatch? overrides, PlanogramFieldErrors errors)
    {
        var wanted = new List<string>();
        if (patch.SetDefaultHook && patch.DefaultHookGuid is not null)
        {
            wanted.Add(patch.DefaultHookGuid);
        }

        if (overrides is not null)
        {
            wanted.AddRange(overrides.Values.Keys);
        }

        var distinct = wanted.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in distinct.Chunk(BatchChunkSize))
        {
            var list = chunk.ToList();
            known.UnionWith(await _db.Queryable<PlanogramHookModel>()
                .Where(hook => list.Contains(hook.Guid))
                .Select(hook => hook.Guid)
                .ToListAsync());
        }

        if (patch.SetDefaultHook && patch.DefaultHookGuid is not null && !known.Contains(patch.DefaultHookGuid))
        {
            errors.Add("defaultHookGuid", "默认挂钩型号不存在");
        }

        if (overrides is not null)
        {
            foreach (var hookGuid in overrides.Values.Keys.Where(guid => !known.Contains(guid)))
            {
                errors.Add("hookQtyOverrides", $"挂钩型号「{hookGuid}」不存在");
            }
        }
    }

    // ---------------------------------------------------------------- 目标解析

    private sealed class TargetRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? ProductName { get; set; }
    }

    /// <summary>批量里的一个目标商品；Found = false 表示商品不存在或已停用 / 已删除。</summary>
    private sealed record BatchTarget(string ProductCode, string ItemNumber, string ProductName, bool Found);

    /// <summary>
    /// 目标二选一：productCodes（去空白、去重，≤ 2000）或 filter（服务端解析出全部匹配商品，匹配数 &gt; 2000 报错）。
    /// 空目标 / 无匹配 → PLANOGRAM_BATCH_EMPTY；超出 → PLANOGRAM_BATCH_TOO_MANY（details 带匹配数与上限）。
    /// </summary>
    private async Task<(List<BatchTarget>? Targets, ApiResponse<ProductSpecBatchResultDto>? Failure)> ResolveBatchTargetsAsync(
        ProductSpecBatchTargetDto? target)
    {
        ApiResponse<ProductSpecBatchResultDto> Empty() =>
            ApiResponse<ProductSpecBatchResultDto>.Error("批量修改的目标为空，没有匹配到任何商品", PlanogramErrorCodes.BatchEmpty);

        ApiResponse<ProductSpecBatchResultDto> TooMany(int matched) =>
            ApiResponse<ProductSpecBatchResultDto>.Error(
                $"批量修改一次最多 {MaxBatchTargets} 个商品，当前匹配 {matched} 个，请缩小范围",
                PlanogramErrorCodes.BatchTooMany,
                new PlanogramBatchLimitDetails(matched, MaxBatchTargets));

        var codes = (target?.ProductCodes ?? new List<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count > 0)
        {
            if (codes.Count > MaxBatchTargets)
            {
                return (null, TooMany(codes.Count));
            }

            return (await ResolveByCodesAsync(codes), null);
        }

        if (target?.Filter is null)
        {
            return (null, Empty());
        }

        var errors = new PlanogramFieldErrors();
        var query = await BuildFilteredQueryAsync(target.Filter, errors);
        if (query is null)
        {
            // 筛选条件里的错误挂在 target.filter. 下，前端能区分是目标筛选的问题。
            return (null, errors.Remap(field => "target.filter." + field).ToFailure<ProductSpecBatchResultDto>());
        }

        var matched = await query.CountAsync();
        if (matched == 0)
        {
            return (null, Empty());
        }

        if (matched > MaxBatchTargets)
        {
            return (null, TooMany(matched));
        }

        var rows = await query
            .OrderBy(product => product.ProductCode)
            .Select(product => new TargetRow
            {
                ProductCode = product.ProductCode!,
                ItemNumber = product.ItemNumber,
                ProductName = product.ProductName,
            })
            .ToListAsync();
        if (rows.Count > MaxBatchTargets)
        {
            // 计数与取数之间又有商品进来：以实际取到的数量为准报超限，不静默截断。
            return (null, TooMany(rows.Count));
        }

        return (rows.Select(row => new BatchTarget(row.ProductCode, row.ItemNumber ?? string.Empty, row.ProductName ?? string.Empty, true)).ToList(), null);
    }

    /// <summary>
    /// 按货号解析目标：启用且未软删的商品为 Found；其余货号保留为 Found = false（后续计入 PRODUCT_NOT_FOUND），
    /// 并尽量补出货号 / 名称（主档里还在但已停用 / 已删除时），方便前端在跳过明细里展示。
    /// </summary>
    private async Task<List<BatchTarget>> ResolveByCodesAsync(List<string> codes)
    {
        var found = new Dictionary<string, TargetRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in codes.Chunk(BatchChunkSize))
        {
            var list = chunk.ToList();
            var rows = await ActiveProducts()
                .Where(product => list.Contains(product.ProductCode!))
                .Select(product => new TargetRow
                {
                    ProductCode = product.ProductCode!,
                    ItemNumber = product.ItemNumber,
                    ProductName = product.ProductName,
                })
                .ToListAsync();
            foreach (var row in rows)
            {
                found.TryAdd(row.ProductCode, row);
            }
        }

        var missing = codes.Where(code => !found.ContainsKey(code)).ToList();
        var missingInfo = new Dictionary<string, TargetRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in missing.Chunk(BatchChunkSize))
        {
            var list = chunk.ToList();
            var rows = await _db.Queryable<Product>()
                .Where(product => list.Contains(product.ProductCode!))
                .Select(product => new TargetRow
                {
                    ProductCode = product.ProductCode!,
                    ItemNumber = product.ItemNumber,
                    ProductName = product.ProductName,
                })
                .ToListAsync();
            foreach (var row in rows)
            {
                missingInfo.TryAdd(row.ProductCode, row);
            }
        }

        return codes.Select(code =>
        {
            if (found.TryGetValue(code, out var row))
            {
                return new BatchTarget(row.ProductCode, row.ItemNumber ?? string.Empty, row.ProductName ?? string.Empty, true);
            }

            missingInfo.TryGetValue(code, out var info);
            return new BatchTarget(code, info?.ItemNumber ?? string.Empty, info?.ProductName ?? string.Empty, false);
        }).ToList();
    }

    // ---------------------------------------------------------------- 逐商品计算

    /// <summary>一条将要写入的改动：<c>Before</c> 为 null 表示新建。</summary>
    private sealed record BatchChange(BatchTarget Target, ProductDisplaySpec? Before, ProductDisplaySpec After);

    private sealed class BatchPlan
    {
        public List<BatchChange> Changes { get; } = new();
        public List<ProductDisplaySpec> Creates { get; } = new();
        public List<ProductDisplaySpec> Updates { get; } = new();
        public int UnchangedCount { get; set; }
        public List<ProductSpecBatchSkippedItemDto> Skipped { get; } = new();
    }

    /// <summary>
    /// 逐商品套用补丁，得到创建 / 更新 / 不变 / 跳过四类结果（不写库）。
    /// 跳过顺序：商品不存在 → 未设参数（NO_SPEC / MISSING_DIMENSIONS）→ 吊孔偏移超高 → 挂数覆盖超长。
    /// </summary>
    private async Task<BatchPlan> PlanBatchAsync(
        List<BatchTarget> targets, BatchPatch patch, BatchOverridesPatch? overrides, bool createMissing)
    {
        var plan = new BatchPlan();

        // 已有参数分批读取（500 一批）。
        var existing = new Dictionary<string, ProductDisplaySpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in targets.Where(target => target.Found).Select(target => target.ProductCode).Chunk(BatchChunkSize))
        {
            var list = chunk.ToList();
            foreach (var spec in await _db.Queryable<ProductDisplaySpec>().Where(item => list.Contains(item.ProductCode)).ToListAsync())
            {
                existing.TryAdd(spec.ProductCode, spec);
            }
        }

        foreach (var target in targets)
        {
            void Skip(string reason) => plan.Skipped.Add(new ProductSpecBatchSkippedItemDto
            {
                ProductCode = target.ProductCode,
                ItemNumber = target.ItemNumber,
                ProductName = target.ProductName,
                Reason = reason,
            });

            if (!target.Found)
            {
                Skip(PlanogramBatchSkipReasons.ProductNotFound);
                continue;
            }

            existing.TryGetValue(target.ProductCode, out var current);
            if (current is null)
            {
                if (!createMissing)
                {
                    Skip(PlanogramBatchSkipReasons.NoSpec);
                    continue;
                }

                // 创建必须同时给出宽、高、厚（单个保存里这三项必填）。
                if (patch.WidthMm is null || patch.HeightMm is null || patch.DepthMm is null)
                {
                    Skip(PlanogramBatchSkipReasons.MissingDimensions);
                    continue;
                }
            }

            var candidate = current is null ? NewBatchSpec(target.ProductCode) : CloneSpec(current);
            ApplyPatch(candidate, patch);
            if (OffsetExceedsHeight(candidate.HangHoleType, candidate.HeightMm, candidate.HangHoleOffsetMm))
            {
                Skip(PlanogramBatchSkipReasons.OffsetExceedsHeight);
                continue;
            }

            if (overrides is not null)
            {
                var json = BuildOverridesJson(current?.HookQtyOverridesJson, overrides);
                if (json is { Length: > MaxOverridesJsonLength })
                {
                    // 与单个保存一致：超出库里 1000 字符的上限就拒绝，而不是截断。
                    Skip(PlanogramBatchSkipReasons.OverridesTooLong);
                    continue;
                }

                candidate.HookQtyOverridesJson = json;
            }

            if (current is null)
            {
                plan.Creates.Add(candidate);
                plan.Changes.Add(new BatchChange(target, null, candidate));
            }
            else if (IsSameSpec(current, candidate))
            {
                plan.UnchangedCount++;
            }
            else
            {
                plan.Updates.Add(candidate);
                plan.Changes.Add(new BatchChange(target, current, candidate));
            }
        }

        return plan;
    }

    /// <summary>创建时补丁没给的字段取附录 D.2 的缺省值；宽高厚由补丁覆盖（调用前已确认三项都给了）。</summary>
    private static ProductDisplaySpec NewBatchSpec(string productCode) => new()
    {
        ProductCode = productCode,
        HangHoleOffsetMm = BatchDefaultHangHoleOffsetMm,
        HangHoleType = BatchDefaultHangHoleType,
        Source = BatchDefaultSource,
    };

    private static ProductDisplaySpec CloneSpec(ProductDisplaySpec spec) => new()
    {
        ProductCode = spec.ProductCode,
        WidthMm = spec.WidthMm,
        HeightMm = spec.HeightMm,
        DepthMm = spec.DepthMm,
        HangHoleOffsetMm = spec.HangHoleOffsetMm,
        HangHoleType = spec.HangHoleType,
        UnitWeightG = spec.UnitWeightG,
        DefaultHookGuid = spec.DefaultHookGuid,
        HookQtyOverridesJson = spec.HookQtyOverridesJson,
        Source = spec.Source,
        Notes = spec.Notes,
        CreatedAt = spec.CreatedAt,
        CreatedBy = spec.CreatedBy,
        UpdatedAt = spec.UpdatedAt,
        UpdatedBy = spec.UpdatedBy,
        IsDeleted = spec.IsDeleted,
    };

    /// <summary>补丁里勾选的字段覆盖到候选行；没勾选的保持原值（新建时是缺省值）。</summary>
    private static void ApplyPatch(ProductDisplaySpec spec, BatchPatch patch)
    {
        if (patch.WidthMm is { } width) spec.WidthMm = width;
        if (patch.HeightMm is { } height) spec.HeightMm = height;
        if (patch.DepthMm is { } depth) spec.DepthMm = depth;
        if (patch.HangHoleOffsetMm is { } offset) spec.HangHoleOffsetMm = offset;
        if (patch.HangHoleType is { } holeType) spec.HangHoleType = (byte)holeType;
        if (patch.Source is { } source) spec.Source = (byte)source;
        if (patch.SetUnitWeight) spec.UnitWeightG = patch.UnitWeightG;
        if (patch.SetDefaultHook) spec.DefaultHookGuid = patch.DefaultHookGuid;
        if (patch.SetNotes) spec.Notes = patch.Notes;
    }

    /// <summary>
    /// 按 replace / merge 算出新的挂数覆盖 JSON（键排序后序列化，空覆盖返回 null）。
    /// 长度是否超限由调用方判断：这里不截断。
    /// </summary>
    private static string? BuildOverridesJson(string? existingJson, BatchOverridesPatch patch)
    {
        var merged = patch.Replace
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(ParseOverrides(existingJson), StringComparer.Ordinal);
        foreach (var (hookGuid, qty) in patch.Values)
        {
            merged[hookGuid] = qty;
        }

        if (merged.Count == 0)
        {
            return null;
        }

        var ordered = merged.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value);
        return JsonSerializer.Serialize(ordered, JsonOptions);
    }

    /// <summary>补丁应用后与现有值是否完全相同（挂数覆盖按解析后的内容比，不比 JSON 文本，免得排序 / 空白差异误判为改动）。</summary>
    private static bool IsSameSpec(ProductDisplaySpec existing, ProductDisplaySpec candidate)
    {
        if (existing.WidthMm != candidate.WidthMm
            || existing.HeightMm != candidate.HeightMm
            || existing.DepthMm != candidate.DepthMm
            || existing.HangHoleOffsetMm != candidate.HangHoleOffsetMm
            || existing.HangHoleType != candidate.HangHoleType
            || existing.UnitWeightG != candidate.UnitWeightG
            || !string.Equals(existing.DefaultHookGuid, candidate.DefaultHookGuid, StringComparison.Ordinal)
            || existing.Source != candidate.Source
            || !string.Equals(existing.Notes, candidate.Notes, StringComparison.Ordinal))
        {
            return false;
        }

        var before = ParseOverrides(existing.HookQtyOverridesJson);
        var after = ParseOverrides(candidate.HookQtyOverridesJson);
        return before.Count == after.Count
            && before.All(pair => after.TryGetValue(pair.Key, out var qty) && qty == pair.Value);
    }

    // ---------------------------------------------------------------- 写库（一个事务）

    /// <summary>
    /// 创建与更新在同一个事务里，分批写入；任何一批失败都回滚整批并把异常继续抛出，不留下半截结果。
    /// 写入的行 UpdatedBy / UpdatedAt 与单个保存一致；更新只动参数列（不碰 CreatedAt / CreatedBy），不变的商品不写。
    /// </summary>
    /// <returns>
    /// true = 已提交；false = 并发冲突，整批已回滚：
    /// 创建时撞了主键 / 唯一约束（别人同时创建了同一货号的参数），或某批更新的实际影响行数少于应更新行数（别人同时删了参数）。
    /// 其他数据库错误回滚后原样抛出。
    /// </returns>
    private async Task<bool> WriteBatchAsync(BatchPlan plan, string actor)
    {
        if (plan.Creates.Count == 0 && plan.Updates.Count == 0)
        {
            return true;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var spec in plan.Creates)
        {
            spec.CreatedAt = now;
            spec.CreatedBy = actor;
            spec.UpdatedAt = now;
            spec.UpdatedBy = actor;
        }

        foreach (var spec in plan.Updates)
        {
            spec.UpdatedAt = now;
            spec.UpdatedBy = actor;
        }

        await _db.Ado.BeginTranAsync();
        try
        {
            foreach (var chunk in plan.Updates.Chunk(BatchChunkSize))
            {
                var affected = await _db.Updateable(chunk.ToList())
                    .UpdateColumns(spec => new
                    {
                        spec.WidthMm,
                        spec.HeightMm,
                        spec.DepthMm,
                        spec.HangHoleOffsetMm,
                        spec.HangHoleType,
                        spec.UnitWeightG,
                        spec.DefaultHookGuid,
                        spec.HookQtyOverridesJson,
                        spec.Source,
                        spec.Notes,
                        spec.UpdatedAt,
                        spec.UpdatedBy,
                    })
                    .PageSize(BatchWritePageSize)
                    .ExecuteCommandAsync();
                if (affected < chunk.Length)
                {
                    // 计算时还在的参数行，写入时已不存在（别人同时清除了参数）：不能记成「已更新」，整批回滚报冲突。
                    await TryRollbackAsync();
                    return false;
                }
            }

            foreach (var chunk in plan.Creates.Chunk(BatchChunkSize))
            {
                await _db.Insertable(chunk.ToList()).PageSize(BatchWritePageSize).ExecuteCommandAsync();
            }

            await _db.Ado.CommitTranAsync();
            return true;
        }
        catch (Exception ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
        {
            // 只看异常类型与错误号（SQL Server 2601 / 2627；SQLite 测试库按驱动固定文案），不外传异常文本。
            await TryRollbackAsync();
            return false;
        }
        catch
        {
            await TryRollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 服务端可能已经自行回滚了事务（死锁牺牲者等），此时再回滚会抛第二个异常；
    /// 吞掉回滚本身的异常，保证调用方看到的是写库失败的原始异常（或冲突结果）。
    /// </summary>
    private async Task TryRollbackAsync()
    {
        try
        {
            await _db.Ado.RollbackTranAsync();
        }
        catch
        {
            // 事务已结束，无需处理。
        }
    }

    // ---------------------------------------------------------------- 改前改后明细

    /// <summary>
    /// 按货号排序取前 <see cref="MaxChangedItems"/> 条改动，生成改前改后明细。
    /// 更新：before / after 只含本次实际改变的字段；新建：before 为 null，after 给出宽高厚等全部非空字段与补丁设置的字段。
    /// </summary>
    private static List<ProductSpecBatchChangedItemDto> BuildChangedItems(List<BatchChange> changes) =>
        changes
            .OrderBy(change => change.After.ProductCode, StringComparer.Ordinal)
            .Take(MaxChangedItems)
            .Select(change =>
            {
                var item = new ProductSpecBatchChangedItemDto
                {
                    ProductCode = change.After.ProductCode,
                    ItemNumber = change.Target.ItemNumber,
                    ProductName = change.Target.ProductName,
                    Action = change.Before is null ? PlanogramBatchChangeActions.Create : PlanogramBatchChangeActions.Update,
                };

                var after = SpecFieldValues(change.After);
                if (change.Before is null)
                {
                    // 新建：宽高厚、吊孔偏移 / 类型、来源总有值；可空字段与挂数覆盖只在设置了时给出。
                    foreach (var (key, value) in after)
                    {
                        if (value is null || value is Dictionary<string, int> { Count: 0 })
                        {
                            continue;
                        }

                        item.After[key] = value;
                    }

                    return item;
                }

                var before = SpecFieldValues(change.Before);
                item.Before = new Dictionary<string, object?>();
                for (var i = 0; i < after.Count; i++)
                {
                    if (SameFieldValue(before[i].Value, after[i].Value))
                    {
                        continue;
                    }

                    item.Before[before[i].Key] = before[i].Value;
                    item.After[after[i].Key] = after[i].Value;
                }

                return item;
            })
            .ToList();

    /// <summary>参数行的字段值，键名与批量补丁白名单一致（顺序同附录 D.2 的 patch 示例），挂数覆盖用对象（键排序）。</summary>
    private static List<(string Key, object? Value)> SpecFieldValues(ProductDisplaySpec spec) => new()
    {
        ("widthMm", spec.WidthMm),
        ("heightMm", spec.HeightMm),
        ("depthMm", spec.DepthMm),
        ("hangHoleOffsetMm", spec.HangHoleOffsetMm),
        ("hangHoleType", (int)spec.HangHoleType),
        ("unitWeightG", spec.UnitWeightG),
        ("defaultHookGuid", spec.DefaultHookGuid),
        ("source", (int)spec.Source),
        ("notes", spec.Notes),
        ("hookQtyOverrides", ParseOverrides(spec.HookQtyOverridesJson)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value)),
    };

    private static bool SameFieldValue(object? before, object? after) => (before, after) switch
    {
        (Dictionary<string, int> left, Dictionary<string, int> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var qty) && qty == pair.Value),
        _ => Equals(before, after),
    };
}
