using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 原始报文落盘与存储统计。
/// 业务口径：
/// - 请求体逐字节原样写入 <c>.bin</c>（不解析 multipart），边写边算 SHA-256；
/// - 先写 <c>.bin.tmp</c> → Flush → 改名 <c>.bin</c>，再写 <c>.json.tmp</c> → 改名 <c>.json</c>，
///   .json 出现即代表该条完整；任何失败路径都删除临时文件，不留残片，也不留只有 .bin 的半条；
/// - 统计只算 .bin + .json（忽略 .tmp），启动时全量扫描，之后增量维护，清理任务每轮重扫校正。
/// </summary>
internal sealed class RawCaptureStore
{
    private const int CopyBufferSize = 81920;

    // 中文注释：剩余空间查询有系统调用开销，缓存几秒即可，不必每个请求都查。
    private static readonly TimeSpan FreeSpaceCacheDuration = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;
    private readonly IDiskSpaceProbe _diskSpaceProbe;
    private readonly ILogger<RawCaptureStore> _logger;
    private readonly ThrottledLogGate _logGate;
    private readonly object _freeSpaceGate = new();
    private long _storedBytes;
    private long _storedFileCount;
    private bool _hasFreeSpaceSample;
    private long _freeSpaceSampledAt;
    private long? _cachedFreeBytes;

    public RawCaptureStore(
        FaceIngestSettingsMonitor settings,
        TimeProvider timeProvider,
        IDiskSpaceProbe diskSpaceProbe,
        ILogger<RawCaptureStore> logger)
    {
        _timeProvider = timeProvider;
        _diskSpaceProbe = diskSpaceProbe;
        _logger = logger;
        _logGate = new ThrottledLogGate(timeProvider, TimeSpan.FromSeconds(60));

        // 中文注释：根目录只在启动时确定；热更新 StorageRootPath 不会搬家，需重启。
        RootPath = settings.Current.StorageRootPath;
        RawRootPath = Path.Combine(RootPath, CaptureFileNames.RawDirectoryName);
        try
        {
            Directory.CreateDirectory(RawRootPath);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            // 中文注释：建目录失败不阻止启动（容器会反复重启、更难排查）；后续写入会再试，失败则回 503。
            _logger.LogError(ex, "Face ingest storage directory could not be created.");
        }

        RescanStatistics();
    }

    /// <summary>存储根目录（绝对路径）。</summary>
    public string RootPath { get; }

    /// <summary>原始报文目录 <c>{root}/raw</c>。</summary>
    public string RawRootPath { get; }

    /// <summary>已存字节数（.bin + .json）。</summary>
    public long StoredBytes => Interlocked.Read(ref _storedBytes);

    /// <summary>已存文件数（.bin 与 .json 各算一个）。</summary>
    public long StoredFileCount => Interlocked.Read(ref _storedFileCount);

