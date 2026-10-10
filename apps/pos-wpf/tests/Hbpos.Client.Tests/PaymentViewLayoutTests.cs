using System.Xml.Linq;

namespace Hbpos.Client.Tests;

public sealed class PaymentViewLayoutTests
{
    [Fact]
    public void Installment_toggle_is_compact_accessible_and_in_payment_method_area()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PaymentView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var elements = document.Descendants().ToArray();
        var backButton = Assert.Single(elements.Where(element =>
            element.Name == presentation + "Button" &&
            (string?)element.Attribute("Command") == "{Binding BackToPosCommand}"));
        var installmentToggle = Assert.Single(elements.Where(element =>
            element.Name == presentation + "ToggleButton" &&
            (string?)element.Attribute("AutomationProperties.AutomationId") == "InstallmentPaymentToggle"));
        var orderSummary = Assert.Single(elements.Where(element =>
            element.Name == presentation + "TextBlock" &&
            (string?)element.Attribute("Text") == "{Binding OrderSummaryText}"));

        // 分期开关位于右侧「支付方式」2×2 方块网格的第 2 行右格（与其他方块等高），返回按钮独占左上角整行。
        Assert.Equal("PaymentMethodTiles", (string?)installmentToggle.Parent?.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("1", (string?)installmentToggle.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)installmentToggle.Attribute("Grid.Column"));
        Assert.NotSame(backButton.Parent, installmentToggle.Parent);
        Assert.Equal("2", (string?)backButton.Attribute("Grid.ColumnSpan"));
        Assert.Equal("1", (string?)orderSummary.Parent?.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)orderSummary.Parent?.Attribute("Grid.ColumnSpan"));

        Assert.Equal(
            "{Binding IsInstallmentPaymentEnabled, Mode=TwoWay}",
            (string?)installmentToggle.Attribute("IsChecked"));
        Assert.Single(elements.SelectMany(element => element.Attributes()).Where(attribute =>
            attribute.Value == "{Binding IsInstallmentPaymentEnabled, Mode=TwoWay}"));
        Assert.Equal(
            "{Binding InstallmentMethodText}",
            (string?)installmentToggle.Attribute("AutomationProperties.Name"));
        Assert.Equal(
            "{loc:Loc payment.installment.switchHelp}",
            (string?)installmentToggle.Attribute("ToolTip"));
        Assert.Equal("True", (string?)installmentToggle.Attribute("ToolTipService.ShowOnDisabled"));

        var localStyle = Assert.Single(installmentToggle
            .Elements(presentation + "ToggleButton.Style")
            .Elements(presentation + "Style"));
        Assert.Contains(localStyle.Descendants(presentation + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsInstallmentEntryVisible}" &&
            (string?)trigger.Attribute("Value") == "False" &&
            HasSetter(trigger, "Visibility", "Collapsed"));
        Assert.Contains(localStyle.Descendants(presentation + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsInstallmentSwitchLocked}" &&
            (string?)trigger.Attribute("Value") == "True" &&
            HasSetter(trigger, "IsEnabled", "False"));

        var content = Assert.Single(installmentToggle.Elements(presentation + "Grid"));
        Assert.Empty(content.Descendants().Where(element => element.Name.LocalName == "PackIcon"));
        Assert.Single(content.Descendants(presentation + "TextBlock").Where(text =>
            (string?)text.Attribute("Text") == "{Binding InstallmentMethodText}"));
        Assert.DoesNotContain(content.Descendants(presentation + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{loc:Loc payment.installment.switchHelp}");

        var sharedStyle = Assert.Single(elements.Where(element =>
            element.Name == presentation + "Style" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" &&
                attribute.Value == "InstallmentMethodToggleStyle")));
        Assert.Contains(sharedStyle.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(sharedStyle.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsChecked" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(sharedStyle.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsEnabled" &&
            (string?)trigger.Attribute("Value") == "False");
    }

    [Fact]
    public void Payment_cart_supports_touch_panning_with_touch_sized_scrollbar()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PaymentView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var cartGrid = Assert.Single(document.Descendants(presentation + "DataGrid").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "PaymentCartItemsGrid"));

        Assert.Equal("Pixel", (string?)cartGrid.Attribute("VirtualizingPanel.ScrollUnit"));
        Assert.Equal("True", (string?)cartGrid.Attribute("ScrollViewer.CanContentScroll"));
        Assert.Equal("Disabled", (string?)cartGrid.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("Visible", (string?)cartGrid.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        Assert.Equal("VerticalOnly", (string?)cartGrid.Attribute("ScrollViewer.PanningMode"));
        Assert.Equal("0.0008", (string?)cartGrid.Attribute("ScrollViewer.PanningDeceleration"));
        Assert.Equal("1.0", (string?)cartGrid.Attribute("ScrollViewer.PanningRatio"));

        var touchScrollBarStyle = Assert.Single(document.Descendants(presentation + "Style").Where(style =>
            (string?)style.Attribute(x + "Key") == "PaymentCartTouchScrollBarStyle"));
        Assert.True(HasSetter(touchScrollBarStyle, "Width", "22"));
        Assert.True(HasSetter(touchScrollBarStyle, "MinWidth", "22"));

        var dataGridResources = Assert.Single(cartGrid.Elements(presentation + "DataGrid.Resources"));
        var scrollBarStyle = Assert.Single(dataGridResources.Elements(presentation + "Style").Where(style =>
            (string?)style.Attribute("TargetType") == "{x:Type ScrollBar}"));
        Assert.Equal(
            "{StaticResource PaymentCartTouchScrollBarStyle}",
            (string?)scrollBarStyle.Attribute("BasedOn"));
        var thumbStyle = Assert.Single(dataGridResources.Elements(presentation + "Style").Where(style =>
            (string?)style.Attribute("TargetType") == "{x:Type Thumb}"));
        Assert.True(HasSetter(thumbStyle, "MinHeight", "52"));
    }

    [Fact]
    public void Unknown_card_result_keeps_recovery_action_visible_after_dialog_closes()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PaymentView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var recoveryButton = Assert.Single(document.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RecoverPreviousCardTransactionButton"));
        Assert.Equal(
            "{Binding CardPaymentErrorPrimaryActionCommand}",
            (string?)recoveryButton.Attribute("Command"));

        var trigger = Assert.Single(recoveryButton.Descendants(presentation + "MultiDataTrigger"));
        var conditions = trigger.Descendants(presentation + "Condition").ToArray();
        Assert.Contains(conditions, condition =>
            (string?)condition.Attribute("Binding") == "{Binding IsPaymentInteractionLocked}" &&
            (string?)condition.Attribute("Value") == "True");
        Assert.Contains(conditions, condition =>
            (string?)condition.Attribute("Binding") == "{Binding CardPaymentErrorOverlay.PrimaryActionKind}" &&
            (string?)condition.Attribute("Value") ==
            "{x:Static vm:CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious}");
        Assert.Contains(conditions, condition =>
            (string?)condition.Attribute("Binding") == "{Binding CardPaymentErrorOverlay.HasPrimaryAction}" &&
            (string?)condition.Attribute("Value") == "True");
        Assert.True(HasSetter(trigger, "Visibility", "Visible"));

        var safeExitButton = Assert.Single(document.Descendants(presentation + "Button").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "OpenCardRecoveryCenterFromCardErrorButton"));
        Assert.Equal(
            "{Binding OpenCardRecoveryCenterCommand}",
            (string?)safeExitButton.Attribute("Command"));
        var safeExitTrigger = Assert.Single(safeExitButton.Descendants(presentation + "MultiDataTrigger"));
        var safeExitConditions = safeExitTrigger.Descendants(presentation + "Condition").ToArray();
        Assert.Contains(safeExitConditions, condition =>
            (string?)condition.Attribute("Binding") == "{Binding IsCardPaymentRecoveryRequired}" &&
            (string?)condition.Attribute("Value") == "True");
        Assert.Contains(safeExitConditions, condition =>
            (string?)condition.Attribute("Binding") == "{Binding IsPaymentInteractionLocked}" &&
            (string?)condition.Attribute("Value") == "True");
        Assert.True(HasSetter(safeExitTrigger, "Visibility", "Visible"));
    }

    private static bool HasSetter(XElement trigger, string property, string value)
    {
        return trigger.Elements().Any(element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == property &&
            (string?)element.Attribute("Value") == value);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
