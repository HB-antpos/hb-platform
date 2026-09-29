using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Services;

/// <summary>
/// 目录快照的磁盘配置。第一版仅使用 BCL gzip，保留 codec 字段便于后续无缝加入 zstd。
/// </summary>
public sealed class CatalogSnapshotOptions
{
    public string? RootPath { get; set; }

    /// <summary>每门店保留的目录版本上限（先达到者淘汰，始终保留最新 LKG）。</summary>
    public int MaxSnapshotsPerStore { get; set; } = 8;

    /// <summary>目录快照保留时长（小时）；超过保留期且非最新版本的驻留快照先淘汰。</summary>
    public int RetentionHours { get; set; } = 72;

    /// <summary>驻留目录条目软上限；超过后先淘汰无租约的旧版本。</summary>
    public int SoftItemCapacity { get; set; } = 1_500_000;

    /// <summary>含租约与待发布目录在内的硬上限。</summary>
    public int HardItemCapacity { get; set; } = 2_500_000;

    /// <summary>用于旧版增量查询的原始候选工件驻留版本数。</summary>
    public int RawArtifactCapacity { get; set; } = 2;

    /// <summary>等待全局目录构建槽位的最长秒数；超时返回容量繁忙。</summary>
    public int BuildGateWaitSeconds { get; set; } = 30;
}

public sealed record CatalogPersistedSnapshot(
    string StoreCode,
    DateTimeOffset? Since,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    string CatalogVersion,
    IReadOnlyList<SellableItemDto> SellableItems,
    // 新增的可选字段：旧版本写入的快照没有该属性，反序列化为 null，表示冲突未计算。
    IReadOnlyList<SellableItemDto>? CodeConflicts = null);

public sealed record CatalogSnapshotDescriptor(
    string StoreCode,
    DateTimeOffset? Since,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    string CatalogVersion);

public interface ICatalogSnapshotStore
{
    IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now);

    CatalogPersistedSnapshot? Load(
        string storeCode,
        DateTimeOffset? since,
        string catalogVersion);

    IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now);

    void Save(CatalogPersistedSnapshot snapshot);

    void RefreshExpiration(
        string storeCode,
        DateTimeOffset? since,
        string catalogVersion,
        DateTimeOffset expiresAt);
}

/// <summary>
/// 单容器部署下的本地版本化目录快照仓库。
/// manifest 和 gzip 正文均以临时文件落盘后原子替换，读取时同时核对 SHA-256；
/// 因此异常写入或单个损坏版本只会降级为重新构建，不会污染已发布版本。
/// </summary>
public sealed class GzipCatalogSnapshotStore : ICatalogSnapshotStore
{
    private const string ManifestFileName = "manifest.json";
    private const string Codec = "gzip";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _rootPath;
    private readonly int _maxSnapshotsPerStore;
    private readonly Func<bool>? _manifestWriteFailure;
    private readonly object _gate = new();

