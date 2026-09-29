using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Wpf.Services;

public sealed record AppUpdateDownloadResult(
    bool Success,
    string? FilePath,
    string? ErrorMessage = null)
{
    public static AppUpdateDownloadResult Succeeded(string filePath) => new(true, filePath);

    public static AppUpdateDownloadResult Fail(string? filePath, string errorMessage) => new(false, filePath, errorMessage);
}

public sealed record AppUpdateDownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    int Percent);

public interface IAppUpdateDownloadDirectoryProvider
{
    string GetDownloadDirectory();
}

public sealed class AppUpdateDownloadDirectoryProvider : IAppUpdateDownloadDirectoryProvider
{
    public string GetDownloadDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hbpos",
            "AppUpdates");
    }
}

public interface IAppUpdateDownloadService
{
    Task<AppUpdateDownloadResult> DownloadAsync(
        AppUpdateCheckResponse update,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    // 中文注释：只查本地缓存、不联网；返回已通过大小与 SHA256 校验的安装包路径，没有则返回 null。
    Task<string?> TryGetVerifiedCachedInstallerAsync(
        AppUpdateCheckResponse update,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

public sealed class AppUpdateDownloadService(
    HttpClient httpClient,
    IAppUpdateDownloadDirectoryProvider directoryProvider) : IAppUpdateDownloadService
{
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;

    private const int CachedInstallerRetentionCount = 5;

    private const int CopyBufferSize = 81920;

    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public async Task<AppUpdateDownloadResult> DownloadAsync(
        AppUpdateCheckResponse update,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateDownloadContract(update, out var downloadUri, out var expectedSize, out var errorMessage))
        {
            return AppUpdateDownloadResult.Fail(null, errorMessage);
        }

        if (!TryResolveInstallerFileName(update, out var fileName, out _, out errorMessage))
        {
            return AppUpdateDownloadResult.Fail(null, errorMessage);
        }

        var directory = directoryProvider.GetDownloadDirectory();
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, fileName);
        var tempFilePath = filePath + ".download";

        try
        {
            DeleteIfExists(tempFilePath);

            if (await IsVerifiedCachedInstallerAsync(filePath, expectedSize, update.Sha256, cancellationToken))
            {
                // 中文注释：本地同名安装包的大小和 SHA256 都与本次发布合同一致时直接复用，避免后台定时检查和每次启动都重新下载整包。
                PruneCachedInstallers(directory, filePath);
                ReportProgress(progress, expectedSize, expectedSize);
                return AppUpdateDownloadResult.Succeeded(filePath);
            }

            using var response = await httpClient.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            // 中文注释：部分下载源不会返回 Content-Length，此时放行并依赖后续实际字节数与 SHA256 校验兜底。
            if (contentLength is long actualContentLength && actualContentLength != expectedSize)
            {
                return AppUpdateDownloadResult.Fail(filePath, "Update package size does not match the release contract.");
            }

            ReportProgress(progress, 0, expectedSize);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true))
            {
                var copiedBytes = await CopyExactSizeAsync(
                    source,
                    target,
                    expectedSize,
                    progress,
                    cancellationToken);
                if (copiedBytes != expectedSize)
                {
                    DeleteIfExists(tempFilePath);
                    return AppUpdateDownloadResult.Fail(filePath, "Update package size does not match the release contract.");
                }
            }

            if (!await VerifySha256Async(tempFilePath, update.Sha256, cancellationToken))
            {
                // 中文注释：校验失败只删除临时文件，避免误覆盖或误复用损坏安装包。
                DeleteIfExists(tempFilePath);
                return AppUpdateDownloadResult.Fail(filePath, "Update package verification failed.");
            }

            File.Move(tempFilePath, filePath, overwrite: true);
            PruneCachedInstallers(directory, filePath);
            ReportProgress(progress, expectedSize, expectedSize);
            return AppUpdateDownloadResult.Succeeded(filePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteIfExists(tempFilePath);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DeleteIfExists(tempFilePath);
            // 中文注释：HttpClient 超时也会表现为取消异常；这里转为失败结果，避免强制更新遮罩停在下载中。
            return AppUpdateDownloadResult.Fail(filePath, "Update download timeout. Check the network and retry.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            DeleteIfExists(tempFilePath);
            return AppUpdateDownloadResult.Fail(filePath, ex.Message);
        }
    }

    public async Task<string?> TryGetVerifiedCachedInstallerAsync(
        AppUpdateCheckResponse update,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateDownloadContract(update, out _, out var expectedSize, out _) ||
            !TryResolveInstallerFileName(update, out var fileName, out _, out _))
        {
            return null;
        }

        var filePath = Path.Combine(directoryProvider.GetDownloadDirectory(), fileName);
        return await IsVerifiedCachedInstallerAsync(filePath, expectedSize, update.Sha256, cancellationToken)
            ? filePath
            : null;
    }

    private static bool TryValidateDownloadContract(
        AppUpdateCheckResponse update,
        out Uri downloadUri,
        out long expectedSize,
        out string errorMessage)
    {
        downloadUri = null!;
        expectedSize = 0;
        errorMessage = string.Empty;

        var rawDownloadUrl = update.DownloadUrl?.Trim();
        if (string.IsNullOrWhiteSpace(rawDownloadUrl) ||
            !Uri.TryCreate(rawDownloadUrl, UriKind.Absolute, out var parsedDownloadUri) ||
            !IsTrustedDownloadUri(parsedDownloadUri))
        {
            errorMessage = "Update package URL is not trusted.";
            return false;
        }

        downloadUri = parsedDownloadUri;

        if (update.FileSize is not > 0 or > MaximumInstallerBytes)
        {
            errorMessage = "Update package size is invalid.";
            return false;
        }

        if (!IsSha256Hex(update.Sha256))
        {
            errorMessage = "Update package SHA256 is missing or invalid.";
            return false;
        }

        expectedSize = update.FileSize.Value;
        return true;
    }

