using Hbpos.Api.Data;
using Hbpos.Contracts.Stores;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IStoreReceiptProfileService
{
    Task<StoreReceiptProfileLookupResult> GetCurrentAsync(
        string storeCode,
        CancellationToken cancellationToken);
}

public sealed record StoreReceiptProfileLookupResult(
    StoreReceiptProfileDto? Profile,
    string? ErrorCode = null,
    string? Message = null);

public sealed class StoreReceiptProfileService : IStoreReceiptProfileService
{
    public const string StoreNotFoundCode = "STORE_NOT_FOUND";
    public const string StoreCodeRequiredCode = "STORE_CODE_REQUIRED";
    public const string InvalidCharactersCode = "STORE_PROFILE_INVALID_CHARACTERS";

    private readonly HbposSqlSugarContext? dbContext;
    private readonly Func<string, CancellationToken, Task<StoreReceiptProfileDto?>> loadProfileAsync;

    public StoreReceiptProfileService(HbposSqlSugarContext dbContext)
    {
        this.dbContext = dbContext;
        loadProfileAsync = LoadProfileAsync;
    }

    public StoreReceiptProfileService(
        Func<string, CancellationToken, Task<StoreReceiptProfileDto?>> loadProfileAsync)
    {
        this.loadProfileAsync = loadProfileAsync;
    }

    public async Task<StoreReceiptProfileLookupResult> GetCurrentAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return new StoreReceiptProfileLookupResult(null, StoreCodeRequiredCode, "storeCode 不能为空");
        }

        var profile = await loadProfileAsync(storeCode, cancellationToken);
        if (profile is null)
        {
            return new StoreReceiptProfileLookupResult(null, StoreNotFoundCode, "门店不存在或已停用");
        }

        if (!StoreReceiptProfileGuard.IsValid(profile))
        {
            return new StoreReceiptProfileLookupResult(null, InvalidCharactersCode, "门店资料包含不可打印控制字符");
        }

        return new StoreReceiptProfileLookupResult(profile);
    }

    private async Task<StoreReceiptProfileDto?> LoadProfileAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var context = dbContext ?? throw new InvalidOperationException(
            "Db context is required for store receipt profile lookup.");
        cancellationToken.ThrowIfCancellationRequested();

        var row = await context.MainDb.Ado.SqlQuerySingleAsync<StoreReceiptProfileRow>(
            """
            SELECT
                StoreCode,
                StoreName,
                BrandName,
                Address,
                Phone,
                ABN AS Abn,
                ReturnPolicy
            FROM [dbo].[Store]
            WHERE StoreCode = @StoreCode
              AND IsActive = 1
              AND (IsDeleted = 0 OR IsDeleted IS NULL)
            """,
            new SugarParameter("@StoreCode", storeCode));

        if (row is null)
        {
            return null;
        }

        // 门店存在且启用后，若总部下发过快照就以最新快照为准（收银端打印认的是下发版本，
        // 「载入/立即同步」也必须拿到同一份内容）；从未下发（或快照表还没建）时沿用门店当前值，Version=0。
        var release = await StoreReceiptProfileReleaseQueries.GetLatestAsync(context.MainDb, storeCode);
        if (release is not null)
        {
            return release with { StoreCode = row.StoreCode };
        }

        return new StoreReceiptProfileDto(
            row.StoreCode,
            row.StoreName,
            row.BrandName,
            row.Address,
            row.Phone,
            row.Abn,
            row.ReturnPolicy);
    }
}

public static class StoreReceiptProfileGuard
{
    // 仅 Address 与 ReturnPolicy 需要 CR/LF/TAB 排版；其余字段（含 StoreCode/StoreName/
    // BrandName/Phone/Abn）任何控制字符均会污染小票草稿，必须整接口失败且不返回数据。
    public static bool IsValid(StoreReceiptProfileDto profile)
    {
        return NoControlCharacters(profile.StoreCode)
            && NoControlCharacters(profile.StoreName)
            && NoControlCharacters(profile.BrandName)
            && NoControlCharacters(profile.Phone)
            && NoControlCharacters(profile.Abn)
            && AllowedMultiline(profile.Address)
            && AllowedMultiline(profile.ReturnPolicy);
    }

    private static bool NoControlCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllowedMultiline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class StoreReceiptProfileRow
{
    public string StoreCode { get; set; } = string.Empty;

    public string StoreName { get; set; } = string.Empty;

    public string? BrandName { get; set; }

    public string? Address { get; set; }

    public string? Phone { get; set; }

    public string? Abn { get; set; }

    public string? ReturnPolicy { get; set; }
}
