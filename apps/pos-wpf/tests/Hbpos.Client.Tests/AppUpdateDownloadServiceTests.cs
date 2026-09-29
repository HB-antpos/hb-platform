using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Tests;

public sealed class AppUpdateDownloadServiceTests
{
    [Fact]
    public async Task DownloadAsync_saves_file_and_accepts_matching_sha256_and_size()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task DownloadAsync_keeps_current_package_and_four_newest_cached_installers()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload)) with { FileName = "hbpos-current.exe" };
        await using var sandbox = TempDirectory.Create();
        var oldFiles = new[]
        {
            "hbpos-1.0.0.exe",
            "hbpos-1.0.1.msi",
            "hbpos-1.0.2.exe",
            "hbpos-1.0.3.exe",
            "hbpos-1.0.4.exe"
        };

        for (var index = 0; index < oldFiles.Length; index++)
        {
            var path = Path.Combine(sandbox.Path, oldFiles[index]);
            await File.WriteAllTextAsync(path, oldFiles[index]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 10));
        }

        var readmePath = Path.Combine(sandbox.Path, "readme.txt");
        await File.WriteAllTextAsync(readmePath, "not an installer");
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.True(result.Success);
        var remainingInstallers = Directory
            .GetFiles(sandbox.Path)
            .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(
            [
                "hbpos-1.0.1.msi",
                "hbpos-1.0.2.exe",
                "hbpos-1.0.3.exe",
                "hbpos-1.0.4.exe",
                "hbpos-current.exe"
            ],
            remainingInstallers);
        Assert.True(File.Exists(readmePath));
    }

    [Fact]
    public async Task DownloadAsync_reports_progress_to_100_percent()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var progress = new RecordingProgress();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response, progress);

        Assert.True(result.Success);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(0, progress.Values[0].Percent);
        Assert.Equal(100, progress.Values[^1].Percent);
        Assert.Equal(payload.Length, progress.Values[^1].DownloadedBytes);
        Assert.Equal(payload.Length, progress.Values[^1].TotalBytes);
    }

    [Fact]
    public async Task DownloadAsync_reuses_cached_installer_when_size_and_sha256_match()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var cachedPath = Path.Combine(sandbox.Path, response.FileName!);
        await File.WriteAllBytesAsync(cachedPath, payload);
        var handler = new BytesHandler(payload);
        var progress = new RecordingProgress();
        var service = new AppUpdateDownloadService(
            new HttpClient(handler),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response, progress);

        // 中文注释：已校验过的同版本安装包直接复用，后台定时检查和重启都不再重复下载整包。
        Assert.True(result.Success);
        Assert.Equal(cachedPath, result.FilePath);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(100, Assert.Single(progress.Values).Percent);
        Assert.Equal(payload, await File.ReadAllBytesAsync(cachedPath));
    }

    [Theory]
    [InlineData("installer-old")]
    [InlineData("INSTALLER")]
    public async Task DownloadAsync_redownloads_when_cached_installer_size_or_sha256_differs(string cachedContent)
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var cachedPath = Path.Combine(sandbox.Path, response.FileName!);
        await File.WriteAllTextAsync(cachedPath, cachedContent);
        var handler = new BytesHandler(payload);
        var service = new AppUpdateDownloadService(
            new HttpClient(handler),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.True(result.Success);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(cachedPath));
    }

    [Theory]
    [InlineData("installer", true)]
    [InlineData("INSTALLER", false)]
    [InlineData(null, false)]
    public async Task TryGetVerifiedCachedInstallerAsync_only_returns_verified_local_installer_without_network(
        string? cachedContent,
        bool expectedCached)
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var cachedPath = Path.Combine(sandbox.Path, response.FileName!);
        if (cachedContent is not null)
        {
            await File.WriteAllTextAsync(cachedPath, cachedContent);
        }

        var handler = new BytesHandler(payload);
        var service = new AppUpdateDownloadService(
            new HttpClient(handler),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.TryGetVerifiedCachedInstallerAsync(response);

        if (expectedCached)
        {
            Assert.Equal(cachedPath, result);
        }
        else
        {
            Assert.Null(result);
        }

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_replaces_stale_temp_file_only_after_successful_validation()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var finalPath = Path.Combine(sandbox.Path, response.FileName!);
        var tempPath = finalPath + ".download";
        await File.WriteAllTextAsync(tempPath, "stale partial installer");
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.True(result.Success);
        Assert.Equal(finalPath, result.FilePath);
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(tempPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(finalPath));
    }

    [Fact]
    public async Task DownloadAsync_deletes_file_when_sha256_does_not_match()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, new string('b', 64));
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.False(File.Exists(result.FilePath));
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    public async Task DownloadAsync_rejects_missing_or_invalid_sha256(string sha256)
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, sha256);
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.False(File.Exists(result.FilePath));
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Theory]
    [InlineData("ftp://downloads.example/hbpos.exe")]
    [InlineData("file:///C:/Temp/hbpos.exe")]
    [InlineData("http://downloads.example/hbpos.exe")]
    public async Task DownloadAsync_rejects_untrusted_download_url(string downloadUrl)
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload)) with { DownloadUrl = downloadUrl };
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Fact]
    public async Task DownloadAsync_allows_loopback_http_for_local_debugging()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload)) with { DownloadUrl = "http://127.0.0.1/hbpos.exe" };
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(AppUpdateDownloadService.MaximumInstallerBytes + 1)]
    public async Task DownloadAsync_rejects_missing_or_oversized_file_size(long? fileSize)
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload)) with { FileSize = fileSize };
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Fact]
    public async Task DownloadAsync_rejects_content_length_mismatch_before_copying()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload, contentLengthOverride: payload.Length + 1)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Fact]
    public async Task DownloadAsync_allows_missing_content_length_when_body_size_and_sha256_match()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload));
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload, omitContentLength: true)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task DownloadAsync_rejects_actual_size_mismatch_after_copying()
    {
        var expectedPayload = Encoding.UTF8.GetBytes("installer");
        var actualPayload = Encoding.UTF8.GetBytes("installer-extra");
        var response = CreateRelease(expectedPayload, Sha256(actualPayload));
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(actualPayload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.False(File.Exists(result.FilePath));
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Fact]
    public async Task DownloadAsync_rejects_installer_type_that_does_not_match_file_extension()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload)) with
        {
            FileName = "hbpos.msi",
            InstallerType = "exe"
        };
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Theory]
    [InlineData("folder/hbpos.exe")]
    [InlineData(@"folder\\hbpos.exe")]
    [InlineData("../hbpos.exe")]
    [InlineData(@"..\\hbpos.exe")]
    [InlineData("hbpos.zip")]
    [InlineData("hbpos?.exe")]
    [InlineData("hbpos:1.2.3.exe")]
    [InlineData("CON.exe")]
    [InlineData("CON.any.exe")]
    [InlineData("NUL.v1.msi")]
    [InlineData("hbpos.exe ")]
    [InlineData("hbpos.exe.")]
    public async Task DownloadAsync_rejects_unsafe_or_unsupported_file_name(string fileName)
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, Sha256(payload)) with
        {
            FileName = fileName,
            InstallerType = fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "msi" : "exe"
        };
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new BytesHandler(payload)),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    [Fact]
    public async Task DownloadAsync_returns_failure_when_http_timeout_cancels_without_user_request()
    {
        var payload = Encoding.UTF8.GetBytes("installer");
        var response = CreateRelease(payload, new string('a', 64));
        await using var sandbox = TempDirectory.Create();
        var service = new AppUpdateDownloadService(
            new HttpClient(new TimeoutHandler()),
            new FixedDownloadDirectoryProvider(sandbox.Path));

        var result = await service.DownloadAsync(response);

        Assert.False(result.Success);
        Assert.Contains("timeout", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(sandbox.Path));
    }

    private static AppUpdateCheckResponse CreateRelease(byte[] payload, string sha256) => new()
    {
        UpdateAvailable = true,
        CurrentVersion = "1.0.0",
        TargetVersion = "1.1.0",
        DownloadUrl = "https://downloads.example/hbpos.exe",
        FileName = "hbpos.exe",
        FileSize = payload.Length,
        Sha256 = sha256,
        InstallerType = "exe"
    };

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class BytesHandler(
        byte[] payload,
        long? contentLengthOverride = null,
        bool omitContentLength = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            HttpContent content = omitContentLength
                ? new UnknownLengthContent(payload)
                : new ByteArrayContent(payload);
            if (contentLengthOverride is not null)
            {
                content.Headers.ContentLength = contentLengthOverride;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(payload, CancellationToken.None).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("The request timed out."));
        }
    }

    private sealed class RecordingProgress : IProgress<AppUpdateDownloadProgress>
    {
        public List<AppUpdateDownloadProgress> Values { get; } = [];

        public void Report(AppUpdateDownloadProgress value)
        {
            Values.Add(value);
        }
    }

    private sealed class FixedDownloadDirectoryProvider(string path) : IAppUpdateDownloadDirectoryProvider
    {
        public string GetDownloadDirectory() => path;
    }

    private sealed class TempDirectory : IAsyncDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hbpos-update-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
