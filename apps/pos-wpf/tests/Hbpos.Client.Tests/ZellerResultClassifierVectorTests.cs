using System.Text.Json;
using Hbpos.Client.Wpf.Services.Zeller;

namespace Hbpos.Client.Tests;

/// <summary>
/// 跨端黄金向量：与 packages/pos-payments-core 的 zeller-result-classifier.test.ts 共用
/// test-fixtures/zeller/result-classifier-vectors.json。iPad / 手持用 TS 移植版的分类器，
/// 判错的代价是重复扣款或已扣款却没建单，所以两端必须对同一份输入给出同一个结论。
/// </summary>
public sealed class ZellerResultClassifierVectorTests
{
    private static JsonElement LoadVectors()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "test-fixtures", "zeller", "result-classifier-vectors.json");
            if (File.Exists(candidate))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                return document.RootElement.Clone();
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "未找到 test-fixtures/zeller/result-classifier-vectors.json：" + AppContext.BaseDirectory);
    }

    private static JsonElement[] Cases(string section, int atLeast)
    {
        var cases = LoadVectors().GetProperty(section).EnumerateArray().ToArray();
        Assert.True(cases.Length >= atLeast, section);
        return cases;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static T ParseEnum<T>(JsonElement element, string name) where T : struct, Enum =>
        Enum.Parse<T>(element.GetProperty(name).GetString()!);

    private static ZellerAttemptExpectation Expectation(JsonElement item)
    {
        var expectation = item.GetProperty("expectation");
        return new ZellerAttemptExpectation(
            ParseEnum<ZellerTransactionType>(expectation, "type"),
            expectation.GetProperty("amountCents").GetInt32(),
            expectation.GetProperty("reference").GetString()!);
    }

    private static ZellerTransaction Transaction(JsonElement element)
    {
        var timestamp = OptionalString(element, "timestampIso");
        return new ZellerTransaction(
            ParseEnum<ZellerTransactionStatus>(element, "status"),
            ParseEnum<ZellerTransactionType>(element, "type"),
            element.GetProperty("amountCents").GetInt32(),
            OptionalString(element, "transactionUuid"),
            OptionalString(element, "externalReference"),
            Timestamp: timestamp is null ? null : DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ZellerCallResult CallResult(JsonElement element)
    {
        var errorType = OptionalString(element, "errorType");
        return new ZellerCallResult(
            ParseEnum<ZellerCallOutcome>(element, "outcome"),
            element.TryGetProperty("transaction", out var transaction) && transaction.ValueKind == JsonValueKind.Object
                ? Transaction(transaction)
                : null,
            errorType is null ? null : Enum.Parse<ZellerErrorType>(errorType),
            ReachedTerminal: OptionalBool(element, "reachedTerminal"),
            ProcessingStarted: OptionalBool(element, "processingStarted"));
    }

    private static ZellerQueryResult Query(JsonElement element)
    {
        var errorType = OptionalString(element, "errorType");
        return new ZellerQueryResult(
            element.GetProperty("succeeded").GetBoolean(),
            element.GetProperty("transactions").EnumerateArray().Select(Transaction).ToArray(),
            errorType is null ? null : Enum.Parse<ZellerErrorType>(errorType));
    }

    private static void AssertVerification(
        ZellerVerificationClassification actual,
        ZellerQueryResult query,
        JsonElement expected,
        string name)
    {
        Assert.True(ParseEnum<ZellerVerificationDecision>(expected, "decision") == actual.Decision, name);
        Assert.True(ParseEnum<ZellerUnknownReason>(expected, "unknownReason") == actual.UnknownReason, name);

        // 结论带出的必须是查询结果里的同一笔，不是内容相同的另一笔。
        var index = expected.GetProperty("transactionIndex");
        if (index.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual.Transaction is null, name);
        }
        else
        {
            Assert.True(ReferenceEquals(query.Transactions[index.GetInt32()], actual.Transaction), name);
        }
    }

    [Fact]
    public void Call_classification_matches_the_shared_vectors()
    {
        foreach (var item in Cases("call", 120))
        {
            var name = item.GetProperty("name").GetString()!;
            var result = CallResult(item.GetProperty("result"));
            var expected = item.GetProperty("expected");

            var actual = ZellerResultClassifier.ClassifyCall(result, Expectation(item));

            Assert.True(ParseEnum<ZellerCallDecision>(expected, "decision") == actual.Decision, name);
            Assert.True(ParseEnum<ZellerFailureKind>(expected, "failureKind") == actual.FailureKind, name);
            Assert.True(ParseEnum<ZellerUnknownReason>(expected, "unknownReason") == actual.UnknownReason, name);
            Assert.True(expected.GetProperty("rebuildTerminalFirst").GetBoolean() == actual.RebuildTerminalFirst, name);
            Assert.True(expected.GetProperty("serialGateBreached").GetBoolean() == actual.SerialGateBreached, name);
            Assert.True(expected.GetProperty("returnsTransaction").GetBoolean() == (actual.Transaction is not null), name);
            if (actual.Transaction is not null)
            {
                Assert.True(ReferenceEquals(result.Transaction, actual.Transaction), name);
            }
        }
    }

    [Fact]
    public void Verification_classification_matches_the_shared_vectors()
    {
        foreach (var item in Cases("verification", 20))
        {
            var name = item.GetProperty("name").GetString()!;
            var pending = ParseEnum<ZellerCallDecision>(item, "pending");
            var query = Query(item.GetProperty("query"));
            var expected = item.GetProperty("expected");

            if (OptionalBool(expected, "throws"))
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => ZellerResultClassifier.ClassifyVerification(pending, query, Expectation(item)));
                continue;
            }

            AssertVerification(
                ZellerResultClassifier.ClassifyVerification(pending, query, Expectation(item)),
                query,
                expected,
                name);
        }
    }

    [Fact]
    public void Late_recheck_classification_matches_the_shared_vectors()
    {
        foreach (var item in Cases("lateRecheck", 5))
        {
            var query = Query(item.GetProperty("query"));

            AssertVerification(
                ZellerResultClassifier.ClassifyLateRecheck(query, Expectation(item)),
                query,
                item.GetProperty("expected"),
                item.GetProperty("name").GetString()!);
        }
    }
}
