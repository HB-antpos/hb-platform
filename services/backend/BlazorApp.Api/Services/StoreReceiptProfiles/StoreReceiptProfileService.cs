using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.SqlClient;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreReceiptProfiles;

/// <summary>
/// 门店小票资料「下发」：总部在 Web 分店管理页点「下发」才生成快照（新版本），收银端只认快照；
/// 改分店资料只是保存到 Store，不影响收银端。本服务负责三件事：
/// 1. status：每家店 Store 当前值与最新快照的对比，以及设备应用情况；
/// 2. devices：某家店的 POS 设备清单与每台设备已应用的版本；
/// 3. publish：原子地给一批门店生成新版本快照。
/// </summary>
public interface IStoreReceiptProfileService
{
    Task<ApiResponse<List<StoreReceiptProfileStatusItemDto>>> GetStatusAsync(
        IReadOnlyList<string>? storeGuids,
        CancellationToken cancellationToken = default
    );

    Task<ApiResponse<StoreReceiptProfileDevicesDto>> GetDevicesAsync(
        string storeGuid,
        CancellationToken cancellationToken = default
    );

    Task<ApiResponse<StoreReceiptProfilePublishResultDto>> PublishAsync(
        IReadOnlyList<string>? storeGuids,
        string? publishedBy,
        CancellationToken cancellationToken = default
    );
}

public sealed class StoreReceiptProfileService : IStoreReceiptProfileService
{
    public const int MaxStoresPerRequest = 100;
    private const int MaxPublishedByLength = 100;
    private const string PosDeviceType = "POS";
    private const int EnabledDeviceStatus = 1;
    // SQL Server 单语句参数上限 2100；回执按设备编号分批查，留足余量。
    private const int AckLookupChunkSize = 500;

    private readonly ISqlSugarClient _mainDb;
    private readonly ISqlSugarClient _posmDb;
    private readonly ILogger<StoreReceiptProfileService> _logger;
    private readonly TimeProvider _timeProvider;
    // 仅测试使用：在版本号已读出、快照尚未写入之间插入一段逻辑，用来稳定复现主键冲突。生产路径恒为 null。
    private readonly Func<Task>? _beforeInsertHookForTests;

    public StoreReceiptProfileService(
        SqlSugarContext mainContext,
        POSMSqlSugarContext posmContext,
        ILogger<StoreReceiptProfileService> logger,
        TimeProvider timeProvider
    )
        : this(mainContext.Db, posmContext.Db, logger, timeProvider, null) { }

    internal StoreReceiptProfileService(
        ISqlSugarClient mainDb,
        ISqlSugarClient posmDb,
        ILogger<StoreReceiptProfileService> logger,
        TimeProvider timeProvider,
        Func<Task>? beforeInsertHookForTests = null
    )
    {
        _mainDb = mainDb;
        _posmDb = posmDb;
        _logger = logger;
        _timeProvider = timeProvider;
        _beforeInsertHookForTests = beforeInsertHookForTests;
    }

    // ───────────────────────── status ─────────────────────────

