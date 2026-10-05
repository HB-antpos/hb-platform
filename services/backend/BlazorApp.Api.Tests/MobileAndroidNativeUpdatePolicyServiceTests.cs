using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// Mobile 安卓原生最低构建号策略：判定矩阵、保存校验、幂等 / 乐观锁、DTO 全字段输出与权限特性。
/// 公开包筛选走真实 MobileAppBuildService（SQLite），确保与匿名 android-latest 同一口径。
/// </summary>
public sealed class MobileAndroidNativeUpdatePolicyServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"mobile-android-policy-{Guid.NewGuid():N}.db"
    );
    private readonly ISqlSugarClient _db;

    public MobileAndroidNativeUpdatePolicyServiceTests()
    {
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = $"DataSource={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        _db.CodeFirst.InitTables(typeof(MobileAndroidNativeUpdatePolicy), typeof(MobileAppBuild));
    }

    [Fact]
    public async Task 判定_无策略时返回none且policyVersion为none其余字段null()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var decision = await CreateService().GetDecisionAsync("50");

        Assert.Equal(AppUpdateStates.None, decision.State);
        Assert.Equal("none", decision.PolicyVersion);
        Assert.Null(decision.MinimumSupportedBuildNumber);
        Assert.Null(decision.LatestVersion);
        Assert.Null(decision.LatestBuildNumber);
        Assert.Null(decision.ReleaseMessage);
    }

    [Fact]
    public async Task 判定矩阵_按启用_minimum_build与公开包返回required或none()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        var saved = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 60,
                ReleaseMessage = "  请安装新版  ",
            },
            "admin"
        );
        Assert.True(saved.Success, saved.Message);

        (string? Build, string Expected)[] cases =
        {
            ("59", AppUpdateStates.Required),
            ("1", AppUpdateStates.Required),
            ("60", AppUpdateStates.None),
            ("63", AppUpdateStates.None),
            ("64", AppUpdateStates.None),
            // build 缺失或非法一律 fail-open。
            (null, AppUpdateStates.None),
            ("", AppUpdateStates.None),
            ("abc", AppUpdateStates.None),
            ("0", AppUpdateStates.None),
            ("-5", AppUpdateStates.None),
            (" 59", AppUpdateStates.None),
            ("59.0", AppUpdateStates.None),
            ("99999999999", AppUpdateStates.None),
        };
        foreach (var item in cases)
        {
            var decision = await service.GetDecisionAsync(item.Build);
            Assert.True(item.Expected == decision.State, $"build={item.Build ?? "<null>"}");
            Assert.Equal("1", decision.PolicyVersion);
            Assert.Equal(60, decision.MinimumSupportedBuildNumber);
            Assert.Equal("1.0.10", decision.LatestVersion);
            Assert.Equal(63, decision.LatestBuildNumber);
            Assert.Equal("请安装新版", decision.ReleaseMessage);
        }
    }

    [Fact]
    public async Task 判定_公开包低于minimum时返回none()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        Assert.True(
            (
                await service.SetPolicyAsync(
                    new MobileAndroidNativeUpdatePolicyRequest
                    {
                        ExpectedPolicyVersion = 0,
                        Enabled = true,
                        MinimumSupportedBuildNumber = 63,
                    },
                    "admin"
                )
            ).Success
        );

        // 保存后公开包被撤下（镜像不安全），最新可装包退回 62：不能把设备拦在装不上的 63 前。
        var current = await _db.Queryable<MobileAppBuild>().FirstAsync(item => item.AppBuildVersion == "63");
        current.CosMirrorStatus = MobileAppBuildService.CosMirrorStatusUnsafe;
        await _db.Updateable(current).ExecuteCommandAsync();
        await SeedPublicBuildAsync("62", "1.0.9", completedAt: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var decision = await service.GetDecisionAsync("50");

        Assert.Equal(AppUpdateStates.None, decision.State);
        Assert.Equal(62, decision.LatestBuildNumber);
        Assert.Equal("1.0.9", decision.LatestVersion);
        Assert.Equal(63, decision.MinimumSupportedBuildNumber);
    }

    [Fact]
    public async Task 判定_没有公开包或自动更新开关关闭时返回none()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        Assert.True(
            (
                await service.SetPolicyAsync(
                    new MobileAndroidNativeUpdatePolicyRequest
                    {
                        ExpectedPolicyVersion = 0,
                        Enabled = true,
                        MinimumSupportedBuildNumber = 63,
                    },
                    "admin"
                )
            ).Success
        );

        var switchedOff = await CreateService(publicAndroidUpdatesEnabled: false).GetDecisionAsync("50");
        Assert.Equal(AppUpdateStates.None, switchedOff.State);
        Assert.Null(switchedOff.LatestBuildNumber);
        Assert.Null(switchedOff.LatestVersion);

        await _db.Deleteable<MobileAppBuild>().ExecuteCommandAsync();
        var noBuild = await service.GetDecisionAsync("50");
        Assert.Equal(AppUpdateStates.None, noBuild.State);
        Assert.Equal("1", noBuild.PolicyVersion);
        Assert.Null(noBuild.LatestBuildNumber);
    }

    [Fact]
    public async Task 判定_停用策略返回none并带policyVersion与公开包()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 63,
            },
            "admin"
        );
        var disabled = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = false,
                MinimumSupportedBuildNumber = 63,
            },
            "admin"
        );
        Assert.True(disabled.Success);

        var decision = await service.GetDecisionAsync("10");

        Assert.Equal(AppUpdateStates.None, decision.State);
        Assert.Equal("2", decision.PolicyVersion);
        Assert.Null(decision.MinimumSupportedBuildNumber);
        Assert.Equal(63, decision.LatestBuildNumber);
    }

    [Fact]
    public async Task 公开包筛选_与androidLatest同一口径且versionCode非法时视为无包()
    {
        // 非 production、非 mobile AppKey、镜像未完成、摘要非法的包都不算公开包。
        await SeedPublicBuildAsync("90", "2.0.0", profile: "preview");
        await SeedPublicBuildAsync("91", "2.0.0", appKey: "pos-handheld");
        await SeedPublicBuildAsync("92", "2.0.0", mirrorStatus: MobileAppBuildService.CosMirrorStatusPending);
        await SeedPublicBuildAsync("93", "2.0.0", sha256: new string('g', 64));
        await SeedPublicBuildAsync("63", "1.0.10", completedAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var policy = await CreateService().GetPolicyAsync();
        Assert.Equal(63, policy.Data!.LatestBuild!.AppBuildVersion);
        Assert.Equal("1.0.10", policy.Data.LatestBuild.AppVersion);

        // 最新公开包 versionCode 不是正整数：管理端不显示、判定也不会 required。
        await SeedPublicBuildAsync("v64", "1.0.11", completedAt: new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
        var invalid = await CreateService().GetPolicyAsync();
        Assert.Null(invalid.Data!.LatestBuild);
    }

    [Fact]
    public async Task 保存校验_各错误码()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();

        var versionRequired = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest { Enabled = true, MinimumSupportedBuildNumber = 60 },
            "admin"
        );
        Assert.Equal(AppUpdatePolicyErrorCodes.VersionRequired, versionRequired.ErrorCode);

        var tooLong = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 60,
                ReleaseMessage = new string('a', 1001),
            },
            "admin"
        );
        Assert.Equal("RELEASE_MESSAGE_TOO_LONG", tooLong.ErrorCode);

        var required = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest { ExpectedPolicyVersion = 0, Enabled = true },
            "admin"
        );
        Assert.Equal("MINIMUM_BUILD_REQUIRED", required.ErrorCode);

        foreach (var invalidMinimum in new[] { 0, -1 })
        {
            var invalid = await service.SetPolicyAsync(
                new MobileAndroidNativeUpdatePolicyRequest
                {
                    ExpectedPolicyVersion = 0,
                    Enabled = true,
                    MinimumSupportedBuildNumber = invalidMinimum,
                },
                "admin"
            );
            Assert.Equal("MINIMUM_BUILD_INVALID", invalid.ErrorCode);
        }

        var above = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 64,
            },
            "admin"
        );
        Assert.Equal("MINIMUM_BUILD_ABOVE_LATEST", above.ErrorCode);

        // 等于公开包 versionCode 允许保存。
        var equal = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 63,
                ReleaseMessage = new string('a', 1000),
            },
            "admin"
        );
        Assert.True(equal.Success, equal.Message);

        // 没有公开包时不能启用。
        await _db.Deleteable<MobileAppBuild>().ExecuteCommandAsync();
        var noBuild = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = true,
                MinimumSupportedBuildNumber = 10,
            },
            "admin"
        );
        Assert.Equal("MINIMUM_BUILD_ABOVE_LATEST", noBuild.ErrorCode);

        // 校验失败一律不写库。
        var row = await _db.Queryable<MobileAndroidNativeUpdatePolicy>().SingleAsync(item => !item.IsDeleted);
        Assert.Equal(1, row.PolicyVersion);
        Assert.Equal(63, row.MinimumSupportedBuildNumber);
    }

    [Fact]
    public async Task 保存_无公开包时仍可停用()
    {
        var saved = await CreateService().SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest { ExpectedPolicyVersion = 0, Enabled = false },
            "admin"
        );

        Assert.True(saved.Success);
        Assert.False(saved.Data!.Enabled);
        Assert.Equal(0, saved.Data.PolicyVersion);
        Assert.Null(saved.Data.LatestBuild);
        // 无行且提交停用 = 与默认状态相同，幂等不建行。
        Assert.Equal(0, await _db.Queryable<MobileAndroidNativeUpdatePolicy>().CountAsync());
    }

    [Fact]
    public async Task 保存_内容相同时幂等不加版本_变化时加一()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        var request = new MobileAndroidNativeUpdatePolicyRequest
        {
            ExpectedPolicyVersion = 0,
            Enabled = true,
            MinimumSupportedBuildNumber = 62,
            ReleaseMessage = "更新",
        };

        var first = await service.SetPolicyAsync(request, "admin");
        Assert.Equal(1, first.Data!.PolicyVersion);
        Assert.Equal("admin", first.Data.UpdatedBy);
        Assert.NotNull(first.Data.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, first.Data.UpdatedAt!.Value.Kind);

        // 相同内容（说明只差前后空白）即使带旧版本号也幂等返回，不推进版本。
        request.ExpectedPolicyVersion = 0;
        request.ReleaseMessage = " 更新 ";
        var repeated = await service.SetPolicyAsync(request, "other");
        Assert.True(repeated.Success);
        Assert.Equal(1, repeated.Data!.PolicyVersion);
        Assert.Equal("admin", repeated.Data.UpdatedBy);

        request.ExpectedPolicyVersion = 1;
        request.MinimumSupportedBuildNumber = 63;
        var changed = await service.SetPolicyAsync(request, "other");
        Assert.True(changed.Success);
        Assert.Equal(2, changed.Data!.PolicyVersion);
        Assert.Equal(63, changed.Data.MinimumSupportedBuildNumber);
        Assert.Equal("other", changed.Data.UpdatedBy);
        Assert.Equal(1, await _db.Queryable<MobileAndroidNativeUpdatePolicy>().CountAsync());
    }

    [Fact]
    public async Task 保存_版本不一致返回冲突且带实际版本()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 60,
            },
            "admin"
        );

        var conflict = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 61,
            },
            "admin"
        );

        Assert.False(conflict.Success);
        Assert.Equal(AppUpdatePolicyErrorCodes.VersionConflict, conflict.ErrorCode);
        var details = JsonSerializer.SerializeToElement(conflict.Details);
        Assert.Equal(0, details.GetProperty("ExpectedPolicyVersion").GetInt64());
        Assert.Equal(1, details.GetProperty("ActualPolicyVersion").GetInt64());

        var missing = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest { Enabled = false },
            "admin"
        );
        var missingDetails = JsonSerializer.SerializeToElement(missing.Details);
        Assert.Equal(JsonValueKind.Null, missingDetails.GetProperty("ExpectedPolicyVersion").ValueKind);
        Assert.Equal(1, missingDetails.GetProperty("ActualPolicyVersion").GetInt64());

        var row = await _db.Queryable<MobileAndroidNativeUpdatePolicy>().SingleAsync(item => !item.IsDeleted);
        Assert.Equal(60, row.MinimumSupportedBuildNumber);
    }

    [Fact]
    public async Task 保存_停用时清空最低构建号与说明()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 60,
                ReleaseMessage = "说明",
            },
            "admin"
        );

        var disabled = await service.SetPolicyAsync(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = false,
                MinimumSupportedBuildNumber = 60,
                ReleaseMessage = "说明",
            },
            "admin"
        );

        Assert.True(disabled.Success);
        Assert.False(disabled.Data!.Enabled);
        Assert.Null(disabled.Data.MinimumSupportedBuildNumber);
        Assert.Null(disabled.Data.ReleaseMessage);
        Assert.Equal(2, disabled.Data.PolicyVersion);
        var row = await _db.Queryable<MobileAndroidNativeUpdatePolicy>().SingleAsync(item => !item.IsDeleted);
        Assert.False(row.Enabled);
        Assert.Null(row.MinimumSupportedBuildNumber);
        Assert.Null(row.ReleaseMessage);
    }

    [Fact]
    public async Task 管理读取_无行返回默认值并带公开包()
    {
        await SeedPublicBuildAsync(
            "63",
            "1.0.10",
            easBuildId: "eas-63",
            completedAt: new DateTime(2026, 10, 3, 5, 6, 0, DateTimeKind.Utc)
        );

        var response = await CreateService().GetPolicyAsync();

        Assert.True(response.Success);
        Assert.False(response.Data!.Enabled);
        Assert.Equal(0, response.Data.PolicyVersion);
        Assert.Null(response.Data.MinimumSupportedBuildNumber);
        var latest = response.Data.LatestBuild!;
        Assert.Equal("eas-63", latest.EasBuildId);
        Assert.Equal("1.0.10", latest.AppVersion);
        Assert.Equal(63, latest.AppBuildVersion);
        Assert.Equal(new DateTime(2026, 10, 3, 5, 6, 0, DateTimeKind.Utc), latest.CompletedAt);
        Assert.Equal(DateTimeKind.Utc, latest.CompletedAt!.Value.Kind);
    }

    [Fact]
    public void DTO_即使全局忽略null也输出全部字段()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        using var decision = JsonDocument.Parse(
            JsonSerializer.Serialize(new MobileAndroidNativeUpdateDecisionDto(), options)
        );
        Assert.Equal(
            [
                "state",
                "policyVersion",
                "minimumSupportedBuildNumber",
                "latestVersion",
                "latestBuildNumber",
                "releaseMessage",
            ],
            decision.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal("none", decision.RootElement.GetProperty("state").GetString());
        Assert.Equal("none", decision.RootElement.GetProperty("policyVersion").GetString());

        using var policy = JsonDocument.Parse(
            JsonSerializer.Serialize(new MobileAndroidNativeUpdatePolicyDto(), options)
        );
        Assert.Equal(
            [
                "enabled",
                "minimumSupportedBuildNumber",
                "releaseMessage",
                "policyVersion",
                "updatedAt",
                "updatedBy",
                "latestBuild",
            ],
            policy.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
        );

        using var latest = JsonDocument.Parse(
            JsonSerializer.Serialize(
                new MobileAndroidLatestBuildDto
                {
                    EasBuildId = "eas-63",
                    AppBuildVersion = 63,
                    CompletedAt = new DateTime(2026, 10, 3, 5, 6, 0, DateTimeKind.Utc),
                },
                options
            )
        );
        Assert.Equal(
            ["easBuildId", "appVersion", "appBuildVersion", "completedAt"],
            latest.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal(JsonValueKind.Number, latest.RootElement.GetProperty("appBuildVersion").ValueKind);
        Assert.Equal("2026-10-03T05:06:00Z", latest.RootElement.GetProperty("completedAt").GetString());
    }

    [Fact]
    public void 权限特性_管理读需View_写需Manage_公开判定匿名()
    {
        AssertRoute<AppUpdatePoliciesController, HttpGetAttribute>(
            nameof(AppUpdatePoliciesController.GetMobileAndroid),
            "mobile-android"
        );
        AssertRoute<AppUpdatePoliciesController, HttpPutAttribute>(
            nameof(AppUpdatePoliciesController.PutMobileAndroid),
            "mobile-android"
        );
        Assert.Equal(
            Permissions.System.ViewAppDownloads,
            GetMethod<AppUpdatePoliciesController>(nameof(AppUpdatePoliciesController.GetMobileAndroid))
                .GetCustomAttribute<AuthorizeAttribute>()!
                .Policy
        );
        Assert.Equal(
            Permissions.System.ManageAppDownloads,
            GetMethod<AppUpdatePoliciesController>(nameof(AppUpdatePoliciesController.PutMobileAndroid))
                .GetCustomAttribute<AuthorizeAttribute>()!
                .Policy
        );

        Assert.Equal(
            "api/app-updates/mobile-android",
            typeof(MobileAndroidAppUpdatesController).GetCustomAttribute<RouteAttribute>()!.Template
        );
        var check = GetMethod<MobileAndroidAppUpdatesController>(
            nameof(MobileAndroidAppUpdatesController.Check)
        );
        Assert.NotNull(check.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(check.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(typeof(MobileAndroidAppUpdatesController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public async Task 控制器_版本冲突映射409_校验错误200_公开判定包ApiResponse()
    {
        await SeedPublicBuildAsync("63", "1.0.10");
        var service = CreateService();
        var controller = new AppUpdatePoliciesController(
            new Moq.Mock<INativeAppUpdatePolicyService>().Object,
            mobileAndroidService: service
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var conflict = await controller.PutMobileAndroid(
            new MobileAndroidNativeUpdatePolicyRequest { Enabled = true, MinimumSupportedBuildNumber = 60 }
        );
        Assert.IsType<ConflictObjectResult>(conflict);

        var invalid = await controller.PutMobileAndroid(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 64,
            }
        );
        var invalidBody = Assert.IsType<ApiResponse<MobileAndroidNativeUpdatePolicyDto>>(
            Assert.IsType<OkObjectResult>(invalid).Value
        );
        Assert.Equal("MINIMUM_BUILD_ABOVE_LATEST", invalidBody.ErrorCode);

        var saved = await controller.PutMobileAndroid(
            new MobileAndroidNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                MinimumSupportedBuildNumber = 63,
            }
        );
        Assert.True(
            Assert.IsType<ApiResponse<MobileAndroidNativeUpdatePolicyDto>>(
                Assert.IsType<OkObjectResult>(saved).Value
            ).Success
        );

        var publicResult = await new MobileAndroidAppUpdatesController(service).Check("62");
        var publicBody = Assert.IsType<ApiResponse<MobileAndroidNativeUpdateDecisionDto>>(
            Assert.IsType<OkObjectResult>(publicResult).Value
        );
        Assert.True(publicBody.Success);
        Assert.Equal(AppUpdateStates.Required, publicBody.Data!.State);
        Assert.Equal("1", publicBody.Data.PolicyVersion);
    }

    private MobileAndroidNativeUpdatePolicyService CreateService(bool publicAndroidUpdatesEnabled = true) =>
        new(
            _db,
            new MobileAppBuildService(
                _db,
                Options.Create(new EasWebhookOptions()),
                NullLogger<MobileAppBuildService>.Instance
            ),
            NullLogger<MobileAndroidNativeUpdatePolicyService>.Instance,
            Options.Create(
                new MobileAppBuildOptions { PublicAndroidUpdatesEnabled = publicAndroidUpdatesEnabled }
            )
        );

    private async Task SeedPublicBuildAsync(
        string appBuildVersion,
        string appVersion,
        string? easBuildId = null,
        string profile = "production",
        string appKey = "mobile",
        string mirrorStatus = MobileAppBuildService.CosMirrorStatusSucceeded,
        string? sha256 = null,
        DateTime? completedAt = null
    )
    {
        var id = easBuildId ?? $"eas-{Guid.NewGuid():N}";
        await _db.Insertable(
                new MobileAppBuild
                {
                    Id = Guid.NewGuid(),
                    AppKey = appKey,
                    EasBuildId = id,
                    AccountName = "hb",
                    ProjectName = "hbweb-expo",
                    Platform = "android",
                    Status = "finished",
                    BuildProfile = profile,
                    AppVersion = appVersion,
                    AppBuildVersion = appBuildVersion,
                    ArtifactUrl = $"https://expo.dev/artifacts/{id}.apk",
                    CosArtifactUrl = $"https://downloads.example/{id}.apk",
                    CosMirrorStatus = mirrorStatus,
                    ArtifactSha256 = sha256 ?? new string('a', 64),
                    ArtifactSize = 4096,
                    CompletedAt = completedAt ?? new DateTime(2026, 10, 3, 5, 6, 0, DateTimeKind.Utc),
                    ReceivedAt = DateTime.UtcNow,
                }
            )
            .ExecuteCommandAsync();
    }

    private static void AssertRoute<TController, TAttribute>(string methodName, string template)
        where TAttribute : HttpMethodAttribute
    {
        Assert.Equal(template, GetMethod<TController>(methodName).GetCustomAttribute<TAttribute>()!.Template);
    }

    private static MethodInfo GetMethod<TController>(string methodName) =>
        typeof(TController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{typeof(TController).Name}.{methodName} missing.");

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
