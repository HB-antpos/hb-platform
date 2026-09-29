using BlazorApp.Api.Features.DataSync.Common;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 中包数（WarehouseProduct.MinOrderQuantity）由仓库本地维护：HQ 同步只补本地空缺，不覆盖本地正数。
/// </summary>
public sealed class WarehouseMinOrderQuantitySyncGuardTests
{
    [Fact]
    public void BuildLocalValues_只收正数_编码去空格且大小写不敏感()
    {
        var values = WarehouseMinOrderQuantitySyncGuard.BuildLocalValues(
            new[]
            {
                Row(" p-a ", 6),
                Row("P-A", 99),
                Row("P-ZERO", 0),
                Row("P-NEG", -3),
                Row("P-NULL", null),
                Row(null, 5),
                Row("  ", 5),
            }
        );

        Assert.Single(values);
        Assert.Equal(6, values["P-A"]);
    }

    [Fact]
    public void Apply_本地正数覆盖HQ值_本地缺失时保留HQ值()
    {
        var local = WarehouseMinOrderQuantitySyncGuard.BuildLocalValues(
            new[] { Row("P-KEEP", 6), Row("P-SAME", 4) }
        );
        var incoming = new List<WarehouseProduct>
        {
            new() { ProductCode = "p-keep ", MinOrderQuantity = 0 },
            new() { ProductCode = "P-SAME", MinOrderQuantity = 4 },
            new() { ProductCode = "P-FILL", MinOrderQuantity = 12 },
            new() { ProductCode = "P-NONE", MinOrderQuantity = null },
        };

        var preserved = WarehouseMinOrderQuantitySyncGuard.Apply(incoming, local);

        Assert.Equal(1, preserved);
        Assert.Equal(6, incoming[0].MinOrderQuantity);
        Assert.Equal(4, incoming[1].MinOrderQuantity);
        Assert.Equal(12, incoming[2].MinOrderQuantity);
        Assert.Null(incoming[3].MinOrderQuantity);
    }

    [Fact]
    public void React增量同步_批量整行更新前必须回填本地中包数()
    {
        var source = SourceFiles.ReadApi("Services/React/DataSyncIncrementalService.cs");

        SourceFiles.AssertInOrderFrom(
            source,
            "public async Task<SyncResult> SyncWarehouseProductsFromHqIncrementalAsync(",
            "WarehouseMinOrderQuantitySyncGuard.BuildLocalValues(existingRows)",
            "WarehouseMinOrderQuantitySyncGuard.Apply(toUpdate, localMinOrderQuantities)",
            ".BulkUpdateAsync(toUpdate)"
        );
    }

    [Fact]
    public void React全量同步_按列更新前必须回填本地中包数()
    {
        var source = SourceFiles.ReadApi("Services/React/DataSyncFullService.cs");

        SourceFiles.AssertInOrderFrom(
            source,
            "public async Task<SyncResult> SyncWarehouseProductsFromHqAsync(",
            "WarehouseMinOrderQuantitySyncGuard.BuildLocalValues(existingRows)",
            "WarehouseMinOrderQuantitySyncGuard.Apply(toUpdate, localMinOrderQuantities)",
            ".Db.Updateable(toUpdate)"
        );
    }

    private static WarehouseMinOrderQuantitySyncGuard.LocalMinOrderQuantityRow Row(
        string? productCode,
        int? minOrderQuantity
    ) => new() { ProductCode = productCode, MinOrderQuantity = minOrderQuantity };

    private static class SourceFiles
    {
        internal static string ReadApi(string relativePath)
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(
                    current.FullName,
                    "services",
                    "backend",
                    "BlazorApp.Api",
                    relativePath
                );
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                current = current.Parent;
            }

            throw new FileNotFoundException(relativePath);
        }

        internal static void AssertInOrderFrom(
            string source,
            string startMarker,
            params string[] markers
        )
        {
            var offset = source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"未找到起点: {startMarker}");

            foreach (var marker in markers)
            {
                var next = source.IndexOf(marker, offset, StringComparison.Ordinal);
                Assert.True(next >= 0, $"未在预期顺序找到: {marker}");
                offset = next + marker.Length;
            }
        }
    }
}
