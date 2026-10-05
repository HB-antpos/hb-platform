using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Api.Tests;

/// <summary>
/// 设备号撞号回归：在真实 SQL Server 上按生产表结构（无（分店代码, 系统设备编号）唯一约束）验证
/// 设备号生成去重、并发分配与 Windows verify 的确定性命中。
/// </summary>
[Collection(DeviceActivationSqlServerCollection.Name)]
[Trait("Category", "SQL")]
public sealed class DeviceCodeCollisionSqlServerIntegrationTests(
    DeviceActivationSqlServerFixture fixture)
{
    private static readonly DateTime FixedNow = new(2026, 10, 5, 11, 1, 0);

    // 生产表只有 PK(ID)、硬件码索引和分店+状态索引；夹具默认的唯一约束会掩盖撞号，必须先去掉。
    private const string UseProductionLikeIndexesSql = """
        ALTER TABLE [dbo].[POSM_设备注册信息表] DROP CONSTRAINT [UX_POSM_Device_StoreCode];
        CREATE NONCLUSTERED INDEX [IX_POSM_DeviceRegistration_HardwareId]
            ON [dbo].[POSM_设备注册信息表] ([设备硬件识别码])
            INCLUDE ([ID], [分店代码], [系统设备编号], [设备状态], [设备授权码], [设备系统]);
        CREATE NONCLUSTERED INDEX [IX_POSM_DeviceRegistration_StoreCode_Status]
            ON [dbo].[POSM_设备注册信息表] ([分店代码], [设备状态])
            INCLUDE ([ID]);
        """;

    private const string RestoreFixtureIndexesSql = """
        DROP INDEX [IX_POSM_DeviceRegistration_HardwareId] ON [dbo].[POSM_设备注册信息表];
        DROP INDEX [IX_POSM_DeviceRegistration_StoreCode_Status] ON [dbo].[POSM_设备注册信息表];
        ALTER TABLE [dbo].[POSM_设备注册信息表]
            ADD CONSTRAINT [UX_POSM_Device_StoreCode] UNIQUE ([分店代码], [系统设备编号]);
        """;

    [DeviceActivationSqlServerFact]
    public async Task VerifyAsync_WhenHistoricalDuplicateDeviceCodes_WindowsHitsOwnRow()
    {
        await WithProductionLikeTableAsync(async () =>
        {
            // 模拟历史撞号：先注册的是其他设备，物理顺序靠前。
            await fixture.SeedDeviceAsync("HW-A", "POS_S002_1101", "S002", 1, "AUTH-A");
            await fixture.SeedDeviceAsync("HW-B", "POS_S002_1101", "S002", 1, "AUTH-B");
            var service = CreateDeviceService();

            var deviceB = await service.VerifyAsync(
                new DeviceVerifyRequest("POS_S002_1101", "S002", "HW-B", DeviceSystem: DeviceSystems.Windows),
                CancellationToken.None);
            var deviceA = await service.VerifyAsync(
                new DeviceVerifyRequest("POS_S002_1101", "S002", "hw-a ", DeviceSystem: DeviceSystems.Windows),
                CancellationToken.None);
            var legacy = await service.VerifyAsync(
                new DeviceVerifyRequest("POS_S002_1101", "S002"),
                CancellationToken.None);

            Assert.True(deviceB.IsAllowed, deviceB.Message);
            Assert.Equal("AUTH-B", deviceB.AuthorizationCode);
            Assert.True(deviceA.IsAllowed, deviceA.Message);
            Assert.Equal("AUTH-A", deviceA.AuthorizationCode);
            // 旧客户端不带硬件码时确定性地取最新一行。
            Assert.True(legacy.IsAllowed, legacy.Message);
            Assert.Equal("AUTH-B", legacy.AuthorizationCode);
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task RegisterAsync_WhenHistoricalDeviceUsedSameHourMinute_AllocatesNextSuffix()
    {
        await WithProductionLikeTableAsync(async () =>
        {
            await fixture.SeedDeviceAsync("HW-OLD", "POS_S002_1101", "S002", 0, "AUTH-OLD", DeviceSystems.IpadOs);
            var service = CreateDeviceService();

            var response = await service.RegisterAsync(
                new DeviceRegisterRequest("S002", "HW-NEW", "Counter 1"),
                CancellationToken.None);

            Assert.Equal("POS_S002_1101_2", response.DeviceCode);
            Assert.Equal(0, await CountDuplicateDeviceCodesAsync());
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task RegisterAsync_WhenConcurrentRequestsForSameStoreAndMinute_AllocatesDistinctDeviceCodes()
    {
        await WithProductionLikeTableAsync(async () =>
        {
            const int requestCount = 6;
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(1, requestCount)
                .Select(index => Task.Run(async () =>
                {
                    // 每个请求独立上下文与连接，模拟多台收银机同一分钟并发注册。
                    var service = CreateDeviceService();
                    start.Wait();
                    return await service.RegisterAsync(
                        new DeviceRegisterRequest("S002", $"HW-CONCURRENT-{index}", $"Counter {index}"),
                        CancellationToken.None);
                }))
                .ToArray();
            start.Set();

            var responses = await Task.WhenAll(tasks);

            var deviceCodes = responses.Select(response => response.DeviceCode).ToArray();
            Assert.Equal(requestCount, deviceCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(deviceCodes, code => Assert.StartsWith("POS_S002_1101", code, StringComparison.Ordinal));
            Assert.Equal(requestCount, await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [分店代码] = 'S002';"));
            Assert.Equal(0, await CountDuplicateDeviceCodesAsync());
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task ReregisterAsync_WhenTargetStoreNeedsNewDeviceCode_AvoidsTakenDeviceCode()
    {
        await WithProductionLikeTableAsync(async () =>
        {
            await fixture.SeedDeviceAsync("HW-X", "POS_S001_0800", "S001", 1, "AUTH-X");
            await fixture.SeedDeviceAsync("HW-Y", "POS_S002_1101", "S002", 1, "AUTH-Y");
            var service = CreateDeviceService();

            var response = await service.ReregisterAsync(
                new DeviceReregisterRequest("S002", "HW-X", "Counter X"),
                new DeviceReregisterContext("POS_S001_0800", "S001", "HW-X"),
                CancellationToken.None);

            Assert.Equal("POS_S002_1101_2", response.DeviceCode);
            Assert.Equal(-1, response.DeviceStatus);
            Assert.Equal(0, await CountDuplicateDeviceCodesAsync());
            Assert.Equal(1, await fixture.ScalarIntAsync(
                """
                SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表]
                WHERE [分店代码] = 'S002' AND [系统设备编号] = 'POS_S002_1101' AND [设备硬件识别码] = 'HW-Y';
                """));
        });
    }

    private DeviceService CreateDeviceService() =>
        new(
            new SqlSugarDeviceRegistrationRepository(fixture.CreateContext()),
            (storeCode, _) => Task.FromResult<DeviceStoreInfo?>(new DeviceStoreInfo(storeCode, "Store " + storeCode)),
            () => FixedNow);

    private Task<int> CountDuplicateDeviceCodesAsync() =>
        fixture.ScalarIntAsync(
            """
            SELECT COUNT(1)
            FROM (
                SELECT [分店代码], [系统设备编号]
                FROM [dbo].[POSM_设备注册信息表]
                GROUP BY [分店代码], [系统设备编号]
                HAVING COUNT(1) > 1
            ) AS duplicates;
            """);

    private async Task WithProductionLikeTableAsync(Func<Task> action)
    {
        await fixture.ResetAsync();
        await fixture.ExecutePosmAsync(UseProductionLikeIndexesSql);
        try
        {
            await action();
        }
        finally
        {
            // 先清空可能的重复行，再恢复夹具约束，保证同集合其他用例的前置条件不变。
            await fixture.ResetAsync();
            await fixture.ExecutePosmAsync(RestoreFixtureIndexesSql);
        }
    }
}
