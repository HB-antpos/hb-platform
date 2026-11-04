using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Planogram;

public interface IProductDisplaySpecService
{
    /// <summary>商品参数列表；status 取 all（默认）/ set（已设参数）/ unset（未设参数）。</summary>
    Task<ApiResponse<PagedResult<ProductSpecListItemDto>>> ListAsync(
        string? keyword, string? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<ApiResponse<ProductSpecListItemDto>> GetAsync(string productCode, CancellationToken cancellationToken);

    /// <summary>新增或覆盖某商品的陈列参数（PUT 语义），返回保存后的列表行。</summary>
    Task<ApiResponse<ProductSpecListItemDto>> SaveAsync(
        string productCode, ProductDisplaySpecDto input, string actor, CancellationToken cancellationToken);

    /// <summary>清除某商品的陈列参数（商品主档不动）。被陈列方案引用时拒绝（PLANOGRAM_SPEC_IN_USE）。</summary>
    Task<ApiResponse<object>> DeleteAsync(string productCode, CancellationToken cancellationToken);
}

/// <summary>
/// 商品陈列参数（附录 A）：按 ProductCode 业务键联 Product，只面向启用且未软删的商品。
/// 权限在控制器按方法区分（GET 为 Planogram.View，PUT 为 Planogram.Edit，DELETE 为 Planogram.Manage）。
/// </summary>
public sealed class ProductDisplaySpecService : IProductDisplaySpecService
{
    private const int MaxMm = 10000;
    private const int MaxPageSize = 500;
    private const int MaxHookQty = 9999;

    // 表里 HookQtyOverridesJson 是 nvarchar(1000)；每个挂钩型号约占 50 字符，超长直接拒绝而不是截断。
    private const int MaxOverridesJsonLength = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ISqlSugarClient _db;
    private readonly TimeProvider _time;

    public ProductDisplaySpecService(SqlSugarContext context, TimeProvider? timeProvider = null)
        : this(context.Db, timeProvider)
    {
    }

    internal ProductDisplaySpecService(ISqlSugarClient db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<PagedResult<ProductSpecListItemDto>>> ListAsync(
        string? keyword, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var mode = (status ?? "all").Trim().ToLowerInvariant();
        if (mode.Length == 0)
        {
            mode = "all";
        }

        if (mode is not ("all" or "set" or "unset"))
        {
            var errors = new PlanogramFieldErrors();
            errors.Add("status", "状态只能是 all、set 或 unset");
            return errors.ToFailure<PagedResult<ProductSpecListItemDto>>();
        }

        page = Math.Max(page, 1);
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, MaxPageSize);

        var query = ActiveProducts();
        var key = keyword?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            // 货号 / 条码 / 名称 / 英文名；货号即 ProductCode 业务键，也一并匹配，方便直接粘贴。
            query = query.Where(product =>
                product.ProductCode!.Contains(key)
                || product.ItemNumber!.Contains(key)
                || product.Barcode!.Contains(key)
                || product.ProductName.Contains(key)
                || product.EnglishName!.Contains(key));
        }

        if (mode == "set")
        {
            query = query.Where(product =>
                SqlFunc.Subqueryable<ProductDisplaySpec>().Where(spec => spec.ProductCode == product.ProductCode).Any());
        }
        else if (mode == "unset")
        {
            query = query.Where(product =>
                !SqlFunc.Subqueryable<ProductDisplaySpec>().Where(spec => spec.ProductCode == product.ProductCode).Any());
        }