    /// <summary>
    /// 全量扫描 raw/ 重算统计并覆盖计数器。扫描期间并发完成的写入可能被漏算或多算，下一轮重扫会校正。
    /// </summary>
    public StorageStatistics RescanStatistics()
    {
        long bytes = 0;
        long files = 0;
        try
        {
            if (Directory.Exists(RawRootPath))
            {
                foreach (var file in new DirectoryInfo(RawRootPath).EnumerateFiles("*", RecursiveEnumeration))
                {
                    var kind = CaptureFileNames.Classify(file.Name);
                    if (kind is CaptureFileKind.Body or CaptureFileKind.Metadata)
                    {
                        bytes += file.Length;
                        files++;
                    }
                }
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _logger.LogWarning(ex, "Face ingest storage statistics scan failed; keeping previous counters.");
            return new StorageStatistics(StoredFileCount, StoredBytes);
        }

        Interlocked.Exchange(ref _storedBytes, bytes);
        Interlocked.Exchange(ref _storedFileCount, files);
        return new StorageStatistics(files, bytes);
    }

    /// <summary>清理任务删掉文件后同步扣减计数（本轮结束还会整体重扫）。</summary>
    public void OnFilesDeleted(long fileCount, long bytes)
    {
        Interlocked.Add(ref _storedFileCount, -fileCount);
        Interlocked.Add(ref _storedBytes, -bytes);
    }

    /// <summary>
    /// 判断当前是否还能落盘。超总字节、超文件数、盘剩余不足任一触发即返回对应原因。
    /// 并发请求可能同时通过检查，实际最多超出「并发数 × 单请求上限」，P0 可接受。
    /// </summary>
    public StorageRejectReason CheckCapacity(FaceIngestSettings settings)
    {
        if (StoredBytes >= settings.MaxTotalBytes)
        {
            return StorageRejectReason.MaxTotalBytes;
        }

        if (StoredFileCount >= settings.MaxFileCount)
        {
            return StorageRejectReason.MaxFileCount;
        }

        if (settings.MinFreeDiskBytes > 0)
        {
            var freeBytes = GetCachedFreeBytes();
            if (freeBytes is long free && free < settings.MinFreeDiskBytes)
            {
                return StorageRejectReason.MinFreeDiskBytes;
            }
        }

        return StorageRejectReason.None;
    }

    /// <summary>
    /// 把请求体原样流式写入 .bin 并写元数据 .json。返回结果状态，由端点映射成 200 / 413 / 503。
    /// </summary>
    public async Task<CaptureSaveResult> SaveAsync(CaptureWriteRequest request, Stream body, CancellationToken requestAborted)
    {
        var captureId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(
            RawRootPath,
            CaptureFileNames.FormatDateDirectory(request.ReceivedAtUtc),
            request.Camera.CameraId);
        var baseName = CaptureFileNames.FormatBaseName(request.ReceivedAtUtc, captureId);
        var bodyPath = Path.Combine(directory, baseName + CaptureFileNames.BodyExtension);
        var bodyTempPath = bodyPath + CaptureFileNames.TempExtension;
        var metadataPath = Path.Combine(directory, baseName + CaptureFileNames.MetadataExtension);
        var metadataTempPath = metadataPath + CaptureFileNames.TempExtension;

        var completed = false;
        var bodyMoved = false;
        long bodyBytes = 0;
        FileStream? bodyFile = null;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                Directory.CreateDirectory(directory);
                bodyFile = new FileStream(bodyTempPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                });
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return CaptureSaveResult.Failed(CaptureSaveStatus.StorageFailed, bodyBytes, ex);
            }

