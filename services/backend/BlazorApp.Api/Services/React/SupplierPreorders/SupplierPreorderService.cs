using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using SqlSugar;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>业务错误：带错误码与 HTTP 状态，控制器统一转成 ApiResponse。</summary>
public sealed class SupplierPreorderException : Exception
{
    public SupplierPreorderException(string message, string errorCode, int statusCode = 400) : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string ErrorCode { get; }
    public int StatusCode { get; }
}

public interface ISupplierPreorderService
{
    // 导入
    Task<SupplierPreorderParseResultDto> ParseFileAsync(Stream stream, string fileName, string contentType, long length, string? localSupplierCode);
    Task<SupplierPreorderParseResultDto> ParsePastedAsync(SupplierPreorderParsePastedRequestDto request);
    Task<SupplierPreorderPreviewResultDto> PreviewAsync(SupplierPreorderPreviewRequestDto request);
    Task<SupplierPreorderFormDetailDto> CreateFormAsync(SupplierPreorderCreateFormDto request);
    Task<List<SupplierPreorderSupplierOptionDto>> GetSuppliersAsync();
    Task<List<SupplierPreorderStoreRefDto>> GetActiveStoresAsync();

    // 订单表（管理）
    Task<PagedResult<SupplierPreorderFormSummaryDto>> GetFormsAsync(SupplierPreorderFormQueryDto query);
    Task<SupplierPreorderFormDetailDto> GetFormAsync(string formGuid);
    Task<SupplierPreorderFormDetailDto> UpdateFormAsync(string formGuid, SupplierPreorderUpdateFormDto request);
    Task<SupplierPreorderFormDetailDto> SetStatusAsync(string formGuid, SupplierPreorderSetStatusDto request);
    Task<bool> DeleteFormAsync(string formGuid);
    Task<PagedResult<SupplierPreorderFormItemDto>> GetFormItemsAsync(string formGuid, SupplierPreorderFormItemQueryDto query);
    Task<List<SupplierPreorderFormStoreStatusDto>> GetFormStoresAsync(string formGuid);
    Task<List<SupplierPreorderFormStoreStatusDto>> UpdateFormStoresAsync(string formGuid, SupplierPreorderUpdateFormStoresDto request);
    Task<SupplierPreorderCopyStoresResultDto> CopyStoresFromPreviousAsync(string formGuid);
    Task<SupplierPreorderMatrixDto> GetMatrixAsync(string formGuid, SupplierPreorderMatrixQueryDto query);
    Task<SupplierPreorderExportFile> ExportAsync(string formGuid, string format);

    // 下单人员授权
    Task<SupplierPreorderOrderersOverviewDto> GetOrderersAsync();
    Task<SupplierPreorderOrdererDto> SaveOrdererAsync(string userGuid, SupplierPreorderSaveOrdererDto request);
    Task<bool> RemoveOrdererAsync(string userGuid);
    Task<List<SupplierPreorderUserCandidateDto>> SearchUserCandidatesAsync(string? keyword);
    Task<PagedResult<SupplierPreorderAuthLogDto>> GetAuthLogsAsync(SupplierPreorderAuthLogQueryDto query);

    // 下单
    Task<List<SupplierPreorderOrderingFormDto>> GetOrderingFormsAsync();
    Task<SupplierPreorderOrderingContextDto> GetOrderingContextAsync(string formGuid);
    Task<SupplierPreorderOrderingSummaryDto> GetOrderingSummaryAsync(string formGuid, string storeGuid);
    Task<PagedResult<SupplierPreorderOrderingItemDto>> GetOrderingItemsAsync(string formGuid, SupplierPreorderOrderingItemQueryDto query);
    Task<SupplierPreorderSaveLineResultDto> SaveLineAsync(string formGuid, string storeGuid, string itemGuid, SupplierPreorderSaveLineDto request);
    Task<SupplierPreorderOrderingSummaryDto> SubmitStoreOrderAsync(string formGuid, string storeGuid);
    Task<SupplierPreorderExportFile> ExportStoreOrderAsync(string formGuid, string storeGuid);

    // 去年历史与参考商品（PR 2）
    Task<SupplierPreorderItemHistoryDetailDto> GetOrderingItemHistoryAsync(string formGuid, string itemGuid, string storeGuid);
    Task<SupplierPreorderFormDetailDto> RecomputeHistoryAsync(string formGuid);
    Task<SupplierPreorderRefCandidatesDto> GetRefCandidatesAsync(string formGuid, string itemGuid, string? keyword);
    Task<SupplierPreorderFormItemDto> SetItemRefAsync(string formGuid, string itemGuid, SupplierPreorderSetRefDto request);
    Task<SupplierPreorderRefCandidatesDto> GetImportRefCandidatesAsync(SupplierPreorderImportRefCandidatesRequestDto request);

    // 新品图片转存 COS
    Task<SupplierPreorderFormDetailDto> RetryFailedImagesAsync(string formGuid);
}

