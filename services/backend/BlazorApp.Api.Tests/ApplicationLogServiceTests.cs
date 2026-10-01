using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Services.Logging;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public class ApplicationLogServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ISqlSugarClient _db;

    public ApplicationLogServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"application-log-{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = $"DataSource={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        CreateApplicationLogTable();
    }

    [Fact]
    public void ApplicationLoggingOptions_默认保留天数为7天()
    {
        var options = new ApplicationLoggingOptions();

        Assert.Equal(7, options.DefaultRetentionDays);
    }

    [Fact]
    public void 示例配置_中心日志完整保留六个已知项目且不包含真实密钥()
    {
        var configurationPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../BlazorApp.Api/appsettings.ApplicationLogging.example.json"
            )
        );
        using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
        var projects = document.RootElement
            .GetProperty("ApplicationLogging")
            .GetProperty("Projects")
            .EnumerateArray()
            .Select(project => new
            {
                ProjectCode = project.GetProperty("ProjectCode").GetString(),
                DisplayName = project.GetProperty("DisplayName").GetString(),
                Enabled = project.GetProperty("Enabled").GetBoolean(),
                RetentionDays = project.GetProperty("RetentionDays").GetInt32(),
                ApiKeyHash = project.TryGetProperty("ApiKeyHash", out var apiKeyHash)
                    ? apiKeyHash.GetString()
                    : null,
            })
            .ToArray();

        Assert.Collection(
            projects,
            project => Assert.Equal(("HBBBackend", "Web/移动端后端", true, 7), (project.ProjectCode, project.DisplayName, project.Enabled, project.RetentionDays)),
            project => Assert.Equal(("hbweb_rv", "Web前端", true, 7), (project.ProjectCode, project.DisplayName, project.Enabled, project.RetentionDays)),
            project => Assert.Equal(("HbwebExpo", "移动端", false, 7), (project.ProjectCode, project.DisplayName, project.Enabled, project.RetentionDays)),
            project => Assert.Equal(("hbpos_win", "WPF客户端", true, 30), (project.ProjectCode, project.DisplayName, project.Enabled, project.RetentionDays)),
            project => Assert.Equal(("hbpos_api", "WPF收银后端", true, 7), (project.ProjectCode, project.DisplayName, project.Enabled, project.RetentionDays)),
            project => Assert.Equal(("hbpos_ipad", "iPad客户端", true, 30), (project.ProjectCode, project.DisplayName, project.Enabled, project.RetentionDays)),
            project => Assert.Equal(("hbpos_handheld", "手持收银客户端", true, 30), (project.ProjectCode, project.DisplayName, project.Enabled, project.RetentionDays))
        );
        Assert.All(projects, project =>
            Assert.True(
                string.IsNullOrWhiteSpace(project.ApiKeyHash)
                    || project.ApiKeyHash == "<sha256-lower-hex>"
            )
        );
    }

    [Fact]
    public void 生产Compose_手持收银中心日志使用独立摘要环境变量()
    {
        var composePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../docker-compose.yml"));
        var compose = File.ReadAllText(composePath);

        Assert.Contains("ApplicationLogging__Projects__6__ProjectCode=hbpos_handheld", compose, StringComparison.Ordinal);
        Assert.Contains("ApplicationLogging__Projects__6__Enabled=true", compose, StringComparison.Ordinal);
        Assert.Contains("ApplicationLogging__Projects__6__RetentionDays=30", compose, StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationLogging__Projects__6__ApiKeyHash=${CENTER_LOG_HBPOS_HANDHELD_KEY_SHA256:?required}",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 生产Compose_iPad中心日志使用独立摘要环境变量()
    {
        var composePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../docker-compose.yml"));
        var compose = File.ReadAllText(composePath);

        Assert.Contains("ApplicationLogging__Projects__5__ProjectCode=hbpos_ipad", compose, StringComparison.Ordinal);
        Assert.Contains("ApplicationLogging__Projects__5__Enabled=true", compose, StringComparison.Ordinal);
        Assert.Contains("ApplicationLogging__Projects__5__RetentionDays=30", compose, StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationLogging__Projects__5__ApiKeyHash=${CENTER_LOG_HBPOS_IPAD_KEY_SHA256:?required}",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateProjectAsync_Key正确且项目启用_返回项目配置()
    {
        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "hbweb_rv",
                DisplayName = "Web 后台",
                ApiKeyHash = Sha256("web-secret"),
                Enabled = true,
            }
        );

        var project = await service.AuthenticateProjectAsync("hbweb_rv", "web-secret");

        Assert.NotNull(project);
        Assert.Equal("hbweb_rv", project.ProjectCode);
        Assert.Equal("Web 后台", project.DisplayName);
    }

    [Theory]
    [InlineData("", "web-secret")]
    [InlineData("hbweb_rv", "")]
    [InlineData("missing", "web-secret")]
    [InlineData("hbweb_rv", "wrong-secret")]
    public async Task AuthenticateProjectAsync_Key缺失或错误_返回空(string projectCode, string apiKey)
    {
        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "hbweb_rv",
                ApiKeyHash = Sha256("web-secret"),
                Enabled = true,
            }
        );

        var project = await service.AuthenticateProjectAsync(projectCode, apiKey);

        Assert.Null(project);
    }

    [Fact]
    public async Task AuthenticateProjectAsync_配置项目码带首尾空格_与状态使用相同规范化项目码()
    {
        var options = new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            Projects =
            [
                new()
                {
                    ProjectCode = "  hbweb_rv  ",
                    DisplayName = "Web前端",
                    ApiKeyHash = Sha256("web-secret"),
                    Enabled = true,
                },
            ],
        };
        var service = CreateService(options);

        var authenticated = await service.AuthenticateProjectAsync("hbweb_rv", "web-secret");
        var status = Assert.Single(
            (await service.GetSummaryAsync(new ApplicationLogQueryDto())).Status.Projects,
            project => project.Mode == "External"
        );

        Assert.NotNull(authenticated);
        Assert.Equal("hbweb_rv", authenticated.ProjectCode);
        Assert.Equal(authenticated.ProjectCode, status.ProjectCode);
        Assert.Equal("Ready", status.ConfigurationState);
    }

    [Fact]
    public async Task IngestAsync_批量写入日志_保存项目和异常分析字段()
    {
        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "HbwebExpo",
                DisplayName = "移动端",
                ApiKeyHash = Sha256("mobile-secret"),
                Enabled = true,
            }
        );
        var request = new ApplicationLogIngestRequestDto
        {
            Logs =
            [
                new ApplicationLogIngestItemDto
                {
                    Level = "Error",
                    Message = "Bind failed",
                    TimestampUtc = new DateTime(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc),
                    ProjectCode = "HbwebExpo",
                    Environment = "preview",
                    SourceType = "Mobile",
                    ServiceName = "PDA",
                    TraceId = "trace-1",
                    RequestPath = "/api/react/warehouse-products/mobile/HB001",
                    RequestMethod = "PATCH",
                    StatusCode = 500,
                    UserId = "u-1",
                    UserName = "sean",
                    ExceptionType = "InvalidOperationException",
                    ExceptionMessage = "货位不存在",
                    StackTrace = "stack",
                    Properties = new Dictionary<string, object?> { ["productCode"] = "HB001" },
                },
            ],
        };

        var result = await service.IngestAsync("HbwebExpo", request);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(0, result.DuplicateCount);
        var itemResult = Assert.Single(result.Results);
        Assert.Null(itemResult.ClientEventId);
        Assert.Equal("accepted", itemResult.Status);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.Equal("HbwebExpo", saved.ProjectCode);
        Assert.Equal("移动端", saved.ProjectName);
        Assert.Equal("Error", saved.Level);
        Assert.Equal("Mobile", saved.SourceType);
        Assert.Equal("trace-1", saved.TraceId);
        Assert.Equal("/api/react/warehouse-products/mobile/HB001", saved.RequestPath);
        Assert.Equal("InvalidOperationException", saved.ExceptionType);
        Assert.Contains("productCode", saved.PropertiesJson);
    }

    [Fact]
    public async Task IngestAsync_同一项目重复ClientEventId_仅保存一次并返回逐条状态()
    {
        var service = CreateService();
        var clientEventId = Guid.NewGuid();
        var request = new ApplicationLogIngestRequestDto
        {
            Logs =
            [
                CreateIngestItem("第一次上报", clientEventId),
                CreateIngestItem("重复上报", clientEventId),
            ],
        };

        var result = await service.IngestAsync("HBBBackend", request);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Collection(
            result.Results,
            item =>
            {
                Assert.Equal(clientEventId, item.ClientEventId);
                Assert.Equal("accepted", item.Status);
                Assert.Null(item.ErrorCode);
            },
            item =>
            {
                Assert.Equal(clientEventId, item.ClientEventId);
                Assert.Equal("duplicate", item.Status);
                Assert.Null(item.ErrorCode);
            }
        );
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.Equal(clientEventId, saved.ClientEventId);
        Assert.Equal("第一次上报", saved.Message);
    }

    [Fact]
    public async Task IngestAsync_嵌套属性重复键按最后值保留且同批继续写入()
    {
        var service = CreateService();
        using var document = JsonDocument.Parse(
            """
            {"context":{"attempt":"first","attempt":"last"}}
            """
        );
        var first = CreateIngestItem("包含重复嵌套键");
        first.Properties = new Dictionary<string, object?>
        {
            ["payload"] = document.RootElement.Clone(),
        };

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs = [first, CreateIngestItem("同批正常日志")],
            }
        );

        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.RejectedCount);
        var saved = await _db.Queryable<ApplicationLog>().OrderBy(item => item.Message).ToListAsync();
        Assert.Equal(2, saved.Count);
        var duplicateKeyLog = Assert.Single(saved, item => item.Message == "包含重复嵌套键");
        Assert.Contains("last", duplicateKeyLog.PropertiesJson);
        Assert.DoesNotContain("first", duplicateKeyLog.PropertiesJson);
    }

    [Fact]
    public async Task IngestAsync_属性键先NFKC且敏感键名和值均不入库()
    {
        var service = CreateService();
        using var nested = JsonDocument.Parse(
            """
            {
              "ｓａｆｅ": "nested-first",
              "safe": "nested-last",
              "ｔｏｋｅｎ": "wide-value-secret",
              "toκen": "mixed-value-secret",
              "token=nested-key-secret": "nested-value-secret",
              "deep": {
                "l2": {
                  "l3": {
                    "l4": { "token=deep-key-secret": "deep-value-secret" }
                  }
                }
              }
            }
            """
        );
        var item = CreateIngestItem("属性键安全测试");
        item.Properties = new Dictionary<string, object?>
        {
            ["ｓａｆｅ"] = "top-first",
            ["safe"] = "top-last",
            ["ｔｏｋｅｎ=top-key-secret"] = "top-value-secret",
            ["payload"] = nested.RootElement.Clone(),
        };

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto { Logs = [item] }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        foreach (
            var secret in new[]
            {
                "top-key-secret",
                "top-value-secret",
                "wide-value-secret",
                "mixed-value-secret",
                "nested-key-secret",
                "nested-value-secret",
                "deep-key-secret",
                "deep-value-secret",
            }
        )
            Assert.DoesNotContain(secret, saved.PropertiesJson);

        using var persisted = JsonDocument.Parse(saved.PropertiesJson!);
        Assert.Equal("top-last", persisted.RootElement.GetProperty("safe").GetString());
        Assert.False(persisted.RootElement.TryGetProperty("ｓａｆｅ", out _));
        var payload = persisted.RootElement.GetProperty("payload");
        Assert.Equal("nested-last", payload.GetProperty("safe").GetString());
        Assert.False(payload.TryGetProperty("ｓａｆｅ", out _));
        Assert.Contains(
            persisted.RootElement.EnumerateObject(),
            property => property.Name == "[REDACTED_KEY]"
        );
        Assert.Contains(
            payload.EnumerateObject(),
            property => property.Name == "[REDACTED_KEY]"
        );
        var deepJson = payload
            .GetProperty("deep")
            .GetProperty("l2")
            .GetProperty("l3")
            .GetProperty("l4")
            .GetString();
        using var deepPersisted = JsonDocument.Parse(deepJson!);
        Assert.Equal("[REDACTED_KEY]", Assert.Single(deepPersisted.RootElement.EnumerateObject()).Name);
    }

    [Fact]
    public async Task IngestAsync_结构字段和自由文本均不得原样保存令牌或卡号()
    {
        var service = CreateService();
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "正常消息",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "Backend",
                        Category = "authorization=category-secret",
                        TraceId = "4111 1111 1111 1111",
                        UserName = "Bearer user-name-secret",
                        ExceptionType = "token=exception-secret",
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        var persisted = $"{saved.Category}\n{saved.TraceId}\n{saved.UserName}\n{saved.ExceptionType}";
        Assert.DoesNotContain("category-secret", persisted);
        Assert.DoesNotContain("4111 1111 1111 1111", persisted);
        Assert.DoesNotContain("user-name-secret", persisted);
        Assert.DoesNotContain("exception-secret", persisted);
    }

    [Fact]
    public async Task Ingest_空日志列表在鉴权后仍消耗请求额度()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestRequestsPerMinute = 1;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);

        var invalid = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = null! }
        );
        var valid = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = [CreateIngestItem("有效日志")] }
        );

        var invalidResult = Assert.IsType<BadRequestObjectResult>(invalid.Result);
        Assert.Equal(
            "LOG_INGEST_INVALID",
            Assert.IsType<ApiResponse<object>>(invalidResult.Value).ErrorCode
        );
        var limited = Assert.IsType<ObjectResult>(valid.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Ingest_超批量在鉴权后仍消耗请求额度()
    {
        var options = CreateIngestControllerOptions();
        options.MaxBatchSize = 1;
        options.MaxIngestRequestsPerMinute = 1;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);

        var invalid = await controller.Ingest(
            new ApplicationLogIngestRequestDto
            {
                Logs = [CreateIngestItem("过量一"), CreateIngestItem("过量二")],
            }
        );
        var valid = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = [CreateIngestItem("有效日志")] }
        );

        Assert.IsType<BadRequestObjectResult>(invalid.Result);
        var limited = Assert.IsType<ObjectResult>(valid.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Ingest_无效大请求连续提交会先触发字节429()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestRequestsPerMinute = 10;
        options.MaxIngestBytesPerMinute = 1_000;
        options.MaxIngestFieldBytes = 8;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);
        controller.HttpContext.Request.ContentLength = 800;
        var invalidRequest = new ApplicationLogIngestRequestDto
        {
            Logs = [CreateIngestItem("超过字段预算的无效日志")],
        };

        var first = await controller.Ingest(invalidRequest);
        var second = await controller.Ingest(invalidRequest);

        Assert.IsType<BadRequestObjectResult>(first.Result);
        var limited = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Ingest_有效请求的request和log额度各只扣一次()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestRequestsPerMinute = 1;
        options.MaxIngestLogsPerMinute = 1;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);

        var first = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = [CreateIngestItem("首个有效请求")] }
        );
        var second = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = [CreateIngestItem("第二个有效请求")] }
        );

        Assert.IsType<OkObjectResult>(first.Result);
        var limited = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Ingest_单条字段超出字节上限时拒绝且不触发脱敏写入()
    {
        var options = CreateIngestControllerOptions();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);

        var response = await controller.Ingest(
            new ApplicationLogIngestRequestDto
            {
                Logs = [CreateIngestItem(new string('x', 64 * 1024 + 1))],
            }
        );

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(0, await _db.Queryable<ApplicationLog>().CountAsync());
    }

    [Fact]
    public async Task Ingest_聚合字节超出上限时整批拒绝()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestFieldBytes = 64 * 1024;
        options.MaxIngestItemBytes = 128 * 1024;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);
        var logs = Enumerable
            .Range(0, 18)
            .Select(index => CreateIngestItem($"{index}-{new string('x', 60 * 1024)}"))
            .ToList();

        var response = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = logs }
        );

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(0, await _db.Queryable<ApplicationLog>().CountAsync());
    }

    [Fact]
    public void ApplicationLogRateLimiter_按ASP兼容规范JSON计量完整结构()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestFieldBytes = 64 * 1024;
        options.MaxIngestItemBytes = 2 * 1024 * 1024;
        options.MaxIngestBatchBytes = 2 * 1024 * 1024;
        var monitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        monitor.SetupGet(item => item.CurrentValue).Returns(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ApplicationLogRateLimiter(cache, monitor.Object);
        var item = CreateIngestItem("控制字符\n\"quoted\"\u0001", Guid.Parse("11111111-2222-3333-4444-555555555555"));
        item.TimestampUtc = new DateTime(2026, 8, 1, 10, 20, 30, DateTimeKind.Utc);
        item.StatusCode = 503;
        item.Properties = Enumerable.Range(0, 2_000).ToDictionary(
            index => $"k{index}",
            _ => (object?)null
        );
        var request = new ApplicationLogIngestRequestDto { Logs = [item] };
        var jsonOptions = CreateAspNetCompatibleJsonOptions();
        var expectedItemBytes = JsonSerializer.SerializeToUtf8Bytes(item, jsonOptions).LongLength;
        var expectedRequestBytes = JsonSerializer.SerializeToUtf8Bytes(request, jsonOptions).LongLength;

        Assert.True(limiter.TryValidateIngestRequest(request, out var payloadBytes, out _));
        Assert.Equal(expectedRequestBytes, Convert.ToInt64(payloadBytes));

        options.MaxIngestItemBytes = checked((int)expectedItemBytes - 1);
        Assert.False(limiter.TryValidateIngestRequest(request, out _, out var message));
        Assert.Contains("单条日志", message);
    }

    [Fact]
    public void ApplicationLogRateLimiter_规范JSON固定Guid和UTC时间形状()
    {
        var item = CreateIngestItem(
            "wire-shape",
            Guid.Parse("ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB")
        );
        item.TimestampUtc = new DateTime(2026, 8, 1, 10, 20, 30, DateTimeKind.Utc);

        var json = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(item, CreateAspNetCompatibleJsonOptions())
        );

        Assert.Contains(
            "\"clientEventId\":\"abcdefab-cdef-abcd-efab-cdefabcdefab\"",
            json
        );
        Assert.Contains("\"timestampUtc\":\"2026-08-01T10:20:30Z\"", json);
    }

    [Fact]
    public void ApplicationLogRateLimiter_CJK按JSONstringify等价UTF8计量()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestFieldBytes = 64 * 1024;
        options.MaxIngestItemBytes = 2 * 1024 * 1024;
        options.MaxIngestBatchBytes = 2 * 1024 * 1024;
        var monitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        monitor.SetupGet(item => item.CurrentValue).Returns(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ApplicationLogRateLimiter(cache, monitor.Object);
        var request = new ApplicationLogIngestRequestDto
        {
            Logs = [CreateIngestItem(string.Concat(Enumerable.Repeat("中文日志", 1_000)))],
        };
        var wireBytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            CreateAspNetCompatibleJsonOptions()
        ).LongLength;
        var escapedBytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
            }
        ).LongLength;

        Assert.True(wireBytes < escapedBytes);
        Assert.True(limiter.TryValidateIngestRequest(request, out var payloadBytes, out _));
        Assert.Equal(wireBytes, payloadBytes);
    }

    [Fact]
    public void ApplicationLogRateLimiter_字段上限按JsonElement解码后的UTF8计量()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestFieldBytes = 12;
        options.MaxIngestItemBytes = 64 * 1024;
        options.MaxIngestBatchBytes = 1024 * 1024;
        var monitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        monitor.SetupGet(item => item.CurrentValue).Returns(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ApplicationLogRateLimiter(cache, monitor.Object);
        using var escaped = JsonDocument.Parse("\"\\u4E2D\\u6587\"");
        var item = CreateIngestItem("短消息");
        item.Properties = new Dictionary<string, object?>
        {
            ["escaped"] = escaped.RootElement.Clone(),
        };
        var request = new ApplicationLogIngestRequestDto { Logs = [item] };

        Assert.True(limiter.TryValidateIngestRequest(request, out _, out _));

        using var decodedTooLong = JsonDocument.Parse("\"中文中文中文中文中文\"");
        item.Properties["escaped"] = decodedTooLong.RootElement.Clone();
        Assert.False(limiter.TryValidateIngestRequest(request, out _, out var message));
        Assert.Contains("字段", message);
    }

    [Fact]
    public async Task Ingest_批次字节上限优先使用有效ContentLength()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestBatchBytes = 700;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);
        controller.HttpContext.Request.ContentLength = 800;

        var response = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = [CreateIngestItem("小规范请求大实际请求体")] }
        );

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task Ingest_分钟字节额度优先使用有效ContentLength()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestBytesPerMinute = 1_000;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);
        controller.HttpContext.Request.ContentLength = 800;

        var first = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = [CreateIngestItem("首个实际请求体")] }
        );
        var second = await controller.Ingest(
            new ApplicationLogIngestRequestDto { Logs = [CreateIngestItem("第二个实际请求体")] }
        );

        Assert.IsType<OkObjectResult>(first.Result);
        var limited = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Ingest_无ContentLength时使用规范化请求字节计费()
    {
        var options = CreateIngestControllerOptions();
        var item = CreateIngestItem("chunked-fallback", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        item.StatusCode = 500;
        item.Properties = Enumerable.Range(0, 20).ToDictionary(
            index => $"n{index}",
            _ => (object?)null
        );
        var request = new ApplicationLogIngestRequestDto { Logs = [item] };
        options.MaxIngestBytesPerMinute = checked(
            (int)JsonSerializer.SerializeToUtf8Bytes(
                request,
                CreateAspNetCompatibleJsonOptions()
            ).LongLength
        );
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateIngestController(options, cache);
        controller.HttpContext.Request.ContentLength = null;

        var first = await controller.Ingest(request);
        var second = await controller.Ingest(request);

        Assert.IsType<OkObjectResult>(first.Result);
        var limited = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public void ApplicationLogRateLimiter_字节额度按项目隔离()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestRequestsPerMinute = 10;
        options.MaxIngestLogsPerMinute = 100;
        options.MaxIngestBytesPerMinute = 10;
        var monitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        monitor.SetupGet(item => item.CurrentValue).Returns(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ApplicationLogRateLimiter(cache, monitor.Object);

        Assert.True(limiter.TryConsume("project-a", 1, 8, out _));
        Assert.False(limiter.TryConsume("project-a", 1, 3, out var message));
        Assert.Contains("字节数", message);
        Assert.True(limiter.TryConsume("project-b", 1, 8, out _));
    }

    [Fact]
    public void Ingest_公开入口限制原始请求体为四MiB()
    {
        var method = typeof(SystemLogsController).GetMethod(nameof(SystemLogsController.Ingest));
        var sizeLimit = Assert.Single(
            method!.GetCustomAttributes(typeof(RequestSizeLimitAttribute), true)
        ) as RequestSizeLimitAttribute;

        Assert.NotNull(sizeLimit);
        Assert.Equal(4L * 1024 * 1024, ((IRequestSizeLimitMetadata)sizeLimit).MaxRequestBodySize);
    }

    [Fact]
    public void Ingest_公开入口挂载模型绑定前资源过滤器()
    {
        var method = typeof(SystemLogsController).GetMethod(nameof(SystemLogsController.Ingest));

        Assert.Contains(
            method!.GetCustomAttributes(inherit: true),
            attribute =>
                attribute is TypeFilterAttribute filter
                && filter.ImplementationType.Name == "ApplicationLogIngestResourceFilter"
        );
    }

    [Fact]
    public async Task ApplicationLogIngestResourceFilter_合法key畸形请求在模型绑定前扣字节额度()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestRequestsPerMinute = 10;
        options.MaxIngestBytesPerMinute = 1_000;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var optionsMonitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        optionsMonitor.SetupGet(item => item.CurrentValue).Returns(options);
        var filter = new ApplicationLogIngestResourceFilter(
            CreateService(options),
            new ApplicationLogRateLimiter(cache, optionsMonitor.Object)
        );
        var nextCalls = 0;

        async Task<ResourceExecutingContext> ExecuteAsync()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["X-Log-Project"] = "HBBBackend";
            httpContext.Request.Headers["X-Log-Key"] = "backend-secret";
            httpContext.Request.ContentLength = 800;
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{invalid-json"));
            var actionContext = new ActionContext(
                httpContext,
                new RouteData(),
                new ActionDescriptor(),
                new ModelStateDictionary()
            );
            var filters = new List<IFilterMetadata>();
            var executing = new ResourceExecutingContext(
                actionContext,
                filters,
                new List<IValueProviderFactory>()
            );
            await filter.OnResourceExecutionAsync(
                executing,
                () =>
                {
                    nextCalls++;
                    return Task.FromResult(
                        new ResourceExecutedContext(actionContext, filters)
                        {
                            Result = new BadRequestObjectResult("模拟模型绑定失败"),
                        }
                    );
                }
            );
            return executing;
        }

        var first = await ExecuteAsync();
        var second = await ExecuteAsync();

        Assert.Null(first.Result);
        var limited = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
        Assert.Equal(1, nextCalls);
    }

    [Fact]
    public async Task ApplicationLogIngestResourceFilter_有效请求进入action不双扣request额度()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestRequestsPerMinute = 1;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var optionsMonitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        optionsMonitor.SetupGet(item => item.CurrentValue).Returns(options);
        var service = CreateService(options);
        var limiter = new ApplicationLogRateLimiter(cache, optionsMonitor.Object);
        var filter = new ApplicationLogIngestResourceFilter(service, limiter);
        var controller = new SystemLogsController(
            service,
            limiter,
            NullLogger<SystemLogsController>.Instance
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Log-Project"] = "HBBBackend";
        httpContext.Request.Headers["X-Log-Key"] = "backend-secret";
        httpContext.Request.ContentLength = 500;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary()
        );
        var filters = new List<IFilterMetadata>();
        var executing = new ResourceExecutingContext(
            actionContext,
            filters,
            new List<IValueProviderFactory>()
        );
        ActionResult<ApiResponse<ApplicationLogIngestResultDto>>? actionResult = null;

        await filter.OnResourceExecutionAsync(
            executing,
            async () =>
            {
                actionResult = await controller.Ingest(
                    new ApplicationLogIngestRequestDto
                    {
                        Logs = [CreateIngestItem("过滤器后的有效请求")],
                    }
                );
                return new ResourceExecutedContext(actionContext, filters)
                {
                    Result = actionResult.Result,
                };
            }
        );

        Assert.NotNull(actionResult);
        Assert.IsType<OkObjectResult>(actionResult!.Result);
    }

    [Fact]
    public async Task ApplicationLogIngestResourceFilter_分块请求保守按四MiB扣字节额度()
    {
        var options = CreateIngestControllerOptions();
        options.MaxIngestRequestsPerMinute = 10;
        options.MaxIngestBytesPerMinute = 4 * 1024 * 1024;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var optionsMonitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        optionsMonitor.SetupGet(item => item.CurrentValue).Returns(options);
        var filter = new ApplicationLogIngestResourceFilter(
            CreateService(options),
            new ApplicationLogRateLimiter(cache, optionsMonitor.Object)
        );
        var nextCalls = 0;

        async Task<ResourceExecutingContext> ExecuteAsync()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["X-Log-Project"] = "HBBBackend";
            httpContext.Request.Headers["X-Log-Key"] = "backend-secret";
            httpContext.Request.ContentLength = null;
            var actionContext = new ActionContext(
                httpContext,
                new RouteData(),
                new ActionDescriptor(),
                new ModelStateDictionary()
            );
            var filters = new List<IFilterMetadata>();
            var executing = new ResourceExecutingContext(
                actionContext,
                filters,
                new List<IValueProviderFactory>()
            );
            await filter.OnResourceExecutionAsync(
                executing,
                () =>
                {
                    nextCalls++;
                    return Task.FromResult(new ResourceExecutedContext(actionContext, filters));
                }
            );
            return executing;
        }

        var first = await ExecuteAsync();
        var second = await ExecuteAsync();

        Assert.Null(first.Result);
        var limited = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
        Assert.Equal(1, nextCalls);
    }

    [Fact]
    public async Task IngestAsync_旧客户端相同EventId仍按原语义逐条写入()
    {
        var service = CreateService();
        var first = CreateIngestItem("旧客户端第一次");
        first.EventId = "legacy-event";
        var second = CreateIngestItem("旧客户端第二次");
        second.EventId = "legacy-event";

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto { Logs = [first, second] }
        );

        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(2, await _db.Queryable<ApplicationLog>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_并发上报相同ClientEventId_数据库只保存一次()
    {
        var clientEventId = Guid.NewGuid();
        using var secondDb = CreateSqliteClient();
        var firstService = CreateService(_db);
        var secondService = CreateService(secondDb);

        var results = await Task.WhenAll(
            firstService.IngestAsync(
                "HBBBackend",
                new ApplicationLogIngestRequestDto
                {
                    Logs = [CreateIngestItem("并发一", clientEventId)],
                }
            ),
            secondService.IngestAsync(
                "HBBBackend",
                new ApplicationLogIngestRequestDto
                {
                    Logs = [CreateIngestItem("并发二", clientEventId)],
                }
            )
        );

        Assert.Equal(1, results.Sum(result => result.AcceptedCount));
        Assert.Equal(1, results.Sum(result => result.DuplicateCount));
        Assert.Equal(1, await _db.Queryable<ApplicationLog>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_Wpf字段和可信客户端Ip_入库前完成脱敏()
    {
        var service = CreateService();
        var clientEventId = Guid.NewGuid();
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        ClientEventId = clientEventId,
                        Level = "Error",
                        Message = "Authorization: Bearer top-secret-token card 4111111111111111 " +
                            "/api/pay?customer=alice&token=query-secret " +
                            "voucher_code=full-voucher employee-barcode=staff-secret " +
                            "credential=client-secret voucher=GIFT-ABC cvv=XYZ " +
                            "cookie=session123 header=x-private " +
                            "{\"clientCredential\":\"json-secret\",\"voucher\":\"JSON-GIFT\"," +
                            "\"cookie\":\"json-session\",\"pin\":1234,\"cvv\":9999," +
                            "\"authorizationMode\":\"supervisor\"}",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                        StoreCode = "S001",
                        DeviceCode = "POS-01",
                        AppVersion = "2.5.0",
                        InstanceId = "instance-1",
                        EventId = "event-7",
                        ClientIp = "198.51.100.99",
                        RequestPath = "/api/orders?authorizationCode=secret-code",
                        Properties = new Dictionary<string, object?>
                        {
                            ["productCode"] = "HB001",
                            ["Authorization"] = "Bearer property-secret",
                            ["customerEmail"] = "private@example.test",
                            ["requestBody"] = "{\"voucherCode\":\"full-voucher\"}",
                            ["nested"] = new Dictionary<string, object?>
                            {
                                ["password"] = "password-secret",
                            },
                        },
                    },
                ],
            },
            "203.0.113.10"
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.Equal(clientEventId, saved.ClientEventId);
        Assert.Equal("S001", saved.StoreCode);
        Assert.Equal("POS-01", saved.DeviceCode);
        Assert.Equal("2.5.0", saved.AppVersion);
        Assert.Equal("203.0.113.10", saved.ClientIp);
        Assert.Equal("/api/orders", saved.RequestPath);
        Assert.DoesNotContain("top-secret-token", saved.Message);
        Assert.DoesNotContain("4111111111111111", saved.Message);
        Assert.DoesNotContain("?customer=", saved.Message);
        Assert.DoesNotContain("query-secret", saved.Message);
        Assert.DoesNotContain("full-voucher", saved.Message);
        Assert.DoesNotContain("staff-secret", saved.Message);
        Assert.DoesNotContain("client-secret", saved.Message);
        Assert.DoesNotContain("GIFT-ABC", saved.Message);
        Assert.DoesNotContain("XYZ", saved.Message);
        Assert.DoesNotContain("session123", saved.Message);
        Assert.DoesNotContain("x-private", saved.Message);
        Assert.DoesNotContain("json-secret", saved.Message);
        Assert.DoesNotContain("JSON-GIFT", saved.Message);
        Assert.DoesNotContain("json-session", saved.Message);
        Assert.DoesNotContain("1234", saved.Message);
        Assert.DoesNotContain("9999", saved.Message);
        Assert.Contains("authorizationMode", saved.Message);
        Assert.Contains("supervisor", saved.Message);
        Assert.DoesNotContain("property-secret", saved.PropertiesJson);
        Assert.DoesNotContain("password-secret", saved.PropertiesJson);
        Assert.DoesNotContain("private@example.test", saved.PropertiesJson);
        Assert.DoesNotContain("full-voucher", saved.PropertiesJson);
        Assert.Contains("HB001", saved.PropertiesJson);
        Assert.Contains("[REDACTED]", saved.PropertiesJson);
    }

    [Fact]
    public async Task IngestAsync_递归脱敏根JSON和嵌入文本中的敏感父键对象数组()
    {
        var service = CreateService();
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "{\"token\":{\"value\":\"secret-value\",\"other\":\"second-secret\"},\"authorizationMode\":\"supervisor\"}",
                        ExceptionMessage = "嵌入片段 {\"details\":{\"password\":\"nested-secret\"},\"authorizationMode\":\"supervisor\"} 非法片段 {\"token\":{\"value\":\"still-safe\"}",
                        StackTrace = "{\"token\":[\"secret-one\",\"secret-two\"],\"authorizationMode\":\"supervisor\"}",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        var persistedText = $"{saved.Message}\n{saved.ExceptionMessage}\n{saved.StackTrace}";
        foreach (var secret in new[]
                 {
                     "secret-value", "second-secret", "secret-one", "secret-two", "nested-secret",
                 })
            Assert.DoesNotContain(secret, persistedText);
        Assert.Contains("[REDACTED]", persistedText);
        Assert.Contains("authorizationMode", persistedText);
        Assert.Contains("supervisor", persistedText);
    }

    [Fact]
    public async Task IngestAsync_JSON超过递归深度时整体脱敏且不抛出()
    {
        var service = CreateService();
        var deepJson = "{\"token\":[\"deep-secret-one\",\"deep-secret-two\"]}";
        for (var index = 0; index < 20; index++)
            deepJson = $"{{\"context\":{deepJson}}}";

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = deepJson,
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.DoesNotContain("deep-secret-one", saved.Message);
        Assert.DoesNotContain("deep-secret-two", saved.Message);
        Assert.Contains("[REDACTED]", saved.Message);
    }

    [Fact]
    public async Task IngestAsync_非法或超长JSON敏感父键FailClosed且键名归一化一致()
    {
        var service = CreateService();
        var longJson = $"{{\"token\":[\"long-secret-one\",\"long-secret-two\",\"{new string('x', 5_000)}\"],\"note\":\"safe\"}}";
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "{\"token\":{\"value\":\"secret-one\",\"other\":\"secret-two\"}",
                        ExceptionMessage = "{\"token\":[\"array-secret-one\",\"array-secret-two\"}",
                        StackTrace = longJson,
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                        Properties = new Dictionary<string, object?>
                        {
                            ["api.key"] = "dot-key-secret",
                            ["authorization.mode"] = "supervisor",
                        },
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        var persistedText = $"{saved.Message}\n{saved.ExceptionMessage}\n{saved.StackTrace}\n{saved.PropertiesJson}";
        foreach (var secret in new[]
                 {
                     "secret-one", "secret-two", "array-secret-one", "array-secret-two", "long-secret-one", "long-secret-two", "dot-key-secret",
                 })
            Assert.DoesNotContain(secret, persistedText);
        Assert.Contains("[REDACTED]", persistedText);
        Assert.Contains("authorization.mode", saved.PropertiesJson);
        Assert.Contains("supervisor", saved.PropertiesJson);
    }

    [Fact]
    public async Task IngestAsync_大量未闭合括号对敏感结构FailClosed()
    {
        var service = CreateService();
        var malformed = $"{new string('{', 2_000)}\"token\":{{\"value\":\"bulk-secret-one\",\"other\":\"bulk-secret-two\"}}";
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = malformed,
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.DoesNotContain("bulk-secret-one", saved.Message);
        Assert.DoesNotContain("bulk-secret-two", saved.Message);
        Assert.Contains("[REDACTED]", saved.Message);
    }

    [Fact]
    public async Task IngestAsync_普通文本未配对引号不阻断后续敏感JSON对象数组()
    {
        var service = CreateService();
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "prefix \"oops {\"token\":{\"value\":\"quoted-secret-one\",\"other\":\"quoted-secret-two\"}}",
                        ExceptionMessage = "prefix \"oops {\"token\":[\"quoted-array-one\",\"quoted-array-two\"]}",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        var persistedText = $"{saved.Message}\n{saved.ExceptionMessage}";
        foreach (var secret in new[]
                 {
                     "quoted-secret-one", "quoted-secret-two", "quoted-array-one", "quoted-array-two",
                 })
            Assert.DoesNotContain(secret, persistedText);
        Assert.Contains("[REDACTED]", persistedText);
    }

    [Fact]
    public async Task IngestAsync_回看前置敏感赋值键对未闭合对象数组FailClosed()
    {
        var service = CreateService();
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "token={\"value\":\"prefix-secret-one\",\"other\":\"prefix-secret-two\"",
                        ExceptionMessage = "\"token\":{\"value\":\"quoted-prefix-one\",\"other\":\"quoted-prefix-two\"",
                        StackTrace = "token=[\"array-prefix-one\",\"array-prefix-two\"",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                        Properties = new Dictionary<string, object?>
                        {
                            ["quotedArray"] = "\"token\":[\"quoted-array-one\",\"quoted-array-two\"",
                            ["note"] = "note={\"value\":\"safe\",\"other\":\"still-safe\"",
                            ["authorizationMode"] = "authorizationMode={\"value\":\"supervisor\"",
                        },
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        var persistedText = $"{saved.Message}\n{saved.ExceptionMessage}\n{saved.StackTrace}\n{saved.PropertiesJson}";
        foreach (var secret in new[]
                 {
                     "prefix-secret-one", "prefix-secret-two", "quoted-prefix-one", "quoted-prefix-two", "array-prefix-one", "array-prefix-two", "quoted-array-one", "quoted-array-two",
                 })
            Assert.DoesNotContain(secret, persistedText);
        Assert.Contains("[REDACTED]", persistedText);
        Assert.Contains("still-safe", saved.PropertiesJson);
        Assert.Contains("supervisor", saved.PropertiesJson);
    }

    [Fact]
    public async Task IngestAsync_前置任意空白与APIKEY赋值不会绕过脱敏()
    {
        var service = CreateService();
        var gap = new string(' ', 200);
        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = $"token{gap}={{\"value\":\"wide-object-one\",\"other\":\"wide-object-two\"",
                        ExceptionMessage = $"\"token\"{gap}:[\"wide-array-one\",\"wide-array-two\"",
                        StackTrace = $"API.KEY{gap}={{\"value\":\"dot-object-one\",\"other\":\"dot-object-two\"",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "Production",
                        SourceType = "POS",
                        Properties = new Dictionary<string, object?>
                        {
                            ["dotArray"] = $"\"API.KEY\"{gap}:[\"dot-array-one\",\"dot-array-two\"",
                            ["flat"] = "API.KEY=dot-equals-secret API.KEY:dot-colon-secret 'API.KEY'='dot-single-secret' \"API.KEY\":\"dot-double-secret\"",
                            ["authorizationMode"] = $"authorization.mode{gap}={{\"value\":\"supervisor\"",
                        },
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        var persistedText = $"{saved.Message}\n{saved.ExceptionMessage}\n{saved.StackTrace}\n{saved.PropertiesJson}";
        foreach (var secret in new[]
                 {
                     "wide-object-one", "wide-object-two", "wide-array-one", "wide-array-two", "dot-object-one", "dot-object-two", "dot-array-one", "dot-array-two", "dot-equals-secret", "dot-colon-secret", "dot-single-secret", "dot-double-secret",
                 })
            Assert.DoesNotContain(secret, persistedText);
        Assert.Contains("[REDACTED]", persistedText);
        Assert.Contains("supervisor", saved.PropertiesJson);
    }

    [Fact]
    public async Task IngestAsync_敏感Assignment对象数组闭合与未闭合组合矩阵均整值脱敏()
    {
        var service = CreateService();
        var whitespace = new[] { string.Empty, " ", new string(' ', 200) };
        var separators = new[] { ":", "=" };
        var keys = new[] { "token", "\"token\"", "API.KEY", "\"API.KEY\"", "\"to\\u006ben\"" };
        var structures = new[]
        {
            (Open: "{", Body: "\"value\":\"matrix-secret-one\",\"other\":\"matrix-secret-two\"", Close: "}"),
            (Open: "[", Body: "\"matrix-secret-one\",\"matrix-secret-two\"", Close: "]"),
        };
        var logs = new List<ApplicationLogIngestItemDto>();
        foreach (var gap in whitespace)
        foreach (var separator in separators)
        foreach (var key in keys)
        foreach (var structure in structures)
        foreach (var isClosed in new[] { true, false })
        {
            logs.Add(new ApplicationLogIngestItemDto
            {
                Level = "Error",
                Message = $"{key}{gap}{separator}{gap}{structure.Open}{structure.Body}{(isClosed ? structure.Close : string.Empty)}",
                TimestampUtc = DateTime.UtcNow,
                ProjectCode = "HBBBackend",
                Environment = "Production",
                SourceType = "POS",
            });
        }

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto { Logs = logs }
        );

        Assert.Equal(logs.Count, result.AcceptedCount);
        var persistedText = string.Join("\n", (await _db.Queryable<ApplicationLog>().ToListAsync()).Select(item => item.Message));
        Assert.DoesNotContain("matrix-secret-one", persistedText);
        Assert.DoesNotContain("matrix-secret-two", persistedText);
        Assert.Contains("[REDACTED]", persistedText);
    }

    [Fact]
    public async Task IngestAsync_不可靠Assignment键长度或非ASCII边界均FailClosed()
    {
        var service = CreateService();
        var keys = new[]
        {
            $"token{new string('x', 123)}",
            $"token{new string('x', 124)}",
            $"n{new string('x', 128)}",
            "密token",
            "token密",
            "to密ken",
        };
        var structures = new[]
        {
            (Open: "{", Body: "\"value\":\"unreliable-secret-one\",\"other\":\"unreliable-secret-two\"", Close: "}"),
            (Open: "[", Body: "\"unreliable-secret-one\",\"unreliable-secret-two\"", Close: "]"),
        };
        var logs = new List<ApplicationLogIngestItemDto>();
        foreach (var key in keys)
        foreach (var structure in structures)
        foreach (var isClosed in new[] { true, false })
        {
            logs.Add(new ApplicationLogIngestItemDto
            {
                Level = "Error",
                Message = $"{key}={structure.Open}{structure.Body}{(isClosed ? structure.Close : string.Empty)}",
                TimestampUtc = DateTime.UtcNow,
                ProjectCode = "HBBBackend",
                Environment = "Production",
                SourceType = "POS",
            });
        }

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto { Logs = logs }
        );

        Assert.Equal(logs.Count, result.AcceptedCount);
        var persistedText = string.Join("\n", (await _db.Queryable<ApplicationLog>().ToListAsync()).Select(item => item.Message));
        Assert.DoesNotContain("unreliable-secret-one", persistedText);
        Assert.DoesNotContain("unreliable-secret-two", persistedText);
        Assert.Contains("[REDACTED]", persistedText);
    }

    [Fact]
    public async Task IngestAsync_Assignment键必须是完整且边界可信的Token()
    {
        var service = CreateService();
        var malformedKeys = new[]
        {
            "to/ken", "to@ken", "api/key", "pass/word", "authoriz/ation", "'to'ken'", "\"to\"ken\"", "'token\\'", "\"token\\\"",
        };
        var structures = new[]
        {
            (Open: "{", Body: "\"value\":\"boundary-secret-one\",\"other\":\"boundary-secret-two\"", Close: "}"),
            (Open: "[", Body: "\"boundary-secret-one\",\"boundary-secret-two\"", Close: "]"),
        };
        var logs = new List<ApplicationLogIngestItemDto>();
        foreach (var key in malformedKeys)
        foreach (var structure in structures)
        foreach (var isClosed in new[] { true, false })
        {
            logs.Add(new ApplicationLogIngestItemDto
            {
                Level = "Error",
                Message = $"{key}={structure.Open}{structure.Body}{(isClosed ? structure.Close : string.Empty)}",
                TimestampUtc = DateTime.UtcNow,
                ProjectCode = "HBBBackend",
                Environment = "Production",
                SourceType = "POS",
            });
        }
        foreach (var key in new[] { "note", "\"note\"", "authorizationMode", "\"authorization\\u004dode\"" })
        {
            logs.Add(new ApplicationLogIngestItemDto
            {
                Level = "Error",
                Message = $"{key}={{\"value\":\"boundary-retained\",\"other\":\"also-retained\"}}",
                TimestampUtc = DateTime.UtcNow,
                ProjectCode = "HBBBackend",
                Environment = "Production",
                SourceType = "POS",
            });
        }
        logs.Add(new ApplicationLogIngestItemDto
        {
            Level = "Error",
            Message = "响应片段: {\"note\":\"中文安全值\"}",
            TimestampUtc = DateTime.UtcNow,
            ProjectCode = "HBBBackend",
            Environment = "Production",
            SourceType = "POS",
        });
        logs.Add(new ApplicationLogIngestItemDto
        {
            Level = "Error",
            Message = "响应片段: {\"details\":{\"token\":\"中文标签敏感值\"}}",
            TimestampUtc = DateTime.UtcNow,
            ProjectCode = "HBBBackend",
            Environment = "Production",
            SourceType = "POS",
        });

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto { Logs = logs }
        );

        Assert.Equal(logs.Count, result.AcceptedCount);
        var persistedText = string.Join("\n", (await _db.Queryable<ApplicationLog>().ToListAsync()).Select(item => item.Message));
        Assert.DoesNotContain("boundary-secret-one", persistedText);
        Assert.DoesNotContain("boundary-secret-two", persistedText);
        Assert.Contains("[REDACTED]", persistedText);
        Assert.Contains("boundary-retained", persistedText);
        Assert.Contains("also-retained", persistedText);
        // 结构化 JSON 会按默认编码写入 Unicode 转义，仍需确认安全字段未被整段误删。
        Assert.Contains("\\u4E2D\\u6587\\u5B89\\u5168\\u503C", persistedText);
        Assert.DoesNotContain("中文标签敏感值", persistedText);
    }

    [Fact]
    public async Task IngestAsync_单引号标准控制转义未知转义和双引号授权方式例外均安全()
    {
        var service = CreateService();
        var keys = new[]
        {
            "'to\\\\ken'", "'to\\'ken'", "'to\\\"ken'", "'to\\bken'", "'to\\fken'", "'to\\nken'", "'to\\rken'", "'to\\tken'", "'to\\u0009ken'", "'to\\xken'", "'to\\qken'", "'to" + "\\",
        };
        var structures = new[]
        {
            (Open: "{", Body: "\"value\":\"escape-secret-one\",\"other\":\"escape-secret-two\"", Close: "}"),
            (Open: "[", Body: "\"escape-secret-one\",\"escape-secret-two\"", Close: "]"),
        };
        var logs = new List<ApplicationLogIngestItemDto>();
        foreach (var key in keys)
        foreach (var structure in structures)
        foreach (var isClosed in new[] { true, false })
        {
            logs.Add(new ApplicationLogIngestItemDto
            {
                Level = "Error",
                Message = $"{key}:{structure.Open}{structure.Body}{(isClosed ? structure.Close : string.Empty)}",
                TimestampUtc = DateTime.UtcNow,
                ProjectCode = "HBBBackend",
                Environment = "Production",
                SourceType = "POS",
            });
        }
        logs.Add(new ApplicationLogIngestItemDto
        {
            Level = "Error",
            Message = "\"authorization\\u004dode\":{\"value\":\"supervisor\",\"other\":\"retained\"}",
            TimestampUtc = DateTime.UtcNow,
            ProjectCode = "HBBBackend",
            Environment = "Production",
            SourceType = "POS",
        });

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto { Logs = logs }
        );

        Assert.Equal(logs.Count, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().ToListAsync();
        var persistedText = string.Join("\n", saved.Select(item => item.Message));
        Assert.DoesNotContain("escape-secret-one", persistedText);
        Assert.DoesNotContain("escape-secret-two", persistedText);
        Assert.Contains("[REDACTED]", persistedText);
        var authorizationMode = Assert.Single(saved, item => item.Message.Contains("supervisor"));
        Assert.DoesNotContain("[REDACTED]", authorizationMode.Message);
        Assert.Contains("retained", authorizationMode.Message);
    }

    [Fact]
    public async Task IngestAsync_超过批量上限_拒绝写入()
    {
        var service = CreateService();
        var request = new ApplicationLogIngestRequestDto
        {
            Logs = Enumerable
                .Range(0, 201)
                .Select(index => new ApplicationLogIngestItemDto
                {
                    Level = "Error",
                    Message = $"错误 {index}",
                    TimestampUtc = DateTime.UtcNow,
                    ProjectCode = "HBBBackend",
                    Environment = "Development",
                    SourceType = "Backend",
                })
                .ToList(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IngestAsync("HBBBackend", request)
        );
    }

    [Fact]
    public async Task IngestAsync_Payload项目与鉴权项目不一致_按鉴权项目入库()
    {
        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "hbweb_rv",
                DisplayName = "Web 后台",
                ApiKeyHash = Sha256("web-secret"),
                Enabled = true,
            }
        );

        var result = await service.IngestAsync(
            "hbweb_rv",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "冒用移动端项目码",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HbwebExpo",
                        Environment = "test",
                        SourceType = "Web",
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.Equal("hbweb_rv", saved.ProjectCode);
        Assert.Equal("Web 后台", saved.ProjectName);
    }

    [Fact]
    public async Task IngestAsync_SourceType不在白名单_拒绝写入()
    {
        var service = CreateService();

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "细分来源不应写入 sourceType",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "test",
                        SourceType = "backend.worker",
                    },
                ],
            }
        );

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(0, result.DuplicateCount);
        var itemResult = Assert.Single(result.Results);
        Assert.Equal("rejected", itemResult.Status);
        Assert.Equal("INVALID_LOG_ITEM", itemResult.ErrorCode);
        Assert.Equal(0, await _db.Queryable<ApplicationLog>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_复杂属性值不能直接Json序列化_安全转成可查询文本()
    {
        var service = CreateService();

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "包含复杂属性的日志",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "test",
                        SourceType = "Backend",
                        Properties = new Dictionary<string, object?>
                        {
                            ["remoteIp"] = IPAddress.Loopback,
                            ["endpoint"] = new IPEndPoint(IPAddress.Loopback, 5002),
                            ["tags"] = new[] { "backend", "logging" },
                        },
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.Contains("remoteIp", saved.PropertiesJson);
        Assert.Contains("127.0.0.1", saved.PropertiesJson);
        Assert.Contains("endpoint", saved.PropertiesJson);
        Assert.Contains("backend", saved.PropertiesJson);
    }

    [Fact]
    public async Task IngestAsync_非有限浮点属性值_安全转成可查询文本()
    {
        var service = CreateService();

        var result = await service.IngestAsync(
            "HBBBackend",
            new ApplicationLogIngestRequestDto
            {
                Logs =
                [
                    new ApplicationLogIngestItemDto
                    {
                        Level = "Error",
                        Message = "包含非有限浮点属性的日志",
                        TimestampUtc = DateTime.UtcNow,
                        ProjectCode = "HBBBackend",
                        Environment = "test",
                        SourceType = "Backend",
                        Properties = new Dictionary<string, object?>
                        {
                            ["ratio"] = double.NaN,
                            ["positiveInfinity"] = double.PositiveInfinity,
                            ["negativeInfinity"] = float.NegativeInfinity,
                        },
                    },
                ],
            }
        );

        Assert.Equal(1, result.AcceptedCount);
        var saved = await _db.Queryable<ApplicationLog>().SingleAsync();
        Assert.Contains("\"ratio\":\"NaN\"", saved.PropertiesJson);
        Assert.Contains("\"positiveInfinity\":\"Infinity\"", saved.PropertiesJson);
        Assert.Contains("\"negativeInfinity\":\"-Infinity\"", saved.PropertiesJson);
    }

    [Fact]
    public async Task QueryAsync_按项目等级路径关键词筛选_返回匹配日志()
    {
        await InsertLogAsync("HBBBackend", "Error", "/api/orders", "订单同步失败", "trace-1");
        await InsertLogAsync("HbwebExpo", "Error", "/api/orders", "移动端错误", "trace-2");
        await InsertLogAsync("HBBBackend", "Warning", "/api/products", "商品警告", "trace-3");

        var service = CreateService();
        var result = await service.QueryAsync(
            new ApplicationLogQueryDto
            {
                ProjectCode = "HBBBackend",
                Level = "Error",
                RequestPath = "orders",
                Keyword = "同步",
                PageNumber = 1,
                PageSize = 20,
            }
        );

        Assert.Equal(1, result.Total);
        var item = Assert.Single(result.Items!);
        Assert.Equal("trace-1", item.TraceId);
        Assert.Equal("订单同步失败", item.Message);
    }

    [Fact]
    public async Task QueryAsync_按多个项目筛选_返回所选项目日志()
    {
        await InsertLogAsync("HBBBackend", "Error", "/api/orders", "后端错误", "trace-backend");
        await InsertLogAsync("HbwebExpo", "Error", "/api/mobile", "移动端错误", "trace-mobile");
        await InsertLogAsync("hbweb_rv", "Error", "/api/web", "前端错误", "trace-web");

        var service = CreateService();
        var result = await service.QueryAsync(
            new ApplicationLogQueryDto
            {
                ProjectCodes = ["HBBBackend", "HbwebExpo"],
                PageNumber = 1,
                PageSize = 20,
                SortBy = "ProjectCode",
                SortDirection = "asc",
            }
        );

        Assert.Equal(2, result.Total);
        Assert.DoesNotContain(result.Items!, item => item.ProjectCode == "hbweb_rv");
        Assert.Contains(result.Items!, item => item.ProjectCode == "HBBBackend");
        Assert.Contains(result.Items!, item => item.ProjectCode == "HbwebExpo");
    }

    [Fact]
    public async Task QueryAsync_按Wpf维度筛选_返回事件标识实例和服务端接收时间()
    {
        var receivedAtUtc = new DateTime(2026, 7, 10, 1, 2, 3, DateTimeKind.Utc);
        await _db.Insertable(
                new ApplicationLog
                {
                    ClientEventId = Guid.NewGuid(),
                    ProjectCode = "hbpos_win",
                    ProjectName = "WPF POS",
                    Environment = "Production",
                    SourceType = "POS",
                    StoreCode = "S001",
                    DeviceCode = "POS-01",
                    AppVersion = "2.5.0",
                    InstanceId = "instance-1",
                    EventId = "event-1",
                    Level = "Error",
                    Category = "Payment",
                    Message = "支付失败",
                    TimestampUtc = receivedAtUtc.AddMinutes(-5),
                    CreatedAt = receivedAtUtc,
                }
            )
            .ExecuteCommandAsync();
        await _db.Insertable(
                new ApplicationLog
                {
                    ProjectCode = "hbpos_win",
                    ProjectName = "WPF POS",
                    Environment = "Production",
                    SourceType = "POS",
                    StoreCode = "S002",
                    DeviceCode = "POS-02",
                    AppVersion = "2.4.0",
                    InstanceId = "instance-2",
                    EventId = "event-2",
                    Level = "Warning",
                    Message = "其他终端",
                    TimestampUtc = receivedAtUtc,
                    CreatedAt = receivedAtUtc,
                }
            )
            .ExecuteCommandAsync();

        var result = await CreateService().QueryAsync(
            new ApplicationLogQueryDto
            {
                StoreCode = "S001",
                DeviceCode = "POS-01",
                AppVersion = "2.5.0",
                InstanceId = "instance-1",
                EventId = "event-1",
            }
        );

        var item = Assert.Single(result.Items!);
        Assert.Equal("S001", item.StoreCode);
        Assert.Equal("POS-01", item.DeviceCode);
        Assert.Equal("2.5.0", item.AppVersion);
        Assert.Equal("instance-1", item.InstanceId);
        Assert.Equal("event-1", item.EventId);
        Assert.NotNull(item.ClientEventId);
        Assert.Equal(receivedAtUtc, item.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, item.TimestampUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, item.CreatedAtUtc.Kind);
    }

    [Fact]
    public async Task GetSummaryAsync_按Brisbane本地日窗口统计_只包含当天UTC范围内的日志()
    {
        await InsertLogAsync(
            "HBBBackend",
            "Error",
            "/api/before",
            "窗口开始前",
            "trace-before",
            new DateTime(2026, 6, 5, 13, 59, 59, DateTimeKind.Utc)
        );
        await InsertLogAsync(
            "HBBBackend",
            "Error",
            "/api/start",
            "窗口开始",
            "trace-start",
            new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc)
        );
        await InsertLogAsync(
            "HBBBackend",
            "Critical",
            "/api/inside",
            "窗口内",
            "trace-inside",
            new DateTime(2026, 6, 6, 13, 59, 59, DateTimeKind.Utc)
        );
        await InsertLogAsync(
            "HBBBackend",
            "Error",
            "/api/end",
            "下一本地日开始",
            "trace-end",
            new DateTime(2026, 6, 6, 14, 0, 0, DateTimeKind.Utc)
        );

        var service = CreateService();
        var result = await service.GetSummaryAsync(
            new ApplicationLogQueryDto
            {
                StartUtc = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 6, 6, 14, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.Equal(2, result.Total);
        Assert.Contains(result.ByRequestPath, item => item.Name == "/api/start" && item.Count == 1);
        Assert.Contains(result.ByRequestPath, item => item.Name == "/api/inside" && item.Count == 1);
        Assert.DoesNotContain(result.ByRequestPath, item => item.Name == "/api/before");
        Assert.DoesNotContain(result.ByRequestPath, item => item.Name == "/api/end");
    }

    [Theory]
    [InlineData("Error", "trace-error")]
    [InlineData("Critical", "trace-critical")]
    public async Task GetSummaryAsync_按等级筛选_返回对应等级统计(
        string level,
        string expectedTraceId
    )
    {
        await InsertLogAsync("HBBBackend", "Error", "/api/error", "错误日志", "trace-error");
        await InsertLogAsync("HBBBackend", "Critical", "/api/critical", "严重日志", "trace-critical");
        await InsertLogAsync("HBBBackend", "Warning", "/api/warning", "警告日志", "trace-warning");

        var service = CreateService();
        var summary = await service.GetSummaryAsync(
            new ApplicationLogQueryDto
            {
                Level = level,
            }
        );
        var query = await service.QueryAsync(
            new ApplicationLogQueryDto
            {
                Level = level,
                PageNumber = 1,
                PageSize = 10,
            }
        );

        Assert.Equal(1, summary.Total);
        Assert.Single(summary.ByLevel);
        Assert.Equal(level, summary.ByLevel[0].Name);
        Assert.Single(query.Items!);
        Assert.Equal(expectedTraceId, query.Items![0].TraceId);
    }

    [Fact]
    public async Task GetSummaryAsync_默认内部项目_返回后端采集状态并保留管道指标()
    {
        var queue = new ApplicationLogQueue(capacity: 1);
        queue.TryEnqueue(CreateIngestItem("第一条"));
        queue.TryEnqueue(CreateIngestItem("触发丢弃"));
        queue.RecordFlushFailure(3, "安全失败原因");
        var options = new ApplicationLoggingOptions
        {
            Enabled = true,
            DefaultProjectCode = "HBBBackend",
            DefaultEnvironment = "Production",
            ServiceName = "HBBBackend.Api",
            MinimumLevel = "Warning",
            DefaultRetentionDays = 7,
            Projects = [],
        };

        var summary = await CreateService(options, queue).GetSummaryAsync(new ApplicationLogQueryDto());

        Assert.True(summary.Status.BackendCaptureEnabled);
        Assert.Equal("Warning", summary.Status.BackendMinimumLevel);
        Assert.Equal("HBBBackend", summary.Status.DefaultProjectCode);
        Assert.Equal("Production", summary.Status.DefaultEnvironment);
        Assert.Equal("HBBBackend.Api", summary.Status.ServiceName);
        var project = Assert.Single(summary.Status.Projects);
        Assert.Equal("HBBBackend", project.ProjectCode);
        Assert.Equal("HBBBackend", project.DisplayName);
        Assert.Equal("Internal", project.Mode);
        Assert.False(project.ExplicitlyConfigured);
        Assert.True(project.Enabled);
        Assert.Null(project.CredentialConfigured);
        Assert.Equal("Ready", project.ConfigurationState);
        Assert.Equal(7, project.EffectiveRetentionDays);
        Assert.Null(project.LastReceivedAtUtc);
        Assert.Equal(1, summary.Pipeline.DroppedOldestCount);
        Assert.Equal(1, summary.Pipeline.FailedFlushBatchCount);
        Assert.Equal(3, summary.Pipeline.FailedFlushLogCount);
        Assert.Equal("安全失败原因", summary.Pipeline.LastFailedFlushReason);
    }

    [Fact]
    public async Task GetSummaryAsync_外部项目按启用状态和Hash合法性返回配置状态()
    {
        var options = new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            DefaultRetentionDays = 7,
            Projects =
            [
                new() { ProjectCode = "ready", Enabled = true, ApiKeyHash = Sha256("ready") },
                new() { ProjectCode = "empty", Enabled = true, ApiKeyHash = "" },
                new() { ProjectCode = "invalid", Enabled = true, ApiKeyHash = "不是合法摘要" },
                new() { ProjectCode = "disabled", Enabled = false, ApiKeyHash = Sha256("disabled") },
            ],
        };

        var projects = (await CreateService(options).GetSummaryAsync(new ApplicationLogQueryDto()))
            .Status.Projects;

        AssertProjectStatus(projects, "ready", true, true, "Ready");
        AssertProjectStatus(projects, "empty", true, false, "MissingCredential");
        AssertProjectStatus(projects, "invalid", true, false, "MissingCredential");
        AssertProjectStatus(projects, "disabled", false, true, "Disabled");
    }

    [Fact]
    public async Task GetSummaryAsync_内部项目状态只由全局采集开关决定()
    {
        var options = new ApplicationLoggingOptions
        {
            Enabled = false,
            DefaultProjectCode = "HBBBackend",
            Projects =
            [
                new()
                {
                    ProjectCode = "HBBBackend",
                    Enabled = true,
                    ApiKeyHash = Sha256("内部项目不使用此摘要"),
                },
            ],
        };

        var project = Assert.Single(
            (await CreateService(options).GetSummaryAsync(new ApplicationLogQueryDto()))
                .Status.Projects
        );

        Assert.False(project.Enabled);
        Assert.Null(project.CredentialConfigured);
        Assert.Equal("Disabled", project.ConfigurationState);
    }

    [Fact]
    public async Task GetSummaryAsync_默认项目码为空白_内部项目不得显示Ready()
    {
        var options = new ApplicationLoggingOptions
        {
            Enabled = true,
            DefaultProjectCode = "   ",
            Projects = [],
        };

        var project = Assert.Single(
            (await CreateService(options).GetSummaryAsync(new ApplicationLogQueryDto()))
                .Status.Projects
        );

        Assert.False(project.Enabled);
        Assert.Equal("Disabled", project.ConfigurationState);
    }

    [Fact]
    public async Task GetSummaryAsync_默认项目与显式项目重复_按项目码忽略大小写去重()
    {
        var options = new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            Projects =
            [
                new() { ProjectCode = "HBBBackend", DisplayName = "Web/移动端后端", RetentionDays = 7 },
                new() { ProjectCode = "hbbbackend", DisplayName = "重复后端", RetentionDays = 30 },
                new() { ProjectCode = "hbweb_rv", DisplayName = "Web前端", ApiKeyHash = Sha256("web") },
                new() { ProjectCode = "HBWEB_RV", DisplayName = "重复前端", ApiKeyHash = Sha256("web-2") },
            ],
        };

        var projects = (await CreateService(options).GetSummaryAsync(new ApplicationLogQueryDto()))
            .Status.Projects;

        Assert.Equal(2, projects.Count);
        var backend = Assert.Single(projects, item => item.Mode == "Internal");
        Assert.True(backend.ExplicitlyConfigured);
        Assert.Equal("Web/移动端后端", backend.DisplayName);
        Assert.Equal(7, backend.EffectiveRetentionDays);
        var web = Assert.Single(projects, item => item.Mode == "External");
        Assert.Equal("hbweb_rv", web.ProjectCode);
        Assert.Equal("Web前端", web.DisplayName);
    }

    [Fact]
    public async Task GetSummaryAsync_最后接收时间按CreatedAt最大值且不受汇总筛选影响()
    {
        var earlierReceivedAt = new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc);
        var latestReceivedAt = new DateTime(2026, 7, 10, 2, 0, 0, DateTimeKind.Utc);
        await InsertLogAsync(
            "hbpos_api",
            "Error",
            "/api/earlier",
            "客户端时间较新但先接收",
            "earlier",
            latestReceivedAt.AddDays(2),
            earlierReceivedAt
        );
        await InsertLogAsync(
            "hbpos_api",
            "Information",
            "/api/latest",
            "客户端时间较旧但后接收",
            "latest",
            earlierReceivedAt.AddDays(-2),
            latestReceivedAt
        );
        var options = new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            Projects =
            [
                new()
                {
                    ProjectCode = "hbpos_api",
                    DisplayName = "WPF收银后端",
                    Enabled = true,
                    ApiKeyHash = Sha256("pos-api"),
                },
            ],
        };

        var summary = await CreateService(options).GetSummaryAsync(
            new ApplicationLogQueryDto { Level = "Critical", ProjectCode = "HBBBackend" }
        );

        Assert.Equal(0, summary.Total);
        var project = Assert.Single(summary.Status.Projects, item => item.ProjectCode == "hbpos_api");
        Assert.Equal(latestReceivedAt, project.LastReceivedAtUtc);
        Assert.Equal(DateTimeKind.Utc, project.LastReceivedAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task GetSummaryAsync_响应模型不暴露项目Hash或Hash片段()
    {
        var keyHash = Sha256("绝不返回的项目密钥");
        var options = new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            Projects =
            [
                new()
                {
                    ProjectCode = "hbweb_rv",
                    ApiKeyHash = keyHash,
                    Enabled = true,
                },
            ],
        };

        var summary = await CreateService(options).GetSummaryAsync(new ApplicationLogQueryDto());
        var json = JsonSerializer.Serialize(
            summary,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );
        using var document = JsonDocument.Parse(json);
        var statusElement = document.RootElement.GetProperty("status");
        var statusPropertyNames = statusElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectPropertyNames = statusElement
            .GetProperty("projects")[1]
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(keyHash, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.Subset(
            new HashSet<string>(
                [
                    "backendCaptureEnabled",
                    "backendMinimumLevel",
                    "defaultProjectCode",
                    "defaultEnvironment",
                    "serviceName",
                    "projects",
                ],
                StringComparer.OrdinalIgnoreCase
            ),
            statusPropertyNames
        );
        Assert.Subset(
            new HashSet<string>(
                [
                    "projectCode",
                    "displayName",
                    "mode",
                    "explicitlyConfigured",
                    "enabled",
                    "credentialConfigured",
                    "configurationState",
                    "effectiveRetentionDays",
                    "lastReceivedAtUtc",
                ],
                StringComparer.OrdinalIgnoreCase
            ),
            projectPropertyNames
        );
        Assert.DoesNotContain(projectPropertyNames, name =>
            name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Fragment", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void Summary_继续要求SystemViewLogs权限()
    {
        var method = typeof(SystemLogsController).GetMethod(nameof(SystemLogsController.Summary));
        var authorize = Assert.Single(method!.GetCustomAttributes(typeof(AuthorizeAttribute), true)) as AuthorizeAttribute;

        Assert.NotNull(authorize);
        Assert.Equal("System.ViewLogs", authorize.Policy);
    }

    [Fact]
    public async Task CleanupExpiredLogsAsync_项目保留7天时删除8天前并保留7天内日志()
    {
        var now = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        await InsertLogAsync("HBBBackend", "Error", "/api/expired", "8天前日志", "expired", now.AddDays(-8));
        await InsertLogAsync("HBBBackend", "Error", "/api/kept", "7天内日志", "kept", now.AddDays(-6));

        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "HBBBackend",
                ApiKeyHash = Sha256("backend-secret"),
                RetentionDays = 7,
                Enabled = true,
            }
        );

        var deleted = await service.CleanupExpiredLogsAsync(now);

        Assert.Equal(1, deleted);
        var remaining = await _db.Queryable<ApplicationLog>().OrderBy(x => x.TraceId).ToListAsync();
        var item = Assert.Single(remaining);
        Assert.Equal("kept", item.TraceId);
    }

    [Fact]
    public async Task CleanupExpiredLogsAsync_配置项目码带空格_规范化写入并清理历史日志()
    {
        var now = DateTime.UtcNow;
        await InsertLogAsync(
            "hbweb_rv",
            "Error",
            "/api/expired",
            "历史日志",
            "expired-spaced-project",
            now.AddDays(-8),
            now.AddDays(-8)
        );
        var options = new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            DefaultRetentionDays = 7,
            Projects =
            [
                new()
                {
                    ProjectCode = "  hbweb_rv  ",
                    DisplayName = "Web前端",
                    ApiKeyHash = Sha256("web-secret"),
                    Enabled = true,
                    RetentionDays = 7,
                },
                new() { ProjectCode = "   ", Enabled = true, RetentionDays = 1 },
            ],
        };
        var service = CreateService(options);
        var authenticated = await service.AuthenticateProjectAsync("hbweb_rv", "web-secret");
        Assert.NotNull(authenticated);

        var ingest = await service.IngestAsync(
            authenticated.ProjectCode,
            new ApplicationLogIngestRequestDto
            {
                Logs = [CreateIngestItem("新日志")],
            }
        );
        var deleted = await service.CleanupExpiredLogsAsync(now);

        Assert.Equal(1, ingest.AcceptedCount);
        Assert.Equal(1, deleted);
        var remaining = Assert.Single(await _db.Queryable<ApplicationLog>().ToListAsync());
        Assert.Equal("hbweb_rv", remaining.ProjectCode);
        Assert.Equal("新日志", remaining.Message);
    }

    [Fact]
    public async Task CleanupExpiredLogsAsync_按项目保留天数删除过期日志()
    {
        var now = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        await InsertLogAsync("HBBBackend", "Error", "/api/old", "旧日志", "old", now.AddDays(-31));
        await InsertLogAsync("HBBBackend", "Error", "/api/new", "新日志", "new", now.AddDays(-29));
        await InsertLogAsync("HbwebExpo", "Error", "/api/mobile", "移动端旧日志", "mobile", now.AddDays(-8));

        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "HBBBackend",
                ApiKeyHash = Sha256("backend-secret"),
                RetentionDays = 30,
                Enabled = true,
            },
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "HbwebExpo",
                ApiKeyHash = Sha256("mobile-secret"),
                RetentionDays = 7,
                Enabled = true,
            }
        );

        var deleted = await service.CleanupExpiredLogsAsync(now);

        Assert.Equal(2, deleted);
        var remaining = await _db.Queryable<ApplicationLog>().OrderBy(x => x.TraceId).ToListAsync();
        var item = Assert.Single(remaining);
        Assert.Equal("new", item.TraceId);
    }

    [Fact]
    public async Task CleanupExpiredLogsAsync_客户端时间很旧但服务端刚接收_不删除日志()
    {
        var now = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        await InsertLogAsync(
            "hbpos_win",
            "Error",
            "/api/wpf",
            "离线后补传",
            "wpf-delayed",
            now.AddDays(-60),
            now.AddDays(-1)
        );
        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "hbpos_win",
                ApiKeyHash = Sha256("wpf-secret"),
                RetentionDays = 30,
                Enabled = true,
            }
        );

        var deleted = await service.CleanupExpiredLogsAsync(now);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _db.Queryable<ApplicationLog>().CountAsync());
    }

    [Fact]
    public async Task CleanupExpiredLogsAsync_恰好30天边界保留_更早一刻删除()
    {
        var now = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        await InsertLogAsync(
            "hbpos_win",
            "Information",
            "/api/wpf",
            "边界日志",
            "boundary",
            now.AddDays(-30),
            now.AddDays(-30));
        await InsertLogAsync(
            "hbpos_win",
            "Information",
            "/api/wpf",
            "已过期日志",
            "expired",
            now.AddDays(-30).AddTicks(-1),
            now.AddDays(-30).AddTicks(-1));
        var service = CreateService(
            new ApplicationLoggingProjectOptions
            {
                ProjectCode = "hbpos_win",
                ApiKeyHash = Sha256("wpf-secret"),
                RetentionDays = 30,
                Enabled = true,
            }
        );

        var deleted = await service.CleanupExpiredLogsAsync(now);

        Assert.Equal(1, deleted);
        var remaining = Assert.Single(await _db.Queryable<ApplicationLog>().ToListAsync());
        Assert.Equal("boundary", remaining.TraceId);
    }

    [Fact]
    public async Task CleanupExpiredLogsAsync_六个显式项目包含禁用项目_全部按各自保留天数清理()
    {
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        await InsertLogAsync("HBBBackend", "Error", "/old", "旧日志", "backend", now.AddDays(-8), now.AddDays(-8));
        await InsertLogAsync("hbweb_rv", "Error", "/old", "旧日志", "web", now.AddDays(-8), now.AddDays(-8));
        await InsertLogAsync("HbwebExpo", "Error", "/old", "旧日志", "mobile", now.AddDays(-8), now.AddDays(-8));
        await InsertLogAsync("hbpos_win", "Error", "/old", "旧日志", "pos", now.AddDays(-31), now.AddDays(-31));
        await InsertLogAsync("hbpos_api", "Error", "/old", "旧日志", "pos-api", now.AddDays(-8), now.AddDays(-8));
        await InsertLogAsync("hbpos_ipad", "Error", "/old", "旧日志", "pos-ipad", now.AddDays(-31), now.AddDays(-31));
        var options = new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            DefaultRetentionDays = 7,
            Projects =
            [
                new() { ProjectCode = "HBBBackend", Enabled = true, RetentionDays = 7 },
                new() { ProjectCode = "hbweb_rv", Enabled = true, RetentionDays = 7 },
                new() { ProjectCode = "HbwebExpo", Enabled = false, RetentionDays = 7 },
                new() { ProjectCode = "hbpos_win", Enabled = false, RetentionDays = 30 },
                new() { ProjectCode = "hbpos_api", Enabled = true, RetentionDays = 7 },
                new() { ProjectCode = "hbpos_ipad", Enabled = true, RetentionDays = 30 },
            ],
        };

        var deleted = await CreateService(options).CleanupExpiredLogsAsync(now);

        Assert.Equal(6, deleted);
        Assert.Equal(0, await _db.Queryable<ApplicationLog>().CountAsync());
    }

    private ApplicationLogService CreateService(params ApplicationLoggingProjectOptions[] projects)
    {
        return CreateService(_db, projects);
    }

    private ApplicationLogService CreateService(
        ApplicationLoggingOptions options,
        IApplicationLogQueue? queue = null
    )
    {
        return new ApplicationLogService(
            _db,
            Options.Create(options),
            NullLogger<ApplicationLogService>.Instance,
            queue
        );
    }

    private ApplicationLoggingOptions CreateIngestControllerOptions()
    {
        return new ApplicationLoggingOptions
        {
            DefaultProjectCode = "HBBBackend",
            Projects =
            [
                new ApplicationLoggingProjectOptions
                {
                    ProjectCode = "HBBBackend",
                    DisplayName = "后端",
                    ApiKeyHash = Sha256("backend-secret"),
                    Enabled = true,
                },
            ],
        };
    }

    private static JsonSerializerOptions CreateAspNetCompatibleJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
        };
    }

    private SystemLogsController CreateIngestController(
        ApplicationLoggingOptions options,
        IMemoryCache cache
    )
    {
        var optionsMonitor = new Mock<IOptionsMonitor<ApplicationLoggingOptions>>();
        optionsMonitor.SetupGet(item => item.CurrentValue).Returns(options);
        var controller = new SystemLogsController(
            CreateService(options),
            new ApplicationLogRateLimiter(cache, optionsMonitor.Object),
            NullLogger<SystemLogsController>.Instance
        );
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Log-Project"] = "HBBBackend";
        context.Request.Headers["X-Log-Key"] = "backend-secret";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static void AssertProjectStatus(
        IReadOnlyCollection<ApplicationLogProjectStatusDto> projects,
        string projectCode,
        bool enabled,
        bool credentialConfigured,
        string configurationState
    )
    {
        var project = Assert.Single(projects, item => item.ProjectCode == projectCode);
        Assert.Equal("External", project.Mode);
        Assert.True(project.ExplicitlyConfigured);
        Assert.Equal(enabled, project.Enabled);
        Assert.Equal(credentialConfigured, project.CredentialConfigured);
        Assert.Equal(configurationState, project.ConfigurationState);
    }

    private static ApplicationLogService CreateService(
        ISqlSugarClient db,
        params ApplicationLoggingProjectOptions[] projects
    )
    {
        var options = Options.Create(
            new ApplicationLoggingOptions
            {
                DefaultProjectCode = "HBBBackend",
                DefaultRetentionDays = 7,
                MaxBatchSize = 200,
                Projects = projects.Length > 0
                    ? projects.ToList()
                    : new List<ApplicationLoggingProjectOptions>
                    {
                        new()
                        {
                            ProjectCode = "HBBBackend",
                            DisplayName = "后端",
                            ApiKeyHash = Sha256("backend-secret"),
                            Enabled = true,
                            RetentionDays = 7,
                        },
                    },
            }
        );

        return new ApplicationLogService(db, options, NullLogger<ApplicationLogService>.Instance);
    }

    private async Task InsertLogAsync(
        string projectCode,
        string level,
        string path,
        string message,
        string traceId,
        DateTime? timestampUtc = null,
        DateTime? createdAtUtc = null
    )
    {
        await _db.Insertable(
                new ApplicationLog
                {
                    ProjectCode = projectCode,
                    ProjectName = projectCode,
                    Environment = "Development",
                    SourceType = "Backend",
                    Level = level,
                    Category = "Test",
                    Message = message,
                    RequestPath = path,
                    TraceId = traceId,
                    TimestampUtc = timestampUtc ?? DateTime.UtcNow,
                    CreatedAt = createdAtUtc ?? timestampUtc ?? DateTime.UtcNow,
                }
            )
            .ExecuteCommandAsync();
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static ApplicationLogIngestItemDto CreateIngestItem(
        string message,
        Guid? clientEventId = null
    )
    {
        return new ApplicationLogIngestItemDto
        {
            ClientEventId = clientEventId,
            Level = "Error",
            Message = message,
            TimestampUtc = DateTime.UtcNow,
            ProjectCode = "HBBBackend",
            Environment = "Development",
            SourceType = "Backend",
        };
    }

    private ISqlSugarClient CreateSqliteClient()
    {
        var db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = $"DataSource={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        db.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        return db;
    }

    private void CreateApplicationLogTable()
    {
        _db.Ado.ExecuteCommand(
            """
            CREATE TABLE ApplicationLog (
                Id TEXT PRIMARY KEY,
                TimestampUtc TEXT NOT NULL,
                ProjectCode TEXT NOT NULL,
                ProjectName TEXT NULL,
                Environment TEXT NOT NULL,
                SourceType TEXT NOT NULL,
                ServiceName TEXT NULL,
                InstanceId TEXT NULL,
                ClientEventId TEXT NULL,
                StoreCode TEXT NULL,
                DeviceCode TEXT NULL,
                AppVersion TEXT NULL,
                Level TEXT NOT NULL,
                Category TEXT NULL,
                EventId TEXT NULL,
                Message TEXT NOT NULL,
                ExceptionType TEXT NULL,
                ExceptionMessage TEXT NULL,
                StackTrace TEXT NULL,
                RequestPath TEXT NULL,
                RequestMethod TEXT NULL,
                StatusCode INTEGER NULL,
                TraceId TEXT NULL,
                UserId TEXT NULL,
                UserName TEXT NULL,
                ClientIp TEXT NULL,
                PropertiesJson TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CreatedBy TEXT NULL,
                UpdatedAt TEXT NULL,
                UpdatedBy TEXT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0
            )
            """
        );
        _db.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IX_ApplicationLog_ProjectCode_ClientEventId ON ApplicationLog(ProjectCode, ClientEventId) WHERE ClientEventId IS NOT NULL"
        );
        _db.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