    public async Task<ApiResponse<List<StoreReceiptProfileStatusItemDto>>> GetStatusAsync(
        IReadOnlyList<string>? storeGuids,
        CancellationToken cancellationToken = default
    )
    {
        var guids = ValidateStoreGuids(storeGuids, out var requestError);
        if (guids is null)
        {
            return ApiResponse<List<StoreReceiptProfileStatusItemDto>>.Error(
                requestError!,
                StoreReceiptProfileErrorCodes.InvalidRequest
            );
        }

        var stores = await LoadStoresAsync(guids, cancellationToken);
        // 顺序与请求一致；找不到或已软删的门店不返回该项。
        var found = guids
            .Where(guid => stores.ContainsKey(guid))
            .Select(guid => stores[guid])
            .ToList();
        if (found.Count == 0)
        {
            return ApiResponse<List<StoreReceiptProfileStatusItemDto>>.OK(new());
        }

        var codes = found.Select(store => store.StoreCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var latestByCode = await LoadLatestReleasesAsync(_mainDb, codes, lockForUpdate: false);
        var deviceView = await LoadDeviceViewAsync(codes, cancellationToken);

        var items = new List<StoreReceiptProfileStatusItemDto>(found.Count);
        foreach (var store in found)
        {
            latestByCode.TryGetValue(store.StoreCode, out var latest);
            var current = ToFields(store);
            var latestFields = latest is null ? null : ToFields(latest);
            var latestVersion = latest?.Version ?? 0;
            var devices = deviceView.GetDevices(store.StoreCode);

            items.Add(
                new StoreReceiptProfileStatusItemDto
                {
                    StoreGuid = store.StoreGUID,
                    StoreCode = store.StoreCode,
                    StoreName = store.StoreName,
                    Status = latest is null
                        ? StoreReceiptProfileStatuses.Never
                        : SameFields(current, latestFields!)
                            ? StoreReceiptProfileStatuses.Synced
                            : StoreReceiptProfileStatuses.Pending,
                    LatestVersion = latestVersion,
                    PublishedAtUtc = latest is null ? null : AsUtc(latest.PublishedAtUtc),
                    PublishedBy = latest?.PublishedBy,
                    Current = current,
                    Latest = latestFields,
                    DeviceTotal = devices.Count,
                    // latestVersion=0 时没有「已应用」可言，固定为 0。
                    DeviceApplied = latestVersion > 0
                        ? devices.Count(device => deviceView.IsUpToDate(device, latestVersion))
                        : 0,
                }
            );
        }

        return ApiResponse<List<StoreReceiptProfileStatusItemDto>>.OK(items);
    }

    // ───────────────────────── devices ─────────────────────────

    public async Task<ApiResponse<StoreReceiptProfileDevicesDto>> GetDevicesAsync(
        string storeGuid,
        CancellationToken cancellationToken = default
    )
    {
        var guid = storeGuid?.Trim();
        var store = string.IsNullOrEmpty(guid)
            ? null
            : (await LoadStoresAsync(new[] { guid }, cancellationToken)).GetValueOrDefault(guid);
        if (store is null)
        {
            return ApiResponse<StoreReceiptProfileDevicesDto>.Error(
                "门店不存在或已删除",
                StoreReceiptProfileErrorCodes.StoreNotFound
            );
        }

        var codes = new[] { store.StoreCode };
        var latestByCode = await LoadLatestReleasesAsync(_mainDb, codes, lockForUpdate: false);
        latestByCode.TryGetValue(store.StoreCode, out var latest);
        var latestVersion = latest?.Version ?? 0;
        var deviceView = await LoadDeviceViewAsync(codes, cancellationToken);

        var devices = deviceView
            .GetDevices(store.StoreCode)
            .OrderBy(device => device.系统设备编号, StringComparer.OrdinalIgnoreCase)
            .Select(device =>
            {
                var ack = deviceView.FindAck(device);
                return new StoreReceiptProfileDeviceDto
                {
                    DeviceCode = device.系统设备编号,
                    DeviceSystem = device.设备系统,
                    ClientKind = ResolveClientKind(device.设备系统),
                    DeviceStatus = device.设备状态,
                    IsOnline = device.是否在线,
                    LastHeartbeatAt = device.最后心跳时间,
                    // 只采信属于「设备当前门店」的回执；换店后旧店回执视为尚未应用。
                    AppliedVersion = ack?.AppliedVersion,
                    AppliedAtUtc = ack is null ? null : AsUtc(ack.AppliedAtUtc),
                    UpToDate = latestVersion > 0 && deviceView.IsUpToDate(device, latestVersion),
                };
            })
            .ToList();

        return ApiResponse<StoreReceiptProfileDevicesDto>.OK(
            new StoreReceiptProfileDevicesDto
            {
                StoreGuid = store.StoreGUID,
                StoreCode = store.StoreCode,
                LatestVersion = latestVersion,
                Devices = devices,
            }
        );
    }

    // ───────────────────────── publish ─────────────────────────

    public async Task<ApiResponse<StoreReceiptProfilePublishResultDto>> PublishAsync(
        IReadOnlyList<string>? storeGuids,
        string? publishedBy,
        CancellationToken cancellationToken = default
    )
    {
        var guids = ValidateStoreGuids(storeGuids, out var requestError);
        if (guids is null)
        {
            return ApiResponse<StoreReceiptProfilePublishResultDto>.Error(
                requestError!,
                StoreReceiptProfileErrorCodes.InvalidRequest
            );
        }

        var stores = await LoadStoresAsync(guids, cancellationToken);

        // 批量原子：先逐店校验并收集全部原因，任一店不可下发则整批不写入。
        var errors = new List<StoreReceiptProfilePublishErrorDetailDto>();
        foreach (var guid in guids)
        {
            stores.TryGetValue(guid, out var store);
            var error = ValidateStoreForPublish(guid, store);
            if (error is not null)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            return ApiResponse<StoreReceiptProfilePublishResultDto>.Error(
                $"有 {errors.Count} 家门店不能下发，本次没有下发任何门店",
                StoreReceiptProfileErrorCodes.NotPublishable,
                errors
            );
        }

        var ordered = guids.Select(guid => stores[guid]).ToList();
        var codes = ordered.Select(store => store.StoreCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var actor = Truncate(publishedBy?.Trim(), MaxPublishedByLength);

        var items = new List<StoreReceiptProfilePublishItemDto>(ordered.Count);
        var newRows = new List<StoreReceiptProfileRelease>();

        await _mainDb.Ado.BeginTranAsync();
        try
        {
            // 同一事务内带 UPDLOCK, HOLDLOCK 读出每家店的最新快照（含最大版本）：
            // 并发下发同一家店时第二个事务会等到第一个提交，随后读到新版本再 +1，不会算出同一个版本号；
            // 从未下发过的店没有行，HOLDLOCK 的范围锁同样挡住并发插入 (StoreCode, 1)。
            var latestByCode = await LoadLatestReleasesAsync(_mainDb, codes, lockForUpdate: true);

            foreach (var store in ordered)
            {
                latestByCode.TryGetValue(store.StoreCode, out var latest);
                if (latest is not null && SameFields(ToFields(store), ToFields(latest)))
                {
                    // 与最新快照一致：不生成新版本，重复点「下发」是安全的空操作。
                    items.Add(NewItem(store, StoreReceiptProfileOutcomes.Unchanged, latest.Version));
                    continue;
                }

                var version = (latest?.Version ?? 0) + 1;
                var fields = ToFields(store);
                newRows.Add(
                    new StoreReceiptProfileRelease
                    {
                        StoreCode = store.StoreCode,
                        Version = version,
                        StoreName = fields.StoreName,
                        BrandName = fields.BrandName,
                        Address = fields.Address,
                        Phone = fields.Phone,
                        ABN = fields.Abn,
                        ReturnPolicy = fields.ReturnPolicy,
                        PublishedAtUtc = nowUtc,
                        PublishedBy = actor,
                    }
                );
                items.Add(NewItem(store, StoreReceiptProfileOutcomes.Published, version));
            }

            if (_beforeInsertHookForTests is not null)
            {
                await _beforeInsertHookForTests();
            }

            if (newRows.Count > 0)
            {
                // 一条语句写入全部新快照：任一行主键冲突整条失败并随事务回滚，不会留下半批。
                await _mainDb.Insertable(newRows).ExecuteCommandAsync();
            }

            await _mainDb.Ado.CommitTranAsync();
        }
        catch (Exception exception) when (IsPublishConflict(exception))
        {
            await TryRollbackAsync();
            _logger.LogWarning(exception, "门店小票资料下发遇到并发冲突，整批已回滚");
            return ApiResponse<StoreReceiptProfilePublishResultDto>.Error(
                "下发时与其他操作冲突，本次没有下发任何门店，请稍后重试",
                StoreReceiptProfileErrorCodes.PublishConflict
            );
        }
        catch
        {
            await TryRollbackAsync();
            throw;
        }

        var publishedCount = items.Count(item => item.Outcome == StoreReceiptProfileOutcomes.Published);
        _logger.LogInformation(
            "门店小票资料下发完成：请求 {Requested} 家，新版本 {Published} 家，无变化 {Unchanged} 家，操作人 {Actor}",
            items.Count,
            publishedCount,
            items.Count - publishedCount,
            actor
        );

        return ApiResponse<StoreReceiptProfilePublishResultDto>.OK(
            new StoreReceiptProfilePublishResultDto
            {
                RequestedCount = items.Count,
                PublishedCount = publishedCount,
                UnchangedCount = items.Count - publishedCount,
                Items = items,
            }
        );
    }

    // ───────────────────────── 校验与归一 ─────────────────────────

    /// <summary>
    /// 请求参数校验：1–100 个、不重复（忽略大小写与首尾空白）、非空白。
    /// 通过时返回去掉首尾空白后的 GUID 列表（保持请求顺序）；失败返回 null 并给出原因。
    /// </summary>
    internal static List<string>? ValidateStoreGuids(IReadOnlyList<string>? storeGuids, out string? error)
    {
        error = null;
        if (storeGuids is null || storeGuids.Count == 0)
        {
            error = "storeGuids 至少包含 1 个门店";
            return null;
        }

        if (storeGuids.Count > MaxStoresPerRequest)
        {
            error = $"storeGuids 一次最多 {MaxStoresPerRequest} 个门店";
            return null;
        }

        var normalized = new List<string>(storeGuids.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in storeGuids)
        {
            var guid = raw?.Trim();
            if (string.IsNullOrEmpty(guid))
            {
                error = "storeGuids 不能包含空白项";
                return null;
            }

            if (!seen.Add(guid))
            {
                error = "storeGuids 不能重复";
                return null;
            }

            normalized.Add(guid);
        }

        return normalized;
    }

    private static StoreReceiptProfilePublishErrorDetailDto? ValidateStoreForPublish(
        string requestedGuid,
        Store? store
    )
    {
        if (store is null)
        {
            return Detail(requestedGuid, null, StoreReceiptProfileErrorCodes.StoreNotFound, "门店不存在或已删除");
        }

        if (!store.IsActive)
        {
            return Detail(store, StoreReceiptProfileErrorCodes.StoreInactive, "门店已停用，不能下发小票资料");
        }

        if (string.IsNullOrWhiteSpace(store.StoreCode))
        {
            // 收银端只按认证声明里的门店代码取快照，没有门店代码的快照永远读不到。
            return Detail(store, StoreReceiptProfileErrorCodes.StoreCodeRequired, "门店代码为空，不能下发小票资料");
        }

        if (string.IsNullOrWhiteSpace(store.StoreName))
        {
            return Detail(store, StoreReceiptProfileErrorCodes.StoreNameRequired, "门店名称为空，请先在分店资料里补全再下发");
        }

        // 与 Hbpos.Api 的 StoreReceiptProfileGuard 口径一致：含非法控制字符的资料下发后会让收银端接口整体 400。
        var invalidField = StoreReceiptProfileGuard.FindInvalidField(
            store.StoreCode,
            store.StoreName,
            store.BrandName,
            store.Address,
            store.Phone,
            store.ABN,
            store.ReturnPolicy
        );
        if (invalidField is not null)
        {
            return Detail(
                store,
                StoreReceiptProfileErrorCodes.InvalidCharacters,
                $"{invalidField} 含不可打印的控制字符，请先在分店资料里清理再下发"
            );
        }

        return null;
    }

    private static StoreReceiptProfilePublishErrorDetailDto Detail(Store store, string errorCode, string message) =>
        Detail(store.StoreGUID, store.StoreCode, errorCode, message);

    private static StoreReceiptProfilePublishErrorDetailDto Detail(
        string storeGuid,
        string? storeCode,
        string errorCode,
        string message
    ) =>
        new()
        {
            StoreGuid = storeGuid,
            StoreCode = storeCode,
            ErrorCode = errorCode,
            Message = message,
        };

    /// <summary>
    /// Store 与快照共用的归一口径：首尾空白去掉，可空字段的空串/纯空白视为 null；其余区分大小写。
    /// current、latest 与「是否一致」比较都用同一个归一结果，所以确认框里不会出现「看起来没变却显示有差异」。
    /// </summary>
    internal static StoreReceiptProfileFieldsDto ToFields(Store store) =>
        Normalize(store.StoreName, store.BrandName, store.Address, store.Phone, store.ABN, store.ReturnPolicy);

    internal static StoreReceiptProfileFieldsDto ToFields(StoreReceiptProfileRelease release) =>
        Normalize(
            release.StoreName,
            release.BrandName,
            release.Address,
            release.Phone,
            release.ABN,
            release.ReturnPolicy
        );

    private static StoreReceiptProfileFieldsDto Normalize(
        string? storeName,
        string? brandName,
        string? address,
        string? phone,
        string? abn,
        string? returnPolicy
    ) =>
        new()
        {
            StoreName = storeName?.Trim() ?? string.Empty,
            BrandName = NormalizeOptional(brandName),
            Address = NormalizeOptional(address),
            Phone = NormalizeOptional(phone),
            Abn = NormalizeOptional(abn),
            ReturnPolicy = NormalizeOptional(returnPolicy),
        };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool SameFields(StoreReceiptProfileFieldsDto left, StoreReceiptProfileFieldsDto right) =>
        string.Equals(left.StoreName, right.StoreName, StringComparison.Ordinal)
        && string.Equals(left.BrandName, right.BrandName, StringComparison.Ordinal)
        && string.Equals(left.Address, right.Address, StringComparison.Ordinal)
        && string.Equals(left.Phone, right.Phone, StringComparison.Ordinal)
        && string.Equals(left.Abn, right.Abn, StringComparison.Ordinal)
        && string.Equals(left.ReturnPolicy, right.ReturnPolicy, StringComparison.Ordinal);

    /// <summary>
    /// 由设备系统推导客户端类型，与 Hbpos.Api 写回执时由认证声明 hbpos_device_system 推导的口径一致：
    /// Windows→wpf，iPadOS/iOS→ipad，Android→handheld，其余→other（大小写不敏感）。
    /// </summary>
    internal static string ResolveClientKind(string? deviceSystem)
    {
        var system = deviceSystem?.Trim();
        if (string.Equals(system, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return PosReceiptProfileAck.ClientKindWpf;
        }

        if (
            string.Equals(system, "iPadOS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(system, "iOS", StringComparison.OrdinalIgnoreCase)
        )
        {
            return PosReceiptProfileAck.ClientKindIpad;
        }

        if (string.Equals(system, "Android", StringComparison.OrdinalIgnoreCase))
        {
            return PosReceiptProfileAck.ClientKindHandheld;
        }

        return PosReceiptProfileAck.ClientKindOther;
    }

    private static StoreReceiptProfilePublishItemDto NewItem(Store store, string outcome, int version) =>
        new()
        {
            StoreGuid = store.StoreGUID,
            StoreCode = store.StoreCode,
            Outcome = outcome,
            Version = version,
        };

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>SQL Server 读出的 datetime2 是 Unspecified，标成 UTC 后序列化才带 Z。</summary>
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// 主键冲突（2601/2627）或死锁牺牲（1205）都说明有并发下发在同一家店上竞争，统一映射为可重试的 409。
    /// SQLite 只在测试里出现，按 UNIQUE 冲突文案识别。
    /// </summary>
    internal static bool IsPublishConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: 2601 or 2627 or 1205 })
            {
                return true;
            }

            if (
                current.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException"
                && current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private async Task TryRollbackAsync()
    {
        try
        {
            await _mainDb.Ado.RollbackTranAsync();
        }
        catch (Exception rollbackException)
        {
            // 回滚失败不能掩盖原始异常；连接关闭时 SQL Server 也会自动回滚未提交事务。
            _logger.LogError(rollbackException, "门店小票资料下发回滚失败");
        }
    }

    // ───────────────────────── 数据读取 ─────────────────────────

    /// <summary>按 GUID 读未软删的门店，字典键忽略大小写（SQL Server 默认排序规则本就不区分，SQLite 测试库区分）。</summary>
    private async Task<Dictionary<string, Store>> LoadStoresAsync(
        IReadOnlyCollection<string> guids,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = guids.ToList();
        var stores = await _mainDb
            .Queryable<Store>()
            .Where(store => list.Contains(store.StoreGUID) && store.IsDeleted == false)
            .ToListAsync();
        var result = new Dictionary<string, Store>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in stores)
        {
            result[store.StoreGUID] = store;
        }

        return result;
    }

    /// <summary>
    /// 每家店取最大版本那一行。lockForUpdate=true 时（SQL Server）对两处引用都加 UPDLOCK, HOLDLOCK，
    /// 必须在调用方已开启的事务内使用；其他数据库（测试用 SQLite）没有锁提示，语义等价。
    /// </summary>
    private static async Task<Dictionary<string, StoreReceiptProfileRelease>> LoadLatestReleasesAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<string> storeCodes,
        bool lockForUpdate
    )
    {
        var result = new Dictionary<string, StoreReceiptProfileRelease>(StringComparer.OrdinalIgnoreCase);
        if (storeCodes.Count == 0)
        {
            return result;
        }

        var hint = lockForUpdate && db.CurrentConnectionConfig.DbType == DbType.SqlServer
            ? " WITH (UPDLOCK, HOLDLOCK)"
            : string.Empty;
        var parameters = new List<SugarParameter>(storeCodes.Count);
        var names = new List<string>(storeCodes.Count);
        // 门店代码按固定顺序进入 IN 列表：并发下发的事务以相同顺序申请范围锁，不因请求顺序不同而互相死锁。
        foreach (var code in storeCodes.OrderBy(code => code, StringComparer.Ordinal))
        {
            var name = $"@code{parameters.Count}";
            names.Add(name);
            parameters.Add(new SugarParameter(name, code));
        }

        var sql = $"""
            SELECT r.[StoreCode], r.[Version], r.[StoreName], r.[BrandName], r.[Address], r.[Phone],
                   r.[ABN], r.[ReturnPolicy], r.[PublishedAtUtc], r.[PublishedBy]
            FROM [StoreReceiptProfileRelease] AS r{hint}
            INNER JOIN (
                SELECT [StoreCode], MAX([Version]) AS [MaxVersion]
                FROM [StoreReceiptProfileRelease]{hint}
                WHERE [StoreCode] IN ({string.Join(", ", names)})
                GROUP BY [StoreCode]
            ) AS m ON m.[StoreCode] = r.[StoreCode] AND m.[MaxVersion] = r.[Version]
            """;
        var rows = await db.Ado.SqlQueryAsync<StoreReceiptProfileRelease>(sql, parameters);
        foreach (var row in rows)
        {
            result[row.StoreCode] = row;
        }

        return result;
    }

    /// <summary>
    /// 设备应用情况：POSM 设备（分店代码、设备类型='POS'、设备状态=1）与主库 Ack 在应用层内存关联，不跨库 SQL。
    /// </summary>
    private async Task<DeviceView> LoadDeviceViewAsync(
        IReadOnlyCollection<string> storeCodes,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var codes = storeCodes.ToList();
        var candidates = await _posmDb
            .Queryable<POSM_设备注册信息表>()
            .Where(device => device.设备状态 == EnabledDeviceStatus && codes.Contains(device.分店代码!))
            .ToListAsync();
        // 设备类型在内存里按大小写不敏感比较（SQL Server 列排序规则本就不区分，SQLite 测试库区分）。
        var devices = candidates
            .Where(device => string.Equals(device.设备类型?.Trim(), PosDeviceType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 回执按设备编号（主键）取，不按门店取：门店归属的判断统一放在 DeviceView 里做忽略大小写与首尾空白的比较，
        // 这样「设备换店后旧店回执不计入新店」的口径不依赖数据库排序规则。分批避免 IN 参数过多。
        var deviceCodes = devices
            .Select(device => device.系统设备编号.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var acks = new List<PosReceiptProfileAck>();
        foreach (var chunk in deviceCodes.Chunk(AckLookupChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = chunk.ToList();
            acks.AddRange(
                await _mainDb
                    .Queryable<PosReceiptProfileAck>()
                    .Where(ack => batch.Contains(ack.DeviceCode))
                    .ToListAsync()
            );
        }

        return new DeviceView(devices, acks);
    }

    /// <summary>设备与回执的内存关联结果。</summary>
    private sealed class DeviceView
    {
        private readonly Dictionary<string, List<POSM_设备注册信息表>> _devicesByStore =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PosReceiptProfileAck> _acksByDevice =
            new(StringComparer.OrdinalIgnoreCase);

        public DeviceView(IEnumerable<POSM_设备注册信息表> devices, IEnumerable<PosReceiptProfileAck> acks)
        {
            foreach (var device in devices)
            {
                var storeCode = NormalizeCode(device.分店代码);
                if (!_devicesByStore.TryGetValue(storeCode, out var list))
                {
                    list = new List<POSM_设备注册信息表>();
                    _devicesByStore[storeCode] = list;
                }

                list.Add(device);
            }

            foreach (var ack in acks)
            {
                _acksByDevice[NormalizeCode(ack.DeviceCode)] = ack;
            }
        }

        public List<POSM_设备注册信息表> GetDevices(string storeCode) =>
            _devicesByStore.TryGetValue(NormalizeCode(storeCode), out var list)
                ? list
                : new List<POSM_设备注册信息表>();

        /// <summary>
        /// 只采信 Ack.StoreCode 等于设备当前 POSM 分店代码（忽略大小写与首尾空白）的回执：
        /// 版本号是「每家店各自从 1 递增」，设备换店后旧店的回执版本号对新店没有意义，必须视为尚未应用。
        /// </summary>
        public PosReceiptProfileAck? FindAck(POSM_设备注册信息表 device)
        {
            if (!_acksByDevice.TryGetValue(NormalizeCode(device.系统设备编号), out var ack))
            {
                return null;
            }

            return string.Equals(
                NormalizeCode(ack.StoreCode),
                NormalizeCode(device.分店代码),
                StringComparison.OrdinalIgnoreCase
            )
                ? ack
                : null;
        }

        public bool IsUpToDate(POSM_设备注册信息表 device, int latestVersion)
        {
            var ack = FindAck(device);
            return latestVersion > 0 && ack is not null && ack.AppliedVersion >= latestVersion;
        }

        private static string NormalizeCode(string? code) => code?.Trim() ?? string.Empty;
    }
}
