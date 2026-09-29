using System.Globalization;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class StartupProgressStateTests
{
    [Fact]
    public void SetStage_updates_percent_and_progress_text()
    {
        var state = new StartupProgressState();
        var changedProperties = new List<string>();
        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changedProperties.Add(args.PropertyName);
            }
        };

        state.SetStage(50);

        Assert.Equal(50, state.StagePercent);
        Assert.Equal("50%", state.ProgressText);
        Assert.Contains(nameof(StartupProgressState.StagePercent), changedProperties);
        Assert.Contains(nameof(StartupProgressState.ProgressText), changedProperties);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(110, 100)]
    public void SetStage_clamps_percent_into_valid_range(int input, int expected)
    {
        var state = new StartupProgressState();

        state.SetStage(input);

        Assert.Equal(expected, state.StagePercent);
    }

    [Theory]
    [InlineData("en-US", false, "Updated to 1.9.0")]
    [InlineData("en-US", true, "Switched back to 1.9.0")]
    [InlineData("zh-CN", false, "已更新到 1.9.0")]
    [InlineData("zh-CN", true, "已回退到 1.9.0")]
    public void SetVersion_shows_localized_update_notice(string culture, bool isRollback, string expectedNotice)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        var state = new StartupProgressState();

        state.SetVersion(
            "1.9.0",
            new AppLaunchVersionNotice("1.9.0", isRollback),
            localization.T,
            CultureInfo.GetCultureInfo(culture));

        Assert.Equal("1.9.0", state.VersionText);
        Assert.Equal(expectedNotice, state.UpdateNoticeText);
        Assert.True(state.HasUpdateNotice);
    }

    [Fact]
    public void LocalizeUpdateNotice_reformats_notice_after_saved_language_loads()
    {
        var localization = new LocalizationService();
        var state = new StartupProgressState();
        state.SetVersion("1.9.0", new AppLaunchVersionNotice("1.9.0", false), localization.T, localization.CurrentCulture);
        Assert.Equal("Updated to 1.9.0", state.UpdateNoticeText);

        localization.SetCulture("zh-CN");
        state.LocalizeUpdateNotice(localization.T, localization.CurrentCulture);

        Assert.Equal("已更新到 1.9.0", state.UpdateNoticeText);
    }

    [Fact]
    public void SetVersion_without_notice_keeps_generic_loading_hint()
    {
        var state = new StartupProgressState();
        var changedProperties = new List<string>();
        state.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? string.Empty);

        state.SetVersion("1.8.3", null, key => key, CultureInfo.InvariantCulture);

        Assert.Equal("1.8.3", state.VersionText);
        Assert.False(state.HasUpdateNotice);
        Assert.Contains(nameof(StartupProgressState.VersionText), changedProperties);
    }

    [Fact]
    public void Version_label_is_localized()
    {
        var localization = new LocalizationService();

        Assert.Equal("Version", localization.T("startup.versionLabel"));
        localization.SetCulture("zh-CN");
        Assert.Equal("版本", localization.T("startup.versionLabel"));
    }
}
