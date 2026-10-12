using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 后台处理器的一轮逻辑（与接收端点完全解耦：处理慢、DB 挂、COS 挂都不影响接收回 200）。业务口径：
/// - 找 raw/ 下已完整（有 .json）且没有 .processed / .failed 标记、.retry 已到期的报文，按接收时间从旧到新，每轮最多 ProcessingBatchSize 条，串行处理；
/// - 一条：读元数据 + .bin → 宽容解析（不抛异常）→ 存图（COS 或本地 images/）→ 写库（连库时）或写本地 .parsed.json 摘要（不连库时）→ 写 .processed；
/// - 存图 / 写库 / 读文件失败：写 .retry（attempts 递增，指数退避 30 秒起、单次最长 6 小时），达到 ProcessingMaxAttempts 改写 .failed；
///   元数据损坏这类重试也没用的错误直接写 .failed。原始报文一律保留到保留期，供排障；
/// - 标记、日志、摘要、DB 字段都不含 token（元数据里本来就已打码，处理器也从不接触 token）。
/// 抽成独立类是为了测试能直接调用 <see cref="RunOnceAsync"/>，不依赖真实时钟等待。
/// </summary>
internal sealed partial class CaptureProcessor
{
    /// <summary>首次重试等待。</summary>
    internal static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>单次重试等待上限。</summary>
    internal static readonly TimeSpan RetryMaxDelay = TimeSpan.FromHours(6);

    /// <summary>同一摄像机 LastSeenAtUtc 至多每 5 分钟更新一次。</summary>
    internal static readonly TimeSpan LastSeenThrottle = TimeSpan.FromMinutes(5);

    private const int MaxErrorLength = 300;

    private readonly FaceIngestSettingsMonitor _settings;
    private readonly RawCaptureStore _store;
    private readonly IImageStore _imageStore;
    private readonly ICaptureEventWriter? _writer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureProcessor> _logger;
    private readonly ThrottledLogGate _failureLogGate;
    private readonly Dictionary<string, DateTime> _lastSeenTouched = new(StringComparer.Ordinal);

    // 中文注释：早于昨天、且所有报文都已有终态标记的日期目录，不再每轮重复扫描（新报文只会进今天的目录）。
    private readonly HashSet<string> _settledDateDirectories = new(StringComparer.Ordinal);

    public CaptureProcessor(
        FaceIngestSettingsMonitor settings,
        RawCaptureStore store,
        IImageStore imageStore,
        IServiceProvider services,
        TimeProvider timeProvider,
        ILogger<CaptureProcessor> logger)
    {
        _settings = settings;
        _store = store;
        _imageStore = imageStore;
        // 关键逻辑：只有配置了数据库才写库；否则只写本地摘要（cameras.json 模式，P0 环境平滑升级）。
        _writer = settings.Current.DatabaseConfigured ? services.GetRequiredService<ICaptureEventWriter>() : null;
        _timeProvider = timeProvider;
        _logger = logger;
        _failureLogGate = new ThrottledLogGate(timeProvider, TimeSpan.FromSeconds(60));
    }

    /// <summary>是否写库。</summary>
    public bool DatabaseEnabled => _writer is not null;

