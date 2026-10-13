using System.Globalization;

namespace BlazorApp.FaceIngest;

/// <summary>一张待存图片：<see cref="Data"/> 是原始报文的切片，<see cref="Sha256"/> 为小写十六进制。</summary>
internal sealed record ImageWriteRequest(
    string CaptureId,
    string CameraId,
    string StoreCode,
    DateTimeOffset ReceivedAtUtc,
    int PartIndex,
    ReadOnlyMemory<byte> Data,
    string ContentType,
    string Sha256);

/// <summary>一条待归档的原始报文（只有 COS 开启且 UploadRawBody=true 时才上传）。</summary>
internal sealed record RawBodyWriteRequest(
    string CaptureId,
    string CameraId,
    string StoreCode,
    DateTimeOffset ReceivedAtUtc,
    ReadOnlyMemory<byte> Data,
    string Sha256);

/// <summary>存储结果：<see cref="Storage"/> 为 <c>local</c> | <c>cos</c>，<see cref="ObjectKey"/> 为本地相对路径或 COS 对象键。</summary>
internal sealed record StoredObject(string Storage, string ObjectKey);

/// <summary>
/// 图片（与可选的原始报文）存储。失败一律抛异常，由处理器按退避重试；同一图片重存是覆盖写（键确定），天然幂等。
/// </summary>
internal interface IImageStore
{
    /// <summary>存储名：<c>local</c> | <c>cos</c>（写进日志与标记）。</summary>
    string StorageName { get; }

    Task<StoredObject> SaveImageAsync(ImageWriteRequest request, CancellationToken cancellationToken);

