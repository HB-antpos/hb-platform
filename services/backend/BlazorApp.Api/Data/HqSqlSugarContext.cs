using BlazorApp.Shared.Models.HqEntities;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Performance;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace BlazorApp.Api.Data
{
    /// <summary>
    /// HQ数据库上下文 - 用于连接HOT_HQ_CLOUD数据库
    /// </summary>
    public class HqSqlSugarContext
    {
        private readonly ISqlSugarClient _db;

        /// <summary>
        /// 配置对象，用于并发连接创建
        /// </summary>
        public IConfiguration Configuration { get; }

        public HqSqlSugarContext(
            IConfiguration configuration,
            ICurrentUserService currentUserService)
        {
            Configuration = configuration;
            // 确保HQ连接字符串包含MARS支持
            // HQ 断开后不读真实连接串，后续下线配置时这里也不会抛异常。
            var connectionString = !LegacyDatabaseGate.HqEnabled
                ? LegacyDatabaseGate.DisabledConnectionString
                : configuration.GetConnectionString("StoreHzgHQConnection")
                    ?? throw new InvalidOperationException("StoreHzgHQConnection 连接字符串未配置");
            if (
                !connectionString.Contains(
                    "MultipleActiveResultSets",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                connectionString += ";MultipleActiveResultSets=True";
            }

            // 使用 Scoped 生命周期管理连接
            // IsAutoCloseConnection = true 自动管理连接的打开和关闭，避免连接泄露

            _db = new SqlSugarClient(
                new ConnectionConfig()
                {
                    ConnectionString = connectionString,
                    DbType = DbType.SqlServer,
                    IsAutoCloseConnection = true,
                    InitKeyType = InitKeyType.Attribute,
                    MoreSettings = new ConnMoreSettings()
                    {
                        IsAutoRemoveDataCache = true,
                        IsWithNoLockQuery = true,
                    },
                }
            );

            // 设置命令超时时间（30分钟）
            _db.Ado.CommandTimeOut = 1800;

            // 调试模式 - 默认关闭时不注册逐SQL日志回调；启用后维持HQ SQL控制台输出
            if (configuration.GetValue<bool>("Database:EnableSqlLogging", false))
            {
                _db.Aop.OnLogExecuting = (sql, pars) =>
                {
                    Console.WriteLine($"[HQ Database] {sql}");
                };
            }

            _db.Aop.DataExecuting = (oldValue, entityInfo) =>
            {
                var username = currentUserService.GetCurrentUsername();

                switch (entityInfo.OperationType)
                {
                    case DataFilterType.InsertByObject:
                        if (entityInfo.PropertyName == "CreatedBy" && string.IsNullOrEmpty((string?)oldValue))
                            entityInfo.SetValue(username);
                        if (entityInfo.PropertyName == "CreatedAt")
                            entityInfo.SetValue(DateTime.UtcNow);
                        if (entityInfo.PropertyName == "UpdatedBy" && string.IsNullOrEmpty((string?)oldValue))
                            entityInfo.SetValue(username);
                        if (entityInfo.PropertyName == "UpdatedAt")
                            entityInfo.SetValue(DateTime.UtcNow);
                        break;
                    case DataFilterType.UpdateByObject:
                        if (entityInfo.PropertyName == "UpdatedBy")
                            entityInfo.SetValue(username);
                        if (entityInfo.PropertyName == "UpdatedAt")
                            entityInfo.SetValue(DateTime.UtcNow);
                        break;
                }
            };

            // 配置索引
            // ConfigureIndexes();
            SqlPerformanceAttachmentService.Attach(_db, nameof(HqSqlSugarContext));
        }

        public ISqlSugarClient Db => _db;

        // 简化的数据访问接口
        public SimpleClient<HqBranch> HqBranchDb => new SimpleClient<HqBranch>(_db);
        public SimpleClient<CBP_DIC_商品分类码表> CBP_DIC_商品分类码表Db =>
            new SimpleClient<CBP_DIC_商品分类码表>(_db);
        public SimpleClient<DIC_商品分类码表> DIC_商品分类码表Db =>
            new SimpleClient<DIC_商品分类码表>(_db);
        public SimpleClient<CBP_DIC_商品库存表> CBP_DIC_商品库存表Db =>
            new SimpleClient<CBP_DIC_商品库存表>(_db);
        public SimpleClient<CBP_DIC_国内供应商信息表> CBP_DIC_国内供应商信息表Db =>
            new SimpleClient<CBP_DIC_国内供应商信息表>(_db);
        public SimpleClient<CPT_DIC_商品信息字典表> CPT_DIC_商品信息字典表_HQDb =>
            new SimpleClient<CPT_DIC_商品信息字典表>(_db);
        public SimpleClient<CPT_DIC_货位编码信息表> CPT_DIC_货位编码信息表Db =>
            new SimpleClient<CPT_DIC_货位编码信息表>(_db);
        public SimpleClient<CPT_RED_货位存货信息表> CPT_RED_货位存货信息表Db =>
            new SimpleClient<CPT_RED_货位存货信息表>(_db);
        public SimpleClient<CPT_RED_货位配货信息表> CPT_RED_货位配货信息表Db =>
            new SimpleClient<CPT_RED_货位配货信息表>(_db);
        public SimpleClient<DIC_商品信息字典表> DIC_商品信息字典表Db =>
            new SimpleClient<DIC_商品信息字典表>(_db);
        public SimpleClient<DIC_一品多码表> DIC_一品多码表Db =>
            new SimpleClient<DIC_一品多码表>(_db);
        public SimpleClient<CPT_RED_货柜单主表Store> CPT_RED_货柜单主表Db =>
            new SimpleClient<CPT_RED_货柜单主表Store>(_db);
        public SimpleClient<CPT_RED_货柜单详情表Store> CPT_RED_货柜单详情表Db =>
            new SimpleClient<CPT_RED_货柜单详情表Store>(_db);

        public SimpleClient<DIC_供应商信息表> DIC_供应商信息表Db =>
            new SimpleClient<DIC_供应商信息表>(_db);

        // HQ专用表模型
        public SimpleClient<CPT_DIC_货号前缀信息表> CPT_DIC_货号前缀信息表Db =>
            new SimpleClient<CPT_DIC_货号前缀信息表>(_db);
        public SimpleClient<CPT_DIC_商品套装信息表> CPT_DIC_商品套装信息表Db =>
            new SimpleClient<CPT_DIC_商品套装信息表>(_db);

        public SimpleClient<RED_进货单主表Store> RED_进货单主表Db =>
            new SimpleClient<RED_进货单主表Store>(_db);
        public SimpleClient<RED_进货单详情表Store> RED_进货单详情表Db =>
            new SimpleClient<RED_进货单详情表Store>(_db);
        public SimpleClient<CBP_RED_分店订货单主表Store> CBP_RED_分店订货单主表Db =>
            new SimpleClient<CBP_RED_分店订货单主表Store>(_db);
        public SimpleClient<CBP_RED_分店订单详情表Store> CBP_RED_分店订单详情表Db =>
            new SimpleClient<CBP_RED_分店订单详情表Store>(_db);

        // 分店价格相关表
        public SimpleClient<DIC_商品零售价表> DIC_商品零售价表Db =>
            new SimpleClient<DIC_商品零售价表>(_db);
        public SimpleClient<DIC_商品清货价表> DIC_商品清货价表Db =>
            new SimpleClient<DIC_商品清货价表>(_db);
        public SimpleClient<DIC_分店一品多码表> DIC_分店一品多码表Db =>
            new SimpleClient<DIC_分店一品多码表>(_db);

        // 收银用户相关表
        public SimpleClient<DIC_收银用户信息表> DIC_收银用户信息表Db =>
            new SimpleClient<DIC_收银用户信息表>(_db);

        private void ConfigureIndexes()
        {
            // 配置默认字符串长度
            _db.CodeFirst.SetStringDefaultLength(200);
        }

        /// <summary>
        /// 检查HQ数据库连接状态
        /// </summary>
        public void CheckConnection()
        {
            try
            {
                Console.WriteLine("检查HQ数据库连接...");

                // 简单的连接测试
                _db.Ado.CheckConnection();
                Console.WriteLine("✓ HQ数据库连接成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HQ数据库连接失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 创建并发数据库连接（用于多线程环境）
        /// </summary>
        /// <param name="configuration">配置对象</param>
        /// <returns>独立的SqlSugar客户端实例</returns>
        public static ISqlSugarClient CreateConcurrentConnection(IConfiguration configuration)
        {
            // 确保HQ连接字符串包含MARS支持
            // HQ 断开后不读真实连接串，后续下线配置时这里也不会抛异常。
            var connectionString = !LegacyDatabaseGate.HqEnabled
                ? LegacyDatabaseGate.DisabledConnectionString
                : configuration.GetConnectionString("StoreHzgHQConnection")
                    ?? throw new InvalidOperationException("StoreHzgHQConnection 连接字符串未配置");
            if (
                !connectionString.Contains(
                    "MultipleActiveResultSets",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                connectionString += ";MultipleActiveResultSets=True";
            }
            if (!connectionString.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase))
            {
                var maxPoolSize = configuration.GetValue<int?>("Database:HqMaxPoolSize") ?? 100;
                connectionString += $";Max Pool Size={maxPoolSize}";
            }
            if (!connectionString.Contains("Min Pool Size", StringComparison.OrdinalIgnoreCase))
            {
                var minPoolSize = configuration.GetValue<int?>("Database:HqMinPoolSize") ?? 5;
                connectionString += $";Min Pool Size={minPoolSize}";
            }
            if (
                !connectionString.Contains(
                    "Connect Retry Count",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var connectRetryCount =
                    configuration.GetValue<int?>("Database:ConnectRetryCount") ?? 2;
                connectionString += $";Connect Retry Count={connectRetryCount}";
            }
            if (
                !connectionString.Contains(
                    "Connect Retry Interval",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var connectRetryInterval =
                    configuration.GetValue<int?>("Database:ConnectRetryIntervalSeconds") ?? 5;
                connectionString += $";Connect Retry Interval={connectRetryInterval}";
            }

            var client = new SqlSugarClient(
                new ConnectionConfig()
                {
                    ConnectionString = connectionString,
                    DbType = DbType.SqlServer,
                    IsAutoCloseConnection = true,
                    InitKeyType = InitKeyType.Attribute,
                    MoreSettings = new ConnMoreSettings()
                    {
                        IsAutoRemoveDataCache = true,
                        IsWithNoLockQuery = true,
                        SqlServerCodeFirstNvarchar = true,
                        DefaultCacheDurationInSeconds = 0,
                    },
                }
            );

            // 设置命令超时时间（5分钟）
            client.Ado.CommandTimeOut = 300;

            SqlPerformanceAttachmentService.Attach(
                client,
                $"{nameof(HqSqlSugarContext)}.Concurrent"
            );

            return client;
        }

        /// <summary>
        /// 检查HQ数据库表状态
        /// </summary>
        public void CheckTables()
        {
            try
            {
                Console.WriteLine("检查HQ数据库表状态...");

                var sql =
                    @"
                    SELECT 
                        t.name AS TableName,
                        s.name AS SchemaName
                    FROM sys.tables t
                    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE t.name LIKE '%分店%' OR t.name = 'DIC_分店信息表' OR t.name = 'HqBranches'
                    ORDER BY t.name";

                var tables = _db.Ado.SqlQuery<dynamic>(sql);

                Console.WriteLine("找到的分店相关表:");
                foreach (var table in tables)
                {
                    Console.WriteLine($"  - {table.SchemaName}.{table.TableName}");
                }

                Console.WriteLine("✓ HQ数据库表检查完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检查HQ表时出现错误: {ex.Message}");
            }
        }
    }
}
