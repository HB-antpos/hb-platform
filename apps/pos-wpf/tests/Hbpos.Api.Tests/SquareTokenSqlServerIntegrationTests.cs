using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

// 真实 SQL Server 验证 Square Token 的门店专用列、索引升级与门店优先/回落解析。
// 复用周跑已配置的隔离库连接变量（与 Linkly 线路用例同一个门控）；每个用例独立建库、独立销毁。
[Trait("Category", "SQL")]
public sealed class SquareTokenSqlServerIntegrationTests : IAsyncLifetime
{
    private string? masterConnection;
    private string connection = string.Empty;
    private string database = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(LinklyLineSqlServerFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        database = $"HbSquareToken_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;

        // 先复刻升级前的生产形态：旧表 + 「每环境一条启用行」的旧唯一索引 + 存量共用 Token。
        await ExecuteAsync(SqlSugarSquareTokenSchemaInitializer.EnsureTableSql);
        await ExecuteAsync("""
            CREATE UNIQUE INDEX [UX_POSM_SquareToken_Environment_Enabled]
            ON [dbo].[POSM_SquareToken]([Environment]) WHERE [IsEnabled] = 1;
            INSERT INTO [dbo].[POSM_SquareToken] ([Environment],[AccessToken],[IsEnabled],[UpdatedBy]) VALUES
                (N'Production', N'legacy-shared-prod', 1, N'admin'),
                (N'Production', N'legacy-disabled', 0, N'admin'),
                (N'Sandbox', N'legacy-shared-sandbox', 1, N'admin');
            """);
    }

    public async Task DisposeAsync()
    {
        if (masterConnection is null) return;
        // 名称只来自本用例生成的 GUID，销毁范围严格限于本用例数据库。
        await ExecuteAtAsync(masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [LinklyLineSqlServerFact]
    public async Task Initializer_upgrades_legacy_table_keeps_existing_rows_shared_and_is_idempotent()
    {
        var initializer = CreateInitializer();

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        // 存量行全部保持 StoreCode 为空 = 全部门店共用，升级无需数据迁移。
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM [dbo].[POSM_SquareToken] WHERE [StoreCode] IS NULL"));
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_POSM_SquareToken_Environment_Enabled' AND object_id = OBJECT_ID(N'[dbo].[POSM_SquareToken]')"));
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_POSM_SquareToken_Environment_Store_Enabled' AND is_unique = 1 AND object_id = OBJECT_ID(N'[dbo].[POSM_SquareToken]')"));
    }

    [LinklyLineSqlServerFact]
    public async Task Unique_index_allows_one_shared_and_one_store_row_per_environment_but_rejects_duplicates()
    {
        await CreateInitializer().InitializeAsync();

        // 同环境下共用行与门店行可以并存。
        await InsertAsync("Production", "1042", "store-1042", enabled: true);
        await InsertAsync("Production", "1005", "store-1005", enabled: true);

        await Assert.ThrowsAsync<SqlException>(() => InsertAsync("Production", "1042", "dup-store", enabled: true));
        await Assert.ThrowsAsync<SqlException>(() => InsertAsync("Production", null, "dup-shared", enabled: true));
        // 被停用的历史行不受唯一约束限制。
        await InsertAsync("Production", "1042", "history", enabled: false);
    }

    [LinklyLineSqlServerFact]
    public async Task Repository_prefers_store_token_and_falls_back_to_shared_token()
    {
        await CreateInitializer().InitializeAsync();
        await InsertAsync("Production", "1042", "store-1042", enabled: true);
        var repository = new SqlSugarSquareTokenRepository(CreateContext());

        var forStore = await repository.GetActiveTokenAsync("Production", "1042", default);
        var otherStore = await repository.GetActiveTokenAsync("Production", "1005", default);
        // 没有门店上下文（StoreCode 为 null）只能命中共用行，也要能正确把 null 作为参数传给 SQL Server。
        var noStore = await repository.GetActiveTokenAsync("Production", null, default);
        var sandbox = await repository.GetActiveTokenAsync("Sandbox", "1042", default);

        Assert.Equal("store-1042", forStore!.AccessToken);
        Assert.Equal("1042", forStore.StoreCode);
        Assert.Equal("legacy-shared-prod", otherStore!.AccessToken);
        Assert.Null(otherStore.StoreCode);
        Assert.Equal("legacy-shared-prod", noStore!.AccessToken);
        Assert.Equal("legacy-shared-sandbox", sandbox!.AccessToken);
    }

    [LinklyLineSqlServerFact]
    public async Task Repository_ignores_blank_or_disabled_store_token_and_falls_back()
    {
        await CreateInitializer().InitializeAsync();
        // 清除专用 Token 时后台会停用该行并置空密钥；另一家门店有一条空白但启用的异常行。
        await InsertAsync("Production", "1042", string.Empty, enabled: false);
        await InsertAsync("Production", "1005", "   ", enabled: true);
        var repository = new SqlSugarSquareTokenRepository(CreateContext());

        var cleared = await repository.GetActiveTokenAsync("Production", "1042", default);
        var blank = await repository.GetActiveTokenAsync("Production", "1005", default);

        Assert.Equal("legacy-shared-prod", cleared!.AccessToken);
        Assert.Equal("legacy-shared-prod", blank!.AccessToken);
    }

    private SqlSugarSquareTokenSchemaInitializer CreateInitializer() =>
        new(new SqlSugarSquareTokenSchemaSqlExecutor(CreateContext()));

    private HbposSqlSugarContext CreateContext()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
        }).Build();
        return new HbposSqlSugarContext(config, NullLogger<HbposSqlSugarContext>.Instance);
    }

    private async Task InsertAsync(string environment, string? storeCode, string accessToken, bool enabled)
    {
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = """
            INSERT INTO [dbo].[POSM_SquareToken] ([Environment],[StoreCode],[AccessToken],[IsEnabled],[UpdatedBy])
            VALUES (@Environment, @StoreCode, @AccessToken, @IsEnabled, N'test');
            """;
        command.Parameters.AddWithValue("@Environment", environment);
        command.Parameters.AddWithValue("@StoreCode", (object?)storeCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@AccessToken", accessToken);
        command.Parameters.AddWithValue("@IsEnabled", enabled);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string sqlText)
    {
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = sqlText;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private Task ExecuteAsync(string sqlText) => ExecuteAtAsync(connection, sqlText);

    private static async Task ExecuteAtAsync(string connectionString, string sqlText)
    {
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync();
    }
}
