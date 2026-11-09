using BlazorApp.Shared.Models.HqEntities;
using BlazorApp.Api.Services.Performance;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace BlazorApp.Api.Data
{
    /// <summary>
    /// HBSales数据库上下文 - HOT_CPT_CLOUD数据库
    /// 专门用于访问CPT相关表，如货号前缀信息表、商品套装信息表等
    /// </summary>
    public class HBSalesSqlSugarContext
    {
        private readonly SqlSugarScope _db;

        public HBSalesSqlSugarContext(IConfiguration configuration)
        {
            var connectionString = LegacyDatabaseGate.HBSalesEnabled
                ? configuration.GetConnectionString("HBSalesConnection")
                : LegacyDatabaseGate.DisabledConnectionString;

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
                            // 确保表名使用SqlSugar特性中定义的名称
                            if (entity.DbTableName == null)
                            {
                                entity.DbTableName = type.Name;
                            }
                        },
                    },
                },
                db =>
                {
                    // 配置日志（可选）
                    db.Aop.OnLogExecuting = (sql, pars) =>
                    {
                        Console.WriteLine($"[HBSales] SQL: {sql}");
                    };
                }
            );

            SqlPerformanceAttachmentService.Attach(_db, nameof(HBSalesSqlSugarContext));
        }

        /// <summary>
        /// 获取SqlSugar数据库实例
        /// </summary>
        public SqlSugarScope Db => _db;

        /// <summary>
        /// CPT货号前缀信息表访问器
        /// </summary>
        public SimpleClient<CPT_DIC_货号前缀信息表> CPT_DIC_货号前缀信息表Db =>
            new SimpleClient<CPT_DIC_货号前缀信息表>(_db);

        /// <summary>
        /// CPT商品套装信息表访问器
        /// </summary>
        public SimpleClient<CPT_DIC_商品套装信息表> CPT_DIC_商品套装信息表Db =>
            new SimpleClient<CPT_DIC_商品套装信息表>(_db);

        /// <summary>
        /// CPT商品信息字典表访问器
        /// </summary>
        public SimpleClient<CPT_DIC_商品信息字典表> CPT_DIC_商品信息字典表Db =>
            new SimpleClient<CPT_DIC_商品信息字典表>(_db);

        /// <summary>
        /// CBP国内供应商信息表访问器
        /// </summary>
        public SimpleClient<CBP_DIC_国内供应商信息表> CBP_DIC_国内供应商信息表Db =>
            new SimpleClient<CBP_DIC_国内供应商信息表>(_db);

        /// <summary>
        /// 测试数据库连接
        /// </summary>
        /// <returns>连接是否成功</returns>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var result = await _db.Ado.GetStringAsync("SELECT @@VERSION");
                return !string.IsNullOrEmpty(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSales] 数据库连接测试失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取数据库表信息
        /// </summary>
        /// <returns>表信息列表</returns>
        public async Task<List<string>> GetTablesAsync()
        {
            try
            {
                var tables = await Task.FromResult(_db.DbMaintenance.GetTableInfoList());
                return tables.Select(t => t.Name).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSales] 获取表信息失败: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 检查表是否存在
        /// </summary>
        /// <param name="tableName">表名</param>
        /// <returns>是否存在</returns>
        public async Task<bool> TableExistsAsync(string tableName)
        {
            try
            {
                return await Task.FromResult(_db.DbMaintenance.IsAnyTable(tableName));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSales] 检查表{tableName}是否存在失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取表记录数
        /// </summary>
        /// <param name="tableName">表名</param>
        /// <returns>记录数</returns>
        public async Task<int> GetTableCountAsync(string tableName)
        {
            try
            {
                var sql = $"SELECT COUNT(*) FROM [{tableName}]";
                return await _db.Ado.GetIntAsync(sql);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HBSales] 获取表{tableName}记录数失败: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _db?.Dispose();
        }
    }
}
