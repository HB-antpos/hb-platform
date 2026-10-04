using System.Threading.Tasks;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Performance;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Data
{
    public class SqlSugarContext
    {
        private readonly ISqlSugarClient _db;
        private readonly ILogger<SqlSugarContext> _logger;
        private readonly bool _syncExistingTableStructureOnStartup;

        public SqlSugarContext(
            IConfiguration configuration,
            ILogger<SqlSugarContext> logger,
            ICurrentUserService currentUserService
        )
        {
            _logger = logger;
            _syncExistingTableStructureOnStartup = configuration.GetValue<bool>(
                "Database:SyncExistingTableStructureOnStartup",
                false
            );

            var connectionString =
                configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection 连接字符串未配置");

            // 自动检测数据库类型
            var dbType = GetDatabaseType(connectionString);

            // 仅SQL Server需要MARS支持
            if (
                dbType == DbType.SqlServer
                && !connectionString.Contains(
                    "MultipleActiveResultSets",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                connectionString += ";MultipleActiveResultSets=True";
            }

            // SQL Server 连接池和超时优化
            if (dbType == DbType.SqlServer)
            {
                if (
                    !connectionString.Contains(
                        "Connect Timeout",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    connectionString += ";Connect Timeout=30";
                }
                if (
                    !connectionString.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase)
                )
                {
                    connectionString += ";Max Pool Size=200";
                }
            }

            // 使用 Scoped 生命周期管理连接
            // IsAutoCloseConnection = false 由 ASP.NET Core 的 Scoped 生命周期管理
            // ASP.NET Core 会在请求结束时自动释放 Scoped 服务的资源
            // 避免并发查询时连接被自动关闭导致"连接被关闭"错误

            _db = new SqlSugarClient(
                new ConnectionConfig()
                {
                    ConnectionString = connectionString,
                    DbType = dbType,
                    IsAutoCloseConnection = true,
                    InitKeyType = InitKeyType.Attribute,
                    MoreSettings = new ConnMoreSettings()
                    {
                        IsAutoRemoveDataCache = true,
                        IsWithNoLockQuery = dbType == DbType.SqlServer, // ⭐ PostgreSQL不支持WITH(NOLOCK)
                        // 关键逻辑：事务内必须遵循隔离级别持锁，避免授权读取被 NOLOCK 绕过。
                        DisableWithNoLockWithTran = true,
                        // 🚀 优化并发性能配置
                        SqlServerCodeFirstNvarchar = dbType == DbType.SqlServer,
                        DefaultCacheDurationInSeconds = 600,
                    },
                    // 🚀 连接池配置优化 - 支持高并发
                    ConfigureExternalServices = new ConfigureExternalServices()
                    {
                        EntityService = (c, p) =>
                        {
                            // PostgreSQL 兼容性处理
                            if (dbType == DbType.PostgreSQL && !string.IsNullOrEmpty(p.DataType))
                            {
                                // 1. 将 nvarchar 映射为 varchar
                                if (
                                    p.DataType.Contains(
                                        "nvarchar",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    p.DataType = p.DataType.Replace(
                                        "nvarchar",
                                        "varchar",
                                        StringComparison.OrdinalIgnoreCase
                                    );
                                }

                                // 2. 将 (max) 映射为 text (PG不支持varchar(max))
                                if (
                                    p.DataType.Contains("(max)", StringComparison.OrdinalIgnoreCase)
                                )
                                {
                                    p.DataType = "text";
                                }
                            }
                        },
                    },
                }
            );

            // 设置命令超时时间（从配置读取，默认60秒）
            _db.Ado.CommandTimeOut = configuration.GetValue<int>(
                "Database:CommandTimeoutSeconds",
                60
            );

            // 调试模式 - 基于配置开关记录SQL日志
            _db.Aop.OnLogExecuting = (sql, pars) =>
            {
                if (configuration.GetValue<bool>("Database:EnableSqlLogging", false))
                {
                    _logger.LogDebug("SQL执行: {Sql}", sql);
                }
            };

            _db.Aop.DataExecuting = (oldValue, entityInfo) =>
            {
                if (entityInfo.EntityValue is not BaseEntity)
                    return;

                if (
                    SqlSugarAuditScope.ShouldPreserveExplicitAuditFields
                    && IsAuditField(entityInfo.PropertyName)
                )
                    return;

                var username = currentUserService.GetCurrentUsername();

                switch (entityInfo.OperationType)
                {
                    case DataFilterType.InsertByObject:
                        if (
                            entityInfo.PropertyName == "CreatedBy"
                            && string.IsNullOrEmpty((string?)oldValue)
                        )
                            entityInfo.SetValue(username);
                        if (entityInfo.PropertyName == "CreatedAt")
                            entityInfo.SetValue(DateTime.UtcNow);
                        if (
                            entityInfo.PropertyName == "UpdatedBy"
                            && string.IsNullOrEmpty((string?)oldValue)
                        )
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

            SqlPerformanceAttachmentService.Attach(_db, nameof(SqlSugarContext));
        }

        private static bool IsAuditField(string? propertyName)
        {
            return propertyName is "CreatedAt" or "UpdatedAt" or "CreatedBy" or "UpdatedBy";
        }

        public ISqlSugarClient Db => _db;

        // 简化的数据访问接口
        public SimpleClient<User> UserDb => new SimpleClient<User>(_db);
        public SimpleClient<Role> RoleDb => new SimpleClient<Role>(_db);
        public SimpleClient<Store> StoreDb => new SimpleClient<Store>(_db);
        public SimpleClient<UserRole> UserRoleDb => new SimpleClient<UserRole>(_db);
        public SimpleClient<UserStore> UserStoreDb => new SimpleClient<UserStore>(_db);
        public SimpleClient<RefreshToken> RefreshTokenDb => new SimpleClient<RefreshToken>(_db);
        public SimpleClient<Product> ProductDb => new SimpleClient<Product>(_db);

        public SimpleClient<WarehouseCategory> WarehouseCategoryDb =>
            new SimpleClient<WarehouseCategory>(_db);
        public SimpleClient<ProductCategory> ProductCategoryDb =>
            new SimpleClient<ProductCategory>(_db);
        public SimpleClient<WarehouseProduct> WarehouseProductDb =>
            new SimpleClient<WarehouseProduct>(_db);

        // 购物车相关实体
        public SimpleClient<Cart> CartDb => new SimpleClient<Cart>(_db);
        public SimpleClient<CartItem> CartItemDb => new SimpleClient<CartItem>(_db);

        // 新添加的实体
        public SimpleClient<ChinaSupplier> ChinaSupplierDb => new SimpleClient<ChinaSupplier>(_db);
        public SimpleClient<Location> LocationDb => new SimpleClient<Location>(_db);
        public SimpleClient<ProductLocation> ProductLocationDb =>
            new SimpleClient<ProductLocation>(_db);

        public SimpleClient<HBLocalSupplier> HBLocalSupplierDb =>
            new SimpleClient<HBLocalSupplier>(_db);

        // 义乌订单相关实体
        public SimpleClient<YIWU_Order> YiwuOrderDb => new SimpleClient<YIWU_Order>(_db);
        public SimpleClient<YIWU_OrderDetail> YiwuOrderDetailDb =>
            new SimpleClient<YIWU_OrderDetail>(_db);

        // 国内商品相关实体
        public SimpleClient<DomesticProduct> DomesticProductDb =>
            new SimpleClient<DomesticProduct>(_db);
        public SimpleClient<ProductPrefixCode> ProductPrefixCodeDb =>
            new SimpleClient<ProductPrefixCode>(_db);
        public SimpleClient<ProductSetCode> ProductSetCodeDb =>
            new SimpleClient<ProductSetCode>(_db);
        public SimpleClient<DomesticSetProduct> DomesticSetProductDb =>
            new SimpleClient<DomesticSetProduct>(_db);
        public SimpleClient<DomesticProductCreationLog> DomesticProductCreationLogDb =>
            new SimpleClient<DomesticProductCreationLog>(_db);
        public SimpleClient<DomesticSetTemplate> DomesticSetTemplateDb =>
            new SimpleClient<DomesticSetTemplate>(_db);
        public SimpleClient<DomesticSetTemplateItem> DomesticSetTemplateItemDb =>
            new SimpleClient<DomesticSetTemplateItem>(_db);

        // 货柜相关实体
        public SimpleClient<Container> ContainerDb => new SimpleClient<Container>(_db);
        public SimpleClient<ContainerDetail> ContainerDetailDb =>
            new SimpleClient<ContainerDetail>(_db);

        // 分店价格相关实体
        public SimpleClient<StoreRetailPrice> StoreRetailPriceDb =>
            new SimpleClient<StoreRetailPrice>(_db);
        public SimpleClient<StoreClearancePrice> StoreClearancePriceDb =>
            new SimpleClient<StoreClearancePrice>(_db);
        public SimpleClient<StoreMultiCodeProduct> StoreMultiCodeProductDb =>
            new SimpleClient<StoreMultiCodeProduct>(_db);

        public SimpleClient<StoreLocalSupplierInvoice> StoreLocalSupplierInvoiceDb =>
            new SimpleClient<StoreLocalSupplierInvoice>(_db);
        public SimpleClient<StoreLocalSupplierInvoiceDetails> StoreLocalSupplierInvoiceDetailsDb =>
            new SimpleClient<StoreLocalSupplierInvoiceDetails>(_db);
        public SimpleClient<WareHouseOrder> WareHouseOrderDb =>
            new SimpleClient<WareHouseOrder>(_db);
        public SimpleClient<WareHouseOrderDetails> WareHouseOrderDetailsDb =>
            new SimpleClient<WareHouseOrderDetails>(_db);

        // Preorder 使用独立订单表，避免进入普通仓库订单的拣货与同步链路。
        public SimpleClient<PreorderTemplate> PreorderTemplateDb => new(_db);
        public SimpleClient<PreorderTemplateItem> PreorderTemplateItemDb => new(_db);
        public SimpleClient<PreorderTemplateStore> PreorderTemplateStoreDb => new(_db);
        public SimpleClient<PreorderActivation> PreorderActivationDb => new(_db);
        public SimpleClient<PreorderActivationItem> PreorderActivationItemDb => new(_db);
        public SimpleClient<PreorderActivationStore> PreorderActivationStoreDb => new(_db);
        public SimpleClient<PreorderWarehouseOrder> PreorderWarehouseOrderDb => new(_db);
        public SimpleClient<PreorderWarehouseOrderItem> PreorderWarehouseOrderItemDb => new(_db);

        // 自动定价策略相关实体
        public SimpleClient<PricingStrategy> PricingStrategyDb =>
            new SimpleClient<PricingStrategy>(_db);
        public SimpleClient<PricingStrategyDetail> PricingStrategyDetailDb =>
            new SimpleClient<PricingStrategyDetail>(_db);
        public SimpleClient<PricingStrategyTarget> PricingStrategyTargetDb =>
            new SimpleClient<PricingStrategyTarget>(_db);

        // 促销相关实体
        public SimpleClient<Advertisement> AdvertisementDb => new SimpleClient<Advertisement>(_db);
        public SimpleClient<AdvertisementStore> AdvertisementStoreDb =>
            new SimpleClient<AdvertisementStore>(_db);
        public SimpleClient<Promotion> PromotionDb => new SimpleClient<Promotion>(_db);
        public SimpleClient<PromotionProduct> PromotionProductDb =>
            new SimpleClient<PromotionProduct>(_db);
        public SimpleClient<PromotionStore> PromotionStoreDb =>
            new SimpleClient<PromotionStore>(_db);

        // 权限管理实体
        public SimpleClient<SysPermission> SysPermissionDb => new SimpleClient<SysPermission>(_db);
        public SimpleClient<SysRolePermission> SysRolePermissionDb =>
            new SimpleClient<SysRolePermission>(_db);
        public SimpleClient<SysUserPermission> SysUserPermissionDb =>
            new SimpleClient<SysUserPermission>(_db);
        public SimpleClient<SysUserStorePosPermission> SysUserStorePosPermissionDb =>
            new SimpleClient<SysUserStorePosPermission>(_db);

        // 收银用户相关实体
        public SimpleClient<CashRegisterUser> CashRegisterUserDb =>
            new SimpleClient<CashRegisterUser>(_db);

        // 销售统计相关实体
        public SimpleClient<DailySalesStatistic> DailySalesStatisticDb =>
            new SimpleClient<DailySalesStatistic>(_db);
        public SimpleClient<HourlySalesStatistic> HourlySalesStatisticDb =>
            new SimpleClient<HourlySalesStatistic>(_db);
        public SimpleClient<StoreSalesStatistic> StoreSalesStatisticDb =>
            new SimpleClient<StoreSalesStatistic>(_db);
        public SimpleClient<SupplierSalesStatistic> SupplierSalesStatisticDb =>
            new SimpleClient<SupplierSalesStatistic>(_db);
        public SimpleClient<StoreSupplierSalesDetail> StoreSupplierSalesDetailDb =>
            new SimpleClient<StoreSupplierSalesDetail>(_db);
        public SimpleClient<AustralianSupplierStoreSalesDetail> AustralianSupplierStoreSalesDetailDb =>
            new SimpleClient<AustralianSupplierStoreSalesDetail>(_db);
        public SimpleClient<ChinaSupplierStoreSalesDetail> ChinaSupplierStoreSalesDetailDb =>
            new SimpleClient<ChinaSupplierStoreSalesDetail>(_db);
        public SimpleClient<ProductStoreDailySalesStatistic> ProductStoreDailySalesStatisticDb =>
            new SimpleClient<ProductStoreDailySalesStatistic>(_db);
        public SimpleClient<SalesStatisticRefreshState> SalesStatisticRefreshStateDb =>
            new SimpleClient<SalesStatisticRefreshState>(_db);

        // 定时任务日志实体
        public SimpleClient<ScheduledTaskLog> ScheduledTaskLogDb =>
            new SimpleClient<ScheduledTaskLog>(_db);
        public SimpleClient<ScheduledTaskLease> ScheduledTaskLeaseDb =>
            new SimpleClient<ScheduledTaskLease>(_db);

        // 发票邮件 SMTP 配置实体
        public SimpleClient<InvoiceEmailConfiguration> InvoiceEmailConfigurationDb =>
            new SimpleClient<InvoiceEmailConfiguration>(_db);

        // 多端中心应用日志实体
        public SimpleClient<ApplicationLog> ApplicationLogDb => new SimpleClient<ApplicationLog>(_db);
        public SimpleClient<MobileAppBuild> MobileAppBuildDb =>
            new SimpleClient<MobileAppBuild>(_db);
        public SimpleClient<MobileAppDeviceStatus> MobileAppDeviceStatusDb =>
            new SimpleClient<MobileAppDeviceStatus>(_db);
        public SimpleClient<MobileAppOtaUpdate> MobileAppOtaUpdateDb =>
            new SimpleClient<MobileAppOtaUpdate>(_db);
        public SimpleClient<ServiceApiToken> ServiceApiTokenDb =>
            new SimpleClient<ServiceApiToken>(_db);

        // 节日商品相关实体
        public SimpleClient<HolidayProduct> HolidayProductDb =>
            new SimpleClient<HolidayProduct>(_db);
        public SimpleClient<SeasonalCardCatalog> SeasonalCardCatalogDb =>
            new SimpleClient<SeasonalCardCatalog>(_db);
        public SimpleClient<SeasonalCardRemainingSubmission> SeasonalCardRemainingSubmissionDb =>
            new SimpleClient<SeasonalCardRemainingSubmission>(_db);

        // 义乌订单相关
        public SimpleClient<YIWU_Order> YIWU_OrderDb => new SimpleClient<YIWU_Order>(_db);
        public SimpleClient<YIWU_OrderDetail> YIWU_OrderDetailDb =>
            new SimpleClient<YIWU_OrderDetail>(_db);

        private void ConfigureIndexes()
        {
            // 配置GUID字段的索引
            _db.CodeFirst.SetStringDefaultLength(200); // 设置默认字符串长度
        }

        public void CreateTable()
        {
            try
            {
                Console.WriteLine("开始检查和初始化数据库表...");

                // 智能初始化：只在需要时创建或更新表
                InitializeTablesIfNeeded();
                PreorderSchemaBootstrap.EnsureIndexesAsync(_db).GetAwaiter().GetResult();
                EnsureDomesticSetProductNameColumn();
                EnsureEmployeeProfilePhoneColumn();
                EnsureUserLastLoginIpColumn();
                EnsureLocalSupplierImageBaseUrlColumn();
                EnsureStoreContactEmailColumn();
                EnsureSalesStatisticRefreshStateJobColumns();
                EnsureSupplierStatisticColumns();
                EnsureInvoiceEmailConfigurationMultiAccountSchema();
                EnsureContainerDetailSchemaColumns();
                EnsureDomesticSetTemplateEnabledSupplierNameUniqueIndex();
                CreateIndexesForCurrentDatabase();
                EnsureStoreLocalSupplierInvoiceBusinessLookupIndex();

                Console.WriteLine("数据库表检查完成！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化表时出现错误: {ex.Message}");
                Console.WriteLine($"错误详情: {ex.StackTrace}");
                throw;
            }
        }

        private void CreateIndexesForCurrentDatabase()
        {
            // 正常建表流程按当前方言进入幂等索引清单；SQL Server 保持原有创建路径不变。
            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                CreateNormalIndexes();
            }
            else if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                CreatePostgreSQLIndexesIfNotExists();
            }
            else if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                CreateSqliteIndexesIfNotExists();
            }
        }

        public void EnsureLoginSessionSchema()
        {
            EnsureUserLastLoginIpColumn();
            EnsureRefreshTokenUserIpAddressIndex();
            EnsureContainerDetailSchemaColumns();
        }

        public void ForceRecreateAllTables()
        {
            try
            {
                Console.WriteLine("强制重新创建所有表（GUID主键）...");
                // 创建索引
                CreateIndexes();

                Console.WriteLine("所有表重新创建完成（GUID主键）！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"重新创建表时出现错误: {ex.Message}");
                throw;
            }
        }

        private void InitializeTablesIfNeeded()
        {
            // 修改历史首次建表必须和后续索引迁移使用同一跨实例锁，避免多实例同时 CodeFirst 建表。
            InitializeWarehouseProductChangeHistoryTable();

            // 定义表的创建顺序（考虑外键依赖）
            var tableTypes = new Type[]
            {
                typeof(User),
                typeof(Role),
                typeof(Store),
                typeof(UserRole),
                typeof(UserStore),
                typeof(RefreshToken),
                typeof(WarehouseCategory),
                typeof(WarehouseProduct),
                typeof(Product),
                typeof(Cart),
                typeof(CartItem),
                typeof(ChinaSupplier),
                typeof(HBLocalSupplier),
                typeof(DomesticProduct),
                typeof(ItemBarcodeReservation),
                typeof(SysPermission),
                typeof(SysRolePermission),
                typeof(SysUserPermission),
                typeof(SysUserStorePosPermission),
                typeof(ProductPrefixCode),
                typeof(ProductSetCode),
                typeof(DomesticSetProduct),
                typeof(DomesticProductCreationLog), // ⭐ 新增：商品创建记录表
                typeof(DomesticSetTemplate),
                typeof(DomesticSetTemplateItem),
                typeof(Location),
                typeof(ProductLocation),
                typeof(Container),
                typeof(ContainerDetail),
                typeof(YIWU_Order),
                typeof(YIWU_OrderDetail),
                typeof(StoreRetailPrice),
                typeof(StoreClearancePrice),
                typeof(StoreMultiCodeProduct),
                typeof(StoreLocalSupplierInvoice),
                typeof(StoreLocalSupplierInvoiceDetails),
                typeof(WareHouseOrder),
                typeof(WareHouseOrderDetails),
                typeof(PreorderTemplate),
                typeof(PreorderTemplateItem),
                typeof(PreorderTemplateStore),
                typeof(PreorderActivation),
                typeof(PreorderActivationItem),
                typeof(PreorderActivationStore),
                typeof(PreorderWarehouseOrder),
                typeof(PreorderWarehouseOrderItem),
                typeof(PricingStrategy),
                typeof(PricingStrategyDetail),
                typeof(PricingStrategyTarget),
                typeof(Advertisement),
                typeof(AdvertisementStore),
                typeof(Promotion),
                typeof(PromotionProduct),
                typeof(PromotionStore),
                typeof(SysPermission),
                typeof(SysRolePermission),
                typeof(SysUserPermission),
                typeof(SysUserStorePosPermission),
                typeof(CashRegisterUser),
                typeof(DailySalesStatistic),
                typeof(HourlySalesStatistic),
                typeof(StoreSalesStatistic),
                typeof(SupplierSalesStatistic),
                typeof(StoreSupplierSalesDetail),
                typeof(AustralianSupplierStoreSalesDetail),
                typeof(ChinaSupplierStoreSalesDetail),
                typeof(ProductStoreDailySalesStatistic),
                typeof(SalesStatisticRefreshState),
                typeof(ScheduledTaskLog),
                typeof(ScheduledTaskRuntimeControl),
                typeof(ScheduledTaskInstanceState),
                typeof(ScheduledTaskLease),
                typeof(InvoiceEmailConfiguration),
                typeof(StoreOrderInvoiceEmailSendRecord),
                typeof(ApplicationLog),
                typeof(MobileAppBuild),
                typeof(MobileAppDeviceStatus),
                typeof(MobileAppOtaUpdate),
                typeof(ServiceApiToken),
                typeof(HolidayProduct),
                typeof(ProductCategory),
                typeof(ProductGrade),
                typeof(EmployeeProfile),
                typeof(UserLoginDeviceRecord),
                typeof(AttendanceSchedule),
                typeof(AttendanceAvailability),
                typeof(AttendancePunch),
                typeof(AttendanceLocationSample),
                typeof(AttendanceApproval),
                typeof(AttendancePunchAdjustment),
                typeof(AttendanceStoreHoliday),
                typeof(AttendanceLeaveRequest),
                typeof(AttendanceSettings),
                typeof(SeasonalCardCatalog),
                typeof(SeasonalCardRemainingSubmission),
            };

            var existingTables = GetExistingTableNames();

            // 检查并创建不存在的表
            foreach (var tableType in tableTypes)
            {
                var tableName = _db.EntityMaintenance.GetTableName(tableType);

                if (!existingTables.Contains(tableName))
                {
                    Console.WriteLine($"表 {tableName} 不存在，正在创建...");
                    _db.CodeFirst.InitTables(tableType);
                    Console.WriteLine($"✓ {tableName} 表创建成功");
                    existingTables.Add(tableName);
                }
                else
                {
                    Console.WriteLine($"✓ {tableName} 表已存在");

                    if (!_syncExistingTableStructureOnStartup)
                    {
                        continue;
                    }

                    try
                    {
                        CleanupLegacyIndexesBeforeSmartInit(tableName);

                        // 仅在显式开启时，对已有表执行结构同步。
                        _db.CodeFirst.InitTables(tableType);
                        Console.WriteLine($"✓ {tableName} 表结构检查完成");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ {tableName} 表结构更新时出现警告: {ex.Message}");
                    }
                }
            }
        }

        private void InitializeWarehouseProductChangeHistoryTable()
        {
            if (_db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            {
                InitializeWarehouseProductChangeHistoryTableUnderLock();
                return;
            }

            _db.Ado.BeginTran();
            try
            {
                var lockResult = _db.Ado.GetInt(
                    """
                    DECLARE @Result int;
                    EXEC @Result = sys.sp_getapplock
                        @Resource = N'WarehouseProductChangeHistory_Schema_Initialization',
                        @LockMode = N'Exclusive',
                        @LockOwner = N'Transaction',
                        @LockTimeout = 30000;
                    SELECT @Result;
                    """
                );
                if (lockResult < 0)
                {
                    throw new InvalidOperationException("获取仓库商品修改历史建表锁失败");
                }

                InitializeWarehouseProductChangeHistoryTableUnderLock();
                _db.Ado.CommitTran();
            }
            catch
            {
                try
                {
                    _db.Ado.RollbackTran();
                }
                catch
                {
                    // 原异常包含真正的建表或锁失败原因，回滚失败不覆盖它。
                }
                throw;
            }
        }

        private void InitializeWarehouseProductChangeHistoryTableUnderLock()
        {
            var tableType = typeof(WarehouseProductChangeHistory);
            var tableName = _db.EntityMaintenance.GetTableName(tableType);
            if (!_db.DbMaintenance.IsAnyTable(tableName))
            {
                Console.WriteLine($"表 {tableName} 不存在，正在创建...");
                _db.CodeFirst.InitTables(tableType);
                Console.WriteLine($"✓ {tableName} 表创建成功");
                return;
            }

            Console.WriteLine($"✓ {tableName} 表已存在");
            if (!_syncExistingTableStructureOnStartup)
            {
                return;
            }

            try
            {
                CleanupLegacyIndexesBeforeSmartInit(tableName);
                _db.CodeFirst.InitTables(tableType);
                Console.WriteLine($"✓ {tableName} 表结构检查完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ {tableName} 表结构更新时出现警告: {ex.Message}");
            }
        }

        private HashSet<string> GetExistingTableNames()
        {
            try
            {
                if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    var tableNames = _db.Ado.SqlQuery<string>(
                        "SELECT name FROM sys.tables WHERE type = 'U'"
                    );
                    return new HashSet<string>(tableNames, StringComparer.OrdinalIgnoreCase);
                }

                if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
                {
                    var tableNames = _db.Ado.SqlQuery<string>(
                        "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"
                    );
                    return new HashSet<string>(tableNames, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 批量读取现有表名失败，将回退逐表检查: {ex.Message}");
            }

            return new HashSet<string>(
                _db.DbMaintenance.GetTableInfoList(false).Select(x => x.Name),
                StringComparer.OrdinalIgnoreCase
            );
        }

        private void EnsureEmployeeProfilePhoneColumn()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(EmployeeProfile));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
            if (columns.Any(column => string.Equals(column.DbColumnName, "Phone", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"IF COL_LENGTH('{tableName}', 'Phone') IS NULL ALTER TABLE [{tableName}] ADD [Phone] nvarchar(50) NULL"
                );
                Console.WriteLine($"✓ {tableName}.Phone 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"Phone\" varchar(50) NULL"
                );
                Console.WriteLine($"✓ {tableName}.Phone 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand($"ALTER TABLE [{tableName}] ADD COLUMN [Phone] varchar(50) NULL");
                Console.WriteLine($"✓ {tableName}.Phone 列已补齐");
            }
        }

        private void EnsureUserLastLoginIpColumn()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(User));
            if (!IsKnownTable(tableName))
            {
                // 兼容早期手工建表/SQLite 测试库：User 实体未显式标注表名时，实体映射名可能与旧表名不同。
                tableName = "User";
            }

            if (!IsKnownTable(tableName))
            {
                return;
            }

            // 最近登录 IP 是后台安全审计字段，老库启动时需要无损补列。
            EnsureColumn(tableName, "LastLoginIp", "nvarchar(50)", "varchar(50)", "varchar(50)");
        }

        private bool IsKnownTable(string tableName)
        {
            if (_db.DbMaintenance.IsAnyTable(tableName))
            {
                return true;
            }

            // SQLite 测试库和部分旧库由手写 SQL 建表，逐表清单比 IsAnyTable 更能反映真实状态。
            return _db.DbMaintenance
                .GetTableInfoList(false)
                .Any(table => string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureRefreshTokenUserIpAddressIndex()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(RefreshToken));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            const string indexName = "IX_RefreshToken_UserGUID_IpAddress";
            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = '{indexName}' AND object_id = OBJECT_ID('{tableName}')) CREATE INDEX {indexName} ON [{tableName}](UserGUID, IpAddress)"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" (\"UserGUID\", \"IpAddress\")"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand(
                    $"CREATE INDEX IF NOT EXISTS [{indexName}] ON [{tableName}] ([UserGUID], [IpAddress])"
                );
            }
        }

        private void EnsureLocalSupplierImageBaseUrlColumn()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(HBLocalSupplier));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
            if (columns.Any(column => string.Equals(column.DbColumnName, "ImageBaseUrl", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"IF COL_LENGTH('{tableName}', 'ImageBaseUrl') IS NULL ALTER TABLE [{tableName}] ADD [ImageBaseUrl] nvarchar(512) NULL"
                );
                Console.WriteLine($"✓ {tableName}.ImageBaseUrl 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"ImageBaseUrl\" varchar(512) NULL"
                );
                Console.WriteLine($"✓ {tableName}.ImageBaseUrl 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand($"ALTER TABLE [{tableName}] ADD COLUMN [ImageBaseUrl] varchar(512) NULL");
                Console.WriteLine($"✓ {tableName}.ImageBaseUrl 列已补齐");
            }
        }

        private void EnsureStoreContactEmailColumn()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(Store));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
            if (columns.Any(column => string.Equals(column.DbColumnName, "ContactEmail", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"IF COL_LENGTH('{tableName}', 'ContactEmail') IS NULL ALTER TABLE [{tableName}] ADD [ContactEmail] nvarchar(100) NULL"
                );
                Console.WriteLine($"✓ {tableName}.ContactEmail 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"ContactEmail\" varchar(100) NULL"
                );
                Console.WriteLine($"✓ {tableName}.ContactEmail 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand($"ALTER TABLE [{tableName}] ADD COLUMN [ContactEmail] varchar(100) NULL");
                Console.WriteLine($"✓ {tableName}.ContactEmail 列已补齐");
            }
        }

        private void EnsureSalesStatisticRefreshStateJobColumns()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(SalesStatisticRefreshState));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            EnsureColumn(tableName, "JobId", "uniqueidentifier", "uuid", "varchar(36)");
            EnsureColumn(tableName, "RequestedBy", "nvarchar(100)", "varchar(100)", "varchar(100)");
            EnsureColumn(tableName, "RequestedAtUtc", "datetime2", "timestamp", "datetime");
            EnsureColumn(tableName, "StartedAtUtc", "datetime2", "timestamp", "datetime");
            EnsureColumn(tableName, "CompletedAtUtc", "datetime2", "timestamp", "datetime");
            EnsureColumn(tableName, "SourceProductVersion", "nvarchar(64)", "varchar(64)", "varchar(64)");
        }

        private void EnsureSupplierStatisticColumns()
        {
            foreach (var entityType in new[]
                     {
                         typeof(AustralianSupplierStoreSalesDetail),
                         typeof(ChinaSupplierStoreSalesDetail),
                     })
            {
                var tableName = _db.EntityMaintenance.GetTableName(entityType);
                if (!IsKnownTable(tableName))
                    continue;
                EnsureColumn(tableName, "TotalCost", "decimal(18, 4)", "numeric(18, 4)", "decimal(18, 4)");
                EnsureColumn(tableName, "GrossProfit", "decimal(18, 4)", "numeric(18, 4)", "decimal(18, 4)");
                EnsureColumn(tableName, "StatisticRowCount", "int", "integer", "integer");
                EnsureColumn(tableName, "CostedRowCount", "int", "integer", "integer");
                EnsureColumn(tableName, "GrossProfitRowCount", "int", "integer", "integer");
            }
        }

        private void EnsureInvoiceEmailConfigurationMultiAccountSchema()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(InvoiceEmailConfiguration));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            // 发票邮箱配置从单账号升级为多账号，老库启动时只补列和回填默认账号，不重建表。
            EnsureColumn(tableName, "Name", "nvarchar(100)", "varchar(100)", "varchar(100)");
            EnsureColumn(tableName, "IsDefault", "bit", "boolean", "integer");
            BackfillInvoiceEmailConfigurationNames(tableName);
            BackfillInvoiceEmailConfigurationDefaultFlags(tableName);
            EnsureInvoiceEmailConfigurationDefaultAccount();
        }

        private void BackfillInvoiceEmailConfigurationNames(string tableName)
        {
            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"UPDATE [{tableName}] SET [Name] = CASE WHEN COALESCE([FromEmail], '') <> '' THEN [FromEmail] ELSE N'默认发件账号' END WHERE [Name] IS NULL OR LTRIM(RTRIM([Name])) = ''"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"UPDATE \"{tableName}\" SET \"Name\" = COALESCE(NULLIF(TRIM(\"FromEmail\"), ''), '默认发件账号') WHERE \"Name\" IS NULL OR TRIM(\"Name\") = ''"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand(
                    $"UPDATE [{tableName}] SET [Name] = COALESCE(NULLIF(TRIM([FromEmail]), ''), '默认发件账号') WHERE [Name] IS NULL OR TRIM([Name]) = ''"
                );
            }
        }

        private void BackfillInvoiceEmailConfigurationDefaultFlags(string tableName)
        {
            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"UPDATE [{tableName}] SET [IsDefault] = 0 WHERE [IsDefault] IS NULL"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"UPDATE \"{tableName}\" SET \"IsDefault\" = false WHERE \"IsDefault\" IS NULL"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand(
                    $"UPDATE [{tableName}] SET [IsDefault] = 0 WHERE [IsDefault] IS NULL"
                );
            }
        }

        private void EnsureInvoiceEmailConfigurationDefaultAccount()
        {
            var rows = _db.Queryable<InvoiceEmailConfiguration>().ToList();
            if (rows.Count == 0 || rows.Count(row => row.IsDefault) == 1)
            {
                return;
            }

            // 默认账号异常时优先沿用历史 default 行，否则取最早账号，保证发送链路始终有唯一默认账号。
            var selectedId = rows.FirstOrDefault(
                    row => string.Equals(
                        row.Id,
                        InvoiceEmailConfiguration.DefaultId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )?.Id
                ?? rows
                    .OrderBy(row => row.CreatedAt)
                    .ThenBy(row => row.Id)
                    .First()
                    .Id;
            var rowIds = rows.Select(row => row.Id).ToList();

            _db.Updateable<InvoiceEmailConfiguration>()
                .SetColumns(row => row.IsDefault == false)
                .Where(row => rowIds.Contains(row.Id))
                .ExecuteCommand();
            _db.Updateable<InvoiceEmailConfiguration>()
                .SetColumns(row => row.IsDefault == true)
                .Where(row => row.Id == selectedId)
                .ExecuteCommand();
            Console.WriteLine($"✓ {nameof(InvoiceEmailConfiguration)} 默认发件账号已回填");
        }

        private void EnsureContainerDetailSchemaColumns()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(ContainerDetail));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            // 老库无损补列：货柜明细阶段先保存目标分类，新商品创建时再继承到 Product。
            EnsureColumn(tableName, "TargetWarehouseCategoryGUID", "nvarchar(50)", "varchar(50)", "varchar(50)");
            // 老库无损补列：上次价格是货柜明细快照，只在新建明细或显式回填时写入。
            EnsureColumn(tableName, "LastImportPrice", "decimal(18,2)", "numeric(18,2)", "decimal(18,2)");
            EnsureColumn(tableName, "LastOEMPrice", "decimal(18,2)", "numeric(18,2)", "decimal(18,2)");
        }

        private void EnsureDomesticSetProductNameColumn()
        {
            var tableName = _db.EntityMaintenance.GetTableName(typeof(DomesticSetProduct));
            if (!IsKnownTable(tableName))
            {
                return;
            }

            // 旧关系表无损补列；历史数据允许为空，读取时继续回退到日志或旧子主档名称。
            EnsureColumn(
                tableName,
                nameof(DomesticSetProduct.SetProductName),
                "nvarchar(200)",
                "varchar(200)",
                "varchar(200)"
            );
        }

        private void EnsureColumn(
            string tableName,
            string columnName,
            string sqlServerType,
            string postgreSqlType,
            string sqliteType
        )
        {
            var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
            if (columns.Any(column => string.Equals(column.DbColumnName, columnName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"IF COL_LENGTH('{tableName}', '{columnName}') IS NULL ALTER TABLE [{tableName}] ADD [{columnName}] {sqlServerType} NULL"
                );
                Console.WriteLine($"✓ {tableName}.{columnName} 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"{columnName}\" {postgreSqlType} NULL"
                );
                Console.WriteLine($"✓ {tableName}.{columnName} 列已补齐");
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand($"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {sqliteType} NULL");
                Console.WriteLine($"✓ {tableName}.{columnName} 列已补齐");
            }
        }

        private void CleanupLegacyIndexesBeforeSmartInit(string tableName)
        {
            if (_db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            {
                return;
            }

            if (!string.Equals(tableName, "Cart", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            const string dropCartCreatedAtIndexSql =
                "IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_CreatedAt' AND object_id = OBJECT_ID('Cart')) "
                + "DROP INDEX IX_Cart_CreatedAt ON [Cart]";

            try
            {
                _db.Ado.ExecuteCommand(dropCartCreatedAtIndexSql);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 清理 Cart 旧索引时出现警告: {ex.Message}");
            }
        }

        private void RecreateAllTables()
        {
            Console.WriteLine("开始删除旧表...");

            // 按相反顺序删除表（避免外键约束问题）
            try
            {
                _db.DbMaintenance.DropTable<ContainerDetail>();
                Console.WriteLine("✓ 删除ContainerDetail表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<Container>();
                Console.WriteLine("✓ 删除Container表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<CashRegisterUser>();
                Console.WriteLine("✓ 删除CashRegisterUser表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<YIWU_OrderDetail>();
                Console.WriteLine("✓ 删除YIWU_OrderDetail表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<YIWU_Order>();
                Console.WriteLine("✓ 删除YIWU_Order表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<ProductLocation>();
                Console.WriteLine("✓ 删除ProductLocation表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<Location>();
                Console.WriteLine("✓ 删除Location表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<ChinaSupplier>();
                Console.WriteLine("✓ 删除Supplier表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<DomesticProductCreationLog>();
                Console.WriteLine("✓ 删除DomesticProductCreationLog表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<DomesticSetProduct>();
                Console.WriteLine("✓ 删除DomesticSetProduct表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<DomesticProduct>();
                Console.WriteLine("✓ 删除DomesticProduct表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<ProductSetCode>();
                Console.WriteLine("✓ 删除ProductSetCode表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<ProductPrefixCode>();
                Console.WriteLine("✓ 删除ProductPrefixCode表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<CartItem>();
                Console.WriteLine("✓ 删除CartItem表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<Cart>();
                Console.WriteLine("✓ 删除Cart表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<Product>();
                Console.WriteLine("✓ 删除Product表");
            }
            catch { }

            try
            {
                _db.DbMaintenance.DropTable<WarehouseProduct>();
                Console.WriteLine("✓ 删除WarehouseProduct表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<WarehouseCategory>();
                Console.WriteLine("✓ 删除WarehouseCategory表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<RefreshToken>();
                Console.WriteLine("✓ 删除RefreshToken表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<UserStore>();
                Console.WriteLine("✓ 删除UserStore表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<UserRole>();
                Console.WriteLine("✓ 删除UserRole表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<Store>();
                Console.WriteLine("✓ 删除Store表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<Role>();
                Console.WriteLine("✓ 删除Role表");
            }
            catch { }
            try
            {
                _db.DbMaintenance.DropTable<User>();
                Console.WriteLine("✓ 删除User表");
            }
            catch { }

            Console.WriteLine("开始创建新表...");

            // 按依赖顺序创建表
            _db.CodeFirst.InitTables(typeof(User));
            Console.WriteLine("✓ User表创建成功");

            _db.CodeFirst.InitTables(typeof(Role));
            Console.WriteLine("✓ Role表创建成功");

            _db.CodeFirst.InitTables(typeof(Store));
            Console.WriteLine("✓ Store表创建成功");

            _db.CodeFirst.InitTables(typeof(UserRole));
            Console.WriteLine("✓ UserRole表创建成功");

            _db.CodeFirst.InitTables(typeof(UserStore));
            Console.WriteLine("✓ UserStore表创建成功");

            _db.CodeFirst.InitTables(typeof(RefreshToken));
            Console.WriteLine("✓ RefreshToken表创建成功");

            _db.CodeFirst.InitTables(typeof(WarehouseCategory));
            Console.WriteLine("✓ WarehouseCategory表创建成功");

            _db.CodeFirst.InitTables(typeof(WarehouseProduct));
            Console.WriteLine("✓ WarehouseProduct表创建成功");

            _db.CodeFirst.InitTables(typeof(Product));
            Console.WriteLine("✓ Product表创建成功");

            _db.CodeFirst.InitTables(typeof(Cart));
            Console.WriteLine("✓ Cart表创建成功");

            _db.CodeFirst.InitTables(typeof(CartItem));
            Console.WriteLine("✓ CartItem表创建成功");

            _db.CodeFirst.InitTables(typeof(ChinaSupplier));
            Console.WriteLine("✓ Supplier表创建成功");

            _db.CodeFirst.InitTables(typeof(DomesticProduct));
            Console.WriteLine("✓ DomesticProduct表创建成功");

            _db.CodeFirst.InitTables(typeof(ProductPrefixCode));
            Console.WriteLine("✓ ProductPrefixCode表创建成功");

            _db.CodeFirst.InitTables(typeof(ProductSetCode));
            Console.WriteLine("✓ ProductSetCode表创建成功");

            _db.CodeFirst.InitTables(typeof(DomesticSetProduct));
            Console.WriteLine("✓ DomesticSetProduct表创建成功");

            _db.CodeFirst.InitTables(typeof(DomesticProductCreationLog));
            Console.WriteLine("✓ DomesticProductCreationLog表创建成功");

            _db.CodeFirst.InitTables(typeof(Location));
            Console.WriteLine("✓ Location表创建成功");

            _db.CodeFirst.InitTables(typeof(ProductLocation));
            Console.WriteLine("✓ ProductLocation表创建成功");

            _db.CodeFirst.InitTables(typeof(YIWU_Order));
            Console.WriteLine("✓ YIWU_Order表创建成功");

            _db.CodeFirst.InitTables(typeof(YIWU_OrderDetail));
            Console.WriteLine("✓ YIWU_OrderDetail表创建成功");

            _db.CodeFirst.InitTables(typeof(Container));
            Console.WriteLine("✓ Container表创建成功");

            _db.CodeFirst.InitTables(typeof(ContainerDetail));
            Console.WriteLine("✓ ContainerDetail表创建成功");

            _db.CodeFirst.InitTables(typeof(CashRegisterUser));
            Console.WriteLine("✓ CashRegisterUser表创建成功");

            _db.CodeFirst.InitTables(typeof(EmployeeProfile));
            Console.WriteLine("✓ EmployeeProfile表创建成功");

            _db.CodeFirst.InitTables(
                typeof(MobileAppDeviceStatus),
                typeof(UserLoginDeviceRecord),
                typeof(AttendanceSchedule),
                typeof(AttendanceAvailability),
                typeof(AttendancePunch),
                typeof(AttendanceLocationSample),
                typeof(AttendanceApproval),
                typeof(AttendancePunchAdjustment),
                typeof(AttendanceStoreHoliday),
                typeof(AttendanceLeaveRequest),
                typeof(AttendanceSettings),
                typeof(InvoiceEmailConfiguration),
                typeof(StoreOrderInvoiceEmailSendRecord),
                typeof(SeasonalCardCatalog),
                typeof(SeasonalCardRemainingSubmission)
            );
            Console.WriteLine("✓ Attendance表创建成功");

            // 创建索引
            CreateIndexes();
        }

        private void CreateIndexesIfNotExists()
        {
            try
            {
                Console.WriteLine("检查并创建缺失的索引...");

                if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    // 创建唯一索引（如果不存在）
                    CreateUniqueIndexesIfNotExists();

                    // 创建普通索引（如果不存在）
                    CreateNormalIndexesIfNotExists();
                    CreateWareHouseOrderOrderNoUniqueIndex();
                }
                else if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
                {
                    CreatePostgreSQLIndexesIfNotExists();
                }
                else if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
                {
                    CreateSqliteIndexesIfNotExists();
                }

                Console.WriteLine("✓ 索引检查完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检查索引时出现错误: {ex.Message}");
                // 索引创建失败不影响主要功能，仅记录日志
            }
        }

        private void CreateSqliteIndexesIfNotExists()
        {
            var indexStatements = new Dictionary<string, string>
            {
                // 商品最近来货后销量：键列与批量统计筛选保持一致，SQLite 不支持 INCLUDE。
                ["IX_ProductStoreDailySalesStatistic_Branch_Product_Date"] =
                    "CREATE INDEX IF NOT EXISTS [IX_ProductStoreDailySalesStatistic_Branch_Product_Date] ON [ProductStoreDailySalesStatistic]([BranchCode], [ProductCode], [Date])",
            };

            foreach (var indexStatement in indexStatements)
            {
                _db.Ado.ExecuteCommand(indexStatement.Value);
            }
        }

        private void CreatePostgreSQLIndexesIfNotExists()
        {
            Console.WriteLine("检查 PostgreSQL 索引...");

            var indexStatements = new Dictionary<string, string>
            {
                // 唯一索引
                ["IX_User_Username_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_User_Username_Unique\" ON \"User\" (\"Username\")",
                ["IX_User_Email_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_User_Email_Unique\" ON \"User\" (\"Email\")",
                ["IX_Role_RoleName_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Role_RoleName_Unique\" ON \"Role\" (\"RoleName\")",
                ["IX_Store_StoreCode_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Store_StoreCode_Unique\" ON \"Store\" (\"StoreCode\")",
                ["IX_UserRole_UserRole_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserRole_UserRole_Unique\" ON \"UserRole\" (\"UserGUID\", \"RoleGUID\")",
                ["IX_UserStore_UserStore_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserStore_UserStore_Unique\" ON \"UserStore\" (\"UserGUID\", \"StoreGUID\")",
                ["IX_SysUserPermission_User_Permission_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SysUserPermission_User_Permission_Unique\" ON \"HBwebSysUserPermissions\" (\"UserGuid\", \"PermissionCode\")",
                ["IX_UserStorePosPermission_Scope_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserStorePosPermission_Scope_Unique\" ON \"HBwebSysUserStorePosPermissions\" (\"UserGuid\", \"StoreGuid\", \"PermissionCode\")",
                ["IX_RefreshToken_Token_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RefreshToken_Token_Unique\" ON \"RefreshToken\" (\"Token\")",
                ["IX_Cart_UserGUID_Unique"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Cart_UserGUID_Unique\" ON \"Cart\" (\"UserGUID\")", // 原代码中此索引非唯一，保持一致
                ["IX_CartItem_CartGUID_ProductCode_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_CartItem_CartGUID_ProductCode_Unique\" ON \"CartItem\" (\"CartGUID\", \"ProductCode\")",
                ["IX_LocalSupplier_Code_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_LocalSupplier_Code_Unique\" ON \"LocalSupplier\" (\"LocalSupplierCode\")",
                ["IX_WareHouseOrder_OrderNo_Unique"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_WareHouseOrder_OrderNo_Unique\" ON \"WareHouseOrder\" (\"OrderNo\") WHERE \"OrderNo\" IS NOT NULL",
                ["IX_WareHouseOrder_CartScope"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_WareHouseOrder_CartScope\" ON \"WareHouseOrder\" (\"StoreCode\", \"FlowStatus\", \"IsDeleted\", \"CartOwnerUserGuid\")",
                // 普通索引
                ["IX_User_IsActive"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_User_IsActive\" ON \"User\" (\"IsActive\")",
                ["IX_User_CreatedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_User_CreatedAt\" ON \"User\" (\"CreatedAt\")",
                ["IX_User_LastLoginAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_User_LastLoginAt\" ON \"User\" (\"LastLoginAt\")",
                ["IX_Role_IsActive"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Role_IsActive\" ON \"Role\" (\"IsActive\")",
                ["IX_Role_CreatedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Role_CreatedAt\" ON \"Role\" (\"CreatedAt\")",
                ["IX_Store_IsActive"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Store_IsActive\" ON \"Store\" (\"IsActive\")",
                ["IX_Store_CreatedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Store_CreatedAt\" ON \"Store\" (\"CreatedAt\")",
                ["IX_UserRole_UserGUID"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_UserRole_UserGUID\" ON \"UserRole\" (\"UserGUID\")",
                ["IX_UserRole_RoleGUID"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_UserRole_RoleGUID\" ON \"UserRole\" (\"RoleGUID\")",
                ["IX_UserStore_UserGUID"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_UserStore_UserGUID\" ON \"UserStore\" (\"UserGUID\")",
                ["IX_UserStore_StoreGUID"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_UserStore_StoreGUID\" ON \"UserStore\" (\"StoreGUID\")",
                ["IX_SysUserPermission_UserGuid"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_SysUserPermission_UserGuid\" ON \"HBwebSysUserPermissions\" (\"UserGuid\")",
                ["IX_RefreshToken_UserGUID"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_RefreshToken_UserGUID\" ON \"RefreshToken\" (\"UserGUID\")",
                ["IX_RefreshToken_UserGUID_IpAddress"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_RefreshToken_UserGUID_IpAddress\" ON \"RefreshToken\" (\"UserGUID\", \"IpAddress\")",
                ["IX_RefreshToken_ExpiresAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_RefreshToken_ExpiresAt\" ON \"RefreshToken\" (\"ExpiresAt\")",
                ["IX_Cart_CartStatus"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Cart_CartStatus\" ON \"Cart\" (\"CartStatus\")",
                ["IX_Cart_LastModified"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Cart_LastModified\" ON \"Cart\" (\"LastModified\")",
                ["IX_Cart_ExpiresAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_Cart_ExpiresAt\" ON \"Cart\" (\"ExpiresAt\")",
                ["IX_CartItem_CartGUID"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_CartItem_CartGUID\" ON \"CartItem\" (\"CartGUID\")",
                ["IX_CartItem_ProductCode"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_CartItem_ProductCode\" ON \"CartItem\" (\"ProductCode\")",
                ["IX_CartItem_AddedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_CartItem_AddedAt\" ON \"CartItem\" (\"AddedAt\")",
                ["IX_LocalSupplier_Status"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_LocalSupplier_Status\" ON \"LocalSupplier\" (\"Status\")",
                ["IX_LocalSupplier_Name"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_LocalSupplier_Name\" ON \"LocalSupplier\" (\"Name\")",
                ["IX_DailySalesStatistic_Date"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_DailySalesStatistic_Date\" ON \"DailySalesStatistic\" (\"Date\")",
                ["IX_HourlySalesStatistic_Date_BranchCode_Hour"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_HourlySalesStatistic_Date_BranchCode_Hour\" ON \"HourlySalesStatistic\" (\"Date\", \"BranchCode\", \"Hour\")",
                ["IX_StoreSalesStatistic_Date_TotalAmount"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_StoreSalesStatistic_Date_TotalAmount\" ON \"StoreSalesStatistic\" (\"Date\", \"TotalAmount\")",
                ["IX_SupplierSalesStatistic_Date_IsDomestic_TotalAmount"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_SupplierSalesStatistic_Date_IsDomestic_TotalAmount\" ON \"SupplierSalesStatistic\" (\"Date\", \"IsDomestic\", \"TotalAmount\")",
                ["IX_StoreSupplierSalesDetail_Date_BranchCode_SupplierCode"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_StoreSupplierSalesDetail_Date_BranchCode_SupplierCode\" ON \"StoreSupplierSalesDetail\" (\"Date\", \"BranchCode\", \"SupplierCode\")",
                ["IX_ProductStoreDailySalesStatistic_Date_Supplier_Product"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ProductStoreDailySalesStatistic_Date_Supplier_Product\" ON \"ProductStoreDailySalesStatistic\" (\"Date\", \"SupplierCode\", \"ProductCode\")",
                ["IX_ProductStoreDailySalesStatistic_Date_Supplier_Quantity"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ProductStoreDailySalesStatistic_Date_Supplier_Quantity\" ON \"ProductStoreDailySalesStatistic\" (\"Date\", \"SupplierCode\", \"TotalQuantity\")",
                ["IX_ProductStoreDailySalesStatistic_Date_Branch_Supplier_Product"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ProductStoreDailySalesStatistic_Date_Branch_Supplier_Product\" ON \"ProductStoreDailySalesStatistic\" (\"Date\", \"BranchCode\", \"SupplierCode\", \"ProductCode\")",
                ["IX_ProductStoreDailySalesStatistic_Branch_Product_Date"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ProductStoreDailySalesStatistic_Branch_Product_Date\" ON \"ProductStoreDailySalesStatistic\" (\"BranchCode\", \"ProductCode\", \"Date\") INCLUDE (\"TotalQuantity\", \"TotalAmount\")",
                ["IX_SalesStatisticRefreshState_Type_Date"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_SalesStatisticRefreshState_Type_Date\" ON \"SalesStatisticRefreshState\" (\"StatisticType\", \"Date\")",
                ["IX_ScheduledTaskLog_TaskType"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ScheduledTaskLog_TaskType\" ON \"ScheduledTaskLog\" (\"TaskType\")",
                ["IX_ScheduledTaskLog_Status"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ScheduledTaskLog_Status\" ON \"ScheduledTaskLog\" (\"Status\")",
                ["IX_ScheduledTaskLog_StartedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ScheduledTaskLog_StartedAt\" ON \"ScheduledTaskLog\" (\"StartedAt\")",
                ["IX_ScheduledTaskLog_TaskType_Status"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ScheduledTaskLog_TaskType_Status\" ON \"ScheduledTaskLog\" (\"TaskType\", \"Status\")",
                ["IX_ScheduledTaskLog_ScheduledTime"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ScheduledTaskLog_ScheduledTime\" ON \"ScheduledTaskLog\" (\"ScheduledTime\")",
                ["IX_ScheduledTaskInstanceState_LastSeenAtUtc"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ScheduledTaskInstanceState_LastSeenAtUtc\" ON \"ScheduledTaskInstanceState\" (\"LastSeenAtUtc\")",
                ["IX_StoreOrderInvoiceEmailSendRecord_Order_Time"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_StoreOrderInvoiceEmailSendRecord_Order_Time\" ON \"StoreOrderInvoiceEmailSendRecord\" (\"StoreOrderUuid\", \"SentAtUtc\" DESC, \"CreatedAtUtc\" DESC)",
                ["IX_ApplicationLog_TimestampUtc"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ApplicationLog_TimestampUtc\" ON \"ApplicationLog\" (\"TimestampUtc\")",
                ["IX_ApplicationLog_Project_Level_Time"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ApplicationLog_Project_Level_Time\" ON \"ApplicationLog\" (\"ProjectCode\", \"Level\", \"TimestampUtc\")",
                ["IX_ApplicationLog_TraceId"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ApplicationLog_TraceId\" ON \"ApplicationLog\" (\"TraceId\")",
                ["IX_ApplicationLog_RequestPath"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ApplicationLog_RequestPath\" ON \"ApplicationLog\" (\"RequestPath\")",
                ["IX_MobileAppBuild_EasBuildId"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_MobileAppBuild_EasBuildId\" ON \"MobileAppBuild\" (\"EasBuildId\")",
                ["IX_MobileAppBuild_Profile_CompletedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_MobileAppBuild_Profile_CompletedAt\" ON \"MobileAppBuild\" (\"BuildProfile\", \"Platform\", \"Status\", \"CompletedAt\")",
                ["IX_MobileAppDeviceStatus_HardwareId"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_MobileAppDeviceStatus_HardwareId\" ON \"MobileAppDeviceStatus\" (\"HardwareId\")",
                ["IX_MobileAppDeviceStatus_LastSeen"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_MobileAppDeviceStatus_LastSeen\" ON \"MobileAppDeviceStatus\" (\"LastSeenAtUtc\")",
                ["IX_MobileAppDeviceStatus_System_LastSeen"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_MobileAppDeviceStatus_System_LastSeen\" ON \"MobileAppDeviceStatus\" (\"DeviceSystem\", \"LastSeenAtUtc\")",
                ["IX_MobileAppOtaUpdate_Group_Platform"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_MobileAppOtaUpdate_Group_Platform\" ON \"MobileAppOtaUpdate\" (\"UpdateGroupId\", \"Platform\")",
                ["IX_MobileAppOtaUpdate_Channel_Runtime_PublishedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_MobileAppOtaUpdate_Channel_Runtime_PublishedAt\" ON \"MobileAppOtaUpdate\" (\"Channel\", \"RuntimeVersion\", \"PublishedAt\")",
                ["IX_ServiceApiToken_TokenHash"] =
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ServiceApiToken_TokenHash\" ON \"ServiceApiToken\" (\"TokenHash\")",
                ["IX_ServiceApiToken_CreatedAt"] =
                    "CREATE INDEX IF NOT EXISTS \"IX_ServiceApiToken_CreatedAt\" ON \"ServiceApiToken\" (\"CreatedAt\")",
            };

            foreach (var indexCheck in indexStatements)
            {
                try
                {
                    _db.Ado.ExecuteCommand(indexCheck.Value);
                    Console.WriteLine($"✓ PostgreSQL 索引 {indexCheck.Key} 检查/创建完成");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"⚠️ 创建 PostgreSQL 索引 {indexCheck.Key} 时出现警告: {ex.Message}"
                    );
                }
            }
        }

        private void CreateUniqueIndexesIfNotExists()
        {
            Console.WriteLine("检查唯一索引...");

            // 检查并创建唯一索引的SQL语句
            var uniqueIndexChecks = new Dictionary<string, string>
            {
                // 注意：主键GUID字段现在由SqlSugar自动处理唯一性，不需要额外的唯一索引
                ["IX_User_Username_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_Username_Unique' AND object_id = OBJECT_ID('User')) CREATE UNIQUE INDEX IX_User_Username_Unique ON [User](Username)",
                ["IX_User_Email_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_Email_Unique' AND object_id = OBJECT_ID('User')) CREATE UNIQUE INDEX IX_User_Email_Unique ON [User](Email)",
                ["IX_Role_RoleName_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Role_RoleName_Unique' AND object_id = OBJECT_ID('Role')) CREATE UNIQUE INDEX IX_Role_RoleName_Unique ON [Role](RoleName)",
                ["IX_Store_StoreCode_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_StoreCode_Unique' AND object_id = OBJECT_ID('Store')) CREATE UNIQUE INDEX IX_Store_StoreCode_Unique ON [Store](StoreCode)",
                ["IX_UserRole_UserRole_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserRole_UserRole_Unique' AND object_id = OBJECT_ID('UserRole')) CREATE UNIQUE INDEX IX_UserRole_UserRole_Unique ON [UserRole](UserGUID, RoleGUID)",
                ["IX_UserStore_UserStore_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_UserStore_Unique' AND object_id = OBJECT_ID('UserStore')) CREATE UNIQUE INDEX IX_UserStore_UserStore_Unique ON [UserStore](UserGUID, StoreGUID)",
                ["IX_SysUserPermission_User_Permission_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SysUserPermission_User_Permission_Unique' AND object_id = OBJECT_ID('HBwebSysUserPermissions')) CREATE UNIQUE INDEX IX_SysUserPermission_User_Permission_Unique ON [HBwebSysUserPermissions](UserGuid, PermissionCode)",
                ["IX_UserStorePosPermission_Scope_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStorePosPermission_Scope_Unique' AND object_id = OBJECT_ID('HBwebSysUserStorePosPermissions')) CREATE UNIQUE INDEX IX_UserStorePosPermission_Scope_Unique ON [HBwebSysUserStorePosPermissions](UserGuid, StoreGuid, PermissionCode)",
                ["IX_RefreshToken_Token_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_Token_Unique' AND object_id = OBJECT_ID('RefreshToken')) CREATE UNIQUE INDEX IX_RefreshToken_Token_Unique ON [RefreshToken](Token)",
                ["IX_Cart_UserGUID_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_UserGUID_Unique' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_UserGUID_Unique ON [Cart](UserGUID)",
                ["IX_CartItem_CartGUID_ProductCode_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_CartGUID_ProductCode_Unique' AND object_id = OBJECT_ID('CartItem')) CREATE UNIQUE INDEX IX_CartItem_CartGUID_ProductCode_Unique ON [CartItem](CartGUID, ProductCode)",
                ["IX_LocalSupplier_Code_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_LocalSupplier_Code_Unique' AND object_id = OBJECT_ID('LocalSupplier')) CREATE UNIQUE INDEX IX_LocalSupplier_Code_Unique ON [LocalSupplier](LocalSupplierCode)",
                ["IX_WareHouseOrder_OrderNo_Unique"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WareHouseOrder_OrderNo_Unique' AND object_id = OBJECT_ID('WareHouseOrder')) CREATE UNIQUE INDEX IX_WareHouseOrder_OrderNo_Unique ON [WareHouseOrder]([OrderNo]) WHERE [OrderNo] IS NOT NULL",
            };

            foreach (var indexCheck in uniqueIndexChecks)
            {
                try
                {
                    _db.Ado.ExecuteCommand(indexCheck.Value);
                    Console.WriteLine($"✓ 唯一索引 {indexCheck.Key} 检查完成");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ 创建唯一索引 {indexCheck.Key} 时出现警告: {ex.Message}");
                }
            }
        }

        private void CreateNormalIndexesIfNotExists()
        {
            Console.WriteLine("检查普通索引...");

            // 检查并创建普通索引的SQL语句
            var normalIndexChecks = new Dictionary<string, string>
            {
                ["IX_User_IsActive"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_IsActive' AND object_id = OBJECT_ID('User')) CREATE INDEX IX_User_IsActive ON [User](IsActive)",
                ["IX_User_CreatedAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_CreatedAt' AND object_id = OBJECT_ID('User')) CREATE INDEX IX_User_CreatedAt ON [User](CreatedAt)",
                ["IX_User_LastLoginAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_LastLoginAt' AND object_id = OBJECT_ID('User')) CREATE INDEX IX_User_LastLoginAt ON [User](LastLoginAt)",
                ["IX_Role_IsActive"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Role_IsActive' AND object_id = OBJECT_ID('Role')) CREATE INDEX IX_Role_IsActive ON [Role](IsActive)",
                ["IX_Role_CreatedAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Role_CreatedAt' AND object_id = OBJECT_ID('Role')) CREATE INDEX IX_Role_CreatedAt ON [Role](CreatedAt)",
                ["IX_Store_IsActive"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_IsActive' AND object_id = OBJECT_ID('Store')) CREATE INDEX IX_Store_IsActive ON [Store](IsActive)",
                ["IX_Store_CreatedAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_CreatedAt' AND object_id = OBJECT_ID('Store')) CREATE INDEX IX_Store_CreatedAt ON [Store](CreatedAt)",
                ["IX_UserRole_UserGUID"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserRole_UserGUID' AND object_id = OBJECT_ID('UserRole')) CREATE INDEX IX_UserRole_UserGUID ON [UserRole](UserGUID)",
                ["IX_UserRole_RoleGUID"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserRole_RoleGUID' AND object_id = OBJECT_ID('UserRole')) CREATE INDEX IX_UserRole_RoleGUID ON [UserRole](RoleGUID)",
                ["IX_UserStore_UserGUID"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_UserGUID' AND object_id = OBJECT_ID('UserStore')) CREATE INDEX IX_UserStore_UserGUID ON [UserStore](UserGUID)",
                ["IX_UserStore_StoreGUID"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_StoreGUID' AND object_id = OBJECT_ID('UserStore')) CREATE INDEX IX_UserStore_StoreGUID ON [UserStore](StoreGUID)",
                ["IX_SysUserPermission_UserGuid"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SysUserPermission_UserGuid' AND object_id = OBJECT_ID('HBwebSysUserPermissions')) CREATE INDEX IX_SysUserPermission_UserGuid ON [HBwebSysUserPermissions](UserGuid)",
                ["IX_RefreshToken_UserGUID"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_UserGUID' AND object_id = OBJECT_ID('RefreshToken')) CREATE INDEX IX_RefreshToken_UserGUID ON [RefreshToken](UserGUID)",
                ["IX_RefreshToken_UserGUID_IpAddress"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_UserGUID_IpAddress' AND object_id = OBJECT_ID('RefreshToken')) CREATE INDEX IX_RefreshToken_UserGUID_IpAddress ON [RefreshToken](UserGUID, IpAddress)",
                ["IX_RefreshToken_ExpiresAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_ExpiresAt' AND object_id = OBJECT_ID('RefreshToken')) CREATE INDEX IX_RefreshToken_ExpiresAt ON [RefreshToken](ExpiresAt)",
                ["IX_Cart_CartStatus"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_CartStatus' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_CartStatus ON [Cart](CartStatus)",
                ["IX_Cart_LastModified"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_LastModified' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_LastModified ON [Cart](LastModified)",
                ["IX_Cart_ExpiresAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_ExpiresAt' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_ExpiresAt ON [Cart](ExpiresAt)",
                ["IX_CartItem_CartGUID"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_CartGUID' AND object_id = OBJECT_ID('CartItem')) CREATE INDEX IX_CartItem_CartGUID ON [CartItem](CartGUID)",
                ["IX_CartItem_ProductCode"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_ProductCode' AND object_id = OBJECT_ID('CartItem')) CREATE INDEX IX_CartItem_ProductCode ON [CartItem](ProductCode)",
                ["IX_CartItem_AddedAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_AddedAt' AND object_id = OBJECT_ID('CartItem')) CREATE INDEX IX_CartItem_AddedAt ON [CartItem](AddedAt)",
                ["IX_WareHouseOrder_CartScope"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WareHouseOrder_CartScope' AND object_id = OBJECT_ID('WareHouseOrder')) CREATE NONCLUSTERED INDEX [IX_WareHouseOrder_CartScope] ON [WareHouseOrder] ([StoreCode], [FlowStatus], [IsDeleted], [CartOwnerUserGuid])",
                ["IX_ProductStoreDailySalesStatistic_Branch_Product_Date"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductStoreDailySalesStatistic_Branch_Product_Date' AND object_id = OBJECT_ID('ProductStoreDailySalesStatistic')) CREATE INDEX IX_ProductStoreDailySalesStatistic_Branch_Product_Date ON [ProductStoreDailySalesStatistic](BranchCode, ProductCode, Date) INCLUDE(TotalQuantity, TotalAmount)",
                ["IX_LocalSupplier_Status"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_LocalSupplier_Status' AND object_id = OBJECT_ID('LocalSupplier')) CREATE INDEX IX_LocalSupplier_Status ON [LocalSupplier](Status)",
                ["IX_LocalSupplier_Name"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_LocalSupplier_Name' AND object_id = OBJECT_ID('LocalSupplier')) CREATE INDEX IX_LocalSupplier_Name ON [LocalSupplier](Name)",
                ["IX_Product_ItemNumber"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_ItemNumber' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_ItemNumber ON [Product]([ItemNumber]) WHERE [ItemNumber] IS NOT NULL",
                ["IX_Product_Barcode"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Barcode' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Barcode ON [Product]([Barcode]) WHERE [Barcode] IS NOT NULL",
                ["IX_Product_ItemNumber_Lookup"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_ItemNumber_Lookup' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_ItemNumber_Lookup ON [Product]([ItemNumber]) INCLUDE([ProductCode], [Barcode]) WHERE [ItemNumber] IS NOT NULL AND [IsDeleted] = 0",
                ["IX_Product_Barcode_Lookup"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Barcode_Lookup' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Barcode_Lookup ON [Product]([Barcode]) INCLUDE([ProductCode], [ItemNumber]) WHERE [Barcode] IS NOT NULL AND [IsDeleted] = 0",
                ["IX_ProductLocation_ProductCode_Active"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductLocation_ProductCode_Active' AND object_id = OBJECT_ID('ProductLocation')) CREATE INDEX IX_ProductLocation_ProductCode_Active ON [ProductLocation]([ProductCode]) INCLUDE([LocationGuid]) WHERE [IsDeleted] = 0 AND [ProductCode] IS NOT NULL AND [LocationGuid] IS NOT NULL",
                ["IX_ProductLocation_LocationGuid_Active"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductLocation_LocationGuid_Active' AND object_id = OBJECT_ID('ProductLocation')) CREATE INDEX IX_ProductLocation_LocationGuid_Active ON [ProductLocation]([LocationGuid]) INCLUDE([ProductCode]) WHERE [IsDeleted] = 0 AND [LocationGuid] IS NOT NULL AND [ProductCode] IS NOT NULL",
                ["IX_Location_List_Code"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_Code' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_Code ON [Location]([IsDeleted], [LocationCode]) INCLUDE([LocationGuid], [LocationType], [LocationBarcode], [Status], [UpdatedAt], [UpdatedBy])",
                ["IX_Location_List_Barcode"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_Barcode' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_Barcode ON [Location]([IsDeleted], [LocationBarcode]) INCLUDE([LocationGuid], [LocationCode], [LocationType], [Status], [UpdatedAt], [UpdatedBy]) WHERE [LocationBarcode] IS NOT NULL",
                ["IX_Location_List_Type_Status"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_Type_Status' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_Type_Status ON [Location]([IsDeleted], [LocationType], [Status]) INCLUDE([LocationGuid], [LocationCode], [LocationBarcode], [UpdatedAt], [UpdatedBy])",
                ["IX_Location_List_UpdatedAt"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_UpdatedAt' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_UpdatedAt ON [Location]([IsDeleted], [UpdatedAt]) INCLUDE([LocationGuid], [LocationCode], [LocationBarcode], [LocationType], [Status], [UpdatedBy])",
                ["IX_Location_List_UpdatedBy"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_UpdatedBy' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_UpdatedBy ON [Location]([IsDeleted], [UpdatedBy]) INCLUDE([LocationGuid], [LocationCode], [LocationBarcode], [LocationType], [Status], [UpdatedAt]) WHERE [UpdatedBy] IS NOT NULL",
                ["IX_ProductSetCode_ProductCode_SetBarcode_Active"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_ProductCode_SetBarcode_Active' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_ProductCode_SetBarcode_Active ON [ProductSetCode]([ProductCode], [SetBarcode]) WHERE [SetBarcode] IS NOT NULL AND [IsDeleted] = 0",
                ["IX_ProductSetCode_SetBarcode_Lookup"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_SetBarcode_Lookup' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_SetBarcode_Lookup ON [ProductSetCode]([SetBarcode]) INCLUDE([ProductCode], [SetItemNumber]) WHERE [SetBarcode] IS NOT NULL AND [IsDeleted] = 0",
                ["IX_ProductSetCode_ProductCode_Detail"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_ProductCode_Detail' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_ProductCode_Detail ON [ProductSetCode]([ProductCode], [SetBarcode]) INCLUDE([SetCodeId], [SetProductCode], [SetItemNumber], [SetPurchasePrice], [SetRetailPrice], [SetQuantity], [SetType], [IsActive]) WHERE [IsDeleted] = 0",
                ["IX_ProductSetCode_ProductCode_SetType_Active"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_ProductCode_SetType_Active' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_ProductCode_SetType_Active ON [ProductSetCode]([ProductCode], [SetType]) WHERE [IsDeleted] = 0",
                ["IX_StoreMultiCodeProduct_StoreCode_MultiCodeProductCode"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreMultiCodeProduct_StoreCode_MultiCodeProductCode' AND object_id = OBJECT_ID('StoreMultiCodeProduct')) CREATE INDEX IX_StoreMultiCodeProduct_StoreCode_MultiCodeProductCode ON [StoreMultiCodeProduct]([StoreCode], [MultiCodeProductCode]) WHERE [StoreCode] IS NOT NULL AND [MultiCodeProductCode] IS NOT NULL AND [IsDeleted] = 0",
                ["IX_StoreMultiCodeProduct_StoreCode_MultiBarcode"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreMultiCodeProduct_StoreCode_MultiBarcode' AND object_id = OBJECT_ID('StoreMultiCodeProduct')) CREATE INDEX IX_StoreMultiCodeProduct_StoreCode_MultiBarcode ON [StoreMultiCodeProduct]([StoreCode], [MultiBarcode]) WHERE [StoreCode] IS NOT NULL AND [MultiBarcode] IS NOT NULL AND [IsDeleted] = 0",
                ["IX_StoreClearancePrice_ClearanceBarcode_Lookup"] =
                    "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreClearancePrice_ClearanceBarcode_Lookup' AND object_id = OBJECT_ID('StoreClearancePrice')) CREATE INDEX IX_StoreClearancePrice_ClearanceBarcode_Lookup ON [StoreClearancePrice]([ClearanceBarcode]) INCLUDE([ProductCode], [StoreCode]) WHERE [ClearanceBarcode] IS NOT NULL AND [IsDeleted] = 0",
            };

            foreach (var indexCheck in normalIndexChecks)
            {
                try
                {
                    _db.Ado.ExecuteCommand(indexCheck.Value);
                    Console.WriteLine($"✓ 普通索引 {indexCheck.Key} 检查完成");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ 创建普通索引 {indexCheck.Key} 时出现警告: {ex.Message}");
                }
            }
        }

        public void CreateIndexes()
        {
            try
            {
                Console.WriteLine("开始创建GUID字段的索引...");
                EnsureDomesticSetTemplateEnabledSupplierNameUniqueIndex();

                if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    // 创建普通索引
                    CreateNormalIndexes();
                    CreateWareHouseOrderOrderNoUniqueIndex();
                }
                else if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
                {
                    CreatePostgreSQLIndexesIfNotExists();
                }
                else if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
                {
                    CreateSqliteIndexesIfNotExists();
                }

                EnsureStoreLocalSupplierInvoiceBusinessLookupIndex();

                Console.WriteLine("✓ 所有索引创建成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"创建索引时出现错误: {ex.Message}");
                // 索引创建失败不影响主要功能，仅记录日志
            }
        }

        private void CreateUniqueIndexes()
        {
            Console.WriteLine("创建唯一索引...");

            // 使用SQL语句直接创建唯一索引 (SQL Server语法)
            var uniqueIndexStatements = new[]
            {
                // User表的唯一索引（主键GUID字段自动由SqlSugar处理，无需手动创建索引）
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_Username' AND object_id = OBJECT_ID('User')) CREATE UNIQUE INDEX IX_User_Username ON [User](Username)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_Email' AND object_id = OBJECT_ID('User')) CREATE UNIQUE INDEX IX_User_Email ON [User](Email)",
                // Role表的唯一索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Role_RoleName' AND object_id = OBJECT_ID('Role')) CREATE UNIQUE INDEX IX_Role_RoleName ON [Role](RoleName)",
                // Store表的唯一索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_StoreCode' AND object_id = OBJECT_ID('Store')) CREATE UNIQUE INDEX IX_Store_StoreCode ON [Store](StoreCode)",
                // UserRole表的唯一索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserRole_UserRole_Unique' AND object_id = OBJECT_ID('UserRole')) CREATE UNIQUE INDEX IX_UserRole_UserRole_Unique ON [UserRole](UserGUID, RoleGUID)",
                // UserStore表的唯一索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_UserStore_Unique' AND object_id = OBJECT_ID('UserStore')) CREATE UNIQUE INDEX IX_UserStore_UserStore_Unique ON [UserStore](UserGUID, StoreGUID)",
                // 用户直接权限表的唯一索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SysUserPermission_User_Permission_Unique' AND object_id = OBJECT_ID('HBwebSysUserPermissions')) CREATE UNIQUE INDEX IX_SysUserPermission_User_Permission_Unique ON [HBwebSysUserPermissions](UserGuid, PermissionCode)",
                // 用户分店 POS 权限覆盖唯一索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStorePosPermission_Scope_Unique' AND object_id = OBJECT_ID('HBwebSysUserStorePosPermissions')) CREATE UNIQUE INDEX IX_UserStorePosPermission_Scope_Unique ON [HBwebSysUserStorePosPermissions](UserGuid, StoreGuid, PermissionCode)",
                // RefreshToken表的唯一索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_Token' AND object_id = OBJECT_ID('RefreshToken')) CREATE UNIQUE INDEX IX_RefreshToken_Token ON [RefreshToken](Token)",
                // Cart表的索引（用户GUID非唯一，一个用户可能有多个购物车）
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_UserGUID' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_UserGUID ON [Cart](UserGUID)",
                // CartItem表的复合唯一索引（一个购物车中同一商品不能重复）
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_CartGUID_ProductCode' AND object_id = OBJECT_ID('CartItem')) CREATE UNIQUE INDEX IX_CartItem_CartGUID_ProductCode ON [CartItem](CartGUID, ProductCode)",
            };

            foreach (var sql in uniqueIndexStatements)
            {
                try
                {
                    _db.Ado.ExecuteCommand(sql);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"创建唯一索引失败: {ex.Message}");
                }
            }

            Console.WriteLine("✓ 唯一索引创建完成");
        }

        private void CreateWareHouseOrderOrderNoUniqueIndex()
        {
            const string sql =
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WareHouseOrder_OrderNo_Unique' AND object_id = OBJECT_ID('WareHouseOrder')) "
                + "CREATE UNIQUE INDEX IX_WareHouseOrder_OrderNo_Unique ON [WareHouseOrder]([OrderNo]) WHERE [OrderNo] IS NOT NULL";

            try
            {
                _db.Ado.ExecuteCommand(sql);
                Console.WriteLine("✓ WareHouseOrder.OrderNo 唯一索引检查完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 创建 WareHouseOrder.OrderNo 唯一索引失败: {ex.Message}");
            }
        }

        private void EnsureStoreLocalSupplierInvoiceBusinessLookupIndex()
        {
            try
            {
                if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    const string sql =
                        "IF OBJECT_ID(N'[dbo].[StoreLocalSupplierInvoice]', N'U') IS NOT NULL "
                        + "BEGIN "
                        + "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StoreLocalSupplierInvoice_Business_Unique' AND object_id = OBJECT_ID(N'[dbo].[StoreLocalSupplierInvoice]')) "
                        + "DROP INDEX [IX_StoreLocalSupplierInvoice_Business_Unique] ON [dbo].[StoreLocalSupplierInvoice]; "
                        + "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StoreLocalSupplierInvoice_Store_Supplier_Invoice_NotDeleted' AND object_id = OBJECT_ID(N'[dbo].[StoreLocalSupplierInvoice]')) "
                        + "CREATE INDEX IX_StoreLocalSupplierInvoice_Store_Supplier_Invoice_NotDeleted "
                        + "ON [dbo].[StoreLocalSupplierInvoice]([StoreCode], [SupplierCode], [InvoiceNo]) "
                        + "INCLUDE([CreatedAt], [TotalAmount], [ReceivedTotalAmount]) WHERE [IsDeleted] = 0; "
                        + "END";
                    _db.Ado.ExecuteCommand(sql);
                    Console.WriteLine("✓ StoreLocalSupplierInvoice 业务查询索引检查完成");
                }
                else if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
                {
                    const string sql =
                        "DROP INDEX IF EXISTS \"IX_StoreLocalSupplierInvoice_Business_Unique\"; "
                        + "CREATE INDEX IF NOT EXISTS \"IX_StoreLocalSupplierInvoice_Store_Supplier_Invoice_NotDeleted\" "
                        + "ON \"StoreLocalSupplierInvoice\" (\"StoreCode\", \"SupplierCode\", \"InvoiceNo\") "
                        + "INCLUDE (\"CreatedAt\", \"TotalAmount\", \"ReceivedTotalAmount\") "
                        + "WHERE \"IsDeleted\" = false AND \"StoreCode\" IS NOT NULL AND \"SupplierCode\" IS NOT NULL AND \"InvoiceNo\" IS NOT NULL AND \"InvoiceNo\" <> ''";
                    _db.Ado.ExecuteCommand(sql);
                    Console.WriteLine("✓ StoreLocalSupplierInvoice 业务查询索引检查完成");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 检查 StoreLocalSupplierInvoice 业务查询索引失败: {ex.Message}");
            }
        }

        private void EnsureDomesticSetTemplateEnabledSupplierNameUniqueIndex()
        {
            const string tableName = "DomesticSetTemplate";
            const string indexName = "IX_DomesticSetTemplate_EnabledSupplierTemplateName_Unique";
            if (!IsKnownTable(tableName))
            {
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                _db.Ado.ExecuteCommand(
                    $"IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = '{indexName}' AND object_id = OBJECT_ID('{tableName}')) "
                    + $"CREATE UNIQUE INDEX [{indexName}] ON [{tableName}]([SupplierCode], [TemplateName]) "
                    + "WHERE [IsDeleted] = 0 AND [IsEnabled] = 1"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                _db.Ado.ExecuteCommand(
                    $"CREATE UNIQUE INDEX IF NOT EXISTS \"{indexName}\" "
                    + $"ON \"{tableName}\" (\"SupplierCode\", \"TemplateName\") "
                    + "WHERE \"IsDeleted\" = false AND \"IsEnabled\" = true"
                );
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                _db.Ado.ExecuteCommand(
                    $"CREATE UNIQUE INDEX IF NOT EXISTS [{indexName}] "
                    + $"ON [{tableName}]([SupplierCode], [TemplateName]) "
                    + "WHERE [IsDeleted] = 0 AND [IsEnabled] = 1"
                );
            }
        }

        private void CreateNormalIndexes()
        {
            Console.WriteLine("创建普通索引...");

            // 使用SQL语句创建普通索引 (SQL Server语法)
            var normalIndexStatements = new[]
            {
                // User表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_IsActive' AND object_id = OBJECT_ID('User')) CREATE INDEX IX_User_IsActive ON [User](IsActive)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_User_CreatedAt' AND object_id = OBJECT_ID('User')) CREATE INDEX IX_User_CreatedAt ON [User](CreatedAt)",
                // Role表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Role_IsActive' AND object_id = OBJECT_ID('Role')) CREATE INDEX IX_Role_IsActive ON [Role](IsActive)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Role_CreatedAt' AND object_id = OBJECT_ID('Role')) CREATE INDEX IX_Role_CreatedAt ON [Role](CreatedAt)",
                // Store表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_StoreName' AND object_id = OBJECT_ID('Store')) CREATE INDEX IX_Store_StoreName ON [Store](StoreName)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_IsActive' AND object_id = OBJECT_ID('Store')) CREATE INDEX IX_Store_IsActive ON [Store](IsActive)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_CreatedAt' AND object_id = OBJECT_ID('Store')) CREATE INDEX IX_Store_CreatedAt ON [Store](CreatedAt)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Store_UpdatedAt' AND object_id = OBJECT_ID('Store')) CREATE INDEX IX_Store_UpdatedAt ON [Store](UpdatedAt)",
                // UserRole表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserRole_UserGUID' AND object_id = OBJECT_ID('UserRole')) CREATE INDEX IX_UserRole_UserGUID ON [UserRole](UserGUID)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserRole_RoleGUID' AND object_id = OBJECT_ID('UserRole')) CREATE INDEX IX_UserRole_RoleGUID ON [UserRole](RoleGUID)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserRole_AssignedAt' AND object_id = OBJECT_ID('UserRole')) CREATE INDEX IX_UserRole_AssignedAt ON [UserRole](AssignedAt)",
                // UserStore表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_UserGUID' AND object_id = OBJECT_ID('UserStore')) CREATE INDEX IX_UserStore_UserGUID ON [UserStore](UserGUID)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_StoreGUID' AND object_id = OBJECT_ID('UserStore')) CREATE INDEX IX_UserStore_StoreGUID ON [UserStore](StoreGUID)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_AssignedAt' AND object_id = OBJECT_ID('UserStore')) CREATE INDEX IX_UserStore_AssignedAt ON [UserStore](AssignedAt)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserStore_AssignedByGUID' AND object_id = OBJECT_ID('UserStore')) CREATE INDEX IX_UserStore_AssignedByGUID ON [UserStore](AssignedByGUID)",
                // 用户直接权限表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SysUserPermission_UserGuid' AND object_id = OBJECT_ID('HBwebSysUserPermissions')) CREATE INDEX IX_SysUserPermission_UserGuid ON [HBwebSysUserPermissions](UserGuid)",
                // RefreshToken表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_UserGUID' AND object_id = OBJECT_ID('RefreshToken')) CREATE INDEX IX_RefreshToken_UserGUID ON [RefreshToken](UserGUID)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_UserGUID_IpAddress' AND object_id = OBJECT_ID('RefreshToken')) CREATE INDEX IX_RefreshToken_UserGUID_IpAddress ON [RefreshToken](UserGUID, IpAddress)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_ExpiresAt' AND object_id = OBJECT_ID('RefreshToken')) CREATE INDEX IX_RefreshToken_ExpiresAt ON [RefreshToken](ExpiresAt)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RefreshToken_CreatedAt' AND object_id = OBJECT_ID('RefreshToken')) CREATE INDEX IX_RefreshToken_CreatedAt ON [RefreshToken](CreatedAt)",
                // Cart表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_CartStatus' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_CartStatus ON [Cart](CartStatus)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_LastModified' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_LastModified ON [Cart](LastModified)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Cart_ExpiresAt' AND object_id = OBJECT_ID('Cart')) CREATE INDEX IX_Cart_ExpiresAt ON [Cart](ExpiresAt)",
                // CartItem表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_CartGUID' AND object_id = OBJECT_ID('CartItem')) CREATE INDEX IX_CartItem_CartGUID ON [CartItem](CartGUID)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_ProductCode' AND object_id = OBJECT_ID('CartItem')) CREATE INDEX IX_CartItem_ProductCode ON [CartItem](ProductCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_AddedAt' AND object_id = OBJECT_ID('CartItem')) CREATE INDEX IX_CartItem_AddedAt ON [CartItem](AddedAt)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CartItem_LastUpdated' AND object_id = OBJECT_ID('CartItem')) CREATE INDEX IX_CartItem_LastUpdated ON [CartItem](LastUpdated)",
                // LocalSupplier表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_LocalSupplier_Status' AND object_id = OBJECT_ID('LocalSupplier')) CREATE INDEX IX_LocalSupplier_Status ON [LocalSupplier](Status)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_LocalSupplier_Name' AND object_id = OBJECT_ID('LocalSupplier')) CREATE INDEX IX_LocalSupplier_Name ON [LocalSupplier](Name)",
                // DailySalesStatistic表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_DailySalesStatistic_Date' AND object_id = OBJECT_ID('DailySalesStatistic')) CREATE INDEX IX_DailySalesStatistic_Date ON [DailySalesStatistic](Date)",
                // HourlySalesStatistic表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_HourlySalesStatistic_Date_BranchCode_Hour' AND object_id = OBJECT_ID('HourlySalesStatistic')) CREATE INDEX IX_HourlySalesStatistic_Date_BranchCode_Hour ON [HourlySalesStatistic](Date, BranchCode, Hour)",
                // StoreSalesStatistic表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreSalesStatistic_Date_TotalAmount' AND object_id = OBJECT_ID('StoreSalesStatistic')) CREATE INDEX IX_StoreSalesStatistic_Date_TotalAmount ON [StoreSalesStatistic](Date, TotalAmount)",
                // SupplierSalesStatistic表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SupplierSalesStatistic_Date_IsDomestic_TotalAmount' AND object_id = OBJECT_ID('SupplierSalesStatistic')) CREATE INDEX IX_SupplierSalesStatistic_Date_IsDomestic_TotalAmount ON [SupplierSalesStatistic](Date, IsDomestic, TotalAmount)",
                // StoreSupplierSalesDetail表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreSupplierSalesDetail_Date_BranchCode_SupplierCode' AND object_id = OBJECT_ID('StoreSupplierSalesDetail')) CREATE INDEX IX_StoreSupplierSalesDetail_Date_BranchCode_SupplierCode ON [StoreSupplierSalesDetail](Date, BranchCode, SupplierCode)",
                // ProductStoreDailySalesStatistic表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductStoreDailySalesStatistic_Date_Supplier_Product' AND object_id = OBJECT_ID('ProductStoreDailySalesStatistic')) CREATE INDEX IX_ProductStoreDailySalesStatistic_Date_Supplier_Product ON [ProductStoreDailySalesStatistic](Date, SupplierCode, ProductCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductStoreDailySalesStatistic_Date_Supplier_Quantity' AND object_id = OBJECT_ID('ProductStoreDailySalesStatistic')) CREATE INDEX IX_ProductStoreDailySalesStatistic_Date_Supplier_Quantity ON [ProductStoreDailySalesStatistic](Date, SupplierCode, TotalQuantity)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductStoreDailySalesStatistic_Date_Branch_Supplier_Product' AND object_id = OBJECT_ID('ProductStoreDailySalesStatistic')) CREATE INDEX IX_ProductStoreDailySalesStatistic_Date_Branch_Supplier_Product ON [ProductStoreDailySalesStatistic](Date, BranchCode, SupplierCode, ProductCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductStoreDailySalesStatistic_Branch_Product_Date' AND object_id = OBJECT_ID('ProductStoreDailySalesStatistic')) CREATE INDEX IX_ProductStoreDailySalesStatistic_Branch_Product_Date ON [ProductStoreDailySalesStatistic](BranchCode, ProductCode, Date) INCLUDE(TotalQuantity, TotalAmount)",
                // SalesStatisticRefreshState表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SalesStatisticRefreshState_Type_Date' AND object_id = OBJECT_ID('SalesStatisticRefreshState')) CREATE INDEX IX_SalesStatisticRefreshState_Type_Date ON [SalesStatisticRefreshState](StatisticType, Date)",
                // StoreRetailPrice表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_ProductCode_StoreCode' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_ProductCode_StoreCode ON [StoreRetailPrice](ProductCode, StoreCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_ProductCode_IsDeleted' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_ProductCode_IsDeleted ON [StoreRetailPrice](ProductCode, IsDeleted) INCLUDE(StoreCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_StoreCode' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_StoreCode ON [StoreRetailPrice](StoreCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_SupplierCode' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_SupplierCode ON [StoreRetailPrice](SupplierCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_PurchasePrice' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_PurchasePrice ON [StoreRetailPrice](PurchasePrice)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_StoreRetailPriceValue' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_StoreRetailPriceValue ON [StoreRetailPrice](StoreRetailPriceValue)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_DiscountRate' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_DiscountRate ON [StoreRetailPrice](DiscountRate)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_IsActive' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_IsActive ON [StoreRetailPrice](IsActive)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreRetailPrice_IsAutoPricing' AND object_id = OBJECT_ID('StoreRetailPrice')) CREATE INDEX IX_StoreRetailPrice_IsAutoPricing ON [StoreRetailPrice](IsAutoPricing)",
                // Product表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_ItemNumber' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_ItemNumber ON [Product]([ItemNumber]) WHERE [ItemNumber] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Barcode' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Barcode ON [Product]([Barcode]) WHERE [Barcode] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_ItemNumber_Lookup' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_ItemNumber_Lookup ON [Product]([ItemNumber]) INCLUDE([ProductCode], [Barcode]) WHERE [ItemNumber] IS NOT NULL AND [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Barcode_Lookup' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Barcode_Lookup ON [Product]([Barcode]) INCLUDE([ProductCode], [ItemNumber]) WHERE [Barcode] IS NOT NULL AND [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Active_ItemNumber_Lookup' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Active_ItemNumber_Lookup ON [Product]([IsDeleted], [IsActive], [ItemNumber]) INCLUDE([ProductCode], [Barcode], [LocalSupplierCode]) WHERE [ItemNumber] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Active_Barcode_Lookup' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Active_Barcode_Lookup ON [Product]([IsDeleted], [IsActive], [Barcode]) INCLUDE([ProductCode], [ItemNumber], [LocalSupplierCode]) WHERE [Barcode] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Active_LocalSupplier_ItemNumber' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Active_LocalSupplier_ItemNumber ON [Product]([IsDeleted], [IsActive], [LocalSupplierCode], [ItemNumber]) INCLUDE([ProductCode], [Barcode]) WHERE [LocalSupplierCode] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_ProductName' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_ProductName ON [Product](ProductName)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_IsActive' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_IsActive ON [Product](IsActive)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_IsSpecialProduct' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_IsSpecialProduct ON [Product](IsSpecialProduct)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_WarehouseCategoryGUID' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_WarehouseCategoryGUID ON [Product](WarehouseCategoryGUID)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_ProductType' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_ProductType ON [Product](ProductType)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_UpdatedAt' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_UpdatedAt ON [Product](UpdatedAt DESC)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_LocalSupplierCode' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_LocalSupplierCode ON [Product](LocalSupplierCode)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Product_Search' AND object_id = OBJECT_ID('Product')) CREATE INDEX IX_Product_Search ON [Product](ProductName, ProductCode, ItemNumber, Barcode)",
                // 扫码加购热路径：快速定位当前门店购物车和订单明细，避免大表回表扫描。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WareHouseOrder_StoreCode_FlowStatus_IsDeleted' AND object_id = OBJECT_ID('WareHouseOrder')) CREATE NONCLUSTERED INDEX [IX_WareHouseOrder_StoreCode_FlowStatus_IsDeleted] ON [WareHouseOrder] ([StoreCode], [FlowStatus], [IsDeleted])",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WareHouseOrder_CartScope' AND object_id = OBJECT_ID('WareHouseOrder')) CREATE NONCLUSTERED INDEX [IX_WareHouseOrder_CartScope] ON [WareHouseOrder] ([StoreCode], [FlowStatus], [IsDeleted], [CartOwnerUserGuid])",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WareHouseOrderDetails_OrderGUID_IsDeleted_ProductCode' AND object_id = OBJECT_ID('WareHouseOrderDetails')) CREATE NONCLUSTERED INDEX [IX_WareHouseOrderDetails_OrderGUID_IsDeleted_ProductCode] ON [WareHouseOrderDetails] ([OrderGUID], [IsDeleted], [ProductCode])",
                // ProductLocation 活跃映射覆盖索引，支撑订货明细货位排序的 ProductLocation -> Location 聚合。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductLocation_ProductCode_Active' AND object_id = OBJECT_ID('ProductLocation')) CREATE INDEX IX_ProductLocation_ProductCode_Active ON [ProductLocation]([ProductCode]) INCLUDE([LocationGuid]) WHERE [IsDeleted] = 0 AND [ProductCode] IS NOT NULL AND [LocationGuid] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductLocation_LocationGuid_Active' AND object_id = OBJECT_ID('ProductLocation')) CREATE INDEX IX_ProductLocation_LocationGuid_Active ON [ProductLocation]([LocationGuid]) INCLUDE([ProductCode]) WHERE [IsDeleted] = 0 AND [LocationGuid] IS NOT NULL AND [ProductCode] IS NOT NULL",
                // Location 列表过滤和远程排序索引，支撑仓库标签管理页快速分页查询。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_Code' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_Code ON [Location]([IsDeleted], [LocationCode]) INCLUDE([LocationGuid], [LocationType], [LocationBarcode], [Status], [UpdatedAt], [UpdatedBy])",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_Barcode' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_Barcode ON [Location]([IsDeleted], [LocationBarcode]) INCLUDE([LocationGuid], [LocationCode], [LocationType], [Status], [UpdatedAt], [UpdatedBy]) WHERE [LocationBarcode] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_Type_Status' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_Type_Status ON [Location]([IsDeleted], [LocationType], [Status]) INCLUDE([LocationGuid], [LocationCode], [LocationBarcode], [UpdatedAt], [UpdatedBy])",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_UpdatedAt' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_UpdatedAt ON [Location]([IsDeleted], [UpdatedAt]) INCLUDE([LocationGuid], [LocationCode], [LocationBarcode], [LocationType], [Status], [UpdatedBy])",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Location_List_UpdatedBy' AND object_id = OBJECT_ID('Location')) CREATE INDEX IX_Location_List_UpdatedBy ON [Location]([IsDeleted], [UpdatedBy]) INCLUDE([LocationGuid], [LocationCode], [LocationBarcode], [LocationType], [Status], [UpdatedAt]) WHERE [UpdatedBy] IS NOT NULL",
                // WarehouseProduct表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WarehouseProduct_ProductCode' AND object_id = OBJECT_ID('WarehouseProduct')) CREATE UNIQUE INDEX IX_WarehouseProduct_ProductCode ON [WarehouseProduct]([ProductCode])",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WarehouseProduct_ProductCode_NotDeleted' AND object_id = OBJECT_ID('WarehouseProduct')) CREATE INDEX IX_WarehouseProduct_ProductCode_NotDeleted ON [WarehouseProduct]([ProductCode]) WHERE [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WarehouseProduct_Table_CreatedAt' AND object_id = OBJECT_ID('WarehouseProduct')) CREATE INDEX IX_WarehouseProduct_Table_CreatedAt ON [WarehouseProduct]([CreatedAt] DESC, [ProductCode]) WHERE [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_WarehouseProduct_IsActive' AND object_id = OBJECT_ID('WarehouseProduct')) CREATE INDEX IX_WarehouseProduct_IsActive ON [WarehouseProduct]([IsActive])",
                // ProductSetCode表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_SetBarcode' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_SetBarcode ON [ProductSetCode]([SetBarcode]) WHERE [SetBarcode] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_ProductCode' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_ProductCode ON [ProductSetCode]([ProductCode])",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_Barcode_ProductCode' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_Barcode_ProductCode ON [ProductSetCode]([SetBarcode], [ProductCode]) WHERE [SetBarcode] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_ProductCode_SetBarcode_Active' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_ProductCode_SetBarcode_Active ON [ProductSetCode]([ProductCode], [SetBarcode]) WHERE [SetBarcode] IS NOT NULL AND [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_SetBarcode_Lookup' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_SetBarcode_Lookup ON [ProductSetCode]([SetBarcode]) INCLUDE([ProductCode], [SetItemNumber]) WHERE [SetBarcode] IS NOT NULL AND [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_ProductCode_Detail' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_ProductCode_Detail ON [ProductSetCode]([ProductCode], [SetBarcode]) INCLUDE([SetCodeId], [SetProductCode], [SetItemNumber], [SetPurchasePrice], [SetRetailPrice], [SetQuantity], [SetType], [IsActive]) WHERE [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_ProductCode_SetType_Active' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_ProductCode_SetType_Active ON [ProductSetCode]([ProductCode], [SetType]) WHERE [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_SetItemNumber' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_SetItemNumber ON [ProductSetCode]([SetItemNumber]) WHERE [SetItemNumber] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductSetCode_IsActive' AND object_id = OBJECT_ID('ProductSetCode')) CREATE INDEX IX_ProductSetCode_IsActive ON [ProductSetCode]([IsActive])",
                // StoreMultiCodeProduct表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreMultiCodeProduct_StoreCode_MultiCodeProductCode' AND object_id = OBJECT_ID('StoreMultiCodeProduct')) CREATE INDEX IX_StoreMultiCodeProduct_StoreCode_MultiCodeProductCode ON [StoreMultiCodeProduct]([StoreCode], [MultiCodeProductCode]) WHERE [StoreCode] IS NOT NULL AND [MultiCodeProductCode] IS NOT NULL AND [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreMultiCodeProduct_StoreCode_MultiBarcode' AND object_id = OBJECT_ID('StoreMultiCodeProduct')) CREATE INDEX IX_StoreMultiCodeProduct_StoreCode_MultiBarcode ON [StoreMultiCodeProduct]([StoreCode], [MultiBarcode]) WHERE [StoreCode] IS NOT NULL AND [MultiBarcode] IS NOT NULL AND [IsDeleted] = 0",
                // StoreClearancePrice表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreClearancePrice_ClearanceBarcode' AND object_id = OBJECT_ID('StoreClearancePrice')) CREATE INDEX IX_StoreClearancePrice_ClearanceBarcode ON [StoreClearancePrice]([ClearanceBarcode]) WHERE [ClearanceBarcode] IS NOT NULL",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreClearancePrice_ClearanceBarcode_Lookup' AND object_id = OBJECT_ID('StoreClearancePrice')) CREATE INDEX IX_StoreClearancePrice_ClearanceBarcode_Lookup ON [StoreClearancePrice]([ClearanceBarcode]) INCLUDE([ProductCode], [StoreCode]) WHERE [ClearanceBarcode] IS NOT NULL AND [IsDeleted] = 0",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreClearancePrice_ProductCode_StoreCode' AND object_id = OBJECT_ID('StoreClearancePrice')) CREATE INDEX IX_StoreClearancePrice_ProductCode_StoreCode ON [StoreClearancePrice]([ProductCode], [StoreCode])",
                // ScheduledTaskLog表的普通索引
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScheduledTaskLog_TaskType' AND object_id = OBJECT_ID('ScheduledTaskLog')) CREATE INDEX IX_ScheduledTaskLog_TaskType ON [ScheduledTaskLog](TaskType)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScheduledTaskLog_Status' AND object_id = OBJECT_ID('ScheduledTaskLog')) CREATE INDEX IX_ScheduledTaskLog_Status ON [ScheduledTaskLog](Status)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScheduledTaskLog_StartedAt' AND object_id = OBJECT_ID('ScheduledTaskLog')) CREATE INDEX IX_ScheduledTaskLog_StartedAt ON [ScheduledTaskLog](StartedAt)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScheduledTaskLog_TaskType_Status' AND object_id = OBJECT_ID('ScheduledTaskLog')) CREATE INDEX IX_ScheduledTaskLog_TaskType_Status ON [ScheduledTaskLog](TaskType, Status)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScheduledTaskLog_ScheduledTime' AND object_id = OBJECT_ID('ScheduledTaskLog')) CREATE INDEX IX_ScheduledTaskLog_ScheduledTime ON [ScheduledTaskLog](ScheduledTime)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScheduledTaskInstanceState_LastSeenAtUtc' AND object_id = OBJECT_ID('ScheduledTaskInstanceState')) CREATE INDEX IX_ScheduledTaskInstanceState_LastSeenAtUtc ON [ScheduledTaskInstanceState](LastSeenAtUtc)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_StoreOrderInvoiceEmailSendRecord_Order_Time' AND object_id = OBJECT_ID('StoreOrderInvoiceEmailSendRecord')) CREATE INDEX IX_StoreOrderInvoiceEmailSendRecord_Order_Time ON [StoreOrderInvoiceEmailSendRecord](StoreOrderUuid, SentAtUtc DESC, CreatedAtUtc DESC)",
                // ApplicationLog表的普通索引，支撑按项目、等级、时间、TraceId 查询。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApplicationLog_TimestampUtc' AND object_id = OBJECT_ID('ApplicationLog')) CREATE INDEX IX_ApplicationLog_TimestampUtc ON [ApplicationLog](TimestampUtc)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApplicationLog_Project_Level_Time' AND object_id = OBJECT_ID('ApplicationLog')) CREATE INDEX IX_ApplicationLog_Project_Level_Time ON [ApplicationLog](ProjectCode, Level, TimestampUtc)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApplicationLog_TraceId' AND object_id = OBJECT_ID('ApplicationLog')) CREATE INDEX IX_ApplicationLog_TraceId ON [ApplicationLog](TraceId)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApplicationLog_RequestPath' AND object_id = OBJECT_ID('ApplicationLog')) CREATE INDEX IX_ApplicationLog_RequestPath ON [ApplicationLog](RequestPath)",
                // MobileAppBuild表的索引，支撑 EAS buildId 幂等和最新 APK 查询。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MobileAppBuild_EasBuildId' AND object_id = OBJECT_ID('MobileAppBuild')) CREATE UNIQUE INDEX IX_MobileAppBuild_EasBuildId ON [MobileAppBuild](EasBuildId)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MobileAppBuild_Profile_CompletedAt' AND object_id = OBJECT_ID('MobileAppBuild')) CREATE INDEX IX_MobileAppBuild_Profile_CompletedAt ON [MobileAppBuild](BuildProfile, Platform, Status, CompletedAt)",
                // MobileAppDeviceStatus表的索引，支撑 hardwareId 幂等快照和在线/系统统计。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MobileAppDeviceStatus_HardwareId' AND object_id = OBJECT_ID('MobileAppDeviceStatus')) CREATE UNIQUE INDEX IX_MobileAppDeviceStatus_HardwareId ON [MobileAppDeviceStatus](HardwareId)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MobileAppDeviceStatus_LastSeen' AND object_id = OBJECT_ID('MobileAppDeviceStatus')) CREATE INDEX IX_MobileAppDeviceStatus_LastSeen ON [MobileAppDeviceStatus](LastSeenAtUtc)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MobileAppDeviceStatus_System_LastSeen' AND object_id = OBJECT_ID('MobileAppDeviceStatus')) CREATE INDEX IX_MobileAppDeviceStatus_System_LastSeen ON [MobileAppDeviceStatus](DeviceSystem, LastSeenAtUtc)",
                // MobileAppOtaUpdate表的索引，支撑 OTA group 幂等登记和渠道/runtime 查询。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MobileAppOtaUpdate_Group_Platform' AND object_id = OBJECT_ID('MobileAppOtaUpdate')) CREATE UNIQUE INDEX IX_MobileAppOtaUpdate_Group_Platform ON [MobileAppOtaUpdate](UpdateGroupId, Platform)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MobileAppOtaUpdate_Channel_Runtime_PublishedAt' AND object_id = OBJECT_ID('MobileAppOtaUpdate')) CREATE INDEX IX_MobileAppOtaUpdate_Channel_Runtime_PublishedAt ON [MobileAppOtaUpdate](Channel, RuntimeVersion, PublishedAt)",
                // ServiceApiToken 只按哈希查验明文 token，列表按创建时间倒序展示。
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ServiceApiToken_TokenHash' AND object_id = OBJECT_ID('ServiceApiToken')) CREATE UNIQUE INDEX IX_ServiceApiToken_TokenHash ON [ServiceApiToken](TokenHash)",
                "IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ServiceApiToken_CreatedAt' AND object_id = OBJECT_ID('ServiceApiToken')) CREATE INDEX IX_ServiceApiToken_CreatedAt ON [ServiceApiToken](CreatedAt)",
            };

            foreach (var sql in normalIndexStatements)
            {
                try
                {
                    _db.Ado.ExecuteCommand(sql);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"创建普通索引失败: {ex.Message}");
                }
            }

            Console.WriteLine("✓ 普通索引创建完成");
        }

        /// <summary>
        /// 检查索引是否存在
        /// </summary>
        public void CheckIndexes()
        {
            try
            {
                if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    CheckSqlServerIndexes();
                }
                else if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
                {
                    CheckPostgreSQLIndexes();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检查索引时出现错误: {ex.Message}");
            }
        }

        private void CheckSqlServerIndexes()
        {
            Console.WriteLine("检查数据库索引状态...");

            var tables = new[]
            {
                "User",
                "Role",
                "Store",
                "UserRole",
                "UserStore",
                "RefreshToken",
                "Product",
                "Cart",
                "CartItem",
                "WarehouseCategory",
                "WarehouseProduct",
                "ChinaSupplier",
                "DomesticProduct",
                "ProductPrefixCode",
                "ProductSetCode",
                "DomesticSetProduct",
                "DomesticProductCreationLog",
                "Location",
                "ProductLocation",
                "YIWU_Order",
                "YIWU_OrderDetail",
                "Container",
                "ContainerDetail",
            };

            foreach (var table in tables)
            {
                var sql =
                    $@"
                        SELECT 
                            i.name AS IndexName,
                            i.type_desc AS IndexType,
                            i.is_unique AS IsUnique,
                            STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS Columns
                        FROM sys.indexes i
                        INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                        WHERE i.object_id = OBJECT_ID('{table}')
                        GROUP BY i.name, i.type_desc, i.is_unique
                        ORDER BY i.name";

                var indexes = _db.Ado.SqlQuery<dynamic>(sql);

                Console.WriteLine($"\n{table}表索引:");
                foreach (var index in indexes)
                {
                    var unique = index.IsUnique ? "唯一" : "普通";
                    Console.WriteLine($"  - {index.IndexName} ({unique}): {index.Columns}");
                }
            }

            Console.WriteLine("\n✓ 索引检查完成");
        }

        private void CheckPostgreSQLIndexes()
        {
            Console.WriteLine("检查 PostgreSQL 数据库索引状态...");

            var tables = new[]
            {
                "User",
                "Role",
                "Store",
                "UserRole",
                "UserStore",
                "RefreshToken",
                "Product",
                "Cart",
                "CartItem",
                "WarehouseCategory",
                "WarehouseProduct",
                "ChinaSupplier",
                "DomesticProduct",
                "ProductPrefixCode",
                "ProductSetCode",
                "DomesticSetProduct",
                "DomesticProductCreationLog",
                "Location",
                "ProductLocation",
                "YIWU_Order",
                "YIWU_OrderDetail",
                "Container",
                "ContainerDetail",
            };

            foreach (var table in tables)
            {
                // 注意：SqlSugar在PG中可能使用带引号的表名，查询pg_indexes时tablename通常不带引号
                // 这里假设表名在数据库中是区分大小写的（因为创建时加了引号）
                // 如果数据库中实际表名是小写，这里可能查不到，需要根据实际情况调整
                var sql =
                    $@"
                    SELECT
                        indexname as IndexName,
                        indexdef as IndexDef
                    FROM pg_indexes
                    WHERE tablename = '{table}' OR tablename = '""{table}""' OR tablename = lower('{table}')
                    ORDER BY indexname";

                var indexes = _db.Ado.SqlQuery<dynamic>(sql);

                Console.WriteLine($"\n{table}表索引:");
                foreach (var index in indexes)
                {
                    string def = index.IndexDef;
                    bool isUnique = def.Contains(
                        "UNIQUE INDEX",
                        StringComparison.OrdinalIgnoreCase
                    );
                    string uniqueStr = isUnique ? "唯一" : "普通";

                    // 简单的解析列名，仅用于显示
                    string columns = "";
                    int startIndex = def.IndexOf('(');
                    int endIndex = def.LastIndexOf(')');
                    if (startIndex > 0 && endIndex > startIndex)
                    {
                        columns = def.Substring(startIndex + 1, endIndex - startIndex - 1);
                    }

                    Console.WriteLine($"  - {index.IndexName} ({uniqueStr}): {columns}");
                }
            }

            Console.WriteLine("\n✓ PostgreSQL 索引检查完成");
        }

        private static DbType GetDatabaseType(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return DbType.SqlServer;

            if (
                connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("Port=", StringComparison.OrdinalIgnoreCase)
            )
            {
                return DbType.PostgreSQL;
            }

            if (IsSqliteConnectionString(connectionString))
            {
                return DbType.Sqlite;
            }

            return DbType.SqlServer;
        }

        private static bool IsSqliteConnectionString(string connectionString)
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder
            {
                ConnectionString = connectionString,
            };

            if (
                builder.ContainsKey("Server")
                || builder.ContainsKey("Database")
                || builder.ContainsKey("Initial Catalog")
            )
            {
                return false;
            }

            var mode = GetConnectionValue(builder, "Mode");
            if (string.Equals(mode, "Memory", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var dataSource = GetConnectionValue(builder, "Data Source")
                ?? GetConnectionValue(builder, "DataSource")
                ?? GetConnectionValue(builder, "Filename");
            if (string.IsNullOrWhiteSpace(dataSource))
            {
                return false;
            }

            var value = dataSource.Trim();
            return string.Equals(value, ":memory:", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetConnectionValue(
            System.Data.Common.DbConnectionStringBuilder builder,
            string key
        )
        {
            return builder.TryGetValue(key, out var value) ? Convert.ToString(value) : null;
        }

        /// <summary>
        /// 🚀 在现有事务上下文中创建独立的查询连接，用于并发查询
        /// 避免多个并发任务共享同一个连接导致连接关闭错误
        /// </summary>
        /// <returns>独立的查询连接实例</returns>
        public ISqlSugarClient CreateConcurrentQueryConnection()
        {
            var config = _db.CurrentConnectionConfig;

            var concurrentDb = new SqlSugarClient(
                new ConnectionConfig()
                {
                    ConnectionString = config.ConnectionString,
                    DbType = config.DbType,
                    IsAutoCloseConnection = false,
                    InitKeyType = config.InitKeyType,
                    MoreSettings = new ConnMoreSettings()
                    {
                        IsAutoRemoveDataCache = config.MoreSettings.IsAutoRemoveDataCache,
                        IsWithNoLockQuery = config.MoreSettings.IsWithNoLockQuery,
                        SqlServerCodeFirstNvarchar = config.MoreSettings.SqlServerCodeFirstNvarchar,
                        DefaultCacheDurationInSeconds = 0,
                    },
                    ConfigureExternalServices = config.ConfigureExternalServices,
                }
            );

            concurrentDb.Ado.CommandTimeOut = _db.Ado.CommandTimeOut;
            SqlPerformanceAttachmentService.Attach(
                concurrentDb,
                $"{nameof(SqlSugarContext)}.ConcurrentQuery"
            );

            return concurrentDb;
        }

        /// <summary>
        /// 🚀 创建独立的数据库连接实例用于并发操作
        /// 每个并发任务使用独立的连接，避免连接冲突
        /// </summary>
        /// <param name="configuration">配置对象</param>
        /// <returns>独立的SqlSugar客户端实例</returns>
        public static ISqlSugarClient CreateConcurrentConnection(IConfiguration configuration)
        {
            // 确保连接字符串包含并发支持配置
            var connectionString =
                configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection 连接字符串未配置");

            var dbType = GetDatabaseType(connectionString);

            if (dbType == DbType.SqlServer)
            {
                // 添加高并发优化参数（从配置读取）
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
                    var maxPoolSize = configuration.GetValue<int?>("Database:MaxPoolSize") ?? 100;
                    connectionString += $";Max Pool Size={maxPoolSize}";
                }
                if (!connectionString.Contains("Min Pool Size", StringComparison.OrdinalIgnoreCase))
                {
                    var minPoolSize = configuration.GetValue<int?>("Database:MinPoolSize") ?? 5;
                    connectionString += $";Min Pool Size={minPoolSize}";
                }
                if (
                    !connectionString.Contains(
                        "Connection Timeout",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    var connectionTimeout =
                        configuration.GetValue<int?>("Database:ConnectionTimeoutSeconds") ?? 60;
                    connectionString += $";Connection Timeout={connectionTimeout}";
                }
                if (
                    !connectionString.Contains(
                        "Command Timeout",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    var commandTimeout =
                        configuration.GetValue<int?>("Database:ConcurrentCommandTimeoutSeconds")
                        ?? 30;
                    connectionString += $";Command Timeout={commandTimeout}";
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
            }

            var client = new SqlSugarClient(
                new ConnectionConfig()
                {
                    ConnectionString = connectionString,
                    DbType = dbType,
                    IsAutoCloseConnection = false, // 重要：自动关闭连接
                    InitKeyType = InitKeyType.Attribute,
                    MoreSettings = new ConnMoreSettings()
                    {
                        IsAutoRemoveDataCache = false,
                        IsWithNoLockQuery = dbType == DbType.SqlServer,
                        // 🚀 并发优化配置
                        SqlServerCodeFirstNvarchar = dbType == DbType.SqlServer,
                        DefaultCacheDurationInSeconds = 0, // 禁用缓存以避免并发问题
                        DisableNvarchar = false,
                    },
                    ConfigureExternalServices = new ConfigureExternalServices()
                    {
                        EntityService = (c, p) =>
                        {
                            // PostgreSQL 兼容性处理
                            if (dbType == DbType.PostgreSQL && !string.IsNullOrEmpty(p.DataType))
                            {
                                // 1. 将 nvarchar 映射为 varchar
                                if (
                                    p.DataType.Contains(
                                        "nvarchar",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    p.DataType = p.DataType.Replace(
                                        "nvarchar",
                                        "varchar",
                                        StringComparison.OrdinalIgnoreCase
                                    );
                                }

                                // 2. 将 (max) 映射为 text (PG不支持varchar(max))
                                if (
                                    p.DataType.Contains("(max)", StringComparison.OrdinalIgnoreCase)
                                )
                                {
                                    p.DataType = "text";
                                }
                            }
                        },
                    },
                }
            );

            // 设置较短的超时时间，避免长时间占用连接（从配置读取）
            var concurrentCommandTimeout = configuration.GetValue<int>(
                "Database:ConcurrentCommandTimeoutSeconds",
                30
            );
            client.Ado.CommandTimeOut = concurrentCommandTimeout;
            SqlPerformanceAttachmentService.Attach(
                client,
                $"{nameof(SqlSugarContext)}.Concurrent"
            );

            return client;
        }
    }
}
