using System.Net;
using System.Text;
using System.Text.Json;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>配置校验：启动时非法即失败；运行中热更新为非法配置时保留旧快照；cameras.json 加载与热更新。</summary>
public sealed class ConfigurationTests
{
    public static TheoryData<string, string> InvalidCameraConfigurations => new()
    {
        { "camera-id-path-traversal", "CameraId '../evil'" },
        { "camera-id-uppercase", "CameraId 'Cam-01'" },
        { "camera-id-trailing-newline", "must match" },
        { "camera-id-too-long", "must match" },
        { "camera-id-duplicated", "is duplicated" },
        { "hash-too-short", "must be 64 hexadecimal characters" },
        { "hash-not-hex", "must be 64 hexadecimal characters" },
        { "hash-duplicated-ignoring-case", "duplicates another camera" },
        { "store-code-empty", "StoreCode" },
        { "store-code-invalid-character", "StoreCode" },
        { "store-code-too-long", "StoreCode" },
        { "enabled-yes", "FaceIngest:Cameras:1:Enabled for camera 'cam-b-02' must be true or false" },
        { "enabled-number", "FaceIngest:Cameras:1:Enabled for camera 'cam-b-02' must be true or false" },
        { "enabled-empty", "FaceIngest:Cameras:1:Enabled for camera 'cam-b-02' must be true or false" },
    };

    [Theory]
    [MemberData(nameof(InvalidCameraConfigurations))]
    public void 启动时摄像机配置非法_服务启动失败(string scenario, string expectedMessage)
    {
        var settings = BuildInvalidCameraSettings(scenario);
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            foreach (var pair in settings)
            {
                options.Settings[pair.Key] = pair.Value;
            }
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = FindValidationException(exception);
        Assert.Contains(expectedMessage, validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestCamera.HashOf("token-a"), validation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("FaceIngest:MaxRequestBodyBytes", "0")]
    [InlineData("FaceIngest:MaxTotalBytes", "-1")]
    [InlineData("FaceIngest:MaxFileCount", "0")]
    [InlineData("FaceIngest:MinFreeDiskBytes", "-1")]
    [InlineData("FaceIngest:RetentionDays", "0")]
    [InlineData("FaceIngest:CleanupIntervalMinutes", "0")]
    public void 启动时数值配置非法_服务启动失败(string key, string value)
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings[key] = value);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(key, FindValidationException(exception).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 热更新为非法配置后旧快照仍可用_合法热更新正常生效()
    {
        var cameraA = TestCamera.Create("cam-a-01", "S3003");
        var cameraB = TestCamera.Create("cam-b-02", "S3003");
        var cameraC = TestCamera.Create("cam-c-03", "S3003");
        var reloadable = new ReloadableTestConfigurationSource(TestCamera.ToSettings(cameraA));
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.ReloadableSource = reloadable;
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));