        RefAsync<int> total = 0;
        var products = await query.OrderBy(product => product.ProductCode).ToPageListAsync(page, pageSize, total);
        var items = await BuildRowsAsync(products);
        return ApiResponse<PagedResult<ProductSpecListItemDto>>.OK(new PagedResult<ProductSpecListItemDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    public async Task<ApiResponse<ProductSpecListItemDto>> GetAsync(string productCode, CancellationToken cancellationToken)
    {
        var product = await FindActiveProductAsync(productCode);
        if (product is null)
        {
            return ApiResponse<ProductSpecListItemDto>.Error("商品不存在或已停用", PlanogramErrorCodes.NotFound);
        }

        var rows = await BuildRowsAsync(new List<Product> { product });
        return ApiResponse<ProductSpecListItemDto>.OK(rows[0]);
    }

    public async Task<ApiResponse<ProductSpecListItemDto>> SaveAsync(
        string productCode, ProductDisplaySpecDto input, string actor, CancellationToken cancellationToken)
    {
        var product = await FindActiveProductAsync(productCode);
        if (product is null)
        {
            return ApiResponse<ProductSpecListItemDto>.Error("商品不存在或已停用", PlanogramErrorCodes.NotFound);
        }

        var errors = new PlanogramFieldErrors();
        var overridesJson = await ValidateAsync(input, errors);
        if (errors.Any)
        {
            return errors.ToFailure<ProductSpecListItemDto>();
        }

        var code = product.ProductCode!;
        var now = _time.GetUtcNow().UtcDateTime;
        var existing = await _db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == code);
        var entity = existing ?? new ProductDisplaySpec { ProductCode = code, CreatedAt = now, CreatedBy = actor };
        entity.WidthMm = input.WidthMm;
        entity.HeightMm = input.HeightMm;
        entity.DepthMm = input.DepthMm;
        entity.HangHoleOffsetMm = input.HangHoleOffsetMm;
        entity.HangHoleType = (byte)input.HangHoleType;
        entity.UnitWeightG = input.UnitWeightG;
        entity.DefaultHookGuid = string.IsNullOrWhiteSpace(input.DefaultHookGuid) ? null : input.DefaultHookGuid.Trim();
        entity.HookQtyOverridesJson = overridesJson;
        entity.Source = (byte)input.Source;
        entity.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        entity.UpdatedAt = now;
        entity.UpdatedBy = actor;
        if (existing is null)
        {
            await _db.Insertable(entity).ExecuteCommandAsync();
        }
        else
        {
            await _db.Updateable(entity).ExecuteCommandAsync();
        }

        var rows = await BuildRowsAsync(new List<Product> { product });
        return ApiResponse<ProductSpecListItemDto>.OK(rows[0], "已保存");
    }

    public async Task<ApiResponse<object>> DeleteAsync(string productCode, CancellationToken cancellationToken)
    {
        var code = (productCode ?? string.Empty).Trim();
        var hasSpec = code.Length > 0
            && await _db.Queryable<ProductDisplaySpec>().Where(spec => spec.ProductCode == code).AnyAsync();
        if (hasSpec)
        {
            // 批 2：方案明细里还在用这个商品时不能清参数 —— 清了会让方案里的条目全部变成「缺参数」。
            // 同一方案多行只算一个方案；归档方案可恢复，所以同样算引用。
            var referencingPlans = (await _db.Queryable<PlanogramItem>()
                    .Where(item => item.ProductCode == code)
                    .Select(item => item.PlanogramGuid)
                    .ToListAsync())
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (referencingPlans > 0)
            {
                return ApiResponse<object>.Error(
                    $"该商品已被 {referencingPlans} 个陈列方案引用，不能清除陈列参数；请先从方案里移除该商品，或直接修改参数",
                    PlanogramErrorCodes.SpecInUse);
            }

            await _db.Deleteable<ProductDisplaySpec>().Where(spec => spec.ProductCode == code).ExecuteCommandAsync();
            return ApiResponse<object>.CreateSuccess("已清除");
        }

        // 没有参数可清：商品存在则幂等成功；商品本身也不存在（或已停用）才报不存在。
        if (code.Length > 0 && await FindActiveProductAsync(code) is not null)
        {
            return ApiResponse<object>.CreateSuccess("该商品没有陈列参数");
        }

        return ApiResponse<object>.Error("商品不存在或已停用", PlanogramErrorCodes.NotFound);
    }

    /// <summary>
    /// 方案详情用：按货号批量取参数行（含零售价、是否已设参数、被引用数）。
    /// 与列表不同，不要求商品启用 / 未软删 —— 方案里已有的商品即使后来被停用也要能显示；主档里查不到的货号不返回。
    /// </summary>
    internal async Task<List<ProductSpecListItemDto>> GetRowsByCodesAsync(IReadOnlyCollection<string> productCodes)
    {
        var codes = productCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count == 0)
        {
            return new List<ProductSpecListItemDto>();
        }

