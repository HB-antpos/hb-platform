using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>人脸比对测试共用：样本报文、档案行、整套假替身的宿主。</summary>
internal static class MatchSamples
{
    /// <summary>用于测试的假腾讯云凭据（明显的假值，不对应任何真实账号）。</summary>
    public const string FakeSecretId = "AKIDfakefakefakefake0001";

    public const string FakeSecretKey = "fakeSecretKey-0123456789-NOT-REAL";

    /// <summary>身份由 seed 决定：同 seed 的两张图字节完全相同，即「同一个人」。</summary>
    public static byte[] Face(byte identity, int width = 120, int height = 160) => HikvisionSamples.FakeJpeg(width, height, identity);

    /// <summary>一条带一张人脸的抓拍；背景图 seed 不同则请求体不同（模拟同一个人相邻两次抓拍）。</summary>
    public static byte[] Capture(byte[] face, byte backgroundSeed = 1)
    {
        var json = Encoding.UTF8.GetBytes(HikvisionSamples.OfficialFaceCaptureJson);
        return HikvisionSamples.BuildMultipart(
            "boundary",
            "\r\n",
            new HikvisionSamples.SamplePart("Content-Disposition: form-data; name=\"faceCapture\"\nContent-Type: application/json; charset=\"UTF-8\"", json),
            new HikvisionSamples.SamplePart("Content-Disposition: form-data; name=\"facePicture\"; filename=\"facePicture.jpg\";\nContent-Type: image/jpeg", face),
            new HikvisionSamples.SamplePart(
                "Content-Disposition: form-data; name=\"backgroundPicture\"; filename=\"background.jpg\";\nContent-Type: image/jpeg",
                HikvisionSamples.FakeJpeg(1920, 1080, backgroundSeed)));
    }

    /// <summary>门店档案行。默认 Match + 跨境审查齐全 + 布里斯班时区；按需改字段。</summary>
    public static FaceStoreProfileRow Profile(
        string storeCode = "S1001",
        byte mode = 2,
        bool evaluate = false,
        bool staff = false,
        bool dedup = false,
        bool unwelcome = false,
        string? allowedSubjects = null,
        string? crossBorderRegion = "ap-guangzhou",
        string? timeZoneId = "Australia/Brisbane",
        bool unwelcomeRecord = true,
        bool crossBorderReviewed = true)
        => new(
            storeCode,
            mode,
            evaluate,
            staff,
            dedup,
            unwelcome,
            allowedSubjects,
            crossBorderRegion,
            crossBorderReviewed ? "XB-REVIEW-001" : null,
            crossBorderReviewed ? "reviewer@example.invalid" : null,
            crossBorderReviewed ? new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc) : null,
            timeZoneId,
            unwelcomeRecord ? "UW-ASSESS-001" : null,
            unwelcomeRecord ? "lawyer@example.invalid" : null,
            unwelcomeRecord ? new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc) : null);
}

/// <summary>整套假替身 + 宿主：引擎、写库、档案、命中图、存图、HTTP 处理器、事件写库器、摄像机仓储。</summary>
internal sealed class MatchHarness : IDisposable
{
    public static readonly DateTimeOffset Now = FaceIngestAppFactory.DefaultUtcNow;

    private MatchHarness()
    {
    }

    public FaceIngestAppFactory Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public FakeFaceEngine Engine { get; } = new();

    public FakeFaceMatchStore Store { get; } = new();

    public FakeFaceStoreProfileRepository Profiles { get; } = new();

    public FakeUnwelcomeHitImageStore Hits { get; } = new();

    public RecordingImageStore Images { get; } = new();

    public FakeCaptureEventWriter Writer { get; } = new();

    public FakeFaceCameraRepository Cameras { get; } = new();

    public ScriptedTencentHandler Http { get; } = new();

    public CaptureProcessor Processor => Factory.GetService<CaptureProcessor>();

    public FaceStoreProfileSource ProfileSource => Factory.GetService<FaceStoreProfileSource>();

