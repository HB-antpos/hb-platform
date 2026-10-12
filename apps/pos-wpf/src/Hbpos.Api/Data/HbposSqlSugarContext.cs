using Microsoft.Data.SqlClient;
using SqlSugar;

namespace Hbpos.Api.Data;

public sealed class HbposSqlSugarContext
{
    /// <summary>
    /// 目录大结果集读取是否改用独立非 MARS 连接的开关（环境变量或配置项同名）。
    /// 默认开启；设为 false 即回退到共用的 MARS 连接，无需改代码。
    /// </summary>
    public const string CatalogNonMarsReadsSettingName = "HBPOS_CATALOG_NON_MARS_READS";

    // 单元测试会绕过构造函数创建实例（字段为默认值 false），此时一律走原路径。
    private readonly bool catalogNonMarsReadsEnabled;
    private readonly int commandTimeoutSeconds;

    public HbposSqlSugarContext(IConfiguration configuration, ILogger<HbposSqlSugarContext> logger)
    {
        catalogNonMarsReadsEnabled = !bool.TryParse(configuration[CatalogNonMarsReadsSettingName], out var enabled) || enabled;
        commandTimeoutSeconds = configuration.GetValue("Database:CommandTimeoutSeconds", 60);
        MainDb = CreateClient(configuration, logger, "MainConnection", "DefaultConnection");
        PosmDb = CreateClient(configuration, logger, "PosmConnection", "HBPOSMConnection");
    }

    public ISqlSugarClient MainDb { get; }

    public ISqlSugarClient PosmDb { get; }

    /// <summary>
    /// 为目录整表键集分页读取创建独立的非 MARS 主库客户端（调用方负责释放）。
    /// 生产连接串开了 MARS，Linux 上 SqlClient 在 MARS 下读大结果集只有每秒约 3 万行
    /// （见提交 92fa4a003 的生产实测）；同一连接串去掉 MARS 即可，其余全局设置与 <see cref="MainDb"/> 一致
    /// （IsAutoCloseConnection、IsWithNoLockQuery、命令超时）。
    /// 返回 null 表示应沿用 <see cref="MainDb"/> 原路径：开关关闭、非 SQL Server（SQLite 测试库）、
    /// 已有外部事务（独立连接看不到它）或连接串无法解析。
    /// </summary>
    public ISqlSugarClient? TryCreateMainNonMarsReadClient()
    {
        if (!catalogNonMarsReadsEnabled
            || MainDb.CurrentConnectionConfig.DbType != DbType.SqlServer
            || MainDb.Ado.Transaction != null)
        {
            return null;
        }

        string connectionString;
        try
        {
            connectionString = new SqlConnectionStringBuilder(MainDb.CurrentConnectionConfig.ConnectionString)
            {
                MultipleActiveResultSets = false
            }.ConnectionString;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true,
            MoreSettings = new ConnMoreSettings
            {
                IsWithNoLockQuery = true
            }
        });
        client.Ado.CommandTimeOut = commandTimeoutSeconds;
        return client;
    }

    private static ISqlSugarClient CreateClient(
        IConfiguration configuration,
        ILogger logger,
        string primaryName,
        string fallbackName)
    {
        var connectionString = configuration.GetConnectionString(primaryName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration.GetConnectionString(fallbackName);
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"缺少数据库连接字符串：ConnectionStrings:{primaryName}");
        }

        if (!connectionString.Contains("MultipleActiveResultSets", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";MultipleActiveResultSets=True";
        }

        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true,
            MoreSettings = new ConnMoreSettings
            {
                IsWithNoLockQuery = true
            }
        });

        client.Ado.CommandTimeOut = configuration.GetValue("Database:CommandTimeoutSeconds", 60);
        client.Aop.OnLogExecuting = (sql, _) =>
        {
            if (configuration.GetValue("Database:EnableSqlLogging", false))
            {
                logger.LogDebug("SQL: {Sql}", sql);
            }
        };

        return client;
    }
}
