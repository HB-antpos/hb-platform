using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 分店订单「负数发货 = 退货」保存层规则表：有 / 无权限 × 订货 0 / &gt; 0 × 有 / 无原因 × 退货行新建 / 修改 / 清零，
/// 以及空行软删规则、单据类型（按净额）判定。规则本身是纯函数，不依赖数据库。
/// </summary>
public sealed class StoreOrderReturnRulesTests
{
    private static readonly StoreOrderReturnLineRequest NoReturnFields = default;

    private static StoreOrderReturnWriteDecision Evaluate(
        bool canRecordReturns,
        decimal newAlloc,
        decimal? existingOrderQuantity = null,
        decimal? existingAlloc = null,
        string? existingReason = null,
        int? existingDisposition = null,
        string? existingNote = null,
        StoreOrderReturnLineRequest request = default
    ) =>
        StoreOrderReturnRules.Evaluate(
            canRecordReturns,
            newAlloc,
            existingOrderQuantity,
            existingAlloc,
            existingReason,
            existingDisposition,
            existingNote,
            request
        );

    [Fact]
    public void 有权限_订货为0_带有效原因_新建退货行放行并规范原因码大小写()
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: -3,
            request: new StoreOrderReturnLineRequest(" WRONGITEM ", 1, "  box crushed  ")
        );

        Assert.Null(decision.Error);
        Assert.True(decision.IsReturnLine);
        Assert.Equal("wrongItem", decision.Reason);
        Assert.Equal(1, decision.Disposition);
        Assert.Equal("box crushed", decision.Note);
    }

    [Fact]
    public void 没有权限_负数发货一律拒绝_即使带了原因()
    {
        var decision = Evaluate(
            canRecordReturns: false,
            newAlloc: -1,
            request: new StoreOrderReturnLineRequest("damaged", null, null)
        );

        Assert.NotNull(decision.Error);
        Assert.Contains("权限", decision.Error);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0.5)]
    public void 本单该商品已有订货_退货被拒绝并提示换单(double existingOrderQuantity)
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: -2,
            existingOrderQuantity: (decimal)existingOrderQuantity,
            existingAlloc: 0,
            request: new StoreOrderReturnLineRequest("damaged", null, null)
        );

        Assert.Equal("该商品本单已有订货，请退到另一张订单", decision.Error);
    }

    [Fact]
    public void 本单该商品只有主动配货行_订货为0_可以直接改成退货()
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: -2,
            existingOrderQuantity: 0,
            existingAlloc: 6,
            request: new StoreOrderReturnLineRequest("overShipped", 0, null)
        );

        Assert.Null(decision.Error);
        Assert.True(decision.IsReturnLine);
        Assert.Equal("overShipped", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 新建退货行没有原因_拒绝(string? reason)
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: -1,
            request: new StoreOrderReturnLineRequest(reason, null, null)
        );

        Assert.Equal("请选择退货原因", decision.Error);
    }

    [Fact]
    public void 原因不在码表内_拒绝()
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: -1,
            request: new StoreOrderReturnLineRequest("changedMind", null, null)
        );

        Assert.Equal("退货原因无效，请重新选择", decision.Error);
    }

    [Fact]
    public void 处置只能是0或1_备注不得超过200字符()
    {
        var badDisposition = Evaluate(
            true,
            -1,
            request: new StoreOrderReturnLineRequest("damaged", 2, null)
        );
        var longNote = Evaluate(
            true,
            -1,
            request: new StoreOrderReturnLineRequest("damaged", null, new string('x', 201))
        );
        var maxNote = Evaluate(
            true,
            -1,
            request: new StoreOrderReturnLineRequest("damaged", null, new string('x', 200))
        );

        Assert.Equal("退货处置无效", badDisposition.Error);
        Assert.Contains("200", longNote.Error);
        Assert.Null(maxNote.Error);
    }

    [Fact]
    public void 已有退货行只改数量_沿用原原因处置备注()
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: -8,
            existingOrderQuantity: 0,
            existingAlloc: -3,
            existingReason: "quality",
            existingDisposition: 1,
            existingNote: "mouldy"
        );

        Assert.Null(decision.Error);
        Assert.True(decision.IsReturnLine);
        Assert.Equal("quality", decision.Reason);
        Assert.Equal(1, decision.Disposition);
        Assert.Equal("mouldy", decision.Note);
    }

    [Fact]
    public void 已有退货行改原因_备注传空串表示清空()
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: -3,
            existingOrderQuantity: 0,
            existingAlloc: -3,
            existingReason: "quality",
            existingNote: "mouldy",
            request: new StoreOrderReturnLineRequest("nearExpiry", null, "")
        );

        Assert.Null(decision.Error);
        Assert.Equal("nearExpiry", decision.Reason);
        Assert.Null(decision.Note);
    }

    [Fact]
    public void 没有权限_不能动已有退货行_包括改成0或正数以及只改价格()
    {
        // 改成 0（会触发软删）和改成正数，都等于抹掉退货。
        var toZero = Evaluate(false, 0, existingOrderQuantity: 0, existingAlloc: -3, existingReason: "damaged");
        var toPositive = Evaluate(false, 4, existingOrderQuantity: 0, existingAlloc: -3, existingReason: "damaged");
        // 只改价格：调用方把「改后发货数量」传成原值（仍为负），无权限同样拒绝。
        var priceOnly = Evaluate(false, -3, existingOrderQuantity: 0, existingAlloc: -3, existingReason: "damaged");

        Assert.All(new[] { toZero, toPositive, priceOnly }, decision => Assert.NotNull(decision.Error));
    }

    [Fact]
    public void 有权限_把退货行改回0_放行且退货字段清空()
    {
        var decision = Evaluate(
            canRecordReturns: true,
            newAlloc: 0,
            existingOrderQuantity: 0,
            existingAlloc: -3,
            existingReason: "damaged",
            existingDisposition: 0,
            existingNote: "x",
            request: new StoreOrderReturnLineRequest("damaged", 0, "x")
        );

        Assert.Null(decision.Error);
        Assert.False(decision.IsReturnLine);
        Assert.Null(decision.Reason);
        Assert.Null(decision.Disposition);
        Assert.Null(decision.Note);
    }

    [Fact]
    public void 普通发货行不得带退货字段_无权限者的普通编辑不受影响()
    {
        var withFields = Evaluate(
            canRecordReturns: true,
            newAlloc: 5,
            existingOrderQuantity: 5,
            existingAlloc: 5,
            request: new StoreOrderReturnLineRequest("damaged", null, null)
        );
        var plainEdit = Evaluate(
            canRecordReturns: false,
            newAlloc: 7,
            existingOrderQuantity: 5,
            existingAlloc: 5
        );

        Assert.Equal("只有退货行（发货数量为负）才能填写退货原因、处置或备注", withFields.Error);
        Assert.Null(plainEdit.Error);
        Assert.False(plainEdit.IsReturnLine);
    }

    [Fact]
    public void 原因码表与单据英文文案一一对应()
    {
        Assert.Equal(
            new[] { "damaged", "wrongItem", "overShipped", "quality", "nearExpiry", "other" },
            StoreOrderReturnRules.ReasonCodes
        );
        Assert.Equal("damaged", StoreOrderReturnRules.ToDocumentReason("damaged"));
        Assert.Equal("wrong item sent", StoreOrderReturnRules.ToDocumentReason("wrongItem"));
        Assert.Equal("over-shipped", StoreOrderReturnRules.ToDocumentReason("overShipped"));
        Assert.Equal("quality issue", StoreOrderReturnRules.ToDocumentReason("quality"));
        Assert.Equal("near expiry", StoreOrderReturnRules.ToDocumentReason("nearExpiry"));
        Assert.Equal("other", StoreOrderReturnRules.ToDocumentReason("other"));
        Assert.Null(StoreOrderReturnRules.ToDocumentReason(" "));
    }

    [Theory]
    [InlineData(-0.01, "creditNote")]
    [InlineData(-13.90, "creditNote")]
    [InlineData(0, "invoice")]
    [InlineData(0.01, "invoice")]
    [InlineData(69.90, "invoice")]
    // 取整到 2 位小数后为 0 的微小负数仍是发票（$0.00），不能变成退款单。
    [InlineData(-0.004, "invoice")]
    public void 单据类型按净额判定(double net, string expected)
    {
        Assert.Equal(expected, StoreOrderReturnRules.ResolveDocumentType((decimal)net));
    }

    [Theory]
    // 空行 = 订货 <= 0 且发货恰好为 0
    [InlineData(0, 0, true)]
    [InlineData(null, 0, false)]
    // 退货行（订货 0、发货 < 0）不是空行，不能被软删
    [InlineData(0, -3, false)]
    // 有发货或有订货都不是空行
    [InlineData(0, 5, false)]
    [InlineData(3, 0, false)]
    public void 软删规则_退货行不会被当成空行(int? quantity, int? alloc, bool expected)
    {
        decimal? q = quantity;
        decimal? a = alloc;

        Assert.Equal(expected, StoreOrderManagementRules.ShouldSoftDeleteExistingDetail(q, a));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(null, null, true)]
    [InlineData(0, -3, false)]
    [InlineData(0, 5, false)]
    [InlineData(2, 0, false)]
    public void 批量按明细保存的软删规则_退货行不会被当成空行(int? quantity, int? alloc, bool expected)
    {
        decimal? q = quantity;
        decimal? a = alloc;

        Assert.Equal(expected, StoreOrderManagementRules.ShouldSoftDelete(q, a));
    }
}
