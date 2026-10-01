using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public interface ICatalogFileSyncService
{
    bool IsEnabled { get; }

    Task<CatalogFileSyncPlanResponse?> GetFileSyncPlanAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken);

    CatalogDownloadFileInfo? FindFullFile(string storeCode, string catalogVersion);

    CatalogDownloadFileInfo? FindDeltaFile(string storeCode, string baseCatalogVersion, string targetCatalogVersion);
}

/// <summary>
/// 整文件同步计划：决策规则与分页 sync-plan 相同（无基准或基准不可用 → full，同版本 → noChange，
/// 差异不超过阈值 → delta），但不发放下载租约，基准版本从磁盘码号版本表读取而不是载入内存。
/// </summary>
public sealed class CatalogFileSyncService(
    ICatalogTargetIndexSource targetIndexSource,
    ICatalogDownloadFileStore fileStore,
    IOptions<CatalogDownloadFileOptions> fileOptions,
    IOptions<CatalogSyncOptions>? catalogSyncOptions = null,
    ICatalogIndexCache? catalogIndexCache = null) : ICatalogFileSyncService
{
    public bool IsEnabled => fileOptions.Value.Enabled;

    private bool IsDeltaEnabled => catalogSyncOptions?.Value.DeltaEnabled ?? true;

    public async Task<CatalogFileSyncPlanResponse?> GetFileSyncPlanAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken)
    {
        // 中文注释：先只看版本号；该版本的文件已发布时整个计划从磁盘得出，不为取版本号把整份索引（每店约 600 MB）载入内存。
        var peek = catalogIndexCache?.PeekLatestVersion(storeCode);
        if (peek is not null && fileStore.FindVersion(storeCode, peek.CatalogVersion) is { } published)
        {
            Log($"file plan source=disk store={published.StoreCode} version={published.CatalogVersion} stale={peek.IsStale}");
            return await PlanFromDiskAsync(published, baseCatalogVersion, cancellationToken);
        }

        Log($"file plan source=index store={storeCode} peek={(peek is null ? "none" : "unpublished")}");
        var target = await targetIndexSource.GetCurrentIndexAsync(storeCode, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var targetVersion = target.CatalogIndex.CatalogVersion;
        var targetTotal = target.CatalogIndex.Items.Count;
        var normalizedBase = string.IsNullOrWhiteSpace(baseCatalogVersion) ? null : baseCatalogVersion.Trim();
        if (normalizedBase is not null && string.Equals(normalizedBase, targetVersion, StringComparison.Ordinal))
        {
            return new CatalogFileSyncPlanResponse(
                target.StoreCode, target.GeneratedAt, CatalogSyncModes.NoChange,
                normalizedBase, targetVersion, targetTotal, File: null);
        }

        int? deltaOperationCount = null;
        if (normalizedBase is not null && IsDeltaEnabled)
        {
            var delta = await fileStore.EnsureDeltaFileAsync(target, normalizedBase, cancellationToken);
            if (delta is { Status: CatalogDeltaFileStatus.Ready, File: { } deltaFile })
            {
                return new CatalogFileSyncPlanResponse(
                    target.StoreCode, target.GeneratedAt, CatalogSyncModes.Delta,
                    normalizedBase, targetVersion, targetTotal,
                    ToDto(deltaFile, BuildDeltaPath(target.CatalogIndex.StoreCode, normalizedBase, targetVersion)),
                    delta.OperationCount);
            }

            // 差异过大时和分页协议一样把操作数带回，便于客户端日志说明为何改走全量。
            deltaOperationCount = delta.Status == CatalogDeltaFileStatus.TooLarge ? delta.OperationCount : null;
        }

        var fullFile = await fileStore.EnsureFullFileAsync(target, cancellationToken);
        return new CatalogFileSyncPlanResponse(
            target.StoreCode, target.GeneratedAt, CatalogSyncModes.Full,
            normalizedBase, targetVersion, targetTotal,
            ToDto(fullFile, BuildFullPath(target.CatalogIndex.StoreCode, targetVersion)),
            deltaOperationCount);
    }

    private async Task<CatalogFileSyncPlanResponse> PlanFromDiskAsync(
        CatalogDownloadVersionInfo target,
        string? baseCatalogVersion,
        CancellationToken cancellationToken)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(baseCatalogVersion) ? null : baseCatalogVersion.Trim();
        if (normalizedBase is not null && string.Equals(normalizedBase, target.CatalogVersion, StringComparison.Ordinal))
        {
            return new CatalogFileSyncPlanResponse(
                target.StoreCode, target.GeneratedAt, CatalogSyncModes.NoChange,
                normalizedBase, target.CatalogVersion, target.TotalCount, File: null);
        }

        int? deltaOperationCount = null;
        if (normalizedBase is not null && IsDeltaEnabled)
        {
            var delta = await fileStore.EnsureDeltaFileFromDiskAsync(
                target.StoreCode, normalizedBase, target.CatalogVersion, cancellationToken);
            if (delta is { Status: CatalogDeltaFileStatus.Ready, File: { } deltaFile })
            {
                return new CatalogFileSyncPlanResponse(
                    target.StoreCode, target.GeneratedAt, CatalogSyncModes.Delta,
                    normalizedBase, target.CatalogVersion, target.TotalCount,
                    ToDto(deltaFile, BuildDeltaPath(target.StoreCode, normalizedBase, target.CatalogVersion)),
                    delta.OperationCount);
            }

            deltaOperationCount = delta.Status == CatalogDeltaFileStatus.TooLarge ? delta.OperationCount : null;
        }

        return new CatalogFileSyncPlanResponse(
            target.StoreCode, target.GeneratedAt, CatalogSyncModes.Full,
            normalizedBase, target.CatalogVersion, target.TotalCount,
            ToDto(target.FullFile, BuildFullPath(target.StoreCode, target.CatalogVersion)),
            deltaOperationCount);
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][CatalogFiles] {DateTimeOffset.Now:O} {message}");
    }

    public CatalogDownloadFileInfo? FindFullFile(string storeCode, string catalogVersion) =>
        fileStore.FindFullFile(storeCode, catalogVersion);

    public CatalogDownloadFileInfo? FindDeltaFile(string storeCode, string baseCatalogVersion, string targetCatalogVersion) =>
        fileStore.FindDeltaFile(storeCode, baseCatalogVersion, targetCatalogVersion);

    internal static string BuildFullPath(string storeCode, string catalogVersion) =>
        $"api/v1/catalog/files/full?storeCode={Uri.EscapeDataString(storeCode)}&catalogVersion={Uri.EscapeDataString(catalogVersion)}";

    internal static string BuildDeltaPath(string storeCode, string baseCatalogVersion, string targetCatalogVersion) =>
        $"api/v1/catalog/files/delta?storeCode={Uri.EscapeDataString(storeCode)}" +
        $"&baseCatalogVersion={Uri.EscapeDataString(baseCatalogVersion)}" +
        $"&targetCatalogVersion={Uri.EscapeDataString(targetCatalogVersion)}";

    private static CatalogDownloadFileDto ToDto(CatalogDownloadFileInfo file, string path) => new(
        file.Kind,
        file.Kind == CatalogFileKinds.Delta ? CatalogFileFormats.DeltaV1 : CatalogFileFormats.FullV1,
        path,
        file.Bytes,
        file.Sha256);
}
