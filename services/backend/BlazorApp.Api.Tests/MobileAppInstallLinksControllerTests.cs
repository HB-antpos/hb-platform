using BlazorApp.Api.Controllers;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class MobileAppInstallLinksControllerTests
{
    private static (Mock<IMobileAppBuildService> Builds, Mock<IIosAppStoreReleaseService> Ios) CreateMocks(
        List<IosAppStoreReleaseDto> releases,
        MobileAppBuildDto? latestAndroid
    )
    {
        var builds = new Mock<IMobileAppBuildService>();
        builds
            .Setup(x => x.GetLatestAsync(MobileAppKeys.Mobile, "production"))
            .ReturnsAsync(ApiResponse<MobileAppBuildDto?>.OK(latestAndroid));
        var ios = new Mock<IIosAppStoreReleaseService>();
        ios
            .Setup(x => x.GetAsync(It.Is<IosAppStoreReleaseQuery>(q =>
                q.App == AppUpdateApps.MobileIos && q.Storefront == "au")))
            .ReturnsAsync(ApiResponse<List<IosAppStoreReleaseDto>>.OK(releases));
        return (builds, ios);
    }

    private static MobileAppInstallLinksDto ReadData(IActionResult response)
    {
        var ok = Assert.IsType<OkObjectResult>(response);
        var body = Assert.IsType<ApiResponse<MobileAppInstallLinksDto>>(ok.Value);
        Assert.True(body.Success);
        return Assert.IsType<MobileAppInstallLinksDto>(body.Data);
    }

    [Fact]
    public async Task Get_ReturnsLatestIosReleaseAndProductionAndroidBuildPreferringCosMirror()
    {
        var (builds, ios) = CreateMocks(
            [
                new() { Version = "1.0.5", BuildNumber = "48", AppStoreUrl = "https://apps.apple.com/au/app/id1", AppleVerifiedAtUtc = new DateTime(2026, 9, 1) },
                new() { Version = "1.0.6", BuildNumber = "50", AppStoreUrl = "https://apps.apple.com/au/app/id1", AppleVerifiedAtUtc = new DateTime(2026, 9, 22) },
            ],
            new MobileAppBuildDto
            {
                EasBuildId = "build-1",
                AppVersion = "1.0.6",
                AppBuildVersion = "51",
                ArtifactUrl = "https://expo.dev/artifacts/eas/a.apk",
                CosArtifactUrl = "https://cos.example.com/a.apk",
                ArtifactSize = 1024,
                CompletedAt = new DateTime(2026, 9, 23),
            }
        );

        var data = ReadData(await new MobileAppInstallLinksController(builds.Object, ios.Object).Get());

        Assert.NotNull(data.Ios);
        Assert.Equal("1.0.6", data.Ios!.Version);
        Assert.Equal("50", data.Ios.BuildNumber);
        Assert.NotNull(data.Android);
        Assert.Equal("build-1", data.Android!.EasBuildId);
        Assert.Equal("https://cos.example.com/a.apk", data.Android.DownloadUrl);
        Assert.Equal(1024, data.Android.ArtifactSize);
    }

    [Fact]
    public async Task Get_FallsBackToEasArtifactAndReturnsNullIosWhenNothingRegistered()
    {
        var (builds, ios) = CreateMocks(
            [],
            new MobileAppBuildDto { EasBuildId = "build-2", ArtifactUrl = "https://expo.dev/artifacts/eas/b.apk" }
        );

        var data = ReadData(await new MobileAppInstallLinksController(builds.Object, ios.Object).Get());

        Assert.Null(data.Ios);
        Assert.Equal("https://expo.dev/artifacts/eas/b.apk", data.Android!.DownloadUrl);
    }

    [Fact]
    public async Task Get_ReturnsNullAndroidWhenNoDownloadableBuild()
    {
        var (builds, ios) = CreateMocks([], null);

        var data = ReadData(await new MobileAppInstallLinksController(builds.Object, ios.Object).Get());

        Assert.Null(data.Android);
        Assert.Null(data.Ios);
    }
}
