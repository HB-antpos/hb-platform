using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Services;

/// <summary>
/// 整文件目录下载的磁盘配置。默认关闭；开启后 sync-plan 文件模式按版本把目录写成不可变 gzip 文件，
/// 下载直接由文件流出，不再把目录版本钉在托管堆里。
/// </summary>
public sealed class CatalogDownloadFileOptions
{
    public const string SectionName = "CatalogDownloadFiles";

    public bool Enabled { get; set; }

    /// <summary>为空时放在目录快照根目录下的 download-files 子目录（同一持久卷）。</summary>
    public string? RootPath { get; set; }

    /// <summary>每门店至少保留的最新版本数。</summary>
    public int RetainVersionsPerStore { get; set; } = 3;

    /// <summary>版本被新版本取代后继续保留的小时数，保证进行中的下载与增量基准仍然可用。</summary>
    public int SupersededRetentionHours { get; set; } = 24;

    /// <summary>增量操作数超过该值时改发全量文件，与分页协议的阈值一致。</summary>
    public int MaxDeltaOperations { get; set; } = 5_000;

    /// <summary>等待全局文件生成槽位的最长秒数；超时视为暂不可用，由客户端回退分页协议。</summary>
    public int GenerationWaitSeconds { get; set; } = 30;
}

public sealed record CatalogDownloadFileInfo(
    string Kind,
    string FilePath,
    long Bytes,
    string Sha256,
    DateTimeOffset CreatedAt,
    int? OperationCount = null);

public enum CatalogDeltaFileStatus
{
    /// <summary>基准版本的码号版本表不在磁盘上（从未生成或已清理），只能全量。</summary>
    BaselineUnavailable,
    /// <summary>差异超过阈值，全量更划算。</summary>
    TooLarge,
    Ready
}

public sealed record CatalogDeltaFileResult(
    CatalogDeltaFileStatus Status,
    int OperationCount,
    CatalogDownloadFileInfo? File);

/// <summary>文件生成槽位等待超时或磁盘写入失败；调用方返回 503 让客户端回退分页协议。</summary>
public sealed class CatalogDownloadFileUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public interface ICatalogDownloadFileStore
{
    Task<CatalogDownloadFileInfo> EnsureFullFileAsync(
        CatalogIndexBuildResult target,
        CancellationToken cancellationToken);

    Task<CatalogDeltaFileResult> EnsureDeltaFileAsync(
        CatalogIndexBuildResult target,
        string baseCatalogVersion,
        CancellationToken cancellationToken);

    CatalogDownloadFileInfo? FindFullFile(string storeCode, string catalogVersion);

    CatalogDownloadFileInfo? FindDeltaFile(string storeCode, string baseCatalogVersion, string targetCatalogVersion);

    /// <summary>已发布版本的身份与全量文件；未发布或文件不完整时返回 null。</summary>
    CatalogDownloadVersionInfo? FindVersion(string storeCode, string catalogVersion);

    /// <summary>已发布版本的码冲突候选（与接口响应同形）；版本未发布或尚无该文件时返回 null。</summary>
    CatalogCodeConflictsResponse? FindCodeConflicts(string storeCode, string catalogVersion);

    /// <summary>版本已发布但还没有码冲突文件时补写（旧版本自愈）；版本未发布时不做任何事。</summary>
    void EnsureCodeConflictsFile(CatalogIndexBuildResult target);

    /// <summary>
    /// 只用磁盘上两个版本的码号版本表与目标全量文件计算增量，不需要任何一个版本的内存索引；
    /// 目标版本必须已发布（见 <see cref="FindVersion"/>）。
    /// </summary>
    Task<CatalogDeltaFileResult> EnsureDeltaFileFromDiskAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        CancellationToken cancellationToken);
}

/// <summary>磁盘上已发布目录版本的身份；StoreCode/GeneratedAt 与生成该版本的内存索引一致。</summary>
public sealed record CatalogDownloadVersionInfo(
    string StoreCode,
    string CatalogVersion,
    DateTimeOffset GeneratedAt,
    int TotalCount,
    CatalogDownloadFileInfo FullFile);

