using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public interface ICatalogFileDownloader
{
    /// <summary>
    /// 下载并校验目录文件，返回本地已校验文件的路径。中断后再次调用会从已下载的字节续传；
    /// 字节数或 SHA-256 不符的文件绝不返回。
    /// </summary>
    Task<string> DownloadAsync(
        CatalogDownloadFileDto file,
        IProgress<long>? bytesDownloaded,
        CancellationToken cancellationToken);

    /// <summary>文件已写入本地库后删除，释放磁盘。</summary>
    void Release(string verifiedPath);
}

/// <summary>
/// 目录文件下载器：残片 {sha256}.part 在缓存目录里跨重试、跨重启保留，用 Range + If-Range 续传；
/// 下完核对字节数与 SHA-256 后改名为 {sha256}.ndjson.gz。文件名就是内容哈希，同名即同内容。
/// </summary>
public sealed class CatalogFileDownloader : ICatalogFileDownloader
{
    internal static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private const string PartSuffix = ".part";
    private const string VerifiedSuffix = ".ndjson.gz";
    private readonly ICatalogApiClient _catalogApiClient;
    private readonly string _cacheDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public CatalogFileDownloader(ICatalogApiClient catalogApiClient, string cacheDirectory)
        : this(catalogApiClient, cacheDirectory, Task.Delay)
    {
    }

    internal CatalogFileDownloader(
        ICatalogApiClient catalogApiClient,
        string cacheDirectory,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _catalogApiClient = catalogApiClient;
        _cacheDirectory = cacheDirectory;
        _delayAsync = delayAsync;
    }

    public async Task<string> DownloadAsync(
        CatalogDownloadFileDto file,
        IProgress<long>? bytesDownloaded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sha256 = NormalizeSha256(file.Sha256);
        if (file.Bytes < 0 || string.IsNullOrWhiteSpace(file.Path))
        {
            throw new CatalogApiException("Catalog file descriptor is invalid.", HttpStatusCode.OK, "CATALOG_FILE_INVALID");
        }

        Directory.CreateDirectory(_cacheDirectory);
        var verifiedPath = Path.Combine(_cacheDirectory, sha256 + VerifiedSuffix);
        var partPath = Path.Combine(_cacheDirectory, sha256 + PartSuffix);
        RemoveOtherFiles(sha256);
        if (File.Exists(verifiedPath))
        {
            if (await MatchesAsync(verifiedPath, file.Bytes, sha256, cancellationToken))
            {
                bytesDownloaded?.Report(file.Bytes);
                Log($"file reused kind={file.Kind} sha256={sha256} bytes={file.Bytes}");
                return verifiedPath;
            }

            TryDelete(verifiedPath);
        }

        // 残片损坏或范围被拒时只允许从头重下一次，避免反复下载同一个坏文件。
        var restartedFromScratch = false;
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadRemainingAsync(file, sha256, partPath, bytesDownloaded, cancellationToken);
            }
            catch (CatalogApiException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable ||
                                                 ex.ErrorCode == "CATALOG_FILE_RANGE_INVALID")
            {
                // 残片与服务端文件对不上，丢弃后从头下载一次。
                TryDelete(partPath);
                if (restartedFromScratch)
                {
                    throw;
                }

                Log($"download range rejected kind={file.Kind} sha256={sha256} restart=true");
                restartedFromScratch = true;
                continue;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken) && attempt < RetryDelays.Length)
            {
                // 中文注释：断网、网关错误、读超时都保留残片，等一会儿从断点继续。
                var delay = RetryDelays[attempt++];
                Log($"download retry kind={file.Kind} sha256={sha256} attempt={attempt + 1} downloaded={GetLength(partPath)}/{file.Bytes} delayMs={delay.TotalMilliseconds:0} error={ex.GetType().Name}: {ex.Message}");
                await _delayAsync(delay, cancellationToken);
                continue;
            }

            if (await MatchesAsync(partPath, file.Bytes, sha256, cancellationToken))
            {
                File.Move(partPath, verifiedPath, overwrite: true);
                Log($"file verified kind={file.Kind} sha256={sha256} bytes={file.Bytes}");
                return verifiedPath;
            }

            // 中文注释：整份校验不过说明残片被污染或中途换了内容，删掉从头再下一次；仍不对就放弃，交给分页协议。
            TryDelete(partPath);
            if (restartedFromScratch)
            {
                throw new CatalogApiException(
                    $"Catalog file checksum does not match. expected={sha256}",
                    HttpStatusCode.OK,
                    "CATALOG_FILE_CHECKSUM_MISMATCH");
            }

            Log($"download checksum mismatch kind={file.Kind} sha256={sha256} restart=true");
            restartedFromScratch = true;
        }
    }

    public void Release(string verifiedPath)
    {
        TryDelete(verifiedPath);
    }

    private async Task DownloadRemainingAsync(
        CatalogDownloadFileDto file,
        string sha256,
        string partPath,
        IProgress<long>? bytesDownloaded,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 64 * 1024, useAsync: true);
        var offset = stream.Length;
        if (offset > file.Bytes)
        {
            stream.SetLength(0);
            offset = 0;
        }

        bytesDownloaded?.Report(offset);
        if (offset == file.Bytes && offset > 0)
        {
            return;
        }

        stream.Position = offset;
        var result = await _catalogApiClient.DownloadCatalogFileRangeAsync(
            file.Path,
            stream,
            offset,
            $"\"{sha256}\"",
            bytesDownloaded is null ? null : new SynchronousProgress(written => bytesDownloaded.Report(offset + written)),
            cancellationToken);
        if (result.Restarted && offset > 0)
        {
            Log($"download restarted kind={file.Kind} sha256={sha256} discardedBytes={offset}");
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            TimeoutException or IOException or HttpRequestException => true,
            // 调用方未取消时的取消异常来自连接层超时，同样按瞬时故障续传。
            OperationCanceledException => true,
            CatalogApiException apiException => apiException.StatusCode is
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
            _ => false
        };
    }

    private static async Task<bool> MatchesAsync(
        string path,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedBytes)
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveOtherFiles(string currentSha256)
    {
        // 中文注释：缓存目录只留当前文件的残片/成品，旧版本残片不再有用，及时清掉。
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory))
            {
                if (!Path.GetFileName(path).StartsWith(currentSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"cache cleanup skipped error={ex.GetType().Name}");
        }
    }

    private static string NormalizeSha256(string? sha256)
    {
        var value = (sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            // 文件名直接取哈希，格式不对的值一律拒绝，避免路径注入。
            throw new CatalogApiException("Catalog file SHA-256 is invalid.", HttpStatusCode.OK, "CATALOG_FILE_INVALID");
        }

        return value;
    }

    private static long GetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"delete failed error={ex.GetType().Name}");
        }
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("CatalogFile", message);
    }

    /// <summary>直接在写入线程上报进度；Progress&lt;T&gt; 会切到同步上下文，下载线程上不需要。</summary>
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

