using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreSpecialProductCategories;

/// <summary>分店级特殊商品分类接口的稳定业务错误码（客户端按它映射中文）。</summary>
public static class StoreSpecialCategoryErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";

    /// <summary>分店不在当前账号「管理的分店」（主分店口径）范围内。</summary>
    public const string StoreOutOfScope = "STORE_OUT_OF_SCOPE";

    public const string StoreNotFound = "STORE_NOT_FOUND";
    public const string CategoryNotFound = "SPECIAL_CATEGORY_NOT_FOUND";

    /// <summary>同店已有同名分类（不区分大小写）。</summary>
    public const string NameDuplicate = "SPECIAL_CATEGORY_NAME_DUPLICATE";

    /// <summary>每家店最多 50 个分类。</summary>
    public const string LimitReached = "SPECIAL_CATEGORY_LIMIT_REACHED";

    /// <summary>提交的排列与当前数据不一致（别处刚改过），客户端应先刷新。</summary>
    public const string OrderConflict = "SPECIAL_CATEGORY_ORDER_CONFLICT";

    /// <summary>mode=replace 但 confirmReplace 不是 true。</summary>
    public const string ReplaceNotConfirmed = "SPECIAL_CATEGORY_REPLACE_NOT_CONFIRMED";

    /// <summary>并发归类同一商品撞了归属主键，重读重试一次仍失败（客户端提示稍后重试 / 刷新）。</summary>
    public const string AssignConflict = "SPECIAL_CATEGORY_ASSIGN_CONFLICT";

    /// <summary>sync/execute 里某家目标店失败（已回滚）：result.error 只给这个固定码，详情只写服务端日志。</summary>
    public const string SyncStoreFailed = "SYNC_STORE_FAILED";
}

/// <summary>被跳过商品的稳定英文原因码。</summary>
public static class StoreSpecialCategorySkipReasons
{
    /// <summary>本店没有该商品的有效 StoreRetailPrice 行（本接口不新建价格行）。</summary>
    public const string NoStorePrice = "NO_STORE_PRICE";

    /// <summary>markAsSpecial=false 时要求商品已是特殊商品。</summary>
    public const string NotSpecialProduct = "NOT_SPECIAL_PRODUCT";
}

public interface IStoreSpecialProductCategoryService
{
    Task<ApiResponse<StoreSpecialCategoryListDto>> GetListAsync(string? storeCode);

    Task<ApiResponse<StoreSpecialCategoryDto>> CreateAsync(StoreSpecialCategorySaveRequestDto? request);

    Task<ApiResponse<StoreSpecialCategoryDto>> UpdateAsync(string id, StoreSpecialCategorySaveRequestDto? request);

    Task<ApiResponse<StoreSpecialCategoryDeleteResultDto>> DeleteAsync(string id, string? storeCode);

    Task<ApiResponse<StoreSpecialCategoryOrderResultDto>> ReorderCategoriesAsync(StoreSpecialCategoryOrderRequestDto? request);

    Task<ApiResponse<StoreSpecialCategoryProductPageDto>> GetProductsAsync(
        string? storeCode, string? categoryId, string? keyword, int page, int pageSize);

    Task<ApiResponse<StoreSpecialCategoryCandidateResultDto>> SearchCandidatesAsync(
        string? storeCode, string? keyword, int? limit);

    Task<ApiResponse<StoreSpecialCategoryAssignResultDto>> AssignAsync(StoreSpecialCategoryAssignRequestDto? request);

    Task<ApiResponse<StoreSpecialCategoryUnmarkResultDto>> UnmarkAsync(StoreSpecialCategoryUnmarkRequestDto? request);

    Task<ApiResponse<StoreSpecialCategoryOkDto>> ReorderProductsAsync(string id, StoreSpecialCategoryProductOrderRequestDto? request);

    Task<ApiResponse<StoreSpecialCategorySyncTargetsDto>> GetSyncTargetsAsync(string? sourceStoreCode);

    Task<ApiResponse<StoreSpecialCategorySyncPreviewDto>> PreviewSyncAsync(StoreSpecialCategorySyncRequestDto? request);