public sealed record SupplierPreorderExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// 供应商节日预订单服务。订单表规模在几千款以内，统计、筛选、分页都在内存里做（一次读出整张表的商品与明细），
/// 换来 SQL 简单、SQL Server 与 SQLite 测试库行为一致。
/// </summary>
public sealed partial class SupplierPreorderService : ISupplierPreorderService
{
    internal static readonly string[] HolidayTypes = { "Christmas", "Halloween", "Easter", "Diaries", "Custom" };
    internal static readonly int[] AllowedPageSizes = { 50, 100, 200, 500 };
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISqlSugarClient _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRoleService _roleService;
    private readonly ILogger<SupplierPreorderService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SupplierPreorderHistoryService _history;
    private readonly bool _imageStoreConfigured;

    public SupplierPreorderService(
        SqlSugarContext context,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IHttpContextAccessor httpContextAccessor,
        IRoleService roleService,
        ILogger<SupplierPreorderService> logger,
        TimeProvider? timeProvider = null,
        ISupplierPreorderImageStore? imageStore = null
    )
        : this(context.Db, currentUser, authorization, httpContextAccessor, roleService, logger, timeProvider, imageStore)
    {
    }

    internal SupplierPreorderService(
        ISqlSugarClient db,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IHttpContextAccessor httpContextAccessor,
        IRoleService roleService,
        ILogger<SupplierPreorderService> logger,
        TimeProvider? timeProvider = null,
        ISupplierPreorderImageStore? imageStore = null
    )
    {
        _db = db;
        _currentUser = currentUser;
        _authorization = authorization;
        _httpContextAccessor = httpContextAccessor;
        _roleService = roleService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _history = new SupplierPreorderHistoryService(db, logger, _timeProvider);
        // 测试里不传：按已配置处理
        _imageStoreConfigured = imageStore?.IsConfigured ?? true;
    }

    private DateTime NowUtc => _timeProvider.GetUtcNow().UtcDateTime;

    private string CurrentUserName
    {
        get
        {
            var name = _currentUser.GetCurrentUsername();
            return string.IsNullOrWhiteSpace(name) ? "System" : (name.Length > 100 ? name[..100] : name);
        }
    }

    private string CurrentUserGuid => _currentUser.GetCurrentUserGuid() ?? string.Empty;