            while (true)
            {
                int read;
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferSize), requestAborted);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    // 中文注释：Kestrel 全局上限触发（与 MaxRequestBodyBytes 同值），按 413 处理。
                    return CaptureSaveResult.Failed(CaptureSaveStatus.BodyTooLarge, bodyBytes, ex);
                }
                catch (Exception ex)
                {
                    // 中文注释：读请求体阶段的异常都来自客户端（中途断开 IOException、RequestAborted 取消、
                    // 坏的 chunked 编码等），不是本机存储问题，不能回 503 误导摄像机。
                    return CaptureSaveResult.Failed(CaptureSaveStatus.ClientAborted, bodyBytes, ex);
                }

                if (read == 0)
                {
                    break;
                }

                bodyBytes += read;
                if (bodyBytes > request.MaxBodyBytes)
                {
                    // 中文注释：没有 Content-Length（chunked）或 TestServer 等不执行 Kestrel 上限的场景，在这里兜底。
                    return CaptureSaveResult.Failed(CaptureSaveStatus.BodyTooLarge, bodyBytes, null);
                }

                hash.AppendData(buffer, 0, read);
                try
                {
                    // 中文注释：本地写盘不跟随 RequestAborted，避免把客户端断开误判成存储失败。
                    await bodyFile.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    return CaptureSaveResult.Failed(CaptureSaveStatus.StorageFailed, bodyBytes, ex);
                }
            }

            try
            {
                await bodyFile.FlushAsync(CancellationToken.None);
                await bodyFile.DisposeAsync();
                bodyFile = null;
                File.Move(bodyTempPath, bodyPath);
                bodyMoved = true;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return CaptureSaveResult.Failed(CaptureSaveStatus.StorageFailed, bodyBytes, ex);
            }

            var bodySha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            var durationMs = Math.Round(_timeProvider.GetElapsedTime(request.StartTimestamp).TotalMilliseconds, 3);
            var info = request.RequestInfo;
            var metadata = new CaptureMetadata
            {
                CaptureId = captureId,
                CameraId = request.Camera.CameraId,
                StoreCode = request.Camera.StoreCode,
                ReceivedAtUtc = request.ReceivedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                DurationMs = durationMs,
                Method = info.Method,
                Path = info.Path,
                Query = info.Query,
                Protocol = info.Protocol,
                Headers = info.Headers,
                RemoteIpAddress = info.RemoteIpAddress,
                ForwardedFor = info.ForwardedFor,
                RealIp = info.RealIp,
                ContentType = info.ContentType,
                DeclaredContentLength = info.DeclaredContentLength,
                BodyBytes = bodyBytes,
                BodySha256 = bodySha256,
                TokenSource = info.TokenSource,
            };
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, CaptureMetadata.JsonOptions);

            try
            {
                await using (var metadataFile = new FileStream(metadataTempPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                }))
                {
                    await metadataFile.WriteAsync(metadataBytes, CancellationToken.None);
                    await metadataFile.FlushAsync(CancellationToken.None);
                }

                File.Move(metadataTempPath, metadataPath);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return CaptureSaveResult.Failed(CaptureSaveStatus.StorageFailed, bodyBytes, ex);
            }

            completed = true;
            Interlocked.Add(ref _storedBytes, bodyBytes + metadataBytes.LongLength);
            Interlocked.Add(ref _storedFileCount, 2);
            return new CaptureSaveResult(CaptureSaveStatus.Stored, captureId, bodyBytes, bodySha256, durationMs, null);
        }
        finally
        {
            if (bodyFile is not null)
            {
                try
                {
                    await bodyFile.DisposeAsync();
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    _logger.LogDebug(ex, "Face ingest temp file close failed during cleanup.");
                }
            }

            if (!completed)
            {
                // 关键逻辑：任何失败路径都不留 .tmp；.json 没写成时连已改名的 .bin 也删掉，
                // 保证「有 .json 才算一条」且统计不被半条数据污染（摄像机收到非 200 会重推）。
                TryDelete(bodyTempPath);
                TryDelete(metadataTempPath);
                if (bodyMoved)
                {
                    TryDelete(bodyPath);
                }
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>读写文件时视为「存储失败」的异常类型（磁盘满、权限、路径被占用等）。</summary>
    internal static bool IsStorageFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

    internal static EnumerationOptions RecursiveEnumeration { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private long? GetCachedFreeBytes()
    {
        lock (_freeSpaceGate)
        {
            var now = _timeProvider.GetTimestamp();
            if (_hasFreeSpaceSample && _timeProvider.GetElapsedTime(_freeSpaceSampledAt, now) < FreeSpaceCacheDuration)
            {
                return _cachedFreeBytes;
            }

            _cachedFreeBytes = _diskSpaceProbe.GetAvailableFreeBytes(RootPath);
            _freeSpaceSampledAt = now;
            _hasFreeSpaceSample = true;
            if (_cachedFreeBytes is null && _logGate.TryAcquire("disk-probe-failed", out var suppressed))
            {
                // 中文注释：查不到剩余空间时不拦截写入（真满了写盘会失败并回 503），只节流告警。
                _logger.LogWarning(
                    "Face ingest free disk space is unknown; free-space guard skipped. suppressedSinceLastLog={SuppressedCount}",
                    suppressed);
            }

            return _cachedFreeBytes;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _logger.LogWarning(ex, "Face ingest could not delete leftover file {FileName}.", Path.GetFileName(path));
        }
    }
}

/// <summary>一次落盘请求的输入：已鉴权的摄像机、接收时间、打码后的请求信息与大小上限。</summary>
internal sealed record CaptureWriteRequest(
    CameraRegistration Camera,
    DateTimeOffset ReceivedAtUtc,
    long StartTimestamp,
    long MaxBodyBytes,
    CapturedRequestInfo RequestInfo);

/// <summary>落盘结果。</summary>
internal sealed record CaptureSaveResult(
    CaptureSaveStatus Status,
    string? CaptureId,
    long BodyBytes,
    string? BodySha256,
    double DurationMs,
    Exception? Error)
{
    public static CaptureSaveResult Failed(CaptureSaveStatus status, long bodyBytes, Exception? error)
        => new(status, null, bodyBytes, null, 0, error);
}

/// <summary>落盘结果状态。</summary>
internal enum CaptureSaveStatus
{
    /// <summary>已完整落盘（.bin + .json）。</summary>
    Stored,

    /// <summary>请求体超过上限，回 413。</summary>
    BodyTooLarge,

    /// <summary>客户端中途断开或请求体读取失败。</summary>
    ClientAborted,

    /// <summary>本机写盘失败（磁盘满、权限等），回 503 让摄像机断网续传后重推。</summary>
    StorageFailed,
}

/// <summary>拒绝落盘的原因（存储护栏）。</summary>
internal enum StorageRejectReason
{
    None,
    MaxTotalBytes,
    MaxFileCount,
    MinFreeDiskBytes,
}

/// <summary>存储统计快照。</summary>
internal readonly record struct StorageStatistics(long FileCount, long Bytes);
