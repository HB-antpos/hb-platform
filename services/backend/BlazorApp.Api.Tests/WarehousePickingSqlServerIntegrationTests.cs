using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class WarehousePickingSqlServerFactAttribute : FactAttribute
{
    public WarehousePickingSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LocalSupplierCategorySqlServerFactAttribute.ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {LocalSupplierCategorySqlServerFactAttribute.ConnectionEnvironmentVariable}，跳过真实 SQL Server 拣货集成测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证拣货迁移与会话行更新锁：多人并发写入时“行合计 = 记录之和”不丢更新，
/// 按旧合计改总数只有一个成功，并发加入只建一个会话。每个用例使用独立数据库。
/// </summary>
[Trait("Category", "SQL")]
public sealed class WarehousePickingSqlServerIntegrationTests
{
    private const string OrderGuid = "order-sql-1";

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_迁移可重复执行_校验通过_权限码只入库一次()
    {
        await using var database = await IsolatedDatabase.CreateAsync();

        await database.ExecuteAsync(WarehouseOrderPickingSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickingSchema.VerifySql);
        await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.VerifySql);
        await database.ExecuteAsync(WarehouseOrderPickAssignmentSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickAssignmentSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickAssignmentSchema.VerifySql);

        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.HbwebSysPermissions WHERE Code = N'Warehouse.Picking'"));
        Assert.Equal(
            1,
            await database.ScalarAsync(
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WarehouseOrderPickRecord') AND name = N'UX_WarehouseOrderPickRecord_ClientRequestId' AND is_unique = 1"
            )
        );
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_多人并发扫码_行合计等于记录之和且不丢更新()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        await CreateService(database).JoinAsync(OrderGuid, Picker("u-1"));

        // 4 个拣货人共 40 次并发扫码，每次 +2。每次请求各用独立连接（与生产每个请求一个作用域一致；
        // SqlSugarClient 本身不是线程安全的，不能让并发请求共用一个客户端）。
        var tasks = Enumerable.Range(0, 40).Select(index =>
            CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker($"u-{index % 4 + 1}"))
        );
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.Success, result.Message));
        using var db = database.CreateClient();
        Assert.Equal(40, await db.Queryable<WarehouseOrderPickRecord>().CountAsync());
        var progress = await CreateService(database).GetProgressAsync(OrderGuid);
        Assert.Equal(80, progress.Data!.Lines.Single().PickedTotal);
        Assert.Equal(4, progress.Data.Participants.Count);
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_货位没货_标记覆盖原因_又拣到货自动失效_撤销幂等()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        await CreateService(database).JoinAsync(OrderGuid, Picker("u-1"));
        await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-1"));

        var marked = await CreateService(database).MarkStockoutAsync(OrderGuid, "d-1", WarehouseOrderPickStockoutReasons.LocationEmpty, Picker("u-1"));
        var remarked = await CreateService(database).MarkStockoutAsync(OrderGuid, "d-1", WarehouseOrderPickStockoutReasons.Damaged, Picker("u-2"));

        Assert.True(marked.Success, marked.Message);
        Assert.Equal(WarehouseOrderPickStockoutReasons.Damaged, remarked.Data!.Line.Stockout!.Reason);
        Assert.Equal(2, remarked.Data.Line.Stockout.PickedAtMark);
        var progress = await CreateService(database).GetProgressAsync(OrderGuid);
        Assert.NotNull(progress.Data!.Lines.Single().Stockout);

        var scanned = await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-3"));
        Assert.Null(scanned.Data!.Line.Stockout);

        var cleared = await CreateService(database).ClearStockoutAsync(OrderGuid, "d-1", Picker("u-1"));
        Assert.True(cleared.Success, cleared.Message);
        using var db = database.CreateClient();
        var row = await db.Queryable<WarehouseOrderPickStockout>().SingleAsync();
        Assert.NotNull(row.ClearedAtUtc);
        // 自动失效时记下又拣到货的人；之后的撤销没有有效标记，不覆盖。
        Assert.Equal("Picker u-3", row.ClearedByName);
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_多位经理并发派同一张单_串行整单替换_派给我的与候选在拣单数正确()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        using (var seed = database.CreateClient())
        {
            await seed.Insertable(Enumerable.Range(2, 5).Select(index => new WareHouseOrderDetails
            {
                DetailGUID = $"d-{index}",
                OrderGUID = OrderGuid,
                StoreCode = "1013",
                ProductCode = $"P-{index}",
                Quantity = 12,
                OEMPrice = 1m,
                ImportPrice = 1m,
            }).ToList()).ExecuteCommandAsync();
        }

        var pickers = new Mock<IWarehousePickerService>();
        foreach (var guid in new[] { "u-1", "u-2", "u-3" })
        {
            pickers.Setup(service => service.GetEligibilityAsync(guid))
                .ReturnsAsync(new WarehousePickerEligibility(guid, $"Picker {guid}", true, false, "WarehouseStaff"));
        }

        pickers.Setup(service => service.ListEligibleAsync())
            .ReturnsAsync(new List<WarehousePickerEligibility> { new("u-1", "Picker u-1", true, false, null), new("u-3", "Picker u-3", true, false, null) });
        WarehousePickingAssignmentService Assignments() =>
            new(CreateContext(database.CreateClient()), pickers.Object, NullLogger<WarehousePickingAssignmentService>.Instance);

        // 6 次并发派单（两组员工交替）：订单行更新锁让它们串行，最终是其中一次的完整结果，不撞主键、不混合。
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => Assignments().AssignEvenlyAsync(
            new WarehousePickingBatchAssignRequestDto
            {
                OrderGuids = new List<string> { OrderGuid },
                PickerUserGuids = index % 2 == 0 ? new List<string> { "u-1", "u-2" } : new List<string> { "u-3" },
            },
            new WarehousePickingAssigner($"m-{index}", $"Manager {index}")
        )));

        Assert.All(results, result => Assert.True(result.Data!.Items.Single().Success, result.Data.Items.Single().Message));
        using var db = database.CreateClient();
        var rows = await db.Queryable<WarehouseOrderPickAssignment>().ToListAsync();
        Assert.Equal(6, rows.Count);
        Assert.Single(rows.Select(row => row.AssignedByUserGuid).Distinct());
        var pickerSet = rows.Select(row => row.PickerUserGuid).Distinct().OrderBy(guid => guid).ToArray();
        Assert.True(pickerSet.SequenceEqual(new[] { "u-1", "u-2" }) || pickerSet.SequenceEqual(new[] { "u-3" }));

        await Assignments().SaveAsync(
            OrderGuid,
            new WarehousePickingAssignmentSaveRequestDto
            {
                Assignments = new List<WarehousePickingAssignmentInputDto>
                {
                    new() { PickerUserGuid = "u-1", DetailGuids = new List<string> { "d-1", "d-2" } },
                    new() { PickerUserGuid = "u-3", DetailGuids = new List<string> { "d-3" } },
                },
            },
            new WarehousePickingAssigner("m-x", "Manager X")
        );
        var mine = await CreateService(database).ListOrdersAsync("mine", null, "u-3");
        Assert.Equal(1, mine.Data!.Counts.Mine);
        Assert.Equal(new[] { ("u-1", 2), ("u-3", 1) }, mine.Data.Items.Single().Assignees.Select(item => (item.PickerUserGuid, item.LineCount)));
        var candidates = await Assignments().ListCandidatesAsync();
        Assert.Equal(new[] { 1, 1 }, candidates.Data!.Select(item => item.ActiveOrderCount));

        var summary = await Assignments().GetAsync(OrderGuid);
        var slipCode = summary.Data!.Assignees.Single(item => item.SegmentNo == 2).SlipCode!;
        var resolved = await Assignments().ResolveSlipAsync(slipCode);
        Assert.True(resolved.Success, resolved.Message);
        Assert.Equal(("u-3", 2, 1), (resolved.Data!.PickerUserGuid, resolved.Data.SegmentCount, resolved.Data.LineCount));
        var slips = await Assignments().GetSlipsAsync(OrderGuid, null);
        Assert.Equal(new[] { 2, 1 }, slips.Data!.Slips.Select(slip => slip.LineCount));
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_多人同时扫同一张待领取分单_只有一人领到()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        var pickers = new Mock<IWarehousePickerService>();
        WarehousePickingAssignmentService Assignments() =>
            new(CreateContext(database.CreateClient()), pickers.Object, NullLogger<WarehousePickingAssignmentService>.Instance);
        await Assignments().AssignEvenlyAsync(
            new WarehousePickingBatchAssignRequestDto { OrderGuids = new List<string> { OrderGuid }, SegmentCount = 1 },
            new WarehousePickingAssigner("m-1", "Manager")
        );
        var code = (await Assignments().GetAsync(OrderGuid)).Data!.Assignees.Single().SlipCode!;

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(index =>
            Assignments().ClaimSlipAsync(code, Picker($"u-{index}"))
        ));

        Assert.All(results, result => Assert.True(result.Success, result.Message));
        var winner = Assert.Single(results, result => result.Data!.ClaimedNow);
        Assert.All(results, result => Assert.Equal(winner.Data!.PickerUserGuid, result.Data!.PickerUserGuid));
        Assert.Single(results, result => result.Data!.ClaimedByMe);
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_候选员工粗筛_仓库角色与角色或本人拣货权限()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        db.CodeFirst.InitTables(typeof(User), typeof(Role), typeof(UserRole), typeof(SysRolePermission), typeof(SysUserPermission));
        foreach (var (guid, name) in new[] { ("u-staff", "Chen"), ("u-pack", "Dan"), ("u-direct", "Bo"), ("u-plain", "Pat") })
        {
            await db.Insertable(new User { UserGUID = guid, Username = guid, Email = $"{guid}@example.invalid", PasswordHash = "x", FullName = name, IsActive = true })
                .ExecuteCommandAsync();
        }

        await db.Insertable(new List<Role>
        {
            new() { RoleGUID = "r-staff", RoleName = "仓库员工", IsActive = true },
            new() { RoleGUID = "r-pack", RoleName = "打包组", IsActive = true },
            new() { RoleGUID = "r-user", RoleName = "User", IsActive = true },
        }).ExecuteCommandAsync();
        await db.Insertable(new List<UserRole>
        {
            new() { UserGUID = "u-staff", RoleGUID = "r-staff" },
            new() { UserGUID = "u-pack", RoleGUID = "r-pack" },
            new() { UserGUID = "u-plain", RoleGUID = "r-user" },
        }).ExecuteCommandAsync();
        await db.Insertable(new SysRolePermission { RoleGuid = "r-pack", PermissionCode = "Warehouse.Picking" }).ExecuteCommandAsync();
        await db.Insertable(new SysUserPermission { UserGuid = "u-direct", PermissionCode = "Warehouse.ManageOrders" }).ExecuteCommandAsync();
        var roles = new Mock<BlazorApp.Api.Interfaces.IRoleService>();
        roles.Setup(service => service.GetUserPermissionSnapshotAsync(It.IsAny<string>()))
            .ReturnsAsync((string guid) => BlazorApp.Shared.DTOs.ApiResponse<BlazorApp.Shared.DTOs.UserPermissionSnapshotDto>.OK(
                new BlazorApp.Shared.DTOs.UserPermissionSnapshotDto
                {
                    UserGuid = guid,
                    RoleNames = guid == "u-staff" ? new List<string> { "仓库员工" } : new List<string>(),
                    PermissionCodes = guid is "u-pack" or "u-direct" ? new List<string> { "Warehouse.Picking" } : new List<string>(),
                }
            ));
        var service = new WarehousePickerService(
            CreateContext(database.CreateClient()),
            roles.Object,
            new WarehousePickerTicketProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider())
        );

        var eligible = await service.ListEligibleAsync();

        Assert.Equal(new[] { "u-direct", "u-staff", "u-pack" }, eligible.Select(item => item.UserGuid).ToArray());
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_按同一旧合计并发改总数只有一个成功_其余返回最新合计()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        await CreateService(database).JoinAsync(OrderGuid, Picker("u-1"));

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(index =>
            CreateService(database).SetLineTotalAsync(
                OrderGuid,
                "d-1",
                new WarehousePickingSetTotalRequestDto { Total = 10 + index, ExpectedTotal = 0, ClientRequestId = Guid.NewGuid() },
                Picker($"u-{index}")
            )
        ));

        var succeeded = Assert.Single(results, result => result.Success);
        var winner = succeeded.Data!.Line.PickedTotal;
        Assert.All(results.Where(result => !result.Success), result =>
        {
            Assert.Equal(WarehousePickingErrorCodes.PickedTotalChanged, result.ErrorCode);
            Assert.Equal(winner, result.Data!.Line.PickedTotal);
        });
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_并发加入只建一个会话_订单只转一次配货中_提交后拒绝写入()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);

        var joins = await Task.WhenAll(Enumerable.Range(1, 8).Select(index =>
            CreateService(database).JoinAsync(OrderGuid, Picker($"u-{index}"))
        ));
        await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-1"));
        var submitted = await CreateService(database).SubmitAsync(OrderGuid, Picker("u-1"));
        var late = await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-2"));

        Assert.All(joins, result => Assert.True(result.Success, result.Message));
        using var db = database.CreateClient();
        Assert.Equal(1, await db.Queryable<WarehouseOrderPickSession>().CountAsync());
        Assert.Equal(8, await db.Queryable<WarehouseOrderPickParticipant>().CountAsync());
        Assert.Equal(3, (await db.Queryable<WareHouseOrder>().SingleAsync()).FlowStatus);
        Assert.True(submitted.Success, submitted.Message);
        Assert.Equal(2m, (await db.Queryable<WareHouseOrderDetails>().SingleAsync()).AllocQuantity);
        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, late.ErrorCode);
    }

    private static WarehousePickerContext Picker(string userGuid) => new(userGuid, $"Picker {userGuid}", false, userGuid, null);

    private static WarehousePickingRecordRequestDto Scan() => new()
    {
        DetailGuid = "d-1",
        Source = WarehouseOrderPickSources.Scan,
        ScannedCode = "9312345678905",
        MatchedBy = WarehouseOrderPickMatchKinds.Barcode,
        ClientRequestId = Guid.NewGuid(),
    };

    private static async Task SeedOrderAsync(IsolatedDatabase database, int minOrderQuantity)
    {
        using var db = database.CreateClient();
        await db.Insertable(new WareHouseOrder { OrderGUID = OrderGuid, OrderNo = "2026-0418", StoreCode = "1013", FlowStatus = 1 }).ExecuteCommandAsync();
        await db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "d-1",
            OrderGUID = OrderGuid,
            StoreCode = "1013",
            ProductCode = "P-1",
            Quantity = 36,
            OEMPrice = 1m,
            ImportPrice = 1m,
        }).ExecuteCommandAsync();
        await db.Insertable(new Product { ProductCode = "P-1", ItemNumber = "HB1", Barcode = "9312345678905", ProductName = "Cup" }).ExecuteCommandAsync();
        await db.Insertable(new WarehouseProduct { ProductCode = "P-1", MinOrderQuantity = minOrderQuantity, IsActive = true }).ExecuteCommandAsync();
    }

    private static WarehousePickingService CreateService(IsolatedDatabase database) =>
        new(CreateContext(database.CreateClient()), Mock.Of<IProductWarehouseReactService>(), NullLogger<WarehousePickingService>.Instance);

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;

        private IsolatedDatabase(string serverConnectionString, string databaseName, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(LocalSupplierCategorySqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var databaseName = $"hb_pick_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{databaseName}];");
            var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = databaseName };
            var database = new IsolatedDatabase(server, databaseName, builder.ConnectionString);

            // 与生产同名同结构的最小权限表，验证迁移里的权限码幂等入库。
            await database.ExecuteAsync("""
CREATE TABLE dbo.HbwebSysPermissions (
    Id nvarchar(36) NOT NULL PRIMARY KEY, Code nvarchar(100) NOT NULL, Name nvarchar(100) NULL,
    Category nvarchar(100) NULL, Description nvarchar(500) NULL, CreatedAt datetime2 NOT NULL,
    CreatedBy nvarchar(100) NULL, UpdatedAt datetime2 NULL, UpdatedBy nvarchar(100) NULL, IsDeleted bit NOT NULL
);
""");
            await database.ExecuteAsync(WarehouseOrderPickingSchema.ApplySql);
            await database.ExecuteAsync(WarehouseOrderPickingSchema.VerifySql);
            await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.ApplySql);
            await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.VerifySql);
            await database.ExecuteAsync(WarehouseOrderPickAssignmentSchema.ApplySql);
            await database.ExecuteAsync(WarehouseOrderPickAssignmentSchema.VerifySql);
            using var db = database.CreateClient();
            db.CodeFirst.InitTables(
                typeof(WareHouseOrder),
                typeof(WareHouseOrderDetails),
                typeof(Product),
                typeof(WarehouseProduct),
                typeof(Store),
                typeof(ProductSetCode),
                typeof(StoreMultiCodeProduct),
                typeof(ProductLocation),
                typeof(Location)
            );
            return database;
        }

        public SqlSugarClient CreateClient() =>
            new(new ConnectionConfig
            {
                ConnectionString = ConnectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(ConnectionString, sql);

        public async Task<int> ScalarAsync(string sql)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnAsync(
                _serverConnectionString,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];"
            );
        }

        private static async Task ExecuteOnAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }
}
