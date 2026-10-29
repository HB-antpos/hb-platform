using Hbpos.Updater;

namespace Hbpos.Client.Tests;

public sealed class UpdateAgentLayoutTests
{
    private const string AppDirectory = @"C:\Program Files\HB POS";
    private const string ProgramFiles = @"C:\Program Files";

    [Fact]
    public void Paths_live_under_install_folder_update_agent_directory()
    {
        var layout = UpdateAgentLayout.ForAppExecutable(Path.Combine(AppDirectory, "Hbpos.Client.Wpf.exe"));

        Assert.Equal(AppDirectory, layout.AppDirectory);
        Assert.Equal(Path.Combine(AppDirectory, "UpdateAgent", "status", "abc.json"), layout.StatusFile("abc"));
        Assert.Equal(Path.Combine(AppDirectory, "UpdateAgent", "status", "abc.progress"), layout.ProgressFile("abc"));
        Assert.Equal(Path.Combine(AppDirectory, "UpdateAgent", "logs", "abc.log"), layout.LogFile("abc"));
        Assert.Equal(Path.Combine(AppDirectory, "UpdateAgent", "packages", "sha.exe"), layout.PackageFile("sha"));
        Assert.Equal(Path.Combine(AppDirectory, "UpdateAgent", "runtime", "abc"), layout.RuntimeDirectory("abc"));
        Assert.Equal(Path.Combine(AppDirectory, "updater", "Hbpos.Updater.exe"), layout.UpdaterExecutablePath);
    }

    [Fact]
    public void TryFromAgentBaseDirectory_derives_install_folder_for_dispatcher_and_worker()
    {
        Assert.True(UpdateAgentLayout.TryFromAgentBaseDirectory(Path.Combine(AppDirectory, "updater") + @"\", worker: false, out var dispatcher));
        Assert.Equal(AppDirectory, dispatcher.AppDirectory);

        Assert.True(UpdateAgentLayout.TryFromAgentBaseDirectory(
            Path.Combine(AppDirectory, "UpdateAgent", "runtime", "abc") + @"\", worker: true, out var worker));
        Assert.Equal(AppDirectory, worker.AppDirectory);
    }

    [Theory]
    [InlineData(@"C:\Users\Cashier\AppData\Local\Temp\HbposUpdater\20260926-abc", false)]
    [InlineData(@"C:\Program Files\HB POS", false)]
    [InlineData(@"C:\Program Files\HB POS\UpdateAgent\runtime", true)]
    [InlineData(@"C:\Users\Cashier\Downloads\runtime\abc", true)]
    [InlineData(@"C:\Program Files\HB POS\updater", true)]
    public void TryFromAgentBaseDirectory_rejects_processes_started_from_unexpected_folders(string baseDirectory, bool worker)
    {
        // 中文注释：代理只认自己在安装目录里的两个固定位置，普通用户从别处复制一份程序来冒充代理会被拒绝。
        Assert.False(UpdateAgentLayout.TryFromAgentBaseDirectory(baseDirectory, worker, out _));
    }

    [Theory]
    [InlineData(@"C:\Program Files\HB POS", true)]
    [InlineData(@"C:\Program Files\HB POS\", true)]
    [InlineData(@"c:\program files\hb pos", true)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\Program Files (x86)\HB POS", false)]
    [InlineData(@"C:\Program Files\..\Users\Public\HB POS", false)]
    [InlineData(@"C:\HB POS", false)]
    [InlineData(@"D:\Program Files\HB POS", false)]
    [InlineData(@"C:\Program FilesX\HB POS", false)]
    public void IsUnderProgramFiles_only_accepts_admin_protected_install_locations(string appDirectory, bool expected)
    {
        Assert.Equal(expected, new UpdateAgentLayout(appDirectory).IsUnderProgramFiles(ProgramFiles));
    }

    [Fact]
    public void Status_round_trips_and_only_accepts_matching_request_id()
    {
        var status = new UpdateAgentStatus("abc", UpdateAgentState.Failed, 4, "installer-exit-code", DateTimeOffset.UnixEpoch);

        Assert.True(UpdateAgentStatus.TryParse(status.Serialize(), "abc", out var parsed));
        Assert.Equal(status, parsed);
        Assert.True(parsed!.IsFinal);
        Assert.False(UpdateAgentStatus.TryParse(status.Serialize(), "other", out _));
        Assert.False(UpdateAgentStatus.TryParse("{not json", "abc", out _));
        Assert.False(UpdateAgentStatus.TryParse(null, "abc", out _));
        Assert.False(new UpdateAgentStatus("abc", UpdateAgentState.Installing, null, null, DateTimeOffset.UnixEpoch).IsFinal);
    }
}
