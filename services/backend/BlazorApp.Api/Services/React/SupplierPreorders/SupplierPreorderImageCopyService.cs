using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Features.ProductDescriptions;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>把一张图片写进 COS 商品图片桶（便于测试替换）。</summary>
public interface ISupplierPreorderImageStore
{
    bool IsConfigured { get; }

    /// <summary>成功返回公开地址，失败返回 (null, 原因)。</summary>
    Task<(string? Url, string? Error)> UploadAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken);
}

/// <summary>默认实现：TencentCloudUploadService 的商品图片桶（已开通数据万象，前端用 imageMogr2 取 WebP 缩略图）。</summary>
public sealed class SupplierPreorderCosImageStore : ISupplierPreorderImageStore
{
    private readonly TencentCloudUploadService _cos;

    public SupplierPreorderCosImageStore(TencentCloudUploadService cos)
    {
        _cos = cos;
    }

    public bool IsConfigured => _cos.HasImageBucketConfiguration();

    public async Task<(string? Url, string? Error)> UploadAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        var result = await _cos.UploadImageBucketBytesAsync(objectKey, contentType, content, cancellationToken);
        return result.Success && result.Data is not null ? (result.Data.DownloadUrl, null) : (null, result.Message ?? "上传失败");
    }
}

/// <summary>
/// 新品图片转存 COS（方案 3.2）：原表图片地址（如 unclebills.hosted-by-files.com，单张约 700KB）可能很大、防盗链或过期失效，
/// 后台逐条下载（复用 ProductImageFetcher：只连公网地址、限 10MB、防解压炸弹，统一转成长边 ≤ 1568 的 JPEG），
/// 存进商品图片桶；页面缩略图由 COS 数据万象按需生成 WebP。
/// - 有我方商品图（匹配到的商品 ProductImage 是 http 地址）的不转存，记 Skipped；
/// - 同一个原图地址只下载一次：先找已经转存过的同地址商品直接复用；对象名按地址哈希，重复上传也只是覆盖同一个对象；
/// - 失败记 Failed 与原因，最多尝试 3 次，每次间隔至少 1 小时；管理端可以「重试失败」把次数清零。
/// </summary>
public sealed class SupplierPreorderImageCopyService
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    private const int BatchSize = 30;
    private const string ObjectKeyPrefix = "supplier-preorder";

    private readonly ISqlSugarClient _db;
    private readonly IProductImageFetcher _fetcher;
    private readonly ISupplierPreorderImageStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public SupplierPreorderImageCopyService(ISqlSugarClient db, IProductImageFetcher fetcher, ISupplierPreorderImageStore store, ILogger logger, TimeProvider? timeProvider = null)
    {
        _db = db;
        _fetcher = fetcher;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime NowUtc => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>对象名：supplier-preorder/{供应商}/{原图地址 SHA-256 前 24 位}.jpg —— 同一地址永远是同一个对象。</summary>
    public static string BuildObjectKey(string localSupplierCode, string sourceUrl)
    {
        var supplier = new string((localSupplierCode ?? string.Empty).Trim().Where(char.IsLetterOrDigit).ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl.Trim()))).ToLowerInvariant()[..24];
        return $"{ObjectKeyPrefix}/{(supplier.Length == 0 ? "unknown" : supplier)}/{hash}.jpg";
    }

    /// <summary>这一行现在要不要处理：有原图地址、没转存过、不是已跳过，失败的未满 3 次且距上次至少 1 小时。</summary>
    public static bool IsDue(SupplierPreorderFormItem item, DateTime nowUtc, DateTime? lastAttemptUtc)
    {
        if (!item.IsActive || string.IsNullOrWhiteSpace(item.ImageSourceUrl) || !string.IsNullOrEmpty(item.ImageCosUrl))
        {
            return false;
        }

        return item.ImageCopyStatus switch
        {
            null => true,
            SupplierPreorderImageCopyStatuses.Failed => item.ImageCopyAttempts < MaxAttempts
                                                        && (lastAttemptUtc is null || nowUtc - lastAttemptUtc.Value >= RetryAfter),
            _ => false,
        };
    }

    private static bool IsHttpImage(string? image) =>
        !string.IsNullOrWhiteSpace(image)
        && (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>后台任务入口：处理一批，返回处理了几行（0 表示没有待办，可以歇一会儿）。</summary>
    public async Task<int> RunBatchAsync(CancellationToken cancellationToken)
    {
        if (!_store.IsConfigured || !_db.DbMaintenance.IsAnyTable("SupplierPreorderFormItem", false))
        {
            return 0;
        }

        var now = NowUtc;
        var liveForms = await _db.Queryable<SupplierPreorderForm>()
            .Where(f => !f.IsDeleted)
            .Select(f => new { f.FormGuid, f.LocalSupplierCode })
            .ToListAsync();
        if (liveForms.Count == 0)
        {
            return 0;
        }

        var supplierByForm = liveForms.ToDictionary(f => f.FormGuid, f => f.LocalSupplierCode, StringComparer.OrdinalIgnoreCase);
        var formGuids = liveForms.Select(f => f.FormGuid).ToList();
        // 只取候选（失败的按上次尝试时间在内存里再筛）；ImageCopiedAtUtc 对失败行记的是最近一次尝试时间
        var candidates = await _db.Queryable<SupplierPreorderFormItem>()
            .Where(i => formGuids.Contains(i.FormGuid) && i.IsActive && i.ImageSourceUrl != null && i.ImageSourceUrl != ""
                        && i.ImageCosUrl == null
                        && (i.ImageCopyStatus == null || (i.ImageCopyStatus == SupplierPreorderImageCopyStatuses.Failed && i.ImageCopyAttempts < MaxAttempts)))
            .OrderBy(i => i.FormGuid)
            .OrderBy(i => i.SortOrder)
            .Take(BatchSize * 4)
            .ToListAsync();
        var due = candidates.Where(i => IsDue(i, now, i.ImageCopiedAtUtc)).Take(BatchSize).ToList();
        if (due.Count == 0)
        {
            return 0;
        }

        // 有我方商品图的不转存
        var matchedCodes = due.Select(i => i.MatchedProductCode).Where(c => !string.IsNullOrWhiteSpace(c)).Cast<string>().Distinct().ToList();
        var productImages = matchedCodes.Count == 0
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : (await _db.Queryable<Product>()
                    .Where(p => matchedCodes.Contains(p.ProductCode!))
                    .Select(p => new { p.ProductCode, p.ProductImage })
                    .ToListAsync())
                .Where(p => p.ProductCode is not null)
                .GroupBy(p => p.ProductCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ProductImage, StringComparer.OrdinalIgnoreCase);

        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.MatchedProductCode is not null && IsHttpImage(productImages.GetValueOrDefault(item.MatchedProductCode)))
            {
                item.ImageCopyStatus = SupplierPreorderImageCopyStatuses.Skipped;
                item.ImageCopyError = null;
                await SaveAsync(item);
                continue;
            }

            await CopyOneAsync(item, supplierByForm.GetValueOrDefault(item.FormGuid) ?? string.Empty, cancellationToken);
        }

        return due.Count;
    }

    private async Task CopyOneAsync(SupplierPreorderFormItem item, string localSupplierCode, CancellationToken cancellationToken)
    {
        var source = item.ImageSourceUrl!.Trim();
        var now = NowUtc;
        // 同一原图地址别的行（往年订单表、同表重复行）已转存过：直接复用，不再下载
        var existing = await _db.Queryable<SupplierPreorderFormItem>()
            .Where(i => i.ImageSourceUrl == source && i.ImageCosUrl != null && i.ImageCopyStatus == SupplierPreorderImageCopyStatuses.Done)
            .Select(i => i.ImageCosUrl)
            .FirstAsync();
        if (!string.IsNullOrEmpty(existing))
        {
            MarkDone(item, existing, now);
            await SaveAsync(item);
            return;
        }

        item.ImageCopyAttempts++;
        item.ImageCopiedAtUtc = now;
        try
        {
            var image = await _fetcher.FetchAsync(source, cancellationToken);
            var (url, error) = await _store.UploadAsync(BuildObjectKey(localSupplierCode, source), image.Data, image.MediaType, cancellationToken);
            if (url is null)
            {
                MarkFailed(item, error ?? "上传失败");
            }
            else
            {
                MarkDone(item, url, now);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProductImageFetchException ex)
        {
            MarkFailed(item, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "供应商预订单图片转存失败：{ItemGuid} {Url}", item.ItemGuid, source);
            MarkFailed(item, "下载或上传出错");
        }

        await SaveAsync(item);
    }

    private static void MarkDone(SupplierPreorderFormItem item, string url, DateTime now)
    {
        item.ImageCosUrl = url;
        item.ImageCopyStatus = SupplierPreorderImageCopyStatuses.Done;
        item.ImageCopyError = null;
        item.ImageCopiedAtUtc = now;
    }

    private static void MarkFailed(SupplierPreorderFormItem item, string error)
    {
        item.ImageCopyStatus = SupplierPreorderImageCopyStatuses.Failed;
        item.ImageCopyError = error.Length > 300 ? error[..300] : error;
    }

    private Task<int> SaveAsync(SupplierPreorderFormItem item) =>
        _db.Updateable(item)
            .UpdateColumns(i => new { i.ImageCosUrl, i.ImageCopyStatus, i.ImageCopyAttempts, i.ImageCopyError, i.ImageCopiedAtUtc })
            .ExecuteCommandAsync();
}
