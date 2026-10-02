using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

public sealed class DailyCloseViewLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void History_and_linkly_are_the_only_tabs_and_history_toolbar_creates_or_resumes_drafts()
    {
        var view = LoadView();
        var tabControl = Assert.Single(view.Descendants(Presentation + "TabControl"));
        var tabs = tabControl.Elements(Presentation + "TabItem").ToArray();

        Assert.Equal(2, tabs.Length);
        Assert.Equal("{loc:Loc dailyClose.tab.history}", (string?)tabs[0].Attribute("Header"));
        Assert.Equal("{loc:Loc dailyClose.linklySettlement.tab}", (string?)tabs[1].Attribute("Header"));
        Assert.Empty(tabControl.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") == "CashCountPanel"));

        var datePicker = Assert.Single(view.Descendants(Presentation + "DatePicker"));
        Assert.Equal("{Binding CanChangeBusinessDate}", (string?)datePicker.Attribute("IsEnabled"));

        var toolbarTemplate = Assert.Single(view.Descendants(Presentation + "ControlTemplate").Where(template =>
            template.Descendants(Presentation + "ContentPresenter").Any(presenter =>
                (string?)presenter.Attribute(Xaml + "Name") == "PART_SelectedContentHost")));
        // 顶栏只保留返回、营业日期、页签和主操作；刷新历史收进历史列表卡片，避免两个同功能按钮。
        Assert.Empty(toolbarTemplate.Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("LoadHistoryCommand", StringComparison.Ordinal) == true));
        var historyRefresh = Assert.Single(tabs[0].Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("LoadHistoryCommand", StringComparison.Ordinal) == true));
        Assert.Equal("DailyCloseRefreshHistoryButton", (string?)historyRefresh.Attribute(Xaml + "Name"));
        Assert.Contains("dailyClose.refreshHistory", historyRefresh.ToString(SaveOptions.DisableFormatting));

        // 营业日期旁的“今天”标签只在日期等于今天时显示。
        var todayChipStyle = Assert.Single(view.Descendants(Presentation + "Style").Where(style =>
            (string?)style.Attribute(Xaml + "Key") == "TodayChipStyle"));
        Assert.Contains(todayChipStyle.Descendants(Presentation + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsBusinessDateToday}" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains("dailyClose.today", toolbarTemplate.ToString(SaveOptions.DisableFormatting));

        var createOrResumeButtons = toolbarTemplate.Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("CreateOrResumeDailyCloseCommand", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, createOrResumeButtons.Length);
        Assert.Contains(createOrResumeButtons, button =>
            button.ToString(SaveOptions.DisableFormatting).Contains("dailyClose.createNew", StringComparison.Ordinal) &&
            button.Descendants(Presentation + "Condition").Any(condition =>
                (string?)condition.Attribute("Binding") == "{Binding HasDailyCloseDraft}" &&
                (string?)condition.Attribute("Value") == "False"));
        Assert.Contains(createOrResumeButtons, button =>
            button.ToString(SaveOptions.DisableFormatting).Contains("dailyClose.continueDraft", StringComparison.Ordinal) &&
            button.Descendants(Presentation + "Condition").Any(condition =>
                (string?)condition.Attribute("Binding") == "{Binding HasDailyCloseDraft}" &&
                (string?)condition.Attribute("Value") == "True"));
        Assert.All(createOrResumeButtons, button => Assert.Contains(
            button.Descendants(Presentation + "Condition"),
            condition => (string?)condition.Attribute("Binding") == "{Binding IsHistoryTabSelected}"));
        Assert.Empty(toolbarTemplate.Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("SaveAndPrintCommand", StringComparison.Ordinal) == true));

        Assert.Contains(tabs[1].Descendants(Presentation + "Button"), button =>
            ((string?)button.Attribute("Command"))?.Contains("LoadSettlementHistoryCommand", StringComparison.Ordinal) == true);
        Assert.Contains(tabs[1].Descendants(Presentation + "Button"), button =>
            ((string?)button.Attribute("Command"))?.Contains("SettleAndPrintCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Daily_close_toolbar_removes_text_above_tabs_and_keeps_remaining_controls_on_one_row()
    {
        var view = LoadView();
        var toolbar = FindNamedElement(view, "DailyCloseToolbar");
        var toolbarLayout = FindNamedElement(view, "DailyCloseToolbarLayout");

        Assert.Contains(toolbarLayout, toolbar.Descendants());
        Assert.Empty(toolbarLayout.Elements(Presentation + "Grid.RowDefinitions"));
        Assert.DoesNotContain("dailyClose.title", toolbar.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding StatusMessage}", toolbar.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);

        foreach (var elementName in new[]
                 {
                     "DailyCloseNavigationTabsBorder",
                     "DailyCloseRefreshHistoryButton",
                     "DailyCloseCreateDraftButton",
                     "DailyCloseContinueDraftButton"
                 })
        {
            var element = FindNamedElement(view, elementName);
            Assert.Null(element.Attribute("Grid.Row"));
        }
    }

    [Fact]
    public void Daily_close_status_is_visible_below_the_tabs_and_inside_the_cash_workspace()
    {
        var view = LoadView();
        var statusBar = FindNamedElement(view, "DailyCloseStatusBar");
        var tabControl = FindNamedElement(view, "DailyCloseTabControl");
        var workspace = FindNamedElement(view, "DailyCloseCashWorkspaceOverlay");
        var discard = FindNamedElement(view, "DailyCloseDiscardDraftOverlay");

        // 状态栏独立于顶栏，位于页签内容下方；保存、打印、结算的提示都要让收银员看得到。
        Assert.Equal("1", (string?)statusBar.Attribute("Grid.Row"));
        Assert.Null(tabControl.Attribute("Grid.Row"));
        Assert.Equal("{Binding StatusMessage}", (string?)FindNamedElement(view, "DailyCloseStatusText").Attribute("Text"));
        Assert.Equal(
            "{Binding StatusMessage, Converter={StaticResource StringHasValueToVis}}",
            (string?)statusBar.Attribute("Visibility"));

        // 工作区覆盖整页（含状态栏行），其底栏同样显示状态。
        Assert.Equal("2", (string?)workspace.Attribute("Grid.RowSpan"));
        Assert.Equal("2", (string?)discard.Attribute("Grid.RowSpan"));
        var workspaceStatus = FindNamedElement(view, "DailyCloseWorkspaceStatusText");
        Assert.Contains(workspaceStatus, workspace.Descendants());
        Assert.Equal("{Binding StatusMessage}", (string?)workspaceStatus.Attribute("Text"));
    }

    [Fact]
    public void Daily_close_tabs_keep_spacing_inside_the_template_so_both_outer_corners_render()
    {
        var view = LoadView();
        var tabItemStyle = Assert.Single(view.Descendants(Presentation + "Style").Where(style =>
            (string?)style.Attribute(Xaml + "Key") == "DailyCloseTabItemStyle"));

        Assert.DoesNotContain(tabItemStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Margin");

        var template = Assert.Single(tabItemStyle.Descendants(Presentation + "ControlTemplate"));
        var root = Assert.Single(template.Descendants(Presentation + "Border").Where(border =>
            (string?)border.Attribute(Xaml + "Name") == "Root"));
        Assert.Equal("2,0", (string?)root.Attribute("Margin"));
        Assert.Equal("6", (string?)root.Attribute("CornerRadius"));
    }

    [Fact]
    public void Cash_count_panel_uses_denomination_buttons_without_inline_keypad_or_apply_buttons()
    {
        var view = LoadView();
        var cashPanel = FindNamedElement(view, "CashCountPanel");
        var workspace = FindNamedElement(view, "DailyCloseCashWorkspaceOverlay");

        Assert.Contains(cashPanel, workspace.Descendants());
        Assert.NotNull(FindNamedElement(view, "NoteDenominationList"));
        Assert.NotNull(FindNamedElement(view, "CoinDenominationList"));
        Assert.Empty(cashPanel.Descendants(Presentation + "DataGrid"));
        Assert.Empty(cashPanel.Descendants(Presentation + "UniformGrid"));

        var itemTemplate = Assert.Single(view.Descendants(Presentation + "DataTemplate").Where(element =>
            (string?)element.Attribute(Xaml + "Key") == "CashDenominationItemTemplate"));
        var denominationButton = Assert.Single(itemTemplate.Descendants(Presentation + "Button"));
        Assert.Contains("OpenCashCountDialogCommand", (string?)denominationButton.Attribute("Command"));
        Assert.Equal("{Binding}", (string?)denominationButton.Attribute("CommandParameter"));
        Assert.DoesNotContain("ApplyDenominationCommand", itemTemplate.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Cash_workspace_keypad_and_discard_confirmation_use_ordered_modal_layers_and_focus_scopes()
    {
        var view = LoadView();
        var workspace = FindNamedElement(view, "DailyCloseCashWorkspaceOverlay");
        var keypad = FindNamedElement(view, "CashCountKeypadPanel");
        var discard = FindNamedElement(view, "DailyCloseDiscardDraftOverlay");

        AssertOverlayContract(
            workspace,
            zIndex: "100",
            visibility: "{Binding IsCashCountWorkspaceOpen, Converter={StaticResource BoolToVis}}",
            visibleChanged: "DailyCloseCashWorkspaceOverlayIsVisibleChanged",
            previewKeyDown: "DailyCloseCashWorkspaceOverlayPreviewKeyDown");
        var workspaceSurface = Assert.Single(workspace.Elements(Presentation + "Border"));
        Assert.Equal("16", (string?)workspaceSurface.Attribute("Margin"));
        Assert.Equal("1600", (string?)workspaceSurface.Attribute("MaxWidth"));
        Assert.NotNull(FindNamedElement(view, "DailyCloseCashWorkspaceReturnButton"));
        Assert.Single(workspace.Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("SaveAndPrintCommand", StringComparison.Ordinal) == true));

        // 数字键盘常驻在工作区第二栏，不再是覆盖整屏的二层弹窗。
        Assert.Contains(keypad, FindNamedElement(view, "DailyCloseCashWorkspaceBody").Descendants());
        Assert.Null(keypad.Attribute("Panel.ZIndex"));
        Assert.Empty(view.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") is "CashCountDialogOverlay" or "CashCountDialogCancelButton"));

        AssertOverlayContract(
            discard,
            zIndex: "300",
            visibility: "{Binding IsDiscardDailyCloseDraftConfirmationOpen, Converter={StaticResource BoolToVis}}",
            visibleChanged: "DailyCloseDiscardDraftOverlayIsVisibleChanged",
            previewKeyDown: "DailyCloseDiscardDraftOverlayPreviewKeyDown");
        Assert.NotNull(FindNamedElement(view, "DailyCloseDiscardDraftCancelButton"));
    }

    [Fact]
    public void Cash_count_keypad_uses_strict_nine_grid_and_separate_zero_clear_backspace_row()
    {
        var view = LoadView();
        var overlay = FindNamedElement(view, "CashCountKeypadPanel");
        Assert.Equal("DailyCloseCashCountKeypad", (string?)overlay.Attribute("AutomationProperties.AutomationId"));

        var nineGrid = FindNamedElement(view, "CashCountNineGrid");
        Assert.Equal("3", (string?)nineGrid.Attribute("Rows"));
        Assert.Equal("3", (string?)nineGrid.Attribute("Columns"));
        var digitButtons = nineGrid.Elements(Presentation + "Button").ToArray();
        Assert.Equal(9, digitButtons.Length);
        Assert.Equal(
            Enumerable.Range(1, 9).Select(value => value.ToString()).ToArray(),
            digitButtons.Select(button => (string?)button.Attribute("CommandParameter")).ToArray());
        Assert.All(digitButtons, button => Assert.Contains("KeypadInputCommand", (string?)button.Attribute("Command")));

        var utilityRow = FindNamedElement(view, "CashCountUtilityRow");
        Assert.Equal("1", (string?)utilityRow.Attribute("Rows"));
        Assert.Equal("3", (string?)utilityRow.Attribute("Columns"));
        var utilityButtons = utilityRow.Elements(Presentation + "Button").ToArray();
        Assert.Equal(3, utilityButtons.Length);
        Assert.Contains("KeypadClearCommand", (string?)utilityButtons[0].Attribute("Command"));
        Assert.Equal("0", (string?)utilityButtons[1].Attribute("CommandParameter"));
        Assert.Contains("KeypadBackspaceCommand", (string?)utilityButtons[2].Attribute("Command"));

        var applyButton = Assert.Single(overlay.Descendants(Presentation + "Button").Where(button =>
            ((string?)button.Attribute("Command"))?.Contains("ApplyDenominationCommand", StringComparison.Ordinal) == true));
        Assert.Equal("{Binding SelectedCashDenomination}", (string?)applyButton.Attribute("CommandParameter"));
        Assert.Contains("dailyClose.cashCountDialog.applyNext", applyButton.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Daily_close_run_bindings_are_one_way_for_read_only_display_values()
    {
        var view = LoadView();
        var keypad = FindNamedElement(view, "CashCountKeypadPanel");

        Assert.Equal(
            [
                "{Binding SelectedCashDenominationPosition, Mode=OneWay}",
                "{Binding DenominationCount, Mode=OneWay}",
                "{Binding CashCountDialogQuantity, Mode=OneWay}",
                "{Binding SelectedCashDenomination.Label, Mode=OneWay}",
                "{Binding CashCountDialogSubtotal, Mode=OneWay, StringFormat={}{0:C2}}",
            ],
            keypad.Descendants(Presentation + "Run")
                .Select(run => (string?)run.Attribute("Text"))
                .Where(text => text?.StartsWith("{Binding ", StringComparison.Ordinal) == true)
                .ToArray());

        // Run.Text 默认双向绑定，绑定到只读属性会在运行时抛异常；整页所有 Run 绑定都必须声明 OneWay。
        Assert.All(
            view.Descendants(Presentation + "Run")
                .Select(run => (string?)run.Attribute("Text"))
                .Where(text => text?.StartsWith("{Binding ", StringComparison.Ordinal) == true),
            text => Assert.Contains("Mode=OneWay", text, StringComparison.Ordinal));

        var workspace = FindNamedElement(view, "DailyCloseCashWorkspaceOverlay");
        Assert.Contains(
            "{Binding BusinessDateText, Mode=OneWay}",
            workspace.Descendants(Presentation + "Run").Select(run => (string?)run.Attribute("Text")));
    }

    [Fact]
    public void Daily_close_workspace_and_cash_dialog_text_is_available_in_english_and_chinese_resources()
    {
        var repoRoot = FindRepoRoot();
        var keys = new[]
        {
            "dailyClose.cashCountHint",
            "dailyClose.quantityShort",
            "dailyClose.cashCountDialog.automationName",
            "dailyClose.cashCountDialog.titlePrefix",
            "dailyClose.cashCountDialog.helperPrefix",
            "dailyClose.cashCountDialog.helperSuffix",
            "dailyClose.cashCountDialog.clear",
            "dailyClose.cashCountDialog.backspace",
            "dailyClose.cashCountDialog.apply",
            "dailyClose.refreshHistory",
            "dailyClose.createNew",
            "dailyClose.continueDraft",
            "dailyClose.cashWorkspace.title",
            "dailyClose.cashWorkspace.automationName",
            "dailyClose.cashWorkspace.returnHistory",
            "dailyClose.cashWorkspace.discard",
            "dailyClose.cashWorkspace.discardConfirm.title",
            "dailyClose.cashWorkspace.discardConfirm.message",
            "dailyClose.cashWorkspace.discardConfirm.action",
            "dailyClose.status.draftPreparing",
            "dailyClose.status.draftReady",
            "dailyClose.status.draftResumed",
            "dailyClose.status.draftPreserved",
            "dailyClose.status.draftDiscarded",
            "dailyClose.status.draftIdentityChanged",
            "dailyClose.today",
            "dailyClose.history.newestFirst",
            "dailyClose.history.selectHint",
            "dailyClose.archive.titlePrefix",
            "dailyClose.archive.savedAt",
            "dailyClose.archive.id",
            "dailyClose.difference.short",
            "dailyClose.difference.over",
            "dailyClose.difference.balanced",
            "dailyClose.keypad.counting",
            "dailyClose.keypad.idle",
            "dailyClose.cashCountDialog.applyNext",
            "dailyClose.reconciliation",
            "dailyClose.cashWorkspace.returnHistoryHint",
            "dailyClose.linklySettlement.status.pending",
            "dailyClose.linklySettlement.status.succeeded",
            "dailyClose.linklySettlement.status.failed",
            "dailyClose.linklySettlement.status.unknown",
            "dailyClose.linklySettlement.mode.localIp",
            "dailyClose.linklySettlement.mode.cloudDirectSync",
            "dailyClose.linklySettlement.mode.cloudBackendAsync",
            "dailyClose.linklySettlement.submission.notSubmitted",
            "dailyClose.linklySettlement.submission.submitted",
            "dailyClose.linklySettlement.submission.unknown",
            "dailyClose.linklySettlement.noReceiptPreview",
            "dailyClose.linklySettlement.notToday"
        };

        foreach (var resourceName in new[] { "Strings.resx", "Strings.zh-CN.resx" })
        {
            var resource = XDocument.Load(Path.Combine(
                repoRoot,
                "apps",
                "pos-wpf",
                "src",
                "Hbpos.Client.Wpf",
                "Resources",
                resourceName));
            var resourceKeys = resource.Descendants("data")
                .Select(element => (string?)element.Attribute("name"))
                .Where(key => key is not null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.All(keys, key => Assert.Contains(key, resourceKeys));
        }
    }

    private static XDocument LoadView() => XDocument.Load(Path.Combine(
        FindRepoRoot(),
        "apps",
        "pos-wpf",
        "src",
        "Hbpos.Client.Wpf",
        "Views",
        "Screens",
        "DailyCloseView.xaml"));

    private static XElement FindNamedElement(XDocument document, string name) =>
        Assert.Single(document.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") == name));

    private static void AssertOverlayContract(
        XElement overlay,
        string zIndex,
        string visibility,
        string visibleChanged,
        string previewKeyDown)
    {
        Assert.Equal(zIndex, (string?)overlay.Attribute("Panel.ZIndex"));
        Assert.Equal(visibility, (string?)overlay.Attribute("Visibility"));
        Assert.Equal(visibleChanged, (string?)overlay.Attribute("IsVisibleChanged"));
        Assert.Equal(previewKeyDown, (string?)overlay.Attribute("PreviewKeyDown"));
        Assert.Equal("True", (string?)overlay.Attribute("FocusManager.IsFocusScope"));
        Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Equal("None", (string?)overlay.Attribute("KeyboardNavigation.ControlTabNavigation"));
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        foreach (var start in new[]
                 {
                     Path.GetDirectoryName(sourceFilePath),
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
