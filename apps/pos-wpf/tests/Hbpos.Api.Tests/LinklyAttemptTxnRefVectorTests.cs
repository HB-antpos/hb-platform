using System.Text.Json;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Api.Tests;

/// <summary>
/// 跨端黄金向量：与 packages/pos-payments-core 的 linkly-attempt-txn-ref.test.ts 共用
/// test-fixtures/linkly-attempt-txnref/vectors.json。iPad/手持在发请求前用 TS 移植版派生并落库 TxnRef，
/// Hbpos.Api 收到 attemptGuid 后用本类派生同一值，两端任何一边改动算法都会在这里或 TS 侧失败。
/// </summary>
public sealed class LinklyAttemptTxnRefVectorTests
{
    private static string ResolveVectorFile()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName, "test-fixtures", "linkly-attempt-txnref", "vectors.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "未找到 test-fixtures/linkly-attempt-txnref/vectors.json：" + AppContext.BaseDirectory);
    }

    [Fact]
    public void Create_matches_the_shared_cross_language_vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResolveVectorFile()));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.True(cases.Length >= 10);

        foreach (var item in cases)
        {
            var type = item.GetProperty("transactionType").GetString()!;
            var guid = Guid.Parse(item.GetProperty("attemptGuid").GetString()!);
            var expected = item.GetProperty("txnRef").GetString()!;

            Assert.Equal(expected, LinklyAttemptTxnRef.Create(type[0], guid));
            // 字符串入口必须使用小写 D 格式，与 POS 侧落库值一致。
            Assert.Equal(expected, LinklyAttemptTxnRef.Create(type[0], guid.ToString("D")));
        }
    }
}
