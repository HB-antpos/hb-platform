namespace BlazorApp.FaceIngest;

/// <summary>
/// 原始报文清理（一轮）。业务口径：
/// - 删除接收时间早于 now - RetentionDays 的 .bin / .json；接收时间以文件名里的 UTC 时间戳为准
///   （与目录日期同源，都来自 TimeProvider），文件名解析不出时回退 LastWriteTimeUtc；
/// - 删除超过 1 小时的 .tmp 残片（正常写入在秒级完成，残片只可能来自进程被杀）；
/// - 删除空目录（今天与昨天的日期目录除外，避免与正在建目录的写入竞争）；
/// - 最后全量重扫校正统计。单个文件删除失败只记日志不中断。
/// 抽成独立类是为了测试能直接调用 <see cref="RunOnceAsync"/>，不依赖真实时钟等待。
/// </summary>
internal sealed class CaptureStorageCleaner
{
    /// <summary>.tmp 残片的最大存活时间。</summary>
    internal static readonly TimeSpan TempFileMaxAge = TimeSpan.FromHours(1);

    private readonly FaceIngestSettingsMonitor _settings;
    private readonly RawCaptureStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureStorageCleaner> _logger;

    public CaptureStorageCleaner(
        FaceIngestSettingsMonitor settings,
        RawCaptureStore store,
        TimeProvider timeProvider,
        ILogger<CaptureStorageCleaner> logger)
    {
        _settings = settings;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>执行一轮清理并返回本轮摘要。</summary>
    public Task<CleanupSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RunOnce(cancellationToken));
    }

    private CleanupSummary RunOnce(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var settings = _settings.Current;
        var retentionCutoff = now - TimeSpan.FromDays(settings.RetentionDays);
        var tempCutoff = now - TempFileMaxAge;
        var rawRoot = _store.RawRootPath;

        long deletedCaptureFiles = 0;
        long deletedBytes = 0;
        long deletedTempFiles = 0;
        long failedDeletes = 0;
        long deletedDirectories = 0;

        if (Directory.Exists(rawRoot))
        {
            // 中文注释：先把清单取出来再删，避免边枚举边删除导致的跳项。
            List<FileInfo> files;
            try
            {
                files = [.. new DirectoryInfo(rawRoot).EnumerateFiles("*", RawCaptureStore.RecursiveEnumeration)];
            }
            catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
            {
                _logger.LogWarning(ex, "Face ingest cleanup could not enumerate storage; skipping this round.");
                files = [];
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = CaptureFileNames.Classify(file.Name);
                if (kind == CaptureFileKind.Other)
                {
                    continue;
                }

                DateTimeOffset receivedAt;
                long length;
                try
                {
                    receivedAt = CaptureFileNames.TryParseReceivedAt(file.Name)
                        ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                    length = file.Length;
                }
                catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
                {
                    // 文件已被并发删除等情况，跳过即可。
                    continue;
                }

                var expired = kind == CaptureFileKind.Temp ? receivedAt < tempCutoff : receivedAt < retentionCutoff;
                if (!expired)
                {
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
                {
                    failedDeletes++;
                    _logger.LogWarning(ex, "Face ingest cleanup could not delete {FileName}.", file.Name);
                    continue;
                }

                if (kind == CaptureFileKind.Temp)
                {
                    deletedTempFiles++;
                }
                else
                {
                    deletedCaptureFiles++;
                    deletedBytes += length;
                    _store.OnFilesDeleted(1, length);
                }
            }

            deletedDirectories = DeleteEmptyDirectories(rawRoot, now, cancellationToken);
        }

        var statistics = _store.RescanStatistics();
        var summary = new CleanupSummary(
            deletedCaptureFiles,
            deletedBytes,
            deletedTempFiles,
            deletedDirectories,
            failedDeletes,
            statistics.FileCount,
            statistics.Bytes);
        _logger.LogInformation(
            "Face ingest cleanup finished deletedFiles={DeletedFiles} deletedBytes={DeletedBytes} deletedTempFiles={DeletedTempFiles} deletedDirectories={DeletedDirectories} failedDeletes={FailedDeletes} storedFiles={StoredFileCount} storedBytes={StoredBytes}",
            summary.DeletedCaptureFiles,
            summary.DeletedBytes,
            summary.DeletedTempFiles,
            summary.DeletedDirectories,
            summary.FailedDeletes,
            summary.StoredFileCount,
            summary.StoredBytes);
        return summary;
    }

    private long DeleteEmptyDirectories(string rawRoot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var yesterday = today.AddDays(-1);
        long deleted = 0;
        IEnumerable<string> dateDirectories;
        try
        {
            dateDirectories = [.. Directory.EnumerateDirectories(rawRoot)];
        }
        catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
        {
            _logger.LogWarning(ex, "Face ingest cleanup could not enumerate date directories.");
            return 0;
        }

        foreach (var dateDirectory in dateDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = CaptureFileNames.TryParseDateDirectory(Path.GetFileName(dateDirectory));
            // 关键逻辑：今天 / 昨天的目录可能正被写入（先 CreateDirectory 再建文件），删空目录会让写入失败，跳过。
            if (date == today || date == yesterday)
            {
                continue;
            }

            deleted += DeleteEmptyTree(dateDirectory);
        }

        return deleted;
    }

    /// <summary>自底向上删除空目录，返回删除的目录数。</summary>
    private long DeleteEmptyTree(string directory)
    {
        long deleted = 0;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory).ToArray())
            {
                deleted += DeleteEmptyTree(child);
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
                deleted++;
            }
        }
        catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
        {
            _logger.LogWarning(ex, "Face ingest cleanup could not remove directory {DirectoryName}.", Path.GetFileName(directory));
        }

        return deleted;
    }
}

/// <summary>一轮清理的摘要（用于日志与测试断言）。</summary>
internal sealed record CleanupSummary(
    long DeletedCaptureFiles,
    long DeletedBytes,
    long DeletedTempFiles,
    long DeletedDirectories,
    long FailedDeletes,
    long StoredFileCount,
    long StoredBytes);
