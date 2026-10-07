using BlazorApp.Api.Data;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 存单与收据图片：签发私有直传地址、提交时校验并转正、详情里签发短时效下载地址。
/// 调用方（StoreCashService）负责先校验分店范围；这里只认「同一分店、同一上传人、未过期、未被占用」。
/// </summary>
public interface IStoreCashAttachmentService
{
    Task<ApiResponse<CashAttachmentUploadSignatureDto>> CreateUploadSignatureAsync(
        string storeCode,
        string userGuid,
        string contentType,
        long fileSize,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 提交存款或支出前调用：校验这些附件都属于本店、本人、未过期、未被占用，并把待确认的对象转正为私有正式对象。
    /// 成功返回按入参顺序排列的附件行（均为 Promoted）；失败时已转正的保持 Promoted，可在重试时直接复用。
    /// </summary>
    Task<ApiResponse<IReadOnlyList<StoreCashAttachment>>> PrepareAsync(
        string storeCode,
        string userGuid,
        IReadOnlyList<string> attachmentGuids,
        CancellationToken cancellationToken
    );

    Task<Dictionary<string, List<CashAttachmentDto>>> GetLinkedAsync(
        IReadOnlyCollection<string> ownerGuids,
        CancellationToken cancellationToken
    );

    Task<Dictionary<string, int>> CountLinkedAsync(
        IReadOnlyCollection<string> ownerGuids,
        CancellationToken cancellationToken
    );
}

public sealed class StoreCashAttachmentService : IStoreCashAttachmentService
{
    /// <summary>票据有效期：从签发到提交表单之间可能要翻找存单、反复修改，留 2 小时。</summary>
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(2);

    private const int SignatureLifetimeSeconds = 900;
    private const int DownloadUrlLifetimeSeconds = 300;
    private const int MaxPendingPerUser = 40;
    private const int MaxParallelCosOperations = 4;

    private readonly ISqlSugarClient _db;
    private readonly TencentCloudUploadService _uploadService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StoreCashAttachmentService> _logger;

    public StoreCashAttachmentService(
        SqlSugarContext context,
        TencentCloudUploadService uploadService,
        ILogger<StoreCashAttachmentService> logger,
        TimeProvider timeProvider
    )
        : this(context.Db, uploadService, logger, timeProvider) { }

    internal StoreCashAttachmentService(
        ISqlSugarClient db,
        TencentCloudUploadService uploadService,
        ILogger<StoreCashAttachmentService> logger,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _uploadService = uploadService;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<ApiResponse<CashAttachmentUploadSignatureDto>> CreateUploadSignatureAsync(
        string storeCode,
        string userGuid,
        string contentType,
        long fileSize,
        CancellationToken cancellationToken
    )
    {
        var normalizedType = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!StoreCashImageRules.IsValid(normalizedType, fileSize))
        {
            return ApiResponse<CashAttachmentUploadSignatureDto>.Error(
                "仅支持不超过 5 MiB 的 JPEG、PNG 或 WebP 图片",
                StoreCashConstants.ErrorCodes.AttachmentInvalid
            );
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var pendingCount = await _db.Queryable<StoreCashAttachment>()
            .CountAsync(item => item.UploadedByUserGuid == userGuid
                && item.Status == StoreCashConstants.AttachmentStatus.Pending
                && item.ExpiresAtUtc > now);
        if (pendingCount >= MaxPendingPerUser)
        {
            return ApiResponse<CashAttachmentUploadSignatureDto>.Error(
                "待确认的图片过多，请先提交已上传的图片",
                StoreCashConstants.ErrorCodes.AttachmentInvalid
            );
        }

        var token = Guid.NewGuid().ToString("N");
        var pendingKey = StoreCashImageRules.BuildPendingObjectKey(storeCode, token, normalizedType);
        var finalKey = StoreCashImageRules.BuildFinalObjectKey(storeCode, now, token, normalizedType);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-cos-meta-owner"] = userGuid,
            ["x-cos-meta-kind"] = "cash",
            ["x-cos-meta-content-type"] = normalizedType,
            ["x-cos-meta-file-size"] = fileSize.ToString(),
            // 存单含金额与账户信息：未确认的对象和正式对象一样全程私有。
            ["x-cos-acl"] = "private",
        };
        var attachment = new StoreCashAttachment
        {
            StoreCode = storeCode,
            Status = StoreCashConstants.AttachmentStatus.Pending,
            PendingObjectKey = pendingKey,
            FinalObjectKey = finalKey,
            ContentType = normalizedType,
            FileSize = fileSize,
            UploadedByUserGuid = userGuid,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(TicketLifetime),
        };
        await _db.Insertable(attachment).ExecuteCommandAsync(cancellationToken);

        var signature = _uploadService.GetDirectUploadSignature(
            pendingKey,
            normalizedType,
            SignatureLifetimeSeconds,
            headers
        );
        return ApiResponse<CashAttachmentUploadSignatureDto>.OK(
            new CashAttachmentUploadSignatureDto
            {
                AttachmentGuid = attachment.AttachmentGuid,
                Url = signature.Url,
                Headers = new Dictionary<string, string>(signature.Headers),
                ExpiresAtUtc = now.AddSeconds(SignatureLifetimeSeconds),
            }
        );
    }

