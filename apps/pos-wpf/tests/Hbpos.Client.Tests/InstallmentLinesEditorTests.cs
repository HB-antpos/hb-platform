using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Installments;

namespace Hbpos.Client.Tests;

/// <summary>分期单“修改商品”编辑器的纯逻辑测试：数量 / 单价 / 折扣缩放、总额校验、增删行与脏检测。</summary>
public sealed class InstallmentLinesEditorTests
{
    [Fact]
    public void Freshly_seeded_editor_is_clean_and_cannot_save()
    {
        var editor = CreateEditor([Line("P1", 2m, 50m, 10m)], paid: 30m);

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanSave);
        Assert.Equal(InstallmentLinesEditorIssue.None, editor.Issue);
        Assert.Equal(90m, editor.NewTotal);
        Assert.Equal(60m, editor.NewBalance);
        Assert.Equal(0m, editor.Delta);
        // 未改动的行必须原样提交，不能因舍入产生差异。
        Assert.Equal(
            [Line("P1", 2m, 50m, 10m, Guid.Empty)],
            editor.BuildLines()!.Select(line => line with { InstallmentLineGuid = Guid.Empty }).ToList());
    }

    [Fact]
    public void Quantity_step_keeps_per_unit_discount_and_recomputes_actual_amount()
    {
        var editor = CreateEditor([Line("P1", 2m, 50m, 10m)], paid: 30m);
        var row = Assert.Single(editor.Rows);

        row.IncrementCommand.Execute(null);

        // 每件折扣 5：2 件折 10，3 件折 15。
        Assert.Equal(3m, row.Quantity);
        Assert.Equal(15m, row.DiscountAmount);
        Assert.Equal(135m, row.ActualAmount);
        Assert.Equal(135m, editor.NewTotal);
        Assert.Equal(45m, editor.Delta);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanSave);

        row.DecrementCommand.Execute(null);
        row.DecrementCommand.Execute(null);

        Assert.Equal(1m, row.Quantity);
        Assert.Equal(5m, row.DiscountAmount);
        Assert.False(row.DecrementCommand.CanExecute(null));
    }

    [Fact]
    public void Returning_to_original_values_clears_dirty_flag()
    {
        var editor = CreateEditor([Line("P1", 2m, 50m, 10m)], paid: 30m);
        var row = Assert.Single(editor.Rows);

        row.QuantityText = "5";
        Assert.True(editor.IsDirty);
        row.QuantityText = "2";

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanSave);
        Assert.Equal(10m, row.DiscountAmount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("100000")]
    public void Invalid_quantity_text_flags_the_row_and_blocks_saving(string text)
    {
        var editor = CreateEditor([Line("P1", 2m, 50m, 0m)], paid: 30m);
        var row = Assert.Single(editor.Rows);

        row.QuantityText = text;

        Assert.True(row.IsQuantityInvalid);
        Assert.True(row.HasError);
        Assert.Equal(InstallmentLinesEditorIssue.InvalidInput, editor.Issue);
        Assert.False(editor.CanSave);
        Assert.Null(editor.BuildLines());
        // 非法输入期间合计沿用最后一个合法值，不会跳成 0。
        Assert.Equal(100m, editor.NewTotal);
    }

    [Fact]
    public void Untouched_fractional_quantity_from_history_stays_valid_but_edits_must_be_whole_numbers()
    {
        var editor = CreateEditor([Line("P1", 1.5m, 40m, 0m)], paid: 30m);
        var row = Assert.Single(editor.Rows);

        Assert.False(row.HasError);
        Assert.Equal(60m, editor.NewTotal);

        row.QuantityText = "2.5";

        Assert.True(row.IsQuantityInvalid);

        row.IncrementCommand.Execute(null);

        // 从 1.5 步进：向下取整后 +1 = 2。
        Assert.Equal(2m, row.Quantity);
        Assert.False(row.HasError);
    }

    [Fact]
    public void Unit_price_change_recomputes_total_and_clamps_discount_below_gross()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 90m)], paid: 5m, originalTotal: 10m);
        var row = Assert.Single(editor.Rows);

        row.UnitPriceText = "50";

        // 折扣 90 已不小于毛额 50，必须夹到 49.99，实收至少 0.01。
        Assert.Equal(50m, row.UnitPrice);
        Assert.Equal(49.99m, row.DiscountAmount);
        Assert.Equal(0.01m, row.ActualAmount);
        Assert.Equal(InstallmentAmendLinesValidation.Valid, InstallmentAmendRules.ValidateLines(editor.BuildLines()));

        // 基准每件折扣不被夹值破坏：价格改回去折扣随之恢复。
        row.UnitPriceText = "100";
        Assert.Equal(90m, row.DiscountAmount);
        Assert.Equal(10m, row.ActualAmount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1000001")]
    public void Invalid_unit_price_text_flags_the_row(string text)
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);
        var row = Assert.Single(editor.Rows);

        row.UnitPriceText = text;

        Assert.True(row.IsUnitPriceInvalid);
        Assert.Equal(InstallmentLinesEditorIssue.InvalidInput, editor.Issue);
        Assert.False(editor.CanSave);
    }

    [Fact]
    public void Unit_price_with_more_than_two_decimals_is_rounded_away_from_zero()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);
        var row = Assert.Single(editor.Rows);

        row.UnitPriceText = "$12.345";

        Assert.False(row.HasError);
        Assert.Equal(12.35m, row.UnitPrice);
    }

    [Fact]
    public void New_total_below_paid_reports_required_minimum_equal_to_paid()
    {
        var editor = CreateEditor([Line("P1", 1m, 200m, 0m)], paid: 120m);
        var row = Assert.Single(editor.Rows);

        row.UnitPriceText = "100";

        Assert.Equal(InstallmentLinesEditorIssue.TotalBelowPaid, editor.Issue);
        Assert.Equal(120m, editor.RequiredMinimum);
        Assert.False(editor.CanSave);
        Assert.False(editor.WillBePaidOff);
    }

    [Fact]
    public void New_total_below_fifty_floor_reports_floor_as_required_minimum()
    {
        var editor = CreateEditor([Line("P1", 1m, 200m, 0m)], paid: 20m);
        var row = Assert.Single(editor.Rows);

        row.UnitPriceText = "40";

        Assert.Equal(InstallmentLinesEditorIssue.TotalBelowMinimum, editor.Issue);
        Assert.Equal(InstallmentAmendRules.MinimumTotalAmount, editor.RequiredMinimum);
        Assert.False(editor.CanSave);
    }

    [Fact]
    public void New_total_equal_to_paid_is_allowed_and_marks_order_as_paid_off()
    {
        var editor = CreateEditor([Line("P1", 1m, 200m, 0m)], paid: 120m);
        var row = Assert.Single(editor.Rows);

        row.UnitPriceText = "120";

        Assert.Equal(InstallmentLinesEditorIssue.None, editor.Issue);
        Assert.Equal(0m, editor.NewBalance);
        Assert.True(editor.WillBePaidOff);
        Assert.True(editor.CanSave);
        Assert.Equal(-80m, editor.Delta);
    }

    [Fact]
    public void Last_remaining_line_cannot_be_removed()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);
        var row = Assert.Single(editor.Rows);

        Assert.False(row.RemoveCommand.CanExecute(null));
        Assert.False(editor.RemoveRow(row));
        Assert.Single(editor.Rows);
    }

    [Fact]
    public void Removing_a_line_updates_totals_marks_dirty_and_re_enables_nothing_for_last_line()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m), Line("P2", 2m, 40m, 0m)], paid: 30m);
        var second = editor.Rows[1];
        var first = editor.Rows[0];
        Assert.True(second.RemoveCommand.CanExecute(null));

        second.RemoveCommand.Execute(null);

        Assert.Single(editor.Rows);
        Assert.Equal(100m, editor.NewTotal);
        Assert.Equal(-80m, editor.Delta);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanSave);
        // 只剩一行后“删除”立即变为不可用。
        Assert.False(first.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public void Adding_a_new_product_creates_a_row_with_catalog_price_and_quantity_one()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);

        var added = editor.AddProduct(Item("P9", "930009", 19.994m));

        Assert.True(added.IsNew);
        Assert.Equal(2, editor.Rows.Count);
        Assert.Equal(1m, added.Quantity);
        Assert.Equal(19.99m, added.UnitPrice);
        Assert.Equal(0m, added.DiscountAmount);
        Assert.Equal(19.99m, added.ActualAmount);
        Assert.Equal(119.99m, editor.NewTotal);
        Assert.True(editor.CanSave);
        var line = editor.BuildLines()![1];
        Assert.NotEqual(Guid.Empty, line.InstallmentLineGuid);
        Assert.Equal("P9", line.ProductCode);
        Assert.Equal("930009", line.LookupCode);
        Assert.Equal("ITEM-P9", line.ItemNumber);
    }

    [Fact]
    public void Adding_a_product_already_in_the_list_increases_its_quantity_instead_of_duplicating()
    {
        var editor = CreateEditor([Line("P1", 2m, 50m, 10m)], paid: 30m);
        var existing = Assert.Single(editor.Rows);

        var merged = editor.AddProduct(Item("p1", "OTHER-BARCODE", 99m));

        Assert.Same(existing, merged);
        Assert.Single(editor.Rows);
        Assert.Equal(3m, existing.Quantity);
        // 合并加购沿用现有行的价格与每件折扣，不被目录价覆盖。
        Assert.Equal(50m, existing.UnitPrice);
        Assert.Equal(15m, existing.DiscountAmount);
        Assert.Equal(135m, editor.NewTotal);
    }

    [Fact]
    public void Adding_a_product_twice_merges_the_new_row_too()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);

        var first = editor.AddProduct(Item("P9", "930009", 20m));
        var second = editor.AddProduct(Item("P9", "930009", 20m));

        Assert.Same(first, second);
        Assert.Equal(2, editor.Rows.Count);
        Assert.Equal(2m, first.Quantity);
        Assert.Equal(140m, editor.NewTotal);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    public void Adding_a_product_uses_integer_quantity_factor_like_the_cart(int factor, int expectedQuantity)
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);

        var added = editor.AddProduct(Item("P9", "930009", 10m, quantityFactor: factor));

        Assert.Equal((decimal)expectedQuantity, added.Quantity);
    }

    [Fact]
    public void Adding_a_product_with_non_integer_quantity_factor_falls_back_to_one()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);

        var added = editor.AddProduct(Item("P9", "930009", 10m, quantityFactor: 1.5m));

        Assert.Equal(1m, added.Quantity);
    }

    [Fact]
    public void New_product_without_catalog_price_must_be_priced_before_saving()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);

        var added = editor.AddProduct(Item("P9", "930009", 0m));

        Assert.True(added.IsUnitPriceInvalid);
        Assert.False(editor.CanSave);

        added.UnitPriceText = "12.50";

        Assert.False(added.HasError);
        Assert.True(editor.CanSave);
        Assert.Equal(112.5m, editor.NewTotal);
    }

    [Fact]
    public void Built_lines_always_satisfy_the_shared_server_validation()
    {
        var editor = CreateEditor(
            [Line("P1", 3m, 33.33m, 9.99m), Line("P2", 1m, 12.5m, 0m)],
            paid: 30m);
        editor.Rows[0].QuantityText = "7";
        editor.Rows[1].UnitPriceText = "13.37";
        editor.AddProduct(Item("P3", "930003", 4.45m));

        var lines = editor.BuildLines();

        Assert.NotNull(lines);
        Assert.Equal(InstallmentAmendLinesValidation.Valid, InstallmentAmendRules.ValidateLines(lines));
        Assert.Equal(InstallmentAmendRules.CalculateTotal(lines), editor.NewTotal);
        Assert.Equal(InstallmentAmendLinesValidation.Valid, InstallmentAmendRules.ValidateTotal(editor.NewTotal, editor.PaidAmount));
    }

    [Fact]
    public void Property_changes_are_raised_so_the_view_updates_live()
    {
        var editor = CreateEditor([Line("P1", 1m, 100m, 0m)], paid: 30m);
        var raised = new List<string?>();
        editor.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        Assert.Single(editor.Rows).QuantityText = "3";

        Assert.Contains(nameof(InstallmentLinesEditor.NewTotal), raised);
        Assert.Contains(nameof(InstallmentLinesEditor.NewBalance), raised);
        Assert.Contains(nameof(InstallmentLinesEditor.Delta), raised);
        Assert.Contains(nameof(InstallmentLinesEditor.IsDirty), raised);
        Assert.Contains(nameof(InstallmentLinesEditor.CanSave), raised);
    }

    private static InstallmentLinesEditor CreateEditor(
        IReadOnlyList<InstallmentLineDto> lines,
        decimal paid,
        decimal? originalTotal = null) =>
        new(lines, paid, originalTotal ?? InstallmentAmendRules.CalculateTotal(lines));

    private static InstallmentLineDto Line(
        string productCode,
        decimal quantity,
        decimal unitPrice,
        decimal discount,
        Guid? guid = null) => new(
        guid ?? Guid.NewGuid(),
        productCode,
        null,
        $"Item {productCode}",
        $"LOOKUP-{productCode}",
        quantity,
        unitPrice,
        discount,
        InstallmentAmendRules.CalculateActualAmount(quantity, unitPrice, discount),
        $"ITEM-{productCode}");

    private static SellableItemDto Item(
        string productCode,
        string lookupCode,
        decimal retailPrice,
        decimal quantityFactor = 1m) => new(
        "S001",
        productCode,
        null,
        $"Item {productCode}",
        lookupCode,
        $"ITEM-{productCode}",
        lookupCode,
        retailPrice,
        PriceSourceKind.ProductBase,
        "Product",
        quantityFactor,
        DateTimeOffset.UtcNow);
}
