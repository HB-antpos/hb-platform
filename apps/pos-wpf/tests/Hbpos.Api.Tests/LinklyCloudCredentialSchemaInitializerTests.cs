using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudCredentialSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_linkly_cloud_credential_ddl()
    {
        var executor = new CapturingLinklyCloudCredentialSchemaSqlExecutor();
        var initializer = new SqlSugarLinklyCloudCredentialSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(4, executor.SqlStatements.Count);
        var sql = string.Join(Environment.NewLine, executor.SqlStatements);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NULL", sql);
        Assert.Contains("[StoreCode] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[Environment] NVARCHAR(32) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment] DEFAULT (N'Production')", sql);
        Assert.Contains("[Username] NVARCHAR(256) NOT NULL", sql);
        Assert.Contains("[Password] NVARCHAR(256) NOT NULL", sql);
        Assert.Contains("[UpdatedAt] DATETIME2(7) NOT NULL", sql);
        Assert.Contains("[UpdatedBy] NVARCHAR(128) NULL", sql);
        Assert.Contains("SET [Environment] = N'Production'", sql);
        Assert.Contains("CONSTRAINT [CK_POSM_LinklyCloudCredential_Environment]", sql);
        Assert.Contains("DROP CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode]", sql);
        Assert.Contains("CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment]", sql);
        Assert.Contains("UNIQUE ([StoreCode], [Environment])", sql);
    }

    [Fact]
    public async Task InitializeAsync_executes_environment_column_batch_before_constraint_batch()
    {
        var executor = new CapturingLinklyCloudCredentialSchemaSqlExecutor();
        var initializer = new SqlSugarLinklyCloudCredentialSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(4, executor.SqlStatements.Count);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudCredential', N'Environment') IS NULL", executor.SqlStatements[1]);
        Assert.DoesNotContain("SET [Environment] = N'Production'", executor.SqlStatements[1]);
        Assert.Contains("SET [Environment] = N'Production'", executor.SqlStatements[2]);
        Assert.Contains("CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment]", executor.SqlStatements[3]);
    }

    [Fact]
    public async Task InitializeAsync_wraps_every_batch_in_the_shared_schema_applock_transaction()
    {
        var executor = new CapturingLinklyCloudCredentialSchemaSqlExecutor();
        var initializer = new SqlSugarLinklyCloudCredentialSchemaInitializer(executor);

        await initializer.InitializeAsync();

        // 与 Admin 迁移器、BackendAsync 初始化器同名的锁，否则首次部署并发建表会互相踩。
        Assert.Equal(4, executor.SqlStatements.Count);
        Assert.All(executor.SqlStatements, sql =>
        {
            Assert.Contains("N'Hbpos.LinklyCloud.Schema.v2'", sql);
            Assert.Contains("@LockOwner = N'Transaction'", sql);
            Assert.StartsWith("SET XACT_ABORT ON;", sql.TrimStart());
            Assert.EndsWith("COMMIT TRANSACTION;", sql.TrimEnd());
            Assert.True(
                sql.IndexOf("sp_getapplock", StringComparison.Ordinal)
                < sql.IndexOf("POSM_LinklyCloudCredential", StringComparison.Ordinal),
                "必须先拿锁再碰表");
        });
    }

    private sealed class CapturingLinklyCloudCredentialSchemaSqlExecutor : ILinklyCloudCredentialSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}

public sealed class CredentialSchemaSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public CredentialSchemaSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过 Linkly 凭据表并发初始化真实 SQL 测试。";
        }
    }
}

/// <summary>在真实 SQL Server 上模拟新库首次部署时多个初始化者并发建表。</summary>
[Trait("Category", "SQL")]
public sealed class LinklyCloudCredentialSchemaConcurrencySqlServerTests
{
    [CredentialSchemaSqlServerFact]
    public async Task Concurrent_initializers_on_a_new_database_all_succeed_and_build_one_table()
    {
        var configured = Environment.GetEnvironmentVariable(CredentialSchemaSqlServerFactAttribute.ConnectionVariable)!;
        var database = $"HbLinklyCredSchema_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteAsync(master, $"CREATE DATABASE [{database}]");
        try
        {
            var connection = new SqlConnectionStringBuilder(configured) { InitialCatalog = database }.ConnectionString;
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                var context = new HbposSqlSugarContext(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:MainConnection"] = connection,
                        ["ConnectionStrings:PosmConnection"] = connection,
                        ["Database:CommandTimeoutSeconds"] = "60",
                    }).Build(),
                    NullLogger<HbposSqlSugarContext>.Instance);
                await new SqlSugarLinklyCloudCredentialSchemaInitializer(
                    new SqlSugarLinklyCloudCredentialSchemaSqlExecutor(context)).InitializeAsync();
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Equal(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = N'POSM_LinklyCloudCredential'"));
        }
        finally
        {
            await ExecuteAsync(
                master,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