    public async Task<ApiResponse<IReadOnlyList<StoreCashAttachment>>> PrepareAsync(
        string storeCode,
        string userGuid,
        IReadOnlyList<string> attachmentGuids,
        CancellationToken cancellationToken
    )
    {
        if (attachmentGuids.Count == 0)
        {
            return ApiResponse<IReadOnlyList<StoreCashAttachment>>.OK(Array.Empty<StoreCashAttachment>());
        }

        var distinct = attachmentGuids.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count != attachmentGuids.Count)
        {
            return Fail("同一张图片不能重复使用");
        }

        var rows = await _db.Queryable<StoreCashAttachment>()
            .Where(item => distinct.Contains(item.AttachmentGuid))
            .ToListAsync(cancellationToken);
        var byGuid = rows.ToDictionary(item => item.AttachmentGuid, StringComparer.Ordinal);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var guid in attachmentGuids)
        {
            if (!byGuid.TryGetValue(guid, out var row)
                || !string.Equals(row.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(row.UploadedByUserGuid, userGuid, StringComparison.Ordinal))
            {
                // 不存在、不是本店或不是本人上传的，一律按「无效图片」处理，不暴露它是否存在。
                return Fail("图片无效，请重新上传");
            }

            if (row.Status == StoreCashConstants.AttachmentStatus.Linked)
            {
                return Fail("图片已被其他记录使用，请重新上传");
            }

            if (row.Status == StoreCashConstants.AttachmentStatus.Pending && row.ExpiresAtUtc <= now)
            {
                return Fail("图片上传已过期，请重新上传");
            }
        }

        // 只对待确认的对象访问 COS（校验加转正）；已转正的（上次提交中途失败）直接复用。
        var pending = attachmentGuids
            .Select(guid => byGuid[guid])
            .Where(row => row.Status == StoreCashConstants.AttachmentStatus.Pending)
            .ToList();
        var outcomes = new Dictionary<string, string?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelCosOperations,
                CancellationToken = cancellationToken,
            },
            async (row, token) =>
            {
                var error = await ValidateAndPromoteAsync(row, userGuid, token);
                lock (outcomes)
                {
                    outcomes[row.AttachmentGuid] = error;
                }
            }
        );

        // 数据库状态流转放在 COS 并行段之外顺序执行：SqlSugar 客户端不是线程安全的。
        string? firstError = null;
        foreach (var row in pending)
        {
            if (outcomes.TryGetValue(row.AttachmentGuid, out var error) && error is not null)
            {
                firstError ??= error;
                continue;
            }

            var promoted = await _db.Updateable<StoreCashAttachment>()
                .SetColumns(item => new StoreCashAttachment
                {
                    Status = StoreCashConstants.AttachmentStatus.Promoted,
                })
                .Where(item => item.AttachmentGuid == row.AttachmentGuid
                    && item.Status == StoreCashConstants.AttachmentStatus.Pending)
                .ExecuteCommandAsync(cancellationToken);
            if (promoted != 1)
            {
                // 另一个并发提交抢先转正了同一张图：以数据库当前状态为准，让调用方重试。
                firstError ??= "图片状态已变更，请重试";
                continue;
            }

            row.Status = StoreCashConstants.AttachmentStatus.Promoted;
            _ = await _uploadService.DeleteObjectAsync(row.PendingObjectKey, CancellationToken.None);
        }

        if (firstError is not null)
        {
            return Fail(firstError);
        }

        return ApiResponse<IReadOnlyList<StoreCashAttachment>>.OK(
            attachmentGuids.Select(guid => byGuid[guid]).ToList()
        );

        static ApiResponse<IReadOnlyList<StoreCashAttachment>> Fail(string message) =>
            ApiResponse<IReadOnlyList<StoreCashAttachment>>.Error(
                message,
                StoreCashConstants.ErrorCodes.AttachmentInvalid
            );
    }

    /// <summary>校验待确认对象的实际属性与内容，通过后转正为私有正式对象。返回错误文案，成功返回 null。</summary>
    private async Task<string?> ValidateAndPromoteAsync(
        StoreCashAttachment row,
        string userGuid,
        CancellationToken cancellationToken
    )
    {
        var metadataResult = await _uploadService.GetObjectMetadataAsync(row.PendingObjectKey, cancellationToken);
        var metadata = metadataResult.Data;
        if (!metadataResult.Success || metadata is null)
        {
            return "图片还没有上传成功，请重新上传";
        }

        if (!StoreCashImageRules.MatchesMetadata(
                userGuid,
                row.FileSize,
                row.ContentType,
                metadata.ContentLength,
                metadata.ContentType?.Trim().ToLowerInvariant(),
                metadata.Owner,
                metadata.Kind,
                metadata.DeclaredFileSize,
                metadata.DeclaredContentType
            ))
        {
            return "图片实际属性与上传申请不一致，请重新上传";
        }

        var contentResult = await _uploadService.DownloadObjectBytesAsync(
            row.PendingObjectKey,
            (int)StoreCashImageRules.MaximumFileSize,
            cancellationToken
        );
        if (!contentResult.Success
            || contentResult.Data is null
            || contentResult.Data.LongLength != row.FileSize
            || !StoreCashImageRules.MatchesImageContent(contentResult.Data, row.ContentType))
        {
            return "上传的内容不是有效的图片，请重新上传";
        }

        var promoteResult = await _uploadService.PromoteObjectAsync(
            row.PendingObjectKey,
            row.FinalObjectKey,
            row.ContentType,
            isPublic: false,
            cancellationToken
        );
        if (!promoteResult.Success)
        {
            _logger.LogWarning("现金管理图片转正失败，AttachmentGuid: {AttachmentGuid}", row.AttachmentGuid);
            return "图片保存失败，请重试";
        }

        return null;
    }

    public async Task<Dictionary<string, List<CashAttachmentDto>>> GetLinkedAsync(
        IReadOnlyCollection<string> ownerGuids,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<string, List<CashAttachmentDto>>(StringComparer.Ordinal);
        if (ownerGuids.Count == 0)
        {
            return result;
        }

        var owners = ownerGuids.ToList();
        var rows = await _db.Queryable<StoreCashAttachment>()
            .Where(item => item.Status == StoreCashConstants.AttachmentStatus.Linked
                && owners.Contains(item.OwnerGuid!))
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (row.OwnerGuid is null)
            {
                continue;
            }

            var signed = _uploadService.GetSignedDownload(row.FinalObjectKey, DownloadUrlLifetimeSeconds);
            if (!result.TryGetValue(row.OwnerGuid, out var list))
            {
                list = new List<CashAttachmentDto>();
                result[row.OwnerGuid] = list;
            }

            list.Add(
                new CashAttachmentDto
                {
                    AttachmentGuid = row.AttachmentGuid,
                    Url = signed.Url,
                    UrlExpiresAtUtc = signed.ExpiresAtUtc,
                    ContentType = row.ContentType,
                    SortOrder = row.SortOrder,
                }
            );
        }

        return result;
    }

    public async Task<Dictionary<string, int>> CountLinkedAsync(
        IReadOnlyCollection<string> ownerGuids,
        CancellationToken cancellationToken
    )
    {
        if (ownerGuids.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var owners = ownerGuids.ToList();
        var owned = await _db.Queryable<StoreCashAttachment>()
            .Where(item => item.Status == StoreCashConstants.AttachmentStatus.Linked
                && owners.Contains(item.OwnerGuid!))
            .Select(item => item.OwnerGuid)
            .ToListAsync(cancellationToken);
        return owned
            .Where(owner => owner is not null)
            .GroupBy(owner => owner!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }
}