/// <summary>
/// 流式读取目录文件：边解压边逐行反序列化，内存只占一个批次。头与条数和同步计划不一致时抛出，不写入本地库。
/// </summary>
public static class CatalogFileReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> ReadFullAsync(
        string path,
        string storeCode,
        string catalogVersion,
        int expectedTotal,
        int batchSize,
        Func<IReadOnlyList<CatalogLookupItemDto>, Task> onBatch,
        CancellationToken cancellationToken)
    {
        using var reader = OpenReader(path);
        var header = ReadHeader<CatalogFullFileHeader>(await reader.ReadLineAsync(cancellationToken));
        if (header.Format != CatalogFileFormats.FullV1 ||
            !string.Equals(header.StoreCode?.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(header.CatalogVersion, catalogVersion, StringComparison.Ordinal) ||
            header.TotalCount != expectedTotal)
        {
            throw Invalid($"Catalog full file header does not match the sync plan. format={header.Format} store={header.StoreCode} version={header.CatalogVersion} total={header.TotalCount} expectedTotal={expectedTotal}");
        }

        var batch = new List<CatalogLookupItemDto>(batchSize);
        var count = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            batch.Add(Deserialize<CatalogLookupItemDto>(line));
            count++;
            if (batch.Count >= batchSize)
            {
                await onBatch(batch);
                batch = new List<CatalogLookupItemDto>(batchSize);
            }
        }

        if (batch.Count > 0)
        {
            await onBatch(batch);
        }

        if (count != header.TotalCount)
        {
            throw Invalid($"Catalog full file is incomplete. expected={header.TotalCount} read={count}");
        }

        return count;
    }

    public static async Task<(IReadOnlyList<CatalogLookupItemDto> Upserts, IReadOnlyList<DeletedLookupDto> Deletes)> ReadDeltaAsync(
        string path,
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        CancellationToken cancellationToken)
    {
        using var reader = OpenReader(path);
        var header = ReadHeader<CatalogDeltaFileHeader>(await reader.ReadLineAsync(cancellationToken));
        if (header.Format != CatalogFileFormats.DeltaV1 ||
            !string.Equals(header.StoreCode?.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(header.BaseCatalogVersion, baseCatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(header.TargetCatalogVersion, targetCatalogVersion, StringComparison.Ordinal))
        {
            throw Invalid($"Catalog delta file header does not match the sync plan. format={header.Format} store={header.StoreCode} base={header.BaseCatalogVersion} target={header.TargetCatalogVersion}");
        }

        // 增量操作数受服务端阈值（5000）约束，整体放进内存后在一个事务里应用。
        var upserts = new List<CatalogLookupItemDto>();
        var deletes = new List<DeletedLookupDto>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var operation = Deserialize<CatalogDeltaFileLine>(line);
            if ((operation.Item is null) == (operation.Deleted is null))
            {
                throw Invalid("Catalog delta line must contain exactly one of item or deleted.");
            }

            if (operation.Item is not null)
            {
                upserts.Add(operation.Item);
            }
            else
            {
                deletes.Add(operation.Deleted!);
            }
        }

        if (upserts.Count + deletes.Count != header.OperationCount)
        {
            throw Invalid($"Catalog delta file is incomplete. expected={header.OperationCount} read={upserts.Count + deletes.Count}");
        }

        return (upserts, deletes);
    }

    private static StreamReader OpenReader(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
    }

    private static T ReadHeader<T>(string? line) where T : class =>
        string.IsNullOrWhiteSpace(line)
            ? throw Invalid("Catalog file is empty.")
            : Deserialize<T>(line);

    private static T Deserialize<T>(string line) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, JsonOptions)
                ?? throw Invalid("Catalog file contains an empty line value.");
        }
        catch (JsonException ex)
        {
            throw new CatalogApiException("Catalog file contains invalid JSON.", HttpStatusCode.OK, "CATALOG_FILE_INVALID", ex);
        }
    }

    private static CatalogApiException Invalid(string message) =>
        new(message, HttpStatusCode.OK, "CATALOG_FILE_INVALID");
}
