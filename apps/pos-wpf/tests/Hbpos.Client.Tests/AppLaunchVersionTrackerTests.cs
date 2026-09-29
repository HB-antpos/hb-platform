using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class AppLaunchVersionTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hbpos-launch-version-{Guid.NewGuid():N}");

    private string MarkerPath => Path.Combine(_directory, "nested", "last-launched-version.txt");

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
    public void RecordLaunch_does_not_announce_first_launch_but_remembers_version()
    {
        var tracker = new AppLaunchVersionTracker(MarkerPath);

        Assert.Null(tracker.RecordLaunch("1.8.3"));
        Assert.Equal("1.8.3", File.ReadAllText(MarkerPath));
    }

    [Fact]
    public void RecordLaunch_announces_new_version_only_once()
    {
        var tracker = new AppLaunchVersionTracker(MarkerPath);
        tracker.RecordLaunch("1.8.3");

        Assert.Equal(new AppLaunchVersionNotice("1.9.0", IsRollback: false), tracker.RecordLaunch("1.9.0"));
        Assert.Null(tracker.RecordLaunch("1.9.0"));
    }

    [Fact]
    public void RecordLaunch_marks_lower_version_as_rollback()
    {
        var tracker = new AppLaunchVersionTracker(MarkerPath);
        tracker.RecordLaunch("1.10.0");

        Assert.Equal(new AppLaunchVersionNotice("1.9.5", IsRollback: true), tracker.RecordLaunch("1.9.5"));
    }

    [Fact]
    public void RecordLaunch_ignores_unpublished_local_builds()
    {
        var tracker = new AppLaunchVersionTracker(MarkerPath);
        tracker.RecordLaunch("1.8.3");

        Assert.Null(tracker.RecordLaunch("0.0.0"));
        Assert.Equal("1.8.3", File.ReadAllText(MarkerPath));
    }

    [Fact]
    public void RecordLaunch_tolerates_unreadable_marker()
    {
        // 标记路径被目录占用时读写都会失败，启动页只是不提示。
        Directory.CreateDirectory(MarkerPath);
        var tracker = new AppLaunchVersionTracker(MarkerPath);

        Assert.Null(tracker.RecordLaunch("1.9.0"));
    }
}
