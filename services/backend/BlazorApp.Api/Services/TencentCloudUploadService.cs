using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BlazorApp.Api.Models;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services
{
    public class TencentCloudUploadService
    {
        private readonly TencentCloudSettings _settings;
        private readonly ILogger<TencentCloudUploadService> _logger;
        private readonly HttpClient _httpClient;
        private readonly TimeProvider _timeProvider;
        private const int DefaultChunkSize = 40 * 1024 * 1024;

        public TencentCloudUploadService(
            IOptions<TencentCloudSettings> settings,
            ILogger<TencentCloudUploadService> logger,
            HttpClient httpClient,
            TimeProvider? timeProvider = null
        )
        {
            _settings = settings.Value;
            _logger = logger;
            _httpClient = httpClient;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public bool HasRequiredConfiguration()
        {
            return !string.IsNullOrWhiteSpace(_settings.SecretId)
                && !string.IsNullOrWhiteSpace(_settings.SecretKey)
                && !string.IsNullOrWhiteSpace(_settings.BucketName)
                && !string.IsNullOrWhiteSpace(_settings.Region);
        }

        public bool TryMatchPublicDownloadUrl(
            string? downloadUrl,
            string objectKey,
            out string normalizedDownloadUrl
        )
        {
            normalizedDownloadUrl = string.Empty;
            if (!HasRequiredConfiguration())
            {
                return false;
            }

            normalizedDownloadUrl = downloadUrl?.Trim() ?? string.Empty;
            if (
                !Uri.TryCreate(normalizedDownloadUrl, UriKind.Absolute, out var actualUri)
                || !Uri.TryCreate(GeneratePublicUrl(objectKey), UriKind.Absolute, out var expectedUri)
            )
            {
                return false;
            }

            var isMatched = string.Equals(
                    actualUri.Scheme,
                    expectedUri.Scheme,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    actualUri.Host,
                    expectedUri.Host,
                    StringComparison.OrdinalIgnoreCase
                )
                && actualUri.Port == expectedUri.Port
                && string.Equals(
                    Uri.UnescapeDataString(actualUri.AbsolutePath.TrimStart('/')),
                    Uri.UnescapeDataString(expectedUri.AbsolutePath.TrimStart('/')),
                    StringComparison.Ordinal
                );
            if (!isMatched)
            {
                return false;
            }

            // 中文注释：一旦确认 bucket/region/objectKey 命中，就只持久化公开下载地址，避免把临时签名 query 或 fragment 写入发布记录。
            normalizedDownloadUrl = expectedUri.GetLeftPart(UriPartial.Path);
            return true;
        }

        public virtual async Task<ApiResponse<CosObjectMetadata>> GetObjectMetadataAsync(
            string objectKey,
            CancellationToken cancellationToken = default
        )
        {
            if (!HasRequiredConfiguration())
            {
                return ApiResponse<CosObjectMetadata>.Error(
                    "COS 配置不完整",
                    "COS_NOT_CONFIGURED"
                );
            }

            try
            {
                // 私有证件照必须使用签名 HEAD，不能依赖 bucket 的公开读权限。
                using var request = new HttpRequestMessage(
                    HttpMethod.Head,
                    GeneratePresignedUrl(objectKey, "HEAD", 300)
                );
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                );

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return ApiResponse<CosObjectMetadata>.Error(
                        "COS 对象不存在",
                        "COS_OBJECT_NOT_FOUND"
                    );
                }

                if (!response.IsSuccessStatusCode)
                {
                    return ApiResponse<CosObjectMetadata>.Error(
                        $"COS 对象元数据校验失败，状态码：{(int)response.StatusCode}",
                        "COS_OBJECT_METADATA_UNAVAILABLE"
                    );
                }

                var sha256 = TryGetHeaderValue(response, "x-cos-meta-sha256");
                return ApiResponse<CosObjectMetadata>.OK(
                    new CosObjectMetadata
                    {
                        ContentLength = response.Content.Headers.ContentLength,
                        ContentType = response.Content.Headers.ContentType?.MediaType,
                        Sha256 = string.IsNullOrWhiteSpace(sha256)
                            ? null
                            : sha256.Trim().ToLowerInvariant(),
                        Owner = TryGetHeaderValue(response, "x-cos-meta-owner"),
                        Kind = TryGetHeaderValue(response, "x-cos-meta-kind"),
                        DeclaredContentType = TryGetHeaderValue(
                            response,
                            "x-cos-meta-content-type"
                        ),
                        DeclaredFileSize = long.TryParse(
                            TryGetHeaderValue(response, "x-cos-meta-file-size"),
                            out var declaredFileSize
                        )
                            ? declaredFileSize
                            : null,
                    }
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 COS 对象元数据失败，ObjectKey: {ObjectKey}", objectKey);
                return ApiResponse<CosObjectMetadata>.Error(
                    "COS 对象元数据校验失败",
                    "COS_OBJECT_METADATA_UNAVAILABLE",
                    ex.Message
                );
            }
        }

        private string GeneratePublicUrl(string objectKey)
        {
            return $"https://{_settings.BucketName}.cos.{_settings.Region}.myqcloud.com/{objectKey}";
        }

        public string GetPublicDownloadUrl(string objectKey) => GeneratePublicUrl(objectKey);

        public string GetSignedDownloadUrl(string objectKey, int expiresInSeconds = 300) =>
            GeneratePresignedUrl(objectKey, "GET", expiresInSeconds);

        public (string Url, DateTime ExpiresAtUtc) GetSignedDownload(
            string objectKey,
            int expiresInSeconds = 300
        ) => (
            GeneratePresignedUrl(objectKey, "GET", expiresInSeconds),
            _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(expiresInSeconds)
        );

        public bool TryGetPublicObjectKey(string? url, out string objectKey)
        {
            objectKey = string.Empty;
            if (
                !Uri.TryCreate(url, UriKind.Absolute, out var actual)
                || !Uri.TryCreate(GeneratePublicUrl("probe"), UriKind.Absolute, out var expected)
                || !string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }
            objectKey = Uri.UnescapeDataString(actual.AbsolutePath.TrimStart('/'));
            return !string.IsNullOrWhiteSpace(objectKey);
        }

        public virtual async Task<ApiResponse<byte[]>> DownloadObjectBytesAsync(
            string objectKey,
            int maximumBytes,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    GeneratePresignedUrl(objectKey, "GET", 300)
                );
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                );
                if (!response.IsSuccessStatusCode)
                {
                    return ApiResponse<byte[]>.Error("COS 图片读取失败", "COS_OBJECT_DOWNLOAD_FAILED");
                }
                if (response.Content.Headers.ContentLength > maximumBytes)
                {
                    return ApiResponse<byte[]>.Error("COS 图片超过允许大小", "COS_OBJECT_TOO_LARGE");
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    if (output.Length + read > maximumBytes)
                    {
                        return ApiResponse<byte[]>.Error("COS 图片超过允许大小", "COS_OBJECT_TOO_LARGE");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                return ApiResponse<byte[]>.OK(output.ToArray());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 COS 图片内容失败，ObjectKey: {ObjectKey}", objectKey);
                return ApiResponse<byte[]>.Error("COS 图片读取失败", "COS_OBJECT_DOWNLOAD_FAILED");
            }
        }

        public virtual async Task<ApiResponse<bool>> DeleteObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Delete,
                    GeneratePresignedUrl(objectKey, "DELETE", 300)
                );
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
                    ? ApiResponse<bool>.OK(true)
                    : ApiResponse<bool>.Error("COS 图片删除失败", "COS_OBJECT_DELETE_FAILED");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除 COS 图片失败，ObjectKey: {ObjectKey}", objectKey);
                return ApiResponse<bool>.Error("COS 图片删除失败", "COS_OBJECT_DELETE_FAILED");
            }
        }

        public virtual async Task<ApiResponse<bool>> PromoteObjectAsync(
            string sourceObjectKey,
            string targetObjectKey,
            string contentType,
            bool isPublic,
            CancellationToken cancellationToken = default
        )
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-cos-copy-source"] = $"/{_settings.BucketName}/{sourceObjectKey}",
                ["x-cos-acl"] = isPublic ? "public-read" : "private",
            };
            var signature = GetDirectUploadSignature(targetObjectKey, contentType, 300, headers);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, signature.Url)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode
                    ? ApiResponse<bool>.OK(true)
                    : ApiResponse<bool>.Error("COS 图片转正失败", "COS_OBJECT_PROMOTE_FAILED");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "COS 图片转正失败，Source: {Source}, Target: {Target}", sourceObjectKey, targetObjectKey);
                return ApiResponse<bool>.Error("COS 图片转正失败", "COS_OBJECT_PROMOTE_FAILED");
            }
        }

        private static string? TryGetHeaderValue(HttpResponseMessage response, string headerName)
        {
            if (response.Headers.TryGetValues(headerName, out var headerValues))
            {
                return headerValues.FirstOrDefault();
            }

            return response.Content.Headers.TryGetValues(headerName, out var contentHeaderValues)
                ? contentHeaderValues.FirstOrDefault()
                : null;
        }

        private string ExtractUploadIdFromResponse(string response)
        {
            try
            {
                var doc = XDocument.Parse(response);
                var uploadIdElement = doc.Descendants("UploadId").FirstOrDefault();
                return uploadIdElement?.Value ?? string.Empty;
            }
            catch
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    response,
                    "<UploadId>(.*?)</UploadId>"
                );
                return match.Success ? match.Groups[1].Value : string.Empty;
            }
        }

        private static string HmacSha1(string key, string data)
        {
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hash).ToLower();
        }

        private static string Sha1Hex(string input)
        {
            using var sha1 = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha1.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLower();
        }

        #region 前端直传签名方法

        public DirectUploadSignature GetDirectUploadSignature(
            string objectKey,
            string contentType,
            long expiresInSeconds = 3600,
            IReadOnlyDictionary<string, string>? additionalHeaders = null
        )
        {
            var uploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = contentType,
            };
            if (additionalHeaders is not null)
            {
                foreach (var header in additionalHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                    {
                        uploadHeaders[header.Key.Trim()] = header.Value.Trim();
                    }
                }
            }

            var url = GeneratePresignedPutUrl(
                objectKey,
                contentType,
                expiresInSeconds,
                signedHeaders: uploadHeaders
            );
            return new DirectUploadSignature
            {
                Url = url,
                ObjectKey = objectKey,
                Headers = uploadHeaders,
            };
        }

        public List<PresignedUrlItem> GenerateBatchPresignedPutUrls(
            List<PresignedFileInfo> files,
            string directory = "YW200",
            int maxUrls = 1000,
            string? bucketName = null,
            string? region = null
        )
        {
            var bucket = bucketName ?? _settings.BucketName;
            var reg = region ?? _settings.Region;
            var results = new List<PresignedUrlItem>();
            var count = 0;

            foreach (var file in files)
            {
                if (count >= maxUrls)
                {
                    results.Add(new PresignedUrlItem
                    {
                        FileName = file.FileName,
                        FileSize = file.FileSize,
                        Error = $"超出最大文件数量限制（{maxUrls}个）"
                    });
                    continue;
                }

                try
                {
                    // 统一转为 jpg 后缀
                    var baseName = Path.GetFileNameWithoutExtension(file.FileName);
                    var objectKey = $"{directory}/{baseName}.jpg";
                    var host = $"{bucket}.cos.{reg}.myqcloud.com";
                    var url = GeneratePresignedPutUrl(objectKey, "image/jpeg", 7200, bucket, reg);
                    var downloadUrl = $"https://{host}/{objectKey}";

                    results.Add(new PresignedUrlItem
                    {
                        ObjectKey = objectKey,
                        PresignedUrl = url,
                        DownloadUrl = downloadUrl,
                        FileName = file.FileName,
                        FileSize = file.FileSize,
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new PresignedUrlItem
                    {
                        FileName = file.FileName,
                        FileSize = file.FileSize,
                        Error = ex.Message
                    });
                }

                count++;
            }

            return results;
        }

        public async Task<ApiResponse<MultipartUploadInit>> InitiateMultipartUploadForClientAsync(
            string objectKey,
            string contentType,
            long fileSize,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                var uploadId = await InitiateMultipartUploadAsync(
                    objectKey,
                    contentType,
                    cancellationToken
                );
                var chunkSize = DefaultChunkSize;
                var totalChunks = (int)Math.Ceiling((double)fileSize / chunkSize);

                return ApiResponse<MultipartUploadInit>.OK(
                    new MultipartUploadInit
                    {
                        UploadId = uploadId,
                        ObjectKey = objectKey,
                        ChunkSize = chunkSize,
                        TotalChunks = totalChunks,
                    },
                    "分块上传初始化成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化分块上传失败，ObjectKey: {ObjectKey}", objectKey);
                return ApiResponse<MultipartUploadInit>.Error(
                    "初始化分块上传失败",
                    "INIT_MULTIPART_FAILED",
                    ex.Message
                );
            }
        }

        public PartUploadSignature GetPartUploadSignature(
            string objectKey,
            string uploadId,
            int partNumber,
            int expiresInSeconds = 3600
        )
        {
            var url = GeneratePresignedUrl(
                objectKey,
                "PUT",
                expiresInSeconds,
                uploadId,
                partNumber
            );
            return new PartUploadSignature { Url = url, PartNumber = partNumber };
        }

        public async Task<ApiResponse<UploadResult>> CompleteMultipartUploadForClientAsync(
            string objectKey,
            string uploadId,
            List<PartETag> parts,
            long fileSize,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                var etagList = parts
                    .OrderBy(p => p.PartNumber)
                    .Select(p => (p.PartNumber, p.ETag))
                    .ToList();

                await CompleteMultipartUploadAsync(
                    uploadId,
                    objectKey,
                    etagList,
                    cancellationToken
                );

                var downloadUrl = GeneratePublicUrl(objectKey);
                return ApiResponse<UploadResult>.OK(
                    new UploadResult
                    {
                        ObjectKey = objectKey,
                        DownloadUrl = downloadUrl,
                        FileSize = fileSize,
                    },
                    "文件上传成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "完成分块上传失败，ObjectKey: {ObjectKey}, UploadId: {UploadId}",
                    objectKey,
                    uploadId
                );
                return ApiResponse<UploadResult>.Error(
                    "完成分块上传失败",
                    "COMPLETE_MULTIPART_FAILED",
                    ex.Message
                );
            }
        }

        public async Task<ApiResponse<UploadResult>> UploadStreamAsync(
            string objectKey,
            string contentType,
            Stream content,
            long? fileSize = null,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.SecretId)
                    || string.IsNullOrWhiteSpace(_settings.SecretKey)
                    || string.IsNullOrWhiteSpace(_settings.BucketName)
                    || string.IsNullOrWhiteSpace(_settings.Region))
                {
                    return ApiResponse<UploadResult>.Error("腾讯云主桶配置不完整", "COS_NOT_CONFIGURED");
                }

                var url = GeneratePresignedPutUrl(objectKey, contentType, 3600);
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Content = new StreamContent(content);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                if (fileSize is > 0)
                {
                    request.Content.Headers.ContentLength = fileSize.Value;
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var downloadUrl = GeneratePublicUrl(objectKey);
                return ApiResponse<UploadResult>.OK(
                    new UploadResult
                    {
                        ObjectKey = objectKey,
                        DownloadUrl = downloadUrl,
                        FileSize = fileSize ?? (content.CanSeek ? content.Length : 0),
                    },
                    "文件上传成功"
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 关键位置：后台任务停机取消必须向上传递，不能被包装成 COS_UPLOAD_FAILED 后回写为镜像失败。
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "服务端上传文件到 COS 失败，ObjectKey: {ObjectKey}", objectKey);
                return ApiResponse<UploadResult>.Error(
                    "服务端上传文件到 COS 失败",
                    "COS_UPLOAD_FAILED",
                    ex.Message
                );
            }
        }

        /// <summary>商品图片桶（ImageBucketName / ImageRegion）是否配置齐全；该桶已开通数据万象，前端可用 imageMogr2 取缩略图。</summary>
        public bool HasImageBucketConfiguration() =>
            !string.IsNullOrWhiteSpace(_settings.SecretId)
            && !string.IsNullOrWhiteSpace(_settings.SecretKey)
            && !string.IsNullOrWhiteSpace(_settings.ImageBucketName)
            && !string.IsNullOrWhiteSpace(_settings.ImageRegion);

        /// <summary>
        /// 服务端把一张图片写进商品图片桶，返回公开地址。与 UploadStreamAsync（写主桶）分开，不影响现有调用方。
        /// </summary>
        public async Task<ApiResponse<UploadResult>> UploadImageBucketBytesAsync(
            string objectKey,
            string contentType,
            byte[] content,
            CancellationToken cancellationToken = default
        )
        {
            if (!HasImageBucketConfiguration())
            {
                return ApiResponse<UploadResult>.Error("腾讯云图片桶配置不完整", "COS_NOT_CONFIGURED");
            }

            try
            {
                var url = GeneratePresignedPutUrl(objectKey, contentType, 3600, _settings.ImageBucketName, _settings.ImageRegion);
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Content = new ByteArrayContent(content);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return ApiResponse<UploadResult>.OK(
                    new UploadResult
                    {
                        ObjectKey = objectKey,
                        DownloadUrl = $"https://{_settings.ImageBucketName}.cos.{_settings.ImageRegion}.myqcloud.com/{objectKey}",
                        FileSize = content.Length,
                    },
                    "文件上传成功"
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "服务端上传图片到 COS 图片桶失败，ObjectKey: {ObjectKey}", objectKey);
                return ApiResponse<UploadResult>.Error("服务端上传图片到 COS 失败", "COS_UPLOAD_FAILED", ex.Message);
            }
        }

        private string GeneratePresignedPutUrl(
            string objectKey,
            string contentType,
            long expiresInSeconds,
            string? bucketName = null,
            string? region = null,
            IReadOnlyDictionary<string, string>? signedHeaders = null
        )
        {
            var bucket = bucketName ?? _settings.BucketName;
            var regionValue = region ?? _settings.Region;
            var secretId = _settings.SecretId;
            var secretKey = _settings.SecretKey;

            var host = $"{bucket}.cos.{regionValue}.myqcloud.com";
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var startTimestamp = new DateTimeOffset(now).ToUnixTimeSeconds();
            var expiredTimestamp = new DateTimeOffset(
                now.AddSeconds(expiresInSeconds)
            ).ToUnixTimeSeconds();

            var keyTime = $"{startTimestamp};{expiredTimestamp}";
            var path = BuildCosCanonicalPath(objectKey);
            var headersToSign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = contentType,
                ["host"] = host,
            };
            if (signedHeaders is not null)
            {
                foreach (var header in signedHeaders)
                {
                    if (
                        !string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(header.Key)
                        && !string.IsNullOrWhiteSpace(header.Value)
                    )
                    {
                        headersToSign[header.Key.Trim()] = header.Value.Trim();
                    }
                }
            }

            var (headerList, httpHeaders) = BuildCosHeaderSignatureParts(headersToSign);
            var authorization = GenerateCosAuthorization(
                "put",
                path,
                "",
                httpHeaders,
                keyTime,
                secretId,
                secretKey,
                headerList,
                ""
            );

            return $"https://{host}{BuildCosUrlPath(objectKey)}?{BuildCosAuthorizationQuery(authorization)}";
        }

        #endregion

        #region 内部辅助方法

        private async Task<string> InitiateMultipartUploadAsync(
            string objectKey,
            string contentType,
            CancellationToken cancellationToken
        )
        {
            var bucket = _settings.BucketName;
            var region = _settings.Region;
            var host = $"{bucket}.cos.{region}.myqcloud.com";
            var url = GeneratePresignedUrl(objectKey, "POST", 3600, multipart: true);

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Host", host);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var uploadId = ExtractUploadIdFromResponse(responseBody);

            _logger.LogInformation(
                "初始化分块上传成功，uploadId: {UploadId}, objectKey: {ObjectKey}",
                uploadId,
                objectKey
            );

            return uploadId;
        }

        private async Task CompleteMultipartUploadAsync(
            string uploadId,
            string objectKey,
            List<(int PartNumber, string Etag)> etags,
            CancellationToken cancellationToken
        )
        {
            var url = GeneratePresignedUrl(
                objectKey,
                "POST",
                3600,
                uploadId: uploadId,
                complete: true
            );

            var partsXml = new StringBuilder();
            partsXml.Append("<CompleteMultipartUpload>");
            foreach (var (partNumber, etag) in etags.OrderBy(e => e.PartNumber))
            {
                partsXml.Append(
                    $"<Part><PartNumber>{partNumber}</PartNumber><ETag>{etag}</ETag></Part>"
                );
            }
            partsXml.Append("</CompleteMultipartUpload>");

            var content = new StringContent(partsXml.ToString(), Encoding.UTF8, "application/xml");
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private string GeneratePresignedUrl(
            string objectKey,
            string method,
            int expiresInSeconds,
            string? uploadId = null,
            int partNumber = 0,
            bool multipart = false,
            bool complete = false
        )
        {
            var bucket = _settings.BucketName;
            var region = _settings.Region;
            var secretId = _settings.SecretId;
            var secretKey = _settings.SecretKey;

            var host = $"{bucket}.cos.{region}.myqcloud.com";
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var startTimestamp = new DateTimeOffset(now).ToUnixTimeSeconds();
            var expiredTimestamp = new DateTimeOffset(
                now.AddSeconds(expiresInSeconds)
            ).ToUnixTimeSeconds();
            var keyTime = $"{startTimestamp};{expiredTimestamp}";
            var operationParameters = new List<(string Key, string Value)>();

            if (multipart)
            {
                operationParameters.Add(("uploads", ""));
            }
            else if (!string.IsNullOrEmpty(uploadId))
            {
                if (partNumber > 0)
                {
                    operationParameters.Add(("partNumber", partNumber.ToString()));
                    operationParameters.Add(("uploadId", uploadId));
                }
                else
                {
                    operationParameters.Add(("uploadId", uploadId));
                }
            }

            var path = BuildCosCanonicalPath(objectKey);
            var canonicalParameters = BuildCosCanonicalQueryParameters(operationParameters);
            var urlParamList = BuildCosQueryParameterList(operationParameters);
            var (headerList, httpHeaders) = BuildCosHeaderSignatureParts(
                new Dictionary<string, string> { ["host"] = host }
            );
            var authorization = GenerateCosAuthorization(
                method,
                path,
                canonicalParameters,
                httpHeaders,
                keyTime,
                secretId,
                secretKey,
                headerList,
                urlParamList
            );
            var queryParts = new List<string>();
            var operationQuery = BuildCosActualQueryParameters(operationParameters);
            if (!string.IsNullOrEmpty(operationQuery))
            {
                queryParts.Add(operationQuery);
            }
            queryParts.Add(BuildCosAuthorizationQuery(authorization));

            return $"https://{host}{BuildCosUrlPath(objectKey)}?{string.Join("&", queryParts)}";
        }

        private static string GenerateCosAuthorization(
            string method,
            string canonicalPath,
            string canonicalParameters,
            string canonicalHeaders,
            string keyTime,
            string secretId,
            string secretKey,
            string headerList,
            string urlParamList
        )
        {
            // 腾讯云 COS XML API 签名只支持 sha1；这里和官方 HttpString 格式保持一致。
            var httpString =
                $"{method.ToLowerInvariant()}\n{canonicalPath}\n{canonicalParameters}\n{canonicalHeaders}\n";
            var stringToSign = $"sha1\n{keyTime}\n{Sha1Hex(httpString)}\n";
            var signKey = HmacSha1(secretKey, keyTime);
            var signature = HmacSha1(signKey, stringToSign);

            return string.Join(
                "&",
                "q-sign-algorithm=sha1",
                $"q-ak={secretId}",
                $"q-sign-time={keyTime}",
                $"q-key-time={keyTime}",
                $"q-header-list={headerList}",
                $"q-url-param-list={urlParamList}",
                $"q-signature={signature}"
            );
        }

        private static string BuildCosAuthorizationQuery(string authorization)
        {
            return string.Join(
                "&",
                authorization
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part =>
                    {
                        var index = part.IndexOf('=');
                        if (index < 0)
                        {
                            return CosUrlEncode(part);
                        }

                        var key = part[..index];
                        var value = part[(index + 1)..];
                        return $"{CosUrlEncode(key)}={CosUrlEncode(value)}";
                    })
            );
        }

        private static (string HeaderList, string HttpHeaders) BuildCosHeaderSignatureParts(
            IReadOnlyDictionary<string, string> headers
        )
        {
            var normalized = headers
                .Select(pair => new
                {
                    Key = CosUrlEncode(pair.Key.Trim().ToLowerInvariant()),
                    Value = CosUrlEncode(pair.Value.Trim()),
                })
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();

            return (
                string.Join(";", normalized.Select(pair => pair.Key)),
                string.Join("&", normalized.Select(pair => $"{pair.Key}={pair.Value}"))
            );
        }

        private static string BuildCosCanonicalQueryParameters(
            IReadOnlyCollection<(string Key, string Value)> parameters
        )
        {
            if (parameters.Count == 0)
            {
                return "";
            }

            return string.Join(
                "&",
                parameters
                    .Select(pair => new
                    {
                        Key = CosUrlEncode(pair.Key.ToLowerInvariant()),
                        Value = CosUrlEncode(pair.Value),
                    })
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}")
            );
        }

        private static string BuildCosQueryParameterList(
            IReadOnlyCollection<(string Key, string Value)> parameters
        )
        {
            return parameters.Count == 0
                ? ""
                : string.Join(
                    ";",
                    parameters
                        .Select(pair => CosUrlEncode(pair.Key.ToLowerInvariant()))
                        .Order(StringComparer.Ordinal)
                );
        }

        private static string BuildCosActualQueryParameters(
            IReadOnlyCollection<(string Key, string Value)> parameters
        )
        {
            return parameters.Count == 0
                ? ""
                : string.Join(
                    "&",
                    parameters.Select(pair => $"{CosUrlEncode(pair.Key)}={CosUrlEncode(pair.Value)}")
                );
        }

        private static string BuildCosCanonicalPath(string objectKey)
        {
            return "/" + objectKey.TrimStart('/');
        }

        private static string BuildCosUrlPath(string objectKey)
        {
            return "/"
                + string.Join(
                    "/",
                    objectKey
                        .TrimStart('/')
                        .Split('/', StringSplitOptions.None)
                        .Select(CosUrlEncode)
                );
        }

        private static string CosUrlEncode(string value)
        {
            return Uri.EscapeDataString(value);
        }

        #endregion

        public sealed class CosObjectMetadata
        {
            public long? ContentLength { get; set; }

            public string? ContentType { get; set; }

            public string? Sha256 { get; set; }

            public string? Owner { get; set; }

            public string? Kind { get; set; }

            public string? DeclaredContentType { get; set; }

            public long? DeclaredFileSize { get; set; }
        }
    }
}
