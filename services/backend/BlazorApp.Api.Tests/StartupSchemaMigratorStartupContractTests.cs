using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StartupSchemaMigratorStartupContractTests
{
    [Fact]
    public async Task Program_普通启动必须先执行只读Schema门禁且拒绝遗留自动初始化开关()
    {
        var program = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Program.cs")
        );
        Assert.Contains("SchemaCommand.Parse(args)", program, StringComparison.Ordinal);
        Assert.Contains("SchemaCommandMode.Check", program, StringComparison.Ordinal);
        Assert.Contains("await coordinator.CheckAsync", program, StringComparison.Ordinal);
        Assert.Contains("Database:InitializeOnStartup", program, StringComparison.Ordinal);
        Assert.Contains("SCHEMA_LEGACY_INITIALIZE_ON_STARTUP_FORBIDDEN", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await StartupSchemaMigrator.EnsureAsync(dbContext.Db, app.Logger);",
            program,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StartupSchemaMigrator_POSM旧启动兜底不包含Mobile设备绑定Schema()
    {
        var migratorSource = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
            )
        );

        Assert.Contains("EnsurePosmAsync", migratorSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MobileDeviceActivationSchema.EnsureSql",
            migratorSource,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Mobile设备绑定Schema_条件抛错必须使用独立THROW语句()
    {
        var schemaSource = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Shared/Models/POSM/MobileDeviceActivationModels.cs"
            )
        );

        Assert.Equal(
            10,
            System.Text.RegularExpressions.Regex.Matches(
                schemaSource,
                @"(?m)^\s*;THROW 514\d{2},"
            ).Count
        );
        Assert.DoesNotMatch(@"(?m)^\s*THROW 514\d{2},", schemaSource);
    }

    [Fact]
    public async Task Program_绑定Mobile设备开通分阶段闸门配置()
    {
        var program = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Program.cs")
        );
        var normalizedProgram = System.Text.RegularExpressions.Regex.Replace(
            program,
            @"\s+",
            string.Empty
        );

        Assert.Contains(
            "MobileDeviceActivationOptions.SectionName",
            program,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "AddRateLimiter(MobileDeviceActivationRateLimits.Configure)",
            program,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "AddScoped<IMobileDeviceActivationService,MobileDeviceActivationService>()",
            normalizedProgram,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "AddScoped<IMobileDeviceActivationCodeManagementService,MobileDeviceActivationCodeManagementService>()",
            normalizedProgram,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "AddScoped<IMobileDeviceAccountTokenIssuer,MobileDeviceAccountTokenIssuer>()",
            normalizedProgram,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StartupSchemaMigrator_仓库商品历史表使用幂等索引和只追加触发器()
    {
        var repoRoot = FindRepoRoot();
        var migratorSource = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs")
        );
        var contextSource = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "services/backend/BlazorApp.Api/Data/SqlSugarContext.cs")
        );
        var historyServiceSource = await File.ReadAllTextAsync(
            Path.Combine(
                repoRoot,
                "services/backend/BlazorApp.Api/Services/React/WarehouseProductChangeHistoryService.cs"
            )
        );

        Assert.Contains("typeof(WarehouseProductChangeHistory)", contextSource, StringComparison.Ordinal);
        var tableListStart = contextSource.IndexOf("var tableTypes = new Type[]", StringComparison.Ordinal);
        var tableListEnd = contextSource.IndexOf("};", tableListStart, StringComparison.Ordinal);
        Assert.True(tableListStart >= 0 && tableListEnd > tableListStart);
        Assert.DoesNotContain(
            "typeof(WarehouseProductChangeHistory)",
            contextSource[tableListStart..tableListEnd],
            StringComparison.Ordinal
        );
        Assert.Contains("InitializeWarehouseProductChangeHistoryTable();", contextSource, StringComparison.Ordinal);
        Assert.Contains("WarehouseProductChangeHistory_Schema_Initialization", contextSource, StringComparison.Ordinal);
        Assert.Contains("_db.Ado.BeginTran();", contextSource, StringComparison.Ordinal);
        Assert.Contains("_db.DbMaintenance.IsAnyTable(tableName)", contextSource, StringComparison.Ordinal);
        Assert.Contains("EnsureWarehouseProductChangeHistoryIndexesAsync", migratorSource, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[WarehouseProductChangeHistory]'", migratorSource, StringComparison.Ordinal);
        Assert.Contains("IX_WarehouseProductChangeHistory_ProductCode_OccurredAtUtc_Id", migratorSource, StringComparison.Ordinal);
        Assert.Contains("IX_WarehouseProductChangeHistory_BatchGuid", migratorSource, StringComparison.Ordinal);
        Assert.Contains("UX_WarehouseProductChangeHistory_BatchGuid_ProductCode", migratorSource, StringComparison.Ordinal);
        Assert.Contains("WarehouseProductChangeHistory_Schema_Initialization", migratorSource, StringComparison.Ordinal);
        Assert.Contains("sys.sp_getapplock", migratorSource, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Transaction'", migratorSource, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER TRIGGER [dbo].[TR_WarehouseProductChangeHistory_AppendOnly]", migratorSource, StringComparison.Ordinal);
        Assert.Contains("INSTEAD OF UPDATE, DELETE", migratorSource, StringComparison.Ordinal);
        Assert.Contains("EnsureWarehouseProductChangeHistorySqliteSchemaAsync", migratorSource, StringComparison.Ordinal);
        Assert.Contains("TR_WarehouseProductChangeHistory_AppendOnly_Update", migratorSource, StringComparison.Ordinal);
        Assert.Contains("TR_WarehouseProductChangeHistory_AppendOnly_Delete", migratorSource, StringComparison.Ordinal);
        Assert.Contains("SELECT RAISE(ABORT, 'WarehouseProductChangeHistory is append-only.')", migratorSource, StringComparison.Ordinal);
        Assert.Contains("WarehouseProductChangeHistory_Batch_", historyServiceSource, StringComparison.Ordinal);
        Assert.Contains("_context.Db.Ado.Transaction == null", historyServiceSource, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Transaction'", historyServiceSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupSchemaMigrator_幂等补齐门店时区列且不回填()
    {
        var migrator = await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));

        Assert.Contains("await EnsureStoreTimeZoneSchemaAsync(db, logger);", migrator);
        Assert.Contains("private static async Task EnsureStoreTimeZoneSchemaAsync", migrator);
        Assert.Contains("OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL", migrator);
        Assert.Contains("COL_LENGTH('dbo.Store', 'TimeZoneId') IS NULL", migrator);
        Assert.Contains("ALTER TABLE [dbo].[Store] ADD [TimeZoneId] nvarchar(80) NULL;", migrator);
        Assert.Contains("@LockOwner = N'Transaction'", migrator);
        Assert.Contains("COMMIT TRANSACTION", migrator);
        Assert.DoesNotContain("UPDATE [dbo].[Store] SET [TimeZoneId]", migrator);
    }

    [Fact]
    public async Task StartupSchemaMigrator_幂等补齐门店退换货政策列且不回填()
    {
        var migrator = await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));

        Assert.Contains("await EnsureStoreReturnPolicySchemaAsync(db, logger);", migrator);
        Assert.Contains("private static async Task EnsureStoreReturnPolicySchemaAsync", migrator);
        Assert.Contains("StoreReturnPolicy_Schema_Initialization", migrator);
        Assert.Contains("OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL", migrator);
        Assert.Contains("COL_LENGTH('dbo.Store', 'ReturnPolicy') IS NULL", migrator);
        Assert.Contains("ALTER TABLE [dbo].[Store] ADD [ReturnPolicy] nvarchar(500) NULL;", migrator);
        Assert.Contains("@LockOwner = N'Transaction'", migrator);
        Assert.Contains("COMMIT TRANSACTION", migrator);
        Assert.DoesNotContain("UPDATE [dbo].[Store] SET [ReturnPolicy]", migrator);
    }

    [Fact]
    public async Task StoreDtos_退换货政策整表DTO与批量补丁保持各自校验语义()
    {
        var dtoSource = await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Shared/DTOs/StoreDtos.cs"));

        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(
            dtoSource,
            @"public string\? ReturnPolicy \{ get; set; \}").Count);
        // 四个整表 DTO 继续使用 DataAnnotation；批量补丁只在 fields 选中时由服务校验。
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(
            dtoSource,
            @"\[StringLength\(500, ErrorMessage = ""退换货政策长度不能超过500个字符""\)\]").Count);
    }

    [Fact]
    public async Task StoreDtos_代金券使用说明与分期条款随退换货政策同位置声明且上限600()
    {
        var dtoSource = await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Shared/DTOs/StoreDtos.cs"));

        // 与 ReturnPolicy 一样出现在 Create/Update/Store/StoreDetail 四个整表 DTO 与批量补丁共 5 处。
        foreach (var property in new[] { "VoucherTerms", "InstallmentTerms" })
        {
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(
                dtoSource,
                @"public string\? " + property + @" \{ get; set; \}").Count);
        }

        // 四个整表 DTO 用 DataAnnotation 限 600；批量补丁只在 fields 选中时由服务校验。
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(
            dtoSource,
            @"\[StringLength\(600, ErrorMessage = ""代金券使用说明长度不能超过600个字符""\)\]").Count);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(
            dtoSource,
            @"\[StringLength\(600, ErrorMessage = ""分期条款长度不能超过600个字符""\)\]").Count);
    }

    [Fact]
    public async Task 代金券使用说明与分期条款只走版本迁移_启动补列与Hq同步都不触碰()
    {
        var root = FindRepoRoot();
        var migrator = await File.ReadAllTextAsync(Path.Combine(
            root,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));
        // 生产启动不迁移，新字段只能走 20261008.002-store-receipt-terms 版本迁移，不得再混入启动期补列。
        Assert.DoesNotContain("VoucherTerms", migrator, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallmentTerms", migrator, StringComparison.Ordinal);

        var serviceSource = await File.ReadAllTextAsync(Path.Combine(
            root,
            "services/backend/BlazorApp.Api/Services/StoreService.cs"));
        var hqStart = serviceSource.IndexOf(
            "private async Task UpsertHqBranchAsync",
            StringComparison.Ordinal);
        var hqEnd = serviceSource.IndexOf(
            "private static SugarParameter ToParameter",
            StringComparison.Ordinal);
        Assert.True(hqStart >= 0 && hqEnd > hqStart, "无法定位 StoreService 的 HQ 同步代码区间");
        var hqRegion = serviceSource[hqStart..hqEnd];
        Assert.DoesNotContain("VoucherTerms", hqRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallmentTerms", hqRegion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 贺卡填报供应商与批次列只走版本迁移_启动补列不触碰()
    {
        var migrator = await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));
        // 贺卡表随基线登记，三列只能由 20261009.001-seasonal-card-supplier-batch 加出，不得混入启动期补列。
        // 只断言贺卡表名与权限码：BatchGuid 这个列名仓库进货变更历史表也在用。
        Assert.DoesNotContain("SeasonalCardRemainingSubmission", migrator, StringComparison.Ordinal);
        Assert.DoesNotContain("SeasonalCards.Remaining.ViewAllStores", migrator, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 客显广告版式与素材宽高列只走版本迁移_启动补列不触碰()
    {
        var root = FindRepoRoot();
        var migrator = await File.ReadAllTextAsync(Path.Combine(
            root,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));
        // 广告表随基线登记，三列与两个具名约束只能由 20261009.002-advertisement-orientation 加出，不得混入启动期补列。
        Assert.DoesNotContain("Advertisement", migrator, StringComparison.Ordinal);
        Assert.DoesNotContain("DF_Advertisement_Orientation", migrator, StringComparison.Ordinal);
        Assert.DoesNotContain("CK_Advertisement_Orientation", migrator, StringComparison.Ordinal);

        // 实体特性须与迁移列定义一致：POS API 启动对广告表执行 CodeFirst.InitTables，定义不一致会被自动改表。
        var entity = await File.ReadAllTextAsync(Path.Combine(
            root,
            "services/backend/BlazorApp.Shared/Models/HBweb/Advertisement.cs"));
        Assert.Contains(
            "[SugarColumn(Length = 16, IsNullable = false, DefaultValue = AdvertisementOrientations.Any)]",
            entity,
            StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            entity,
            @"\[SugarColumn\(IsNullable = true\)\]\s+public int\? Media(Width|Height) \{ get; set; \}").Count);
    }

    [Fact]
    public async Task StoreService_Hq同步不写入或覆盖退换货政策()
    {
        var serviceSource = await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Services/StoreService.cs"));

        var hqStart = serviceSource.IndexOf(
            "private async Task UpsertHqBranchAsync",
            StringComparison.Ordinal);
        var hqEnd = serviceSource.IndexOf(
            "private static SugarParameter ToParameter",
            StringComparison.Ordinal);
        Assert.True(hqStart >= 0 && hqEnd > hqStart, "无法定位 StoreService 的 HQ 同步代码区间");

        var hqRegion = serviceSource[hqStart..hqEnd];
        Assert.Contains("BranchName = store.StoreName", hqRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("ReturnPolicy", hqRegion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupSchemaMigrator_创建员工敏感审批表和Pending过滤唯一索引()
    {
        var migrator = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
            )
        );

        Assert.Contains("EnsureEmployeeProfileSensitiveChangeSchemaAsync", migrator);
        Assert.Contains("EmployeeProfileSensitiveChangeRequest", migrator);
        Assert.Contains("SensitiveRevision", migrator);
        Assert.Contains("UX_EmployeeProfileSensitiveChangeRequest_User_Pending", migrator);
        Assert.Contains("WHERE [Status] = 0", migrator);
        Assert.Contains("SensitiveChangeRequestId", migrator);
        Assert.Contains("[ChangedFieldsJson] nvarchar(1000) NULL", migrator);
        Assert.Contains(
            "COL_LENGTH('dbo.EmployeeProfileSensitiveChangeRequest', 'ChangedFieldsJson') IS NULL",
            migrator
        );
        Assert.Contains("[RemoveIdentityPhoto] bit NOT NULL", migrator);
        Assert.Contains(
            "COL_LENGTH('dbo.EmployeeProfileSensitiveChangeRequest', 'RemoveIdentityPhoto') IS NULL",
            migrator
        );

        var migration = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Data/Migrations/20260715_CreateEmployeeProfileSensitiveChangeRequest.sql"
            )
        );
        Assert.Contains("[ChangedFieldsJson] nvarchar(1000) NULL", migration);
        Assert.Contains(
            "COL_LENGTH('dbo.EmployeeProfileSensitiveChangeRequest', 'ChangedFieldsJson') IS NULL",
            migration
        );
        Assert.Contains("[RemoveIdentityPhoto] bit NOT NULL", migration);
        Assert.Contains(
            "COL_LENGTH('dbo.EmployeeProfileSensitiveChangeRequest', 'RemoveIdentityPhoto') IS NULL",
            migration
        );
    }

    [Fact]
    public async Task AttendanceQrSchemaScripts_use_the_same_transaction_scoped_application_lock()
    {
        var repoRoot = FindRepoRoot();
        var hbposInitializer = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "apps/pos-wpf/src/Hbpos.Api/Services/AttendanceQrKeySchemaInitializer.cs"));
        var backendMigrator = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));

        var hbposSqlStart = hbposInitializer.IndexOf(
            "EnsureAttendanceQrKeySchemaSql = \"\"\"",
            StringComparison.Ordinal);
        var hbposSqlEnd = hbposInitializer.IndexOf(
            "public Task InitializeAsync",
            hbposSqlStart,
            StringComparison.Ordinal);
        var backendSqlStart = backendMigrator.IndexOf(
            "private static async Task EnsureAttendanceQrSchemaAsync",
            StringComparison.Ordinal);
        var backendSqlEnd = backendMigrator.IndexOf(
            "private static async Task EnsureUserStorePosPermissionSchemaAsync",
            backendSqlStart,
            StringComparison.Ordinal);

        Assert.True(hbposSqlStart >= 0 && hbposSqlEnd > hbposSqlStart);
        Assert.True(backendSqlStart >= 0 && backendSqlEnd > backendSqlStart);

        foreach (var source in new[]
        {
            hbposInitializer[hbposSqlStart..hbposSqlEnd],
            backendMigrator[backendSqlStart..backendSqlEnd]
        })
        {
            Assert.Contains("SET XACT_ABORT ON", source);
            Assert.Contains("BEGIN TRANSACTION", source);
            Assert.Contains("sys.sp_getapplock", source);
            Assert.Contains("@Resource = N'AttendancePosQrKey_Schema_Initialization'", source);
            Assert.Contains("@LockMode = N'Exclusive'", source);
            Assert.Contains("@LockOwner = N'Transaction'", source);
            Assert.Contains("@LockTimeout = 30000", source);
            Assert.Contains("IF @AttendanceQrSchemaLockResult < 0", source);
            Assert.Contains("THROW 51010", source);
            Assert.Contains("COMMIT TRANSACTION", source);
            Assert.Contains("BEGIN CATCH", source);
            Assert.Contains("ROLLBACK TRANSACTION", source);
            Assert.Contains("THROW;", source);
            Assert.Contains("WITH (TABLOCKX, HOLDLOCK)", source);
        }
    }

    [Fact]
    public async Task AttendanceWorkSessionSchema_IsWiredAndIdempotentlyPreservesPunchEvidence()
    {
        var repoRoot = FindRepoRoot();
        var migration = (await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"))).ReplaceLineEndings("\n");

        Assert.Contains("await EnsureAttendanceWorkSessionSchemaAsync(db, logger);", migration);
        Assert.Contains("private static async Task EnsureAttendanceWorkSessionSchemaAsync", migration);
        Assert.Contains("COL_LENGTH('dbo.AttendancePunch', 'SupersedesPunchGuid') IS NULL", migration);
        Assert.Contains("COL_LENGTH('dbo.AttendancePunch', 'AdjustmentGuid') IS NULL", migration);
        Assert.Contains("COL_LENGTH('dbo.AttendanceApproval', 'CandidateOvertimeMinutes') IS NULL", migration);
        Assert.Contains("OBJECT_ID(N'[dbo].[AttendancePunchAdjustment]', N'U') IS NULL", migration);
        Assert.Contains("UX_AttendancePunch_AdjustmentGuid", migration);
        Assert.Contains("UX_AttendancePunch_SupersedesPunchGuid", migration);
        Assert.Contains("UX_AttendanceApproval_Pending_Source", migration);
        Assert.Contains("UX_AttendancePunchAdjustment_ActiveOriginalPunchGuid", migration);
        Assert.Contains("PARTITION BY [SourceType], [SourceGuid]", migration);
        Assert.Contains("[ReviewStatus] = N''Cancelled''", migration);
        Assert.Contains("SET [Remark] = CONCAT", migration);
        Assert.Contains("[IsDeleted] = 1", migration);
        Assert.DoesNotContain("[AdjustmentGuid] = NULL", migration);
        Assert.DoesNotContain("[SupersedesPunchGuid] = NULL", migration);
        Assert.DoesNotContain("UPDATE [dbo].[AttendancePunch] SET [PunchTime", migration);
    }

    [Fact]
    public async Task AttendanceWorkSessionSchema_ActiveOriginalPunchUniqueIndexUsesBaseColumnsOnly()
    {
        var migration = (await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"))).ReplaceLineEndings("\n");
        var normalizeNullableDeleteFlag = migration.IndexOf(
            "UPDATE [dbo].[AttendancePunchAdjustment] SET [IsDeleted] = 0 WHERE [IsDeleted] IS NULL",
            StringComparison.Ordinal);
        var createActiveOriginalPunchIndex = migration.IndexOf(
            "CREATE UNIQUE INDEX [UX_AttendancePunchAdjustment_ActiveOriginalPunchGuid]",
            StringComparison.Ordinal);

        Assert.Contains(
            "ON [dbo].[AttendancePunchAdjustment]([OriginalPunchGuid])\n" +
            "            WHERE [OriginalPunchGuid] IS NOT NULL\n" +
            "              AND [Status] IN (N''Pending'', N''Applied'')\n" +
            "              AND [IsDeleted] = 0",
            migration);
        Assert.DoesNotContain("ADD [ActiveOriginalPunchGuid] AS", migration);
        Assert.DoesNotContain("WHERE [ActiveOriginalPunchGuid] IS NOT NULL", migration);
        Assert.True(normalizeNullableDeleteFlag >= 0, "建立唯一索引前必须把历史 NULL 软删除标记规范为 0。 ");
        Assert.True(
            createActiveOriginalPunchIndex > normalizeNullableDeleteFlag,
            "历史软删除标记规范化必须发生在唯一索引创建之前。 ");
    }

    [Fact]
    public async Task AttendanceWorkSessionSchema_DedupKeepsAdjustmentAndPunchAsOneCanonicalChain()
    {
        var migration = (await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"))).ReplaceLineEndings("\n");
        var start = migration.IndexOf("#CanonicalAttendanceAdjustment", StringComparison.Ordinal);
        var validAppliedPunch = migration.IndexOf(
            "punch.[PunchGuid] = adjustment.[AppliedPunchGuid]",
            start,
            StringComparison.Ordinal);
        var reviewedAt = migration.IndexOf(
            "adjustment.[ReviewedAt], adjustment.[CreatedAt]",
            start,
            StringComparison.Ordinal);
        var pairedPunchJoin = migration.IndexOf(
            "canonical.[OriginalPunchGuid] = adjustment.[OriginalPunchGuid]",
            reviewedAt,
            StringComparison.Ordinal);
        var matchingAdjustmentGuard = migration.IndexOf(
            "adjustment.[AdjustmentGuid] <> canonical.[AdjustmentGuid]",
            pairedPunchJoin,
            StringComparison.Ordinal);

        // A 先申请但晚审批、B 后申请但早审批时，先按有效 AppliedPunch 链和 ReviewedAt 选 B，不能按 CreatedAt 各自排名。
        Assert.True(start >= 0, "迁移必须先物化唯一 canonical adjustment。 ");
        Assert.True(validAppliedPunch > start, "有效 AppliedPunchGuid/实际 Punch 链必须是最高优先级。 ");
        Assert.True(reviewedAt > validAppliedPunch, "相同链证据下必须先按审批时间，再按申请时间兜底。 ");
        Assert.True(pairedPunchJoin > reviewedAt, "Punch 去重必须复用已经选中的 original-adjustment 映射。 ");
        Assert.True(matchingAdjustmentGuard > pairedPunchJoin, "非 canonical adjustment 与其 Punch 必须成对停用。 ");
        Assert.Contains("CASE WHEN punch.[PunchGuid] = adjustment.[AppliedPunchGuid] THEN 0", migration);
        Assert.DoesNotContain(
            "PARTITION BY [OriginalPunchGuid]\n            ORDER BY CASE WHEN [Status]",
            migration);
    }

    [Fact]
    public async Task StartupSchemaMigrator_考勤二维码密钥与幂等索引使用追加式迁移()
    {
        var migrator = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));

        Assert.Contains("AttendancePosQrKey", migrator);
        Assert.Contains("[ProtectedKey] nvarchar(max) NOT NULL", migrator);
        Assert.Contains("UX_AttendancePosQrKey_ActiveDevice", migrator);
        Assert.Contains("PARTITION BY [DeviceCode]", migrator);
        Assert.Contains("UX_AttendancePosQrKey_ActiveHardware", migrator);
        Assert.Contains("PARTITION BY [HardwareId]", migrator);
        Assert.Contains("UX_AttendancePunch_User_QrTokenId", migrator);
        Assert.Contains("WHERE [QrTokenId] IS NOT NULL", migrator);
    }

    [Fact]
    public async Task StartupSchemaMigrator_考勤QrToken唯一索引在补列后动态编译()
    {
        var migrator = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"));
        var attendanceSchemaStart = migrator.IndexOf(
            "private static async Task EnsureAttendanceQrSchemaAsync",
            StringComparison.Ordinal);
        var attendanceSchemaEnd = migrator.IndexOf(
            "private static async Task EnsureUserStorePosPermissionSchemaAsync",
            attendanceSchemaStart,
            StringComparison.Ordinal);

        Assert.True(
            attendanceSchemaStart >= 0 && attendanceSchemaEnd > attendanceSchemaStart,
            "必须能定位考勤二维码启动迁移方法。");

        var attendanceSchema = migrator[attendanceSchemaStart..attendanceSchemaEnd];
        var addQrTokenColumnIndex = attendanceSchema.IndexOf(
            "ALTER TABLE [dbo].[AttendancePunch] ADD [QrTokenId] nvarchar(50) NULL;",
            StringComparison.Ordinal);
        var createQrTokenIndexIndex = attendanceSchema.IndexOf(
            "EXEC(N'CREATE UNIQUE INDEX [UX_AttendancePunch_User_QrTokenId]",
            StringComparison.Ordinal);

        // 关键位置：SQL Server 会先编译静态 batch；新增列后的索引必须延迟到 EXEC 时再编译。
        Assert.True(addQrTokenColumnIndex >= 0, "考勤迁移必须幂等补齐 QrTokenId 列。");
        Assert.True(
            createQrTokenIndexIndex > addQrTokenColumnIndex,
            "QrTokenId 唯一索引必须在补列后通过动态 SQL 建立独立编译边界。"
        );
        Assert.Contains("WHERE [QrTokenId] IS NOT NULL;');", attendanceSchema);
    }

    [Fact]
    public async Task StartupSchemaMigrator_创建用户分店Pos权限表和唯一索引()
    {
        var migrator = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
            )
        );

        Assert.Contains("HBwebSysUserStorePosPermissions", migrator);
        Assert.Contains("IX_UserStorePosPermission_Scope_Unique", migrator);
        Assert.Contains("[UserGuid], [StoreGuid], [PermissionCode]", migrator);
    }

    [Fact]
    public async Task Program_启动通过迁移协调器管理统一StartupSchemaMigrator()
    {
        var repoRoot = FindRepoRoot();
        var programPath = Path.Combine(repoRoot, "services/backend/BlazorApp.Api/Program.cs");
        var schemaCommandPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/SchemaMigrations/SchemaCommand.cs"
        );
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );

        var program = await File.ReadAllTextAsync(programPath);
        var schemaCommand = await File.ReadAllTextAsync(schemaCommandPath);
        var migrator = await File.ReadAllTextAsync(migratorPath);

        var coordinatorIndex = program.IndexOf("SchemaMigrationCoordinator", StringComparison.Ordinal);
        var appRunIndex = program.IndexOf("app.Run();", StringComparison.Ordinal);
        Assert.True(coordinatorIndex >= 0, "Program 必须委托 SchemaMigrationCoordinator 管理数据库迁移与就绪门禁。");
        Assert.True(
            appRunIndex > coordinatorIndex,
            "迁移命令处理和正常启动就绪门禁必须在应用开始监听前完成。"
        );
        Assert.Contains("var schemaCommand = SchemaCommand.Parse(args);", program, StringComparison.Ordinal);
        // 命令字面量由独立解析器统一维护；Program 只按解析后的模式构建最小 schema host。
        Assert.Contains("private const string MigrateArgument = \"--schema=migrate\";", schemaCommand, StringComparison.Ordinal);
        Assert.Contains("private const string CheckArgument = \"--schema=check\";", schemaCommand, StringComparison.Ordinal);
        Assert.Contains("MigrateArgument => new SchemaCommand(SchemaCommandMode.Migrate, null)", schemaCommand, StringComparison.Ordinal);
        Assert.Contains("CheckArgument => new SchemaCommand(SchemaCommandMode.Check, null)", schemaCommand, StringComparison.Ordinal);
        var explicitSchemaStart = program.IndexOf(
            "if (schemaCommand.Mode != SchemaCommandMode.Server)",
            StringComparison.Ordinal
        );
        var regularServiceRegistration = program.IndexOf(
            "// ===================== 服务注册区域 =====================",
            StringComparison.Ordinal
        );
        Assert.True(
            explicitSchemaStart >= 0 && regularServiceRegistration > explicitSchemaStart,
            "显式 schema 命令必须在常规 HTTP 服务注册前进入独立分支。"
        );
        var explicitSchemaBranch = program[explicitSchemaStart..regularServiceRegistration];
        Assert.Contains("schemaApp = builder.Build();", explicitSchemaBranch, StringComparison.Ordinal);
        Assert.Contains("SCHEMA_HOST_BUILD_FAILED", explicitSchemaBranch, StringComparison.Ordinal);
        Assert.Contains("SchemaExitCodes.ConfigurationError", explicitSchemaBranch, StringComparison.Ordinal);
        Assert.Contains("ExecuteSchemaOperationAsync(schemaApp.Services, schemaCommand.Mode)", explicitSchemaBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("AddControllers", explicitSchemaBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSwaggerGen", explicitSchemaBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService", explicitSchemaBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("dbContext.EnsureLoginSessionSchema();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("dbContext.CreateTable();", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await StartupSchemaMigrator.EnsureAsync(dbContext.Db, app.Logger);",
            program,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("hqDbContext.CheckTables();", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await LocalSupplierInvoiceStartupSchemaMigrator.EnsureAsync(dbContext.Db, app.Logger);",
            program,
            StringComparison.Ordinal
        );

        var localSupplierIndex = migrator.IndexOf(
            "await LocalSupplierInvoiceStartupSchemaMigrator.EnsureAsync(db, logger);",
            StringComparison.Ordinal
        );
        var mobileBuildIndex = migrator.IndexOf(
            "await EnsureMobileAppBuildSchemaAsync(db, logger);",
            StringComparison.Ordinal
        );
        var mobileDeviceStatusIndex = migrator.IndexOf(
            "await EnsureMobileAppDeviceStatusSchemaAsync(db, logger);",
            StringComparison.Ordinal
        );
        var serviceTokenIndex = migrator.IndexOf(
            "await EnsureServiceApiTokenSchemaAsync(db, logger);",
            StringComparison.Ordinal
        );
        var wpfReleaseIndex = migrator.IndexOf(
            "await EnsureWpfAppReleaseSchemaAsync(db, logger);",
            StringComparison.Ordinal
        );

        // 关键位置：统一 migrator 必须先保留既有 LocalSupplier 兜底，再补移动端 APK/OTA 表结构。
        Assert.True(localSupplierIndex >= 0, "统一启动迁移必须继续保留 LocalSupplier 发票表兜底。");
        Assert.True(
            mobileBuildIndex > localSupplierIndex,
            "移动端 APK/OTA 表结构迁移必须在 LocalSupplier 兜底之后执行。"
        );
        Assert.True(
            mobileDeviceStatusIndex > mobileBuildIndex,
            "App 设备状态快照表必须在移动端 APK/OTA 表之后独立启动自举。"
        );
        Assert.True(
            serviceTokenIndex > mobileDeviceStatusIndex,
            "Service API Token 表必须随移动端 OTA 管理链路一起启动自举。"
        );
        Assert.True(
            wpfReleaseIndex > serviceTokenIndex,
            "WPF 发布表结构迁移必须在移动端发布表迁移之后执行，且保持独立表结构。"
        );
        Assert.Contains("IF OBJECT_ID('MobileAppBuild', 'U') IS NULL", migrator);
        Assert.Contains("IF OBJECT_ID('MobileAppOtaUpdate', 'U') IS NULL", migrator);
        Assert.Contains("IF OBJECT_ID('MobileAppDeviceStatus', 'U') IS NULL", migrator);
        Assert.Contains("SET [HardwareId] = CONCAT(''legacy-''", migrator);
        Assert.Contains(
            "ALTER TABLE [MobileAppDeviceStatus] ALTER COLUMN [HardwareId] nvarchar(120) NOT NULL",
            migrator
        );
        Assert.DoesNotContain(
            "ALTER TABLE [MobileAppDeviceStatus] ADD [HardwareId] nvarchar(120) NOT NULL DEFAULT('')",
            migrator
        );
        Assert.Contains("IF OBJECT_ID('ServiceApiToken', 'U') IS NULL", migrator);
        Assert.Contains("IF OBJECT_ID('WpfAppRelease', 'U') IS NULL", migrator);
        Assert.Contains("IF OBJECT_ID('WpfUpdatePolicy', 'U') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WareHouseOrder', 'CartOwnerUserGuid') IS NULL", migrator);
        Assert.Contains("ADD [CartOwnerUserGuid] nvarchar(50) NULL", migrator);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_WareHouseOrder_CartScope]", migrator);
        Assert.Contains("CREATE UNIQUE INDEX [IX_MobileAppBuild_AppKey_EasBuildId]", migrator);
        Assert.Contains(
            "CREATE UNIQUE INDEX [IX_MobileAppOtaUpdate_AppKey_Group_Platform]",
            migrator
        );
        Assert.Contains("CREATE UNIQUE INDEX [IX_MobileAppDeviceStatus_HardwareId]", migrator);
        Assert.Contains("CREATE INDEX [IX_MobileAppDeviceStatus_System_LastSeen]", migrator);
        Assert.Contains("CREATE UNIQUE INDEX [IX_ServiceApiToken_TokenHash]", migrator);
        Assert.Contains("CREATE UNIQUE INDEX [IX_WpfAppRelease_Channel_Version]", migrator);
        Assert.Contains("CREATE UNIQUE INDEX [IX_WpfUpdatePolicy_Channel]", migrator);
        Assert.Contains("IF COL_LENGTH('MobileAppBuild', 'CosArtifactUrl') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'Channel') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'Version') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'FileName') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'FileSize') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'Sha256') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'DownloadUrl') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'InstallerType') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'ReleaseNotes') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'PublishedAt') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfAppRelease', 'CreatedAt') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfUpdatePolicy', 'Channel') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfUpdatePolicy', 'TargetVersion') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfUpdatePolicy', 'MinimumSupportedVersion') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('WpfUpdatePolicy', 'ForceUpdate') IS NULL", migrator);
        Assert.Contains("IF COL_LENGTH('CashRegisterUsers', 'UserGUID') IS NULL", migrator);
        Assert.Contains("ADD [UserGUID] nvarchar(50) NULL", migrator);
        Assert.Contains("SET [UserGUID] = LTRIM(RTRIM([UserGUID]))", migrator);
        Assert.Contains("INNER JOIN [User] AS [linkedUser]", migrator);
        Assert.Contains(
            "ON [linkedUser].[UserGUID] = LTRIM(RTRIM([cashier].[OperatorUser]))",
            migrator
        );
        Assert.Contains("CREATE UNIQUE INDEX [IX_CashRegisterUsers_UserGUID_Active]", migrator);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [UserGUID]", migrator);
        Assert.Contains("CREATE UNIQUE INDEX [IX_CashRegisterUsers_UserBarcode_Active]", migrator);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [UserBarcode]", migrator);
        Assert.Contains("COL_LENGTH('CashRegisterUsers', 'Status') IS NOT NULL", migrator);
        Assert.Contains("COL_LENGTH('CashRegisterUsers', 'Id') IS NOT NULL", migrator);
        Assert.Contains("COL_LENGTH('CashRegisterUsers', 'LastModifyDate') IS NOT NULL", migrator);
        Assert.Contains("COL_LENGTH('CashRegisterUsers', 'CreateDate') IS NOT NULL", migrator);
        Assert.Contains(
            "AND ([UserBarcode] IS NULL OR LTRIM(RTRIM([UserBarcode])) = '')",
            migrator
        );
        Assert.Contains(
            "WHERE [Status] = 1 AND [UserBarcode] IS NOT NULL AND [UserBarcode] <> ''",
            migrator
        );

        var wpfReleaseColumnBackfillIndex = migrator.IndexOf(
            "IF COL_LENGTH('WpfAppRelease', 'PublishedAt') IS NULL",
            StringComparison.Ordinal
        );
        var wpfReleasePublishedIndex = migrator.IndexOf(
            "CREATE INDEX [IX_WpfAppRelease_Channel_PublishedAt]",
            StringComparison.Ordinal
        );
        var wpfPolicyColumnBackfillIndex = migrator.IndexOf(
            "IF COL_LENGTH('WpfUpdatePolicy', 'Channel') IS NULL",
            StringComparison.Ordinal
        );
        var wpfPolicyIndex = migrator.IndexOf(
            "CREATE UNIQUE INDEX [IX_WpfUpdatePolicy_Channel]",
            StringComparison.Ordinal
        );
        var cashierUserGuidColumnIndex = migrator.IndexOf(
            "ADD [UserGUID] nvarchar(50) NULL",
            StringComparison.Ordinal
        );
        var cashierUserGuidNormalizeIndex = migrator.IndexOf(
            "SET [UserGUID] = LTRIM(RTRIM([UserGUID]))",
            StringComparison.Ordinal
        );
        var cashierUserGuidBackfillIndex = migrator.IndexOf(
            "INNER JOIN [User] AS [linkedUser]",
            StringComparison.Ordinal
        );
        var cashierDuplicateCleanupIndex = migrator.IndexOf(
            "ROW_NUMBER() OVER (PARTITION BY [UserGUID]",
            StringComparison.Ordinal
        );
        var cashierUniqueIndex = migrator.IndexOf(
            "CREATE UNIQUE INDEX [IX_CashRegisterUsers_UserGUID_Active]",
            StringComparison.Ordinal
        );
        var cashierBarcodeDuplicateCleanupIndex = migrator.IndexOf(
            "ROW_NUMBER() OVER (PARTITION BY [UserBarcode]",
            StringComparison.Ordinal
        );
        var cashierBarcodeUniqueIndex = migrator.IndexOf(
            "CREATE UNIQUE INDEX [IX_CashRegisterUsers_UserBarcode_Active]",
            StringComparison.Ordinal
        );

        // 关键位置：旧表缺列时必须先补齐索引依赖列，再创建索引，否则启动迁移会在补列前失败。
        Assert.True(
            wpfReleasePublishedIndex > wpfReleaseColumnBackfillIndex,
            "WPF 发布表索引必须晚于 PublishedAt/IsActive 等缺列补齐。"
        );
        Assert.True(
            wpfPolicyIndex > wpfPolicyColumnBackfillIndex,
            "WPF 策略表唯一索引必须晚于 Channel 等缺列补齐。"
        );
        Assert.True(
            cashierUserGuidNormalizeIndex > cashierUserGuidColumnIndex,
            "收银条码 UserGUID 必须先 trim 规范化，避免空格绕过有效唯一约束。"
        );
        Assert.True(
            cashierUserGuidBackfillIndex > cashierUserGuidNormalizeIndex,
            "收银条码 UserGUID 规范化后才能从旧 OperatorUser 的确定后台 UserGUID 做兼容回填。"
        );
        Assert.True(
            cashierDuplicateCleanupIndex > cashierUserGuidBackfillIndex,
            "收银条码有效唯一索引前必须先补齐 UserGUID 并清理历史重复有效条码。"
        );
        Assert.True(
            cashierUniqueIndex > cashierDuplicateCleanupIndex,
            "收银条码 UserGUID+Status 过滤唯一索引必须晚于重复数据清理 SQL。"
        );
        Assert.True(
            cashierBarcodeDuplicateCleanupIndex > cashierUserGuidColumnIndex,
            "收银条码 UserBarcode 过滤唯一索引前必须先清理历史重复有效条码。"
        );
        Assert.True(
            cashierBarcodeUniqueIndex > cashierBarcodeDuplicateCleanupIndex,
            "收银条码 UserBarcode+Status 过滤唯一索引必须晚于重复条码清理 SQL。"
        );
    }

    [Fact]
    public async Task StartupSchemaMigrator_新建Ota表ProjectName默认值与旧表迁移一致()
    {
        var migrator = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
            )
        );

        Assert.Contains(
            "[ProjectName] nvarchar(120) NOT NULL CONSTRAINT [DF_MobileAppOtaUpdate_ProjectName] DEFAULT('legacy-mobile')",
            migrator
        );
        Assert.Contains("SET [ProjectName] = ''legacy-mobile''", migrator);
        Assert.Contains(
            "ADD CONSTRAINT [DF_MobileAppOtaUpdate_ProjectName] DEFAULT('legacy-mobile') FOR [ProjectName]",
            migrator
        );
        Assert.DoesNotContain(
            "[ProjectName] nvarchar(120) NOT NULL CONSTRAINT [DF_MobileAppOtaUpdate_ProjectName] DEFAULT('')",
            migrator
        );
    }

    [Fact]
    public async Task Program_默认认证保持JwtServiceToken只能显式启用()
    {
        var repoRoot = FindRepoRoot();
        var programPath = Path.Combine(repoRoot, "services/backend/BlazorApp.Api/Program.cs");
        var program = await File.ReadAllTextAsync(programPath);

        // 关键位置：hbsvc_ 不能成为全站默认认证，否则会通过普通 [Authorize] 业务接口。
        Assert.Contains(
            "options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;",
            program
        );
        Assert.Contains(
            "options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;",
            program
        );
        Assert.DoesNotContain(
            "options.DefaultAuthenticateScheme = ServiceApiTokenAuthenticationDefaults.PolicyScheme;",
            program
        );
        Assert.Contains(
            "ServiceApiTokenAuthenticationDefaults.RequestHasServiceApiToken(context.Request)",
            program
        );
    }

    [Fact]
    public async Task StartupSchemaMigrator_UserGuidBackfillUsesSeparateBatchAfterAddingColumn()
    {
        var repoRoot = FindRepoRoot();
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );

        var migrator = await File.ReadAllTextAsync(migratorPath);

        var addColumnIndex = migrator.IndexOf(
            "ADD [UserGUID] nvarchar(50) NULL",
            StringComparison.Ordinal
        );
        var normalizeIndex = migrator.IndexOf(
            "SET [UserGUID] = LTRIM(RTRIM([UserGUID]))",
            StringComparison.Ordinal
        );

        Assert.True(addColumnIndex >= 0, "收银条码迁移必须先补齐 UserGUID 列。");
        Assert.True(normalizeIndex > addColumnIndex, "UserGUID 清理必须晚于补列。");

        var executeAfterAddIndex = migrator.IndexOf(
            "await db.Ado.ExecuteCommandAsync",
            addColumnIndex,
            StringComparison.Ordinal
        );

        // 关键位置：SQL Server 会先编译整个 batch，同一 batch 中 ADD 后再静态引用新列会触发 Invalid column name。
        Assert.True(
            executeAfterAddIndex > addColumnIndex && executeAfterAddIndex < normalizeIndex,
            "CashRegisterUsers.UserGUID 补列必须先作为独立 batch 执行，再执行引用 UserGUID 的清理/回填 SQL。"
        );
    }

    [Fact]
    public async Task ApplicationLogSchemaMigrator_SqlServerIndexesUseSeparateBatchAfterAddingColumns()
    {
        var repoRoot = FindRepoRoot();
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Services/Logging/ApplicationLogSchemaMigrator.cs"
        );

        var migrator = await File.ReadAllTextAsync(migratorPath);
        var sqlServerCaseIndex = migrator.IndexOf(
            "case DbType.SqlServer:",
            StringComparison.Ordinal
        );

        Assert.True(sqlServerCaseIndex >= 0, "必须存在 SQL Server 日志结构迁移分支。");

        var sqlServerBreakIndex = migrator.IndexOf(
            "break;",
            sqlServerCaseIndex,
            StringComparison.Ordinal
        );

        Assert.True(sqlServerBreakIndex > sqlServerCaseIndex, "SQL Server 日志结构迁移分支必须正常结束。");

        var sqlServerBranch = migrator[sqlServerCaseIndex..sqlServerBreakIndex];
        var columnMigrationIndex = sqlServerBranch.IndexOf(
            "db.Ado.ExecuteCommand(SqlServerColumnMigrationSql);",
            StringComparison.Ordinal
        );
        var indexMigrationIndex = sqlServerBranch.IndexOf(
            "db.Ado.ExecuteCommand(SqlServerIndexMigrationSql);",
            StringComparison.Ordinal
        );

        // 关键位置：SQL Server 会预编译整个 batch，新增列与静态引用新列的索引必须分批执行。
        Assert.True(columnMigrationIndex >= 0, "SQL Server 日志迁移必须先执行独立的补列 batch。");
        Assert.True(
            indexMigrationIndex > columnMigrationIndex,
            "SQL Server 日志迁移必须在补列 batch 完成后再执行索引 batch。"
        );

        const string columnSqlDeclaration =
            "private const string SqlServerColumnMigrationSql = \"\"\"";
        const string indexSqlDeclaration =
            "private const string SqlServerIndexMigrationSql = \"\"\"";
        const string rawStringTerminator = "\"\"\";";

        var columnSqlDeclarationIndex = migrator.IndexOf(
            columnSqlDeclaration,
            StringComparison.Ordinal
        );
        var indexSqlDeclarationIndex = migrator.IndexOf(
            indexSqlDeclaration,
            StringComparison.Ordinal
        );

        Assert.True(columnSqlDeclarationIndex >= 0, "必须声明 SQL Server 独立补列 SQL 常量。");
        Assert.True(
            indexSqlDeclarationIndex > columnSqlDeclarationIndex,
            "必须在补列 SQL 常量之后声明 SQL Server 独立索引 SQL 常量。"
        );

        var columnSqlBodyStartIndex = columnSqlDeclarationIndex + columnSqlDeclaration.Length;
        var columnSqlBodyEndIndex = migrator.IndexOf(
            rawStringTerminator,
            columnSqlBodyStartIndex,
            StringComparison.Ordinal
        );
        var indexSqlBodyStartIndex = indexSqlDeclarationIndex + indexSqlDeclaration.Length;
        var indexSqlBodyEndIndex = migrator.IndexOf(
            rawStringTerminator,
            indexSqlBodyStartIndex,
            StringComparison.Ordinal
        );

        Assert.True(
            columnSqlBodyEndIndex > columnSqlBodyStartIndex
                && columnSqlBodyEndIndex < indexSqlDeclarationIndex,
            "必须能按声明边界提取 SQL Server 补列 SQL 正文。"
        );
        Assert.True(
            indexSqlBodyEndIndex > indexSqlBodyStartIndex,
            "必须能按声明边界提取 SQL Server 索引 SQL 正文。"
        );

        var columnSqlBody = migrator[columnSqlBodyStartIndex..columnSqlBodyEndIndex];
        var indexSqlBody = migrator[indexSqlBodyStartIndex..indexSqlBodyEndIndex];
        const string addColumnStatement = "ALTER TABLE [dbo].[ApplicationLog] ADD";
        var addColumnCount = columnSqlBody.Split(addColumnStatement, StringSplitOptions.None).Length - 1;

        // 关键位置：除了调用顺序，还要锁定两个 SQL 常量的职责，防止索引重新混入补列 batch。
        Assert.True(
            addColumnCount == 4,
            $"SQL Server 补列 SQL 必须恰好包含 4 次幂等补列，实际为 {addColumnCount} 次。"
        );
        Assert.True(
            !columnSqlBody.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase),
            "SQL Server 补列 SQL 不得包含普通索引创建语句。"
        );
        Assert.True(
            !columnSqlBody.Contains("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase),
            "SQL Server 补列 SQL 不得包含唯一索引创建语句。"
        );
        Assert.True(
            !indexSqlBody.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase),
            "SQL Server 索引 SQL 不得包含补列语句。"
        );

        var expectedIndexNames = new[]
        {
            "IX_ApplicationLog_ProjectCode_ClientEventId",
            "IX_ApplicationLog_StoreCode_TimestampUtc",
            "IX_ApplicationLog_DeviceCode_TimestampUtc",
            "IX_ApplicationLog_InstanceId",
        };

        foreach (var expectedIndexName in expectedIndexNames)
        {
            Assert.True(
                indexSqlBody.Contains(expectedIndexName, StringComparison.Ordinal),
                $"SQL Server 索引 SQL 必须包含预期索引 {expectedIndexName}。"
            );
        }
    }

    [Fact]
    public async Task StartupSchemaMigrator_WarehouseCartOwnerColumnFailureIsNotSwallowed()
    {
        var repoRoot = FindRepoRoot();
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );

        var migrator = await File.ReadAllTextAsync(migratorPath);

        var addColumnIndex = migrator.IndexOf(
            "ADD [CartOwnerUserGuid] nvarchar(50) NULL",
            StringComparison.Ordinal
        );
        var throwIndex = migrator.IndexOf(
            "THROW 51000, 'WareHouseOrder.CartOwnerUserGuid migration failed', 1;",
            StringComparison.Ordinal
        );
        var indexWarningIndex = migrator.IndexOf(
            "logger.LogWarning(ex, \"仓库订货购物车归属索引迁移失败\");",
            StringComparison.Ordinal
        );

        // 关键位置：CartOwnerUserGuid 缺列会让所有活动购物车 scope 查询失败，补列不能被 catch 吞掉。
        Assert.True(addColumnIndex >= 0, "仓库购物车 owner 补列 SQL 必须存在。");
        Assert.True(throwIndex > addColumnIndex, "补列后必须验证列存在，不存在就抛错阻断启动。");
        Assert.True(indexWarningIndex > throwIndex, "只有索引创建可以降级 warning，必需列不能降级。");
        Assert.DoesNotContain("仓库订货购物车归属字段迁移失败", migrator);
    }

    [Fact]
    public async Task StartupSchemaMigrator_幂等创建Wpf定向目标表并约束单一目标()
    {
        var migrator = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs")
        );

        Assert.Contains("IF COL_LENGTH('WpfUpdatePolicy', 'TargetScope') IS NULL", migrator);
        Assert.Contains("IF OBJECT_ID('WpfUpdatePolicyTarget', 'U') IS NULL", migrator);
        Assert.Contains("[StoreGuid] nvarchar(100) NULL", migrator);
        Assert.Contains("[DeviceRegistrationId] int NULL", migrator);
        Assert.Contains("IX_WpfUpdatePolicyTarget_Policy_StoreGuid", migrator);
        Assert.Contains("IX_WpfUpdatePolicyTarget_Policy_DeviceRegistrationId", migrator);
        Assert.Contains("IX_WpfUpdatePolicyTarget_PolicyId", migrator);
        Assert.Contains("CK_WpfUpdatePolicyTarget_ExactlyOneTarget", migrator);
        Assert.Contains("WHERE [StoreGuid] IS NOT NULL", migrator);
        Assert.Contains("WHERE [DeviceRegistrationId] IS NOT NULL", migrator);
        Assert.Contains("WITH CHECK", migrator);
        Assert.Contains("[StoreGuid] IS NOT NULL AND [DeviceRegistrationId] IS NULL", migrator);
        Assert.Contains("[StoreGuid] IS NULL AND [DeviceRegistrationId] IS NOT NULL", migrator);
        Assert.DoesNotContain("[TargetType] nvarchar", migrator);
        Assert.DoesNotContain("[TargetValue] nvarchar", migrator);

        var targetScopeAddIndex = migrator.IndexOf(
            "ADD [TargetScope] nvarchar(16) NOT NULL",
            StringComparison.Ordinal
        );
        var targetScopeDynamicUpdateIndex = migrator.IndexOf(
            "EXEC(N'\n        UPDATE [WpfUpdatePolicy]",
            StringComparison.Ordinal
        );
        var targetColumnsAddIndex = migrator.IndexOf(
            "IF COL_LENGTH('WpfUpdatePolicyTarget', 'StoreGuid') IS NULL",
            StringComparison.Ordinal
        );
        var targetIndexesDynamicBatchIndex = migrator.IndexOf(
            "EXEC(N'\n        IF NOT EXISTS (\n            SELECT * FROM sys.indexes",
            StringComparison.Ordinal
        );
        Assert.True(targetScopeAddIndex >= 0, "WpfUpdatePolicy.TargetScope 补列 SQL 必须存在。");
        Assert.True(
            targetScopeDynamicUpdateIndex > targetScopeAddIndex,
            "TargetScope 补列后的首次引用必须在动态 SQL 批次中执行。"
        );
        Assert.True(targetColumnsAddIndex >= 0, "WpfUpdatePolicyTarget 目标列补列 SQL 必须存在。");
        Assert.True(
            targetIndexesDynamicBatchIndex > targetColumnsAddIndex,
            "目标列补齐后，索引与 CHECK 必须在独立动态批次中引用。"
        );
    }

    [Fact]
    public async Task StoreLocalSupplierInvoice_业务索引兼容迁移取消全表唯一约束()
    {
        var repoRoot = FindRepoRoot();
        var context = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/SqlSugarContext.cs"
        ));
        var service = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Features/LocalSupplierInvoices/Header/LocalSupplierInvoicesHeaderHandler.cs"
        ));

        Assert.DoesNotContain(
            "CREATE UNIQUE INDEX IX_StoreLocalSupplierInvoice_Business_Unique",
            context,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "CREATE UNIQUE INDEX IF NOT EXISTS \\\"IX_StoreLocalSupplierInvoice_Business_Unique\\\"",
            context,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "DROP INDEX [IX_StoreLocalSupplierInvoice_Business_Unique] ON [dbo].[StoreLocalSupplierInvoice]",
            context,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "DROP INDEX IF EXISTS \\\"IX_StoreLocalSupplierInvoice_Business_Unique\\\"",
            context,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "CREATE INDEX IX_StoreLocalSupplierInvoice_Store_Supplier_Invoice_NotDeleted",
            context,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "CREATE INDEX IF NOT EXISTS \\\"IX_StoreLocalSupplierInvoice_Store_Supplier_Invoice_NotDeleted\\\"",
            context,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "EnsureStoreLocalSupplierInvoiceBusinessLookupIndex();",
            context,
            StringComparison.Ordinal
        );
        Assert.True(
            service.Split(
                "BeginTranAsync(IsolationLevel.Serializable)",
                StringSplitOptions.None
            ).Length - 1 >= 2,
            "人工创建和编辑发票都必须在 Serializable 事务内完成业务键查重。"
        );
        Assert.Contains(
            "for (var attempt = 0; attempt < 2; attempt++)",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "PostgresErrorCodes.SerializationFailure",
            service,
            StringComparison.Ordinal
        );

        var updateMethodStart = service.IndexOf(
            "public async Task<ApiResponse<bool>> UpdateAsync(",
            StringComparison.Ordinal
        );
        var createMethodStart = service.IndexOf(
            "public async Task<ApiResponse<string>> CreateAsync(",
            StringComparison.Ordinal
        );
        Assert.True(updateMethodStart >= 0 && createMethodStart > updateMethodStart);
        var updateMethod = service[updateMethodStart..createMethodStart];
        Assert.Contains(
            "for (var attempt = 0; attempt < 2; attempt++)",
            updateMethod,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "IsSerializationFailure(ex)",
            updateMethod,
            StringComparison.Ordinal
        );
        Assert.Contains("UPDATE_RETRY_REQUIRED", updateMethod, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupSchemaMigrator_唯一索引前先清理Wpf重复数据()
    {
        var repoRoot = FindRepoRoot();
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );

        var migrator = await File.ReadAllTextAsync(migratorPath);

        var releaseDedupIndex = migrator.IndexOf(
            "ROW_NUMBER() OVER (PARTITION BY [NormalizedChannel], [NormalizedVersion]",
            StringComparison.Ordinal
        );
        var releaseDeactivateIndex = migrator.IndexOf(
            "SET [IsActive] = 0",
            StringComparison.Ordinal
        );
        var releaseUniqueIndex = migrator.IndexOf(
            "CREATE UNIQUE INDEX [IX_WpfAppRelease_Channel_Version]",
            StringComparison.Ordinal
        );
        var policyDedupIndex = migrator.IndexOf(
            "ROW_NUMBER() OVER (PARTITION BY [NormalizedChannel]",
            StringComparison.Ordinal
        );
        var policyDeleteIndex = migrator.IndexOf(
            "DELETE FROM [WpfUpdatePolicy]",
            StringComparison.Ordinal
        );
        var policyUniqueIndex = migrator.IndexOf(
            "CREATE UNIQUE INDEX [IX_WpfUpdatePolicy_Channel]",
            StringComparison.Ordinal
        );

        // 关键位置：旧库里可能已经存在重复 channel/version 或 channel，启动迁移必须先整理脏数据再建唯一索引。
        Assert.True(
            releaseDedupIndex >= 0,
            "WpfAppRelease 建唯一索引前必须先按规范化后的 Channel+Version 做窗口去重排序。"
        );
        Assert.True(
            releaseDeactivateIndex > releaseDedupIndex,
            "WpfAppRelease 重复记录必须先把非保留行安全失活，避免唯一索引创建失败。"
        );
        Assert.True(
            releaseUniqueIndex > releaseDeactivateIndex,
            "WpfAppRelease 唯一索引必须晚于重复数据清理 SQL。"
        );
        Assert.True(
            policyDedupIndex >= 0,
            "WpfUpdatePolicy 建唯一索引前必须先按规范化后的 Channel 做窗口去重排序。"
        );
        Assert.True(
            policyDeleteIndex > policyDedupIndex,
            "WpfUpdatePolicy 重复记录必须先删除或归并非保留行。"
        );
        Assert.True(
            policyUniqueIndex > policyDeleteIndex,
            "WpfUpdatePolicy 唯一索引必须晚于重复数据清理 SQL。"
        );
    }

    [Fact]
    public async Task StartupSchemaMigrator_先规范化Wpf版本与渠道再去重()
    {
        var repoRoot = FindRepoRoot();
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );

        var migrator = await File.ReadAllTextAsync(migratorPath);

        var releaseNormalizationIndex = migrator.IndexOf(
            "-- 关键位置：先把 WPF 发布历史数据规范成业务层可比较的渠道和版本，避免大小写和 v 前缀把重复版本漏过去。",
            StringComparison.Ordinal
        );
        var policyNormalizationIndex = migrator.IndexOf(
            "-- 关键位置：策略表也要先按业务层语义规范化渠道和版本，避免引用不到已规范化的发布版本。",
            StringComparison.Ordinal
        );
        var releaseDedupIndex = releaseNormalizationIndex < 0
            ? -1
            : migrator.IndexOf(
                "ROW_NUMBER() OVER (PARTITION BY [NormalizedChannel], [NormalizedVersion]",
                releaseNormalizationIndex,
                StringComparison.Ordinal
            );
        var policyDedupIndex = policyNormalizationIndex < 0
            ? -1
            : migrator.IndexOf(
                "ROW_NUMBER() OVER (PARTITION BY [NormalizedChannel]",
                policyNormalizationIndex,
                StringComparison.Ordinal
            );

        // 关键位置：迁移必须先把历史数据按业务语义规范化，再按规范化值去重，否则 v1.2.3/1.2.3 与大小写渠道会漏掉脏数据。
        Assert.Contains("LOWER(LTRIM(RTRIM([Channel])))", migrator);
        Assert.Contains("LEFT(LTRIM(RTRIM([Version])), 1) IN ('v', 'V')", migrator);
        Assert.Contains("LEFT(LTRIM(RTRIM([TargetVersion])), 1) IN ('v', 'V')", migrator);
        Assert.Contains(
            "LEFT(LTRIM(RTRIM([MinimumSupportedVersion])), 1) IN ('v', 'V')",
            migrator
        );
        Assert.Contains("ISNULL([Channel], '') <>", migrator);
        Assert.Contains("ISNULL([Version], '') <>", migrator);
        Assert.Contains("ISNULL([TargetVersion], '') <>", migrator);
        Assert.True(
            releaseNormalizationIndex >= 0,
            "WpfAppRelease 必须先执行 channel/version 规范化更新 SQL。"
        );
        Assert.True(
            releaseDedupIndex > releaseNormalizationIndex,
            "WpfAppRelease 必须在规范化 SQL 之后按规范化后的 Channel/Version 去重。"
        );
        Assert.True(
            policyNormalizationIndex >= 0,
            "WpfUpdatePolicy 必须先执行 channel/target/minimum 规范化更新 SQL。"
        );
        Assert.True(
            policyDedupIndex > policyNormalizationIndex,
            "WpfUpdatePolicy 必须在规范化 SQL 之后按规范化后的 Channel 去重。"
        );
    }

    [Fact]
    public async Task StartupSchemaMigrator_WpfDuplicateCleanup_prefers_non_deleted_rows()
    {
        var repoRoot = FindRepoRoot();
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );

        var migrator = await File.ReadAllTextAsync(migratorPath);

        Assert.DoesNotContain(
            migrator.Split(Environment.NewLine),
            line => line.TrimStart().StartsWith("--", StringComparison.Ordinal) &&
                (line.Contains("IF OBJECT_ID('WpfAppRelease'", StringComparison.Ordinal) ||
                    line.Contains("IF OBJECT_ID('WpfUpdatePolicy'", StringComparison.Ordinal))
        );
        Assert.Contains("CASE WHEN ISNULL([IsDeleted], 0) = 0 THEN 0 ELSE 1 END", migrator);

        var releaseDeletedPreferenceIndex = migrator.IndexOf(
            "CASE WHEN ISNULL([IsDeleted], 0) = 0 THEN 0 ELSE 1 END",
            StringComparison.Ordinal
        );
        var releaseActivePreferenceIndex = migrator.IndexOf(
            "CASE WHEN [IsActive] = 1 THEN 0 ELSE 1 END",
            StringComparison.Ordinal
        );
        Assert.True(
            releaseDeletedPreferenceIndex >= 0 &&
                releaseDeletedPreferenceIndex < releaseActivePreferenceIndex,
            "WpfAppRelease duplicate cleanup must prefer non-deleted rows before active/newer rows."
        );

        var policyDedupIndex = migrator.IndexOf(
            "ROW_NUMBER() OVER (PARTITION BY [NormalizedChannel]",
            StringComparison.Ordinal
        );
        var policyDeletedPreferenceIndex = migrator.IndexOf(
            "CASE WHEN ISNULL([IsDeleted], 0) = 0 THEN 0 ELSE 1 END",
            policyDedupIndex,
            StringComparison.Ordinal
        );
        var policyLastChangedPreferenceIndex = migrator.IndexOf(
            "[LastChangedAt] DESC",
            policyDedupIndex,
            StringComparison.Ordinal
        );
        Assert.True(
            policyDeletedPreferenceIndex >= 0 &&
                policyDeletedPreferenceIndex < policyLastChangedPreferenceIndex,
            "WpfUpdatePolicy duplicate cleanup must prefer non-deleted rows before newer rows."
        );
    }

    [Fact]
    public async Task StartupSchemaMigrator_EnsuresNullableMinimumSupportedVersionBeforePolicyNormalization()
    {
        var repoRoot = FindRepoRoot();
        var migratorPath = Path.Combine(
            repoRoot,
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );

        var migrator = await File.ReadAllTextAsync(migratorPath);

        var ensureNullableIndex = migrator.IndexOf(
            "ALTER COLUMN [MinimumSupportedVersion] nvarchar(80) NULL;",
            StringComparison.Ordinal
        );
        var normalizationIndex = migrator.IndexOf(
            "UPDATE [WpfUpdatePolicy]",
            StringComparison.Ordinal
        );

        // 关键位置：旧库可能把 MinimumSupportedVersion 建成 NOT NULL，规范化空白值写回 NULL 之前必须先放宽列可空。
        Assert.Contains(
            "IF COL_LENGTH('WpfUpdatePolicy', 'MinimumSupportedVersion') IS NOT NULL",
            migrator
        );
        Assert.True(
            ensureNullableIndex >= 0,
            "WpfUpdatePolicy.MinimumSupportedVersion 已存在时，启动迁移必须显式 ALTER COLUMN 为 nvarchar(80) NULL。"
        );
        Assert.True(
            normalizationIndex > ensureNullableIndex,
            "WpfUpdatePolicy.MinimumSupportedVersion 的可空兜底必须早于规范化 UPDATE SQL。"
        );
    }

    [Fact]
    public async Task CashierBarcodeMigration_BackfillsDistinctHistoricalReservations()
    {
        var migrator = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
            )
        );

        Assert.Contains("CREATE TABLE [CashierBarcodeReservations]", migrator);
        Assert.Contains("PRIMARY KEY", migrator);
        Assert.Contains("SELECT DISTINCT", migrator);
        Assert.Contains("FROM [CashRegisterUsers]", migrator);
        Assert.Contains("CREATE TABLE [EmployeeCashierBarcodes]", migrator);
        Assert.Contains("FROM [EmployeeCashierBarcodes]", migrator);
        Assert.Contains("CREATE TABLE [EmployeeCashierBarcodePrintAttempts]", migrator);
        Assert.Contains("EmployeeImageUploadTickets", migrator);
    }

    [Fact]
    public async Task HqFullSync_OwnsOnlyLegacyCashRegisterUsers()
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Services/React/DataSyncFullService.cs"
            )
        );
        var guard = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Services/React/CashierBarcodeSyncGuard.cs"
            )
        );
        Assert.Contains("Deleteable<CashRegisterUser>()", source);
        Assert.DoesNotContain("Deleteable<EmployeeCashierBarcode>()", source);
        Assert.Contains("ValidateAndReserveHqBatchAsync", source);
        Assert.Contains("Queryable<CashierBarcodeReservation>()", guard);
        Assert.Contains("HQ 收银条码与员工个人条码冲突", guard);
    }

    [Fact]
    public async Task CashierBarcodeMigration_发现跨Owner或跨表冲突时必须阻断且不覆盖()
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs")
        );

        Assert.Contains("THROW 51001", source);
        Assert.Contains("THROW 51002", source);
        Assert.Contains("THROW 51005", source);
        Assert.Contains("[legacy].[UserGUID] = [employee].[UserGUID]", source);
        Assert.DoesNotContain("SET [OwnerType] = 'employee'", source);
    }

    [Fact]
    public async Task HqCashierSync_全量删除前完整预检且增量批写入使用事务()
    {
        var root = FindRepoRoot();
        var full = await File.ReadAllTextAsync(Path.Combine(
            root, "services/backend/BlazorApp.Api/Services/React/DataSyncFullService.cs"));
        var incremental = await File.ReadAllTextAsync(Path.Combine(
            root, "services/backend/BlazorApp.Api/Services/React/DataSyncIncrementalService.cs"));

        Assert.True(
            full.IndexOf("ValidateAndReserveHqBatchAsync", StringComparison.Ordinal)
                < full.IndexOf("Deleteable<CashRegisterUser>()", StringComparison.Ordinal),
            "全量同步必须在删除旧表前完成全部 HQ 条码预检。"
        );
        var deleteIndex = full.IndexOf("Deleteable<CashRegisterUser>()", StringComparison.Ordinal);
        var nextMethodIndex = full.IndexOf(
            "SyncPosmProductSupplierMappingsAsync",
            deleteIndex,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "Queryable<DIC_收银用户信息表>()",
            full[deleteIndex..nextMethodIndex]
        );
        Assert.Contains("await _localContext.Db.Ado.BeginTranAsync();", incremental);
        Assert.Contains("ValidateAndReserveHqBatchAsync", incremental);
        Assert.Contains("await _localContext.Db.Ado.RollbackTranAsync();", incremental);
    }

    [Fact]
    public async Task WarehouseProductHistory_全量同步删除陈旧商品不加入修改审计集合()
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Services/React/DataSyncFullService.cs"
            )
        );

        foreach (
            var staleMarker in new[]
            {
                "var staleCodes = initialProducts",
                "var staleCodes = initialCodes",
            }
        )
        {
            var staleStart = source.IndexOf(staleMarker, StringComparison.Ordinal);
            var auditStart = source.IndexOf(
                "if (batchAuditCodes.Count > 0)",
                staleStart,
                StringComparison.Ordinal
            );
            Assert.True(staleStart >= 0 && auditStart > staleStart, $"未找到 {staleMarker} 删除区段");
            var staleDeleteBlock = source[staleStart..auditStart];
            Assert.DoesNotContain("CaptureSnapshotsAsync", staleDeleteBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("batchAuditCodes.Add", staleDeleteBlock, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WarehouseProductHistory_旧入口以用户Guid优先判定操作人类型()
    {
        var root = FindRepoRoot();
        foreach (
            var relativePath in new[]
            {
                "services/backend/BlazorApp.Api/Services/WarehouseProductService.cs",
                "services/backend/BlazorApp.Api/Services/ProductSyncService.cs",
                "services/backend/BlazorApp.Api/Services/WarehouseProductBatchService.cs",
            }
        )
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, relativePath));
            Assert.Contains(
                "!string.IsNullOrWhiteSpace(ResolveActorGuid())",
                source,
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public async Task SqlSugarContext_遗留启动初始化不再通过后台任务绕过迁移协调器()
    {
        var repoRoot = FindRepoRoot();
        var contextSource = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "services/backend/BlazorApp.Api/Data/SqlSugarContext.cs")
        );
        var programSource = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "services/backend/BlazorApp.Api/Program.cs")
        );

        Assert.DoesNotContain("Database:InitializeOnStartup", contextSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", contextSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTable();\n                        CreateIndexes();", contextSource, StringComparison.Ordinal);

        // 遗留开关只能由 Program 的统一入口拒绝，避免 Scoped 构造器在后台并发执行 DDL。
        Assert.Contains("Database:InitializeOnStartup", programSource, StringComparison.Ordinal);
        Assert.Contains("SchemaMigrationCoordinator", programSource, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var programPath = Path.Combine(
                directory.FullName,
                "services/backend/BlazorApp.Api/Program.cs"
            );
            if (
                (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git"))
                    || Directory.Exists(Path.Combine(directory.FullName, ".gitnexus")))
                && File.Exists(programPath)
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var programPath = Path.Combine(
                directory.FullName,
                "services/backend/BlazorApp.Api/Program.cs"
            );
            if (File.Exists(programPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 hb-platform 仓库根目录");
    }
}
