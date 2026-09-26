using Hbpos.Updater;

namespace Hbpos.Client.Tests;

public sealed class UpdaterOptionsTests
{
    [Fact]
    public void TryParse_reads_all_known_options()
    {
        var parsed = UpdaterOptions.TryParse(
            [
                "--installer", @"C:\Updates\setup.exe",
                "--installer-args", " /SP- /VERYSILENT ",
                "--app-exe", @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe",
                "--wait-pid", "1234",
                "--from", "1.8.3",
                "--to", "1.9.0",
                "--culture", "zh-CN",
                "--log", @"C:\Logs\install.log"
            ],
            out var options);

        Assert.True(parsed);
        Assert.Equal(
            new UpdaterOptions
            {
                InstallerPath = @"C:\Updates\setup.exe",
                InstallerArguments = "/SP- /VERYSILENT",
                AppExePath = @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe",
                WaitProcessId = 1234,
                FromVersion = "1.8.3",
                ToVersion = "1.9.0",
                Culture = "zh-CN",
                LogPath = @"C:\Logs\install.log"
            },
            options);
    }

    [Fact]
    public void TryParse_requires_installer_path()
    {
        Assert.False(UpdaterOptions.TryParse(["--to", "1.9.0"], out var options));
        Assert.Null(options);
        Assert.False(UpdaterOptions.TryParse(["--installer", "  "], out _));
    }

    [Fact]
    public void TryParse_skips_unknown_options_and_defaults_optional_values()
    {
        var parsed = UpdaterOptions.TryParse(
            ["--future-flag", "x", "--installer", "setup.exe", "--wait-pid", "not-a-number"],
            out var options);

        Assert.True(parsed);
        Assert.NotNull(options);
        Assert.Equal("setup.exe", options.InstallerPath);
        Assert.Null(options.WaitProcessId);
        Assert.Equal(string.Empty, options.InstallerArguments);
        Assert.Equal("en", options.Culture);
        Assert.Null(options.LogPath);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void TryParse_ignores_non_positive_process_ids(string value)
    {
        Assert.True(UpdaterOptions.TryParse(["--installer", "setup.exe", "--wait-pid", value], out var options));
        Assert.Null(options!.WaitProcessId);
    }
}
