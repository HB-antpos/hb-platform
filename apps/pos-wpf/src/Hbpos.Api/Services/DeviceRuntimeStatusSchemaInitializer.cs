using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface IDeviceRuntimeStatusSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceRuntimeStatusSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarDeviceRuntimeStatusSchemaInitializer(
    IDeviceRuntimeStatusSchemaSqlExecutor sqlExecutor) : IDeviceRuntimeStatusSchemaInitializer
{
    // 关键逻辑：本地 POS API 也会直接写 POSM 设备表，启动时补齐运行态列，避免心跳上报时报“列名无效”。
    internal const string EnsureRuntimeStatusColumnsSql = """
        IF OBJECT_ID(N'[dbo].[POSM_设备注册信息表]', N'U') IS NOT NULL
        BEGIN
            BEGIN TRY
                IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否允许交易') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[POSM_设备注册信息表]
                        ADD [是否允许交易] BIT NOT NULL
                        CONSTRAINT [DF_POSM_DeviceRegistration_AllowTransactions] DEFAULT (1) WITH VALUES;
                END;
            END TRY
            BEGIN CATCH
                -- Web 与 POS API 可能同时启动；只有另一进程已成功补列时才忽略重复列/约束错误。
                IF ERROR_NUMBER() NOT IN (2705, 2714)
                    OR COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否允许交易') IS NULL
                BEGIN
                    THROW;
                END;
            END CATCH;

            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否在线') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_设备注册信息表]
                    ADD [是否在线] BIT NOT NULL DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'最后心跳时间') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_设备注册信息表]
                    ADD [最后心跳时间] DATETIME2(7) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'当前收银员ID') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_设备注册信息表]
                    ADD [当前收银员ID] NVARCHAR(100) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'当前收银员姓名') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_设备注册信息表]
                    ADD [当前收银员姓名] NVARCHAR(100) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'收银员登录时间') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_设备注册信息表]
                    ADD [收银员登录时间] DATETIME2(7) NULL;
            END;

            -- 客户端（WPF）随心跳上报的程序版本；可空，老客户端不上报时保持 NULL。
            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'应用版本') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_设备注册信息表]
                    ADD [应用版本] NVARCHAR(50) NULL;
            END;

            BEGIN TRY
                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_设备注册信息表]')
                      AND [name] = N'IX_POSM_DeviceRegistration_HardwareId')
                BEGIN
                    -- 关键逻辑：为匿名注册的 SERIALIZABLE 范围锁提供稳定键范围，避免并发插入绕过同硬件检查。
                    CREATE NONCLUSTERED INDEX [IX_POSM_DeviceRegistration_HardwareId]
                        ON [dbo].[POSM_设备注册信息表] ([设备硬件识别码])
                        INCLUDE ([ID], [分店代码], [系统设备编号], [设备状态], [设备授权码], [设备系统]);
                END;
            END TRY
            BEGIN CATCH
                -- 多实例可能同时启动；仅忽略另一实例已创建同名索引的竞争。
                IF ERROR_NUMBER() <> 1913
                    OR NOT EXISTS (
                        SELECT 1
                        FROM sys.indexes
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_设备注册信息表]')
                          AND [name] = N'IX_POSM_DeviceRegistration_HardwareId')
                BEGIN
                    THROW;
                END;
            END CATCH;

            BEGIN TRY
                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_设备注册信息表]')
                      AND [name] = N'IX_POSM_DeviceRegistration_StoreCode_Status')
                BEGIN
                    -- 关键逻辑：审核设备上限计数必须锁定目标分店和状态的键范围，而不是依赖全表扫描锁。
                    CREATE NONCLUSTERED INDEX [IX_POSM_DeviceRegistration_StoreCode_Status]
                        ON [dbo].[POSM_设备注册信息表] ([分店代码], [设备状态])
                        INCLUDE ([ID]);
                END;
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() <> 1913
                    OR NOT EXISTS (
                        SELECT 1
                        FROM sys.indexes
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_设备注册信息表]')
                          AND [name] = N'IX_POSM_DeviceRegistration_StoreCode_Status')
                BEGIN
                    THROW;
                END;
            END CATCH;
        END;

        BEGIN TRY
            IF OBJECT_ID(N'[dbo].[POSM_AppReviewGrantConsumptions]', N'U') IS NULL
            BEGIN
                -- 关键逻辑：审核 grant 消费记录独立于可编辑、可删除的设备备注，以主键保证全生命周期只消费一次。
                CREATE TABLE [dbo].[POSM_AppReviewGrantConsumptions]
                (
                    [GrantId] UNIQUEIDENTIFIER NOT NULL,
                    [StoreCode] VARCHAR(50) NOT NULL,
                    [HardwareId] VARCHAR(100) NOT NULL,
                    [DeviceCode] VARCHAR(50) NOT NULL,
                    [ConsumedAtUtc] DATETIME2(7) NOT NULL,
                    CONSTRAINT [PK_POSM_AppReviewGrantConsumptions] PRIMARY KEY ([GrantId])
                );
            END;
        END TRY
        BEGIN CATCH
            -- 多实例只允许忽略另一实例已经完整建表的竞争。
            IF ERROR_NUMBER() <> 2714
                OR OBJECT_ID(N'[dbo].[POSM_AppReviewGrantConsumptions]', N'U') IS NULL
            BEGIN
                THROW;
            END;
        END CATCH;

        BEGIN TRY
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_AppReviewGrantConsumptions]')
                  AND [name] = N'IX_POSM_AppReviewGrantConsumptions_StoreDevice')
            BEGIN
                CREATE NONCLUSTERED INDEX [IX_POSM_AppReviewGrantConsumptions_StoreDevice]
                    ON [dbo].[POSM_AppReviewGrantConsumptions] ([StoreCode], [HardwareId], [DeviceCode]);
            END;
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() <> 1913
                OR NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_AppReviewGrantConsumptions]')
                      AND [name] = N'IX_POSM_AppReviewGrantConsumptions_StoreDevice')
            BEGIN
                THROW;
            END;
        END CATCH;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await sqlExecutor.ExecuteAsync(EnsureRuntimeStatusColumnsSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(
            BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql,
            cancellationToken);
    }
}

public sealed class SqlSugarDeviceRuntimeStatusSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : IDeviceRuntimeStatusSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
