using Hbpos.Client.Wpf.ViewModels.Settings;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

/// <summary>
/// Linkly 后端异步 master-detail 的「当前聚焦线路」纯逻辑：默认聚焦、按终端 Id 记忆、切换前关闭配对面板、忙时拒绝。
/// 不依赖 WPF，可在 macOS 的 net9 临时宿主里直接跑；SettingsViewModel 层的接线见 SettingsFirstScreenLinklyTests。
/// </summary>
public sealed class LinklyLineFocusTrackerTests
{
    private static LinklyCloudLineItem CreateLine(int lane, bool selected = false, Guid? terminalId = null)
    {
        var terminal = new LinklyCloudTerminalSummary(
            terminalId ?? Guid.NewGuid(), lane, $"Checkout {lane}", "Ready", false, true, null, null, null, lane, $"v-{lane}");
        var line = new LinklyCloudLineItem(
            terminal,
            key => key,
            () => false,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        line.IsSelected = selected;
        return line;
    }

    [Fact]
    public void Empty_list_has_no_focused_line()
    {
        var lines = new List<LinklyCloudLineItem>();
        var tracker = new LinklyLineFocusTracker(lines, () => false, () => { });

        Assert.Null(tracker.Current);
        Assert.Equal(LinklyLineFocusChange.Ignored, tracker.TrySetFocus(null));
    }

    [Fact]
    public void Default_focus_is_the_payment_line_otherwise_the_first_line()
    {
        var lines = new List<LinklyCloudLineItem> { CreateLine(1), CreateLine(2), CreateLine(3) };
        var tracker = new LinklyLineFocusTracker(lines, () => false, () => { });
        Assert.Same(lines[0], tracker.Current);

        lines[1].IsSelected = true;
        Assert.Same(lines[1], tracker.Current);
    }

    [Fact]
    public void Switching_focus_closes_pairing_first_and_does_not_change_the_payment_line()
    {
        var lines = new List<LinklyCloudLineItem> { CreateLine(1, selected: true), CreateLine(2), CreateLine(3) };
        var closeCalls = 0;
        var tracker = new LinklyLineFocusTracker(lines, () => false, () =>
        {
            closeCalls++;
            foreach (var line in lines)
            {
                line.CloseAndClear();
            }
        });
        lines[0].OpenForCodeEntry();
        lines[0].PairCode = "123456";

        Assert.Equal(LinklyLineFocusChange.Changed, tracker.TrySetFocus(lines[2]));

        Assert.Equal(1, closeCalls);
        Assert.Same(lines[2], tracker.Current);
        // 配对面板与输入码都被清掉，不会带到别的线路上。
        Assert.False(lines[0].IsExpanded);
        Assert.Empty(lines[0].PairCode);
        // 只改「正在查看哪一条」，付款线路不变。
        Assert.True(lines[0].IsSelected);
        Assert.False(lines[2].IsSelected);
    }

    [Fact]
    public void Focusing_the_current_line_keeps_its_open_pairing_panel()
    {
        var lines = new List<LinklyCloudLineItem> { CreateLine(1), CreateLine(2) };
        var closeCalls = 0;
        var tracker = new LinklyLineFocusTracker(lines, () => false, () => closeCalls++);
        lines[0].OpenForCodeEntry();
        lines[0].PairCode = "654321";

        Assert.Equal(LinklyLineFocusChange.Unchanged, tracker.TrySetFocus(lines[0]));

        Assert.Equal(0, closeCalls);
        Assert.True(lines[0].IsExpanded);
        Assert.Equal("654321", lines[0].PairCode);
    }

    [Fact]
    public void Busy_line_operation_rejects_the_switch_and_keeps_the_focus()
    {
        var lines = new List<LinklyCloudLineItem> { CreateLine(1), CreateLine(2) };
        var busy = true;
        var closeCalls = 0;
        var tracker = new LinklyLineFocusTracker(lines, () => busy, () => closeCalls++);

        Assert.Equal(LinklyLineFocusChange.Rejected, tracker.TrySetFocus(lines[1]));
        Assert.Equal(0, closeCalls);
        Assert.Same(lines[0], tracker.Current);

        busy = false;
        Assert.Equal(LinklyLineFocusChange.Changed, tracker.TrySetFocus(lines[1]));
        Assert.Same(lines[1], tracker.Current);
    }

    [Fact]
    public void Foreign_or_null_lines_are_ignored()
    {
        var lines = new List<LinklyCloudLineItem> { CreateLine(1), CreateLine(2) };
        var tracker = new LinklyLineFocusTracker(lines, () => false, () => { });

        Assert.Equal(LinklyLineFocusChange.Ignored, tracker.TrySetFocus(null));
        Assert.Equal(LinklyLineFocusChange.Ignored, tracker.TrySetFocus(CreateLine(9)));
        Assert.Same(lines[0], tracker.Current);
    }

    [Fact]
    public void Focus_follows_the_terminal_id_when_the_directory_is_rebuilt()
    {
        var focusedId = Guid.NewGuid();
        var lines = new List<LinklyCloudLineItem> { CreateLine(1), CreateLine(2, terminalId: focusedId), CreateLine(3) };
        var tracker = new LinklyLineFocusTracker(lines, () => false, () => { });
        Assert.Equal(LinklyLineFocusChange.Changed, tracker.TrySetFocus(lines[1]));

        // 目录刷新：整体重建为新对象（同一终端 Id 位置变了）。
        var rebuilt = new[] { CreateLine(3), CreateLine(1), CreateLine(2, terminalId: focusedId) };
        lines.Clear();
        lines.AddRange(rebuilt);

        Assert.Same(rebuilt[2], tracker.Current);

        // 聚焦线路从目录里消失后，回到默认（付款线路 / 第一条）。
        lines.Remove(rebuilt[2]);
        Assert.Same(rebuilt[0], tracker.Current);
    }
}
