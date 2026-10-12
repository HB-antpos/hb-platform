using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>P1 测试共用：数据库模式工厂、推送、定位报文与标记文件。</summary>
internal static class P1TestSupport
{
    /// <summary>
    /// 明显的假连接串（.invalid 保留域名、无账号密码）：只用来让服务进入「数据库模式」，
    /// 真正的读写全部被替换成内存假实现，测试不会发起任何数据库连接。
    /// </summary>
    public const string FakeConnectionString = "Data Source=db.invalid;Initial Catalog=FaceIngestTest;Connect Timeout=1";

    /// <summary>数据库模式的工厂：摄像机来源与写库器换成假实现。</summary>
    public static FaceIngestAppFactory CreateDatabaseFactory(
        FakeFaceCameraRepository repository,
        FakeCaptureEventWriter? writer = null,
        Action<FaceIngestAppOptions>? configure = null)
        => new(options =>
        {
            options.Settings["FaceIngest:Database:ConnectionString"] = FakeConnectionString;
            options.ConfigureServices = services =>
            {
                services.RemoveAll<IFaceCameraRepository>();
                services.AddSingleton<IFaceCameraRepository>(repository);
                services.RemoveAll<ICaptureEventWriter>();
                services.AddSingleton<ICaptureEventWriter>(writer ?? new FakeCaptureEventWriter());
            };
            configure?.Invoke(options);
        });

    /// <summary>让数据库来源加载一次给定的摄像机。</summary>
    public static async Task LoadCamerasAsync(FaceIngestAppFactory factory, FakeFaceCameraRepository repository, params TestCamera[] cameras)
    {
        repository.Return([.. cameras.Select(FakeFaceCameraRepository.Row)]);
        Assert.True(await factory.GetService<DatabaseCameraSource>().RefreshAsync(CancellationToken.None));
    }

    public static async Task<HttpStatusCode> PostAsync(HttpClient client, string token, byte[] body, string contentType)
    {
        using var response = await client.PostAsync($"/hik/v1/{token}", IngestEndpointTests.CreateContent(body, contentType));
        return response.StatusCode;
    }

    public static async Task PostAtAsync(FaceIngestAppFactory factory, HttpClient client, DateTimeOffset at, byte[] body, string contentType)
    {
        factory.Time.SetUtcNow(at);
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory.FrontCamera.Token, body, contentType));
    }

    /// <summary>raw/ 下全部完整报文的「不带扩展名的完整路径」，按接收时间排序（.parsed.json 不算）。</summary>
    public static IReadOnlyList<string> CaptureBasePaths(FaceIngestAppFactory factory)
    {
        if (!Directory.Exists(factory.RawRoot))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(factory.RawRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => CaptureFileNames.Classify(Path.GetFileName(path)) == CaptureFileKind.Metadata)
            .Select(path => path[..^".json".Length])
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)];
    }

    public static string CaptureIdOf(string basePath) => Path.GetFileName(basePath)[(Path.GetFileName(basePath).LastIndexOf('_') + 1)..];

    public static JsonElement ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }
}