    public GzipCatalogSnapshotStore(
        string rootPath,
        int maxSnapshotsPerStore = 3,
        Func<bool>? manifestWriteFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (maxSnapshotsPerStore <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotsPerStore));
        }

        _rootPath = Path.GetFullPath(rootPath);
        _maxSnapshotsPerStore = maxSnapshotsPerStore;
        _manifestWriteFailure = manifestWriteFailure;
        Directory.CreateDirectory(_rootPath);
    }

    public IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now)
    {
        lock (_gate)
        {
            var manifest = ReadManifest();
            return manifest.Snapshots!
                .Select(ToDescriptor)
                .ToArray();
        }
    }

    public CatalogPersistedSnapshot? Load(
        string storeCode,
        DateTimeOffset? since,
        string catalogVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogVersion);

        lock (_gate)
        {
            var normalizedStoreCode = storeCode.Trim();
            var normalizedCatalogVersion = catalogVersion.Trim();
            var entry = ReadManifest().Snapshots!.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.StoreCode,
                    normalizedStoreCode,
                    StringComparison.OrdinalIgnoreCase)
                && candidate.Since == since
                && string.Equals(
                    candidate.CatalogVersion,
                    normalizedCatalogVersion,
                    StringComparison.Ordinal));
            return entry is null ? null : TryReadSnapshot(entry);
        }
    }

    public IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now)
    {
        lock (_gate)
        {
            var manifest = ReadManifest();
            return manifest.Snapshots!
                .Select(TryReadSnapshot)
                .Where(snapshot => snapshot is not null)
                .Select(snapshot => snapshot!)
                .ToArray();
        }
    }

    public void Save(CatalogPersistedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.StoreCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.CatalogVersion);

        lock (_gate)
        {
            // 写入前必须先确认现有发布点可读，避免不兼容或损坏的 manifest 被空清单覆盖。
            var manifest = ReadManifest(failClosedOnInvalid: true);
            var storeHash = HashText(snapshot.StoreCode.Trim().ToUpperInvariant());
            var versionHash = HashText(snapshot.CatalogVersion.Trim());
            // 正文必须不可变：manifest 尚未发布时绝不能覆盖旧 entry 正在引用的 LKG 文件。
            var relativeFileName = Path.Combine(
                "snapshots",
                storeHash,
                $"{versionHash}-{Guid.NewGuid():N}.json.gz");
            var targetPath = ResolveRelativePath(relativeFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            // 只有本次确实创建了新 target，且旧 manifest 未引用它，发布失败后才允许清理。
            // 已存在或同版本的正文归属无法可靠判定，宁可保留也不能误删 LKG。
            var targetExistedBeforeWrite = File.Exists(targetPath);
            var targetWasReferencedByOldManifest = manifest.Snapshots!.Any(entry =>
                string.Equals(entry.FileName, relativeFileName, StringComparison.Ordinal));
            var sha256 = WriteSnapshotBody(targetPath, snapshot);
            var manifestSnapshots = manifest.Snapshots!;
            manifestSnapshots.RemoveAll(entry =>
                string.Equals(entry.StoreCode, snapshot.StoreCode.Trim(), StringComparison.OrdinalIgnoreCase)
                && entry.Since == snapshot.Since
                && string.Equals(entry.CatalogVersion, snapshot.CatalogVersion.Trim(), StringComparison.Ordinal));
            manifestSnapshots.Add(new CatalogSnapshotManifestEntry(
                snapshot.StoreCode.Trim(),
                snapshot.Since,
                snapshot.GeneratedAt,
                snapshot.ExpiresAt,
                snapshot.CatalogVersion.Trim(),
                relativeFileName,
                sha256,
                Codec));

            var evicted = manifestSnapshots
                .Where(entry => string.Equals(entry.StoreCode, snapshot.StoreCode.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.GeneratedAt)
                .ThenByDescending(entry => entry.CatalogVersion, StringComparer.Ordinal)
                .Skip(_maxSnapshotsPerStore)
                .ToArray();
            foreach (var entry in evicted)
            {
                manifestSnapshots.Remove(entry);
            }

            // manifest 是发布点：只有正文和 manifest 都完整后，新的版本才可被启动恢复。
            try
            {
                WriteManifest(manifest);
            }
            catch
            {
                TryDeleteNewUnpublishedBody(
                    targetPath,
                    relativeFileName,
                    targetExistedBeforeWrite,
                    targetWasReferencedByOldManifest);
                throw;
            }

            foreach (var entry in evicted)
            {
                TryDelete(ResolveRelativePath(entry.FileName));
            }

            CleanupUnreferencedSnapshotBodies(manifest);
        }
    }

    public void RefreshExpiration(
        string storeCode,
        DateTimeOffset? since,
        string catalogVersion,
        DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogVersion);

        lock (_gate)
        {
            var manifest = ReadManifest(failClosedOnInvalid: true);
            var manifestSnapshots = manifest.Snapshots!;
            var index = manifestSnapshots.FindIndex(entry =>
                string.Equals(entry.StoreCode, storeCode.Trim(), StringComparison.OrdinalIgnoreCase)
                && entry.Since == since
                && string.Equals(entry.CatalogVersion, catalogVersion.Trim(), StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidDataException("目录快照 manifest 中找不到要刷新的版本。");
            }

            // 续期只改 manifest；正文由 Save 原子发布，校验磁盘字节即可避免再复制整份目录。
            // 真正读取目录时 TryReadSnapshot 仍会校验解压及正文元数据。
            var entry = manifestSnapshots[index];
            var bodyPath = ResolveRelativePath(entry.FileName);
            if (!File.Exists(bodyPath) ||
                !string.Equals(ComputeSha256(bodyPath), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("目录快照正文损坏，不能更新刷新时间。");
            }

            manifestSnapshots[index] = manifestSnapshots[index] with { ExpiresAt = expiresAt };
            WriteManifest(manifest);
        }
    }

    private string ManifestPath => Path.Combine(_rootPath, ManifestFileName);

    private void WriteManifest(CatalogSnapshotManifest manifest)
    {
        // 仅供测试稳定复现“正文已移动、manifest 未发布”的故障窗口；生产默认仍走原子写入。
        if (_manifestWriteFailure?.Invoke() == true)
        {
            throw new IOException("目录快照 manifest 写入故障注入。");
        }

        AtomicWrite(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
    }

    private CatalogSnapshotManifest ReadManifest(bool failClosedOnInvalid = false)
    {
        if (!File.Exists(ManifestPath))
        {
            return new CatalogSnapshotManifest();
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<CatalogSnapshotManifest>(
                File.ReadAllBytes(ManifestPath),
                JsonOptions);
            if (!IsSupportedManifest(manifest))
            {
                Log("manifest skipped reason=invalid-contract");
                if (failClosedOnInvalid)
                {
                    throw new InvalidDataException("目录快照 manifest 的版本或编码不受支持。");
                }

                return new CatalogSnapshotManifest();
            }

            return manifest!;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // 损坏 manifest 不能覆盖磁盘正文；保留现场并让本次退化为冷构建。
            Log($"manifest skipped reason=invalid-json error={exception.GetType().Name}");
            if (failClosedOnInvalid)
            {
                throw new InvalidDataException("目录快照 manifest 已损坏。", exception);
            }

            return new CatalogSnapshotManifest();
        }
    }

    private static bool IsSupportedManifest(CatalogSnapshotManifest? manifest)
    {
        if (manifest is null ||
            manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.Codec, Codec, StringComparison.Ordinal) ||
            manifest.Snapshots is null)
        {
            return false;
        }

        return manifest.Snapshots.All(entry =>
            entry is not null &&
            !string.IsNullOrWhiteSpace(entry.StoreCode) &&
            !string.IsNullOrWhiteSpace(entry.CatalogVersion) &&
            !string.IsNullOrWhiteSpace(entry.FileName) &&
            !Path.IsPathRooted(entry.FileName) &&
            entry.GeneratedAt != default &&
            entry.ExpiresAt > entry.GeneratedAt &&
            string.Equals(entry.Codec, Codec, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(entry.Sha256) &&
            entry.Sha256.Length == 64 &&
            entry.Sha256.All(Uri.IsHexDigit));
    }

    private static CatalogSnapshotDescriptor ToDescriptor(
        CatalogSnapshotManifestEntry entry)
    {
        return new CatalogSnapshotDescriptor(
            entry.StoreCode,
            entry.Since,
            entry.GeneratedAt,
            entry.ExpiresAt,
            entry.CatalogVersion);
    }

    private CatalogPersistedSnapshot? TryReadSnapshot(
        CatalogSnapshotManifestEntry entry)
    {
        try
        {
            var path = ResolveRelativePath(entry.FileName);
            if (!File.Exists(path)
                || !string.Equals(
                    ComputeSha256(path),
                    entry.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                Log($"snapshot skipped reason=checksum store={entry.StoreCode} version={entry.CatalogVersion}");
                return null;
            }

            var snapshot = ReadSnapshot(path);
            if (!string.Equals(
                    snapshot.StoreCode,
                    entry.StoreCode,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    snapshot.CatalogVersion,
                    entry.CatalogVersion,
                    StringComparison.Ordinal)
                || snapshot.Since != entry.Since
                || snapshot.GeneratedAt != entry.GeneratedAt)
            {
                Log($"snapshot skipped reason=metadata-mismatch store={entry.StoreCode} version={entry.CatalogVersion}");
                return null;
            }

            // manifest 是发布点，也负责无内容变化时续期；gzip 正文无需重复改写。
            return snapshot with { ExpiresAt = entry.ExpiresAt };
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or JsonException
            or CryptographicException
            or ArgumentException)
        {
            Log($"snapshot skipped reason=read-failed store={entry.StoreCode} version={entry.CatalogVersion} error={exception.GetType().Name}");
            return null;
        }
    }

    private CatalogPersistedSnapshot ReadSnapshot(string path)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CatalogPersistedSnapshot>(gzip, JsonOptions)
            ?? throw new InvalidDataException("目录快照正文为空。");
    }

    private string WriteSnapshotBody(
        string targetPath,
        CatalogPersistedSnapshot snapshot)
    {
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string sha256;
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                // 压缩字节直接流入临时文件，并在同一遍写入中计算发布清单所需的 SHA-256。
                using var hashingOutput = new HashingWriteStream(output, hash);
                using (var gzip = new GZipStream(
                           hashingOutput,
                           CompressionLevel.Fastest,
                           leaveOpen: true))
                {
                    JsonSerializer.Serialize(gzip, snapshot, JsonOptions);
                }

                hashingOutput.Flush();
                output.Flush(flushToDisk: true);
                sha256 = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (!string.Equals(ComputeSha256(temporaryPath), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("目录快照临时文件 SHA-256 校验失败。");
            }

            // 正文由当前 snapshot 直接序列化；磁盘字节与写入时哈希一致后，
            // 再完整反序列化只会复制整份目录，无法增加对元数据或落盘内容的保证。

            File.Move(temporaryPath, targetPath, overwrite: true);
            return sha256;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void AtomicWrite(string targetPath, byte[] contents)
    {
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private string ResolveRelativePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        if (!fullPath.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, _rootPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("目录快照路径越界。");
        }

        return fullPath;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string HashText(string value)
    {
        return ComputeSha256(System.Text.Encoding.UTF8.GetBytes(value));
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void TryDeleteNewUnpublishedBody(
        string targetPath,
        string relativeFileName,
        bool targetExistedBeforeWrite,
        bool targetWasReferencedByOldManifest)
    {
        if (targetExistedBeforeWrite || targetWasReferencedByOldManifest)
        {
            return;
        }

        try
        {
            // 再次以磁盘 manifest 确认未发布，防止写入错误发生在原子替换之后时误删新 LKG。
            var published = ReadManifest(failClosedOnInvalid: true);
            if (published.Snapshots!.Any(entry =>
                    string.Equals(entry.FileName, relativeFileName, StringComparison.Ordinal)))
            {
                Log($"snapshot cleanup skipped reason=manifest-references-target file={relativeFileName}");
                return;
            }

            TryDelete(targetPath);
        }
        catch (Exception exception)
        {
            // 清理无法安全判断或失败时保留正文，避免遮蔽原始 manifest 发布异常。
            Log($"snapshot cleanup skipped reason=verification-failed file={relativeFileName} error={exception.GetType().Name}");
        }
    }

    private void CleanupUnreferencedSnapshotBodies(CatalogSnapshotManifest manifest)
    {
        try
        {
            var snapshotsPath = Path.Combine(_rootPath, "snapshots");
            if (!Directory.Exists(snapshotsPath))
            {
                return;
            }

            var referencedPaths = manifest.Snapshots!
                .Select(entry => ResolveRelativePath(entry.FileName))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(
                         snapshotsPath,
                         "*.json.gz",
                         SearchOption.AllDirectories))
            {
                if (!referencedPaths.Contains(path))
                {
                    // 仅扫描最终 gzip 名称；写入中的 .tmp 文件不会落入清理范围。
                    TryDelete(path);
                }
            }
        }
        catch (Exception exception)
        {
            // 发布已成功时，历史垃圾的清理不能反过来影响本次可用的 manifest。
            Log($"snapshot cleanup skipped reason=enumeration-failed error={exception.GetType().Name}");
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][CatalogSnapshotStore] {DateTimeOffset.Now:O} {message}");
    }

    private sealed class HashingWriteStream(
        Stream inner,
        IncrementalHash hash) : Stream
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

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

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

    private sealed class CatalogSnapshotManifest
    {
        public int SchemaVersion { get; init; } = 1;

        public string Codec { get; init; } = GzipCatalogSnapshotStore.Codec;

        public List<CatalogSnapshotManifestEntry>? Snapshots { get; init; } = [];
    }

    private sealed record CatalogSnapshotManifestEntry(
        string StoreCode,
        DateTimeOffset? Since,
        DateTimeOffset GeneratedAt,
        DateTimeOffset ExpiresAt,
        string CatalogVersion,
        string FileName,
        string Sha256,
        string Codec);
}