        // 非法 1：新增摄像机的 CameraId 含 ../ —— 整份拒绝，A 继续可用，B 不生效。
        var traversal = TestCamera.ToSettings(cameraA, cameraB with { CameraId = "../evil" });
        reloadable.Replace(traversal);
        Assert.Single(factory.LogEntries, IsReloadRejected);
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, cameraB.Token));

        // 非法 2：哈希重复。
        var duplicateHash = TestCamera.ToSettings(cameraA, cameraB);
        duplicateHash["FaceIngest:Cameras:1:TokenSha256"] = cameraA.TokenSha256.ToUpperInvariant();
        reloadable.Replace(duplicateHash);
        Assert.Equal(2, factory.LogEntries.Count(IsReloadRejected));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));

        // 合法：换成 C，A 被移除。
        reloadable.Replace(TestCamera.ToSettings(cameraC));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraC.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, cameraA.Token));

        Assert.All(factory.LogEntries.Where(IsReloadRejected), log => Assert.Equal(LogLevel.Error, log.Level));
        Assert.Equal(["cam-a-01", "cam-a-01", "cam-a-01", "cam-c-03"], CapturedCameraIds(factory));

        static bool IsReloadRejected(CapturedLog log) => log.Message.Contains("reload rejected", StringComparison.Ordinal);
    }

    [Fact]
    public async Task 从ConfigDirectory下的cameras_json加载摄像机()
    {
        var camera = TestCamera.Create("cam-json-07", "S4004");
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.CamerasJson = CamerasJson(camera);
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, camera.Token));
        var metadata = Assert.Single(factory.StoredCaptures()).ReadMetadata();
        Assert.Equal("cam-json-07", metadata.GetProperty("cameraId").GetString());
        Assert.Equal("S4004", metadata.GetProperty("storeCode").GetString());
    }

    [Fact]
    public void cameras_json里配置非法_服务启动失败()
    {
        var camera = TestCamera.Create("cam-json-08", "S4004") with { CameraId = "../../etc" };
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.CamerasJson = CamerasJson(camera);
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("../../etc", FindValidationException(exception).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task cameras_json文件改动后无需重启即生效_改成非法内容时保留旧快照()
    {
        var cameraA = TestCamera.Create("cam-file-a", "S5005");
        var cameraB = TestCamera.Create("cam-file-b", "S5005");
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.CamerasJson = CamerasJson(cameraA);
        });
        using var client = factory.CreateClient();
        var camerasPath = Path.Combine(factory.ConfigDirectory, "cameras.json");
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));

        await ReplaceFileAsync(camerasPath, CamerasJson(cameraA, cameraB));
        Assert.True(
            await WaitUntilAsync(async () => await PostAsync(client, cameraB.Token) == HttpStatusCode.OK),
            "cameras.json 改动后应在超时前热更新生效。");

        await ReplaceFileAsync(camerasPath, CamerasJson(cameraA, cameraB with { CameraId = "Bad Id" }));
        Assert.True(
            await WaitUntilAsync(() => Task.FromResult(factory.LogEntries.Any(log => log.Message.Contains("reload rejected", StringComparison.Ordinal)))),
            "改成非法内容后应记录 Error 并拒绝这次热更新。");
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraB.Token));

        // 语法错误的 JSON（写了一半）：框架不会触发重新绑定，必须记 Error，旧快照继续服务。
        await ReplaceFileAsync(camerasPath, "{ \"FaceIngest\": { \"Cameras\": [ ");
        Assert.True(
            await WaitUntilAsync(() => Task.FromResult(factory.LogEntries.Any(IsCamerasFileLoadFailure))),
            "cameras.json 改成语法错误的 JSON 后应记录 Error。");
        var loadFailure = factory.LogEntries.First(IsCamerasFileLoadFailure);
        Assert.Equal(LogLevel.Error, loadFailure.Level);
        Assert.DoesNotContain(cameraA.Token, loadFailure.AllText, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraB.Token));

        // 修好后照常热更新（文件监听没有因为上一次失败而失效）：只留 A，B 立刻 401。
        await ReplaceFileAsync(camerasPath, CamerasJson(cameraA));
        Assert.True(
            await WaitUntilAsync(async () => await PostAsync(client, cameraB.Token) == HttpStatusCode.Unauthorized),
            "修好 cameras.json 后应重新生效。");
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));

        static bool IsCamerasFileLoadFailure(CapturedLog log)
            => log.Message.Contains("cameras.json could not be loaded", StringComparison.Ordinal);
    }

    [Fact]
    public void 启动时cameras_json是语法错误的JSON_服务启动失败()
    {
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.CamerasJson = "{ \"FaceIngest\": { \"Cameras\": [ ";
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(EnumerateExceptions(exception), ex => ex is InvalidDataException);
        Assert.DoesNotContain(factory.LogEntries, log => log.Message.Contains("cameras.json could not be loaded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("\"yes\"")]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void cameras_json里Enabled不是布尔值_服务启动失败而不是静默丢掉这台摄像机(string enabledJson)
    {
        // 中文注释：回归点——Enabled 曾声明为 bool，ConfigurationBinder 会吞掉这台的转换异常并把它从清单里丢掉，
        // 结果「启动成功、cameras=1、这台静默 401」。现在必须启动失败并指出是哪一台的 Enabled。
        var cameraA = TestCamera.Create("cam-json-a", "S4004");
        var cameraB = TestCamera.Create("cam-json-b", "S4004");
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.CamerasJson = $$"""
                { "FaceIngest": { "Cameras": [
                  { "CameraId": "cam-json-a", "StoreCode": "S4004", "TokenSha256": "{{cameraA.TokenSha256}}", "Enabled": true },
                  { "CameraId": "cam-json-b", "StoreCode": "S4004", "TokenSha256": "{{cameraB.TokenSha256}}", "Enabled": {{enabledJson}} }
                ] } }
                """;
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(
            "FaceIngest:Cameras:1:Enabled for camera 'cam-json-b' must be true or false",
            FindValidationException(exception).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("True", HttpStatusCode.OK)]
    [InlineData("FALSE", HttpStatusCode.Unauthorized)]
    public async Task Enabled缺省视为启用_布尔值大小写不敏感(string? enabled, HttpStatusCode expected)
    {
        var cameraA = TestCamera.Create("cam-a-01", "S3003");
        var cameraB = TestCamera.Create("cam-b-02", "S3003");
        var settings = TestCamera.ToSettings(cameraA, cameraB);
        if (enabled is null)
        {
            settings.Remove("FaceIngest:Cameras:1:Enabled");
        }
        else
        {
            settings["FaceIngest:Cameras:1:Enabled"] = enabled;
        }

        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            foreach (var pair in settings)
            {
                options.Settings[pair.Key] = pair.Value;
            }
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));
        Assert.Equal(expected, await PostAsync(client, cameraB.Token));
        Assert.Equal(2, factory.GetService<FaceIngestSettingsMonitor>().Current.Cameras.Count);
    }

    [Fact]
    public async Task 热更新把Enabled改成非布尔值_拒绝并保留旧快照里的全部摄像机()
    {
        var cameraA = TestCamera.Create("cam-a-01", "S3003");
        var cameraB = TestCamera.Create("cam-b-02", "S3003");
        var reloadable = new ReloadableTestConfigurationSource(TestCamera.ToSettings(cameraA, cameraB));
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.ReloadableSource = reloadable;
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraB.Token));

        var invalid = TestCamera.ToSettings(cameraA, cameraB);
        invalid["FaceIngest:Cameras:1:Enabled"] = "yes";
        reloadable.Replace(invalid);

        var rejected = Assert.Single(factory.LogEntries, log => log.Message.Contains("reload rejected", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, rejected.Level);
        Assert.Contains("FaceIngest:Cameras:1:Enabled", rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.LogEntries, log => log.Message.Contains("trigger=reload", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraA.Token));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, cameraB.Token));
    }

    [Fact]
    public void 启动时cameras_json存在但运行用户读不到_服务启动失败()
    {
        // 中文注释：迁移文档与 runbook 依赖这个行为：文件级读权限不足（如 root:root 600）不是「当成不存在、全部 401」，
        // 而是 FileConfigurationProvider 打开文件时抛 UnauthorizedAccessException（不经 OnLoadException），服务启动失败。
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var camera = TestCamera.Create("cam-json-09", "S4004");
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.IncludeDefaultCameras = false;
            options.CamerasJson = CamerasJson(camera);
        });
        var camerasPath = Path.Combine(factory.ConfigDirectory, "cameras.json");
        File.SetUnixFileMode(camerasPath, UnixFileMode.None);
        if (CanOpenForRead(camerasPath))
        {
            // 以 root 运行时权限位拦不住读取，模拟不出这个场景。
            return;
        }

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(EnumerateExceptions(exception), ex => ex is UnauthorizedAccessException);

        static bool CanOpenForRead(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(EnumerateExceptions))
                {
                    yield return inner;
                }
            }
        }
    }

    private static Dictionary<string, string?> BuildInvalidCameraSettings(string scenario)
    {
        var a = new TestCamera("cam-a-01", "S1001", "token-a");
        var b = new TestCamera("cam-b-02", "S1001", "token-b");
        return scenario switch
        {
            "camera-id-path-traversal" => TestCamera.ToSettings(a with { CameraId = "../evil" }),
            "camera-id-uppercase" => TestCamera.ToSettings(a with { CameraId = "Cam-01" }),
            "camera-id-trailing-newline" => TestCamera.ToSettings(a with { CameraId = "cam-01\n" }),
            "camera-id-too-long" => TestCamera.ToSettings(a with { CameraId = new string('a', 65) }),
            "camera-id-duplicated" => TestCamera.ToSettings(a, b with { CameraId = a.CameraId }),
            "hash-too-short" => WithHash(TestCamera.ToSettings(a), a.TokenSha256[..63]),
            "hash-not-hex" => WithHash(TestCamera.ToSettings(a), "g" + a.TokenSha256[1..]),
            "hash-duplicated-ignoring-case" => WithHash(TestCamera.ToSettings(a, b), a.TokenSha256.ToUpperInvariant(), index: 1),
            "store-code-empty" => TestCamera.ToSettings(a with { StoreCode = string.Empty }),
            "store-code-invalid-character" => TestCamera.ToSettings(a with { StoreCode = "S/1001" }),
            "store-code-too-long" => TestCamera.ToSettings(a with { StoreCode = new string('S', 33) }),
            "enabled-yes" => WithEnabled(TestCamera.ToSettings(a, b), "yes"),
            "enabled-number" => WithEnabled(TestCamera.ToSettings(a, b), "1"),
            "enabled-empty" => WithEnabled(TestCamera.ToSettings(a, b), string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        // 中文注释：改第二台；第一台合法，确保不是「整份清单为空」碰巧通过，而是坏的那台必须报错。
        static Dictionary<string, string?> WithEnabled(Dictionary<string, string?> settings, string enabled)
        {
            settings["FaceIngest:Cameras:1:Enabled"] = enabled;
            return settings;
        }

        static Dictionary<string, string?> WithHash(Dictionary<string, string?> settings, string hash, int index = 0)
        {
            settings[$"FaceIngest:Cameras:{index}:TokenSha256"] = hash;
            return settings;
        }
    }

    private static OptionsValidationException FindValidationException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OptionsValidationException validation)
            {
                return validation;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (inner is OptionsValidationException innerValidation)
                    {
                        return innerValidation;
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected OptionsValidationException but got: {exception}");
    }

    private static string CamerasJson(params TestCamera[] cameras)
        => JsonSerializer.Serialize(new
        {
            FaceIngest = new
            {
                Cameras = cameras.Select(camera => new
                {
                    camera.CameraId,
                    camera.StoreCode,
                    camera.TokenSha256,
                    camera.Enabled,
                    Note = "测试摄像机（假数据）",
                }),
            },
        });

    private static async Task ReplaceFileAsync(string path, string content)
    {
        // 中文注释：模拟运维「写临时文件再 mv」的原子替换，同时把修改时间往后挪，确保轮询 / 事件都能识别为改动。
        var temp = path + ".new";
        await File.WriteAllTextAsync(temp, content);
        File.Move(temp, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string token)
    {
        using var response = await client.PostAsync(
            $"/hik/v1/{token}",
            IngestEndpointTests.CreateContent(Encoding.UTF8.GetBytes("config-test"), "application/octet-stream"));
        return response.StatusCode;
    }

    private static string[] CapturedCameraIds(FaceIngestAppFactory factory)
        => [.. factory.StoredCaptures()
            .Select(capture => capture.ReadMetadata().GetProperty("cameraId").GetString()!)
            .Order(StringComparer.Ordinal)];
}