        var products = new List<Product>();
        foreach (var chunk in codes.Chunk(500))
        {
            var list = chunk.ToList();
            products.AddRange(await _db.Queryable<Product>().Where(product => list.Contains(product.ProductCode!)).ToListAsync());
        }

        // 货号全库唯一；万一有脏数据重复，取第一条，避免下游按货号建字典时抛异常。
        var distinctProducts = products
            .GroupBy(product => product.ProductCode!, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(product => product.ProductCode, StringComparer.Ordinal)
            .ToList();

        // 分批构建：BuildRowsAsync 内部有 IN 子句，一个方案最多 2000 个款，分批避免触到 SQL Server 单语句 2100 参数上限。
        var rows = new List<ProductSpecListItemDto>(distinctProducts.Count);
        foreach (var chunk in distinctProducts.Chunk(500))
        {
            rows.AddRange(await BuildRowsAsync(chunk.ToList()));
        }

        return rows;
    }

    // ---------------------------------------------------------------- 校验

    /// <summary>
    /// 校验商品参数并把挂数覆盖序列化成 JSON（空覆盖返回 null）。
    /// 覆盖里的 hookGuid 与默认挂钩都必须是已存在的挂钩型号（停用的型号也算存在：只是不能再新建方案时选用）。
    /// </summary>
    private async Task<string?> ValidateAsync(ProductDisplaySpecDto input, PlanogramFieldErrors errors)
    {
        errors.IntRange("widthMm", "包装宽", input.WidthMm, 1, MaxMm);
        errors.IntRange("heightMm", "包装高", input.HeightMm, 1, MaxMm);
        errors.IntRange("depthMm", "包装厚", input.DepthMm, 1, MaxMm);

        if (input.HangHoleType is not (0 or 1 or 2))
        {
            errors.Add("hangHoleType", "吊孔类型必须是 0（无）、1（圆孔）或 2（欧式挂孔）");
        }

        if (input.Source is not (1 or 2 or 3))
        {
            errors.Add("source", "数据来源必须是 1（人工量取）、2（供应商资料）或 3（同套装主档）");
        }

        if (input.HangHoleOffsetMm < 0)
        {
            errors.Add("hangHoleOffsetMm", "吊孔偏移不能为负数");
        }
        else if (input.HangHoleType != 0 && input.HeightMm > 0 && input.HangHoleOffsetMm > input.HeightMm)
        {
            errors.Add("hangHoleOffsetMm", "吊孔偏移不能大于包装高");
        }

        if (input.UnitWeightG.HasValue && (input.UnitWeightG.Value < 1 || input.UnitWeightG.Value > 1_000_000))
        {
            errors.Add("unitWeightG", "单件重必须在 1～1000000 克之间（不填表示未知）");
        }

        if (input.Notes is { } notes && notes.Trim().Length > 500)
        {
            errors.Add("notes", "备注不能超过 500 个字符");
        }

        // 挂钩引用：默认挂钩 + 覆盖里的每个 hookGuid 必须存在。
        var overrides = input.HookQtyOverrides ?? new Dictionary<string, int>();
        var hookGuids = overrides.Keys.ToList();
        var defaultHook = string.IsNullOrWhiteSpace(input.DefaultHookGuid) ? null : input.DefaultHookGuid.Trim();
        if (defaultHook is not null)
        {
            hookGuids.Add(defaultHook);
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        if (hookGuids.Count > 0)
        {
            var distinct = hookGuids.Distinct().ToList();
            var found = await _db.Queryable<PlanogramHookModel>()
                .Where(hook => distinct.Contains(hook.Guid))
                .Select(hook => hook.Guid)
                .ToListAsync();
            known.UnionWith(found);
        }

        if (defaultHook is not null && !known.Contains(defaultHook))
        {
            errors.Add("defaultHookGuid", "默认挂钩型号不存在");
        }

        foreach (var (hookGuid, qty) in overrides)
        {
            if (qty < 1 || qty > MaxHookQty)
            {
                errors.Add("hookQtyOverrides", $"每钩挂数必须在 1～{MaxHookQty} 之间");
            }

            if (!known.Contains(hookGuid))
            {
                errors.Add("hookQtyOverrides", $"挂钩型号「{hookGuid}」不存在");
            }
        }

        if (overrides.Count == 0)
        {
            return null;
        }

        // 键排序后序列化：同样内容得到同样文本，便于比较与审计。
        var ordered = overrides.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var json = JsonSerializer.Serialize(ordered, JsonOptions);
        if (json.Length > MaxOverridesJsonLength)
        {
            errors.Add("hookQtyOverrides", "挂数覆盖的挂钩型号太多，超出可保存的长度");
        }

        return json;
    }

    // ---------------------------------------------------------------- 查询与映射

    private ISugarQueryable<Product> ActiveProducts() =>
        _db.Queryable<Product>().Where(product =>
            product.IsActive && !product.IsDeleted && product.ProductCode != null && product.ProductCode != "");

    private async Task<Product?> FindActiveProductAsync(string productCode)
    {
        var code = (productCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return null;
        }

        return await ActiveProducts().FirstAsync(product => product.ProductCode == code);
    }

    private async Task<List<ProductSpecListItemDto>> BuildRowsAsync(List<Product> products)
    {
        if (products.Count == 0)
        {
            return new List<ProductSpecListItemDto>();
        }

        var codes = products.Select(product => product.ProductCode!).ToList();
        var specs = (await _db.Queryable<ProductDisplaySpec>().Where(spec => codes.Contains(spec.ProductCode)).ToListAsync())
            .ToDictionary(spec => spec.ProductCode, StringComparer.Ordinal);

        var categoryGuids = products
            .Select(product => product.ProductCategoryGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct()
            .ToList();
        var categories = categoryGuids.Count == 0
            ? new Dictionary<string, string>()
            : (await _db.Queryable<ProductCategory>().Where(category => categoryGuids.Contains(category.CategoryGUID)).ToListAsync())
                .ToDictionary(category => category.CategoryGUID, category => category.CategoryName);

        // 被多少个方案引用：同一方案里同一商品出现多行只算一次。
        var references = await _db.Queryable<PlanogramItem>()
            .Where(item => codes.Contains(item.ProductCode))
            .ToListAsync();
        var referenceCounts = references
            .GroupBy(item => item.ProductCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.PlanogramGuid).Distinct().Count());

        return products.Select(product =>
        {
            var code = product.ProductCode!;
            specs.TryGetValue(code, out var spec);
            return new ProductSpecListItemDto
            {
                ProductCode = code,
                ItemNumber = product.ItemNumber ?? string.Empty,
                Barcode = product.Barcode ?? string.Empty,
                ProductName = product.ProductName,
                EnglishName = product.EnglishName,
                ProductImage = product.ProductImage,
                RetailPrice = product.RetailPrice ?? 0m,
                CategoryName = product.ProductCategoryGUID is { Length: > 0 } guid && categories.TryGetValue(guid, out var name)
                    ? name
                    : null,
                HasSpec = spec is not null,
                ReferencedPlanCount = referenceCounts.GetValueOrDefault(code),
                Spec = spec is null ? null : ToDto(spec),
                UpdatedAt = spec?.UpdatedAt,
                UpdatedBy = spec?.UpdatedBy,
            };
        }).ToList();
    }

    private static ProductDisplaySpecDto ToDto(ProductDisplaySpec spec) => new()
    {
        ProductCode = spec.ProductCode,
        WidthMm = spec.WidthMm,
        HeightMm = spec.HeightMm,
        DepthMm = spec.DepthMm,
        HangHoleOffsetMm = spec.HangHoleOffsetMm,
        HangHoleType = spec.HangHoleType,
        UnitWeightG = spec.UnitWeightG,
        DefaultHookGuid = spec.DefaultHookGuid,
        HookQtyOverrides = ParseOverrides(spec.HookQtyOverridesJson),
        Source = spec.Source,
        Notes = spec.Notes,
    };

    /// <summary>解析覆盖 JSON；历史脏数据解析失败时按「无覆盖」处理，不让列表接口 500。</summary>
    internal static Dictionary<string, int> ParseOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }
}
