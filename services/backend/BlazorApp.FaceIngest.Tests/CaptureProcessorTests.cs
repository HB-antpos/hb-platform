using System.Net.Http.Headers;
using System.Text;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static BlazorApp.FaceIngest.Tests.Infrastructure.P1TestSupport;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>后台处理器：成功 / 幂等 / 退避重试 / 放弃 / 不连库摘要 / 重复报文 / LastSeen 节流 / 不含 token。</summary>
public sealed class CaptureProcessorTests
{
    private static readonly DateTimeOffset Now = FaceIngestAppFactory.DefaultUtcNow;

    [Fact]
    public async Task 不连库_解析存本地图_写摘要与processed_统计口径不变_第二轮无事可做()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        var store = factory.GetService<RawCaptureStore>();
        var storedBytesBefore = store.StoredBytes;
        var processor = factory.GetService<CaptureProcessor>();

        var summary = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), summary);
        Assert.False(processor.DatabaseEnabled);
        var basePath = Assert.Single(CaptureBasePaths(factory));
        var captureId = CaptureIdOf(basePath);
        var processed = ReadJson(basePath + ".processed");
        Assert.Equal("Parsed", processed.GetProperty("parseStatus").GetString());
        Assert.Equal(1, processed.GetProperty("faceCount").GetInt32());
        Assert.Equal(2, processed.GetProperty("imageCount").GetInt32());
        Assert.False(processed.GetProperty("databaseWritten").GetBoolean());
        Assert.Equal("local", processed.GetProperty("imageStorage").GetString());

        var parsed = ReadJson(basePath + ".parsed.json");
        Assert.Equal(captureId, parsed.GetProperty("captureId").GetString());
        Assert.Equal("Parsed", parsed.GetProperty("parseStatus").GetString());
        Assert.Equal(0, parsed.GetProperty("parseStatusCode").GetInt32());
        Assert.Equal("faceCapture", parsed.GetProperty("eventType").GetString());
        Assert.Equal("2026-10-12T12:00:00+11:00", parsed.GetProperty("cameraEventTime").GetString());
        var images = parsed.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal(2, images.Count);
        var faceKey = $"images/2026-10-12/cam-front-01/{captureId}_1.jpg";
        Assert.Equal(faceKey, images[0].GetProperty("objectKey").GetString());
        Assert.Equal("Face", images[0].GetProperty("kind").GetString());
        Assert.Equal(120, images[0].GetProperty("width").GetInt32());
        Assert.Equal(550.521m, images[0].GetProperty("rectX").GetDecimal());
        Assert.Equal(CaptureProcessor.BuildImageId(captureId, 1), images[0].GetProperty("imageId").GetString());
        Assert.Equal(HikvisionSamples.FaceJpeg, File.ReadAllBytes(Path.Combine(factory.StorageRoot, faceKey)));
        Assert.Equal(
            HikvisionSamples.BackgroundJpeg,
            File.ReadAllBytes(Path.Combine(factory.StorageRoot, $"images/2026-10-12/cam-front-01/{captureId}_2.jpg")));

        // 标记与图片不计入 P0 容量统计（重扫后也一样）。
        Assert.Equal(2, store.StoredFileCount);
        Assert.Equal(storedBytesBefore, store.StoredBytes);
        Assert.Equal(new StorageStatistics(2, storedBytesBefore), store.RescanStatistics());

        Assert.Equal(new ProcessingSummary(0, 0, 0, 0), await processor.RunOnceAsync(CancellationToken.None));
        Assert.Contains(factory.LogEntries, log => log.Message.Contains("capture processed", StringComparison.Ordinal)
            && log.Message.Contains("parseStatus=Parsed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 连库_事件与图片各写一次_删掉processed重处理仍幂等()
    {
        var repository = new FakeFaceCameraRepository();
        var writer = new FakeCaptureEventWriter();
        using var factory = CreateDatabaseFactory(repository, writer);
        using var client = factory.CreateClient();
        await LoadCamerasAsync(factory, repository, factory.FrontCamera);
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        var processor = factory.GetService<CaptureProcessor>();

        Assert.Equal(1, (await processor.RunOnceAsync(CancellationToken.None)).Processed);

        var basePath = Assert.Single(CaptureBasePaths(factory));
        var captureId = CaptureIdOf(basePath);
        var (captureEvent, duplicateOf) = Assert.Single(writer.Events.Values);
        Assert.Null(duplicateOf);
        Assert.Equal(captureId, captureEvent.CaptureId);
        Assert.Equal("cam-front-01", captureEvent.CameraId);
        Assert.Equal("S1001", captureEvent.StoreCode);
        Assert.Equal(Now.UtcDateTime, captureEvent.ReceivedAtUtc);
        Assert.Equal(DateTimeKind.Utc, captureEvent.ReceivedAtUtc.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(11)), captureEvent.CameraEventTime);
        Assert.Equal("00:00:5e:00:53:01", captureEvent.CameraMac);
        Assert.Equal(1, captureEvent.CameraChannel);
        Assert.False(captureEvent.IsRetransmission);
        Assert.Equal(1, captureEvent.FaceCount);
        Assert.Equal(CaptureParseStatus.Parsed, captureEvent.ParseStatus);
        Assert.Equal("local", captureEvent.RawStorage);
        Assert.Null(captureEvent.RawObjectKey);
        Assert.Equal(HikvisionSamples.OfficialFaceCaptureMultipart().LongLength, captureEvent.BodyBytes);

        Assert.Equal(2, writer.Images.Count);
        var faceImage = writer.Images[CaptureProcessor.BuildImageId(captureId, 1)];
        Assert.Equal(CaptureImageKind.Face, faceImage.Kind);
        Assert.Equal(0, faceImage.FaceIndex);
        Assert.Equal("5288", faceImage.CameraFaceId);
        Assert.Equal(35m, faceImage.FaceScore);
        Assert.False(faceImage.RectNormalized);
        Assert.Equal(HikvisionSamples.FaceJpeg.Length, faceImage.ByteSize);
        Assert.Contains("\"gender\"", faceImage.AttributesJson, StringComparison.Ordinal);
        var background = writer.Images[CaptureProcessor.BuildImageId(captureId, 2)];
        Assert.Equal(CaptureImageKind.Background, background.Kind);
        Assert.Null(background.RectX);
        Assert.Equal(32, background.ImageId.Length);

        // 连库模式不写本地摘要；processed 标记记下已写库。
        Assert.False(File.Exists(basePath + ".parsed.json"));
        Assert.True(ReadJson(basePath + ".processed").GetProperty("databaseWritten").GetBoolean());
        Assert.Single(writer.Touches);

        // 重处理：删掉 .processed 再跑一轮，写库器被再调一次，但事件 / 图片行都不重复。
        File.Delete(basePath + ".processed");
        Assert.Equal(1, (await processor.RunOnceAsync(CancellationToken.None)).Processed);
        Assert.Equal(2, writer.WriteCalls);
        Assert.Single(writer.Events);
        Assert.Equal(2, writer.Images.Count);
        Assert.True(File.Exists(basePath + ".processed"));
    }

    [Fact]
    public async Task 写库一直失败_retry指数退避_attempts递增_达上限标记failed()
    {
        var repository = new FakeFaceCameraRepository();
        var writer = new FakeCaptureEventWriter { FailuresRemaining = int.MaxValue };
        using var factory = CreateDatabaseFactory(repository, writer, options => options.Settings["FaceIngest:ProcessingMaxAttempts"] = "3");
        using var client = factory.CreateClient();
        await LoadCamerasAsync(factory, repository, factory.FrontCamera);
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        var processor = factory.GetService<CaptureProcessor>();
        var basePath = Assert.Single(CaptureBasePaths(factory));

        Assert.Equal(new ProcessingSummary(1, 0, 1, 0), await processor.RunOnceAsync(CancellationToken.None));
        var retry = ReadJson(basePath + ".retry");
        Assert.Equal(1, retry.GetProperty("attempts").GetInt32());
        Assert.Equal(Now + TimeSpan.FromSeconds(30), retry.GetProperty("nextAttemptUtc").GetDateTimeOffset());
        Assert.Contains("Simulated database outage", retry.GetProperty("lastError").GetString(), StringComparison.Ordinal);

        // 未到重试时间：不处理。
        Assert.Equal(0, (await processor.RunOnceAsync(CancellationToken.None)).Candidates);

        factory.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, (await processor.RunOnceAsync(CancellationToken.None)).Retried);
        retry = ReadJson(basePath + ".retry");
        Assert.Equal(2, retry.GetProperty("attempts").GetInt32());
        Assert.Equal(factory.Time.GetUtcNow() + TimeSpan.FromSeconds(60), retry.GetProperty("nextAttemptUtc").GetDateTimeOffset());

        factory.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(new ProcessingSummary(1, 0, 0, 1), await processor.RunOnceAsync(CancellationToken.None));
        var failed = ReadJson(basePath + ".failed");
        Assert.Equal(3, failed.GetProperty("attempts").GetInt32());
        Assert.False(failed.GetProperty("permanent").GetBoolean());
        Assert.False(File.Exists(basePath + ".retry"));
        Assert.False(File.Exists(basePath + ".processed"));
        // 原始报文保留到保留期。
        Assert.True(File.Exists(basePath + ".bin"));

        factory.Time.Advance(TimeSpan.FromHours(7));
        Assert.Equal(0, (await processor.RunOnceAsync(CancellationToken.None)).Candidates);
        Assert.Equal(3, writer.WriteCalls);
        Assert.Contains(factory.LogEntries, log => log.Message.Contains("capture processing failed", StringComparison.Ordinal));
    }

    [Fact]
    public void 退避时长_30秒起翻倍_单次最长6小时()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), CaptureProcessor.ComputeRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), CaptureProcessor.ComputeRetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(4), CaptureProcessor.ComputeRetryDelay(4));
        Assert.Equal(TimeSpan.FromHours(6), CaptureProcessor.ComputeRetryDelay(20));
        Assert.Equal(TimeSpan.FromHours(6), CaptureProcessor.ComputeRetryDelay(int.MaxValue));
    }

    [Fact]
    public async Task 存图失败一次_重试成功后删除retry标记()
    {
        FlakyImageStore? flaky = null;
        using var factory = new FaceIngestAppFactory(options => options.ConfigureServices = services =>
        {
            services.RemoveAll<IImageStore>();
            services.AddSingleton<IImageStore>(provider =>
            {
                flaky = new FlakyImageStore(provider.GetRequiredService<LocalImageStore>()) { FailuresRemaining = 1 };
                return flaky;
            });
        });
        using var client = factory.CreateClient();
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        var processor = factory.GetService<CaptureProcessor>();
        var basePath = Assert.Single(CaptureBasePaths(factory));

        Assert.Equal(1, (await processor.RunOnceAsync(CancellationToken.None)).Retried);
        Assert.Contains("COS PUT failed status=503", ReadJson(basePath + ".retry").GetProperty("lastError").GetString(), StringComparison.Ordinal);

        factory.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, (await processor.RunOnceAsync(CancellationToken.None)).Processed);
        Assert.False(File.Exists(basePath + ".retry"));
        Assert.Equal(2, ReadJson(basePath + ".processed").GetProperty("attempts").GetInt32());
        Assert.Equal(2, flaky!.Saved.Count);
    }

    [Fact]
    public async Task 同一摄像机24小时内相同报文_DuplicateOfCaptureId指向先到者()
    {
        var repository = new FakeFaceCameraRepository();
        var writer = new FakeCaptureEventWriter();
        using var factory = CreateDatabaseFactory(repository, writer);
        using var client = factory.CreateClient();
        await LoadCamerasAsync(factory, repository, factory.FrontCamera);
        var body = HikvisionSamples.OfficialFaceCaptureMultipart();
        await PostAtAsync(factory, client, Now, body, HikvisionSamples.MultipartContentType);
        await PostAtAsync(factory, client, Now.AddMinutes(1), body, HikvisionSamples.MultipartContentType);
        await PostAtAsync(factory, client, Now.AddMinutes(2), HikvisionSamples.TwoFacesContentIdMismatchMultipart(), HikvisionSamples.MultipartContentType);
        await PostAtAsync(factory, client, Now.AddHours(24).AddSeconds(30), body, HikvisionSamples.MultipartContentType);

        Assert.Equal(4, (await factory.GetService<CaptureProcessor>().RunOnceAsync(CancellationToken.None)).Processed);

        var bases = CaptureBasePaths(factory);
        var first = CaptureIdOf(bases[0]);
        Assert.Null(writer.Events[first].DuplicateOf);
        Assert.Equal(first, writer.Events[CaptureIdOf(bases[1])].DuplicateOf);
        Assert.Null(writer.Events[CaptureIdOf(bases[2])].DuplicateOf);
        // 24 小时 30 秒后再来同样的报文：第一条已出窗口，只有 1 分钟后那条还在 24 小时内 → 指向它。
        Assert.Equal(CaptureIdOf(bases[1]), writer.Events[CaptureIdOf(bases[3])].DuplicateOf);
        Assert.Equal(first, ReadJson(bases[1] + ".processed").GetProperty("duplicateOfCaptureId").GetString());
    }

    [Fact]
    public async Task LastSeenAtUtc同一摄像机至多每5分钟更新一次()
    {
        var repository = new FakeFaceCameraRepository();
        var writer = new FakeCaptureEventWriter();
        using var factory = CreateDatabaseFactory(repository, writer);
        using var client = factory.CreateClient();
        await LoadCamerasAsync(factory, repository, factory.FrontCamera);
        foreach (var minutes in new[] { 0, 1, 4, 6, 7 })
        {
            await PostAtAsync(factory, client, Now.AddMinutes(minutes), Encoding.UTF8.GetBytes(HikvisionSamples.BareVideoLossXml), "application/xml");
        }

        Assert.Equal(5, (await factory.GetService<CaptureProcessor>().RunOnceAsync(CancellationToken.None)).Processed);

        Assert.Equal(
            new[] { Now.UtcDateTime, Now.AddMinutes(6).UtcDateTime },
            writer.Touches.Select(touch => touch.SeenAtUtc).ToArray());
        Assert.All(writer.Touches, touch => Assert.Equal("cam-front-01", touch.CameraId));
        Assert.All(writer.Events.Values, e => Assert.Equal(CaptureParseStatus.NotFaceEvent, e.Event.ParseStatus));
    }

    [Fact]
    public async Task 元数据损坏_直接标记failed不重试_不影响同批其他报文()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        var directory = Path.Combine(factory.RawRoot, "2026-10-12", "cam-front-01");
        var brokenBase = Path.Combine(directory, CaptureFileNames.FormatBaseName(Now.AddSeconds(-1), new string('e', 32)));
        await File.WriteAllTextAsync(brokenBase + ".bin", "body");
        await File.WriteAllTextAsync(brokenBase + ".json", "{ \"captureId\": ");
        var mismatchedBase = Path.Combine(directory, CaptureFileNames.FormatBaseName(Now.AddSeconds(-2), new string('f', 32)));
        await File.WriteAllTextAsync(mismatchedBase + ".bin", "body");
        File.Copy(Directory.EnumerateFiles(directory, "*.json").First(path => !path.StartsWith(brokenBase, StringComparison.Ordinal)), mismatchedBase + ".json");

        var summary = await factory.GetService<CaptureProcessor>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ProcessingSummary(3, 1, 0, 2), summary);
        Assert.True(ReadJson(brokenBase + ".failed").GetProperty("permanent").GetBoolean());
        Assert.Contains("does not match the file name", ReadJson(mismatchedBase + ".failed").GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(brokenBase + ".retry"));
    }

    [Fact]
    public async Task 任何产物_标记_摘要_图片_日志_写库行都不含token()
    {
        var basicCamera = TestCamera.Create("cam-basic-03", "S2002");
        var queryCamera = TestCamera.Create("cam-query-04", "S2002");
        var repository = new FakeFaceCameraRepository();
        var writer = new FakeCaptureEventWriter();
        using var dbFactory = CreateDatabaseFactory(repository, writer);
        using var jsonFactory = new FaceIngestAppFactory(options =>
        {
            foreach (var pair in basicCamera.ToSettings(2).Concat(queryCamera.ToSettings(3)))
            {
                options.Settings[pair.Key] = pair.Value;
            }
        });
        var secrets = new List<string>();
        foreach (var factory in new[] { jsonFactory, dbFactory })
        {
            using var client = factory.CreateClient();
            if (factory == dbFactory)
            {
                await LoadCamerasAsync(factory, repository, factory.FrontCamera, basicCamera, queryCamera);
            }

            secrets.AddRange([factory.FrontCamera.Token, basicCamera.Token, queryCamera.Token]);
            await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
            using (var response = await client.PostAsync(
                       $"/hik/v1?token={queryCamera.Token}&macAddress=00:00:5e:00:53:01",
                       IngestEndpointTests.CreateContent(HikvisionSamples.TwoFacesContentIdMismatchMultipart(), HikvisionSamples.MultipartContentType)))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, "/hik/v1"))
            {
                var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + basicCamera.Token));
                secrets.Add(credential);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
                request.Content = IngestEndpointTests.CreateContent(Encoding.UTF8.GetBytes(HikvisionSamples.BareVideoLossXml), "application/xml");
                using var response = await client.SendAsync(request);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            }

            Assert.Equal(3, (await factory.GetService<CaptureProcessor>().RunOnceAsync(CancellationToken.None)).Processed);
        }

        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(jsonFactory.StorageRoot, "images"), "*", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateFiles(jsonFactory.RawRoot, "*.parsed.json", SearchOption.AllDirectories));
        Assert.Equal(3, writer.Events.Count);
        var writerDump = writer.DumpAll();
        foreach (var factory in new[] { jsonFactory, dbFactory })
        {
            foreach (var file in Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories))
            {
                var text = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file));
                Assert.All(secrets, secret => Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase));
            }

            Assert.All(factory.LogEntries, log => Assert.All(secrets, secret => Assert.DoesNotContain(secret, log.AllText, StringComparison.OrdinalIgnoreCase)));
        }

        Assert.All(secrets, secret => Assert.DoesNotContain(secret, writerDump, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 后台处理服务按周期自动处理()
    {
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.EnableProcessingService = true;
            options.Settings["FaceIngest:ProcessingIntervalSeconds"] = "1";
        });
        using var client = factory.CreateClient();
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        var basePath = Assert.Single(CaptureBasePaths(factory));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !File.Exists(basePath + ".processed"))
        {
            await Task.Delay(100);
        }

        Assert.True(File.Exists(basePath + ".processed"));
    }

    [Fact]
    public async Task ProcessingEnabled关闭时_后台服务不处理_只接收落盘()
    {
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.EnableProcessingService = true;
            options.Settings["FaceIngest:ProcessingEnabled"] = "false";
            options.Settings["FaceIngest:ProcessingIntervalSeconds"] = "1";
        });
        using var client = factory.CreateClient();
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !factory.LogEntries.Any(log => log.Message.Contains("processor started", StringComparison.Ordinal)))
        {
            await Task.Delay(50);
        }

        await Task.Delay(1500);
        Assert.Equal(2, factory.AllStoredFiles().Count);
        Assert.Contains(factory.LogEntries, log => log.Message.Contains("processingEnabled=False", StringComparison.Ordinal));
    }
}
