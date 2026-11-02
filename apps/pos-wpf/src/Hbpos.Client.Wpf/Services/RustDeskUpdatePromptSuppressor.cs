using System.IO;
using System.Text;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 关掉受管 RustDesk 界面上的“版本更新”横幅，避免门店员工点“更新”把 RustDesk 升到未经验收的版本。
/// 横幅开关在当前 Windows 用户的 RustDesk 本地配置里，收银端以同一用户运行，不需要提权。
/// RustDesk 界面运行时会把内存中的本地配置整份写回，可能盖掉这次修改，所以每次启动都幂等地写一次。
/// 只处理本机已完成登记（Committed）的受管 RustDesk；全程静默，失败不影响收银。
/// </summary>
public sealed class RustDeskUpdatePromptSuppressor(RemoteMaintenanceJournal journal, string? configFilePath = null)
{
    private readonly string _configFilePath = configFilePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config", RustDeskLocalConfigEditor.FileName);

    /// <summary>返回是否改写了文件；未受管、已是关闭状态或文件结构不认识时返回 false。</summary>
    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var state = await journal.ReadAsync(cancellationToken);
        if (state?.State != RemoteMaintenanceOperationState.Committed) return false;

        var content = File.Exists(_configFilePath) ? await File.ReadAllTextAsync(_configFilePath, cancellationToken) : null;
        var updated = RustDeskLocalConfigEditor.SetOption(content, RustDeskLocalConfigEditor.CheckUpdateOption, "N");
        if (updated is null) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(_configFilePath)!);
        // 临时文件加原子替换：中途断电时 RustDesk 读到的要么是旧文件要么是新文件；不写 BOM，与 RustDesk 自己的写法一致。
        var temporary = _configFilePath + ".hbpos-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, _configFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return true;
    }
}
