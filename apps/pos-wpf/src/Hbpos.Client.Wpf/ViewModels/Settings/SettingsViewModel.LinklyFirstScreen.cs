using System.Collections.Specialized;
using System.ComponentModel;
using Hbpos.Client.Wpf.ViewModels.Settings;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 设置 → 支付终端 → ANZ Linkly「后端异步」模式的 master-detail 状态：
/// 左边线路列表里点选的「当前聚焦线路」。底栏的线路动作（重新配对 / 检测连接 / 使用此线路 / 变更绑定 / 解除绑定）
/// 与右侧详情区都作用于它；点选聚焦线路只改变「正在查看哪一条」，不会触发切换付款线路。
/// 具体规则在 <see cref="LinklyLineFocusTracker"/>（纯逻辑，可脱离整个 VM 单测）。
/// </summary>
public sealed partial class SettingsViewModel
{
    private LinklyLineFocusTracker? _linklyLineFocus;

    /// <summary>
    /// 当前聚焦线路。未显式点选时默认取「当前付款线路」，没有付款线路则取第一条；没有任何线路时为 null。
    /// 切换聚焦线路前先关闭所有线路的配对面板；线路正在配对/检测（忙）时忽略切换。
    /// </summary>
    public LinklyCloudLineItem? FocusedLinklyCloudLine
    {
        get => _linklyLineFocus?.Current;
        set
        {
            if (_linklyLineFocus is null)
            {
                return;
            }

            switch (_linklyLineFocus.TrySetFocus(value))
            {
                case LinklyLineFocusChange.Changed:
                    OnPropertyChanged(nameof(FocusedLinklyCloudLine));
                    break;
                case LinklyLineFocusChange.Rejected:
                    // 忙时不切换；通知一次让列表选中态回到当前聚焦线路。
                    OnPropertyChanged(nameof(FocusedLinklyCloudLine));
                    break;
            }
        }
    }

    /// <summary>构造函数里调用一次：线路列表被重建 / 增删，或某条线路变为「当前付款线路」时刷新聚焦线路。</summary>
    private void InitializeLinklyFocusedLine()
    {
        _linklyLineFocus = new LinklyLineFocusTracker(
            LinklyCloudLines,
            () => IsBusy || _isLinklyLineOperationBusy,
            CloseLinklyLinePairing);
        LinklyCloudLines.CollectionChanged += OnLinklyCloudLinesCollectionChanged;
    }

    private void OnLinklyCloudLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var line in e.NewItems.OfType<LinklyCloudLineItem>())
            {
                line.PropertyChanged += OnLinklyCloudLinePropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (var line in e.OldItems.OfType<LinklyCloudLineItem>())
            {
                line.PropertyChanged -= OnLinklyCloudLinePropertyChanged;
            }
        }

        OnPropertyChanged(nameof(FocusedLinklyCloudLine));
    }

    private void OnLinklyCloudLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LinklyCloudLineItem.IsSelected) &&
            sender is LinklyCloudLineItem { IsSelected: true } line &&
            !ReferenceEquals(line, FocusedLinklyCloudLine))
        {
            // 付款线路改变（例如用旧的「刷卡机」下拉切换）后，详情区跟到新的付款线路。
            FocusedLinklyCloudLine = line;
        }
    }
}
