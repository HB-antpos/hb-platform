using Hbpos.Updater;

namespace Hbpos.Client.Tests;

public sealed class InstallerProgressSnapshotTests
{
    [Theory]
    [InlineData("prepare 0", "Preparing", 0)]
    [InlineData("install 42", "Installing", 42)]
    [InlineData("  INSTALL 7\r\n", "Installing", 7)]
    [InlineData("finish 100", "Finishing", 100)]
    [InlineData("install 250", "Installing", 100)]
    public void TryParse_reads_stage_and_percent_written_by_inno(string content, string stage, int percent)
    {
        Assert.True(InstallerProgressSnapshot.TryParse(content, out var snapshot));
        Assert.Equal(new InstallerProgressSnapshot(Enum.Parse<InstallerProgressStage>(stage), percent), snapshot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("install")]
    [InlineData("install ")]
    [InlineData("install 4x")]
    [InlineData("install -3")]
    [InlineData("copy 30")]
    [InlineData("install 30 extra")]
    public void TryParse_rejects_empty_or_half_written_content(string? content)
    {
        Assert.False(InstallerProgressSnapshot.TryParse(content, out _));
    }
}