    /// <summary>执行一轮处理并返回摘要。取消时抛 <see cref="OperationCanceledException"/>，未完成的那条下轮重做（幂等）。</summary>
    public async Task<ProcessingSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var now = _timeProvider.GetUtcNow();
        var candidates = FindCandidates(settings.ProcessingBatchSize, now, cancellationToken);
        int processed = 0, retried = 0, failed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await ProcessOneAsync(candidate, settings, cancellationToken))
            {
                case ItemOutcome.Processed:
                    processed++;
                    break;
                case ItemOutcome.Retry:
                    retried++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new ProcessingSummary(candidates.Count, processed, retried, failed);
    }

    // ---------------------------------------------------------------- 找待处理报文

    private List<Candidate> FindCandidates(int batchSize, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = new List<Candidate>();
        var rawRoot = _store.RawRootPath;
        if (!Directory.Exists(rawRoot))
        {
            return result;
        }

        List<(string Path, string Name, DateOnly Date)> dateDirectories;
        try
        {
            dateDirectories = [.. Directory.EnumerateDirectories(rawRoot)
                .Select(path => (Path: path, Name: System.IO.Path.GetFileName(path)))
                .Select(entry => (entry.Path, entry.Name, Date: CaptureFileNames.TryParseDateDirectory(entry.Name)))
                .Where(entry => entry.Date is not null)
                .Select(entry => (entry.Path, entry.Name, entry.Date!.Value))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
        {
            _logger.LogWarning(ex, "Face ingest processor could not enumerate raw storage; skipping this round.");
            return result;
        }

        var existingNames = dateDirectories.Select(static entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        _settledDateDirectories.RemoveWhere(name => !existingNames.Contains(name));
        var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);

        foreach (var (datePath, dateName, date) in dateDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_settledDateDirectories.Contains(dateName))
            {
                continue;
            }

            var pending = new List<Candidate>();
            var allTerminal = true;
            try
            {
                foreach (var cameraDirectory in Directory.EnumerateDirectories(datePath))
                {
                    if (!FaceIngestSettings.IsValidCameraId(Path.GetFileName(cameraDirectory)))
                    {
                        continue;
                    }

                    var names = Directory.EnumerateFiles(cameraDirectory)
                        .Select(static path => Path.GetFileName(path))
                        .ToHashSet(StringComparer.Ordinal);
                    foreach (var name in names)
                    {
                        if (CaptureFileNames.Classify(name) != CaptureFileKind.Metadata)
                        {
                            continue;
                        }

                        var baseName = name[..^CaptureFileNames.MetadataExtension.Length];
                        if (names.Contains(baseName + CaptureFileNames.ProcessedMarkerExtension)
                            || names.Contains(baseName + CaptureFileNames.FailedMarkerExtension))
                        {
                            continue;
                        }

                        allTerminal = false;
                        RetryState? retry = null;
                        if (names.Contains(baseName + CaptureFileNames.RetryMarkerExtension))
                        {
                            retry = ReadRetryState(Path.Combine(cameraDirectory, baseName + CaptureFileNames.RetryMarkerExtension));
                            if (retry is not null && retry.NextAttemptUtc > now)
                            {
                                continue;
                            }
                        }

                        pending.Add(new Candidate(cameraDirectory, baseName, retry?.Attempts ?? 0));
                    }
                }
            }
            catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
            {
                _logger.LogWarning(ex, "Face ingest processor could not enumerate {DateDirectory}; will retry next round.", dateName);
                continue;
            }

            if (allTerminal && date < yesterday)
            {
                _settledDateDirectories.Add(dateName);
            }

            // 中文注释：文件名以 UTC 毫秒时间戳开头，按名字排序即按接收时间从旧到新（同一天跨摄像机也成立）。
            foreach (var candidate in pending.OrderBy(static c => c.BaseName, StringComparer.Ordinal))
            {
                result.Add(candidate);
                if (result.Count >= batchSize)
                {
                    return result;
                }
            }
        }

        return result;
    }

    private RetryState? ReadRetryState(string path)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<RetryMarker>(File.ReadAllBytes(path), ProcessingMarkers.JsonOptions);
            return marker is null ? null : new RetryState(Math.Max(0, marker.Attempts), marker.NextAttemptUtc);
        }
        catch (Exception ex) when (ex is JsonException || RawCaptureStore.IsStorageFailure(ex))
        {
            // 中文注释：标记损坏不阻塞处理：当作到期、尝试次数从 0 算（最多多试几次，不会丢数据）。
            _logger.LogWarning("Face ingest retry marker unreadable; treating as due. file={FileName} error={ErrorType}", Path.GetFileName(path), ex.GetType().Name);
            return null;
        }
    }

    // ---------------------------------------------------------------- 处理一条

    private async Task<ItemOutcome> ProcessOneAsync(Candidate candidate, FaceIngestSettings settings, CancellationToken cancellationToken)
    {
        var startTimestamp = _timeProvider.GetTimestamp();
        var basePath = Path.Combine(candidate.Directory, candidate.BaseName);
        var attempts = candidate.PreviousAttempts + 1;

        CaptureMetadata metadata;
        DateTimeOffset receivedAtUtc;
        try
        {
            metadata = JsonSerializer.Deserialize<CaptureMetadata>(
                    await File.ReadAllBytesAsync(basePath + CaptureFileNames.MetadataExtension, cancellationToken),
                    CaptureMetadata.JsonOptions)
                ?? throw new InvalidDataException("metadata is empty");
            receivedAtUtc = ValidateMetadata(metadata, candidate.BaseName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not (JsonException or InvalidDataException) && RawCaptureStore.IsStorageFailure(ex))
        {
            // 中文注释：读文件失败（被并发清理、权限、盘故障）可能是暂时的，按退避重试。
            return await RecordFailureAsync(candidate, basePath, attempts, settings, ex, cancellationToken);
        }
        catch (Exception ex)
        {
            // 中文注释：元数据损坏 / 不合规，重试也不会好，直接标记 .failed，原始报文留给人工排查；
            // 兜住所有异常是为了一条坏数据不会让整轮处理反复失败（毒丸）。
            return FailPermanently(candidate, basePath, attempts, ex);
        }

        try
        {
            var body = await File.ReadAllBytesAsync(basePath + CaptureFileNames.BodyExtension, cancellationToken);
            var bodySha256 = Convert.ToHexStringLower(SHA256.HashData(body));
            var parsed = HikvisionPushParser.Parse(body, metadata.ContentType);
            var parseNote = parsed.Note;
            if (!string.Equals(bodySha256, metadata.BodySha256, StringComparison.OrdinalIgnoreCase))
            {
                // 中文注释：落盘后被改动或磁盘损坏；以实际内容为准并记在 note 里。
                parseNote = JoinNote("body sha256 differs from metadata; actual content used", parseNote);
            }

            var createdAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var imageRecords = new List<CaptureImageRecord>();
            foreach (var image in parsed.Images)
            {
                var imageSha256 = Convert.ToHexStringLower(SHA256.HashData(image.Data.Span));
                var stored = await _imageStore.SaveImageAsync(
                    new ImageWriteRequest(metadata.CaptureId, metadata.CameraId, metadata.StoreCode, receivedAtUtc, image.PartIndex, image.Data, image.ContentType ?? "image/jpeg", imageSha256),
                    cancellationToken);
                var face = image.FaceIndex is int faceIndex && faceIndex < parsed.Faces.Count ? parsed.Faces[faceIndex] : null;
                imageRecords.Add(new CaptureImageRecord
                {
                    ImageId = BuildImageId(metadata.CaptureId, image.PartIndex),
                    CaptureId = metadata.CaptureId,
                    Kind = image.Kind,
                    PartIndex = image.PartIndex,
                    FaceIndex = image.FaceIndex,
                    Storage = stored.Storage,
                    ObjectKey = stored.ObjectKey,
                    ContentType = Truncate(image.ContentType, 64),
                    ByteSize = image.Data.Length,
                    Sha256 = imageSha256,
                    Width = image.Width,
                    Height = image.Height,
                    RectX = face?.RectX,
                    RectY = face?.RectY,
                    RectW = face?.RectW,
                    RectH = face?.RectH,
                    RectNormalized = face?.RectNormalized,
                    FaceScore = face?.FaceScore,
                    CameraFaceId = face?.CameraFaceId,
                    AttributesJson = face?.AttributesJson,
                    CreatedAtUtc = createdAtUtc,
                });
            }

            var rawStored = await _imageStore.SaveRawBodyAsync(
                new RawBodyWriteRequest(metadata.CaptureId, metadata.CameraId, metadata.StoreCode, receivedAtUtc, body, bodySha256),
                cancellationToken);
            var captureEvent = new CaptureEventRecord
            {
                CaptureId = metadata.CaptureId,
                CameraId = metadata.CameraId,
                StoreCode = metadata.StoreCode,
                ReceivedAtUtc = receivedAtUtc.UtcDateTime,
                CameraEventTime = parsed.CameraEventTime,
                EventType = parsed.EventType,
                EventState = parsed.EventState,
                CameraMac = parsed.MacAddress,
                CameraChannel = parsed.Channel,
                BodySha256 = bodySha256,
                BodyBytes = body.LongLength,
                IsRetransmission = parsed.IsRetransmission,
                FaceCount = parsed.FaceCount,
                ParseStatus = parsed.Status,
                ParseNote = Truncate(parseNote, 500),
                RawStorage = rawStored?.Storage ?? LocalImageStore.Name,
                RawObjectKey = rawStored?.ObjectKey,
                CreatedAtUtc = createdAtUtc,
            };

            string? duplicateOf = null;
            if (_writer is not null)
            {
                var outcome = await _writer.WriteAsync(captureEvent, imageRecords, cancellationToken);
                duplicateOf = outcome.DuplicateOfCaptureId;
                await TouchLastSeenAsync(metadata.CameraId, captureEvent.ReceivedAtUtc, cancellationToken);
            }
            else
            {
                var summary = ProcessingMarkers.BuildParsedSummary(captureEvent, parsed, imageRecords);
                await AtomicFile.WriteAsync(basePath + CaptureFileNames.ParsedSummaryExtension, summary, cancellationToken);
            }

            await AtomicFile.WriteAsync(
                basePath + CaptureFileNames.ProcessedMarkerExtension,
                JsonSerializer.SerializeToUtf8Bytes(
                    new ProcessedMarker
                    {
                        CaptureId = metadata.CaptureId,
                        ProcessedAtUtc = _timeProvider.GetUtcNow(),
                        Attempts = attempts,
                        ParseStatus = parsed.Status.ToString(),
                        FaceCount = parsed.FaceCount,
                        ImageCount = imageRecords.Count,
                        ImageStorage = _imageStore.StorageName,
                        RawStorage = captureEvent.RawStorage,
                        DatabaseWritten = _writer is not null,
                        DuplicateOfCaptureId = duplicateOf,
                    },
                    ProcessingMarkers.JsonOptions),
                cancellationToken);
            TryDeleteMarker(basePath + CaptureFileNames.RetryMarkerExtension);

            var durationMs = Math.Round(_timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds, 1);
            _logger.LogInformation(
                "Face ingest capture processed capture={CaptureIdPrefix} camera={CameraId} faces={FaceCount} images={ImageCount} parseStatus={ParseStatus} storage={Storage} database={DatabaseWritten} duplicateOf={DuplicateOfPrefix} attempts={Attempts} durationMs={DurationMs}",
                metadata.CaptureId[..8],
                metadata.CameraId,
                parsed.FaceCount,
                imageRecords.Count,
                parsed.Status,
                _imageStore.StorageName,
                _writer is not null,
                duplicateOf?[..8],
                attempts,
                durationMs);
            return ItemOutcome.Processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await RecordFailureAsync(candidate, basePath, attempts, settings, ex, cancellationToken);
        }
    }

    /// <summary>校验元数据里会进路径 / 对象键 / DB 的字段，返回接收时间。不合法抛 <see cref="InvalidDataException"/>。</summary>
    private static DateTimeOffset ValidateMetadata(CaptureMetadata metadata, string baseName)
    {
        // 中文注释：反序列化不校验可空性，必填字段可能是 null，先挡掉。
        if (metadata.CaptureId is null || metadata.CameraId is null || metadata.StoreCode is null
            || metadata.ReceivedAtUtc is null || metadata.BodySha256 is null)
        {
            throw new InvalidDataException("metadata is missing required fields");
        }

        if (!CaptureIdPattern().IsMatch(metadata.CaptureId) || !baseName.EndsWith("_" + metadata.CaptureId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("metadata captureId is invalid or does not match the file name");
        }

        if (!FaceIngestSettings.IsValidCameraId(metadata.CameraId) || !FaceIngestSettings.IsValidStoreCode(metadata.StoreCode))
        {
            throw new InvalidDataException("metadata cameraId or storeCode is invalid");
        }

        if (!DateTimeOffset.TryParse(metadata.ReceivedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var receivedAtUtc))
        {
            throw new InvalidDataException("metadata receivedAtUtc is invalid");
        }

        return receivedAtUtc;
    }

    private async Task TouchLastSeenAsync(string cameraId, DateTime receivedAtUtc, CancellationToken cancellationToken)
    {
        // 中文注释：节流：同一摄像机距上次更新不足 5 分钟就跳过；失败只节流记 Warning，不影响这条的处理结果、也不重试。
        if (_lastSeenTouched.TryGetValue(cameraId, out var last) && receivedAtUtc - last < LastSeenThrottle)
        {
            return;
        }

        try
        {
            await _writer!.TouchCameraLastSeenAsync(cameraId, receivedAtUtc, cancellationToken);
            _lastSeenTouched[cameraId] = receivedAtUtc > last ? receivedAtUtc : last;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_failureLogGate.TryAcquire("last-seen:" + ex.GetType().Name, out var suppressed))
            {
                _logger.LogWarning(
                    "Face ingest camera LastSeenAtUtc update failed camera={CameraId} error={Error} suppressedSinceLastLog={SuppressedCount}",
                    cameraId,
                    DescribeError(ex),
                    suppressed);
            }
        }
    }

    private async Task<ItemOutcome> RecordFailureAsync(
        Candidate candidate,
        string basePath,
        int attempts,
        FaceIngestSettings settings,
        Exception error,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var description = DescribeError(error);
        var giveUp = attempts >= settings.ProcessingMaxAttempts;
        try
        {
            if (giveUp)
            {
                await AtomicFile.WriteAsync(
                    basePath + CaptureFileNames.FailedMarkerExtension,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new FailedMarker { CaptureId = CaptureIdFrom(candidate.BaseName), Attempts = attempts, FailedAtUtc = now, LastError = description, Permanent = false },
                        ProcessingMarkers.JsonOptions),
                    cancellationToken);
                TryDeleteMarker(basePath + CaptureFileNames.RetryMarkerExtension);
            }
            else
            {
                var delay = ComputeRetryDelay(attempts);
                await AtomicFile.WriteAsync(
                    basePath + CaptureFileNames.RetryMarkerExtension,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new RetryMarker { CaptureId = CaptureIdFrom(candidate.BaseName), Attempts = attempts, LastAttemptUtc = now, NextAttemptUtc = now + delay, LastError = description },
                        ProcessingMarkers.JsonOptions),
                    cancellationToken);
            }
        }
        catch (Exception markerError) when (RawCaptureStore.IsStorageFailure(markerError))
        {
            // 中文注释：连标记都写不了（磁盘满 / 权限），下一轮会按「上次尝试次数」再来一次，不会丢数据。
            _logger.LogError(markerError, "Face ingest processor could not write failure marker for capture={CaptureIdPrefix}.", CaptureIdFrom(candidate.BaseName)[..8]);
        }

        if (_failureLogGate.TryAcquire("process:" + error.GetType().Name, out var suppressed))
        {
            _logger.LogWarning(
                "Face ingest capture processing failed capture={CaptureIdPrefix} attempts={Attempts} maxAttempts={MaxAttempts} giveUp={GiveUp} error={Error} suppressedSinceLastLog={SuppressedCount}",
                CaptureIdFrom(candidate.BaseName)[..8],
                attempts,
                settings.ProcessingMaxAttempts,
                giveUp,
                description,
                suppressed);
        }

        return giveUp ? ItemOutcome.Failed : ItemOutcome.Retry;
    }

    private ItemOutcome FailPermanently(Candidate candidate, string basePath, int attempts, Exception error)
    {
        var description = DescribeError(error);
        try
        {
            File.WriteAllBytes(
                basePath + CaptureFileNames.FailedMarkerExtension + CaptureFileNames.TempExtension,
                JsonSerializer.SerializeToUtf8Bytes(
                    new FailedMarker { CaptureId = CaptureIdFrom(candidate.BaseName), Attempts = attempts, FailedAtUtc = _timeProvider.GetUtcNow(), LastError = description, Permanent = true },
                    ProcessingMarkers.JsonOptions));
            File.Move(
                basePath + CaptureFileNames.FailedMarkerExtension + CaptureFileNames.TempExtension,
                basePath + CaptureFileNames.FailedMarkerExtension,
                overwrite: true);
            TryDeleteMarker(basePath + CaptureFileNames.RetryMarkerExtension);
        }
        catch (Exception markerError) when (RawCaptureStore.IsStorageFailure(markerError))
        {
            _logger.LogError(markerError, "Face ingest processor could not write failure marker for capture={CaptureIdPrefix}.", CaptureIdFrom(candidate.BaseName)[..8]);
        }

        _logger.LogWarning(
            "Face ingest capture metadata unusable; marked failed without retry capture={CaptureIdPrefix} error={Error}",
            CaptureIdFrom(candidate.BaseName)[..8],
            description);
        return ItemOutcome.Failed;
    }

    /// <summary>指数退避：30 秒 × 2^(attempts-1)，单次最长 6 小时。</summary>
    internal static TimeSpan ComputeRetryDelay(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var seconds = RetryBaseDelay.TotalSeconds * Math.Pow(2, exponent);
        return seconds >= RetryMaxDelay.TotalSeconds ? RetryMaxDelay : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>确定性图片编号：SHA-256("{captureId}:{partIndex}") 前 16 字节的小写十六进制（32 位），重处理得到同一编号。</summary>
    internal static string BuildImageId(string captureId, int partIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{captureId}:{partIndex}")));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private void TryDeleteMarker(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (RawCaptureStore.IsStorageFailure(ex))
        {
            _logger.LogWarning(ex, "Face ingest processor could not delete marker {FileName}.", Path.GetFileName(path));
        }
    }

    /// <summary>错误摘要（类型 + 消息，截断 300 字）。消息来自 SqlClient / HttpClient / 本服务自己，不含 token、URL 签名或连接串。</summary>
    private static string DescribeError(Exception error)
    {
        var text = error.GetType().Name + ": " + error.Message.ReplaceLineEndings(" ");
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }

    private static string CaptureIdFrom(string baseName)
    {
        var underscore = baseName.LastIndexOf('_');
        var id = underscore >= 0 ? baseName[(underscore + 1)..] : baseName;
        return id.Length >= 8 ? id : id.PadRight(8, '?');
    }

    private static string? JoinNote(string first, string? rest)
        => string.IsNullOrEmpty(rest) ? first : first + "; " + rest;

    private static string? Truncate(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    [GeneratedRegex(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureIdPattern();

    private sealed record Candidate(string Directory, string BaseName, int PreviousAttempts);

    private sealed record RetryState(int Attempts, DateTimeOffset NextAttemptUtc);

    private enum ItemOutcome
    {
        Processed,
        Retry,
        Failed,
    }
}

/// <summary>一轮处理摘要（用于日志与测试断言）。</summary>
internal sealed record ProcessingSummary(int Candidates, int Processed, int Retried, int Failed);

/// <summary>
/// 后台处理服务：启动后每 ProcessingIntervalSeconds 秒跑一轮（周期与开关支持热更新）。
/// ProcessingEnabled=false 时空转，只接收落盘（P0 行为）。整轮异常只记 Error，不让服务崩溃。
/// </summary>
internal sealed class CaptureProcessingService : BackgroundService
{
    private readonly CaptureProcessor _processor;
    private readonly FaceIngestSettingsMonitor _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureProcessingService> _logger;

    public CaptureProcessingService(
        CaptureProcessor processor,
        FaceIngestSettingsMonitor settings,
        TimeProvider timeProvider,
        ILogger<CaptureProcessingService> logger)
    {
        _processor = processor;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation(
            "Face ingest processor started processingEnabled={ProcessingEnabled} database={DatabaseEnabled}",
            _settings.Current.ProcessingEnabled,
            _processor.DatabaseEnabled);
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settings.Current;
            if (settings.ProcessingEnabled)
            {
                try
                {
                    await _processor.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Face ingest processing round failed; will retry next interval.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.ProcessingIntervalSeconds)), _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
