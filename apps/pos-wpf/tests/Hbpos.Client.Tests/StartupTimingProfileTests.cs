using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class StartupTimingProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hbpos-startup-profile-{Guid.NewGuid():N}");

    private string ProfilePath => Path.Combine(_directory, "startup-profile.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Missing_profile_uses_defaults()
    {
        var profile = StartupTimingProfile.Load(ProfilePath);

        Assert.Null(profile.CultureName);
        Assert.Null(profile.StoreLabel);
        Assert.Equal(StartupTimingProfile.DefaultExpectedDurations, profile.GetExpectedDurations());
    }

    [Fact]
    public void Expected_durations_use_median_of_recent_runs_and_survive_reload()
    {
        var profile = StartupTimingProfile.Load(ProfilePath);
        foreach (var catalogMs in new[] { 900, 1100, 30_000, 1000, 1200 })
        {
            profile.RecordRun(new Dictionary<StartupPhase, TimeSpan>
            {
                [StartupPhase.Catalog] = TimeSpan.FromMilliseconds(catalogMs)
            });
        }

        profile.RememberCulture("zh-CN");
        profile.RememberStoreLabel("Morayfield · POS-03");
        Assert.True(profile.TrySave());

        var reloaded = StartupTimingProfile.Load(ProfilePath);
        var expected = reloaded.GetExpectedDurations();

        // 30 秒的偶发超时被中位数过滤掉。
        Assert.Equal(TimeSpan.FromMilliseconds(1100), expected[StartupPhase.Catalog]);
        Assert.Equal(StartupTimingProfile.DefaultExpectedDurations[StartupPhase.Update], expected[StartupPhase.Update]);
        Assert.Equal("zh-CN", reloaded.CultureName);
        Assert.Equal("Morayfield · POS-03", reloaded.StoreLabel);
        Assert.False(File.Exists(ProfilePath + ".tmp"));
    }

    [Fact]
    public void Only_the_most_recent_samples_are_kept()
    {
        var profile = StartupTimingProfile.Load(ProfilePath);
        foreach (var servicesMs in new[] { 5000, 5000, 5000, 400, 420, 410, 430, 440 })
        {
            profile.RecordRun(new Dictionary<StartupPhase, TimeSpan>
            {
                [StartupPhase.Services] = TimeSpan.FromMilliseconds(servicesMs)
            });
        }

        // 早期的慢样本被挤出窗口，预期耗时跟上本机的最新状态。
        Assert.Equal(TimeSpan.FromMilliseconds(420), profile.GetExpectedDurations()[StartupPhase.Services]);
    }

    [Fact]
    public void Even_sample_count_uses_average_of_middle_values_and_extremes_are_clamped()
    {
        var profile = StartupTimingProfile.Load(ProfilePath);
        profile.RecordRun(new Dictionary<StartupPhase, TimeSpan> { [StartupPhase.Display] = TimeSpan.FromMilliseconds(300) });
        profile.RecordRun(new Dictionary<StartupPhase, TimeSpan> { [StartupPhase.Display] = TimeSpan.FromMilliseconds(500) });
        profile.RecordRun(new Dictionary<StartupPhase, TimeSpan> { [StartupPhase.Device] = TimeSpan.Zero });
        profile.RecordRun(new Dictionary<StartupPhase, TimeSpan> { [StartupPhase.Update] = TimeSpan.FromMinutes(10) });

        var expected = profile.GetExpectedDurations();

        Assert.Equal(TimeSpan.FromMilliseconds(400), expected[StartupPhase.Display]);
        // 0 记为 1ms：极快的阶段权重应接近 0，而不是回落到默认值。
        Assert.Equal(TimeSpan.FromMilliseconds(1), expected[StartupPhase.Device]);
        Assert.Equal(TimeSpan.FromSeconds(60), expected[StartupPhase.Update]);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"Phases\":{\"Catalog\":\"slow\"}}")]
    public void Corrupt_profile_falls_back_to_defaults_and_can_be_rewritten(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ProfilePath, content);

        var profile = StartupTimingProfile.Load(ProfilePath);

        Assert.Equal(StartupTimingProfile.DefaultExpectedDurations, profile.GetExpectedDurations());
        profile.RecordRun(new Dictionary<StartupPhase, TimeSpan> { [StartupPhase.Catalog] = TimeSpan.FromMilliseconds(750) });
        Assert.True(profile.TrySave());
        Assert.Equal(
            TimeSpan.FromMilliseconds(750),
            StartupTimingProfile.Load(ProfilePath).GetExpectedDurations()[StartupPhase.Catalog]);
    }

    [Theory]
    [InlineData(" Morayfield ", " POS-03 ", "Morayfield · POS-03")]
    [InlineData("Morayfield", "", "Morayfield")]
    [InlineData("Morayfield", null, "Morayfield")]
    [InlineData("  ", "POS-03", null)]
    [InlineData(null, "POS-03", null)]
    public void Store_label_combines_store_name_and_device_code(string? storeName, string? deviceCode, string? expected)
    {
        Assert.Equal(expected, StartupTimingProfile.FormatStoreLabel(storeName, deviceCode));
    }

    [Fact]
    public void Blank_culture_and_store_label_are_cleared()
    {
        var profile = StartupTimingProfile.Load(ProfilePath);
        profile.RememberCulture("zh-CN");
        profile.RememberStoreLabel("Morayfield");

        profile.RememberCulture("  ");
        profile.RememberStoreLabel(null);

        Assert.Null(profile.CultureName);
        Assert.Null(profile.StoreLabel);
    }
}
