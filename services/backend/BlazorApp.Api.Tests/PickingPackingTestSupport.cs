using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Packing;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 拣货服务现在依赖打包服务（边拣边装 / 提交对账）：各拣货测试共用同一套装配方式。
/// 打包服务与拣货服务共用同一个 SqlSugarClient，与生产「同一请求共用 scoped 的 SqlSugarContext」一致。
/// </summary>
internal static class PickingPackingTestSupport
{
    /// <summary>拣货测试库里要建的打包表：提交时会查订单有没有箱，所以拣货测试库也必须有这些表。</summary>
    public static readonly Type[] PackTables =
    {
        typeof(PackCarton),
        typeof(PackCartonItem),
        typeof(PackPallet),
        typeof(PackCarrier),
        typeof(PackLocation),
        typeof(PackEvent),
        typeof(PackSequence),
    };

    public static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packing:WarehouseName"] = "HB TEST WAREHOUSE",
                ["Packing:TimeZoneId"] = "Australia/Brisbane",
            })
            .Build();

    public static SqlSugarContext Context(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public static IPackingService PackingService(ISqlSugarClient db, TimeProvider? time = null) =>
        new PackingService(Context(db), Configuration(), NullLogger<PackingService>.Instance, time);

    public static IPackingAccessService AccessService(ISqlSugarClient db) =>
        new PackingAccessService(Context(db), Mock.Of<IAuthorizationService>(), Configuration());

    public static WarehousePickingService PickingService(
        ISqlSugarClient db,
        IProductWarehouseReactService productWarehouseService,
        TimeProvider? time = null
    ) =>
        new(
            Context(db),
            productWarehouseService,
            PackingService(db, time),
            AccessService(db),
            NullLogger<WarehousePickingService>.Instance
        );
}
