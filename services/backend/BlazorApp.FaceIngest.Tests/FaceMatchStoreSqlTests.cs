using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using static BlazorApp.FaceIngest.Tests.CosImageStoreTests;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>
/// 接收端写库 SQL 的静态审查（真实 SQL Server 上的并发 UPSERT、授权清单已在一次性容器里另行验证过，不进仓库）：
/// 全部参数化、不含 DELETE / MERGE / DROP / 拼接，只碰设计 13.3、14.4 授权给 face_ingest_app 的表与列。
/// </summary>
public sealed class FaceMatchStoreSqlTests
{
    private static readonly string[] AllSql =
    [
        SqlFaceStoreProfileRepository.LoadAllSql,
        SqlFaceMatchStore.EnsureVisitGroupSql,
        SqlFaceMatchStore.ApplyTrafficSql,
        SqlFaceMatchStore.FindUnwelcomeEntrySql,
        SqlFaceMatchStore.InsertMatchTaskSql,
        SqlFaceMatchStore.InsertEvaluationSql,
    ];

    [Fact]
    public void 所有SQL不含DELETE_MERGE_DROP_TRUNCATE_ALTER_EXEC()
    {
        foreach (var sql in AllSql)
        {
            Assert.DoesNotMatch(new Regex(@"\b(DELETE|MERGE|DROP|TRUNCATE|ALTER|EXEC|EXECUTE|GRANT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), sql);
        }
    }

    [Fact]
    public void 只引用授权清单内的表()
    {
        var allowed = new[] { "FaceStoreProfile", "FaceTrafficBucket", "FaceVisitGroup", "FaceMatchEvaluation", "FaceUnwelcomeEntry", "FaceMatchTask" };
        foreach (var sql in AllSql)
        {
            foreach (Match match in Regex.Matches(sql, @"\[dbo\]\.\[(?<table>\w+)\]"))
            {
                Assert.Contains(match.Groups["table"].Value, allowed);
            }
        }
    }

    [Fact]
    public void 评估表只授INSERT_SQL里不得读它()
    {
        // face_ingest_app 对 FaceMatchEvaluation 只有 INSERT：不能 SELECT，所以不能写 IF NOT EXISTS (SELECT ...)，重复靠吞主键冲突。
        Assert.DoesNotContain("SELECT", SqlFaceMatchStore.InsertEvaluationSql, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("INSERT INTO [dbo].[FaceMatchEvaluation]", SqlFaceMatchStore.InsertEvaluationSql, StringComparison.Ordinal);
    }

    [Fact]
    public void 名单条目查询只认已生效的条目_Status等于1()
    {
        // 主 API 撤回 / 到期时先把条目改成非 Active，再去腾讯云删人；接收端必须在这个窗口里就不再用该条目建复核任务。
        Assert.Contains("[Status] = 1", SqlFaceMatchStore.FindUnwelcomeEntrySql, StringComparison.Ordinal);
        Assert.Contains("[TencentPersonId] = @TencentPersonId", SqlFaceMatchStore.FindUnwelcomeEntrySql, StringComparison.Ordinal);
    }

    [Fact]
    public void 访客库行只UPDATE_VisitorCount一列()
    {
        var updates = Regex.Matches(SqlFaceMatchStore.ApplyTrafficSql, @"UPDATE \[dbo\]\.\[FaceVisitGroup\] SET (?<set>.+?) WHERE", RegexOptions.CultureInvariant);
        var update = Assert.Single(updates);
        Assert.Equal("[VisitorCount] = [VisitorCount] + @VisitorsAdded", update.Groups["set"].Value.Trim());
    }

    [Fact]
    public void 计数桶UPSERT_先UPDATE带范围锁再INSERT_累加写成列加增量_同一事务()
    {
        var sql = SqlFaceMatchStore.ApplyTrafficSql;

        Assert.Contains("WITH (UPDLOCK, SERIALIZABLE)", sql, StringComparison.Ordinal);
        Assert.Contains("IF @@ROWCOUNT = 0", sql, StringComparison.Ordinal);
        Assert.Contains("[CaptureCount] = [CaptureCount] + @CaptureCount", sql, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT ON", sql, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf("UPDATE [dbo].[FaceTrafficBucket]", StringComparison.Ordinal) < sql.IndexOf("INSERT INTO [dbo].[FaceTrafficBucket]", StringComparison.Ordinal));
    }

    [Fact]
    public void 命中任务不写通知时间和复核人列_新任务是Pending()
    {
        var sql = SqlFaceMatchStore.InsertMatchTaskSql;

        Assert.DoesNotContain("NotifiedAtUtc", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewerId", sql, StringComparison.Ordinal);
        Assert.Contains("@ImageObjectKey, 0, @ImageDeleteAtUtc", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void 所有取值都走参数_没有字符串插值痕迹()
    {
        foreach (var sql in AllSql)
        {
            Assert.DoesNotContain("'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("{", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task COS保存名单命中图_键在私有前缀face_ingest_unwelcome_hits下_私有acl_不用原始报文键()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, etag: "\"fake-etag\"");
        var store = CreateStore(handler, keyPrefix: "face-ingest", uploadRawBody: false);
        var taskId = new string('7', 32);

        Assert.True(store.Available);
        var key = await store.SaveAsync(
            new UnwelcomeHitImage(taskId, "S1001", "cam-front-01", new DateTimeOffset(2026, 10, 12, 23, 59, 0, TimeSpan.Zero), new byte[] { 1, 2, 3 }, new string('a', 64)),
            CancellationToken.None);

        Assert.Equal($"face-ingest/unwelcome-hits/S1001/2026/10/12/cam-front-01/{taskId}.jpg", key);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/" + key, request.Uri.AbsolutePath);
        Assert.Equal("private", request.Headers["x-cos-acl"]);
        Assert.Equal("image/jpeg", request.ContentType);
    }

    [Fact]
    public async Task 没开COS时命中图存储不可用()
    {
        var store = new NoUnwelcomeHitImageStore();

        Assert.False(store.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new UnwelcomeHitImage("t", "S", "c", DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty, "x"), CancellationToken.None));
    }
}