    internal static bool TryResolveInstallerFileName(
        AppUpdateCheckResponse update,
        out string fileName,
        out string installerType,
        out string errorMessage)
    {
        fileName = string.Empty;
        installerType = string.Empty;
        errorMessage = string.Empty;

        var declaredInstallerType = update.InstallerType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(declaredInstallerType) &&
            declaredInstallerType is not ("exe" or "msi"))
        {
            errorMessage = "Update installer type is invalid.";
            return false;
        }

        fileName = ResolveFileName(update, declaredInstallerType);
        if (!IsSafeInstallerFileName(fileName))
        {
            errorMessage = "Update package file name is invalid.";
            return false;
        }

        installerType = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (installerType is not ("exe" or "msi"))
        {
            errorMessage = "Update package file name must end with .exe or .msi.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(declaredInstallerType) &&
            !string.Equals(installerType, declaredInstallerType, StringComparison.OrdinalIgnoreCase))
        {
            // 文件扩展名是最终执行边界，必须和中心声明的安装类型一致。
            errorMessage = "Update installer type does not match the package extension.";
            return false;
        }

        return true;
    }

    private static bool IsTrustedDownloadUri(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttps ||
            (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }

    private static async Task<long> CopyExactSizeAsync(
        Stream source,
        Stream target,
        long expectedSize,
        IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long copiedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return copiedBytes;
            }

            copiedBytes += read;
            if (copiedBytes > expectedSize || copiedBytes > MaximumInstallerBytes)
            {
                return copiedBytes;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            ReportProgress(progress, copiedBytes, expectedSize);
        }
    }

    private static void ReportProgress(
        IProgress<AppUpdateDownloadProgress>? progress,
        long copiedBytes,
        long expectedSize)
    {
        if (progress is null || expectedSize <= 0)
        {
            return;
        }

        var percent = (int)Math.Clamp(copiedBytes * 100 / expectedSize, 0, 100);
        progress.Report(new AppUpdateDownloadProgress(copiedBytes, expectedSize, percent));
    }

    private static void PruneCachedInstallers(string directory, string protectedFilePath)
    {
        try
        {
            var protectedFullPath = Path.GetFullPath(protectedFilePath);
            var installers = Directory.EnumerateFiles(directory)
                .Where(IsInstallerCacheFile)
                .Select(path => new FileInfo(path))
                // 中文注释：当前刚下载完成的安装包必须保留，即使文件时间异常也不能被清掉。
                .OrderByDescending(file => string.Equals(
                    Path.GetFullPath(file.FullName),
                    protectedFullPath,
                    StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (var installer in installers.Skip(CachedInstallerRetentionCount))
            {
                DeleteIfExists(installer.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 中文注释：缓存清理失败不能影响本次已校验安装包的可用性。
        }
    }

    private static bool IsInstallerCacheFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFileName(AppUpdateCheckResponse update, string? declaredInstallerType)
    {
        if (!string.IsNullOrWhiteSpace(update.FileName))
        {
            return update.FileName;
        }

        return string.Equals(declaredInstallerType, "msi", StringComparison.OrdinalIgnoreCase)
            ? "hbpos-update.msi"
            : "hbpos-update.exe";
    }

    private static bool IsSafeInstallerFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOf('/') >= 0 ||
            fileName.IndexOf('\\') >= 0 ||
            fileName != Path.GetFileName(fileName) ||
            ContainsWindowsInvalidFileNameCharacter(fileName) ||
            fileName.EndsWith('.') ||
            fileName.EndsWith(' '))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName) ||
            string.Equals(baseName, ".", StringComparison.Ordinal) ||
            string.Equals(baseName, "..", StringComparison.Ordinal))
        {
            return false;
        }

        // 中文注释：客户端遇到目录片段、保留名或非法结尾时直接失败，不能再把危险文件名“净化”成可下载文件名继续执行。
        return !IsReservedWindowsDeviceFileName(fileName);
    }

    private static bool ContainsWindowsInvalidFileNameCharacter(string fileName)
    {
        foreach (var ch in fileName)
        {
            if (char.IsControl(ch) || ch is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReservedWindowsDeviceFileName(string fileName)
    {
        var firstDotIndex = fileName.IndexOf('.');
        var deviceName = firstDotIndex < 0 ? fileName : fileName[..firstDotIndex];
        return ReservedWindowsFileNames.Contains(deviceName.TrimEnd(' ', '.'));
    }

    private static async Task<bool> IsVerifiedCachedInstallerAsync(
        string filePath,
        long expectedSize,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            var cachedFile = new FileInfo(filePath);
            if (!cachedFile.Exists || cachedFile.Length != expectedSize)
            {
                return false;
            }

            return await VerifySha256Async(filePath, expectedSha256, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 中文注释：缓存安装包被占用或无权读取时回退到重新下载，不能因复用失败让本次更新失败。
            return false;
        }
    }

    private static async Task<bool> VerifySha256Async(
        string filePath,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsSha256Hex(expectedSha256))
        {
            return false;
        }

        var normalizedExpectedSha256 = expectedSha256!.Trim();
        await using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(actual, normalizedExpectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length != 64)
        {
            return false;
        }

        return normalized.All(ch =>
            ch is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');
    }

    private static void DeleteIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