/// <summary>
/// 目录下载文件的落盘布局（均在 RootPath 下，目录名取 SHA-256 以免版本号里的字符进入路径）：
/// <code>
/// {H(门店)}/{H(版本)}/full.ndjson.gz          全量：头 + 每行一条 CatalogLookupItemDto
/// {H(门店)}/{H(版本)}/rowversions.ndjson.gz   码号版本表：每行 [规范码, 原码, RowVersion]，作为后续增量的基准
/// {H(门店)}/{H(版本)}/version.json            发布标记，最后写入；没有它的目录一律视为未发布
/// {H(门店)}/{H(版本)}/delta/{H(基准版本)}.ndjson.gz + .json
/// </code>
/// 版本目录先在同门店下的临时目录写完再整体改名发布，发布后内容不再改写，可安全并发读取与断点续传。
/// </summary>
public sealed class CatalogDownloadFileStore : ICatalogDownloadFileStore
{
    private const string FullFileName = "full.ndjson.gz";
    private const string RowVersionFileName = "rowversions.ndjson.gz";
    private const string VersionManifestName = "version.json";
    private const string CodeConflictsFileName = "code-conflicts.json.gz";
    private const string DeltaDirectoryName = "delta";
    private const string TempDirectoryPrefix = ".tmp-";
    private static readonly TimeSpan StaleTempDirectoryAge = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly string _rootPath;
    private readonly CatalogDownloadFileOptions _options;
    private readonly TimeProvider _timeProvider;
    // 生成会把 35 万条目录压缩成 gzip，CPU 密集；全局串行，避免多店同时生成拖慢收银接口。
    private readonly SemaphoreSlim _generationGate = new(1, 1);

