using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlazorApp.Api.Authentication;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class AppUpdatePolicyServiceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"app-update-policy-{Guid.NewGuid():N}.db");
    private readonly ISqlSugarClient _db;

    public AppUpdatePolicyServiceTests()
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
        _db.CodeFirst.InitTables(
            typeof(IosAppStoreRelease),
            typeof(MobileIosNativeUpdatePolicy),
            typeof(PosIpadNativeUpdatePolicy),
            typeof(PosIpadNativeUpdatePolicyTarget),
            typeof(PosIpadOtaRelease),
            typeof(PosIpadOtaRollout),
            typeof(PosIpadOtaRolloutTarget),
            typeof(PosIpadOtaAssignment),
            typeof(Store)
        );
    }

    [Fact]
    public async Task Mobile原生决策_按minimum和latest返回required_optional_none()
    {
        var release = await SeedIosReleaseAsync(AppUpdateApps.MobileIos, "2.0.0");
        var service = CreateNativeService();
        var saved = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                MinimumSupportedVersion = "1.5.0",
                ReleaseMessage = "发现新版",
            },
            "admin"
        );

        Assert.True(saved.Success);
        Assert.Equal(1, saved.Data!.PolicyVersion);

        var required = await service.GetMobileIosDecisionAsync("1.4.9", "101");
        var optional = await service.GetMobileIosDecisionAsync("1.9.0", "102");
        var none = await service.GetMobileIosDecisionAsync("2.0.0", "103");

        Assert.Equal(AppUpdateStates.Required, required.State);
        Assert.Equal(AppUpdateStates.Optional, optional.State);
        Assert.Equal(AppUpdateStates.None, none.State);
        Assert.Equal("2.0.0", required.LatestVersion);
        Assert.Equal("1.5.0", required.MinimumSupportedVersion);
        Assert.Equal("https://apps.apple.com/au/app/id123456789", required.AppStoreUrl);
    }

    [Fact]
    public async Task Mobile原生决策_同营销版本按Build区分可选与强制阶段()
    {
        var release = await SeedIosReleaseAsync(AppUpdateApps.MobileIos, "1.0.2");
        release.BuildNumber = "31";
        await _db.Updateable(release).ExecuteCommandAsync();
        var service = CreateNativeService();

        var optionalPolicy = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
            },
            "admin"
        );
        Assert.True(optionalPolicy.Success);

        (string? Version, string? Build, string Expected)[] optionalCases =
        {
            ("1.0.2", "31", AppUpdateStates.None),
            ("1.0.2", "30", AppUpdateStates.Optional),
            ("1.0.1", "999", AppUpdateStates.Optional),
            ("1.0.2", null, AppUpdateStates.Optional),
            ("1.0.2", "bad", AppUpdateStates.Optional),
            ("bad", "31", AppUpdateStates.Optional),
            ("1.0.2", "32", AppUpdateStates.None),
            ("1.0.3", null, AppUpdateStates.None),
        };
        foreach (var item in optionalCases)
        {
            var decision = await service.GetMobileIosDecisionAsync(item.Version, item.Build);
            Assert.Equal(item.Expected, decision.State);
        }

        var requiredPolicy = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = true,
                ReleaseId = release.Id,
                MinimumSupportedVersion = "1.0.2",
                MinimumSupportedBuildNumber = 31,
            },
            "admin"
        );
        Assert.True(requiredPolicy.Success);

        (string? Version, string? Build, string Expected)[] requiredCases =
        {
            ("1.0.2", "31", AppUpdateStates.None),
            ("1.0.2", "30", AppUpdateStates.Required),
            ("1.0.1", "999", AppUpdateStates.Required),
            ("1.0.2", null, AppUpdateStates.Required),
            ("1.0.2", "bad", AppUpdateStates.Required),
            ("bad", "31", AppUpdateStates.Required),
            ("1.0.2", "32", AppUpdateStates.None),
            ("1.0.3", null, AppUpdateStates.Required),
            ("1.0.3", "bad", AppUpdateStates.Required),
            ("1.0.3", "0", AppUpdateStates.None),
        };
        foreach (var item in requiredCases)
        {
            var decision = await service.GetMobileIosDecisionAsync(item.Version, item.Build);
            Assert.Equal(item.Expected, decision.State);
        }
    }

    [Fact]
    public async Task Mobile原生策略_校验ReleaseBuild与MinimumBuild关系并保留版本级兼容()
    {
        var invalidBuildRelease = await SeedIosReleaseAsync(AppUpdateApps.MobileIos, "1.0.1");
        invalidBuildRelease.BuildNumber = "2147483648";
        await _db.Updateable(invalidBuildRelease).ExecuteCommandAsync();
        var validRelease = await SeedIosReleaseAsync(AppUpdateApps.MobileIos, "1.0.2");
        validRelease.BuildNumber = "31";
        await _db.Updateable(validRelease).ExecuteCommandAsync();
        var service = CreateNativeService();

        var invalidReleaseBuild = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = invalidBuildRelease.Id,
            },
            "admin"
        );
        var buildWithoutVersion = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = validRelease.Id,
                MinimumSupportedBuildNumber = 31,
            },
            "admin"
        );
        var negativeMinimumBuild = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = validRelease.Id,
                MinimumSupportedVersion = "1.0.2",
                MinimumSupportedBuildNumber = -1,
            },
            "admin"
        );
        var minimumAboveRelease = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = validRelease.Id,
                MinimumSupportedVersion = "1.0.2",
                MinimumSupportedBuildNumber = 32,
            },
            "admin"
        );

        Assert.Equal("LATEST_BUILD_NUMBER_INVALID", invalidReleaseBuild.ErrorCode);
        Assert.Equal("MINIMUM_BUILD_REQUIRES_VERSION", buildWithoutVersion.ErrorCode);
        Assert.Equal("MINIMUM_BUILD_INVALID", negativeMinimumBuild.ErrorCode);
        Assert.Equal("MINIMUM_BUILD_ABOVE_LATEST", minimumAboveRelease.ErrorCode);
        Assert.Equal(0, await _db.Queryable<MobileIosNativeUpdatePolicy>().CountAsync());

        var compatible = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = validRelease.Id,
                MinimumSupportedVersion = "1.0.2",
            },
            "admin"
        );
        Assert.True(compatible.Success);
        Assert.Null(compatible.Data!.MinimumSupportedBuildNumber);
        Assert.Equal(
            AppUpdateStates.Optional,
            (await service.GetMobileIosDecisionAsync("1.0.2", "0")).State
        );
        Assert.Equal(
            AppUpdateStates.Required,
            (await service.GetMobileIosDecisionAsync("1.0.1", "999")).State
        );
    }

    [Fact]
    public async Task 原生策略_归一化后相同请求幂等且真实变化才升版()
    {
        var firstStore = await SeedStoreAsync("BRI", "Brisbane");
        var secondStore = await SeedStoreAsync("SYD", "Sydney");
        var mobileRelease = await SeedIosReleaseAsync(AppUpdateApps.MobileIos, "2.0.0");
        var ipadRelease = await SeedIosReleaseAsync(AppUpdateApps.PosIpad, "3.0.0");
        var service = CreateNativeService();

        var mobileFirst = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = mobileRelease.Id,
                MinimumSupportedVersion = "1.5.0",
                ReleaseMessage = " 发现新版 ",
            },
            "admin"
        );
        var mobileRepeated = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = mobileRelease.Id,
                MinimumSupportedVersion = " 1.5.0 ",
                ReleaseMessage = "发现新版",
            },
            "publisher"
        );
        var mobileChanged = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = true,
                ReleaseId = mobileRelease.Id,
                MinimumSupportedVersion = "1.5.0",
                ReleaseMessage = "新版说明已修改",
            },
            "admin"
        );

        var ipadFirst = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = ipadRelease.Id,
                TargetScope = AppUpdateTargetScopes.Stores,
                TargetStoreGuids = [firstStore.StoreGUID, secondStore.StoreGUID],
                ReleaseMessage = " 分店灰度 ",
            },
            "admin"
        );
        var ipadRepeated = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = ipadRelease.Id,
                TargetScope = AppUpdateTargetScopes.Stores,
                TargetStoreGuids =
                [
                    secondStore.StoreGUID,
                    firstStore.StoreGUID,
                    firstStore.StoreGUID,
                ],
                ReleaseMessage = "分店灰度",
            },
            "publisher"
        );
        var ipadChanged = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = true,
                ReleaseId = ipadRelease.Id,
                TargetScope = AppUpdateTargetScopes.Stores,
                TargetStoreGuids = [firstStore.StoreGUID],
                ReleaseMessage = "分店灰度",
            },
            "admin"
        );

        Assert.Equal(1, mobileFirst.Data!.PolicyVersion);
        Assert.Equal(1, mobileRepeated.Data!.PolicyVersion);
        Assert.Equal(2, mobileChanged.Data!.PolicyVersion);
        Assert.Equal(1, ipadFirst.Data!.PolicyVersion);
        Assert.Equal(1, ipadRepeated.Data!.PolicyVersion);
        Assert.Equal(2, ipadChanged.Data!.PolicyVersion);
        Assert.Equal(
            1,
            await _db.Queryable<PosIpadNativeUpdatePolicyTarget>()
                .Where(item => !item.IsDeleted)
                .CountAsync()
        );
    }

    [Fact]
    public async Task Ipad原生策略_只命中由StoreCode解析出的指定活动分店()
    {
        var target = await SeedStoreAsync("BRI", "Brisbane");
        await SeedStoreAsync("SYD", "Sydney");
        var release = await SeedIosReleaseAsync(AppUpdateApps.PosIpad, "3.0.0");
        var service = CreateNativeService();

        var saved = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                MinimumSupportedVersion = "2.5.0",
                TargetScope = AppUpdateTargetScopes.Stores,
                TargetStoreGuids = [target.StoreGUID],
            },
            "admin"
        );

        Assert.True(saved.Success);
        var targeted = await service.GetPosIpadNativeDecisionAsync(
            new PosIpadNativeDecisionRequest
            {
                StoreCode = "bri",
                Version = "2.0.0",
                Build = "12",
            }
        );
        var other = await service.GetPosIpadNativeDecisionAsync(
            new PosIpadNativeDecisionRequest
            {
                StoreCode = "SYD",
                Version = "2.0.0",
                Build = "12",
            }
        );
        var forgedGuid = await service.GetPosIpadNativeDecisionAsync(
            new PosIpadNativeDecisionRequest
            {
                StoreCode = target.StoreGUID,
                Version = "2.0.0",
            }
        );

        Assert.Equal(AppUpdateStates.Required, targeted.State);
        Assert.Equal(AppUpdateStates.None, other.State);
        Assert.Equal(AppUpdateStates.None, forgedGuid.State);
    }

    [Fact]
    public async Task Ipad原生决策_按营销版本和Build组成四段有效版本()
    {
        await SeedStoreAsync("BRI", "Brisbane");
        var release = await SeedIosReleaseAsync(AppUpdateApps.PosIpad, "3.2");
        release.BuildNumber = "120";
        await _db.Updateable(release).ExecuteCommandAsync();
        var service = CreateNativeService();

        var saved = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                MinimumSupportedVersion = "3.2",
                MinimumSupportedBuildNumber = 100,
            },
            "admin"
        );

        Assert.True(saved.Success);
        Assert.Equal("3.2", saved.Data!.LatestVersion);
        Assert.Equal("3.2", saved.Data.MinimumSupportedVersion);
        Assert.Equal(100, saved.Data.MinimumSupportedBuildNumber);

        var required = await service.GetPosIpadNativeDecisionAsync(
            new PosIpadNativeDecisionRequest
            {
                StoreCode = "BRI",
                Version = "3.2",
                Build = "99",
            }
        );
        var optional = await service.GetPosIpadNativeDecisionAsync(
            new PosIpadNativeDecisionRequest
            {
                StoreCode = "BRI",
                Version = "3.2.0",
                Build = "119",
            }
        );
        var none = await service.GetPosIpadNativeDecisionAsync(
            new PosIpadNativeDecisionRequest
            {
                StoreCode = "BRI",
                Version = "3.2",
                Build = "120",
            }
        );

        Assert.Equal(AppUpdateStates.Required, required.State);
        Assert.Equal("3.2.0.120", required.LatestVersion);
        Assert.Equal("3.2.0.100", required.MinimumSupportedVersion);
        Assert.Equal(AppUpdateStates.Optional, optional.State);
        Assert.Equal(AppUpdateStates.None, none.State);
    }

    [Fact]
    public async Task Ipad原生决策_先比较Marketing再按同版本Build判定()
    {
        await SeedStoreAsync("BRI", "Brisbane");
        var release = await SeedIosReleaseAsync(AppUpdateApps.PosIpad, "3.2");
        release.BuildNumber = "120";
        await _db.Updateable(release).ExecuteCommandAsync();
        var service = CreateNativeService();
        var saved = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                MinimumSupportedVersion = "3.1",
                MinimumSupportedBuildNumber = 50,
            },
            "admin"
        );
        Assert.True(saved.Success);

        (string Version, string? Build, string Expected)[] cases =
        {
            ("bad", "60", AppUpdateStates.Required),
            ("3.0", "999", AppUpdateStates.Required),
            ("3.1", null, AppUpdateStates.Required),
            ("3.1", "49", AppUpdateStates.Required),
            ("3.1", "50", AppUpdateStates.Optional),
            ("3.1.5", "bad", AppUpdateStates.Optional),
            ("3.2", null, AppUpdateStates.Optional),
            ("3.2", "bad", AppUpdateStates.Optional),
            ("3.2", "119", AppUpdateStates.Optional),
            ("3.2", "120", AppUpdateStates.None),
            ("3.3", null, AppUpdateStates.None),
        };

        foreach (var item in cases)
        {
            var decision = await service.GetPosIpadNativeDecisionAsync(
                new PosIpadNativeDecisionRequest
                {
                    StoreCode = "BRI",
                    Version = item.Version,
                    Build = item.Build,
                }
            );

            Assert.Equal(item.Expected, decision.State);
        }
    }

    [Fact]
    public async Task Ipad原生策略_校验营销版本ReleaseBuild与MinimumBuild关系()
    {
        var fourPartRelease = await SeedIosReleaseAsync(AppUpdateApps.PosIpad, "3.0.0.1");
        var invalidBuildRelease = await SeedIosReleaseAsync(AppUpdateApps.PosIpad, "3.1.0");
        invalidBuildRelease.BuildNumber = "2147483648";
        await _db.Updateable(invalidBuildRelease).ExecuteCommandAsync();
        var validRelease = await SeedIosReleaseAsync(AppUpdateApps.PosIpad, "3.2.0");
        validRelease.BuildNumber = "120";
        await _db.Updateable(validRelease).ExecuteCommandAsync();
        var service = CreateNativeService();

        var invalidMarketing = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = fourPartRelease.Id,
            },
            "admin"
        );
        var invalidBuild = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = invalidBuildRelease.Id,
            },
            "admin"
        );
        var buildWithoutMinimum = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = validRelease.Id,
                MinimumSupportedBuildNumber = 100,
            },
            "admin"
        );
        var minimumAboveRelease = await service.SetPosIpadNativePolicyAsync(
            new PosIpadNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = validRelease.Id,
                MinimumSupportedVersion = "3.2",
                MinimumSupportedBuildNumber = 121,
            },
            "admin"
        );

        Assert.Equal("LATEST_VERSION_INVALID", invalidMarketing.ErrorCode);
        Assert.Equal("LATEST_BUILD_NUMBER_INVALID", invalidBuild.ErrorCode);
        Assert.Equal("MINIMUM_BUILD_REQUIRES_VERSION", buildWithoutMinimum.ErrorCode);
        Assert.Equal("MINIMUM_BUILD_ABOVE_LATEST", minimumAboveRelease.ErrorCode);
        Assert.Equal(0, await _db.Queryable<PosIpadNativeUpdatePolicy>().CountAsync());
    }

    [Fact]
    public async Task 原生策略_expectedVersion缺失冲突且stale同内容幂等不同内容不写()
    {
        var release = await SeedIosReleaseAsync(AppUpdateApps.MobileIos, "2.0.0");
        var service = CreateNativeService();
        var missing = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                Enabled = true,
                ReleaseId = release.Id,
            },
            "admin"
        );

        AssertPolicyVersionError(
            missing,
            "APP_UPDATE_POLICY_VERSION_REQUIRED",
            null,
            0
        );
        Assert.Equal(0, await _db.Queryable<MobileIosNativeUpdatePolicy>().CountAsync());

        var first = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ReleaseMessage = " 首次发布 ",
            },
            "admin"
        );
        var staleSame = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ReleaseMessage = "首次发布",
            },
            "publisher"
        );
        var staleChanged = await service.SetMobileIosPolicyAsync(
            new MobileIosNativeUpdatePolicyRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ReleaseMessage = "并发覆盖",
            },
            "publisher"
        );

        Assert.Equal(1, first.Data!.PolicyVersion);
        Assert.Equal(1, staleSame.Data!.PolicyVersion);
        AssertPolicyVersionError(
            staleChanged,
            "APP_UPDATE_POLICY_VERSION_CONFLICT",
            0,
            1
        );
        var stored = await _db.Queryable<MobileIosNativeUpdatePolicy>().SingleAsync();
        Assert.Equal(1, stored.PolicyVersion);
        Assert.Equal("首次发布", stored.ReleaseMessage);
    }

    [Fact]
    public async Task IpadOta决策_校验分店_runtime和当前updateId()
    {
        var target = await SeedStoreAsync("BRI", "Brisbane");
        var other = await SeedStoreAsync("SYD", "Sydney");
        var service = CreateOtaService();
        var release = await SeedOtaReleaseAsync();
        var rollout = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ForceUpdate = true,
                TargetScope = AppUpdateTargetScopes.Stores,
                TargetStoreGuids = [target.StoreGUID],
                ReleaseMessage = "必须更新收银版本",
            },
            "admin"
        );
        Assert.True(rollout.Success);

        var required = await service.GetDecisionAsync(
            new PosIpadOtaDecisionRequest
            {
                StoreCode = "BRI",
                RuntimeVersion = "3.0.0",
                CurrentUpdateId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            }
        );
        var alreadyInstalled = await service.GetDecisionAsync(
            new PosIpadOtaDecisionRequest
            {
                StoreCode = "BRI",
                RuntimeVersion = "3.0.0",
                CurrentUpdateId = release.IosUpdateId,
            }
        );
        var wrongRuntime = await service.GetDecisionAsync(
            new PosIpadOtaDecisionRequest
            {
                StoreCode = "BRI",
                RuntimeVersion = "2.0.0",
            }
        );
        var otherStore = await service.GetDecisionAsync(
            new PosIpadOtaDecisionRequest
            {
                StoreCode = other.StoreCode,
                RuntimeVersion = "3.0.0",
            }
        );

        Assert.Equal(AppUpdateStates.Required, required.State);
        Assert.Equal(release.IosUpdateId, required.IosUpdateId);
        Assert.Equal(AppUpdateStates.None, alreadyInstalled.State);
        Assert.Equal(AppUpdateStates.None, wrongRuntime.State);
        Assert.Equal(AppUpdateStates.None, otherStore.State);
    }

    [Fact]
    public async Task IpadOtaRollout_expectedVersion缺失冲突且stale同内容幂等不同内容不写()
    {
        var release = await SeedOtaReleaseAsync();
        var service = CreateOtaService();
        var missing = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                Enabled = true,
                ReleaseId = release.Id,
            },
            "admin"
        );

        AssertPolicyVersionError(
            missing,
            "APP_UPDATE_POLICY_VERSION_REQUIRED",
            null,
            0
        );
        Assert.Equal(0, await _db.Queryable<PosIpadOtaRollout>().CountAsync());

        var first = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ReleaseMessage = " 首次投放 ",
            },
            "admin"
        );
        var staleSame = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ReleaseMessage = "首次投放",
            },
            "publisher"
        );
        var staleChanged = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ForceUpdate = true,
                ReleaseMessage = "首次投放",
            },
            "publisher"
        );

        Assert.Equal(1, first.Data!.PolicyVersion);
        Assert.Equal(1, staleSame.Data!.PolicyVersion);
        AssertPolicyVersionError(
            staleChanged,
            "APP_UPDATE_POLICY_VERSION_CONFLICT",
            0,
            1
        );
        var stored = await _db.Queryable<PosIpadOtaRollout>().SingleAsync();
        Assert.Equal(1, stored.PolicyVersion);
        Assert.False(stored.ForceUpdate);
    }

    [Fact]
    public async Task 策略控制器_版本前置条件错误映射HTTP409()
    {
        var nativeError = ApiResponse<NativeUpdatePolicyDto>.Error(
            "expectedPolicyVersion 不能为空",
            "APP_UPDATE_POLICY_VERSION_REQUIRED",
            new { ExpectedPolicyVersion = (long?)null, ActualPolicyVersion = 0L }
        );
        var nativeService = new Mock<INativeAppUpdatePolicyService>();
        nativeService
            .Setup(service =>
                service.SetMobileIosPolicyAsync(
                    It.IsAny<MobileIosNativeUpdatePolicyRequest>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(nativeError);
        var nativeController = new AppUpdatePoliciesController(nativeService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var nativeResult = await nativeController.PutMobileIos(
            new MobileIosNativeUpdatePolicyRequest()
        );

        Assert.Same(
            nativeError,
            Assert.IsType<ConflictObjectResult>(nativeResult).Value
        );

        var otaError = ApiResponse<PosIpadOtaRolloutDto>.Error(
            "策略版本已变化",
            "APP_UPDATE_POLICY_VERSION_CONFLICT",
            new { ExpectedPolicyVersion = 0L, ActualPolicyVersion = 1L }
        );
        var otaService = new Mock<IPosIpadOtaPolicyService>();
        otaService
            .Setup(service =>
                service.SetRolloutAsync(
                    It.IsAny<PosIpadOtaRolloutRequest>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(otaError);
        var otaController = new PosIpadOtaRolloutController(otaService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var otaResult = await otaController.Put(new PosIpadOtaRolloutRequest());

        Assert.Same(otaError, Assert.IsType<ConflictObjectResult>(otaResult).Value);
    }

    [Fact]
    public async Task IpadOta激活新版本_同一环境只保留一个活动rollout()
    {
        await SeedStoreAsync("BRI", "Brisbane");
        var service = CreateOtaService();
        var first = await SeedOtaReleaseAsync(
            "11111111-1111-4111-8111-111111111111",
            "21111111-1111-4111-8111-111111111111"
        );
        var second = await SeedOtaReleaseAsync(
            "33333333-3333-4333-8333-333333333333",
            "43333333-3333-4333-8333-333333333333"
        );

        Assert.True(
            (
                await service.SetRolloutAsync(
                    new PosIpadOtaRolloutRequest
                    {
                        ExpectedPolicyVersion = 0,
                        Enabled = true,
                        ReleaseId = first.Id,
                        TargetScope = AppUpdateTargetScopes.All,
                    },
                    "admin"
                )
            ).Success
        );
        var secondResult = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = true,
                ReleaseId = second.Id,
                TargetScope = AppUpdateTargetScopes.All,
            },
            "admin"
        );

        Assert.True(secondResult.Success);
        Assert.Equal(2, secondResult.Data!.PolicyVersion);
        var rows = await _db.Queryable<PosIpadOtaRollout>().ToListAsync();
        Assert.Single(rows, item => item.Enabled && !item.IsDeleted);
        Assert.Equal(second.Id, rows.Single(item => item.Enabled).ReleaseId);
    }

    [Fact]
    public async Task IpadOtaRollout_相同请求幂等且停用追加历史事件()
    {
        var firstStore = await SeedStoreAsync("BRI", "Brisbane");
        var secondStore = await SeedStoreAsync("SYD", "Sydney");
        var release = await SeedOtaReleaseAsync();
        var service = CreateOtaService();
        var request = new PosIpadOtaRolloutRequest
        {
            ExpectedPolicyVersion = 0,
            Enabled = true,
            ReleaseId = release.Id,
            ForceUpdate = true,
            TargetScope = AppUpdateTargetScopes.Stores,
            TargetStoreGuids = [firstStore.StoreGUID, secondStore.StoreGUID],
            ReleaseMessage = " 分店强制更新 ",
        };

        var first = await service.SetRolloutAsync(request, "admin");
        var repeated = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                ForceUpdate = true,
                TargetScope = AppUpdateTargetScopes.Stores,
                TargetStoreGuids =
                [
                    secondStore.StoreGUID,
                    firstStore.StoreGUID,
                    firstStore.StoreGUID,
                ],
                ReleaseMessage = "分店强制更新",
            },
            "publisher"
        );
        var disabled = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = false,
            },
            "admin"
        );
        var disabledAgain = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 1,
                Enabled = false,
            },
            "publisher"
        );
        var latest = await service.GetRolloutAsync();

        Assert.Equal(first.Data!.Id, repeated.Data!.Id);
        Assert.Equal(1, repeated.Data.PolicyVersion);
        Assert.False(disabled.Data!.Enabled);
        Assert.Equal(2, disabled.Data.PolicyVersion);
        Assert.NotEqual(first.Data.Id, disabled.Data.Id);
        Assert.Equal(disabled.Data.Id, disabledAgain.Data!.Id);
        Assert.Equal(2, disabledAgain.Data.PolicyVersion);
        Assert.Equal(disabled.Data.Id, latest.Data!.Id);
        Assert.Equal(2, latest.Data.PolicyVersion);

        var rows = await _db.Queryable<PosIpadOtaRollout>()
            .OrderBy(item => item.PolicyVersion)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].Enabled);
        Assert.Equal(1, rows[0].PolicyVersion);
        Assert.False(rows[1].Enabled);
        Assert.Equal(2, rows[1].PolicyVersion);
    }

    [Fact]
    public async Task IpadOta发布事实_完全相同才幂等且group或iosId冲突都拒绝()
    {
        var service = CreateOtaService();
        var publishedAt = new DateTime(2026, 7, 30, 1, 2, 3, DateTimeKind.Utc);
        var request = new PosIpadOtaReleaseCreateRequest
        {
            UpdateGroupId = "77777777-7777-4777-8777-777777777777",
            IosUpdateId = "88888888-8888-4888-8888-888888888888",
            Channel = " POS-IPAD-RELEASE-PRIMARY ",
            RuntimeVersion = " 3.0.0 ",
            GitCommitHash = " ABC1234 ",
            DashboardUrl = "https://expo.dev/accounts/hb/projects/pos-ipad/updates/test",
            PublishedAtUtc = publishedAt,
            IsRollback = false,
        };

        var first = await service.CreateReleaseAsync(request, "publisher");
        var repeated = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = request.UpdateGroupId,
                IosUpdateId = request.IosUpdateId,
                Channel = "pos-ipad-release-primary",
                RuntimeVersion = "3.0.0",
                GitCommitHash = "abc1234",
                DashboardUrl = request.DashboardUrl,
                PublishedAtUtc = publishedAt,
            },
            "publisher"
        );
        var groupConflict = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = request.UpdateGroupId,
                IosUpdateId = request.IosUpdateId,
                Channel = "pos-ipad-release-other",
                RuntimeVersion = "3.0.0",
                GitCommitHash = "abc1234",
                DashboardUrl = request.DashboardUrl,
                PublishedAtUtc = publishedAt,
            },
            "publisher"
        );
        var iosIdConflict = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = "99999999-9999-4999-8999-999999999999",
                IosUpdateId = request.IosUpdateId,
                Channel = "pos-ipad-release-primary",
                RuntimeVersion = "3.0.0",
                GitCommitHash = "abc1234",
                DashboardUrl = request.DashboardUrl,
                PublishedAtUtc = publishedAt,
            },
            "publisher"
        );
        var channelConflict = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = "12121212-1212-4121-8121-121212121212",
                IosUpdateId = "34343434-3434-4343-8343-343434343434",
                Channel = "pos-ipad-release-primary",
                RuntimeVersion = "3.0.0",
                GitCommitHash = "abc1234",
                DashboardUrl = request.DashboardUrl,
                PublishedAtUtc = publishedAt,
            },
            "publisher"
        );
        var publishedAtConflict = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = request.UpdateGroupId,
                IosUpdateId = request.IosUpdateId,
                Channel = "pos-ipad-release-primary",
                RuntimeVersion = "3.0.0",
                GitCommitHash = "abc1234",
                DashboardUrl = request.DashboardUrl,
                PublishedAtUtc = publishedAt.AddSeconds(1),
            },
            "publisher"
        );

        Assert.True(first.Success);
        Assert.True(repeated.Success);
        Assert.Equal(first.Data!.Id, repeated.Data!.Id);
        Assert.False(groupConflict.Success);
        Assert.Equal("OTA_RELEASE_CONFLICT", groupConflict.ErrorCode);
        Assert.False(iosIdConflict.Success);
        Assert.Equal("OTA_RELEASE_CONFLICT", iosIdConflict.ErrorCode);
        Assert.False(channelConflict.Success);
        Assert.Equal("OTA_RELEASE_CONFLICT", channelConflict.ErrorCode);
        Assert.False(publishedAtConflict.Success);
        Assert.Equal("OTA_RELEASE_CONFLICT", publishedAtConflict.ErrorCode);
        Assert.Equal(1, await _db.Queryable<PosIpadOtaRelease>().CountAsync());
    }

    [Fact]
    public async Task IpadOta发布事实_省略publishedAt时重复登记沿用服务器首次时间()
    {
        var service = CreateOtaService();
        var request = new PosIpadOtaReleaseCreateRequest
        {
            UpdateGroupId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            IosUpdateId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            Channel = "pos-ipad-release-primary",
            RuntimeVersion = "3.0.0",
        };

        var first = await service.CreateReleaseAsync(request, "publisher");
        var repeated = await service.CreateReleaseAsync(request, "publisher");

        Assert.True(first.Success);
        Assert.True(repeated.Success);
        Assert.Equal(first.Data!.Id, repeated.Data!.Id);
        Assert.Equal(first.Data.PublishedAtUtc, repeated.Data.PublishedAtUtc);
    }

    [Fact]
    public async Task IpadOta发布事实_任一不可变字段变化都返回冲突()
    {
        var rollbackSource = await SeedOtaReleaseAsync(
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
        );
        var service = CreateOtaService();
        var publishedAt = new DateTime(2026, 7, 30, 2, 3, 4, DateTimeKind.Utc);

        PosIpadOtaReleaseCreateRequest Create(
            string channel = "pos-ipad-release-primary",
            string runtimeVersion = "3.0.0",
            string gitCommitHash = "abc1234",
            string dashboardUrl = "https://expo.dev/accounts/hb/projects/pos-ipad/updates/test",
            DateTime? published = null,
            bool isRollback = false,
            Guid? rollbackOfReleaseId = null
        ) =>
            new()
            {
                UpdateGroupId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                IosUpdateId = "ffffffff-ffff-4fff-8fff-ffffffffffff",
                Channel = channel,
                RuntimeVersion = runtimeVersion,
                GitCommitHash = gitCommitHash,
                DashboardUrl = dashboardUrl,
                PublishedAtUtc = published ?? publishedAt,
                IsRollback = isRollback,
                RollbackOfReleaseId = rollbackOfReleaseId,
            };

        Assert.True((await service.CreateReleaseAsync(Create(), "publisher")).Success);

        var conflicts = new[]
        {
            Create(channel: "pos-ipad-release-other"),
            Create(runtimeVersion: "3.0.1"),
            Create(gitCommitHash: "def5678"),
            Create(
                dashboardUrl: "https://expo.dev/accounts/hb/projects/pos-ipad/updates/other"
            ),
            Create(published: publishedAt.AddSeconds(1)),
            Create(
                isRollback: true,
                rollbackOfReleaseId: rollbackSource.Id
            ),
        };

        foreach (var conflict in conflicts)
        {
            var result = await service.CreateReleaseAsync(conflict, "publisher");
            Assert.False(result.Success);
            Assert.Equal("OTA_RELEASE_CONFLICT", result.ErrorCode);
        }
    }

    [Fact]
    public async Task AppStore登记_Apple返回bundle不匹配时拒绝且不落库()
    {
        var lookup = new StubAppleLookupClient(
            new AppleAppStoreLookupResult(
                "123456789",
                "com.example.wrong",
                "2.0.0",
                "https://apps.apple.com/au/app/id123456789"
            )
        );
        var service = CreateReleaseService(lookup);

        var result = await service.CreateAsync(
            new IosAppStoreReleaseCreateRequest
            {
                App = AppUpdateApps.MobileIos,
                AppStoreId = "123456789",
                BuildNumber = "200",
                Storefront = "au",
            },
            "admin"
        );

        Assert.False(result.Success);
        Assert.Equal("APP_STORE_BUNDLE_MISMATCH", result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<IosAppStoreRelease>().CountAsync());
    }

    [Fact]
    public async Task AppStore登记_验证成功后保存不可变发布事实且重试幂等()
    {
        var service = CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.expo",
                    "2.0.0",
                    "https://apps.apple.com/au/app/id123456789"
                )
            )
        );
        var request = new IosAppStoreReleaseCreateRequest
        {
            App = AppUpdateApps.MobileIos,
            AppStoreId = "123456789",
            BuildNumber = "200",
            Storefront = "au",
        };

        var first = await service.CreateAsync(request, "admin");
        var repeated = await service.CreateAsync(request, "publisher");

        Assert.True(first.Success);
        Assert.True(repeated.Success);
        Assert.Equal(first.Data!.Id, repeated.Data!.Id);
        Assert.Equal(1, await _db.Queryable<IosAppStoreRelease>().CountAsync());
        Assert.Equal("200", first.Data.BuildNumber);
        Assert.NotEqual(default, first.Data.AppleVerifiedAtUtc);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("31a")]
    [InlineData("2147483648")]
    public async Task AppStore登记_Mobile拒绝Int32范围外或非数字Build(string buildNumber)
    {
        var service = CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.expo",
                    "1.0.2",
                    "https://apps.apple.com/au/app/id123456789"
                )
            )
        );

        var result = await service.CreateAsync(
            new IosAppStoreReleaseCreateRequest
            {
                App = AppUpdateApps.MobileIos,
                AppStoreId = "123456789",
                BuildNumber = buildNumber,
                Storefront = "au",
            },
            "release-bot"
        );

        Assert.False(result.Success);
        Assert.Equal("APP_STORE_BUILD_INVALID", result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<IosAppStoreRelease>().CountAsync());
    }

    [Fact]
    public async Task AppStore登记_手持Pos只接受冻结Bundle并保存独立App事实()
    {
        var service = CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.poshandheld",
                    "3.0.0",
                    "https://apps.apple.com/au/app/id123456789"
                )
            )
        );

        var result = await service.CreateAsync(
            new IosAppStoreReleaseCreateRequest
            {
                App = AppUpdateApps.PosHandheld,
                AppStoreId = "123456789",
                BuildNumber = "300",
                Storefront = "au",
            },
            "release-bot"
        );

        Assert.True(result.Success);
        Assert.Equal(AppUpdateApps.PosHandheld, result.Data!.App);
        Assert.Equal("com.hbweb.poshandheld", result.Data.BundleIdentifier);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("12a")]
    [InlineData("9007199254740992")]
    public async Task AppStore登记_手持Pos拒绝设备候选无法接受的Build(string buildNumber)
    {
        var service = CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.poshandheld",
                    "3.0.0",
                    "https://apps.apple.com/au/app/id123456789"
                )
            )
        );

        var result = await service.CreateAsync(
            new IosAppStoreReleaseCreateRequest
            {
                App = AppUpdateApps.PosHandheld,
                AppStoreId = "123456789",
                BuildNumber = buildNumber,
                Storefront = "au",
            },
            "release-bot"
        );

        Assert.False(result.Success);
        Assert.Equal("APP_STORE_BUILD_INVALID", result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<IosAppStoreRelease>().CountAsync());
    }

    [Fact]
    public async Task AppStore登记_唯一键命中但AppStoreId或Url变化时返回事实冲突()
    {
        var request = new IosAppStoreReleaseCreateRequest
        {
            App = AppUpdateApps.MobileIos,
            AppStoreId = "123456789",
            BuildNumber = "200",
            Storefront = "au",
        };
        var first = await CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.expo",
                    "2.0.0",
                    "https://apps.apple.com/au/app/id123456789"
                )
            )
        ).CreateAsync(request, "admin");

        var differentId = await CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "987654321",
                    "com.hbweb.expo",
                    "2.0.0",
                    "https://apps.apple.com/au/app/id987654321"
                )
            )
        ).CreateAsync(
            new IosAppStoreReleaseCreateRequest
            {
                App = request.App,
                AppStoreId = "987654321",
                BuildNumber = request.BuildNumber,
                Storefront = request.Storefront,
            },
            "publisher"
        );
        var differentUrl = await CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.expo",
                    "2.0.0",
                    "https://apps.apple.com/au/app/renamed/id123456789"
                )
            )
        ).CreateAsync(request, "publisher");

        Assert.True(first.Success);
        Assert.False(differentId.Success);
        Assert.Equal("APP_STORE_RELEASE_CONFLICT", differentId.ErrorCode);
        Assert.False(differentUrl.Success);
        Assert.Equal("APP_STORE_RELEASE_CONFLICT", differentUrl.ErrorCode);
        Assert.Equal(1, await _db.Queryable<IosAppStoreRelease>().CountAsync());
    }

    [Fact]
    public async Task AppStore登记_配置Bundle漂移后相同唯一键返回事实冲突()
    {
        var request = new IosAppStoreReleaseCreateRequest
        {
            App = AppUpdateApps.MobileIos,
            AppStoreId = "123456789",
            BuildNumber = "200",
            Storefront = "au",
        };
        var first = await CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.expo",
                    "2.0.0",
                    "https://apps.apple.com/au/app/id123456789"
                )
            )
        ).CreateAsync(request, "admin");
        var drifted = await CreateReleaseService(
            new StubAppleLookupClient(
                new AppleAppStoreLookupResult(
                    "123456789",
                    "com.hbweb.expo.next",
                    "2.0.0",
                    "https://apps.apple.com/au/app/id123456789"
                )
            ),
            new AppUpdatePolicyOptions
            {
                MobileIosBundleIdentifier = "com.hbweb.expo.next",
            }
        ).CreateAsync(request, "publisher");

        Assert.True(first.Success);
        Assert.False(drifted.Success);
        Assert.Equal("APP_STORE_RELEASE_CONFLICT", drifted.ErrorCode);
        Assert.Equal(1, await _db.Queryable<IosAppStoreRelease>().CountAsync());
    }

    [Fact]
    public async Task IpadOta发布事实_runtime接受通用Token并限制120字符()
    {
        var service = CreateOtaService();
        var slashToken = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = "01010101-0101-4101-8101-010101010101",
                IosUpdateId = "02020202-0202-4202-8202-020202020202",
                Channel = "pos-ipad-release-slash-token",
                RuntimeVersion = "pos-ipad/2026.07.30",
            },
            "publisher"
        );
        var maxToken = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = "03030303-0303-4303-8303-030303030303",
                IosUpdateId = "04040404-0404-4404-8404-040404040404",
                Channel = "pos-ipad-release-max-token",
                RuntimeVersion = new string('a', 120),
            },
            "publisher"
        );
        var tooLong = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = "05050505-0505-4505-8505-050505050505",
                IosUpdateId = "06060606-0606-4606-8606-060606060606",
                Channel = "pos-ipad-release-too-long",
                RuntimeVersion = new string('a', 121),
            },
            "publisher"
        );

        Assert.True(slashToken.Success);
        Assert.Equal("pos-ipad/2026.07.30", slashToken.Data!.RuntimeVersion);
        Assert.True(maxToken.Success);
        Assert.Equal(120, maxToken.Data!.RuntimeVersion.Length);
        Assert.False(tooLong.Success);
        Assert.Equal("OTA_RUNTIME_INVALID", tooLong.ErrorCode);
    }

    [Fact]
    public async Task IpadOta发布事实_拒绝共享或通用Channel()
    {
        var service = CreateOtaService();
        var production = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = "07070707-0707-4707-8707-070707070707",
                IosUpdateId = "08080808-0808-4808-8808-080808080808",
                Channel = "pos-ipad-production",
                RuntimeVersion = "3.0.0",
            },
            "publisher"
        );
        var generic = await service.CreateReleaseAsync(
            new PosIpadOtaReleaseCreateRequest
            {
                UpdateGroupId = "09090909-0909-4909-8909-090909090909",
                IosUpdateId = "10101010-1010-4101-8101-101010101010",
                Channel = "generic-channel",
                RuntimeVersion = "3.0.0",
            },
            "publisher"
        );

        Assert.False(production.Success);
        Assert.Equal("OTA_CHANNEL_INVALID", production.ErrorCode);
        Assert.False(generic.Success);
        Assert.Equal("OTA_CHANNEL_INVALID", generic.ErrorCode);
    }

    [Fact]
    public async Task IpadOta发布预检_规范化合法Channel且保持只读()
    {
        var service = CreateOtaService();

        var result = await service.PreflightReleaseChannelAsync(
            new PosIpadOtaChannelPreflightRequest
            {
                Channel = " POS-IPAD-RELEASE-PREFLIGHT ",
            }
        );
        var maxLengthChannel = $"pos-ipad-release-{new string('a', 103)}";
        var maxLength = await service.PreflightReleaseChannelAsync(
            new PosIpadOtaChannelPreflightRequest
            {
                Channel = maxLengthChannel,
            }
        );

        Assert.True(result.Success);
        Assert.True(result.Data!.Available);
        Assert.Equal("pos-ipad-release-preflight", result.Data.Channel);
        Assert.True(maxLength.Success);
        Assert.True(maxLength.Data!.Available);
        Assert.Equal(120, maxLength.Data.Channel.Length);
        Assert.Equal(maxLengthChannel, maxLength.Data.Channel);
        Assert.Equal(0, await _db.Queryable<PosIpadOtaRelease>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PosIpadOtaRollout>().CountAsync());
    }

    [Fact]
    public async Task IpadOta发布预检_非法Channel复用发布登记验证()
    {
        var service = CreateOtaService();

        var shared = await service.PreflightReleaseChannelAsync(
            new PosIpadOtaChannelPreflightRequest
            {
                Channel = "pos-ipad-production",
            }
        );
        var tooLong = await service.PreflightReleaseChannelAsync(
            new PosIpadOtaChannelPreflightRequest
            {
                Channel = $"pos-ipad-release-{new string('a', 104)}",
            }
        );

        Assert.False(shared.Success);
        Assert.Equal("OTA_CHANNEL_INVALID", shared.ErrorCode);
        Assert.False(tooLong.Success);
        Assert.Equal("OTA_CHANNEL_INVALID", tooLong.ErrorCode);
        Assert.Equal(0, await _db.Queryable<PosIpadOtaRelease>().CountAsync());
    }

    [Fact]
    public async Task IpadOta发布预检_已登记Channel返回稳定冲突且不预留()
    {
        var release = await SeedOtaReleaseAsync();
        var service = CreateOtaService();

        var result = await service.PreflightReleaseChannelAsync(
            new PosIpadOtaChannelPreflightRequest
            {
                Channel = $" {release.Channel.ToUpperInvariant()} ",
            }
        );

        Assert.False(result.Success);
        Assert.Equal("OTA_CHANNEL_ALREADY_REGISTERED", result.ErrorCode);
        Assert.Equal(1, await _db.Queryable<PosIpadOtaRelease>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PosIpadOtaRollout>().CountAsync());
    }

    [Fact]
    public void Controller权限_管理写入JWT限定_内部决策ServiceToken限定_Mobile匿名()
    {
        AssertPolicy<AppUpdatePoliciesController>(
            nameof(AppUpdatePoliciesController.GetMobileIos),
            Permissions.System.ViewAppDownloads
        );
        AssertPolicy<AppUpdatePoliciesController>(
            nameof(AppUpdatePoliciesController.PutMobileIos),
            Permissions.System.ManageAppDownloads
        );
        var managementAuthorize = GetMethod<AppUpdatePoliciesController>(
                nameof(AppUpdatePoliciesController.PutMobileIos)
            )
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(managementAuthorize);
        Assert.True(string.IsNullOrEmpty(managementAuthorize!.AuthenticationSchemes));

        var internalAuthorize =
            typeof(InternalAppUpdateDecisionsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(
            ServiceApiTokenAuthenticationDefaults.AuthenticationScheme,
            internalAuthorize!.AuthenticationSchemes
        );
        Assert.Equal("Service.ReadAppUpdateDecisions", internalAuthorize.Policy);
        AssertPolicy<AppUpdatePoliciesController>(
            nameof(AppUpdatePoliciesController.GetPosIpadStoreOptions),
            Permissions.System.ViewAppDownloads
        );
        Assert.NotNull(
            GetMethod<MobileIosAppUpdatesController>(
                    nameof(MobileIosAppUpdatesController.Check)
                )
                .GetCustomAttribute<AllowAnonymousAttribute>()
        );

        var iosReleasePost = GetMethod<IosAppStoreReleasesController>(
                nameof(IosAppStoreReleasesController.Create)
            )
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.True(string.IsNullOrEmpty(iosReleasePost!.AuthenticationSchemes));
        Assert.Equal(Permissions.System.ManageAppDownloads, iosReleasePost.Policy);

        var otaPost = GetMethod<PosIpadOtaReleasesController>(
                nameof(PosIpadOtaReleasesController.Create)
            )
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.True(string.IsNullOrEmpty(otaPost!.AuthenticationSchemes));
        Assert.Equal(Permissions.System.ManageAppDownloads, otaPost.Policy);

        var otaPreflight = GetMethod<PosIpadOtaReleasesController>(
            nameof(PosIpadOtaReleasesController.Preflight)
        );
        var otaPreflightAuthorize = otaPreflight.GetCustomAttribute<AuthorizeAttribute>();
        var otaPreflightRoute = otaPreflight.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(otaPreflightAuthorize);
        Assert.True(string.IsNullOrEmpty(otaPreflightAuthorize!.AuthenticationSchemes));
        Assert.Equal(Permissions.System.ManageAppDownloads, otaPreflightAuthorize.Policy);
        Assert.Equal("preflight", otaPreflightRoute!.Template);
    }

    [Fact]
    public void 更新策略管理合同_包含冻结的乐观并发与Mobile及IpadBuild字段()
    {
        Assert.Equal(
            typeof(long?),
            typeof(NativeUpdatePolicyRequest)
                .GetProperty("ExpectedPolicyVersion")
                ?.PropertyType
        );
        Assert.Equal(
            typeof(long?),
            typeof(PosIpadOtaRolloutRequest)
                .GetProperty("ExpectedPolicyVersion")
                ?.PropertyType
        );
        Assert.Equal(
            typeof(int?),
            typeof(MobileIosNativeUpdatePolicyRequest)
                .GetProperty("MinimumSupportedBuildNumber")
                ?.PropertyType
        );
        Assert.Equal(
            typeof(int?),
            typeof(PosIpadNativeUpdatePolicyRequest)
                .GetProperty("MinimumSupportedBuildNumber")
                ?.PropertyType
        );
        Assert.Equal(
            typeof(int?),
            typeof(NativeUpdatePolicyDto)
                .GetProperty("MinimumSupportedBuildNumber")
                ?.PropertyType
        );
        Assert.Equal(
            typeof(int?),
            typeof(MobileIosNativeUpdatePolicy)
                .GetProperty("MinimumSupportedBuildNumber")
                ?.PropertyType
        );
        Assert.Equal(
            typeof(MobileIosNativeUpdatePolicyRequest),
            GetMethod<AppUpdatePoliciesController>(
                    nameof(AppUpdatePoliciesController.PutMobileIos)
                )
                .GetParameters()[0]
                .ParameterType
        );
        Assert.Equal(
            typeof(int?),
            typeof(PosIpadNativeUpdatePolicy)
                .GetProperty("MinimumSupportedBuildNumber")
                ?.PropertyType
        );
        Assert.Equal(6, typeof(NativeAppUpdateDecisionDto).GetProperties().Length);
    }

    [Fact]
    public void Schema迁移_独立接入并包含幂等表与活动rollout唯一索引()
    {
        var backendRoot = FindBackendRoot();
        var startup = File.ReadAllText(
            Path.Combine(backendRoot, "BlazorApp.Api", "Data", "StartupSchemaMigrator.cs")
        );
        var migrator = File.ReadAllText(
            Path.Combine(backendRoot, "BlazorApp.Api", "Data", "AppUpdatePolicySchemaMigrator.cs")
        );

        Assert.Contains("await AppUpdatePolicySchemaMigrator.EnsureAsync(db, logger);", startup);
        Assert.Contains("sp_getapplock", migrator);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[IosAppStoreRelease]', N'U') IS NULL", migrator);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[MobileIosNativeUpdatePolicy]', N'U') IS NULL", migrator);
        Assert.Contains("[MinimumSupportedBuildNumber] int NULL", migrator);
        Assert.Contains(
            "COL_LENGTH(N'[dbo].[MobileIosNativeUpdatePolicy]', N'MinimumSupportedBuildNumber') IS NULL",
            migrator
        );
        Assert.Contains("ALTER TABLE [dbo].[MobileIosNativeUpdatePolicy]", migrator);
        Assert.DoesNotContain("UPDATE [dbo].[MobileIosNativeUpdatePolicy]", migrator);
        Assert.DoesNotContain("INSERT INTO [dbo].[MobileIosNativeUpdatePolicy]", migrator);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[PosIpadNativeUpdatePolicy]', N'U') IS NULL", migrator);
        Assert.Contains(
            "COL_LENGTH(N'[dbo].[PosIpadNativeUpdatePolicy]', N'MinimumSupportedBuildNumber') IS NULL",
            migrator
        );
        Assert.Contains("ALTER TABLE [dbo].[PosIpadNativeUpdatePolicy]", migrator);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[PosIpadOtaRelease]', N'U') IS NULL", migrator);
        Assert.Contains(
            "UX_PosIpadOtaRelease_Environment_Channel",
            migrator
        );
        Assert.Contains("GROUP BY [Environment], [Channel]", migrator);
        Assert.Contains(
            "Duplicate production iPad OTA release channels exist.",
            migrator
        );
        Assert.Contains("IF OBJECT_ID(N'[dbo].[PosIpadOtaRollout]', N'U') IS NULL", migrator);
        Assert.Contains("WHERE [Enabled] = 1 AND [IsDeleted] = 0", migrator);
        Assert.Contains(
            "IF OBJECT_ID(N'[dbo].[PosHandheldUpdatePolicy]', N'U') IS NULL",
            migrator
        );
        Assert.Contains(
            "UX_PosHandheldUpdatePolicy_Lane",
            migrator
        );
        Assert.Contains(
            "IF OBJECT_ID(N'[dbo].[PosHandheldUpdatePolicyRevision]', N'U') IS NULL",
            migrator
        );
        Assert.Contains(
            "UX_PosHandheldUpdatePolicyRevision_Lane_Version",
            migrator
        );
        Assert.Contains("TR_PosHandheldUpdatePolicyRevision_AppendOnly", migrator);
        Assert.Contains("INSTEAD OF UPDATE, DELETE", migrator);
        Assert.DoesNotContain("INSERT INTO [dbo].[PosHandheldUpdatePolicy]", migrator);
        Assert.DoesNotContain("ALTER TABLE [MobileAppBuild]", migrator);
        Assert.DoesNotContain("ALTER TABLE [MobileAppOtaUpdate]", migrator);
        Assert.DoesNotContain("WpfAppRelease", migrator);
    }

    [Fact]
    public void MobileBuild迁移_新表直接含列_旧表幂等追加且不改写已有策略行()
    {
        var migrator = File.ReadAllText(
            Path.Combine(
                FindBackendRoot(),
                "BlazorApp.Api",
                "Data",
                "AppUpdatePolicySchemaMigrator.cs"
            )
        );
        var mobileTableStart = migrator.IndexOf(
            "CREATE TABLE [dbo].[MobileIosNativeUpdatePolicy]",
            StringComparison.Ordinal
        );
        var mobileTableEnd = migrator.IndexOf("    END;", mobileTableStart, StringComparison.Ordinal);
        var additiveGuard =
            "IF COL_LENGTH(N'[dbo].[MobileIosNativeUpdatePolicy]', N'MinimumSupportedBuildNumber') IS NULL";
        var additiveGuardStart = migrator.IndexOf(additiveGuard, StringComparison.Ordinal);

        Assert.True(mobileTableStart >= 0);
        Assert.True(mobileTableEnd > mobileTableStart);
        Assert.Contains(
            "[MinimumSupportedBuildNumber] int NULL",
            migrator[mobileTableStart..mobileTableEnd]
        );
        Assert.True(additiveGuardStart > mobileTableEnd);
        Assert.Single(
            Regex.Matches(migrator, Regex.Escape(additiveGuard))
                .Cast<System.Text.RegularExpressions.Match>()
        );
        Assert.Contains(
            "ALTER TABLE [dbo].[MobileIosNativeUpdatePolicy]\n            ADD [MinimumSupportedBuildNumber] int NULL;",
            migrator
        );
        Assert.DoesNotContain("UPDATE [dbo].[MobileIosNativeUpdatePolicy]", migrator);
        Assert.DoesNotContain("INSERT INTO [dbo].[MobileIosNativeUpdatePolicy]", migrator);
    }

    [Fact]
    public void 策略写入_SQLServer事务锁覆盖原生policyKey和productionRollout()
    {
        var backendRoot = FindBackendRoot();
        var mutationLock = File.ReadAllText(
            Path.Combine(
                backendRoot,
                "BlazorApp.Api",
                "Services",
                "AppUpdatePolicyMutationLock.cs"
            )
        );
        var nativeService = File.ReadAllText(
            Path.Combine(
                backendRoot,
                "BlazorApp.Api",
                "Services",
                "NativeAppUpdatePolicyService.cs"
            )
        );
        var otaService = File.ReadAllText(
            Path.Combine(
                backendRoot,
                "BlazorApp.Api",
                "Services",
                "PosIpadOtaPolicyService.cs"
            )
        );

        Assert.Contains("sys.sp_getapplock", mutationLock);
        Assert.Contains("@LockOwner = N'Transaction'", mutationLock);
        Assert.Contains("app-update-policy:native:", nativeService);
        Assert.Contains("app-update-policy:ota-rollout:", otaService);
    }

    [Fact]
    public void None决策_即使全局忽略null也保留冻结合同的全部字段()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        using var native = JsonDocument.Parse(
            JsonSerializer.Serialize(new NativeAppUpdateDecisionDto(), options)
        );
        using var ota = JsonDocument.Parse(
            JsonSerializer.Serialize(new PosIpadOtaDecisionDto(), options)
        );

        Assert.Equal(
            [
                "state",
                "policyVersion",
                "latestVersion",
                "minimumSupportedVersion",
                "appStoreUrl",
                "releaseMessage",
            ],
            native.RootElement.EnumerateObject().Select(item => item.Name).ToArray()
        );
        Assert.Equal(
            [
                "state",
                "policyVersion",
                "channel",
                "runtimeVersion",
                "iosUpdateId",
                "updateGroupId",
                "releaseMessage",
            ],
            ota.RootElement.EnumerateObject().Select(item => item.Name).ToArray()
        );
        Assert.All(
            native.RootElement.EnumerateObject().Skip(2),
            item => Assert.Equal(JsonValueKind.Null, item.Value.ValueKind)
        );
        Assert.All(
            ota.RootElement.EnumerateObject().Skip(2),
            item => Assert.Equal(JsonValueKind.Null, item.Value.ValueKind)
        );
    }

    [Fact]
    public async Task AppleLookup_非唯一结果安全返回null()
    {
        const string json = """
{
  "resultCount": 2,
  "results": [
    {
      "trackId": 123456789,
      "bundleId": "com.hbweb.expo",
      "version": "2.0.0",
      "trackViewUrl": "https://apps.apple.com/au/app/id123456789"
    },
    {
      "trackId": 987654321,
      "bundleId": "com.example.other",
      "version": "9.0.0",
      "trackViewUrl": "https://apps.apple.com/au/app/id987654321"
    }
  ]
}
""";
        using var client = new HttpClient(new JsonHttpMessageHandler(json))
        {
            BaseAddress = new Uri("https://itunes.apple.com/"),
        };
        var lookup = new AppleAppStoreLookupClient(
            client,
            NullLogger<AppleAppStoreLookupClient>.Instance
        );

        var result = await lookup.LookupAsync("123456789", "au");

        Assert.Null(result);
    }

    private NativeAppUpdatePolicyService CreateNativeService() =>
        new(_db, NullLogger<NativeAppUpdatePolicyService>.Instance);

    private PosIpadOtaPolicyService CreateOtaService() =>
        new(_db, NullLogger<PosIpadOtaPolicyService>.Instance);

    private IosAppStoreReleaseService CreateReleaseService(
        IAppleAppStoreLookupClient lookup,
        AppUpdatePolicyOptions? options = null
    ) =>
        new(
            _db,
            lookup,
            Options.Create(options ?? new AppUpdatePolicyOptions()),
            NullLogger<IosAppStoreReleaseService>.Instance
        );

    private async Task<IosAppStoreRelease> SeedIosReleaseAsync(string app, string version)
    {
        var release = new IosAppStoreRelease
        {
            Id = Guid.NewGuid(),
            App = app,
            AppStoreId = "123456789",
            BundleIdentifier = app == AppUpdateApps.MobileIos
                ? "com.hbweb.expo"
                : "com.hbweb.posipad",
            Version = version,
            BuildNumber = "100",
            Storefront = "au",
            AppStoreUrl = "https://apps.apple.com/au/app/id123456789",
            AppleVerifiedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false,
        };
        await _db.Insertable(release).ExecuteCommandAsync();
        return release;
    }

    private async Task<Store> SeedStoreAsync(string code, string name)
    {
        var store = new Store
        {
            StoreGUID = Guid.NewGuid().ToString(),
            StoreCode = code,
            StoreName = name,
            IsActive = true,
            IsDeleted = false,
        };
        await _db.Insertable(store).ExecuteCommandAsync();
        return store;
    }

    private async Task<PosIpadOtaRelease> SeedOtaReleaseAsync(
        string updateGroupId = "55555555-5555-4555-8555-555555555555",
        string iosUpdateId = "66666666-6666-4666-8666-666666666666"
    )
    {
        var release = new PosIpadOtaRelease
        {
            Id = Guid.NewGuid(),
            Environment = "production",
            UpdateGroupId = updateGroupId,
            IosUpdateId = iosUpdateId,
            Channel = $"pos-ipad-release-{updateGroupId[..8]}",
            RuntimeVersion = "3.0.0",
            GitCommitHash = "abc123",
            DashboardUrl = "https://expo.dev/accounts/hb/projects/pos-ipad/updates/test",
            PublishedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false,
        };
        await _db.Insertable(release).ExecuteCommandAsync();
        return release;
    }

    private static void AssertPolicy<TController>(string methodName, string policy)
    {
        var authorize = GetMethod<TController>(methodName).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal(policy, authorize!.Policy);
    }

    private static MethodInfo GetMethod<TController>(string methodName) =>
        typeof(TController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{typeof(TController).Name}.{methodName} missing.");

    private static void AssertPolicyVersionError<T>(
        ApiResponse<T> response,
        string errorCode,
        long? expectedPolicyVersion,
        long actualPolicyVersion
    )
    {
        Assert.False(response.Success);
        Assert.Equal(errorCode, response.ErrorCode);
        var details = JsonSerializer.SerializeToElement(response.Details);
        var expected = details.GetProperty("ExpectedPolicyVersion");
        if (expectedPolicyVersion.HasValue)
        {
            Assert.Equal(expectedPolicyVersion.Value, expected.GetInt64());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, expected.ValueKind);
        }

        Assert.Equal(
            actualPolicyVersion,
            details.GetProperty("ActualPolicyVersion").GetInt64()
        );
    }

    private static string FindBackendRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        return directory.Parent!.FullName;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private sealed class StubAppleLookupClient(AppleAppStoreLookupResult result)
        : IAppleAppStoreLookupClient
    {
        public Task<AppleAppStoreLookupResult?> LookupAsync(
            string appStoreId,
            string storefront,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AppleAppStoreLookupResult?>(result);
    }

    private sealed class JsonHttpMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                }
            );
        }
    }
}
