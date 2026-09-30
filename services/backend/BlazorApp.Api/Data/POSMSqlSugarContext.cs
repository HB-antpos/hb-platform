using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Performance;
using BlazorApp.Service.Models.HBPOSM_POSM;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Data
{
    /// <summary>
    /// POSM 数据库上下文
    /// 用于访问 POSM 数据库
    /// </summary>
    public class POSMSqlSugarContext
    {
        private readonly ISqlSugarClient _db;

        public POSMSqlSugarContext(
            IConfiguration configuration,
            ICurrentUserService currentUserService,
            ILogger<POSMSqlSugarContext> logger
        )
        {
            // 获取POSM数据库连接字符串
            var connectionString =
                configuration.GetConnectionString("HBPOSMConnection")
                ?? throw new InvalidOperationException("HBPOSMConnection 连接字符串未配置");

            // 确保连接字符串包含MARS支持
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
            // ASP.NET Core 会在请求结束时自动释放 Scoped 服务的资源

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
                        // 统计一致快照必须遵守事务隔离级别，禁止全局 NOLOCK 绕过已提交版本。
                        DisableWithNoLockWithTran = true,
                    },
                }
            );

            // 设置命令超时时间（30分钟）
            _db.Ado.CommandTimeOut = 1800;

            // SQL 仅在显式调试开关打开时记录，默认避免输出完整语句和参数。
            if (configuration.GetValue<bool>("Database:EnableSqlLogging", false))
            {
                _db.Aop.OnLogExecuting = (sql, _) => logger.LogDebug("POSM SQL执行: {Sql}", sql);
            }

            _db.Aop.DataExecuting = (oldValue, entityInfo) =>
            {
                var username = currentUserService.GetCurrentUsername();

                switch (entityInfo.OperationType)
                {
                    case DataFilterType.InsertByObject:
                        if (
                            entityInfo.PropertyName == "CreatedBy"
                            && string.IsNullOrEmpty((string?)oldValue)
                        )
                            entityInfo.SetValue(username);
                        if (entityInfo.PropertyName == "CreatedTime")
                            entityInfo.SetValue(DateTime.Now);
                        if (
                            entityInfo.PropertyName == "UpdatedBy"
                            && string.IsNullOrEmpty((string?)oldValue)
                        )
                            entityInfo.SetValue(username);
                        if (entityInfo.PropertyName == "UpdatedTime")
                            entityInfo.SetValue(DateTime.Now);
                        break;
                    case DataFilterType.UpdateByObject:
                        if (entityInfo.PropertyName == "UpdatedBy")
                            entityInfo.SetValue(username);
                        if (entityInfo.PropertyName == "UpdatedTime")
                            entityInfo.SetValue(DateTime.Now);
                        if (entityInfo.PropertyName == "ModifiedBy")
                            entityInfo.SetValue(username);
                        break;
                }
            };

            SqlPerformanceAttachmentService.Attach(_db, nameof(POSMSqlSugarContext));
        }

        /// <summary>
        /// 获取 SqlSugar 客户端实例
        /// </summary>
        public ISqlSugarClient Db => _db;

        // 简化的数据访问接口
        /// <summary>
        /// POSM 设备注册信息表数据访问
        /// </summary>
        public SimpleClient<POSM_设备注册信息表> DeviceRegistrationDb =>
            new SimpleClient<POSM_设备注册信息表>(_db);

        /// <summary>
        /// 银行交易表数据访问
        /// </summary>
        public SimpleClient<BankTransaction> BankTransactionDb =>
            new SimpleClient<BankTransaction>(_db);

        /// <summary>
        /// 日结单表数据访问
        /// </summary>
        public SimpleClient<CashUp> CashUpDb => new SimpleClient<CashUp>(_db);

        /// <summary>
        /// 顾客信息表数据访问
        /// </summary>
        public SimpleClient<CustomerInfo> CustomerInfoDb => new SimpleClient<CustomerInfo>(_db);

        /// <summary>
        /// 支付明细表数据访问
        /// </summary>
        public SimpleClient<PaymentDetail> PaymentDetailDb => new SimpleClient<PaymentDetail>(_db);

        /// <summary>
        /// 销售订单主表数据访问
        /// </summary>
        public SimpleClient<SalesOrder> SalesOrderDb => new SimpleClient<SalesOrder>(_db);

        /// <summary>
        /// 销售订单明细表数据访问
        /// </summary>
        public SimpleClient<SalesOrderDetail> SalesOrderDetailDb =>
            new SimpleClient<SalesOrderDetail>(_db);

        /// <summary>
        /// 销售退货记录表数据访问
        /// </summary>
        public SimpleClient<SalesReturnRecord> SalesReturnRecordDb =>
            new SimpleClient<SalesReturnRecord>(_db);

        /// <summary>
        /// 商品-供应商映射表数据访问
        /// </summary>
        public SimpleClient<PosmProductSupplierMapping> PosmProductSupplierMappingDb =>
            new SimpleClient<PosmProductSupplierMapping>(_db);

        /// <summary>
        /// 版本信息表数据访问
        /// </summary>
        public SimpleClient<VersionInfo> VersionInfoDb => new SimpleClient<VersionInfo>(_db);

        /// <summary>
        /// 测试数据库连接
        /// </summary>
        /// <returns></returns>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                await _db.Ado.GetDataTableAsync("SELECT 1");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"POSM 数据库连接测试失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取数据库中所有表的信息
        /// </summary>
        /// <returns></returns>
        public async Task<List<dynamic>> GetTablesInfoAsync()
        {
            try
            {
                var sql =
                    @"
                    SELECT 
                        t.TABLE_NAME as TableName,
                        t.TABLE_TYPE as TableType,
                        ISNULL(ep.value, '') as TableComment
                    FROM INFORMATION_SCHEMA.TABLES t
                    LEFT JOIN sys.extended_properties ep ON ep.major_id = OBJECT_ID(t.TABLE_SCHEMA + '.' + t.TABLE_NAME)
                        AND ep.minor_id = 0 
                        AND ep.name = 'MS_Description'
                    WHERE t.TABLE_TYPE = 'BASE TABLE'
                    ORDER BY t.TABLE_NAME";

                return await _db.Ado.SqlQueryAsync<dynamic>(sql);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取POSM数据库表信息失败: {ex.Message}");
                return new List<dynamic>();
            }
        }

        /// <summary>
        /// 获取指定表的列信息
        /// </summary>
        /// <param name="tableName">表名</param>
        /// <returns></returns>
        public async Task<List<dynamic>> GetTableColumnsAsync(string tableName)
        {
            try
            {
                var sql =
                    @"
                    SELECT 
                        c.COLUMN_NAME as ColumnName,
                        c.DATA_TYPE as DataType,
                        c.IS_NULLABLE as IsNullable,
                        c.COLUMN_DEFAULT as DefaultValue,
                        c.CHARACTER_MAXIMUM_LENGTH as MaxLength,
                        CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 'YES' ELSE 'NO' END as IsPrimaryKey,
                        ISNULL(ep.value, '') as ColumnComment
                    FROM INFORMATION_SCHEMA.COLUMNS c
                    LEFT JOIN (
                        SELECT ku.TABLE_NAME, ku.COLUMN_NAME
                        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc
                        INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS ku
                            ON tc.CONSTRAINT_TYPE = 'PRIMARY KEY' 
                            AND tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                    ) pk ON c.TABLE_NAME = pk.TABLE_NAME AND c.COLUMN_NAME = pk.COLUMN_NAME
                    LEFT JOIN sys.extended_properties ep ON ep.major_id = OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME)
                        AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'ColumnId')
                        AND ep.name = 'MS_Description'
                    WHERE c.TABLE_NAME = @tableName
                    ORDER BY c.ORDINAL_POSITION";

                return await _db.Ado.SqlQueryAsync<dynamic>(sql, new { tableName });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取POSM数据库表 {tableName} 列信息失败: {ex.Message}");
                return new List<dynamic>();
            }
        }

        /// <summary>
        /// 执行自定义SQL查询
        /// </summary>
        /// <typeparam name="T">返回类型</typeparam>
        /// <param name="sql">SQL语句</param>
        /// <param name="parameters">参数</param>
        /// <returns></returns>
        public async Task<List<T>> QueryAsync<T>(string sql, object? parameters = null)
        {
            try
            {
                if (parameters != null)
                {
                    return await _db.Ado.SqlQueryAsync<T>(sql, parameters);
                }
                else
                {
                    return await _db.Ado.SqlQueryAsync<T>(sql);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"POSM数据库查询失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 执行自定义SQL命令
        /// </summary>
        /// <param name="sql">SQL语句</param>
        /// <param name="parameters">参数</param>
        /// <returns>受影响的行数</returns>
        public async Task<int> ExecuteAsync(string sql, object? parameters = null)
        {
            try
            {
                if (parameters != null)
                {
                    return await _db.Ado.ExecuteCommandAsync(sql, parameters);
                }
                else
                {
                    return await _db.Ado.ExecuteCommandAsync(sql);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"POSM数据库命令执行失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取数据库版本信息
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetDatabaseVersionAsync()
        {
            try
            {
                var result = await _db.Ado.GetStringAsync("SELECT @@VERSION");
                return result ?? "未知版本";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取POSM数据库版本失败: {ex.Message}");
                return "获取版本失败";
            }
        }

        /// <summary>
        /// 初始化数据库表
        /// 检查并创建不存在的表，更新表结构
        /// </summary>
        /// <returns></returns>
        public Task InitializeTablesAsync()
        {
            try
            {
                Console.WriteLine("[POSM DB] 开始检查和初始化数据库表...");

                // 定义需要初始化的表类型
                var tableTypes = new Type[]
                {
                    //  typeof(POSM_设备注册信息表),
                    typeof(PosmProductSupplierMapping),
                };

                // 检查并创建不存在的表
                foreach (var tableType in tableTypes)
                {
                    var tableName = _db.EntityMaintenance.GetTableName(tableType);

                    if (!_db.DbMaintenance.IsAnyTable(tableName))
                    {
                        Console.WriteLine($"[POSM DB] 表 {tableName} 不存在，正在创建...");
                        _db.CodeFirst.InitTables(tableType);
                        Console.WriteLine($"[POSM DB] ✓ {tableName} 表创建成功");
                    }
                    else
                    {
                        Console.WriteLine($"[POSM DB] ✓ {tableName} 表已存在");

                        // 检查是否需要更新表结构（非删除模式）
                        try
                        {
                            _db.CodeFirst.InitTables(tableType);
                            Console.WriteLine($"[POSM DB] ✓ {tableName} 表结构检查完成");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(
                                $"[POSM DB] ⚠️ {tableName} 表结构更新时出现警告: {ex.Message}"
                            );
                        }
                    }
                }

                Console.WriteLine("[POSM DB] 数据库表检查完成！");

                // 创建索引
                CreateIndexes();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM DB] 初始化表时出现错误: {ex.Message}");
                throw;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 创建性能优化索引
        /// </summary>
        public void CreateIndexes()
        {
            try
            {
                Console.WriteLine("[POSM DB] 开始创建索引...");

                var indexStatements = new[]
                {
                    // sales_order 表索引；时间索引包含 Status 供收银记录按状态汇总（生产 2026-09-19 已在线重建）
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_sales_order_OrderTime' AND object_id = OBJECT_ID('sales_order')) CREATE NONCLUSTERED INDEX IX_sales_order_OrderTime ON sales_order(OrderTime) INCLUDE (OrderGuid, BranchCode, DeviceCode, TotalAmount, DiscountAmount, ActualAmount, ItemCount, Status)",
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_sales_order_BranchCode' AND object_id = OBJECT_ID('sales_order')) CREATE NONCLUSTERED INDEX IX_sales_order_BranchCode ON sales_order(BranchCode) INCLUDE (OrderGuid, OrderTime, DeviceCode, TotalAmount, DiscountAmount, ActualAmount, ItemCount)",
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_sales_order_DeviceCode' AND object_id = OBJECT_ID('sales_order')) CREATE NONCLUSTERED INDEX IX_sales_order_DeviceCode ON sales_order(DeviceCode) INCLUDE (OrderGuid, OrderTime, BranchCode, TotalAmount, DiscountAmount, ActualAmount, ItemCount)",
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_sales_order_OrderTime_BranchCode' AND object_id = OBJECT_ID('sales_order')) CREATE NONCLUSTERED INDEX IX_sales_order_OrderTime_BranchCode ON sales_order(OrderTime, BranchCode) INCLUDE (OrderGuid, DeviceCode, TotalAmount, DiscountAmount, ActualAmount, ItemCount)",
                    // sales_order_detail 表索引；订单号索引包含 Quantity 供件数汇总（生产 2026-09-19 已在线重建）
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_sales_order_detail_OrderGuid' AND object_id = OBJECT_ID('sales_order_detail')) CREATE NONCLUSTERED INDEX IX_sales_order_detail_OrderGuid ON sales_order_detail(OrderGuid) INCLUDE (ProductCode, OrderDetailGuid, Quantity)",
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_sales_order_detail_OrderGuid_ProductCode' AND object_id = OBJECT_ID('sales_order_detail')) CREATE NONCLUSTERED INDEX IX_sales_order_detail_OrderGuid_ProductCode ON sales_order_detail(OrderGuid, ProductCode)",
                    // 收银记录关键词按商品编码定位订单；生产已于 2026-09-19 用 SqlScripts/PosmSalesOrderDetailProductCodeIndex.sql 在线创建。
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_sales_order_detail_ProductCode' AND object_id = OBJECT_ID('sales_order_detail')) CREATE NONCLUSTERED INDEX IX_sales_order_detail_ProductCode ON sales_order_detail(ProductCode) INCLUDE (OrderGuid)",
                    // 老系统操作日志按分店 + 时间查询；EmployeeLogs 由旧收银写入，表不存在时跳过。生产用 SqlScripts/PosmEmployeeLogsStoreTimeIndex.sql 在线创建。
                    "IF OBJECT_ID('EmployeeLogs') IS NOT NULL AND NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_EmployeeLogs_StoreCode_OperationTime' AND object_id = OBJECT_ID('EmployeeLogs')) CREATE NONCLUSTERED INDEX IX_EmployeeLogs_StoreCode_OperationTime ON EmployeeLogs(StoreCode, OperationTime) INCLUDE (EmployeeId, EmployeeName, Operation, DeviceCode)",
                };

                foreach (var sql in indexStatements)
                {
                    try
                    {
                        _db.Ado.ExecuteCommand(sql);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[POSM DB] 创建索引失败: {ex.Message}");
                    }
                }

                Console.WriteLine("[POSM DB] ✓ 索引创建完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM DB] 创建索引时出现错误: {ex.Message}");
                // 索引创建失败不影响主要功能，仅记录日志
            }
        }

        /// <summary>
        /// 强制重新创建所有表（危险操作，会删除现有数据）
        /// </summary>
        /// <returns></returns>
        public Task ForceRecreateTablesAsync()
        {
            try
            {
                Console.WriteLine("[POSM DB] ⚠️ 开始强制重新创建所有表（将删除现有数据）...");

                // 删除现有表
                try
                {
                    _db.DbMaintenance.DropTable<POSM_设备注册信息表>();
                    Console.WriteLine("[POSM DB] ✓ 删除 POSM_设备注册信息表");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[POSM DB] 删除表时出现错误（可能表不存在）: {ex.Message}");
                }

                try
                {
                    _db.DbMaintenance.DropTable<PosmProductSupplierMapping>();
                    Console.WriteLine("[POSM DB] ✓ 删除 posm_product_supplier_mapping");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[POSM DB] 删除表时出现错误（可能表不存在）: {ex.Message}");
                }

                // 重新创建表
                _db.CodeFirst.InitTables(
                    typeof(POSM_设备注册信息表),
                    typeof(PosmProductSupplierMapping)
                );
                Console.WriteLine(
                    "[POSM DB] ✓ POSM_设备注册信息表 和 posm_product_supplier_mapping 重新创建成功"
                );

                Console.WriteLine("[POSM DB] 所有表重新创建完成！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM DB] 重新创建表时出现错误: {ex.Message}");
                throw;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 获取数据库大小信息
        /// </summary>
        /// <returns></returns>
        public async Task<dynamic> GetDatabaseSizeAsync()
        {
            try
            {
                var sql =
                    @"
                    SELECT 
                        DB_NAME() as DatabaseName,
                        SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS bigint) * 8192.) / 1024 / 1024 as UsedSpaceMB,
                        SUM(CAST(size AS bigint) * 8192.) / 1024 / 1024 as AllocatedSpaceMB
                    FROM sys.database_files 
                    WHERE type_desc = 'ROWS'";

                var result = await _db.Ado.SqlQuerySingleAsync<dynamic>(sql);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取POSM数据库大小失败: {ex.Message}");
                return new
                {
                    DatabaseName = "POSM",
                    UsedSpaceMB = 0,
                    AllocatedSpaceMB = 0,
                };
            }
        }
    }
}
