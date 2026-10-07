using Hbpos.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hbpos.Api.Tests;

public sealed class DeviceRuntimeStatusSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_device_runtime_status_column_ddl()
    {
        var executor = new CapturingDeviceRuntimeStatusSchemaSqlExecutor();
        var initializer = new SqlSugarDeviceRuntimeStatusSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(2, executor.SqlStatements.Count);
        var sql = executor.SqlStatements[0];
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_设备注册信息表]', N'U') IS NOT NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否允许交易') IS NULL", sql);
        Assert.Contains("ADD [是否允许交易] BIT NOT NULL", sql);
        Assert.Contains("DEFAULT (1) WITH VALUES", sql);
        Assert.Contains("ERROR_NUMBER() NOT IN (2705, 2714)", sql);
        Assert.Contains("OR COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否允许交易') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否在线') IS NULL", sql);
        Assert.Contains("ADD [是否在线] BIT NOT NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'最后心跳时间') IS NULL", sql);
        Assert.Contains("ADD [最后心跳时间] DATETIME2(7) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'当前收银员ID') IS NULL", sql);
        Assert.Contains("ADD [当前收银员ID] NVARCHAR(100) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'当前收银员姓名') IS NULL", sql);
        Assert.Contains("ADD [当前收银员姓名] NVARCHAR(100) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'收银员登录时间') IS NULL", sql);
        Assert.Contains("ADD [收银员登录时间] DATETIME2(7) NULL", sql);
        Assert.Contains("IX_POSM_DeviceRegistration_HardwareId", sql);
        Assert.Contains("([设备硬件识别码])", sql);
        Assert.Contains("IX_POSM_DeviceRegistration_StoreCode_Status", sql);
        Assert.Contains("([分店代码], [设备状态])", sql);
        Assert.Contains("CREATE TABLE [dbo].[POSM_AppReviewGrantConsumptions]", sql);
        Assert.Contains("[GrantId] UNIQUEIDENTIFIER NOT NULL", sql);
        Assert.Contains("CONSTRAINT [PK_POSM_AppReviewGrantConsumptions] PRIMARY KEY", sql);
        Assert.Contains("IX_POSM_AppReviewGrantConsumptions_StoreDevice", sql);
        Assert.Equal(
            BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql,
            executor.SqlStatements[1]);
    }

    [Fact]
    public void Startup_InitializesDeviceRuntimeStatusSchema_WhenPosmConnectionExists()
    {
        var initializer = new RecordingDeviceRuntimeStatusSchemaInitializer();
        using var factory = new RuntimeStatusApiFactory(initializer);

        using var client = factory.CreateClient();

        Assert.Equal(1, initializer.InitializeCallCount);
    }

    private sealed class CapturingDeviceRuntimeStatusSchemaSqlExecutor : IDeviceRuntimeStatusSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeStatusApiFactory(
        IDeviceRuntimeStatusSchemaInitializer runtimeStatusSchemaInitializer)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:MainConnection"] = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-main-test;Trusted_Connection=True;",
                    ["ConnectionStrings:PosmConnection"] = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-posm-test;Trusted_Connection=True;",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStoreSchemaInitializer>();
                services.AddSingleton<IStoreSchemaInitializer>(new NoOpStoreSchemaInitializer());

                services.RemoveAll<IAttendanceQrKeySchemaInitializer>();
                services.AddSingleton<IAttendanceQrKeySchemaInitializer>(new TestNoOpAttendanceQrKeySchemaInitializer());

                services.RemoveAll<IDeviceRuntimeStatusSchemaInitializer>();
                services.AddSingleton(runtimeStatusSchemaInitializer);

                // 本测试只验证 runtime-status 启动初始化；其余 schema initializer
                // 必须保持无副作用，避免 macOS 测试宿主尝试连接 LocalDB。
                services.RemoveAll<ISharedHeldOrderSchemaInitializer>();
                services.AddSingleton<ISharedHeldOrderSchemaInitializer>(
                    new NoOpSharedHeldOrderSchemaInitializer());

                services.RemoveAll<IOperationAuditSchemaInitializer>();
                services.AddSingleton<IOperationAuditSchemaInitializer>(new TestNoOpOperationAuditSchemaInitializer());

                services.RemoveAll<IOrderSyncSchemaInitializer>();
                services.AddSingleton<IOrderSyncSchemaInitializer>(new TestNoOpOrderSyncSchemaInitializer());

                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton<IAdvertisementSchemaInitializer>(new NoOpAdvertisementSchemaInitializer());

                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton<ILinklyCloudCredentialSchemaInitializer>(new NoOpLinklyCloudCredentialSchemaInitializer());

                services.RemoveAll<ILinklyCloudBackendAsyncSchemaInitializer>();
                services.AddSingleton<ILinklyCloudBackendAsyncSchemaInitializer>(new NoOpLinklyCloudBackendAsyncSchemaInitializer());

                services.RemoveAll<ILinklySettlementSchemaInitializer>();
                services.AddSingleton<ILinklySettlementSchemaInitializer>(
                    new TestNoOpLinklySettlementSchemaInitializer());

                services.RemoveAll<IDailyCloseSchemaInitializer>();
                services.AddSingleton<IDailyCloseSchemaInitializer>(
                    new TestNoOpDailyCloseSchemaInitializer());

                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(new NoOpSquareTokenSchemaInitializer());

                services.RemoveAll<ISquareWebhookSchemaInitializer>();
                services.AddSingleton<ISquareWebhookSchemaInitializer>(new NoOpSquareWebhookSchemaInitializer());

                services.RemoveAll<IInstallmentRepaymentClaimSchemaInitializer>();
                services.AddSingleton<IInstallmentRepaymentClaimSchemaInitializer>(
                    new TestNoOpInstallmentRepaymentClaimSchemaInitializer());
                services.RemoveAll<IInstallmentCancelClaimSchemaInitializer>();
                services.AddSingleton<IInstallmentCancelClaimSchemaInitializer>(
                    new TestNoOpInstallmentCancelClaimSchemaInitializer());
                services.RemoveAll<IInstallmentSchemaInitializer>();
                services.AddSingleton<IInstallmentSchemaInitializer>(
                    new TestNoOpInstallmentSchemaInitializer());
            });
        }
    }

    private sealed class RecordingDeviceRuntimeStatusSchemaInitializer : IDeviceRuntimeStatusSchemaInitializer
    {
        public int InitializeCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpStoreSchemaInitializer : IStoreSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpAdvertisementSchemaInitializer : IAdvertisementSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSharedHeldOrderSchemaInitializer : ISharedHeldOrderSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLinklyCloudCredentialSchemaInitializer : ILinklyCloudCredentialSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLinklyCloudBackendAsyncSchemaInitializer : ILinklyCloudBackendAsyncSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSquareTokenSchemaInitializer : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSquareWebhookSchemaInitializer : ISquareWebhookSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
