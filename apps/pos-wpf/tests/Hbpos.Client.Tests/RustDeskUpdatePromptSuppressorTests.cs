using Hbpos.Client.Wpf.Services;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.Client.Tests;

/// <summary>受管 RustDesk 的“版本更新”横幅：只对已登记的设备改当前用户的本地配置，幂等。</summary>
public sealed class RustDeskUpdatePromptSuppressorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hbpos-rustdesk-prompt-" + Guid.NewGuid().ToString("N"));
    private readonly RemoteMaintenanceJournal _journal;

    public RustDeskUpdatePromptSuppressorTests()
    {
        Directory.CreateDirectory(_root);
        _journal = new RemoteMaintenanceJournal(Path.Combine(_root, "journal.json"), new PrefixProtector());
    }

    private string ConfigPath => Path.Combine(_root, "RustDesk", "config", "RustDesk_local.toml");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Committed_device_gets_the_update_prompt_turned_off_once()
    {
        await WriteJournalAsync(RemoteMaintenanceOperationState.Committed);
        var suppressor = new RustDeskUpdatePromptSuppressor(_journal, ConfigPath);

        Assert.True(await suppressor.ApplyAsync());
        Assert.Equal("[options]\nenable-check-update = 'N'\n", await File.ReadAllTextAsync(ConfigPath));
        // 第二次已是关闭状态，不再改写。
        Assert.False(await suppressor.ApplyAsync());
    }

    [Fact]
    public async Task Existing_local_settings_are_kept()
    {
        await WriteJournalAsync(RemoteMaintenanceOperationState.Committed);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, "remote_id = '9'\n\n[options]\nlang = 'zh-cn'\nenable-check-update = 'Y'\n");

        Assert.True(await new RustDeskUpdatePromptSuppressor(_journal, ConfigPath).ApplyAsync());

        Assert.Equal("remote_id = '9'\n\n[options]\nlang = 'zh-cn'\nenable-check-update = 'N'\n", await File.ReadAllTextAsync(ConfigPath));
    }

    [Theory]
    [InlineData(RemoteMaintenanceOperationState.Prepared)]
    [InlineData(RemoteMaintenanceOperationState.InstalledPendingCommit)]
    [InlineData(RemoteMaintenanceOperationState.Failed)]
    public async Task Devices_without_a_committed_install_are_left_alone(RemoteMaintenanceOperationState state)
    {
        await WriteJournalAsync(state);

        Assert.False(await new RustDeskUpdatePromptSuppressor(_journal, ConfigPath).ApplyAsync());
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task Devices_without_remote_maintenance_are_left_alone()
    {
        Assert.False(await new RustDeskUpdatePromptSuppressor(_journal, ConfigPath).ApplyAsync());
        Assert.False(File.Exists(ConfigPath));
    }

    private Task WriteJournalAsync(RemoteMaintenanceOperationState state) => _journal.WriteAsync(new RemoteMaintenanceJournalState(
        Guid.NewGuid(), Guid.NewGuid(), state, "rustdesk-1", "1.4.9", "protected:password", null, null, DateTimeOffset.UtcNow));

    private sealed class PrefixProtector : IRemoteMaintenanceSecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;
        public string? Unprotect(string protectedValue) => protectedValue.StartsWith("protected:", StringComparison.Ordinal) ? protectedValue[10..] : null;
    }
}