    Task<ApiResponse<StoreSpecialCategorySyncExecuteDto>> ExecuteSyncAsync(StoreSpecialCategorySyncRequestDto? request);
}

/// <summary>
/// 分店级「特殊商品分类」的维护（手机 App 专用，设备会话由控制器拒绝）：分类增删改排序、商品归类、取消标记、分类内排序、同步到其他分店。
/// 权限码（SpecialProducts.View / Manage）在控制器按方法区分，这里只校验分店范围：
/// 来源与所有目标分店都必须落在 CurrentUserManageableStoreScopeService.GetScopeAsync()（主分店口径，Admin / 仓库经理不限分店）里。
/// 数据口径：归属行（StoreSpecialProductCategoryItem）取消特殊标记时不删，所有读取都再与
/// StoreRetailPrice.IsSpecialProduct = 1 求交集，才算「分类里的商品」。
/// </summary>
public sealed partial class StoreSpecialProductCategoryService : IStoreSpecialProductCategoryService
{
    internal const int MaxNameLength = 30;
    internal const int MaxCategoriesPerStore = 50;
    internal const int MaxBatchProductCodes = 500;
    internal const int MaxSyncTargets = 100;
    internal const int DefaultCandidateLimit = 30;
    internal const int MaxCandidateLimit = 50;
    internal const int MinCandidateKeywordLength = 2;
    internal const int MaxProductPageSize = 200;

    /// <summary>「未分类」视图在 products 接口 categoryId 参数里的取值。</summary>
    internal const string UncategorizedKey = "uncategorized";

    // 按块写库 / 按块 IN 查询：SQL Server 单条命令参数上限约 2100，批量更新每行带多个参数，留足余量。
    private const int QueryChunkSize = 500;
    private const int WriteChunkSize = 100;

    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private readonly ISqlSugarClient _db;
    private readonly ICurrentUserManageableStoreScopeService _scopeService;
    private readonly ILogger<StoreSpecialProductCategoryService> _logger;

    /// <summary>仅测试使用：每家目标店在事务里写完数据、提交之前回调，抛异常即可模拟该店中途失败（验证单店回滚与隔离）。</summary>
    internal Func<string, Task>? BeforeSyncCommitForTesting { get; set; }

    /// <summary>仅测试使用：归类写归属行之前回调（在事务内），抛唯一冲突异常即可模拟并发撞主键。</summary>
    internal Func<Task>? BeforeAssignInsertForTesting { get; set; }

    public StoreSpecialProductCategoryService(
        SqlSugarContext context,
        ICurrentUserManageableStoreScopeService scopeService,
        ILogger<StoreSpecialProductCategoryService> logger)
        : this(context.Db, scopeService, logger)
    {
    }

