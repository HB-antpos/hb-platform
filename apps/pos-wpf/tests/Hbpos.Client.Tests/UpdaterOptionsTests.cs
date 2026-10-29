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

    [Fact]
    public void TryParse_reads_agent_release_contract_and_unattended_flag()
    {
        Assert.True(UpdaterOptions.TryParse(
            [
                "--installer", "setup.exe",
                "--download-url", " https://example.invalid/setup.exe ",
                "--sha256", "abc",
                "--file-size", "2048",
                "--unattended", "1",
                "--agent-only", "1"
            ],
            out var options));

        Assert.Equal("https://example.invalid/setup.exe", options!.DownloadUrl);
        Assert.Equal("abc", options.Sha256);
        Assert.Equal(2048, options.FileSize);
        Assert.True(options.Unattended);
        Assert.True(options.AgentOnly);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("")]
    public void TryParse_only_treats_exactly_one_as_agent_only(string value)
    {
        Assert.True(UpdaterOptions.TryParse(["--installer", "setup.exe", "--agent-only", value], out var options));
        Assert.False(options!.AgentOnly);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void TryParse_ignores_invalid_file_sizes(string value)
    {
        Assert.True(UpdaterOptions.TryParse(["--installer", "setup.exe", "--file-size", value], out var options));
        Assert.Null(options!.FileSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("")]
    public void TryParse_only_treats_exactly_one_as_unattended(string value)
    {
        Assert.True(UpdaterOptions.TryParse(["--installer", "setup.exe", "--unattended", value], out var options));
        Assert.False(options!.Unattended);
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
