using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Api.Services.Performance;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace BlazorApp.Api.Data
{
    public class HBSalesRecordSqlSugarContext
    {
        private readonly SqlSugarScope _db;

        public HBSalesRecordSqlSugarContext(IConfiguration configuration)
        {
            var connectionString = !LegacyDatabaseGate.HBSalesEnabled
                ? LegacyDatabaseGate.DisabledConnectionString
                : configuration.GetConnectionString("HBSalesRecord")
                    ?? throw new InvalidOperationException("HBSalesRecord 连接字符串未配置");

            // 使用 Scoped 生命周期管理连接
            // IsAutoCloseConnection = true 自动管理连接的打开和关闭，避免连接泄露

            _db = new SqlSugarScope(
                new ConnectionConfig()
                {
                    ConnectionString = connectionString,
                    DbType = DbType.SqlServer,
                    IsAutoCloseConnection = true,
                    ConfigureExternalServices = new ConfigureExternalServices()
                    {
                        EntityNameService = (type, entity) =>
                        {
                            if (entity.DbTableName == null)
                            {
                                entity.DbTableName = type.Name;
                            }
                        },
                    },
                },
                db =>
                {
                    db.Aop.OnLogExecuting = (sql, pars) =>
                    {
                        Console.WriteLine($"[HBSalesRecord] SQL: {sql}");
                    };
                }
            );

            SqlPerformanceAttachmentService.Attach(_db, nameof(HBSalesRecordSqlSugarContext));
        }

        /// <summary>
        /// 供本地测试注入已隔离的 SqlSugar 连接；生产仍使用 IConfiguration 构造函数。
        /// </summary>
        public HBSalesRecordSqlSugarContext(SqlSugarScope db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            SqlPerformanceAttachmentService.Attach(_db, nameof(HBSalesRecordSqlSugarContext));
        }

        public SqlSugarScope Db => _db;

        public SimpleClient<SalesOrderMain> SalesOrderMainDb =>
            new SimpleClient<SalesOrderMain>(_db);

        public SimpleClient<SalesOrderDetailRecord> SalesOrderDetailRecordDb =>
            new SimpleClient<SalesOrderDetailRecord>(_db);

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var result = await _db.Ado.GetStringAsync("SELECT @@VERSION");
                return !string.IsNullOrEmpty(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSalesRecord] 数据库连接测试失败: {ex.Message}");
                return false;
            }
        }

        public async Task<List<string>> GetTablesAsync()
        {
            try
            {
                var tables = await Task.FromResult(_db.DbMaintenance.GetTableInfoList());
                return tables.Select(t => t.Name).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSalesRecord] 获取表信息失败: {ex.Message}");
                return new List<string>();
            }
        }

        public async Task<bool> TableExistsAsync(string tableName)
        {
            try
            {
                return await Task.FromResult(_db.DbMaintenance.IsAnyTable(tableName));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSalesRecord] 检查表{tableName}是否存在失败: {ex.Message}");
                return false;
            }
        }

        public async Task<int> GetTableCountAsync(string tableName)
        {
            try
            {
                var sql = $"SELECT COUNT(*) FROM [{tableName}]";
                return await _db.Ado.GetIntAsync(sql);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSalesRecord] 获取表{tableName}记录数失败: {ex.Message}");
                return -1;
            }
        }

        public void Dispose()
        {
            _db?.Dispose();
        }
    }
}
