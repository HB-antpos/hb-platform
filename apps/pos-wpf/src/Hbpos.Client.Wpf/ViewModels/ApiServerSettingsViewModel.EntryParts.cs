using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 入口状态的拆分展示（设置页「设备注册」首屏用）：
/// 把原来一整句的 <see cref="EntryStatusText"/> 拆成「总部设置的首选入口」「当前实际入口」两个值和一个「回退中」标记，
/// 界面据此排成两行加回退芯片。<see cref="EntryStatusText"/> 与 <see cref="HasEntryStatus"/> 的语义不变，登录页仍用它。
/// </summary>
public sealed partial class ApiServerSettingsViewModel
{
    // 总部后台为本分店设置的首选入口（EdgeOne / AWS 悉尼 / 直连）的显示名；无入口状态时为空。
    [ObservableProperty]
    private string _preferredEntryText = string.Empty;

    // 当前实际在用的入口显示名；首选入口连不上时与首选不同。
    [ObservableProperty]
    private string _currentEntryText = string.Empty;

    // 是否已从首选入口回退到备用入口（界面显示「回退中」警告芯片并把整块改成警告色）。
    [ObservableProperty]
    private bool _isEntryFallback;

    /// <summary>
    /// 与 <see cref="BuildEntryStatusText"/> 同一份数据来源的拆分版本；
    /// 当前地址不在入口组内、没有选择器或解析失败时全部清空，<see cref="HasEntryStatus"/> 随之为假。
    /// </summary>
    private void RefreshEntryParts()
    {
        var preferred = string.Empty;
        var current = string.Empty;
        var fallback = false;

        if (_transportSelector is not null)
        {
            try
            {
                var status = _transportSelector.GetStatus(
                    new Uri(_settingsService.GetCurrentAddress(), UriKind.Absolute));
                if (status.ActiveMode is not null)
                {
                    preferred = _localization.T($"settings.serverAddress.entry.{status.PreferredMode}");
                    current = _localization.T($"settings.serverAddress.entry.{status.ActiveMode}");
                    fallback = status.UsingFallback;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                // 展示用的附加信息，任何异常都不能影响设置页。
            }
        }

        PreferredEntryText = preferred;
        CurrentEntryText = current;
        IsEntryFallback = fallback;
    }
}