    private async Task<bool> HasPolicyAsync(string policy)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user is not null && (await _authorization.AuthorizeAsync(user, null, policy)).Succeeded;
    }

    private Task<bool> IsManagerAsync() => HasPolicyAsync(Permissions.SupplierPreorder.Manage);

    /// <summary>下单接口入口：持有 Order 或 Manage 才能进；具体能改哪家店另由下单人员授权决定。</summary>
    private async Task<bool> RequireOrderingEntryAsync()
    {
        var isManager = await IsManagerAsync();
        if (!isManager && !await HasPolicyAsync(Permissions.SupplierPreorder.Order))
        {
            throw new SupplierPreorderException("没有「供应商节日预订单下单」权限", "FORBIDDEN", 403);
        }

        return isManager;
    }

    private async Task<SupplierPreorderForm> RequireFormAsync(string formGuid)
    {
        var form = string.IsNullOrWhiteSpace(formGuid)
            ? null
            : await _db.Queryable<SupplierPreorderForm>().FirstAsync(f => f.FormGuid == formGuid && !f.IsDeleted);
        return form ?? throw new SupplierPreorderException("订单表不存在或已删除", "FORM_NOT_FOUND", 404);
    }

    private async Task<List<Store>> LoadActiveStoresAsync() =>
        await _db.Queryable<Store>()
            .Where(s => !s.IsDeleted && s.IsActive)
            .OrderBy(s => s.StoreCode)
            .ToListAsync();

    private static SupplierPreorderStoreRefDto ToStoreRef(string guid, string code, string name) =>
        new() { StoreGuid = guid, StoreCode = code, StoreName = name };

    private static int NormalizePageSize(int pageSize) =>
        AllowedPageSizes.Contains(pageSize) ? pageSize : 50;

    private static PagedResult<T> Page<T>(IReadOnlyList<T> source, int page, int pageSize)
    {
        var size = NormalizePageSize(pageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(source.Count / (double)size));
        var current = Math.Clamp(page, 1, totalPages);
        return new PagedResult<T>
        {
            Items = source.Skip((current - 1) * size).Take(size).ToList(),
            Total = source.Count,
            Page = current,
            PageSize = size,
        };
    }

    // ---------------- 订单表的整体数据快照（统计、矩阵、导出、下单共用） ----------------

    /// <summary>一张订单表的商品 + 开启分店 + 各店订单与明细。</summary>
    private sealed class FormSnapshot
    {
        public SupplierPreorderForm Form { get; init; } = null!;
        public List<SupplierPreorderFormItem> Items { get; init; } = new();
        public Dictionary<string, SupplierPreorderFormItem> ItemsByGuid { get; init; } = new();
        public List<SupplierPreorderFormStore> EnabledStores { get; init; } = new();
        public List<SupplierPreorderStoreOrder> Orders { get; init; } = new();
        public List<SupplierPreorderStoreOrderLine> Lines { get; init; } = new();

        public HashSet<string> EnabledStoreGuids => EnabledStores.Select(s => s.StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>只算开启分店、有效商品的明细（关掉的分店保留数据但不计入）。</summary>
        public IEnumerable<(SupplierPreorderStoreOrder Order, SupplierPreorderStoreOrderLine Line, SupplierPreorderFormItem Item)> CountedLines()
        {
            var enabled = EnabledStoreGuids;
            var ordersByGuid = Orders.ToDictionary(o => o.OrderGuid);
            foreach (var line in Lines)
            {
                if (line.Cartons <= 0
                    || !ordersByGuid.TryGetValue(line.OrderGuid, out var order)
                    || !enabled.Contains(order.StoreGuid)
                    || !ItemsByGuid.TryGetValue(line.ItemGuid, out var item)
                    || !item.IsActive)
                {
                    continue;
                }

                yield return (order, line, item);
            }
        }
    }

    private async Task<FormSnapshot> LoadSnapshotAsync(SupplierPreorderForm form)
    {
        var items = await _db.Queryable<SupplierPreorderFormItem>()
            .Where(i => i.FormGuid == form.FormGuid)
            .OrderBy(i => i.SortOrder)
            .ToListAsync();
        var stores = await _db.Queryable<SupplierPreorderFormStore>()
            .Where(s => s.FormGuid == form.FormGuid)
            .OrderBy(s => s.StoreCode)
            .ToListAsync();
        var orders = await _db.Queryable<SupplierPreorderStoreOrder>()
            .Where(o => o.FormGuid == form.FormGuid)
            .ToListAsync();
        var lines = await _db.Queryable<SupplierPreorderStoreOrderLine>()
            .Where(l => l.FormGuid == form.FormGuid)
            .ToListAsync();
        return new FormSnapshot
        {
            Form = form,
            Items = items,
            ItemsByGuid = items.ToDictionary(i => i.ItemGuid),
            EnabledStores = stores,
            Orders = orders,
            Lines = lines,
        };
    }

    internal static decimal LineAmount(int cartons, SupplierPreorderFormItem item) =>
        Math.Round(cartons * item.CartonQty * item.Price, 2);

    internal static decimal LineCbm(int cartons, SupplierPreorderFormItem item) =>
        cartons * (item.CartonCbm ?? 0m);

    private static string? ResolveImageUrl(SupplierPreorderFormItem item, IReadOnlyDictionary<string, string?> productImages)
    {
        // 优先我方商品图（COS，体积小）；其次后台转存到 COS 的原表图片；都没有时用原表图片地址（供应商站点，单张约 700KB，前端懒加载）
        if (item.MatchedProductCode is not null
            && productImages.TryGetValue(item.MatchedProductCode, out var image)
            && !string.IsNullOrWhiteSpace(image)
            && (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return image.Trim();
        }

        return string.IsNullOrWhiteSpace(item.ImageCosUrl) ? item.ImageSourceUrl : item.ImageCosUrl;
    }

    private async Task<Dictionary<string, string?>> LoadProductImagesAsync(IEnumerable<SupplierPreorderFormItem> items)
    {
        var codes = items.Select(i => i.MatchedProductCode).Where(c => !string.IsNullOrEmpty(c)).Distinct().Cast<string>().ToList();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in codes.Chunk(500))
        {
            var list = chunk.ToList();
            var rows = await _db.Queryable<Product>()
                .Where(p => list.Contains(p.ProductCode!))
                .Select(p => new { p.ProductCode, p.ProductImage })
                .ToListAsync();
            foreach (var row in rows.Where(r => r.ProductCode is not null))
            {
                result[row.ProductCode!] = row.ProductImage;
            }
        }

        return result;
    }

    private async Task WriteAuthLogAsync(string scope, string action, string? detail, string? formGuid = null, string? targetUserGuid = null, string? targetUserName = null)
    {
        await _db.Insertable(new SupplierPreorderAuthLog
        {
            Scope = scope,
            Action = action,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            FormGuid = formGuid,
            TargetUserGuid = targetUserGuid,
            TargetUserName = targetUserName,
            OperatorUserGuid = CurrentUserGuid,
            OperatorName = CurrentUserName,
            CreatedAtUtc = NowUtc,
        }).ExecuteCommandAsync();
    }

    /// <summary>事务内执行；失败时把原始异常抛出（SqlSugar 的 UseTranAsync 默认只返回结果不抛）。</summary>
    private async Task RunInTransactionAsync(Func<Task> action)
    {
        var result = await _db.Ado.UseTranAsync(action);
        if (!result.IsSuccess)
        {
            throw result.ErrorException ?? new InvalidOperationException("供应商预订单保存失败");
        }
    }

    private static string SerializeMapping(object value) => JsonSerializer.Serialize(value, JsonOptions);

    internal sealed class StoredMapping
    {
        public string? SheetName { get; set; }
        public int HeaderRowNo { get; set; }
        public string? HeaderSignature { get; set; }
        public SupplierPreorderColumnMappingDto Mapping { get; set; } = new();
    }

    private static StoredMapping? ReadStoredMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredMapping>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
