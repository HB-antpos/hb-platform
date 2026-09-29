using System.Reflection;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class PosHandheldUpdatePolicyServiceTests : IDisposable
{
    private readonly string dbPath = Path.Combine(
        Path.GetTempPath(),
        $"pos-handheld-web-policy-{Guid.NewGuid():N}.db"
    );
    private readonly ISqlSugarClient db;
    private readonly PosHandheldUpdatePolicyOptions policyOptions = new()
    {
        Enabled = true,
        PolicyVersion = "legacy-12",
        EasProjectName = "hb-pos-handheld",
        AndroidProfile = "production",
        AndroidPackageName = "com.hbweb.poshandheld",
        AndroidSigningCertificateSha256 = new string('b', 64),
        IosLatestVersion = "3.0.0",
        IosLatestBuild = "300",
        IosDistribution = "app-store",
        IosDownloadUrl = "https://apps.apple.com/au/app/id123456789",
        IosBundleIdentifier = "com.hbweb.poshandheld",
        IosAppStoreId = "123456789",
        OtaChannel = "pos-handheld-production",
    };
    private readonly EasWebhookOptions easOptions = new()
    {
        ProjectAppKeys = new Dictionary<string, string>
        {
            ["hb-pos-handheld"] = MobileAppKeys.PosHandheld,
            ["hb-mobile"] = MobileAppKeys.Mobile,
        },
    };

    public PosHandheldUpdatePolicyServiceTests()
    {
        db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = $"DataSource={dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        db.CodeFirst.InitTables(
            typeof(MobileAppBuild),
            typeof(MobileAppOtaUpdate),
            typeof(AppOtaRelease),
            typeof(IosAppStoreRelease),
            typeof(PosHandheldUpdatePolicy),
            typeof(PosHandheldUpdatePolicyRevision)
        );
    }

    [Fact]
    public async Task Android策略_绑定精确候选且no_op不升版不追加审计()
    {
        var build = await SeedBuildAsync(
            MobileAppKeys.PosHandheld,
            "hb-pos-handheld",
            "production",
            "2.0.0",
            "200"
        );
        var service = CreateService();
        var request = new PosHandheldUpdatePolicyRequest
        {
            ExpectedPolicyVersion = 0,
            Enabled = true,
            Required = false,
            CandidateId = build.Id,
            MinimumSupportedVersion = "1.5.0",
            MinimumSupportedBuildNumber = 150,
            ReleaseMessage = " 手持 Android 新版 ",
        };

        var first = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            request,
            "admin"
        );
        var repeated = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            new PosHandheldUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = true,
                Required = false,
                CandidateId = build.Id,
                MinimumSupportedVersion = " 1.5.0 ",
                MinimumSupportedBuildNumber = 150,
                ReleaseMessage = "手持 Android 新版",
            },
            "publisher"
        );

        Assert.True(first.Success);
        Assert.Equal(1, first.Data!.PolicyVersion);
        Assert.Equal(build.Id, first.Data.CandidateId);
        Assert.Equal(1, repeated.Data!.PolicyVersion);
        Assert.Single(await db.Queryable<PosHandheldUpdatePolicyRevision>().ToListAsync());
    }

    [Fact]
    public async Task 管理策略_候选事实漂移后总览必须标记绑定失效()
    {
        var build = await SeedBuildAsync(
            MobileAppKeys.PosHandheld,
            "hb-pos-handheld",
            "production",
            "2.0.0",
            "200"
        );
        var service = CreateService();
        var saved = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            EnabledRequest(build.Id),
            "admin"
        );
        build.ArtifactSha256 = new string('b', 64);
        await db.Updateable(build).ExecuteCommandAsync();

        var policies = await service.GetPoliciesAsync();
        var policy = policies.Data!.Single(item =>
            item.Lane == PosHandheldUpdateLanes.AndroidNative
        );

        Assert.True(saved.Success);
        Assert.False(policy.CandidateValid);
        Assert.NotNull(policy.Candidate);
        Assert.Equal(build.Id, policy.Candidate!.Id);
        Assert.Equal(new string('b', 64), policy.Candidate.Sha256);
        Assert.Equal(
            PosHandheldUpdatePolicyErrorCodes.CandidateFingerprintMismatch,
            policy.BlockedReason
        );
    }

    [Fact]
    public async Task 设备决策解析_按候选主键读取且不受管理目录上限影响()
    {
        var selected = await SeedBuildAsync(
            MobileAppKeys.PosHandheld,
            "hb-pos-handheld",
            "production",
            "2.0.0",
            "200"
        );
        var service = CreateService();
        var saved = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            EnabledRequest(selected.Id),
            "admin"
        );
        var baseTime = DateTime.UtcNow;
        var newerBuilds = Enumerable.Range(1, 205).Select(index =>
            new MobileAppBuild
            {
                Id = Guid.NewGuid(),
                AppKey = MobileAppKeys.PosHandheld,
                EasBuildId = Guid.NewGuid().ToString(),
                AccountName = "hotbargain",
                ProjectName = "hb-pos-handheld",
                Platform = "android",
                Status = "finished",
                BuildProfile = "production",
                AppVersion = "3.0.0",
                AppBuildVersion = (300 + index).ToString(),
                ArtifactUrl = "https://downloads.example/handheld-newer.apk",
                CosArtifactUrl = "https://downloads.example/handheld-newer.apk",
                ArtifactSha256 = new string('c', 64),
                ArtifactSize = 4096,
                CosMirrorStatus = MobileAppBuildService.CosMirrorStatusSucceeded,
                CompletedAt = baseTime.AddSeconds(index),
                ExpirationDate = baseTime.AddDays(30),
                ReceivedAt = baseTime.AddSeconds(index),
                CreatedAt = baseTime.AddSeconds(index),
                IsDeleted = false,
            }
        ).ToList();
        await db.Insertable(newerBuilds).ExecuteCommandAsync();

        var resolved = await service.ResolveManagedLaneAsync(
            PosHandheldUpdateLanes.AndroidNative
        );

        Assert.True(saved.Success);
        Assert.NotNull(resolved);
        Assert.True(resolved!.CandidateValid);
        Assert.Equal(selected.Id, resolved.Candidate!.Id);
    }

    [Fact]
    public async Task 策略写入_拒绝跨AppKey和旧Android内部候选()
    {
        var mobile = await SeedBuildAsync(
            MobileAppKeys.Mobile,
            "hb-mobile",
            "android-internal",
            "9.0.0",
            "900"
        );
        var legacyInternal = await SeedBuildAsync(
            MobileAppKeys.PosHandheld,
            "hb-pos-handheld",
            "android-internal",
            "2.0.0",
            "200"
        );
        var service = CreateService();

        var wrongApp = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            EnabledRequest(mobile.Id),
            "admin"
        );
        var wrongProfile = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            EnabledRequest(legacyInternal.Id),
            "admin"
        );

        Assert.False(wrongApp.Success);
        Assert.Equal(PosHandheldUpdatePolicyErrorCodes.CandidateInvalid, wrongApp.ErrorCode);
        Assert.False(wrongProfile.Success);
        Assert.Equal(PosHandheldUpdatePolicyErrorCodes.CandidateInvalid, wrongProfile.ErrorCode);
    }

    [Fact]
    public async Task 原生策略_拒绝候选无法兑现的最低版本或build()
    {
        var build = await SeedBuildAsync(
            MobileAppKeys.PosHandheld,
            "hb-pos-handheld",
            "production",
            "2.0.0",
            "200"
        );
        var service = CreateService();

        var versionTooHigh = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            new PosHandheldUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                CandidateId = build.Id,
                MinimumSupportedVersion = "3.0.0",
            },
            "admin"
        );
        var buildTooHigh = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            new PosHandheldUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                CandidateId = build.Id,
                MinimumSupportedVersion = "2.0.0",
                MinimumSupportedBuildNumber = 201,
            },
            "admin"
        );

        Assert.False(versionTooHigh.Success);
        Assert.Equal(
            PosHandheldUpdatePolicyErrorCodes.NativeMinimumInvalid,
            versionTooHigh.ErrorCode
        );
        Assert.False(buildTooHigh.Success);
        Assert.Equal(
            PosHandheldUpdatePolicyErrorCodes.NativeMinimumInvalid,
            buildTooHigh.ErrorCode
        );
    }

    [Fact]
    public async Task Android候选_空Cos地址不能绕过原始产物过期门禁()
    {
        var expired = await SeedBuildAsync(
            MobileAppKeys.PosHandheld,
            "hb-pos-handheld",
            "production",
            "2.0.0",
            "200"
        );
        expired.CosArtifactUrl = string.Empty;
        expired.ExpirationDate = DateTime.UtcNow.AddMinutes(-1);
        await db.Updateable(expired).ExecuteCommandAsync();

        var candidates = await CreateService().GetCandidatesAsync(
            PosHandheldUpdateLanes.AndroidNative
        );

        Assert.Empty(candidates.Data!);
    }

    [Fact]
    public async Task Ota策略_只能激活当前head且新head到达后旧策略失效()
    {
        var old = await SeedOtaAsync("android-old", new DateTime(2026, 8, 10, 1, 0, 0, DateTimeKind.Utc));
        var head = await SeedOtaAsync("android-head", new DateTime(2026, 8, 10, 2, 0, 0, DateTimeKind.Utc));
        var service = CreateService();

        var rejected = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidOta,
            EnabledRequest(old.Id),
            "admin"
        );
        var activated = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidOta,
            EnabledRequest(head.Id),
            "admin"
        );
        var beforeNewHead = await service.ResolveManagedLaneAsync(
            PosHandheldUpdateLanes.AndroidOta
        );
        await SeedOtaAsync("android-new-head", new DateTime(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc));
        var afterNewHead = await service.ResolveManagedLaneAsync(
            PosHandheldUpdateLanes.AndroidOta
        );

        Assert.False(rejected.Success);
        Assert.Equal(PosHandheldUpdatePolicyErrorCodes.OtaCandidateNotChannelHead, rejected.ErrorCode);
        Assert.True(activated.Success);
        Assert.True(beforeNewHead!.CandidateValid);
        Assert.False(afterNewHead!.CandidateValid);
    }

    [Fact]
    public async Task Disabled策略_建立受管lane且不回落旧配置()
    {
        var service = CreateService();

        var saved = await service.SetLaneAsync(
            PosHandheldUpdateLanes.IosNative,
            new PosHandheldUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = false,
                Required = true,
                CandidateId = Guid.NewGuid(),
                MinimumSupportedVersion = "9.0.0",
                MinimumSupportedBuildNumber = 900,
            },
            "admin"
        );
        var resolved = await service.ResolveManagedLaneAsync(
            PosHandheldUpdateLanes.IosNative
        );

        Assert.True(saved.Success);
        Assert.True(saved.Data!.Managed);
        Assert.False(saved.Data.Enabled);
        Assert.False(saved.Data.Required);
        Assert.Null(saved.Data.CandidateId);
        Assert.NotNull(resolved);
        Assert.False(resolved!.Policy.Enabled);
    }

    [Fact]
    public async Task 策略写入_缺少版本与真实冲突返回冻结错误码()
    {
        var build = await SeedBuildAsync(
            MobileAppKeys.PosHandheld,
            "hb-pos-handheld",
            "production",
            "2.0.0",
            "200"
        );
        var service = CreateService();

        var missing = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            new PosHandheldUpdatePolicyRequest
            {
                Enabled = true,
                CandidateId = build.Id,
            },
            "admin"
        );
        await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            EnabledRequest(build.Id),
            "admin"
        );
        var conflict = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            new PosHandheldUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                Required = true,
                CandidateId = build.Id,
            },
            "admin"
        );
        var staleNoOp = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            EnabledRequest(build.Id),
            "admin"
        );
        var staleInvalidCandidate = await service.SetLaneAsync(
            PosHandheldUpdateLanes.AndroidNative,
            EnabledRequest(Guid.NewGuid()),
            "admin"
        );

        Assert.Equal(AppUpdatePolicyErrorCodes.VersionRequired, missing.ErrorCode);
        Assert.Equal(AppUpdatePolicyErrorCodes.VersionConflict, conflict.ErrorCode);
        Assert.Equal(AppUpdatePolicyErrorCodes.VersionConflict, staleNoOp.ErrorCode);
        Assert.Equal(
            AppUpdatePolicyErrorCodes.VersionConflict,
            staleInvalidCandidate.ErrorCode
        );
    }

    [Fact]
    public async Task 候选目录_返回四lane总览并标记历史Ota不可激活()
    {
        var old = await SeedOtaAsync(
            "android-old",
            new DateTime(2026, 8, 10, 1, 0, 0, DateTimeKind.Utc)
        );
        var head = await SeedOtaAsync(
            "android-head",
            new DateTime(2026, 8, 10, 2, 0, 0, DateTimeKind.Utc)
        );
        var service = CreateService();

        var policies = await service.GetPoliciesAsync();
        var candidates = await service.GetCandidatesAsync(
            PosHandheldUpdateLanes.AndroidOta
        );

        var policyData = Assert.IsType<List<PosHandheldUpdatePolicyDto>>(policies.Data);
        var candidateData = Assert.IsType<List<PosHandheldUpdateCandidateDto>>(
            candidates.Data
        );
        Assert.Equal(PosHandheldUpdateLanes.All, policyData.Select(item => item.Lane));
        Assert.All(policyData, item => Assert.False(item.Managed));
        Assert.False(candidateData.Single(item => item.Id == old.Id).Activatable);
        Assert.Equal(
            PosHandheldUpdatePolicyErrorCodes.OtaCandidateNotChannelHead,
            candidateData.Single(item => item.Id == old.Id).BlockedReason
        );
        Assert.True(candidateData.Single(item => item.Id == head.Id).Activatable);
    }

    [Fact]
    public async Task Ios候选_只返回手持App和冻结Bundle发布事实()
    {
        await db.Insertable(
            new IosAppStoreRelease
            {
                Id = Guid.NewGuid(),
                App = AppUpdateApps.PosHandheld,
                AppStoreId = "123456789",
                BundleIdentifier = "com.example.wrong",
                Version = "3.0.0",
                BuildNumber = "300",
                Storefront = "au",
                AppStoreUrl = "https://apps.apple.com/au/app/id123456789",
                AppleVerifiedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var expected = new IosAppStoreRelease
        {
            Id = Guid.NewGuid(),
            App = AppUpdateApps.PosHandheld,
            AppStoreId = "123456789",
            BundleIdentifier = "com.hbweb.poshandheld",
            Version = "3.0.0",
            BuildNumber = "301",
            Storefront = "au",
            AppStoreUrl = "https://apps.apple.com/au/app/id123456789",
            AppleVerifiedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false,
        };
        await db.Insertable(expected).ExecuteCommandAsync();

        var candidates = await CreateService().GetCandidatesAsync(
            PosHandheldUpdateLanes.IosNative
        );

        var candidate = Assert.Single(candidates.Data!);
        Assert.Equal(expected.Id, candidate.Id);
        Assert.Equal("app-store", candidate.Distribution);
        Assert.True(candidate.Activatable);
    }

    [Fact]
    public void 管理API_读取候选策略审计使用View权限且写入使用Manage权限()
    {
        AssertRouteAndPolicy(
            nameof(AppUpdatePoliciesController.GetPosHandheld),
            "pos-handheld",
            Permissions.System.ViewAppDownloads
        );
        AssertRouteAndPolicy(
            nameof(AppUpdatePoliciesController.GetPosHandheldAndroidCandidates),
            "pos-handheld/candidates/native/android",
            Permissions.System.ViewAppDownloads
        );
        AssertRouteAndPolicy(
            nameof(AppUpdatePoliciesController.GetPosHandheldIosCandidates),
            "pos-handheld/candidates/native/ios",
            Permissions.System.ViewAppDownloads
        );
        AssertRouteAndPolicy(
            nameof(AppUpdatePoliciesController.GetPosHandheldOtaCandidates),
            "pos-handheld/candidates/ota",
            Permissions.System.ViewAppDownloads,
            typeof(string)
        );
        AssertRouteAndPolicy(
            nameof(AppUpdatePoliciesController.GetPosHandheldRevisions),
            "pos-handheld/revisions",
            Permissions.System.ViewAppDownloads,
            typeof(string)
        );
        AssertRouteAndPolicy(
            nameof(AppUpdatePoliciesController.PutPosHandheldLane),
            "pos-handheld/{lane}",
            Permissions.System.ManageAppDownloads,
            typeof(string),
            typeof(PosHandheldUpdatePolicyRequest)
        );
    }

    [Fact]
    public async Task 管理API_手持Pos版本冲突映射HTTP409()
    {
        var error = ApiResponse<PosHandheldUpdatePolicyDto>.Error(
            "策略版本已变化，请刷新后重试",
            AppUpdatePolicyErrorCodes.VersionConflict,
            new
            {
                ExpectedPolicyVersion = 1L,
                ActualPolicyVersion = 2L,
            }
        );
        var nativeService = new Mock<BlazorApp.Api.Interfaces.INativeAppUpdatePolicyService>();
        var policyService = new Mock<BlazorApp.Api.Interfaces.IPosHandheldUpdatePolicyService>();
        policyService
            .Setup(service =>
                service.SetLaneAsync(
                    PosHandheldUpdateLanes.AndroidNative,
                    It.IsAny<PosHandheldUpdatePolicyRequest>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(error);
        var controller = new AppUpdatePoliciesController(
            nativeService.Object,
            policyService.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var result = await controller.PutPosHandheldLane(
            PosHandheldUpdateLanes.AndroidNative,
            new PosHandheldUpdatePolicyRequest()
        );

        Assert.Same(error, Assert.IsType<ConflictObjectResult>(result).Value);
    }

    [Fact]
    public async Task 手持Ota策略_新release_channel事实可独立激活且保留审计身份()
    {
        var release = new AppOtaRelease
        {
            Id = Guid.NewGuid(),
            ReleaseBatchId = Guid.NewGuid(),
            AppKey = MobileAppKeys.PosHandheld,
            Environment = "production",
            ClientChannel = "pos-handheld-production",
            ReleaseChannel = "pos-handheld-production-ios-release-20260827-a",
            EasBranch = "pos-handheld-production-ios-release-20260827-a",
            ProjectName = "hb-pos-handheld",
            Platform = "ios",
            RuntimeVersion = "1.0.2",
            UpdateGroupId = Guid.NewGuid().ToString(),
            UpdateId = Guid.NewGuid().ToString(),
            Message = "iOS 独立发布",
            GitCommitHash = "abcdef12",
            DashboardUrl = "https://expo.dev/accounts/hb/projects/pos/updates/test",
            PublishedAtUtc = DateTime.UtcNow,
            FactFingerprint = new string('a', 64),
            RegistrationSource = "app-ota-release-api",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "publisher",
            IsDeleted = false,
        };
        await db.Insertable(release).ExecuteCommandAsync();
        var service = CreateService();

        var candidates = await service.GetCandidatesAsync(
            PosHandheldUpdateLanes.IosOta
        );
        var saved = await service.SetLaneAsync(
            PosHandheldUpdateLanes.IosOta,
            new PosHandheldUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                Required = true,
                CandidateId = release.Id,
                ReleaseMessage = "必须更新",
            },
            "admin"
        );
        var resolved = await service.ResolveManagedLaneAsync(
            PosHandheldUpdateLanes.IosOta
        );

        Assert.True(candidates.Success);
        var candidate = Assert.Single(candidates.Data!);
        Assert.Equal(release.ReleaseChannel, candidate.Channel);
        Assert.False(candidate.Legacy);
        Assert.Equal(release.ReleaseBatchId, candidate.ReleaseBatchId);
        Assert.Equal("publisher", candidate.RegisteredBy);
        Assert.True(saved.Success, saved.Message);
        Assert.True(resolved!.CandidateValid);
        Assert.Equal(release.Id, resolved.Candidate!.Id);
    }

    [Fact]
    public void Program_注册手持Pos策略服务并注入共享数据库()
    {
        var program = File.ReadAllText(
            Path.Combine(FindBackendRoot(), "BlazorApp.Api", "Program.cs")
        );

        Assert.Contains(
            "builder.Services.AddScoped<IPosHandheldUpdatePolicyService>(sp =>",
            program
        );
        Assert.Contains("new PosHandheldUpdatePolicyService(", program);
        Assert.Contains(
            "builder.Services.AddScoped<IPosHandheldUpdateDecisionService, PosHandheldUpdateDecisionService>();",
            program
        );
    }

    [Fact]
    public void ProductionCompose_注入手持Pos精确Ota候选身份()
    {
        var compose = File.ReadAllText(
            Path.Combine(FindBackendRoot(), "docker-compose.yml")
        );

        // 候选服务会在查询 OTA 表前校验项目身份和固定 channel；生产 compose 必须显式提供。
        Assert.Contains(
            "PosHandheldUpdatePolicy__EasProjectName=hb-pos-handheld",
            compose
        );
        Assert.Contains(
            "PosHandheldUpdatePolicy__OtaChannel=pos-handheld-production",
            compose
        );
        // Android 原生候选在查询构建表前会校验包名与签名信任根，生产必须显式注入。
        Assert.Contains(
            "PosHandheldUpdatePolicy__AndroidPackageName=com.hbweb.poshandheld",
            compose
        );
        Assert.Contains(
            "PosHandheldUpdatePolicy__AndroidSigningCertificateSha256=${POS_HANDHELD_ANDROID_SIGNING_CERTIFICATE_SHA256:?required}",
            compose
        );
        // 手持端 release channel 发布闸门必须可由服务器 .env 开启，且缺省保持关闭。
        Assert.Contains(
            "EasWebhook__PosHandheldReleaseChannelPublishingEnabled=${EAS_WEBHOOK_POS_HANDHELD_RELEASE_CHANNEL_PUBLISHING_ENABLED:-false}",
            compose
        );
    }

    public void Dispose()
    {
        db.Dispose();
        if (File.Exists(dbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(dbPath);
        }
    }

    private PosHandheldUpdatePolicyService CreateService() =>
        new(
            db,
            Options.Create(policyOptions),
            Options.Create(easOptions),
            NullLogger<PosHandheldUpdatePolicyService>.Instance
        );

    private static void AssertRouteAndPolicy(
        string methodName,
        string template,
        string policy,
        params Type[] parameterTypes
    )
    {
        var method = typeof(AppUpdatePoliciesController).GetMethod(
            methodName,
            parameterTypes
        );
        Assert.NotNull(method);
        var route = method!.GetCustomAttributes<HttpMethodAttribute>(false).Single();
        Assert.Equal(template, route.Template);
        Assert.Equal(
            policy,
            method.GetCustomAttribute<AuthorizeAttribute>()?.Policy
        );
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "BlazorApp.Api");
            if (Directory.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 services/backend");
    }

    private static PosHandheldUpdatePolicyRequest EnabledRequest(Guid candidateId) =>
        new()
        {
            ExpectedPolicyVersion = 0,
            Enabled = true,
            CandidateId = candidateId,
        };

    private async Task<MobileAppBuild> SeedBuildAsync(
        string appKey,
        string projectName,
        string profile,
        string version,
        string buildNumber
    )
    {
        var completedAt = DateTime.UtcNow.AddMinutes(-5);
        var entity = new MobileAppBuild
        {
            Id = Guid.NewGuid(),
            AppKey = appKey,
            EasBuildId = Guid.NewGuid().ToString(),
            AccountName = "hotbargain",
            ProjectName = projectName,
            Platform = "android",
            Status = "finished",
            BuildProfile = profile,
            AppVersion = version,
            AppBuildVersion = buildNumber,
            ArtifactUrl = "https://downloads.example/handheld.apk",
            CosArtifactUrl = "https://downloads.example/handheld.apk",
            ArtifactSha256 = new string('a', 64),
            ArtifactSize = 2048,
            CosMirrorStatus = MobileAppBuildService.CosMirrorStatusSucceeded,
            CompletedAt = completedAt,
            ExpirationDate = DateTime.UtcNow.AddDays(30),
            ReceivedAt = completedAt,
            CreatedAt = completedAt,
            IsDeleted = false,
        };
        await db.Insertable(entity).ExecuteCommandAsync();
        return entity;
    }

    private async Task<MobileAppOtaUpdate> SeedOtaAsync(string updateId, DateTime publishedAt)
    {
        var entity = new MobileAppOtaUpdate
        {
            Id = Guid.NewGuid(),
            AppKey = MobileAppKeys.PosHandheld,
            ProjectName = "hb-pos-handheld",
            UpdateGroupId = Guid.NewGuid().ToString(),
            UpdateId = updateId,
            AndroidUpdateId = updateId,
            Channel = "pos-handheld-production",
            Platform = "android",
            RuntimeVersion = "1.0.0",
            PublishedAt = publishedAt,
            CreatedAt = publishedAt,
            IsDeleted = false,
        };
        await db.Insertable(entity).ExecuteCommandAsync();
        return entity;
    }
}