    /// <summary>
    /// 建宿主。<paramref name="credentials"/> 为 false 时不配腾讯云凭据（功能未启用）；
    /// <paramref name="realTencentClient"/> 为 true 时用真实的 <see cref="TencentFaceClient"/>（HTTP 走假处理器），否则用内存引擎。
    /// </summary>
    public static async Task<MatchHarness> CreateAsync(
        FaceStoreProfileRow[]? profiles = null,
        bool credentials = true,
        bool realTencentClient = false,
        Action<Dictionary<string, string?>>? configure = null,
        bool profilesLoadFails = false,
        bool loadProfiles = true)
    {
        var harness = new MatchHarness();
        var factory = new FaceIngestAppFactory(options =>
        {
            options.Settings["FaceIngest:Database:ConnectionString"] = P1TestSupport.FakeConnectionString;
            // 测试宿主默认把质量闸门的体积下限降到极小：FakeJpeg 只有一百来字节；专门测体积闸门的用例自己覆盖。
            options.Settings["FaceMatch:Quality:MinBytes"] = "16";
            options.Settings["FaceMatch:Retry:BaseDelayMilliseconds"] = "1";
            options.Settings["FaceMatch:Retry:MaxDelayMilliseconds"] = "5";
            if (credentials)
            {
                options.Settings["FaceMatch:Tencent:SecretId"] = MatchSamples.FakeSecretId;
                options.Settings["FaceMatch:Tencent:SecretKey"] = MatchSamples.FakeSecretKey;
            }

            configure?.Invoke(options.Settings);
            options.ConfigureServices = services =>
            {
                var h = harness;
                services.RemoveAll<IFaceCameraRepository>();
                services.AddSingleton<IFaceCameraRepository>(h.Cameras);
                services.RemoveAll<ICaptureEventWriter>();
                services.AddSingleton<ICaptureEventWriter>(h.Writer);
                services.RemoveAll<IImageStore>();
                services.AddSingleton<IImageStore>(h.Images);
                services.RemoveAll<IFaceStoreProfileRepository>();
                services.AddSingleton<IFaceStoreProfileRepository>(h.Profiles);
                services.RemoveAll<IFaceMatchStore>();
                services.AddSingleton<IFaceMatchStore>(h.Store);
                services.RemoveAll<IUnwelcomeHitImageStore>();
                services.AddSingleton<IUnwelcomeHitImageStore>(h.Hits);
                if (!realTencentClient)
                {
                    services.RemoveAll<IFaceMatcher>();
                    services.RemoveAll<IVisitorRegistry>();
                    services.AddSingleton<IFaceMatcher>(h.Engine);
                    services.AddSingleton<IVisitorRegistry>(h.Engine);
                }

                // 不管用不用真实客户端，腾讯云命名客户端的出口一律换成假处理器：测试绝不会连外网。
                services.AddHttpClient(TencentFaceClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => h.Http);
            };
        });
        var client = factory.CreateClient();
        harness.Factory = factory;
        harness.Client = client;
        harness.Cameras.Return(FakeFaceCameraRepository.Row(factory.FrontCamera));
        if (profilesLoadFails)
        {
            harness.Profiles.Fail();
        }
        else
        {
            harness.Profiles.Return(profiles ?? []);
        }

        Assert.True(await factory.GetService<DatabaseCameraSource>().RefreshAsync(CancellationToken.None));
        if (loadProfiles)
        {
            // 失败场景下刷新返回 false（保持「从未成功」），成功场景下返回 true。
            await harness.ProfileSource.RefreshAsync(CancellationToken.None);
        }

        return harness;
    }

    public async Task PostAsync(byte[] body, DateTimeOffset? at = null)
    {
        Factory.Time.SetUtcNow(at ?? Now);
        Assert.Equal(HttpStatusCode.OK, await P1TestSupport.PostAsync(Client, Factory.FrontCamera.Token, body, HikvisionSamples.MultipartContentType));
    }

    public Task<ProcessingSummary> RunAsync() => Processor.RunOnceAsync(CancellationToken.None);

    /// <summary>存储根目录下是否一个文件都没有（含 raw/ 与 images/ 下的全部残留）。</summary>
    public IReadOnlyList<string> LeftoverFiles() => Factory.AllStoredFiles();

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }
}
