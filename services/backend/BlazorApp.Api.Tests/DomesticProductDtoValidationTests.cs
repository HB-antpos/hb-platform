using System.ComponentModel.DataAnnotations;
using BlazorApp.Shared.DTOs;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 国内商品单条创建/更新 DTO 的装箱数、中包数量校验口径：最小为 1。
/// 与前端表单的 min=1 保持一致；不填（null）仍然允许。
/// </summary>
public sealed class DomesticProductDtoValidationTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(24, true)]
    public void 更新DTO_装箱数最小为1(int? packingQuantity, bool expectedValid)
    {
        var dto = new UpdateDomesticProductDto { PackingQuantity = packingQuantity };

        Assert.Equal(expectedValid, IsValid(dto));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    public void 更新DTO_中包数量最小为1(int? middlePackQuantity, bool expectedValid)
    {
        var dto = new UpdateDomesticProductDto { MiddlePackQuantity = middlePackQuantity };

        Assert.Equal(expectedValid, IsValid(dto));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    public void 创建DTO_装箱数与中包数量最小为1(int? packingQuantity, int? middlePackQuantity, bool expectedValid)
    {
        var dto = new CreateDomesticProductDto
        {
            SupplierCode = "SUP-1",
            ProductName = "校验用商品",
            PackingQuantity = packingQuantity,
            MiddlePackQuantity = middlePackQuantity,
        };

        Assert.Equal(expectedValid, IsValid(dto));
    }

    private static bool IsValid(object dto) =>
        Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), validateAllProperties: true);
}
