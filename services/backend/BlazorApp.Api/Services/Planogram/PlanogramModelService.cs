using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.Planogram;

public interface IPlanogramModelService
{
    Task<ApiResponse<PagedResult<ShelfModelDto>>> ListShelfModelsAsync(
        string? keyword, bool? isActive, int page, int pageSize, CancellationToken cancellationToken);

    Task<ApiResponse<ShelfModelDto>> CreateShelfModelAsync(
        ShelfModelDto input, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<ShelfModelDto>> UpdateShelfModelAsync(
        string guid, ShelfModelDto input, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<object>> DeleteShelfModelAsync(string guid, CancellationToken cancellationToken);

    Task<ApiResponse<PagedResult<HookModelDto>>> ListHookModelsAsync(
        string? keyword, bool? isActive, int page, int pageSize, CancellationToken cancellationToken);

    Task<ApiResponse<HookModelDto>> CreateHookModelAsync(
        HookModelDto input, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<HookModelDto>> UpdateHookModelAsync(
        string guid, HookModelDto input, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<object>> DeleteHookModelAsync(string guid, CancellationToken cancellationToken);
}

/// <summary>
/// 货架型号与挂钩型号的维护（附录 A）：编码唯一、尺寸校验、孔数自动换算、被方案引用时不能删除（只能停用）。
/// 权限在控制器按方法区分（GET 为 Planogram.View，写入为 Planogram.Manage），这里不判断权限。
/// </summary>
public sealed class PlanogramModelService : IPlanogramModelService
{
    // 尺寸上限：防止明显录错（如多打了几个 0）；decimal(6,2) 的物理上限是 9999.99。
    private const int MaxMm = 100000;
    private const decimal MaxDecimalMm = 9999.99m;
    private const int MaxHoleCount = 10000;
    private const int MaxPageSize = 500;

    private readonly ISqlSugarClient _db;
    private readonly TimeProvider _time;

    public PlanogramModelService(SqlSugarContext context, TimeProvider? timeProvider = null)
        : this(context.Db, timeProvider)
    {
    }

    internal PlanogramModelService(ISqlSugarClient db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 孔数自动换算：⌊(板面尺寸 − 2×首孔边距) ÷ 孔距⌋ + 1。
    /// 板面宽 = 节宽 − 立柱宽，板面高 = 总高 − 底板面离地高。结果 &lt; 1 说明尺寸放不下一个孔。
    /// </summary>
    internal static int ComputeHoleCount(decimal boardSizeMm, decimal firstHoleMarginMm, decimal pitchMm) =>
        (int)Math.Floor((boardSizeMm - 2m * firstHoleMarginMm) / pitchMm) + 1;

    // ---------------------------------------------------------------- 货架型号

    public async Task<ApiResponse<PagedResult<ShelfModelDto>>> ListShelfModelsAsync(
        string? keyword, bool? isActive, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);
        var query = _db.Queryable<PlanogramShelfModel>();
        var key = keyword?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            query = query.Where(model => model.Code.Contains(key) || model.Name.Contains(key));
        }

        if (isActive.HasValue)
        {
            var active = isActive.Value;
            query = query.Where(model => model.IsActive == active);
        }

        RefAsync<int> total = 0;
        var rows = await query.OrderBy(model => model.Code).ToPageListAsync(page, pageSize, total);
        var counts = await CountShelfReferencesAsync(rows.Select(row => row.Guid).ToList());
        return ApiResponse<PagedResult<ShelfModelDto>>.OK(new PagedResult<ShelfModelDto>
        {
            Items = rows.Select(row => ToDto(row, counts.GetValueOrDefault(row.Guid))).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    public async Task<ApiResponse<ShelfModelDto>> CreateShelfModelAsync(
        ShelfModelDto input, string actor, CancellationToken cancellationToken)
    {
        var errors = new PlanogramFieldErrors();
        var normalized = NormalizeShelf(input, errors);
        if (errors.Any)
        {
            return errors.ToFailure<ShelfModelDto>();
        }

        if (await ShelfCodeExistsAsync(normalized.Code, null))
        {
            return DuplicateCode<ShelfModelDto>(normalized.Code);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var entity = new PlanogramShelfModel { CreatedAt = now, CreatedBy = actor };
        Apply(entity, normalized);
        entity.UpdatedAt = now;
        entity.UpdatedBy = actor;
        try
        {
            await _db.Insertable(entity).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            // 并发创建同编码时数据库唯一索引兜底，这里翻译成业务错误而不是 500；其它异常原样抛出。
            if (await ShelfCodeExistsAsync(normalized.Code, entity.Guid))
            {
                return DuplicateCode<ShelfModelDto>(normalized.Code);
            }

            throw;
        }

        return ApiResponse<ShelfModelDto>.OK(ToDto(entity, 0), "已创建");
    }

    public async Task<ApiResponse<ShelfModelDto>> UpdateShelfModelAsync(
        string guid, ShelfModelDto input, string actor, CancellationToken cancellationToken)
    {
        var entity = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == guid);
        if (entity is null)
        {
            return ApiResponse<ShelfModelDto>.Error("货架型号不存在", PlanogramErrorCodes.NotFound);
        }

        var errors = new PlanogramFieldErrors();
        var normalized = NormalizeShelf(input, errors);
        if (errors.Any)
        {
            return errors.ToFailure<ShelfModelDto>();
        }

        // 批 2：型号被已有方案引用后，外形 / 孔阵字段被锁定（改了会悄悄改坏已排好的版面）；
        // 停用、改名称 / 颜色 / 来源等不影响坐标的字段不受限。要改尺寸请复制为新型号。
        var referencedPlans = (await CountShelfReferencesAsync(new List<string> { guid })).GetValueOrDefault(guid);
        if (referencedPlans > 0)
        {
            var locked = FindLockedShelfChanges(entity, normalized);
            if (locked.Count > 0)
            {
                return ApiResponse<ShelfModelDto>.Error(
                    $"该货架型号已被 {referencedPlans} 个陈列方案引用，不能修改{string.Join("、", locked.Select(item => item.Message))}"
                    + "（会悄悄改坏已排版的方案），请复制为新型号；停用或修改名称、颜色不受限",
                    PlanogramErrorCodes.ModelLocked,
                    locked);
            }
        }

        if (await ShelfCodeExistsAsync(normalized.Code, guid))
        {
            return DuplicateCode<ShelfModelDto>(normalized.Code);
        }

        Apply(entity, normalized);
        entity.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        entity.UpdatedBy = actor;
        try
        {
            await _db.Updateable(entity).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            if (await ShelfCodeExistsAsync(normalized.Code, guid))
            {
                return DuplicateCode<ShelfModelDto>(normalized.Code);
            }

            throw;
        }

        return ApiResponse<ShelfModelDto>.OK(ToDto(entity, referencedPlans), "已保存");
    }

    public async Task<ApiResponse<object>> DeleteShelfModelAsync(string guid, CancellationToken cancellationToken)
    {
        var entity = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == guid);
        if (entity is null)
        {
            return ApiResponse<object>.Error("货架型号不存在", PlanogramErrorCodes.NotFound);
        }

        var counts = await CountShelfReferencesAsync(new List<string> { guid });
        var referenced = counts.GetValueOrDefault(guid);
        if (referenced > 0)
        {
            return ApiResponse<object>.Error(
                $"该货架型号已被 {referenced} 个陈列方案引用，不能删除，请改为停用",
                PlanogramErrorCodes.ModelInUse);
        }

        await _db.Deleteable<PlanogramShelfModel>().Where(model => model.Guid == guid).ExecuteCommandAsync();
        return ApiResponse<object>.CreateSuccess("已删除");
    }

    // ---------------------------------------------------------------- 挂钩型号

    public async Task<ApiResponse<PagedResult<HookModelDto>>> ListHookModelsAsync(
        string? keyword, bool? isActive, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);
        var query = _db.Queryable<PlanogramHookModel>();
        var key = keyword?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            query = query.Where(model => model.Code.Contains(key) || model.Name.Contains(key));
        }

        if (isActive.HasValue)
        {
            var active = isActive.Value;
            query = query.Where(model => model.IsActive == active);
        }

        RefAsync<int> total = 0;
        var rows = await query.OrderBy(model => model.Code).ToPageListAsync(page, pageSize, total);
        var counts = await CountHookPlanReferencesAsync(rows.Select(row => row.Guid).ToList());
        return ApiResponse<PagedResult<HookModelDto>>.OK(new PagedResult<HookModelDto>
        {
            Items = rows.Select(row => ToDto(row, counts.GetValueOrDefault(row.Guid))).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    public async Task<ApiResponse<HookModelDto>> CreateHookModelAsync(
        HookModelDto input, string actor, CancellationToken cancellationToken)
    {
        var errors = new PlanogramFieldErrors();
        var normalized = NormalizeHook(input, errors);
        if (errors.Any)
        {
            return errors.ToFailure<HookModelDto>();
        }

        if (await HookCodeExistsAsync(normalized.Code, null))
        {
            return DuplicateCode<HookModelDto>(normalized.Code);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var entity = new PlanogramHookModel { CreatedAt = now, CreatedBy = actor };
        Apply(entity, normalized);
        entity.UpdatedAt = now;
        entity.UpdatedBy = actor;
        try
        {
            await _db.Insertable(entity).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            // 并发创建同编码时数据库唯一索引兜底，这里翻译成业务错误而不是 500；其它异常原样抛出。
            if (await HookCodeExistsAsync(normalized.Code, entity.Guid))
            {
                return DuplicateCode<HookModelDto>(normalized.Code);
            }

            throw;
        }

        return ApiResponse<HookModelDto>.OK(ToDto(entity, 0), "已创建");
    }

    public async Task<ApiResponse<HookModelDto>> UpdateHookModelAsync(
        string guid, HookModelDto input, string actor, CancellationToken cancellationToken)
    {
        var entity = await _db.Queryable<PlanogramHookModel>().FirstAsync(model => model.Guid == guid);
        if (entity is null)
        {
            return ApiResponse<HookModelDto>.Error("挂钩型号不存在", PlanogramErrorCodes.NotFound);
        }

        var errors = new PlanogramFieldErrors();
        var normalized = NormalizeHook(input, errors);
        if (errors.Any)
        {
            return errors.ToFailure<HookModelDto>();
        }

        if (await HookCodeExistsAsync(normalized.Code, guid))
        {
            return DuplicateCode<HookModelDto>(normalized.Code);
        }

        Apply(entity, normalized);
        entity.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        entity.UpdatedBy = actor;
        try
        {
            await _db.Updateable(entity).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            if (await HookCodeExistsAsync(normalized.Code, guid))
            {
                return DuplicateCode<HookModelDto>(normalized.Code);
            }

            throw;
        }

        var counts = await CountHookPlanReferencesAsync(new List<string> { guid });
        return ApiResponse<HookModelDto>.OK(ToDto(entity, counts.GetValueOrDefault(guid)), "已保存");
    }

    public async Task<ApiResponse<object>> DeleteHookModelAsync(string guid, CancellationToken cancellationToken)
    {
        var entity = await _db.Queryable<PlanogramHookModel>().FirstAsync(model => model.Guid == guid);
        if (entity is null)
        {
            return ApiResponse<object>.Error("挂钩型号不存在", PlanogramErrorCodes.NotFound);
        }

        var counts = await CountHookPlanReferencesAsync(new List<string> { guid });
        var referenced = counts.GetValueOrDefault(guid);
        if (referenced > 0)
        {
            return ApiResponse<object>.Error(
                $"该挂钩型号已被 {referenced} 个陈列方案引用，不能删除，请改为停用",
                PlanogramErrorCodes.ModelInUse);
        }

        // 商品参数里的「默认挂钩」和按挂钩型号的挂数覆盖也引用了挂钩 Guid；删掉会留下悬空引用，同样只能停用。
        var specReferenced = await _db.Queryable<ProductDisplaySpec>()
            .Where(spec => spec.DefaultHookGuid == guid
                || (spec.HookQtyOverridesJson != null && spec.HookQtyOverridesJson.Contains(guid)))
            .CountAsync();
        if (specReferenced > 0)
        {
            return ApiResponse<object>.Error(
                $"该挂钩型号已被 {specReferenced} 个商品的陈列参数引用（默认挂钩或挂数覆盖），不能删除，请改为停用",
                PlanogramErrorCodes.ModelInUse);
        }

        await _db.Deleteable<PlanogramHookModel>().Where(model => model.Guid == guid).ExecuteCommandAsync();
        return ApiResponse<object>.CreateSuccess("已删除");
    }

    // ---------------------------------------------------------------- 批 2：被方案引用后的字段锁定 / 方案服务读取辅助

    /// <summary>
    /// 被方案引用后不允许改动的货架字段（附录 B.2）：节宽 / 总高 / 立柱宽 / 底板面离地高 / 两向孔距 / 首孔边距 / 孔数。
    /// 返回真正发生变化的字段（field 为请求 JSON 的 camelCase 名，message 为中文标签）。
    /// 孔数缺省自动换算时 normalized 里已是换算结果，所以「尺寸没变、孔数自动值没变」不算改动。
    /// </summary>
    private static List<PlanogramFieldError> FindLockedShelfChanges(PlanogramShelfModel current, ShelfModelDto next)
    {
        var changed = new List<PlanogramFieldError>();
        void Check(string field, string label, bool different)
        {
            if (different)
            {
                changed.Add(new PlanogramFieldError(field, label));
            }
        }

        Check("bayWidthMm", "节宽", current.BayWidthMm != next.BayWidthMm);
        Check("totalHeightMm", "总高", current.TotalHeightMm != next.TotalHeightMm);
        Check("uprightWidthMm", "立柱宽", current.UprightWidthMm != next.UprightWidthMm);
        Check("baseTopHeightMm", "底板面离地高", current.BaseTopHeightMm != next.BaseTopHeightMm);
        Check("holePitchXMm", "横向孔距", current.HolePitchXMm != next.HolePitchXMm);
        Check("holePitchYMm", "纵向孔距", current.HolePitchYMm != next.HolePitchYMm);
        Check("firstHoleLeftMm", "首孔距板左边", current.FirstHoleLeftMm != next.FirstHoleLeftMm);
        Check("firstHoleTopMm", "首孔距板顶", current.FirstHoleTopMm != next.FirstHoleTopMm);
        Check("holeCols", "横向孔数", current.HoleCols != next.HoleCols);
        Check("holeRows", "纵向孔数", current.HoleRows != next.HoleRows);
        return changed;
    }

    /// <summary>读取单个货架型号（含引用数），不存在返回 null。方案详情用。</summary>
    internal async Task<ShelfModelDto?> GetShelfModelDtoAsync(string guid)
    {
        var entity = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == guid);
        if (entity is null)
        {
            return null;
        }

        var counts = await CountShelfReferencesAsync(new List<string> { guid });
        return ToDto(entity, counts.GetValueOrDefault(guid));
    }

    /// <summary>货架型号名称（按 Guid），方案列表用。</summary>
    internal async Task<Dictionary<string, string>> GetShelfNamesAsync(IReadOnlyCollection<string> guids)
    {
        var distinct = guids.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in distinct.Chunk(500))
        {
            var list = chunk.ToList();
            var rows = await _db.Queryable<PlanogramShelfModel>().Where(model => list.Contains(model.Guid)).ToListAsync();
            foreach (var row in rows)
            {
                result[row.Guid] = row.Name;
            }
        }

        return result;
    }

    /// <summary>方案详情用的挂钩型号：全部启用的 + 方案里引用到的（含停用的）。</summary>
    internal async Task<List<HookModelDto>> GetHookModelDtosForPlanAsync(IReadOnlyCollection<string> referencedGuids)
    {
        var wanted = referencedGuids.Distinct(StringComparer.Ordinal).ToList();
        var rows = await _db.Queryable<PlanogramHookModel>()
            .Where(model => model.IsActive || wanted.Contains(model.Guid))
            .OrderBy(model => model.Code)
            .ToListAsync();
        var counts = await CountHookPlanReferencesAsync(rows.Select(row => row.Guid).ToList());
        return rows.Select(row => ToDto(row, counts.GetValueOrDefault(row.Guid))).ToList();
    }

    // ---------------------------------------------------------------- 校验与归一化

    private static ShelfModelDto NormalizeShelf(ShelfModelDto input, PlanogramFieldErrors errors)
    {
        var result = new ShelfModelDto
        {
            Code = errors.RequiredText("code", "型号编码", input.Code, 50),
            Name = errors.RequiredText("name", "型号名称", input.Name, 100),
            SideType = input.SideType,
            Color = errors.OptionalText("color", "颜色", input.Color, 50),
            Source = errors.OptionalText("source", "来源", input.Source, 100),
            BayWidthMm = input.BayWidthMm,
            TotalHeightMm = input.TotalHeightMm,
            BaseDepthMm = input.BaseDepthMm,
            BaseTopHeightMm = input.BaseTopHeightMm,
            UprightWidthMm = input.UprightWidthMm,
            RowNumberingFrom = input.RowNumberingFrom,
            IsActive = input.IsActive,
        };

        if (input.SideType is not (1 or 2))
        {
            errors.Add("sideType", "面数必须是 1（单面）或 2（双面）");
        }

        if (input.RowNumberingFrom is not (1 or 2))
        {
            errors.Add("rowNumberingFrom", "行号方向必须是 1（自板顶）或 2（自底板）");
        }

        errors.IntRange("bayWidthMm", "节宽", input.BayWidthMm, 1, MaxMm);
        errors.IntRange("totalHeightMm", "总高", input.TotalHeightMm, 1, MaxMm);
        errors.IntRange("baseDepthMm", "底板深", input.BaseDepthMm, 1, MaxMm);
        errors.IntRange("baseTopHeightMm", "底板面离地高", input.BaseTopHeightMm, 1, MaxMm);
        errors.IntRange("uprightWidthMm", "立柱宽", input.UprightWidthMm, 1, MaxMm);

        // 板面宽 = 节宽 − 立柱宽，板面高 = 总高 − 底板面离地高，必须为正，否则孔阵无从谈起。
        var boardWidth = input.BayWidthMm - input.UprightWidthMm;
        var boardHeight = input.TotalHeightMm - input.BaseTopHeightMm;
        if (input.UprightWidthMm >= 1 && input.BayWidthMm >= 1 && boardWidth <= 0)
        {
            errors.Add("uprightWidthMm", "立柱宽必须小于节宽");
        }

        if (input.BaseTopHeightMm >= 1 && input.TotalHeightMm >= 1 && boardHeight <= 0)
        {
            errors.Add("baseTopHeightMm", "底板面离地高必须小于总高");
        }

        result.HolePitchXMm = errors.DecimalRange("holePitchXMm", "横向孔距", input.HolePitchXMm, 0.5m, 200m);
        result.HolePitchYMm = errors.DecimalRange("holePitchYMm", "纵向孔距", input.HolePitchYMm, 0.5m, 200m);
        result.HoleDiameterMm = errors.DecimalRange("holeDiameterMm", "孔径", input.HoleDiameterMm, 0.01m, MaxDecimalMm);
        result.FirstHoleLeftMm = errors.DecimalRange("firstHoleLeftMm", "首孔距板左边", input.FirstHoleLeftMm, 0.01m, MaxDecimalMm);
        result.FirstHoleTopMm = errors.DecimalRange("firstHoleTopMm", "首孔距板顶", input.FirstHoleTopMm, 0.01m, MaxDecimalMm);

        // 孔数：缺省 / 0 自动换算，手填则要求 ≥ 1。
        if (input.HoleCols < 0 || input.HoleCols > MaxHoleCount)
        {
            errors.Add("holeCols", $"横向孔数必须在 1～{MaxHoleCount} 之间（留空自动换算）");
        }
        else if (input.HoleCols == 0 && boardWidth > 0 && result.HolePitchXMm >= 0.5m)
        {
            result.HoleCols = ComputeHoleCount(boardWidth, result.FirstHoleLeftMm, result.HolePitchXMm);
            if (result.HoleCols < 1)
            {
                errors.Add("holeCols", "按当前节宽、立柱宽、首孔边距与孔距换算不出至少 1 列孔，请检查尺寸");
            }
        }
        else
        {
            result.HoleCols = input.HoleCols;
        }

        if (input.HoleRows < 0 || input.HoleRows > MaxHoleCount)
        {
            errors.Add("holeRows", $"纵向孔数必须在 1～{MaxHoleCount} 之间（留空自动换算）");
        }
        else if (input.HoleRows == 0 && boardHeight > 0 && result.HolePitchYMm >= 0.5m)
        {
            result.HoleRows = ComputeHoleCount(boardHeight, result.FirstHoleTopMm, result.HolePitchYMm);
            if (result.HoleRows < 1)
            {
                errors.Add("holeRows", "按当前总高、底板面离地高、首孔边距与孔距换算不出至少 1 行孔，请检查尺寸");
            }
        }
        else
        {
            result.HoleRows = input.HoleRows;
        }

        return result;
    }

    private static HookModelDto NormalizeHook(HookModelDto input, PlanogramFieldErrors errors)
    {
        var result = new HookModelDto
        {
            Code = errors.RequiredText("code", "型号编码", input.Code, 50),
            Name = errors.RequiredText("name", "型号名称", input.Name, 100),
            Color = errors.OptionalText("color", "颜色", input.Color, 50),
            ProngCount = input.ProngCount,
            HasPriceTagArm = input.HasPriceTagArm,
            HoleSpanX = input.HoleSpanX,
            HoleSpanY = input.HoleSpanY,
            IsActive = input.IsActive,
        };

        if (input.ProngCount is not (1 or 2))
        {
            errors.Add("prongCount", "钩数必须是 1（单钩）或 2（双钩）");
        }

        result.ArmLengthMm = errors.DecimalRange("armLengthMm", "臂长", input.ArmLengthMm, 0.01m, MaxDecimalMm);
        result.DropMm = errors.DecimalRange("dropMm", "钩高", input.DropMm, 0.01m, MaxDecimalMm);
        result.WireDiameterMm = errors.DecimalRange("wireDiameterMm", "线径", input.WireDiameterMm, 0.01m, MaxDecimalMm);
        result.TipReserveMm = errors.DecimalRange("tipReserveMm", "臂端预留", input.TipReserveMm, 0m, MaxDecimalMm);
        if (result.TipReserveMm >= result.ArmLengthMm && result.ArmLengthMm > 0)
        {
            errors.Add("tipReserveMm", "臂端预留必须小于臂长");
        }

        // 价签架：带价签臂时宽高必须 > 0；不带时三个字段允许为 0（按原值保存，不强制清零）。
        if (input.HasPriceTagArm)
        {
            result.TagWidthMm = errors.DecimalRange("tagWidthMm", "价签架宽", input.TagWidthMm, 0.01m, MaxDecimalMm);
            result.TagHeightMm = errors.DecimalRange("tagHeightMm", "价签架高", input.TagHeightMm, 0.01m, MaxDecimalMm);
        }
        else
        {
            result.TagWidthMm = errors.DecimalRange("tagWidthMm", "价签架宽", input.TagWidthMm, 0m, MaxDecimalMm);
            result.TagHeightMm = errors.DecimalRange("tagHeightMm", "价签架高", input.TagHeightMm, 0m, MaxDecimalMm);
        }

        result.TagBottomAboveArmMm = errors.DecimalRange(
            "tagBottomAboveArmMm", "价签架底边高于挂货臂", input.TagBottomAboveArmMm, 0m, MaxDecimalMm);

        errors.IntRange("holeSpanX", "横向占孔", input.HoleSpanX, 1, 100);
        errors.IntRange("holeSpanY", "纵向占孔", input.HoleSpanY, 1, 100);

        if (input.LoadKg.HasValue)
        {
            result.LoadKg = errors.DecimalRange("loadKg", "承重", input.LoadKg.Value, 0.01m, 999.99m);
        }

        return result;
    }

    // ---------------------------------------------------------------- 映射

    private static void Apply(PlanogramShelfModel entity, ShelfModelDto dto)
    {
        entity.Code = dto.Code;
        entity.Name = dto.Name;
        entity.SideType = (byte)dto.SideType;
        entity.Color = dto.Color;
        entity.Source = dto.Source;
        entity.BayWidthMm = dto.BayWidthMm;
        entity.TotalHeightMm = dto.TotalHeightMm;
        entity.BaseDepthMm = dto.BaseDepthMm;
        entity.BaseTopHeightMm = dto.BaseTopHeightMm;
        entity.UprightWidthMm = dto.UprightWidthMm;
        entity.HolePitchXMm = dto.HolePitchXMm;
        entity.HolePitchYMm = dto.HolePitchYMm;
        entity.HoleDiameterMm = dto.HoleDiameterMm;
        entity.FirstHoleLeftMm = dto.FirstHoleLeftMm;
        entity.FirstHoleTopMm = dto.FirstHoleTopMm;
        entity.HoleCols = dto.HoleCols;
        entity.HoleRows = dto.HoleRows;
        entity.RowNumberingFrom = (byte)dto.RowNumberingFrom;
        entity.IsActive = dto.IsActive;
    }

    private static void Apply(PlanogramHookModel entity, HookModelDto dto)
    {
        entity.Code = dto.Code;
        entity.Name = dto.Name;
        entity.Color = dto.Color;
        entity.ProngCount = (byte)dto.ProngCount;
        entity.HasPriceTagArm = dto.HasPriceTagArm;
        entity.ArmLengthMm = dto.ArmLengthMm;
        entity.DropMm = dto.DropMm;
        entity.WireDiameterMm = dto.WireDiameterMm;
        entity.TipReserveMm = dto.TipReserveMm;
        entity.TagWidthMm = dto.TagWidthMm;
        entity.TagHeightMm = dto.TagHeightMm;
        entity.TagBottomAboveArmMm = dto.TagBottomAboveArmMm;
        entity.HoleSpanX = dto.HoleSpanX;
        entity.HoleSpanY = dto.HoleSpanY;
        entity.LoadKg = dto.LoadKg;
        entity.IsActive = dto.IsActive;
    }

    private static ShelfModelDto ToDto(PlanogramShelfModel entity, int referencedPlanCount) => new()
    {
        Guid = entity.Guid,
        Code = entity.Code,
        Name = entity.Name,
        SideType = entity.SideType,
        Color = entity.Color,
        Source = entity.Source,
        BayWidthMm = entity.BayWidthMm,
        TotalHeightMm = entity.TotalHeightMm,
        BaseDepthMm = entity.BaseDepthMm,
        BaseTopHeightMm = entity.BaseTopHeightMm,
        UprightWidthMm = entity.UprightWidthMm,
        HolePitchXMm = entity.HolePitchXMm,
        HolePitchYMm = entity.HolePitchYMm,
        HoleDiameterMm = entity.HoleDiameterMm,
        FirstHoleLeftMm = entity.FirstHoleLeftMm,
        FirstHoleTopMm = entity.FirstHoleTopMm,
        HoleCols = entity.HoleCols,
        HoleRows = entity.HoleRows,
        RowNumberingFrom = entity.RowNumberingFrom,
        IsActive = entity.IsActive,
        ReferencedPlanCount = referencedPlanCount,
        UpdatedAt = entity.UpdatedAt,
        UpdatedBy = entity.UpdatedBy,
    };

    private static HookModelDto ToDto(PlanogramHookModel entity, int referencedPlanCount) => new()
    {
        Guid = entity.Guid,
        Code = entity.Code,
        Name = entity.Name,
        Color = entity.Color,
        ProngCount = entity.ProngCount,
        HasPriceTagArm = entity.HasPriceTagArm,
        ArmLengthMm = entity.ArmLengthMm,
        DropMm = entity.DropMm,
        WireDiameterMm = entity.WireDiameterMm,
        TipReserveMm = entity.TipReserveMm,
        TagWidthMm = entity.TagWidthMm,
        TagHeightMm = entity.TagHeightMm,
        TagBottomAboveArmMm = entity.TagBottomAboveArmMm,
        HoleSpanX = entity.HoleSpanX,
        HoleSpanY = entity.HoleSpanY,
        LoadKg = entity.LoadKg,
        IsActive = entity.IsActive,
        ReferencedPlanCount = referencedPlanCount,
        UpdatedAt = entity.UpdatedAt,
        UpdatedBy = entity.UpdatedBy,
    };

    // ---------------------------------------------------------------- 查询辅助

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(page, 1), pageSize <= 0 ? 20 : Math.Min(pageSize, MaxPageSize));

    private static ApiResponse<T> DuplicateCode<T>(string code) =>
        ApiResponse<T>.Error(
            $"型号编码「{code}」已存在",
            PlanogramErrorCodes.CodeDuplicate,
            new List<PlanogramFieldError> { new("code", $"型号编码「{code}」已存在") });

    /// <summary>
    /// 编码是否已被占用。统一转小写比较：SQL Server 默认排序规则本来就不分大小写，
    /// 这里让 SQLite 等区分大小写的库行为一致。excludeGuid 用于更新时排除自身。
    /// </summary>
    private async Task<bool> ShelfCodeExistsAsync(string code, string? excludeGuid)
    {
        var lower = code.ToLowerInvariant();
        var query = _db.Queryable<PlanogramShelfModel>().Where(model => SqlFunc.ToLower(model.Code) == lower);
        if (excludeGuid is not null)
        {
            query = query.Where(model => model.Guid != excludeGuid);
        }

        return await query.AnyAsync();
    }

    private async Task<bool> HookCodeExistsAsync(string code, string? excludeGuid)
    {
        var lower = code.ToLowerInvariant();
        var query = _db.Queryable<PlanogramHookModel>().Where(model => SqlFunc.ToLower(model.Code) == lower);
        if (excludeGuid is not null)
        {
            query = query.Where(model => model.Guid != excludeGuid);
        }

        return await query.AnyAsync();
    }

    /// <summary>每个货架型号被多少个方案引用（含已归档方案：归档方案可以恢复，所以同样算引用）。</summary>
    private async Task<Dictionary<string, int>> CountShelfReferencesAsync(List<string> guids)
    {
        if (guids.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        var referenced = await _db.Queryable<PlanogramHeader>()
            .Where(plan => guids.Contains(plan.ShelfModelGuid))
            .Select(plan => plan.ShelfModelGuid)
            .ToListAsync();
        return referenced.GroupBy(guid => guid).ToDictionary(group => group.Key, group => group.Count());
    }

    /// <summary>每个挂钩型号被多少个方案引用（明细里出现过即算，同一方案多行只计一次）。</summary>
    private async Task<Dictionary<string, int>> CountHookPlanReferencesAsync(List<string> guids)
    {
        if (guids.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        var items = await _db.Queryable<PlanogramItem>()
            .Where(item => guids.Contains(item.HookModelGuid))
            .ToListAsync();
        return items
            .GroupBy(item => item.HookModelGuid)
            .ToDictionary(group => group.Key, group => group.Select(item => item.PlanogramGuid).Distinct().Count());
    }
}
