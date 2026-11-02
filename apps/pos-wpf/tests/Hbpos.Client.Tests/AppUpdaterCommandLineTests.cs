using Hbpos.Client.Wpf.Services;
using Hbpos.Updater;

namespace Hbpos.Client.Tests;

public sealed class AppUpdaterCommandLineTests
{
    [Fact]
    public void Build_round_trips_through_windows_argument_parsing_into_updater_options()
    {
        var request = new AppUpdaterLaunchRequest(
            @"C:\Users\Shop Owner\AppData\Local\Hbpos\AppUpdates\Hbpos.Client.Wpf-1.9.0-x64.exe",
            "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS",
            @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe",
            4321,
            "1.8.3",
            "1.9.0",
            "zh-CN",
            @"C:\Users\Shop Owner\AppData\Local\Hbpos.Client\update-logs\install-1.9.0-20260926-101500.log");

        var args = WindowsArgumentParser.Parse(AppUpdaterCommandLine.Build(request));

        Assert.True(UpdaterOptions.TryParse(args, out var options));
        Assert.Equal(
            new UpdaterOptions
            {
                InstallerPath = request.InstallerPath,
                InstallerArguments = request.InstallerArguments,
                AppExePath = request.AppExePath,
                WaitProcessId = request.WaitProcessId,
                FromVersion = request.FromVersion,
                ToVersion = request.ToVersion,
                Culture = request.Culture,
                LogPath = request.LogPath
            },
            options);
    }

    [Fact]
    public void Build_passes_release_contract_and_unattended_flag_to_updater_options()
    {
        var request = new AppUpdaterLaunchRequest(
            "setup.exe", "/VERYSILENT", @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe", 7, "1.8.3", "1.9.0", "zh-CN", null)
        {
            DownloadUrl = "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/production/1.9.0/setup.exe",
            Sha256 = new string('a', 64),
            FileSize = 1_048_576,
            Unattended = true,
            AgentOnly = true
        };

        Assert.True(UpdaterOptions.TryParse(WindowsArgumentParser.Parse(AppUpdaterCommandLine.Build(request)), out var options));

        Assert.Equal(request.DownloadUrl, options!.DownloadUrl);
        Assert.Equal(request.Sha256, options.Sha256);
        Assert.Equal(1_048_576, options.FileSize);
        Assert.True(options.Unattended);
        Assert.True(options.AgentOnly);
    }

    [Fact]
    public void Build_omits_partial_release_contract_and_defaults_to_attended()
    {
        var request = new AppUpdaterLaunchRequest("setup.exe", string.Empty, "app.exe", 1, "1.0.0", "1.0.1", "en", null)
        {
            DownloadUrl = "https://example.invalid/setup.exe",
            Sha256 = null,
            FileSize = 10
        };

        var commandLine = AppUpdaterCommandLine.Build(request);

        Assert.DoesNotContain("--download-url", commandLine);
        Assert.DoesNotContain("--sha256", commandLine);
        Assert.DoesNotContain("--file-size", commandLine);
        Assert.DoesNotContain("--unattended", commandLine);
        Assert.DoesNotContain("--agent-only", commandLine);
        Assert.True(UpdaterOptions.TryParse(WindowsArgumentParser.Parse(commandLine), out var options));
        Assert.Null(options!.DownloadUrl);
        Assert.False(options.Unattended);
        Assert.False(options.AgentOnly);
    }

    [Fact]
    public void Build_omits_log_option_when_no_log_path_is_available()
    {
        var commandLine = AppUpdaterCommandLine.Build(new AppUpdaterLaunchRequest(
            "setup.exe", string.Empty, "app.exe", 1, "1.0.0", "1.0.1", "en", null));

        Assert.DoesNotContain("--log", commandLine);
        Assert.True(UpdaterOptions.TryParse(WindowsArgumentParser.Parse(commandLine), out var options));
        Assert.Null(options!.LogPath);
        Assert.Equal(string.Empty, options.InstallerArguments);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData(@"C:\dir with space\")]
    [InlineData(@"C:\dir\\")]
    [InlineData("say \"hi\"")]
    [InlineData(@"back\""slash")]
    [InlineData(@"/LOG=""C:\a b\log.txt""")]
    [InlineData("tab\there")]
    public void QuoteArgument_survives_windows_argument_parsing(string value)
    {
        var parsed = WindowsArgumentParser.Parse(AppUpdaterCommandLine.QuoteArgument(value));

        Assert.Equal(value, Assert.Single(parsed));
    }
}
