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
