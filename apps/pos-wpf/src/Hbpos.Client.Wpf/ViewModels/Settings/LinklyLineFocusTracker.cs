namespace Hbpos.Client.Wpf.ViewModels.Settings;

/// <summary>聚焦线路切换请求的结果。</summary>
internal enum LinklyLineFocusChange
{
    /// <summary>空值或不在当前列表里的线路，忽略（列表被重建时 ListBox 会回写 null）。</summary>
    Ignored,

    /// <summary>请求的线路本来就是当前聚焦线路：只把「默认聚焦」固化成显式聚焦，不动配对面板状态。</summary>
    Unchanged,

    /// <summary>线路正在配对 / 检测（忙），拒绝切换，避免清掉进行中的操作状态。</summary>
    Rejected,

    /// <summary>已切换（切换前已关闭所有线路的配对面板）。</summary>
    Changed
}

/// <summary>
/// 「当前聚焦线路」的纯逻辑（从 SettingsViewModel 拆出，便于脱离整个 VM 单测）：
/// - 按终端 Id 记忆聚焦线路：目录刷新会整体重建线路对象，按 Id 才能继续指向同一条线路；
/// - 未显式点选时默认取「当前付款线路」，没有则取第一条，没有线路时为 null；
/// - 切换聚焦线路前必须先关闭配对面板（配对码输入 / 确认只属于被展开的那一条，不清掉会在别的线路上展示上一条的配对码）。
/// </summary>
internal sealed class LinklyLineFocusTracker(
    IReadOnlyList<LinklyCloudLineItem> lines,
    Func<bool> isBusy,
    Action closePairing)
{
    private Guid? _focusedTerminalId;

    /// <summary>当前聚焦线路；没有任何线路时为 null。</summary>
    public LinklyCloudLineItem? Current
    {
        get
        {
            if (lines.Count == 0)
            {
                return null;
            }

            if (_focusedTerminalId is Guid focusedId)
            {
                foreach (var line in lines)
                {
                    if (line.Terminal.TerminalId == focusedId)
                    {
                        return line;
                    }
                }
            }

            foreach (var line in lines)
            {
                if (line.IsSelected)
                {
                    return line;
                }
            }

            return lines[0];
        }
    }

    /// <summary>请求把聚焦线路切到 <paramref name="line"/>。</summary>
    public LinklyLineFocusChange TrySetFocus(LinklyCloudLineItem? line)
    {
        if (line is null || !Contains(line))
        {
            return LinklyLineFocusChange.Ignored;
        }

        if (ReferenceEquals(Current, line))
        {
            _focusedTerminalId = line.Terminal.TerminalId;
            return LinklyLineFocusChange.Unchanged;
        }

        if (isBusy())
        {
            return LinklyLineFocusChange.Rejected;
        }

        closePairing();
        _focusedTerminalId = line.Terminal.TerminalId;
        return LinklyLineFocusChange.Changed;
    }

    private bool Contains(LinklyCloudLineItem line)
    {
        foreach (var candidate in lines)
        {
            if (ReferenceEquals(candidate, line))
            {
                return true;
            }
        }

        return false;
    }
}