    internal StoreSpecialProductCategoryService(
        ISqlSugarClient db,
        ICurrentUserManageableStoreScopeService scopeService,
        ILogger<StoreSpecialProductCategoryService> logger)
    {
        _db = db;
        _scopeService = scopeService;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 内部基础设施

    /// <summary>业务校验失败：带稳定错误码，由 <see cref="RunAsync{T}"/> 转成 ApiResponse。</summary>
    private sealed class CategoryException : Exception
    {
        public CategoryException(string code, string message) : base(message) => Code = code;

        public string Code { get; }
    }

    private static CategoryException Validation(string message) =>
        new(StoreSpecialCategoryErrorCodes.ValidationError, message);

    private static async Task<ApiResponse<T>> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return ApiResponse<T>.OK(await action());
        }
        catch (CategoryException ex)
        {
            return ApiResponse<T>.Error(ex.Message, ex.Code);
        }
    }

    /// <summary>已通过分店范围校验的分店上下文（StoreCode 取库里的规范写法）。</summary>
    private sealed record StoreContext(
        string StoreCode, string StoreName, string Actor, CurrentUserManageableStoreScope Scope, bool IsActive);

    /// <summary>
    /// 校验分店：必须在「管理的分店」范围内且分店存在。
    /// 范围口径沿用 CurrentUserManageableStoreScopeService.GetScopeAsync()（主分店；Admin / 仓库经理不限）。
    /// </summary>
    private async Task<StoreContext> RequireStoreAsync(string? storeCode, string fieldLabel = "分店")
    {
        var code = (storeCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            throw Validation($"缺少{fieldLabel}代码");
        }

        var scope = await _scopeService.GetWarehouseScopeAsync();
        if (!scope.IsAllowed || !scope.CanAccessStoreCode(code))
        {
            throw new CategoryException(
                StoreSpecialCategoryErrorCodes.StoreOutOfScope,
                "无权管理该分店的特殊商品分类");
        }

        var store = await _db.Queryable<Store>()
            .Where(s => s.StoreCode == code && !s.IsDeleted)
            .FirstAsync();
        if (store is null)
        {
            throw new CategoryException(StoreSpecialCategoryErrorCodes.StoreNotFound, "分店不存在");
        }

        return new StoreContext(store.StoreCode, store.StoreName, scope.ActorLabel, scope, store.IsActive);
    }

    private async Task InTransactionAsync(Func<Task> work)
    {
        await _db.Ado.BeginTranAsync();
        try
        {
            await work();
            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static async Task ForEachChunkAsync<T>(IReadOnlyList<T> source, int size, Func<List<T>, Task> action)
    {
        for (var offset = 0; offset < source.Count; offset += size)
        {
            await action(source.Skip(offset).Take(size).ToList());
        }
    }

    // ------------------------------------------------------------------ 读取辅助

    private async Task<List<StoreSpecialProductCategory>> LoadCategoriesAsync(string storeCode)
    {
        var list = await _db.Queryable<StoreSpecialProductCategory>()
            .Where(c => c.StoreCode == storeCode)
            .ToListAsync();
        return list
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Guid, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<StoreSpecialProductCategory?> FindCategoryAsync(string storeCode, string categoryId)
    {
        var id = categoryId.Trim();
        return await _db.Queryable<StoreSpecialProductCategory>()
            .Where(c => c.Guid == id && c.StoreCode == storeCode)
            .FirstAsync();
    }

    private async Task<List<StoreSpecialProductCategoryItem>> LoadItemsAsync(string storeCode)
    {
        return await _db.Queryable<StoreSpecialProductCategoryItem>()
            .Where(i => i.StoreCode == storeCode)
            .ToListAsync();
    }

    /// <summary>
    /// 本店「当前仍是特殊商品」的价格行：返回这些商品的全部启用且未删除价格行（调用方按商品编码分组）。
    /// 口径与 POS（SpecialProductCategoryService.LoadCurrentSpecialProductCodesAsync / CatalogService 特殊商品分页）完全一致，
    /// 全部读取（列表、计数、候选、assign 判断、同步来源与 replace 取消标记集合）都走这里或 <see cref="IsCurrentlySpecial"/>：
    /// 只看启用且未删除的价格行，同一商品取最新一行（UpdatedAt ?? CreatedAt，并列按 UUID 升序），该行带标记才算特殊
    /// （旧行是特殊、新行不是 = 不是特殊商品；停用行上的旧标记不算）。
    /// </summary>
    private async Task<List<StoreRetailPrice>> LoadSpecialRowsAsync(string storeCode)
    {
        // 第 1 步：启用行里带标记的商品编码（超集）。
        var flagged = (await _db.Queryable<StoreRetailPrice>()
                .Where(r => r.StoreCode == storeCode && r.IsActive && !r.IsDeleted && r.IsSpecialProduct && r.ProductCode != null)
                .Select(r => r.ProductCode)
                .ToListAsync())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 第 2 步：这些商品的全部启用行，按「最新一行是否带标记」裁决。
        var result = new List<StoreRetailPrice>();
        await ForEachChunkAsync(flagged, QueryChunkSize, async chunk =>
        {
            var rows = await _db.Queryable<StoreRetailPrice>()
                .Where(r => r.StoreCode == storeCode && r.IsActive && !r.IsDeleted && chunk.Contains(r.ProductCode!))
                .ToListAsync();
            foreach (var group in rows.GroupBy(r => r.ProductCode!, StringComparer.OrdinalIgnoreCase))
            {
                if (IsCurrentlySpecial(group))
                {
                    result.AddRange(group);
                }
            }
        });
        return result;
    }

    /// <summary>某商品的「当前行」：启用且未删除的价格行里最新的一行（UpdatedAt ?? CreatedAt 降序，并列 UUID 升序）；没有则 null。</summary>
    internal static StoreRetailPrice? LatestActiveRow(IEnumerable<StoreRetailPrice>? rows) =>
        rows?.Where(r => r.IsActive && !r.IsDeleted)
            .OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt)
            .ThenBy(r => r.UUID, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>统一的「仍是特殊商品」判定：当前行（见 <see cref="LatestActiveRow"/>）带特殊标记。</summary>
    internal static bool IsCurrentlySpecial(IEnumerable<StoreRetailPrice>? rows) =>
        LatestActiveRow(rows)?.IsSpecialProduct == true;

    /// <summary>本店指定商品的全部未删除价格行（按商品编码不区分大小写分组）。</summary>
    private async Task<Dictionary<string, List<StoreRetailPrice>>> LoadRowsByCodeAsync(
        string storeCode, IReadOnlyCollection<string> productCodes)
    {
        var result = new Dictionary<string, List<StoreRetailPrice>>(StringComparer.OrdinalIgnoreCase);
        await ForEachChunkAsync(productCodes.ToList(), QueryChunkSize, async chunk =>
        {
            var rows = await _db.Queryable<StoreRetailPrice>()
                .Where(r => r.StoreCode == storeCode && !r.IsDeleted && chunk.Contains(r.ProductCode!))
                .ToListAsync();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ProductCode))
                {
                    continue;
                }

                if (!result.TryGetValue(row.ProductCode, out var list))
                {
                    result[row.ProductCode] = list = new List<StoreRetailPrice>();
                }

                list.Add(row);
            }
        });
        return result;
    }

    /// <summary>商品展示用的轻量字段。</summary>
    private sealed class ProductLite
    {
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductImage { get; set; }
        public decimal? RetailPrice { get; set; }
    }

    private async Task<Dictionary<string, ProductLite>> LoadProductsAsync(IReadOnlyCollection<string> productCodes)
    {
        var result = new Dictionary<string, ProductLite>(StringComparer.OrdinalIgnoreCase);
        await ForEachChunkAsync(productCodes.ToList(), QueryChunkSize, async chunk =>
        {
            var rows = await _db.Queryable<Product>()
                .Where(p => !p.IsDeleted && chunk.Contains(p.ProductCode!))
                .Select(p => new ProductLite
                {
                    ProductCode = p.ProductCode,
                    ProductName = p.ProductName,
                    EnglishName = p.EnglishName,
                    ItemNumber = p.ItemNumber,
                    Barcode = p.Barcode,
                    ProductImage = p.ProductImage,
                    RetailPrice = p.RetailPrice,
                })
                .ToListAsync();
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.ProductCode))
                {
                    result.TryAdd(row.ProductCode, row);
                }
            }
        });
        return result;
    }

    private static string DisplayNameOf(ProductLite? product, string productCode)
    {
        if (!string.IsNullOrWhiteSpace(product?.ProductName))
        {
            return product!.ProductName!.Trim();
        }

        return !string.IsNullOrWhiteSpace(product?.EnglishName) ? product!.EnglishName!.Trim() : productCode;
    }

    /// <summary>
    /// 一个商品在本店可能有多行价格：取「当前行」（启用且最近更新，与 POS 选价一致）；全部停用时退而取最近更新的一行。
    /// 门店价为空时由调用方回退商品主档零售价。
    /// </summary>
    private static StoreRetailPrice? PickPrimaryRow(IEnumerable<StoreRetailPrice> rows)
    {
        var list = rows.ToList();
        return LatestActiveRow(list)
            ?? list.OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt).ThenBy(r => r.UUID, StringComparer.Ordinal).FirstOrDefault();
    }

    private static decimal? RetailPriceOf(IEnumerable<StoreRetailPrice>? rows, ProductLite? product)
    {
        var primary = rows is null ? null : PickPrimaryRow(rows);
        return primary?.StoreRetailPriceValue ?? product?.RetailPrice;
    }

    /// <summary>
    /// 只取「分类仍存在」的归属：理论上删分类时归属行一并删除，这里再防一手孤儿行，孤儿行按未分类处理。
    /// </summary>
    private static Dictionary<string, StoreSpecialProductCategoryItem> IndexItemsByCode(
        IEnumerable<StoreSpecialProductCategoryItem> items, IEnumerable<StoreSpecialProductCategory> categories)
    {
        var categoryIds = categories.Select(c => c.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, StoreSpecialProductCategoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (categoryIds.Contains(item.CategoryGuid))
            {
                result[item.ProductCode] = item;
            }
        }

        return result;
    }

    private static StoreSpecialCategoryDto ToDto(StoreSpecialProductCategory category, int itemCount) => new()
    {
        Id = category.Guid,
        Name = category.Name,
        Color = category.Color,
        SortOrder = category.SortOrder,
        ItemCount = itemCount,
    };

    private static List<StoreSpecialCategoryDto> ToDtos(
        IEnumerable<StoreSpecialProductCategory> categories,
        IReadOnlyDictionary<string, StoreSpecialProductCategoryItem> itemsByCode,
        HashSet<string> specialCodes)
    {
        var counts = specialCodes
            .Where(itemsByCode.ContainsKey)
            .GroupBy(code => itemsByCode[code].CategoryGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return categories.Select(c => ToDto(c, counts.GetValueOrDefault(c.Guid))).ToList();
    }

    private static HashSet<string> SpecialCodesOf(IEnumerable<StoreRetailPrice> specialRows) =>
        specialRows
            .Where(r => !string.IsNullOrWhiteSpace(r.ProductCode))
            .Select(r => r.ProductCode!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ 校验辅助

    private static string NormalizeName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw Validation("分类名称不能为空");
        }

        if (text.Length > MaxNameLength)
        {
            throw Validation($"分类名称不能超过 {MaxNameLength} 个字符");
        }

        return text;
    }

    /// <summary>颜色为空 = null；否则必须是 #RRGGBB，统一存大写。</summary>
    private static string? NormalizeColor(string? color)
    {
        var text = color?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!ColorPattern.IsMatch(text))
        {
            throw Validation("分类颜色必须是 #RRGGBB 格式");
        }

        return text.ToUpperInvariant();
    }

    /// <summary>商品编码列表：去空白、去重（保持传入顺序），不能为空，单次最多 500 个。</summary>
    private static List<string> NormalizeCodes(IEnumerable<string>? productCodes)
    {
        var codes = (productCodes ?? Enumerable.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            throw Validation("请选择商品");
        }

        if (codes.Count > MaxBatchProductCodes)
        {
            throw Validation($"一次最多处理 {MaxBatchProductCodes} 个商品");
        }

        return codes;
    }

    // ------------------------------------------------------------------ 分类维护

    public Task<ApiResponse<StoreSpecialCategoryListDto>> GetListAsync(string? storeCode) => RunAsync(async () =>
    {
        var store = await RequireStoreAsync(storeCode);
        var categories = await LoadCategoriesAsync(store.StoreCode);
        var itemsByCode = IndexItemsByCode(await LoadItemsAsync(store.StoreCode), categories);
        var specialCodes = SpecialCodesOf(await LoadSpecialRowsAsync(store.StoreCode));
        return new StoreSpecialCategoryListDto
        {
            StoreCode = store.StoreCode,
            TotalCount = specialCodes.Count,
            UncategorizedCount = specialCodes.Count(code => !itemsByCode.ContainsKey(code)),
            Categories = ToDtos(categories, itemsByCode, specialCodes),
        };
    });

    public Task<ApiResponse<StoreSpecialCategoryDto>> CreateAsync(StoreSpecialCategorySaveRequestDto? request) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(request?.StoreCode);
            var name = NormalizeName(request?.Name);
            var color = NormalizeColor(request?.Color);

            var categories = await LoadCategoriesAsync(store.StoreCode);
            if (categories.Count >= MaxCategoriesPerStore)
            {
                throw new CategoryException(
                    StoreSpecialCategoryErrorCodes.LimitReached,
                    $"每家分店最多 {MaxCategoriesPerStore} 个分类");
            }

            if (categories.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw DuplicateName();
            }

            var now = DateTime.UtcNow;
            var category = new StoreSpecialProductCategory
            {
                StoreCode = store.StoreCode,
                Name = name,
                Color = color,
                // 追加到末尾：取现有最大顺序 + 1（没有分类时从 0 开始）。
                SortOrder = categories.Count == 0 ? 0 : categories.Max(c => c.SortOrder) + 1,
                CreatedAt = now,
                CreatedBy = store.Actor,
                UpdatedAt = now,
                UpdatedBy = store.Actor,
            };

            try
            {
                await _db.Insertable(category).ExecuteCommandAsync();
            }
            catch (Exception ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
            {
                // 预检用 .NET 的 OrdinalIgnoreCase，数据库唯一索引按库排序规则比较，两边口径可能不同；
                // 预检通过后撞唯一索引（2601 / 2627）同样对外给 409 重名，而不是 500。
                throw DuplicateName();
            }

            _logger.LogInformation(
                "StoreSpecialProductCategory created actor={Actor} store={Store} category={Category} name={Name}",
                store.Actor, store.StoreCode, category.Guid, category.Name);
            return ToDto(category, 0);
        });

    public Task<ApiResponse<StoreSpecialCategoryDto>> UpdateAsync(string id, StoreSpecialCategorySaveRequestDto? request) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(request?.StoreCode);
            var name = NormalizeName(request?.Name);
            var color = NormalizeColor(request?.Color);

            var category = await FindCategoryAsync(store.StoreCode, id ?? string.Empty)
                ?? throw CategoryNotFound();
            var categories = await LoadCategoriesAsync(store.StoreCode);
            if (categories.Any(c =>
                    !string.Equals(c.Guid, category.Guid, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw DuplicateName();
            }

            category.Name = name;
            category.Color = color;
            category.UpdatedAt = DateTime.UtcNow;
            category.UpdatedBy = store.Actor;
            try
            {
                await _db.Updateable(category)
                    .UpdateColumns(c => new { c.Name, c.Color, c.UpdatedAt, c.UpdatedBy })
                    .ExecuteCommandAsync();
            }
            catch (Exception ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
            {
                throw DuplicateName();
            }

            var itemsByCode = IndexItemsByCode(await LoadItemsAsync(store.StoreCode), categories);
            var specialCodes = SpecialCodesOf(await LoadSpecialRowsAsync(store.StoreCode));
            var itemCount = specialCodes.Count(code =>
                itemsByCode.TryGetValue(code, out var item)
                && string.Equals(item.CategoryGuid, category.Guid, StringComparison.OrdinalIgnoreCase));
            _logger.LogInformation(
                "StoreSpecialProductCategory updated actor={Actor} store={Store} category={Category} name={Name}",
                store.Actor, store.StoreCode, category.Guid, category.Name);
            return ToDto(category, itemCount);
        });

    public Task<ApiResponse<StoreSpecialCategoryDeleteResultDto>> DeleteAsync(string id, string? storeCode) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(storeCode);
            var category = await FindCategoryAsync(store.StoreCode, id ?? string.Empty)
                ?? throw CategoryNotFound();

            var removedVisible = 0;
            await InTransactionAsync(async () =>
            {
                // 事务内读、事务内按分类整体删除：期间别人刚归入这个分类的商品不会变成孤儿归属行。
                var specialCodes = SpecialCodesOf(await LoadSpecialRowsAsync(store.StoreCode));
                var items = (await LoadItemsAsync(store.StoreCode))
                    .Where(i => string.Equals(i.CategoryGuid, category.Guid, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // 对外报告的是「仍是特殊商品、因此回到未分类」的商品数；取消标记但保留归属的行也一并删除，但用户看不到它们。
                removedVisible = items.Count(i => specialCodes.Contains(i.ProductCode));

                await _db.Deleteable<StoreSpecialProductCategoryItem>()
                    .Where(i => i.StoreCode == store.StoreCode && i.CategoryGuid == category.Guid)
                    .ExecuteCommandAsync();
                await _db.Deleteable<StoreSpecialProductCategory>()
                    .Where(c => c.Guid == category.Guid && c.StoreCode == store.StoreCode)
                    .ExecuteCommandAsync();

                // 删掉后把剩余分类的顺序压实成 0..n-1，保持「同店顺序从 0 连续」的约定。
                var remaining = (await LoadCategoriesAsync(store.StoreCode)).ToList();
                await RenumberCategoriesAsync(remaining, remaining.Select(c => c.Guid).ToList(), store.Actor);
            });

            _logger.LogInformation(
                "StoreSpecialProductCategory deleted actor={Actor} store={Store} category={Category} name={Name} removedItems={RemovedItems}",
                store.Actor, store.StoreCode, category.Guid, category.Name, removedVisible);
            return new StoreSpecialCategoryDeleteResultDto { RemovedItemCount = removedVisible };
        });

    public Task<ApiResponse<StoreSpecialCategoryOrderResultDto>> ReorderCategoriesAsync(
        StoreSpecialCategoryOrderRequestDto? request) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(request?.StoreCode);
            var requested = (request?.CategoryIds ?? new List<string>())
                .Select(id => (id ?? string.Empty).Trim())
                .ToList();

            await InTransactionAsync(async () =>
            {
                var categories = await LoadCategoriesAsync(store.StoreCode);
                // 必须恰好是本店全部分类 id 的一个排列：缺一个、多一个、重复都视为冲突（别处刚改过），客户端应先刷新。
                var current = categories.Select(c => c.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var distinct = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (requested.Count != categories.Count || distinct.Count != requested.Count || !distinct.SetEquals(current))
                {
                    throw new CategoryException(
                        StoreSpecialCategoryErrorCodes.OrderConflict,
                        "分类列表已变化，请刷新后再排序");
                }

                await RenumberCategoriesAsync(categories, requested, store.Actor);
            });

            var latest = await LoadCategoriesAsync(store.StoreCode);
            var itemsByCode = IndexItemsByCode(await LoadItemsAsync(store.StoreCode), latest);
            var specialCodes = SpecialCodesOf(await LoadSpecialRowsAsync(store.StoreCode));
            return new StoreSpecialCategoryOrderResultDto { Categories = ToDtos(latest, itemsByCode, specialCodes) };
        });

    /// <summary>按给定 id 顺序把分类顺序写成 0..n-1，只更新变化的行。</summary>
    private async Task RenumberCategoriesAsync(
        List<StoreSpecialProductCategory> categories, IReadOnlyList<string> orderedIds, string actor)
    {
        var byId = categories.ToDictionary(c => c.Guid, StringComparer.OrdinalIgnoreCase);
        var changed = new List<StoreSpecialProductCategory>();
        var now = DateTime.UtcNow;
        for (var index = 0; index < orderedIds.Count; index++)
        {
            var category = byId[orderedIds[index]];
            if (category.SortOrder != index)
            {
                category.SortOrder = index;
                category.UpdatedAt = now;
                category.UpdatedBy = actor;
                changed.Add(category);
            }
        }

        // 只写 SortOrder 与审计列：不用旧值覆盖并发的改名 / 换色。UpdatedAt 用服务端当前时间即可。
        await ForEachChunkAsync(changed, WriteChunkSize, chunk =>
            _db.Updateable(chunk)
                .UpdateColumns(c => new { c.SortOrder, c.UpdatedAt, c.UpdatedBy })
                .ExecuteCommandAsync());
    }

    private static CategoryException DuplicateName() =>
        new(StoreSpecialCategoryErrorCodes.NameDuplicate, "本店已有同名分类");

    private static CategoryException CategoryNotFound() =>
        new(StoreSpecialCategoryErrorCodes.CategoryNotFound, "分类不存在或已被删除");
}
