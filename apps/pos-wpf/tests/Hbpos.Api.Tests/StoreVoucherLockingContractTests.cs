using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Services;
using SqlSugar;

namespace Hbpos.Api.Tests;

/// <summary>
/// 锁住代金券预占与开券幂等的并发闸门：PosmDb 全局 IsWithNoLockQuery=true，
/// 任何一条读回退成 WITH(NOLOCK) 或丢掉 applock，两台 POS 就能同时预占同一张券、并发重试开出两张券。
/// 真实 SQL Server 上的并发行为见 StoreVoucherConcurrencySqlServerIntegrationTests。
/// </summary>
public sealed class StoreVoucherLockingContractTests
{
    [Fact]
    public void ReserveAsync_takes_voucher_applock_before_reading_and_reads_without_nolock()
    {
        var body = ExtractMethodBody(ReadStoreVoucherServiceSource(), "public async Task<StoreVoucherReservation> ReserveAsync(");

        var lockIndex = body.IndexOf(
            "await StoreVoucherMutationLock.AcquireAsync(\n                db,\n                StoreVoucherMutationLock.BuildVoucherResource(normalizedVoucherCode));",
            StringComparison.Ordinal);
        Assert.True(lockIndex >= 0, "ReserveAsync 必须按券码取 applock。");
        Assert.True(
            body.IndexOf("await db.Ado.BeginTranAsync(", StringComparison.Ordinal) < lockIndex,
            "applock 的 LockOwner 是 Transaction，必须在事务开始之后获取。");
        AssertEveryQueryableReadsCommitted(body, lockIndex, expectedQueryables: 2);
        // 先汇总已预占再读余额：与上传事务「先 claim 再扣余额」配合，交错时只会少算可用额。
        Assert.True(
            body.IndexOf("Queryable<StoreVoucherReservationEntity>()", StringComparison.Ordinal) <
            body.IndexOf("Queryable<StoreVoucher>()", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateVoucherAsync_takes_idempotency_applock_before_lookup_and_lookup_reads_without_nolock()
    {
        var source = ReadStoreVoucherServiceSource();
        var create = ExtractMethodBody(source, "private async Task<StoreVoucher> CreateVoucherAsync(");
        var lockIndex = create.IndexOf(
            "StoreVoucherMutationLock.BuildIdempotencyResource(request.CodePrefix, request.StoreCode, request.IdempotencyKey));",
            StringComparison.Ordinal);
        var lookupIndex = create.IndexOf("await FindVoucherByIdempotencyKeyAsync(request, cancellationToken);", StringComparison.Ordinal);
        Assert.True(lockIndex >= 0, "CreateVoucherAsync 必须按幂等键取 applock。");
        Assert.True(create.IndexOf("BeginTranAsync(", StringComparison.Ordinal) < lockIndex);
        Assert.True(lockIndex < lookupIndex, "applock 必须在幂等查找之前获取。");

        var lookup = ExtractMethodBody(source, "private async Task<StoreVoucher?> FindVoucherByIdempotencyKeyAsync(");
        AssertEveryQueryableReadsCommitted(lookup, startIndex: 0, expectedQueryables: 1);
        Assert.Contains("x.Remark.StartsWith(refundPrefixPattern)", lookup, StringComparison.Ordinal);
        Assert.Contains("x.Remark.Contains(issueMarkerPattern)", lookup, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_sql_replaces_global_nolock_with_read_committed()
    {
        var client = CreateSqlServerClient();

        var sql = new[]
        {
            client.Queryable<StoreVoucher>().With(StoreVoucherMutationLock.ReadCommittedHint)
                .Where(x => x.VoucherCode == "V001").OrderByExactStoreFirst("S01").ToSqlString(),
            client.Queryable<StoreVoucherReservationEntity>().With(StoreVoucherMutationLock.ReadCommittedHint)
                .Where(x => x.VoucherCode == "V001").ToSqlString()
        };

        // 对照：不加提示时全局配置确实会生成 NOLOCK，说明上面的断言不是空转。
        Assert.Contains("WITH(NOLOCK)", client.Queryable<StoreVoucher>().ToSqlString(), StringComparison.OrdinalIgnoreCase);
        Assert.All(sql, statement =>
        {
            Assert.Contains("WITH(READCOMMITTED)", statement, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NOLOCK", statement, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Applock_is_exclusive_and_transaction_owned()
    {
        var sql = Normalize(StoreVoucherMutationLock.AppLockSql);

        Assert.Contains("EXEC @Result = sys.sp_getapplock", sql, StringComparison.Ordinal);
        Assert.Contains("@LockMode = N'Exclusive'", sql, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Transaction'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_resources_are_case_insensitive_for_voucher_codes_and_bounded_in_length()
    {
        Assert.Equal(
            StoreVoucherMutationLock.BuildVoucherResource(" v001 "),
            StoreVoucherMutationLock.BuildVoucherResource("V001"));
        Assert.True(StoreVoucherMutationLock.BuildVoucherResource(new string('A', 400)).Length <= 255);

        var issue = StoreVoucherMutationLock.BuildIdempotencyResource("VC", "S01", new string('k', 1000));
        Assert.True(issue.Length <= 255);
        Assert.Equal(issue, StoreVoucherMutationLock.BuildIdempotencyResource("VC", " s01 ", new string('k', 1000)));
        Assert.NotEqual(issue, StoreVoucherMutationLock.BuildIdempotencyResource("RF", "S01", new string('k', 1000)));
        Assert.NotEqual(issue, StoreVoucherMutationLock.BuildIdempotencyResource("VC", null, new string('k', 1000)));
    }

    [Fact]
    public void Like_literal_escapes_sql_server_wildcards_only()
    {
        var sqlServer = CreateSqlServerClient();
        var sqlite = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute
        });

        Assert.Equal("IssueKey[[]QUJD] | 1[%][_]", StoreVoucherMutationLock.EscapeLikeLiteral(sqlServer, "IssueKey[QUJD] | 1%_"));
        Assert.Equal("IssueKey[QUJD]", StoreVoucherMutationLock.EscapeLikeLiteral(sqlite, "IssueKey[QUJD]"));
    }

    private static void AssertEveryQueryableReadsCommitted(string body, int startIndex, int expectedQueryables)
    {
        var queryables = Regex.Matches(body, @"\.Queryable<[A-Za-z]+>\(\)");
        Assert.Equal(expectedQueryables, queryables.Count);
        foreach (Match match in queryables)
        {
            Assert.True(match.Index > startIndex, $"{match.Value} 必须在 applock 之后执行。");
            var next = body[(match.Index + match.Length)..].TrimStart();
            Assert.StartsWith(".With(StoreVoucherMutationLock.ReadCommittedHint)", next, StringComparison.Ordinal);
        }
    }

    private static SqlSugarClient CreateSqlServerClient() => new(new ConnectionConfig
    {
        ConnectionString = "Server=(local);Database=HBPOSM;Trusted_Connection=True;",
        DbType = SqlSugar.DbType.SqlServer,
        InitKeyType = InitKeyType.Attribute,
        MoreSettings = new ConnMoreSettings { IsWithNoLockQuery = true }
    });

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法：{signature}");
        var open = source.IndexOf("\n    {\n", start, StringComparison.Ordinal);
        var close = source.IndexOf("\n    }\n", open, StringComparison.Ordinal);
        Assert.True(open > start && close > open, $"无法定位方法体：{signature}");
        return source[open..close];
    }

    private static string ReadStoreVoucherServiceSource([CallerFilePath] string testFilePath = "")
    {
        var testsDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("Cannot resolve test source directory.");
        var sourcePath = Path.GetFullPath(Path.Combine(
            testsDirectory,
            "..",
            "..",
            "src",
            "Hbpos.Api",
            "Services",
            "StoreVoucherService.cs"));

        // Windows 检出可能是 CRLF，统一成 LF 再做多行匹配。
        return Normalize(File.ReadAllText(sourcePath));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
