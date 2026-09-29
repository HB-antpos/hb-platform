using System.Xml.Linq;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.Advertisements;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayViewModelTests
{
    [Fact]
    public void LoadLines_calculates_item_quantity_and_sku_count()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [
                new CustomerDisplayLine("Milk", "SKU-001", 2m, 3m, 6m),
                new CustomerDisplayLine("Bread", "SKU-002", 1.5m, 4m, 6m)
            ],
            subtotal: 35.94m,
            savingsAmount: 3.96m);

        Assert.Equal(3.5m, viewModel.TotalItemQuantity);
        Assert.Equal(2, viewModel.SkuCount);
        Assert.Equal(31.98m, viewModel.TotalToPay);
        Assert.Equal(2.91m, viewModel.TaxAmount);
    }

    [Fact]
    public void LoadLines_rounds_and_applies_normal_positive_savings()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [new CustomerDisplayLine("Sale", "SKU-SALE", 1m, 50m, 50m)],
            subtotal: 50m,
            savingsAmount: 10.005m);

        AssertPaymentSummary(viewModel, 10.01m, 39.99m, 3.64m, isReadyForPayment: true);
    }

    [Fact]
    public void LoadLines_normalizes_zero_negative_and_sub_cent_savings_to_zero()
    {
        foreach (var savingsAmount in new[] { 0m, -2m, 0.001m })
        {
            var viewModel = new CustomerDisplayViewModel();

            viewModel.LoadLines(
                [new CustomerDisplayLine("Sale", "SKU-SALE", 1m, 11m, 11m)],
                subtotal: 11m,
                savingsAmount);

            AssertPaymentSummary(viewModel, 0m, 11m, 1m, isReadyForPayment: true);
        }
    }

    [Fact]
    public void LoadLines_caps_savings_above_original_price()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [new CustomerDisplayLine("Sale", "SKU-SALE", 1m, 25.50m, 25.50m)],
            subtotal: 25.50m,
            savingsAmount: 99m);

        AssertPaymentSummary(viewModel, 25.50m, 0m, 0m, isReadyForPayment: false);
    }

    [Fact]
    public void LoadLines_accepts_one_hundred_percent_savings()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [
                new CustomerDisplayLine("Sale one", "SKU-ONE", 1m, 10m, 10m),
                new CustomerDisplayLine("Sale two", "SKU-TWO", 1m, 5m, 5m)
            ],
            subtotal: 15m,
            savingsAmount: 15m);

        AssertPaymentSummary(viewModel, 15m, 0m, 0m, isReadyForPayment: false);
    }

    [Fact]
    public void LoadLines_disallows_savings_for_returns_only()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [new CustomerDisplayLine("Return", "SKU-RETURN", 1m, 11m, -11m)],
            subtotal: -11m,
            savingsAmount: 5m);

        AssertPaymentSummary(viewModel, 0m, -11m, -1m, isReadyForPayment: false);
    }

    [Fact]
    public void LoadLines_caps_mixed_cart_savings_using_positive_gross_amounts_only()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [
                new CustomerDisplayLine("Sale", "SKU-SALE", 1m, 100m, 100m),
                new CustomerDisplayLine("Return", "SKU-RETURN", 1m, 40m, -40m)
            ],
            subtotal: 60m,
            savingsAmount: 150m);

        AssertPaymentSummary(viewModel, 100m, -40m, -3.64m, isReadyForPayment: false);
    }

    [Fact]
    public void LoadLines_notifies_HasSavings_when_positive_savings_refresh_to_zero()
    {
        var viewModel = new CustomerDisplayViewModel();
        var lines = new[] { new CustomerDisplayLine("Sale", "SKU-SALE", 1m, 11m, 11m) };
        viewModel.LoadLines(lines, subtotal: 11m, savingsAmount: 1m);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.LoadLines(lines, subtotal: 11m, savingsAmount: 0m);

        Assert.False(viewModel.HasSavings);
        Assert.Contains(nameof(CustomerDisplayViewModel.HasSavings), changedProperties);
    }

    [Fact]
    public void CustomerDisplayLine_exposes_item_number_presence()
    {
        var populated = new CustomerDisplayLine("Milk", "SKU-001", 1m, 3m, 3m)
        {
            ItemNumber = "ITEM-001"
        };
        var missing = new CustomerDisplayLine("Bread", "SKU-002", 1m, 4m, 4m);
        var whitespace = new CustomerDisplayLine("Eggs", "SKU-003", 1m, 5m, 5m)
        {
            ItemNumber = "   "
        };

        Assert.True(populated.HasItemNumber);
        Assert.False(missing.HasItemNumber);
        Assert.False(whitespace.HasItemNumber);
    }

    [Fact]
    public void CustomerDisplayView_keeps_promotion_on_right_when_cart_has_lines()
    {
        var (_, codeBehind) = ReadCustomerDisplayViewFiles();

        Assert.DoesNotContain("UsesCompactPromotionLayout", codeBehind);
        Assert.DoesNotContain("Grid.SetColumnSpan(CartPanel, 2)", codeBehind);
        Assert.DoesNotContain("PromotionBannerRow.Height = new GridLength(154)", codeBehind);
    }

    [Fact]
    public void CustomerDisplayView_scales_advertisement_media_inside_promotion_panel()
    {
        var (xaml, _) = ReadCustomerDisplayViewFiles();

        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var mediaElements = document
            .Descendants(presentation + "Image")
            .Concat(document.Descendants(presentation + "MediaElement"))
            .ToArray();

        Assert.Equal(2, mediaElements.Length);
        Assert.All(mediaElements, element => Assert.Equal("Uniform", element.Attribute("Stretch")?.Value));
        Assert.DoesNotContain("Stretch=\"UniformToFill\"", xaml);
    }

    [Fact]
    public void CustomerDisplayView_shows_discount_rate_and_original_total_for_discounted_lines()
    {
        var (xaml, _) = ReadCustomerDisplayViewFiles();

        Assert.Contains("Text=\"{Binding DiscountRateText}\"", xaml);
        Assert.Contains("Text=\"{Binding GrossAmount, StringFormat={}{0:C2}}\"", xaml);
        Assert.Contains("TextDecorations=\"Strikethrough\"", xaml);
        Assert.Contains("<DataTrigger Binding=\"{Binding HasDiscount}\" Value=\"True\">", xaml);
    }

    [Fact]
    public void CustomerDisplayView_shows_product_thumbnail_item_number_and_lookup_code()
    {
        var (xaml, _) = ReadCustomerDisplayViewFiles();
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace converters = "clr-namespace:Hbpos.Client.Wpf.Converters";
        XNamespace materialDesign = "http://materialdesigninxaml.net/winfx/xaml/themes";

        var lineDataGrid = Assert.Single(document
            .Descendants(presentation + "DataGrid")
            .Where(element => element.Attribute(x + "Name")?.Value == "LineDataGrid"));
        Assert.Equal("72", lineDataGrid.Attribute("RowHeight")?.Value);

        var productImageBrush = Assert.Single(document
            .Descendants(presentation + "ImageBrush")
            .Where(element => element.Attribute(
                converters + "ProductThumbnailImageSourceConverter.AsyncSourceText")?.Value ==
                "{Binding ProductImage}"));
        Assert.Equal(
            "72",
            productImageBrush.Attribute(
                converters + "ProductThumbnailImageSourceConverter.AsyncDecodePixelWidth")?.Value);
        Assert.Equal("Uniform", productImageBrush.Attribute("Stretch")?.Value);

        var thumbnailBorder = Assert.Single(productImageBrush
            .Ancestors(presentation + "Border")
            .Where(element =>
                element.Attribute("Width")?.Value == "52" &&
                element.Attribute("Height")?.Value == "52"));
        Assert.Contains(thumbnailBorder
            .Descendants(materialDesign + "PackIcon"),
            element => element.Attribute("Kind")?.Value == "Shopping");
        Assert.Contains(thumbnailBorder
            .Ancestors(presentation + "Grid"),
            grid => grid
                .Element(presentation + "Grid.ColumnDefinitions")?
                .Elements(presentation + "ColumnDefinition")
                .Select(column => column.Attribute("Width")?.Value)
                .SequenceEqual(["64", "*"]) == true);

        Assert.Contains(document.Descendants(presentation + "Run"),
            element => element.Attribute("Text")?.Value == "{loc:Loc ItemNumber}");
        Assert.Contains(document.Descendants(presentation + "Run"),
            element => element.Attribute("Text")?.Value == "{Binding ItemNumber}");
        Assert.Contains(document.Descendants(presentation + "TextBlock"),
            element => element.Attribute("Text")?.Value == "{Binding LookupCode}");

        var itemNumberTrigger = Assert.Single(document
            .Descendants(presentation + "DataTrigger")
            .Where(element =>
                element.Attribute("Binding")?.Value == "{Binding HasItemNumber}" &&
                element.Attribute("Value")?.Value == "True"));
        Assert.Contains(itemNumberTrigger
            .Ancestors(presentation + "Style")
            .SelectMany(style => style.Elements(presentation + "Setter")),
            setter =>
                setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Collapsed");
    }

    [Fact]
    public void CustomerDisplayView_wraps_content_in_uniform_fixed_design_canvas()
    {
        var (xaml, _) = ReadCustomerDisplayViewFiles();
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var userControl = document.Root!;
        var viewbox = Assert.Single(userControl.Elements(presentation + "Viewbox"));
        var designCanvas = Assert.Single(viewbox.Elements(presentation + "Grid"));

        Assert.Null(viewbox.Attribute("HorizontalAlignment"));
        Assert.Null(viewbox.Attribute("VerticalAlignment"));
        Assert.Equal("Uniform", viewbox.Attribute("Stretch")?.Value);
        Assert.Equal("Both", viewbox.Attribute("StretchDirection")?.Value);
        Assert.Equal("1366", designCanvas.Attribute("Width")?.Value);
        Assert.Equal("768", designCanvas.Attribute("Height")?.Value);
        Assert.Equal("True", userControl.Attribute("UseLayoutRounding")?.Value);
        Assert.Equal("True", userControl.Attribute("SnapsToDevicePixels")?.Value);
        Assert.Equal("True", designCanvas.Attribute("UseLayoutRounding")?.Value);
        Assert.Equal("True", designCanvas.Attribute("SnapsToDevicePixels")?.Value);
        Assert.Equal(
            "{StaticResource PosCustomerDisplayBackgroundBrush}",
            userControl.Attribute("Background")?.Value);
        Assert.Equal(
            "{StaticResource PosCustomerDisplayBackgroundBrush}",
            designCanvas.Attribute("Background")?.Value);
    }

    [Theory]
    // 4:3（常见 15" 客显）→ 画布 1024 宽，铺满不留白。
    [InlineData(1024d, 768d, 1024d)]
    [InlineData(800d, 600d, 1024d)]
    // 16:10（常见 10.1" 客显）→ 约 1229 宽。
    [InlineData(1280d, 800d, 1228.8d)]
    // 16:9 保持原设计宽度。
    [InlineData(1366d, 768d, 1366d)]
    [InlineData(1920d, 1080d, 1365.3333333333d)]
    // 更宽的带鱼屏与竖屏分别钳在上下限，由 Viewbox 留白。
    [InlineData(2560d, 1080d, 1366d)]
    [InlineData(1080d, 1920d, 1024d)]
    // 尚未布局。
    [InlineData(0d, 0d, 1366d)]
    [InlineData(double.PositiveInfinity, 768d, 1366d)]
    public void CustomerDisplayView_design_canvas_width_follows_host_aspect_ratio(
        double hostWidth,
        double hostHeight,
        double expectedWidth)
    {
        Assert.Equal(expectedWidth, CustomerDisplayView.ResolveDesignCanvasWidth(hostWidth, hostHeight), precision: 6);
    }

    [Theory]
    [InlineData(1366d, 0.60d)]
    [InlineData(1600d, 0.60d)]
    [InlineData(1024d, 0.68d)]
    [InlineData(1195d, 0.64d)]
    public void CustomerDisplayView_gives_cart_more_width_on_narrow_canvases(double canvasWidth, double expectedShare)
    {
        Assert.Equal(expectedShare, CustomerDisplayView.ResolveCartColumnShare(canvasWidth), precision: 6);
    }

    [Fact]
    public void CustomerDisplayView_resizes_design_canvas_and_cart_columns_with_the_host()
    {
        var (xaml, codeBehind) = ReadCustomerDisplayViewFiles();
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToHashSet();

        Assert.Contains("DesignCanvas", names);
        Assert.Contains("CartColumn", names);
        Assert.Contains("PromotionColumn", names);
        Assert.Contains("SizeChanged += CustomerDisplayViewSizeChanged;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DesignCanvas.Width = canvasWidth;", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomerDisplayView_uses_compact_left_aligned_quantity_and_sku_grid()
    {
        var (xaml, _) = ReadCustomerDisplayViewFiles();
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var compactStatsGrid = Assert.Single(document
            .Descendants(presentation + "Grid")
            .Where(grid => grid
                .Element(presentation + "Grid.ColumnDefinitions")?
                .Elements(presentation + "ColumnDefinition")
                .Select(column => column.Attribute("Width")?.Value)
                .SequenceEqual(["Auto", "Auto", "32", "Auto", "Auto"]) == true));

        Assert.Equal("Left", compactStatsGrid.Attribute("HorizontalAlignment")?.Value);

        var itemQuantity = Assert.Single(compactStatsGrid.Elements(presentation + "TextBlock")
            .Where(textBlock => textBlock.Attribute("Text")?.Value == "{loc:Loc customer.itemQuantity}"));
        var itemQuantityValue = Assert.Single(compactStatsGrid.Elements(presentation + "TextBlock")
            .Where(textBlock => textBlock.Attribute("Text")?.Value.Contains(
                "Binding TotalItemQuantity",
                StringComparison.Ordinal) == true));
        var skuCount = Assert.Single(compactStatsGrid.Elements(presentation + "TextBlock")
            .Where(textBlock => textBlock.Attribute("Text")?.Value == "{loc:Loc customer.skuCount}"));
        var skuCountValue = Assert.Single(compactStatsGrid.Elements(presentation + "TextBlock")
            .Where(textBlock => textBlock.Attribute("Text")?.Value == "{Binding SkuCount}"));

        Assert.Equal("8,0,0,0", itemQuantityValue.Attribute("Margin")?.Value);
        Assert.Equal("8,0,0,0", skuCountValue.Attribute("Margin")?.Value);
        Assert.Null(itemQuantity.Attribute("Grid.Column"));
        Assert.Equal("1", itemQuantityValue.Attribute("Grid.Column")?.Value);
        Assert.Equal("3", skuCount.Attribute("Grid.Column")?.Value);
        Assert.Equal("4", skuCountValue.Attribute("Grid.Column")?.Value);
    }

    [Fact]
    public void CustomerDisplayView_enlarges_summary_statistics_for_distance_reading()
    {
        var (xaml, codeBehind) = ReadCustomerDisplayViewFiles();
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var summaryRow = document
            .Descendants(presentation + "RowDefinition")
            .Single(element => element.Attribute(x + "Name")?.Value == "SummaryRow");
        var summaryPanel = document
            .Descendants(presentation + "Border")
            .Single(element => element.Attribute(x + "Name")?.Value == "SummaryPanel");

        Assert.Equal("152", summaryRow.Attribute("Height")?.Value);
        Assert.Equal("24,16", summaryPanel.Attribute("Padding")?.Value);
        Assert.Contains("VisibleSummaryRowHeight = new(152)", codeBehind);
        Assert.Equal("15", FindBoundTextBlock(document, presentation, "TotalItemQuantity").Attribute("FontSize")?.Value);
        Assert.Equal("15", FindBoundTextBlock(document, presentation, "SkuCount").Attribute("FontSize")?.Value);
        Assert.Equal("28", FindBoundTextBlock(document, presentation, "Subtotal").Attribute("FontSize")?.Value);
        Assert.Equal("28", FindBoundTextBlock(document, presentation, "TaxAmount").Attribute("FontSize")?.Value);
        Assert.Equal("28", FindBoundTextBlock(document, presentation, "SavingsAmount").Attribute("FontSize")?.Value);
        Assert.Equal("62", FindBoundTextBlock(document, presentation, "TotalToPay").Attribute("FontSize")?.Value);
        Assert.Equal("15", FindTextBlock(document, presentation, "{loc:Loc customer.itemQuantity}").Attribute("FontSize")?.Value);
        Assert.Equal("15", FindTextBlock(document, presentation, "{loc:Loc customer.skuCount}").Attribute("FontSize")?.Value);
        Assert.Equal("14", FindTextBlock(document, presentation, "{loc:Loc Subtotal}").Attribute("FontSize")?.Value);
        Assert.Equal("14", FindTextBlock(document, presentation, "{loc:Loc Tax}").Attribute("FontSize")?.Value);
        Assert.Equal("14", FindTextBlock(document, presentation, "{loc:Loc Savings}").Attribute("FontSize")?.Value);
        Assert.Equal("16", FindTextBlock(document, presentation, "{loc:Loc customer.totalToPay}").Attribute("FontSize")?.Value);
        Assert.Equal("18", FindTextBlock(document, presentation, "{loc:Loc customer.readyForPayment}").Attribute("FontSize")?.Value);
        Assert.Equal("15", FindTextBlock(document, presentation, "{loc:Loc customer.insertOrTap}").Attribute("FontSize")?.Value);
        Assert.Equal("38", FindBoundViewbox(document, presentation, "Subtotal").Attribute("MaxHeight")?.Value);
        Assert.Equal("38", FindBoundViewbox(document, presentation, "TaxAmount").Attribute("MaxHeight")?.Value);
        Assert.Equal("38", FindBoundViewbox(document, presentation, "SavingsAmount").Attribute("MaxHeight")?.Value);
        Assert.Equal("68", FindBoundViewbox(document, presentation, "TotalToPay").Attribute("MaxHeight")?.Value);
    }

    [Fact]
    public void CustomerDisplayView_hides_the_entire_savings_group_until_savings_exist()
    {
        var (xaml, _) = ReadCustomerDisplayViewFiles();
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var savingsAmount = Assert.Single(document
            .Descendants(presentation + "TextBlock")
            .Where(element => element.Attribute("Text")?.Value ==
                "{Binding SavingsAmount, StringFormat=-{0:C2}}"));
        var savingsViewbox = Assert.IsType<XElement>(savingsAmount.Parent);
        Assert.Equal(presentation + "Viewbox", savingsViewbox.Name);
        var savingsPanel = Assert.IsType<XElement>(savingsViewbox.Parent);
        Assert.Equal(presentation + "StackPanel", savingsPanel.Name);
        Assert.Equal("2", savingsPanel.Attribute("Grid.Column")?.Value);
        Assert.Null(savingsPanel.Attribute("Visibility"));

        var summaryAmountsGrid = Assert.IsType<XElement>(savingsPanel.Parent);
        Assert.Equal(presentation + "Grid", summaryAmountsGrid.Name);
        Assert.True(summaryAmountsGrid
            .Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .Select(column => column.Attribute("Width")?.Value)
            .SequenceEqual(["*", "*", "*"]));

        var savingsStyle = Assert.Single(savingsPanel
            .Elements(presentation + "StackPanel.Style")
            .SelectMany(style => style.Elements(presentation + "Style")));
        Assert.Equal("{x:Type StackPanel}", savingsStyle.Attribute("TargetType")?.Value);
        var defaultVisibility = Assert.Single(savingsStyle
            .Elements(presentation + "Setter")
            .Where(setter => setter.Attribute("Property")?.Value == "Visibility"));
        Assert.Equal("Collapsed", defaultVisibility.Attribute("Value")?.Value);

        var hasSavingsTrigger = Assert.Single(savingsStyle
            .Elements(presentation + "Style.Triggers")
            .Elements(presentation + "DataTrigger")
            .Where(trigger =>
                trigger.Attribute("Binding")?.Value == "{Binding HasSavings}" &&
                trigger.Attribute("Value")?.Value == "True"));
        var visibleSetter = Assert.Single(hasSavingsTrigger
            .Elements(presentation + "Setter")
            .Where(setter => setter.Attribute("Property")?.Value == "Visibility"));
        Assert.Equal("Visible", visibleSetter.Attribute("Value")?.Value);

        var savingsLabel = Assert.Single(savingsPanel
            .Elements(presentation + "TextBlock")
            .Where(element => element.Attribute("Text")?.Value == "{loc:Loc Savings}"));
        Assert.Equal("14", savingsLabel.Attribute("FontSize")?.Value);
        Assert.Equal("Bold", savingsLabel.Attribute("FontWeight")?.Value);
        Assert.Null(savingsLabel.Attribute("Visibility"));
        Assert.Equal("38", savingsViewbox.Attribute("MaxHeight")?.Value);
        Assert.Equal("DownOnly", savingsViewbox.Attribute("StretchDirection")?.Value);
        Assert.Null(savingsViewbox.Attribute("Visibility"));
        Assert.Equal("28", savingsAmount.Attribute("FontSize")?.Value);
        Assert.Equal("Black", savingsAmount.Attribute("FontWeight")?.Value);
        Assert.Null(savingsAmount.Attribute("Visibility"));
    }

    [Fact]
    public void CustomerDisplayView_hides_advertisement_title_when_media_is_available()
    {
        var (_, codeBehind) = ReadCustomerDisplayViewFiles();

        Assert.Contains("PromotionSubtitleText.Visibility = Visibility.Collapsed;", codeBehind);
        Assert.DoesNotContain(
            "PromotionSubtitleText.Visibility = hasAdvertisement ? Visibility.Visible : Visibility.Collapsed;",
            codeBehind);
    }

    [Fact]
    public void CustomerDisplayView_hides_promotion_badge_when_advertisement_media_is_available()
    {
        var (xaml, codeBehind) = ReadCustomerDisplayViewFiles();

        Assert.Contains("Text=\"{loc:Loc customer.promotionTitle}\"", xaml);
        Assert.Contains(
            "PromotionTextPanel.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;",
            codeBehind);
    }

    [Fact]
    public void CustomerDisplayView_hides_fallback_background_when_advertisement_media_is_available()
    {
        var (xaml, codeBehind) = ReadCustomerDisplayViewFiles();
        var fallbackBackgroundIndex = xaml.IndexOf("x:Name=\"PromotionFallbackBackground\"", StringComparison.Ordinal);
        var imageIndex = xaml.IndexOf("x:Name=\"AdvertisementImage\"", StringComparison.Ordinal);
        var videoIndex = xaml.IndexOf("x:Name=\"AdvertisementVideo\"", StringComparison.Ordinal);
        var dimOverlayIndex = xaml.IndexOf("Opacity=\"0.18\" Fill=\"#FF000000\"", StringComparison.Ordinal);

        Assert.Contains("x:Name=\"PromotionFallbackBackground\"", xaml);
        Assert.Contains("PromotionFallbackBackground.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;", codeBehind);
        Assert.True(fallbackBackgroundIndex < imageIndex);
        Assert.True(fallbackBackgroundIndex < videoIndex);
        Assert.True(dimOverlayIndex > imageIndex);
        Assert.True(dimOverlayIndex > videoIndex);
    }

    [Fact]
    public void CustomerDisplayView_uses_ipad_dark_palette_across_content_and_window()
    {
        var repoRoot = FindRepoRoot();
        var (viewXaml, _) = ReadCustomerDisplayViewFiles();
        var themeXaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Themes",
            "PosTheme.xaml"));
        var windowXaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Windows",
            "CustomerDisplayWindow.xaml"));

        Assert.Contains("<Color x:Key=\"PosCustomerDisplayColorBackground\">#FF09111F</Color>", themeXaml);
        Assert.Contains("<Color x:Key=\"PosCustomerDisplayColorAccent\">#FF69E3C2</Color>", themeXaml);
        Assert.Contains("<Color x:Key=\"PosCustomerDisplayColorAmount\">#FFFFC73D</Color>", themeXaml);
        Assert.Contains("Background=\"{StaticResource PosCustomerDisplayBackgroundBrush}\"", viewXaml);
        Assert.Contains("Foreground=\"{StaticResource PosCustomerDisplayTextBrush}\"", viewXaml);
        Assert.Contains("Foreground=\"{StaticResource PosCustomerDisplayAccentBrush}\"", viewXaml);
        Assert.Contains("Foreground=\"{StaticResource PosCustomerDisplayAmountBrush}\"", viewXaml);
        Assert.DoesNotContain("Background=\"White\"", viewXaml);
        Assert.DoesNotContain("<LinearGradientBrush", viewXaml);
        Assert.Equal(4, viewXaml.Split("StretchDirection=\"DownOnly\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Background=\"{StaticResource PosCustomerDisplayBackgroundBrush}\"", windowXaml);
        Assert.Contains("Foreground=\"{StaticResource PosCustomerDisplayTextBrush}\"", windowXaml);
    }

    [Fact]
    public void CustomerDisplayView_coalesces_pending_line_scrolls_without_forcing_layout()
    {
        var (_, codeBehind) = ReadCustomerDisplayViewFiles();

        Assert.Contains("private DispatcherOperation? _pendingScrollOperation;", codeBehind);
        Assert.Contains("DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing", codeBehind);
        Assert.Contains("CancelPendingScroll();", codeBehind);
        Assert.DoesNotContain("LineDataGrid.UpdateLayout();", codeBehind);
    }

    [Fact]
    public void LoadAdvertisements_filters_unplayable_items_and_marks_idle_visible_when_cart_is_empty()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadAdvertisements(
            [
                CreateAdvertisement("ad-image", "image", "https://cdn.example.com/ad-image.png"),
                CreateAdvertisement("ad-video", "video", "https://cdn.example.com/ad-video.mp4"),
                CreateAdvertisement("ad-empty", "image", string.Empty),
                CreateAdvertisement("ad-audio", "audio", "https://cdn.example.com/ad-audio.mp3")
            ]);

        Assert.True(viewModel.IsAdvertisementAvailable);
        Assert.True(viewModel.IsIdleAdvertisementVisible);
        Assert.Equal("ad-image", viewModel.CurrentAdvertisement?.Id);
    }

    [Fact]
    public void LoadAdvertisements_filters_expired_items()
    {
        var now = new DateTimeOffset(2026, 6, 29, 10, 0, 0, TimeSpan.Zero);
        var viewModel = new CustomerDisplayViewModel { UtcNow = () => now };

        viewModel.LoadAdvertisements(
            [
                CreateAdvertisement("ad-expired", "image", "https://cdn.example.com/ad-expired.png", now.AddMinutes(-10), now.AddMinutes(-1)),
                CreateAdvertisement("ad-active", "image", "https://cdn.example.com/ad-active.png", now.AddMinutes(-1), now.AddMinutes(10))
            ]);

        Assert.True(viewModel.IsAdvertisementAvailable);
        Assert.Equal("ad-active", viewModel.CurrentAdvertisement?.Id);
    }

    [Fact]
    public void AdvanceAdvertisement_removes_expired_items_before_selecting_next_advertisement()
    {
        var now = new DateTimeOffset(2026, 6, 29, 10, 0, 0, TimeSpan.Zero);
        var viewModel = new CustomerDisplayViewModel { UtcNow = () => now };
        viewModel.LoadAdvertisements(
            [
                CreateAdvertisement("ad-first", "image", "https://cdn.example.com/ad-first.png", now.AddMinutes(-1), now.AddMinutes(1)),
                CreateAdvertisement("ad-second", "image", "https://cdn.example.com/ad-second.png", now.AddMinutes(-1), now.AddMinutes(1))
            ]);
        now = now.AddMinutes(2);

        viewModel.AdvanceAdvertisement();

        Assert.False(viewModel.IsAdvertisementAvailable);
        Assert.Null(viewModel.CurrentAdvertisement);
    }

    [Fact]
    public void AdvanceAdvertisement_skips_expired_item_and_keeps_active_item()
    {
        var now = new DateTimeOffset(2026, 6, 29, 10, 0, 0, TimeSpan.Zero);
        var viewModel = new CustomerDisplayViewModel { UtcNow = () => now };
        viewModel.LoadAdvertisements(
            [
                CreateAdvertisement("ad-current", "image", "https://cdn.example.com/ad-current.png", now.AddMinutes(-1), now.AddMinutes(1)),
                CreateAdvertisement("ad-expired-next", "image", "https://cdn.example.com/ad-expired-next.png", now.AddMinutes(-1), now.AddMinutes(1)),
                CreateAdvertisement("ad-active-next", "image", "https://cdn.example.com/ad-active-next.png", now.AddMinutes(-1), now.AddMinutes(10))
            ]);
        now = now.AddMinutes(2);

        viewModel.AdvanceAdvertisement();

        Assert.True(viewModel.IsAdvertisementAvailable);
        Assert.Equal("ad-active-next", viewModel.CurrentAdvertisement?.Id);
    }

    [Fact]
    public void AdvanceAdvertisement_with_single_item_raises_change_notifications_for_restart()
    {
        var viewModel = new CustomerDisplayViewModel();
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                changedProperties.Add(e.PropertyName);
            }
        };
        viewModel.LoadAdvertisements([CreateAdvertisement("ad-image", "image", "https://cdn.example.com/ad-image.png")]);
        changedProperties.Clear();

        viewModel.AdvanceAdvertisement();

        Assert.Equal("ad-image", viewModel.CurrentAdvertisement?.Id);
        Assert.Equal(2, changedProperties.Count(name => name == nameof(CustomerDisplayViewModel.CurrentAdvertisement)));
    }

    [Fact]
    public void SkipCurrentAdvertisement_removes_failed_item_and_falls_back_when_last_item_is_skipped()
    {
        var viewModel = new CustomerDisplayViewModel();
        viewModel.LoadAdvertisements([CreateAdvertisement("ad-image", "image", "https://cdn.example.com/ad-image.png")]);

        viewModel.SkipCurrentAdvertisement();

        Assert.False(viewModel.IsAdvertisementAvailable);
        Assert.Null(viewModel.CurrentAdvertisement);
        Assert.False(viewModel.IsIdleAdvertisementVisible);
    }

    private static AdvertisementPlaybackItemDto CreateAdvertisement(string id, string mediaType, string mediaUrl)
    {
        var now = DateTimeOffset.UtcNow;
        return CreateAdvertisement(id, mediaType, mediaUrl, now.AddMinutes(-5), now.AddMinutes(5));
    }

    private static AdvertisementPlaybackItemDto CreateAdvertisement(
        string id,
        string mediaType,
        string mediaUrl,
        DateTimeOffset effectiveStart,
        DateTimeOffset effectiveEnd)
    {
        return new AdvertisementPlaybackItemDto(
            id,
            $"Ad {id}",
            $"Description {id}",
            mediaType,
            mediaUrl,
            null,
            $"object/{id}",
            $"{id}.dat",
            "application/octet-stream",
            1024,
            effectiveStart,
            effectiveEnd,
            1);
    }

    private static void AssertPaymentSummary(
        CustomerDisplayViewModel viewModel,
        decimal savingsAmount,
        decimal totalToPay,
        decimal taxAmount,
        bool isReadyForPayment)
    {
        Assert.Equal(savingsAmount, viewModel.SavingsAmount);
        Assert.Equal(savingsAmount > 0m, viewModel.HasSavings);
        Assert.Equal(totalToPay, viewModel.TotalToPay);
        Assert.Equal(taxAmount, viewModel.TaxAmount);
        Assert.Equal(isReadyForPayment, viewModel.IsReadyForPayment);
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    File.Exists(Path.Combine(current.FullName, ".git")) ||
                    File.Exists(Path.Combine(current.FullName, "hb-platform.sln")) ||
                    File.Exists(Path.Combine(current.FullName, "hb-platform.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }

    private static XElement FindBoundTextBlock(XDocument document, XNamespace presentation, string propertyName)
    {
        return Assert.Single(document
            .Descendants(presentation + "TextBlock")
            .Where(element => element.Attribute("Text")?.Value.Contains(
                $"Binding {propertyName}",
                StringComparison.Ordinal) == true));
    }

    private static XElement FindTextBlock(XDocument document, XNamespace presentation, string text)
    {
        return Assert.Single(document
            .Descendants(presentation + "TextBlock")
            .Where(element => element.Attribute("Text")?.Value == text));
    }

    private static XElement FindBoundViewbox(XDocument document, XNamespace presentation, string propertyName)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var summaryPanel = Assert.Single(document
            .Descendants(presentation + "Border")
            .Where(element => element.Attribute(x + "Name")?.Value == "SummaryPanel"));

        return Assert.Single(summaryPanel
            .Descendants(presentation + "Viewbox")
            .Where(viewbox => viewbox
                .Descendants(presentation + "TextBlock")
                .Any(element => element.Attribute("Text")?.Value.Contains(
                    $"Binding {propertyName}",
                    StringComparison.Ordinal) == true)));
    }

    private static (string Xaml, string CodeBehind) ReadCustomerDisplayViewFiles()
    {
        var viewPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "CustomerDisplayView.xaml");

        return (File.ReadAllText(viewPath), File.ReadAllText(viewPath + ".cs"));
    }
}