    /// <summary>归档原始报文；不归档（原始报文只留本地 raw/）时返回 null。</summary>
    Task<StoredObject?> SaveRawBodyAsync(RawBodyWriteRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 本地图片存储（COS 关闭时）：<c>images/{yyyy-MM-dd}/{cameraId}/{captureId}_{partIndex}.jpg</c>，
/// 先写 .tmp 再改名（与 P0 落盘同一原子写法）；ObjectKey 为相对存储根目录的路径（/ 分隔）。
/// 关键逻辑：图片与原始报文在同一块盘上，写之前复用 MinFreeDiskBytes 护栏——剩余空间不足时抛异常走重试，
/// 不能为了存图把盘写满、反过来让接收端点 503。图片按 RetentionDays 由清理任务删除。
/// </summary>
internal sealed class LocalImageStore : IImageStore
{
    public const string Name = "local";

    private readonly RawCaptureStore _store;
    private readonly FaceIngestSettingsMonitor _settings;
    private readonly IDiskSpaceProbe _diskSpaceProbe;

    public LocalImageStore(RawCaptureStore store, FaceIngestSettingsMonitor settings, IDiskSpaceProbe diskSpaceProbe)
    {
        _store = store;
        _settings = settings;
        _diskSpaceProbe = diskSpaceProbe;
    }

    public string StorageName => Name;

    public async Task<StoredObject> SaveImageAsync(ImageWriteRequest request, CancellationToken cancellationToken)
    {
        var minFree = _settings.Current.MinFreeDiskBytes;
        if (minFree > 0 && _diskSpaceProbe.GetAvailableFreeBytes(_store.RootPath) is long free && free < minFree)
        {
            throw new IOException("Free disk space is below FaceIngest:MinFreeDiskBytes; local image write postponed.");
        }

        var relativeDirectory = string.Join(
            '/',
            CaptureFileNames.ImagesDirectoryName,
            CaptureFileNames.FormatDateDirectory(request.ReceivedAtUtc),
            request.CameraId);
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{request.CaptureId}_{request.PartIndex}{CaptureFileNames.ImageExtension}");
        var directory = Path.Combine(_store.RootPath, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        await AtomicFile.WriteAsync(path, request.Data, cancellationToken);
        return new StoredObject(Name, relativeDirectory + "/" + fileName);
    }

    public Task<StoredObject?> SaveRawBodyAsync(RawBodyWriteRequest request, CancellationToken cancellationToken)
        => Task.FromResult<StoredObject?>(null);
}

/// <summary>
/// 私有 COS 存储（<c>FaceIngest:Cos:Enabled=true</c> 且四个键齐全时启用）。业务口径：
/// - PUT <c>https://{bucket}.cos.{region}.myqcloud.com/{key}</c>，签名里包含并实际发送
///   <c>x-cos-acl: private</c>、<c>x-cos-meta-sha256</c>、<c>x-cos-meta-camera</c>、<c>x-cos-meta-store</c>（不依赖桶默认权限）；
/// - 图片键 <c>{KeyPrefix}/images/{storeCode}/{yyyy}/{MM}/{dd}/{cameraId}/{captureId}_{partIndex}.jpg</c>，日期取 UTC 接收时间；
///   原始报文（可选）<c>{KeyPrefix}/raw/{storeCode}/{yyyy}/{MM}/{dd}/{cameraId}/{captureId}.bin</c>；
/// - 成功判定：HTTP 200 且响应带 ETag；否则抛 <see cref="CosUploadException"/> 走重试；
/// - 异常信息与日志里不出现预签名 URL（含 q-ak）与密钥。
/// </summary>
internal sealed class CosImageStore : IImageStore, IUnwelcomeHitImageStore
{
    public const string Name = "cos";

    /// <summary>IHttpClientFactory 里的命名客户端（超时 30 秒，在 Program 里登记）。</summary>
    public const string HttpClientName = "face-ingest-cos";

    private const long PresignExpiresSeconds = 300;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CosRequestSigner _signer;
    private readonly CosSettings _cos;

    public CosImageStore(IHttpClientFactory httpClientFactory, FaceIngestSettingsMonitor settings, TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _cos = settings.Current.Cos;
        if (!_cos.Enabled)
        {
            throw new InvalidOperationException("CosImageStore requires FaceIngest:Cos:Enabled=true.");
        }

        _signer = new CosRequestSigner(_cos.Bucket, _cos.Region, _cos.SecretId, _cos.SecretKey, timeProvider);
    }

    public string StorageName => Name;

    public async Task<StoredObject> SaveImageAsync(ImageWriteRequest request, CancellationToken cancellationToken)
    {
        var key = BuildKey(
            "images",
            request.StoreCode,
            request.ReceivedAtUtc,
            request.CameraId,
            string.Create(CultureInfo.InvariantCulture, $"{request.CaptureId}_{request.PartIndex}{CaptureFileNames.ImageExtension}"));
        await PutAsync(key, "images", "image/jpeg", request.Data, request.Sha256, request.CameraId, request.StoreCode, cancellationToken);
        return new StoredObject(Name, key);
    }

    public async Task<StoredObject?> SaveRawBodyAsync(RawBodyWriteRequest request, CancellationToken cancellationToken)
    {
        if (!_cos.UploadRawBody)
        {
            return null;
        }

        var key = BuildKey("raw", request.StoreCode, request.ReceivedAtUtc, request.CameraId, request.CaptureId + CaptureFileNames.BodyExtension);
        await PutAsync(key, "raw", "application/octet-stream", request.Data, request.Sha256, request.CameraId, request.StoreCode, cancellationToken);
        return new StoredObject(Name, key);
    }

    /// <summary>COS 已启用即可存名单命中图（本类只有 COS 启用时才会被构造）。</summary>
    public bool Available => true;

    /// <summary>
    /// 保存不受欢迎人员名单命中图（设计第 14.3 节）：键 <c>{KeyPrefix}/unwelcome-hits/{storeCode}/{yyyy}/{MM}/{dd}/{cameraId}/{taskId}.jpg</c>，
    /// 仍是私有 ACL；TaskId 由抓拍编号确定性生成，重试覆盖写同一个键，幂等。
    /// </summary>
    public async Task<string> SaveAsync(UnwelcomeHitImage image, CancellationToken cancellationToken)
    {
        var key = BuildKey("unwelcome-hits", image.StoreCode, image.CapturedAtUtc, image.CameraId, image.TaskId + CaptureFileNames.ImageExtension);
        await PutAsync(key, "unwelcome-hits", "image/jpeg", image.Data, image.Sha256, image.CameraId, image.StoreCode, cancellationToken);
        return key;
    }

    /// <summary>对象键；各段都已在上游校验（门店 / 摄像机编号只含安全字符，captureId 为 32 位十六进制）。</summary>
    internal string BuildKey(string kind, string storeCode, DateTimeOffset receivedAtUtc, string cameraId, string fileName)
    {
        var utc = receivedAtUtc.UtcDateTime;
        return string.Join(
            '/',
            _cos.KeyPrefix,
            kind,
            storeCode,
            utc.ToString("yyyy", CultureInfo.InvariantCulture),
            utc.ToString("MM", CultureInfo.InvariantCulture),
            utc.ToString("dd", CultureInfo.InvariantCulture),
            cameraId,
            fileName);
    }

    private async Task PutAsync(
        string key,
        string kind,
        string contentType,
        ReadOnlyMemory<byte> data,
        string sha256,
        string cameraId,
        string storeCode,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：这些头既参与签名又随请求发送，桶默认权限是什么都不影响对象私有。
        var signedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-cos-acl"] = "private",
            ["x-cos-meta-sha256"] = sha256,
            ["x-cos-meta-camera"] = cameraId,
            ["x-cos-meta-store"] = storeCode,
        };
        var url = _signer.GeneratePresignedPutUrl(key, contentType, PresignExpiresSeconds, signedHeaders);
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new ReadOnlyMemoryContent(data);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        foreach (var header in signedHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var hasEtag = response.Headers.ETag is not null || response.Headers.TryGetValues("ETag", out _);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || !hasEtag)
        {
            throw new CosUploadException(string.Create(
                CultureInfo.InvariantCulture,
                $"COS PUT failed status={(int)response.StatusCode} etag={(hasEtag ? "present" : "missing")} kind={kind}"));
        }
    }
}

/// <summary>COS 上传失败（状态码非 200 或缺 ETag）。信息里只有状态码与对象类别，不含 URL / 密钥。</summary>
internal sealed class CosUploadException(string message) : Exception(message);

/// <summary>原子写小文件：先写同目录 .tmp 再改名覆盖（与 P0 落盘同一写法），半截文件不会以正式名出现。</summary>
internal static class AtomicFile
{
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var tempPath = path + CaptureFileNames.TempExtension;
        try
        {
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            }))
            {
                await stream.WriteAsync(data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanup) when (RawCaptureStore.IsStorageFailure(cleanup))
            {
                // 残片由清理任务按 1 小时规则兜底删除。
            }

            throw;
        }
    }
}
