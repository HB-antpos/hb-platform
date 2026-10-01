using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers;

/// <summary>
/// 移动端「App 安装」页只读接口：只暴露两端最新正式版的版本号与安装地址，
/// 不含策略、历史与回撤等版本管理数据，因此使用独立权限而不复用 System.ViewAppDownloads。
/// </summary>
[ApiController]
[Route("api/mobile-app-install-links")]
[Authorize]
public sealed class MobileAppInstallLinksController(
    IMobileAppBuildService buildService,
    IIosAppStoreReleaseService iosReleaseService
) : ControllerBase
{
    private const string ProductionProfile = "production";
    private const string AppStoreStorefront = "au";

    [HttpGet]
    [Authorize(Policy = Permissions.System.ViewMobileAppInstallLinks)]
    public async Task<IActionResult> Get()
    {
        var iosResult = await iosReleaseService.GetAsync(
            new IosAppStoreReleaseQuery { App = AppUpdateApps.MobileIos, Storefront = AppStoreStorefront }
        );
        if (!iosResult.Success)
        {
            return Ok(ApiResponse<MobileAppInstallLinksDto>.Error(iosResult.Message, iosResult.ErrorCode, iosResult.Details));
        }

        var androidResult = await buildService.GetLatestAsync(MobileAppKeys.Mobile, ProductionProfile);
        if (!androidResult.Success)
        {
            return Ok(ApiResponse<MobileAppInstallLinksDto>.Error(androidResult.Message, androidResult.ErrorCode, androidResult.Details));
        }

        return Ok(ApiResponse<MobileAppInstallLinksDto>.OK(new MobileAppInstallLinksDto
        {
            Ios = MapIos(iosResult.Data),
            Android = MapAndroid(androidResult.Data),
        }));
    }

    private static MobileAppInstallIosDto? MapIos(IEnumerable<IosAppStoreReleaseDto>? releases)
    {
        // 登记时已向 Apple 核验上架，按核验时间取最新一条；商店链接始终打开商店当前版本。
        var latest = releases?
            .Where(item => !string.IsNullOrWhiteSpace(item.AppStoreUrl))
            .OrderByDescending(item => item.AppleVerifiedAtUtc)
            .FirstOrDefault();
        return latest == null
            ? null
            : new MobileAppInstallIosDto
            {
                Version = latest.Version,
                BuildNumber = latest.BuildNumber,
                AppStoreUrl = latest.AppStoreUrl,
                AppleVerifiedAtUtc = latest.AppleVerifiedAtUtc,
            };
    }

    private static MobileAppInstallAndroidDto? MapAndroid(MobileAppBuildDto? build)
    {
        // GetLatestAsync 已过滤掉未完成、不安全镜像和已过期且无镜像的构建。
        var downloadUrl = !string.IsNullOrWhiteSpace(build?.CosArtifactUrl)
            ? build.CosArtifactUrl
            : build?.ArtifactUrl;
        return build == null || string.IsNullOrWhiteSpace(downloadUrl)
            ? null
            : new MobileAppInstallAndroidDto
            {
                EasBuildId = build.EasBuildId,
                AppVersion = build.AppVersion,
                AppBuildVersion = build.AppBuildVersion,
                DownloadUrl = downloadUrl,
                ArtifactSize = build.ArtifactSize,
                CompletedAt = build.CompletedAt,
            };
    }
}