    public CatalogDownloadFileStore(
        string rootPath,
        CatalogDownloadFileOptions options,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<CatalogDownloadFileInfo> EnsureFullFileAsync(
        CatalogIndexBuildResult target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var index = target.CatalogIndex;
        var existing = FindFullFile(index.StoreCode, index.CatalogVersion);
        if (existing is not null)
        {
            return existing;
        }

        await EnterGenerationGateAsync(cancellationToken);
        try
        {
            // 等待槽位期间可能已由别的请求生成。
            existing = FindFullFile(index.StoreCode, index.CatalogVersion);
            if (existing is not null)
            {
                return existing;
            }

            return await Task.Run(() => PublishVersion(target), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CatalogDownloadFileUnavailableException("Catalog download file could not be written.", exception);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public async Task<CatalogDeltaFileResult> EnsureDeltaFileAsync(
        CatalogIndexBuildResult target,
        string baseCatalogVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCatalogVersion);
        var index = target.CatalogIndex;
        var normalizedBase = baseCatalogVersion.Trim();
        var existing = FindDeltaFile(index.StoreCode, normalizedBase, index.CatalogVersion);
        if (existing is not null)
        {
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.Ready, existing.OperationCount ?? 0, existing);
        }

        var baselineManifest = ReadVersionManifest(GetVersionDirectory(index.StoreCode, normalizedBase));
        if (baselineManifest is null)
        {
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.BaselineUnavailable, 0, null);
        }

        await EnterGenerationGateAsync(cancellationToken);
        try
        {
            existing = FindDeltaFile(index.StoreCode, normalizedBase, index.CatalogVersion);
            if (existing is not null)
            {
                return new CatalogDeltaFileResult(CatalogDeltaFileStatus.Ready, existing.OperationCount ?? 0, existing);
            }

            // 增量文件挂在目标版本目录下，目标版本必须先发布。
            if (FindFullFile(index.StoreCode, index.CatalogVersion) is null)
            {
                await Task.Run(() => PublishVersion(target), cancellationToken);
            }

            return await Task.Run(() => PublishDelta(index, normalizedBase), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CatalogDownloadFileUnavailableException("Catalog delta file could not be written.", exception);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public CatalogDownloadFileInfo? FindFullFile(string storeCode, string catalogVersion)
    {
        var versionDirectory = GetVersionDirectory(storeCode, catalogVersion);
        var manifest = ReadVersionManifest(versionDirectory);
        if (manifest is null ||
            !string.Equals(manifest.CatalogVersion, catalogVersion.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(versionDirectory, FullFileName);
        return IsPublishedFile(path, manifest.FullBytes)
            ? new CatalogDownloadFileInfo(CatalogFileKinds.Full, path, manifest.FullBytes, manifest.FullSha256, manifest.CreatedAt)
            : null;
    }

    public CatalogDownloadFileInfo? FindDeltaFile(string storeCode, string baseCatalogVersion, string targetCatalogVersion)
    {
        var deltaDirectory = Path.Combine(GetVersionDirectory(storeCode, targetCatalogVersion), DeltaDirectoryName);
        var name = HashName(baseCatalogVersion.Trim());
        var manifestPath = Path.Combine(deltaDirectory, name + ".json");
        var manifest = TryReadJson<DeltaManifest>(manifestPath);
        if (manifest is null ||
            !string.Equals(manifest.BaseCatalogVersion, baseCatalogVersion.Trim(), StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetCatalogVersion, targetCatalogVersion.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(deltaDirectory, name + ".ndjson.gz");
        return IsPublishedFile(path, manifest.Bytes)
            ? new CatalogDownloadFileInfo(CatalogFileKinds.Delta, path, manifest.Bytes, manifest.Sha256, manifest.CreatedAt, manifest.OperationCount)
            : null;
    }

    public CatalogDownloadVersionInfo? FindVersion(string storeCode, string catalogVersion)
    {
        var manifest = ReadVersionManifest(GetVersionDirectory(storeCode, catalogVersion));
        var full = FindFullFile(storeCode, catalogVersion);
        return manifest is null || full is null
            ? null
            : new CatalogDownloadVersionInfo(manifest.StoreCode, manifest.CatalogVersion, manifest.GeneratedAt, manifest.TotalCount, full);
    }

    public CatalogCodeConflictsResponse? FindCodeConflicts(string storeCode, string catalogVersion)
    {
        var versionDirectory = GetVersionDirectory(storeCode, catalogVersion);
        var manifest = ReadVersionManifest(versionDirectory);
        if (manifest is null || !string.Equals(manifest.CatalogVersion, catalogVersion.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(versionDirectory, CodeConflictsFileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var input = File.OpenRead(path);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<CatalogCodeConflictsResponse>(gzip, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // 文件损坏时按“没有”处理，调用方回到索引路径重新计算。
            Log($"code conflicts file unreadable store={storeCode} version={catalogVersion} error={exception.GetType().Name}");
            return null;
        }
    }

    public void EnsureCodeConflictsFile(CatalogIndexBuildResult target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var index = target.CatalogIndex;
        var versionDirectory = GetVersionDirectory(index.StoreCode, index.CatalogVersion);
        var path = Path.Combine(versionDirectory, CodeConflictsFileName);
        if (ReadVersionManifest(versionDirectory) is null || File.Exists(path))
        {
            return;
        }

        try
        {
            WriteCodeConflictsFile(path, CreateCodeConflictsResponse(target));
            Log($"code conflicts file backfilled store={index.StoreCode} version={index.CatalogVersion}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 补写失败不影响本次响应，下次请求仍走索引路径。
            Log($"code conflicts backfill skipped store={index.StoreCode} error={exception.GetType().Name}");
        }
    }

    /// <summary>码冲突接口的响应内容：磁盘与索引两条路径共用，保证逐字段一致。</summary>
    internal static CatalogCodeConflictsResponse CreateCodeConflictsResponse(CatalogIndexBuildResult index)
    {
        var codeConflicts = index.CodeConflicts;
        return new CatalogCodeConflictsResponse(
            index.StoreCode,
            index.GeneratedAt,
            codeConflicts is not null,
            codeConflicts is null
                ? []
                : codeConflicts.Select(CatalogSellableIndex.ToLookupItem).ToArray());
    }

    private static void WriteCodeConflictsFile(string path, CatalogCodeConflictsResponse response)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                JsonSerializer.Serialize(gzip, response, JsonOptions);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public async Task<CatalogDeltaFileResult> EnsureDeltaFileFromDiskAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCatalogVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCatalogVersion);
        var normalizedBase = baseCatalogVersion.Trim();
        var normalizedTarget = targetCatalogVersion.Trim();
        var existing = FindDeltaFile(storeCode, normalizedBase, normalizedTarget);
        if (existing is not null)
        {
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.Ready, existing.OperationCount ?? 0, existing);
        }

        if (ReadVersionManifest(GetVersionDirectory(storeCode, normalizedBase)) is null)
        {
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.BaselineUnavailable, 0, null);
        }

        await EnterGenerationGateAsync(cancellationToken);
        try
        {
            existing = FindDeltaFile(storeCode, normalizedBase, normalizedTarget);
            if (existing is not null)
            {
                return new CatalogDeltaFileResult(CatalogDeltaFileStatus.Ready, existing.OperationCount ?? 0, existing);
            }

            var target = ReadVersionManifest(GetVersionDirectory(storeCode, normalizedTarget))
                ?? throw new CatalogDownloadFileUnavailableException("Target catalog version files are not published.");
            return await Task.Run(() => PublishDeltaFromDisk(target, normalizedBase), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CatalogDownloadFileUnavailableException("Catalog delta file could not be written.", exception);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    private async Task EnterGenerationGateAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.GenerationWaitSeconds));
        if (!await _generationGate.WaitAsync(timeout, cancellationToken))
        {
            throw new CatalogDownloadFileUnavailableException("Catalog download file generation is busy.");
        }
    }

    private CatalogDownloadFileInfo PublishVersion(CatalogIndexBuildResult target)
    {
        var index = target.CatalogIndex;
        var storeDirectory = GetStoreDirectory(index.StoreCode);
        var versionDirectory = GetVersionDirectory(index.StoreCode, index.CatalogVersion);
        Directory.CreateDirectory(storeDirectory);
        var tempDirectory = Path.Combine(storeDirectory, TempDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var header = new CatalogFullFileHeader(
                CatalogFileFormats.FullV1,
                index.StoreCode,
                index.CatalogVersion,
                index.GeneratedAt,
                index.Items.Count);
            // Items 已在索引构建时按规范码去重并按序号排序，文件行序与分页协议的游标顺序一致。
            var (bytes, sha256) = WriteNdjsonGzip(
                Path.Combine(tempDirectory, FullFileName),
                writeLine => WriteFullLines(writeLine, header, index.Items));
            WriteNdjsonGzip(
                Path.Combine(tempDirectory, RowVersionFileName),
                writeLine => WriteRowVersionLines(writeLine, index.Items));
            WriteCodeConflictsFile(Path.Combine(tempDirectory, CodeConflictsFileName), CreateCodeConflictsResponse(target));
            WriteJsonFile(Path.Combine(tempDirectory, VersionManifestName), new VersionManifest(
                index.StoreCode,
                index.CatalogVersion,
                index.GeneratedAt,
                index.Items.Count,
                bytes,
                sha256,
                now));

            if (Directory.Exists(versionDirectory))
            {
                // 未带发布标记的残留目录（例如进程在改名前被杀）可以安全替换；已发布的目录绝不覆盖。
                if (ReadVersionManifest(versionDirectory) is not null)
                {
                    TryDeleteDirectory(tempDirectory);
                    return FindFullFile(index.StoreCode, index.CatalogVersion)
                        ?? throw new IOException("Published catalog version directory is incomplete.");
                }

                Directory.Delete(versionDirectory, recursive: true);
            }

            Directory.Move(tempDirectory, versionDirectory);
            Log($"full file published store={index.StoreCode} version={index.CatalogVersion} items={index.Items.Count} bytes={bytes}");
            CleanupStore(index.StoreCode, storeDirectory, now);
            return FindFullFile(index.StoreCode, index.CatalogVersion)
                ?? throw new IOException("Published catalog version directory could not be read back.");
        }
        catch
        {
            TryDeleteDirectory(tempDirectory);
            throw;
        }
    }

    private CatalogDeltaFileResult PublishDelta(CatalogSellableIndex target, string baseCatalogVersion)
    {
        var baselinePath = Path.Combine(GetVersionDirectory(target.StoreCode, baseCatalogVersion), RowVersionFileName);
        List<CatalogDeltaFileLine> operations;
        int operationCount;
        try
        {
            (operations, operationCount) = ComputeDeltaOperations(target, baselinePath, _options.MaxDeltaOperations);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or JsonException)
        {
            // 基准在读取期间被清理或内容损坏时安全退回全量，绝不猜测删除项。
            Log($"delta baseline unavailable store={target.StoreCode} base={baseCatalogVersion} error={exception.GetType().Name}");
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.BaselineUnavailable, 0, null);
        }

        if (operationCount > _options.MaxDeltaOperations)
        {
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.TooLarge, operationCount, null);
        }

        return WriteDeltaFile(
            target.StoreCode,
            baseCatalogVersion,
            target.CatalogVersion,
            target.GeneratedAt,
            target.Items.Count,
            operations,
            operationCount);
    }

    /// <summary>
    /// 磁盘版增量：两侧都顺序读码号版本表归并出操作（语义同 <see cref="ComputeDeltaOperations"/>），
    /// upsert 行再按行号从目标全量文件中取出原样反序列化；内存只占不超过阈值的操作。
    /// </summary>
    private CatalogDeltaFileResult PublishDeltaFromDisk(VersionManifest target, string baseCatalogVersion)
    {
        var storeCode = target.StoreCode;
        var baselinePath = Path.Combine(GetVersionDirectory(storeCode, baseCatalogVersion), RowVersionFileName);
        var targetDirectory = GetVersionDirectory(storeCode, target.CatalogVersion);
        List<CatalogDeltaFileLine> operations;
        int operationCount;
        try
        {
            (operations, operationCount) = ComputeDeltaOperationsFromDisk(
                storeCode,
                target.GeneratedAt,
                baselinePath,
                Path.Combine(targetDirectory, RowVersionFileName),
                Path.Combine(targetDirectory, FullFileName),
                _options.MaxDeltaOperations);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or JsonException)
        {
            Log($"delta baseline unavailable store={storeCode} base={baseCatalogVersion} source=disk error={exception.GetType().Name}");
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.BaselineUnavailable, 0, null);
        }

        if (operationCount > _options.MaxDeltaOperations)
        {
            return new CatalogDeltaFileResult(CatalogDeltaFileStatus.TooLarge, operationCount, null);
        }

        return WriteDeltaFile(
            storeCode,
            baseCatalogVersion,
            target.CatalogVersion,
            target.GeneratedAt,
            target.TotalCount,
            operations,
            operationCount);
    }

    private CatalogDeltaFileResult WriteDeltaFile(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        DateTimeOffset generatedAt,
        int targetTotal,
        IReadOnlyList<CatalogDeltaFileLine> operations,
        int operationCount)
    {
        var deltaDirectory = Path.Combine(GetVersionDirectory(storeCode, targetCatalogVersion), DeltaDirectoryName);
        Directory.CreateDirectory(deltaDirectory);
        var name = HashName(baseCatalogVersion);
        var finalPath = Path.Combine(deltaDirectory, name + ".ndjson.gz");
        var tempPath = Path.Combine(deltaDirectory, $"{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            var header = new CatalogDeltaFileHeader(
                CatalogFileFormats.DeltaV1,
                storeCode,
                baseCatalogVersion,
                targetCatalogVersion,
                generatedAt,
                targetTotal,
                operationCount);
            var (bytes, sha256) = WriteNdjsonGzip(tempPath, writeLine =>
            {
                writeLine(header);
                foreach (var operation in operations)
                {
                    writeLine(operation);
                }
            });
            File.Move(tempPath, finalPath, overwrite: true);
            // manifest 最后写入，作为增量文件的发布点。
            WriteJsonFile(Path.Combine(deltaDirectory, name + ".json"), new DeltaManifest(
                baseCatalogVersion,
                targetCatalogVersion,
                operationCount,
                bytes,
                sha256,
                _timeProvider.GetUtcNow()));
            Log($"delta file published store={storeCode} base={baseCatalogVersion} target={targetCatalogVersion} operations={operationCount} bytes={bytes}");
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        var published = FindDeltaFile(storeCode, baseCatalogVersion, targetCatalogVersion)
            ?? throw new IOException("Published catalog delta file could not be read back.");
        return new CatalogDeltaFileResult(CatalogDeltaFileStatus.Ready, operationCount, published);
    }

    /// <summary>
    /// 与 <see cref="CatalogSellableIndex.GetDeltaOperations"/> 语义一致的归并：基准侧从磁盘顺序读码号版本表，
    /// 目标侧是内存中已排序的 Items；只保留不超过阈值的操作，超出部分只计数，内存与目录大小无关。
    /// </summary>
    internal static (List<CatalogDeltaFileLine> Operations, int OperationCount) ComputeDeltaOperations(
        CatalogSellableIndex target,
        string baselineRowVersionPath,
        int maxOperations)
    {
        var operations = new List<CatalogDeltaFileLine>();
        var operationCount = 0;
        void Add(CatalogDeltaFileLine line)
        {
            operationCount++;
            if (operationCount <= maxOperations)
            {
                operations.Add(line);
            }
        }

        CatalogDeltaFileLine Delete(RowVersionEntry baseline) => new(
            null,
            new DeletedLookupDto(target.StoreCode, baseline.LookupCode, baseline.LookupCodeNormalized, target.GeneratedAt));

        var items = target.Items;
        var targetPosition = 0;
        string? previousBaselineCode = null;
        foreach (var baseline in ReadRowVersionEntries(baselineRowVersionPath))
        {
            if (previousBaselineCode is not null &&
                string.Compare(previousBaselineCode, baseline.LookupCodeNormalized, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidDataException("Baseline row versions are not strictly ordered.");
            }

            previousBaselineCode = baseline.LookupCodeNormalized;
            while (targetPosition < items.Count &&
                   string.Compare(items[targetPosition].LookupCodeNormalized, baseline.LookupCodeNormalized, StringComparison.Ordinal) < 0)
            {
                Add(new CatalogDeltaFileLine(items[targetPosition++], null));
            }

            if (targetPosition < items.Count &&
                string.Equals(items[targetPosition].LookupCodeNormalized, baseline.LookupCodeNormalized, StringComparison.Ordinal))
            {
                var item = items[targetPosition++];
                if (!string.Equals(item.RowVersion, baseline.RowVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Add(new CatalogDeltaFileLine(item, null));
                }
            }
            else
            {
                Add(Delete(baseline));
            }
        }

        while (targetPosition < items.Count)
        {
            Add(new CatalogDeltaFileLine(items[targetPosition++], null));
        }

        return (operations, operationCount);
    }

    internal static (List<CatalogDeltaFileLine> Operations, int OperationCount) ComputeDeltaOperationsFromDisk(
        string storeCode,
        DateTimeOffset targetGeneratedAt,
        string baselineRowVersionPath,
        string targetRowVersionPath,
        string targetFullFilePath,
        int maxOperations)
    {
        // 第一遍：两份码号版本表归并，只记操作的类型与目标行号（upsert）或基准码（delete）。
        var plan = new List<(string Code, int TargetOrdinal, RowVersionEntry? Deleted)>();
        var operationCount = 0;
        void Add(string code, int targetOrdinal, RowVersionEntry? deleted)
        {
            operationCount++;
            if (operationCount <= maxOperations)
            {
                plan.Add((code, targetOrdinal, deleted));
            }
        }

        using (var baseline = OrderedEntries(baselineRowVersionPath).GetEnumerator())
        using (var target = OrderedEntries(targetRowVersionPath).GetEnumerator())
        {
            var hasBaseline = baseline.MoveNext();
            var hasTarget = target.MoveNext();
            var targetOrdinal = 0;
            while (hasBaseline || hasTarget)
            {
                var comparison = !hasBaseline ? 1
                    : !hasTarget ? -1
                    : string.Compare(baseline.Current.LookupCodeNormalized, target.Current.LookupCodeNormalized, StringComparison.Ordinal);
                if (comparison < 0)
                {
                    Add(baseline.Current.LookupCodeNormalized, -1, baseline.Current);
                    hasBaseline = baseline.MoveNext();
                }
                else if (comparison > 0)
                {
                    Add(target.Current.LookupCodeNormalized, targetOrdinal, null);
                    hasTarget = target.MoveNext();
                    targetOrdinal++;
                }
                else
                {
                    if (!string.Equals(baseline.Current.RowVersion, target.Current.RowVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(target.Current.LookupCodeNormalized, targetOrdinal, null);
                    }

                    hasBaseline = baseline.MoveNext();
                    hasTarget = target.MoveNext();
                    targetOrdinal++;
                }
            }
        }

        if (operationCount > maxOperations)
        {
            return ([], operationCount);
        }

        // 第二遍：顺序读目标全量文件，只反序列化被选中的行（全量文件第 1 行是头，之后第 i 行对应码号表第 i 行）。
        var wanted = plan.Where(entry => entry.Deleted is null).Select(entry => entry.TargetOrdinal).ToHashSet();
        var items = new Dictionary<int, CatalogLookupItemDto>(wanted.Count);
        using (var input = File.OpenRead(targetFullFilePath))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip, Encoding.UTF8))
        {
            _ = reader.ReadLine() ?? throw new InvalidDataException("Target full file is empty.");
            var ordinal = 0;
            while (items.Count < wanted.Count && reader.ReadLine() is { } line)
            {
                if (wanted.Contains(ordinal))
                {
                    items[ordinal] = JsonSerializer.Deserialize<CatalogLookupItemDto>(line, JsonOptions)
                        ?? throw new InvalidDataException("Target full file contains an empty row.");
                }

                ordinal++;
            }
        }

        if (items.Count != wanted.Count)
        {
            throw new InvalidDataException("Target full file does not match its row version table.");
        }

        var operations = plan
            .Select(entry => entry.Deleted is { } deleted
                ? new CatalogDeltaFileLine(null, new DeletedLookupDto(storeCode, deleted.LookupCode, deleted.LookupCodeNormalized, targetGeneratedAt))
                : new CatalogDeltaFileLine(items[entry.TargetOrdinal], null))
            .ToList();
        return (operations, operationCount);
    }

    /// <summary>码号版本表必须严格按序号递增，否则归并结果不可信，按基准不可用处理。</summary>
    private static IEnumerable<RowVersionEntry> OrderedEntries(string path)
    {
        string? previous = null;
        foreach (var entry in ReadRowVersionEntries(path))
        {
            if (previous is not null && string.Compare(previous, entry.LookupCodeNormalized, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidDataException("Row versions are not strictly ordered.");
            }

            previous = entry.LookupCodeNormalized;
            yield return entry;
        }
    }

    private static void WriteFullLines(
        Action<object> writeLine,
        CatalogFullFileHeader header,
        IReadOnlyList<CatalogLookupItemDto> items)
    {
        writeLine(header);
        foreach (var item in items)
        {
            writeLine(item);
        }
    }

    private static void WriteRowVersionLines(Action<object> writeLine, IReadOnlyList<CatalogLookupItemDto> items)
    {
        foreach (var item in items)
        {
            writeLine(new[] { item.LookupCodeNormalized, item.LookupCode, item.RowVersion ?? string.Empty });
        }
    }

    private static IEnumerable<RowVersionEntry> ReadRowVersionEntries(string path)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var values = JsonSerializer.Deserialize<string[]>(line, JsonOptions);
            if (values is not { Length: 3 })
            {
                throw new InvalidDataException("Row version line must contain three values.");
            }

            yield return new RowVersionEntry(values[0], values[1], values[2]);
        }
    }

    /// <summary>逐行序列化后直接流入 gzip 与 SHA-256，整份内容不经过内存缓冲。</summary>
    private static (long Bytes, string Sha256) WriteNdjsonGzip(string path, Action<Action<object>> writeLines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes;
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            using (var hashing = new HashingWriteStream(output, hash))
            using (var gzip = new GZipStream(hashing, CompressionLevel.Optimal, leaveOpen: true))
            using (var writer = new Utf8JsonWriter(gzip))
            {
                writeLines(value =>
                {
                    // 每行一个独立 JSON 值：先把 writer 缓冲刷进 gzip 再写换行，Reset 后开始下一行。
                    JsonSerializer.Serialize(writer, value, value.GetType(), JsonOptions);
                    writer.Flush();
                    gzip.Write(NewLine);
                    writer.Reset();
                });
            }

            output.Flush(flushToDisk: true);
            bytes = output.Length;
        }

        return (bytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void WriteJsonFile<T>(string path, T value)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static T? TryReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static VersionManifest? ReadVersionManifest(string versionDirectory) =>
        TryReadJson<VersionManifest>(Path.Combine(versionDirectory, VersionManifestName));

    private static bool IsPublishedFile(string path, long expectedBytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == expectedBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 保留每店最新 N 个版本；更旧的版本在被取代后再保留 SupersededRetentionHours，
    /// 以免正在续传的客户端或仍以它为基准的增量突然失效。
    /// </summary>
    private void CleanupStore(string storeCode, string storeDirectory, DateTimeOffset now)
    {
        try
        {
            var retainCount = Math.Max(1, _options.RetainVersionsPerStore);
            var supersededRetention = TimeSpan.FromHours(Math.Max(0, _options.SupersededRetentionHours));
            var versions = new List<(string Directory, DateTimeOffset CreatedAt)>();
            foreach (var directory in Directory.EnumerateDirectories(storeDirectory))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(TempDirectoryPrefix, StringComparison.Ordinal))
                {
                    if (now - Directory.GetCreationTimeUtc(directory) > StaleTempDirectoryAge)
                    {
                        TryDeleteDirectory(directory);
                    }

                    continue;
                }

                var manifest = ReadVersionManifest(directory);
                if (manifest is not null)
                {
                    versions.Add((directory, manifest.CreatedAt));
                }
            }

            var ordered = versions.OrderByDescending(version => version.CreatedAt).ToArray();
            for (var position = retainCount; position < ordered.Length; position++)
            {
                var supersededAt = ordered[position - 1].CreatedAt;
                if (now - supersededAt >= supersededRetention)
                {
                    TryDeleteDirectory(ordered[position].Directory);
                    Log($"version files removed store={storeCode} createdAt={ordered[position].CreatedAt:O}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响本次已经发布成功的文件。
            Log($"cleanup skipped store={storeCode} error={exception.GetType().Name}");
        }
    }

    private string GetStoreDirectory(string storeCode) =>
        Path.Combine(_rootPath, HashName(storeCode.Trim().ToUpperInvariant()));

    private string GetVersionDirectory(string storeCode, string catalogVersion) =>
        Path.Combine(GetStoreDirectory(storeCode), HashName(catalogVersion.Trim()));

    private static string HashName(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"delete directory failed error={exception.GetType().Name}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"delete file failed error={exception.GetType().Name}");
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][CatalogDownloadFiles] {DateTimeOffset.Now:O} {message}");
    }

    private sealed record RowVersionEntry(string LookupCodeNormalized, string LookupCode, string RowVersion);

    private sealed record VersionManifest(
        string StoreCode,
        string CatalogVersion,
        DateTimeOffset GeneratedAt,
        int TotalCount,
        long FullBytes,
        string FullSha256,
        DateTimeOffset CreatedAt);

    private sealed record DeltaManifest(
        string BaseCatalogVersion,
        string TargetCatalogVersion,
        int OperationCount,
        long Bytes,
        string Sha256,
        DateTimeOffset CreatedAt);

    private sealed class HashingWriteStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            inner.Write(buffer);
        }
    }
}
