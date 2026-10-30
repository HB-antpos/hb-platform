using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.PosReceipts;

public enum PosReceiptError
{
    None,
    /// <summary>订单不存在，或公开令牌无效 / 过期 / 已撤销（对外不区分）。</summary>
    NotFound,
    /// <summary>订单状态不适合出在线小票（待支付 / 已取消 / 分期付）。</summary>
    Unsupported,
    /// <summary>员工没有该订单所属分店的权限。</summary>
    Forbidden,
}

public sealed record PosReceiptResult<T>(T? Data, PosReceiptError Error)
{
    public bool Ok => Error == PosReceiptError.None;

    public static PosReceiptResult<T> Success(T data) => new(data, PosReceiptError.None);

    public static PosReceiptResult<T> Fail(PosReceiptError error) => new(default, error);
}

public interface IPosReceiptService
{
    /// <summary>员工端：完整纸面（含银行回单）+ 当前有效链接（没有则为空）。</summary>
    Task<PosReceiptResult<PosReceiptStaffViewDto>> GetStaffViewAsync(
        string orderGuid, IReadOnlyCollection<string>? allowedBranchCodes, CancellationToken cancellationToken);

    /// <summary>员工端：已有有效链接就原样返回，没有才新建。</summary>
    Task<PosReceiptResult<PosReceiptShareLinkDto>> GetOrCreateLinkAsync(
        string orderGuid, IReadOnlyCollection<string>? allowedBranchCodes, string? createdBy, CancellationToken cancellationToken);

    /// <summary>员工端：撤销该订单所有有效链接并新建一条，旧链接立即失效。</summary>
    Task<PosReceiptResult<PosReceiptShareLinkDto>> RegenerateLinkAsync(
        string orderGuid, IReadOnlyCollection<string>? allowedBranchCodes, string? createdBy, CancellationToken cancellationToken);

    /// <summary>客人端：按令牌取只读纸面（不含银行回单）；令牌不合法 / 过期 / 撤销一律 NotFound。</summary>
    Task<PosReceiptResult<PosReceiptDocumentDto>> GetPublicDocumentAsync(string token, CancellationToken cancellationToken);
}

internal sealed class PosReceiptService : IPosReceiptService
{
    /// <summary>令牌密文的 DataProtection 用途隔离串：与其他用途的密文互相解不开。</summary>
    internal const string TokenProtectorPurpose = "HB.Platform.PosReceipt.ShareToken.v1";

    /// <summary>订单状态：1 已支付、3 已退款。0 待支付 / 2 已取消 / 4 分期付 的小票内容与收银机不同，本期不出在线小票。</summary>
    private static readonly int[] SupportedStatuses = [1, 3];

    private readonly POSMSqlSugarContext _posm;
    private readonly SqlSugarContext _main;
    private readonly IDataProtector _protector;
    private readonly IOptions<PosReceiptOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PosReceiptService> _logger;

    public PosReceiptService(
        POSMSqlSugarContext posm,
        SqlSugarContext main,
        IDataProtectionProvider dataProtection,
        IOptions<PosReceiptOptions> options,
        TimeProvider clock,
        ILogger<PosReceiptService> logger)
    {
        _posm = posm;
        _main = main;
        _protector = dataProtection.CreateProtector(TokenProtectorPurpose);
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<PosReceiptResult<PosReceiptStaffViewDto>> GetStaffViewAsync(
        string orderGuid, IReadOnlyCollection<string>? allowedBranchCodes, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(orderGuid, allowedBranchCodes, cancellationToken);
        if (!loaded.Ok)
        {
            return PosReceiptResult<PosReceiptStaffViewDto>.Fail(loaded.Error);
        }

        var link = await FindActiveLinkAsync(loaded.Data!.OrderGuid, cancellationToken);
        var linkDto = link is null ? null : ToDto(link);
        var document = PosReceiptBuilder.Build(loaded.Data, linkDto?.Url, includeBankReceipt: true);
        return PosReceiptResult<PosReceiptStaffViewDto>.Success(new PosReceiptStaffViewDto(document, linkDto));
    }

    public async Task<PosReceiptResult<PosReceiptShareLinkDto>> GetOrCreateLinkAsync(
        string orderGuid, IReadOnlyCollection<string>? allowedBranchCodes, string? createdBy, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(orderGuid, allowedBranchCodes, cancellationToken);
        if (!loaded.Ok)
        {
            return PosReceiptResult<PosReceiptShareLinkDto>.Fail(loaded.Error);
        }

        var existing = await FindActiveLinkAsync(loaded.Data!.OrderGuid, cancellationToken);
        var existingDto = existing is null ? null : ToDto(existing);
        if (existingDto is not null)
        {
            return PosReceiptResult<PosReceiptShareLinkDto>.Success(existingDto);
        }

        var created = await InsertLinkAsync(loaded.Data, createdBy, cancellationToken);
        return PosReceiptResult<PosReceiptShareLinkDto>.Success(ToDto(created)!);
    }

    public async Task<PosReceiptResult<PosReceiptShareLinkDto>> RegenerateLinkAsync(
        string orderGuid, IReadOnlyCollection<string>? allowedBranchCodes, string? createdBy, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(orderGuid, allowedBranchCodes, cancellationToken);
        if (!loaded.Ok)
        {
            return PosReceiptResult<PosReceiptShareLinkDto>.Fail(loaded.Error);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var order = loaded.Data!.OrderGuid;
        // 撤销旧链接与新建链接放进同一个事务：不会出现「旧的已撤销、新的没建成」让客人手里的链接白白失效。
        PosReceiptShareLink created;
        await _main.Db.Ado.BeginTranAsync();
        try
        {
            await _main.Db.Updateable<PosReceiptShareLink>()
                .SetColumns(link => link.RevokedAtUtc == now)
                .Where(link => link.OrderGuid == order && link.RevokedAtUtc == null)
                .ExecuteCommandAsync(cancellationToken);
            created = await InsertLinkAsync(loaded.Data, createdBy, cancellationToken);
            await _main.Db.Ado.CommitTranAsync();
        }
        catch
        {
            await _main.Db.Ado.RollbackTranAsync();
            throw;
        }

        return PosReceiptResult<PosReceiptShareLinkDto>.Success(ToDto(created)!);
    }

    public async Task<PosReceiptResult<PosReceiptDocumentDto>> GetPublicDocumentAsync(
        string token, CancellationToken cancellationToken)
    {
        if (!PosReceiptShareTokens.TryNormalize(token, out var normalized))
        {
            return PosReceiptResult<PosReceiptDocumentDto>.Fail(PosReceiptError.NotFound);
        }

        var hash = PosReceiptShareTokens.Hash(normalized);
        var link = await _main.Db.Queryable<PosReceiptShareLink>()
            .Where(candidate => candidate.TokenHash == hash)
            .FirstAsync(cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (link is null || link.RevokedAtUtc is not null || link.ExpiresAtUtc <= now)
        {
            return PosReceiptResult<PosReceiptDocumentDto>.Fail(PosReceiptError.NotFound);
        }

        var loaded = await LoadAsync(link.OrderGuid, allowedBranchCodes: null, cancellationToken);
        if (!loaded.Ok)
        {
            // 订单后来被取消 / 改状态：链接同样当作不存在，不向外区分原因。
            return PosReceiptResult<PosReceiptDocumentDto>.Fail(PosReceiptError.NotFound);
        }

        await TryCountOpenAsync(link.Id, now, cancellationToken);
        var url = PosReceiptShareTokens.BuildUrl(_options.Value.ResolveBaseUrl(), normalized);
        return PosReceiptResult<PosReceiptDocumentDto>.Success(
            PosReceiptBuilder.Build(loaded.Data!, url, includeBankReceipt: false));
    }

    private async Task<PosReceiptShareLink> InsertLinkAsync(
        PosReceiptSource source, string? createdBy, CancellationToken cancellationToken)
    {
        var token = PosReceiptShareTokens.Generate();
        var now = _clock.GetUtcNow().UtcDateTime;
        var link = new PosReceiptShareLink
        {
            OrderGuid = source.OrderGuid,
            BranchCode = string.IsNullOrWhiteSpace(source.Store.StoreCode) ? null : source.Store.StoreCode,
            TokenHash = PosReceiptShareTokens.Hash(token),
            TokenProtected = _protector.Protect(token),
            CreatedAtUtc = now,
            CreatedBy = createdBy is { Length: > 100 } ? createdBy[..100] : createdBy,
            ExpiresAtUtc = now.AddDays(_options.Value.ResolveLifetimeDays()),
        };
        await _main.Db.Insertable(link).ExecuteCommandAsync(cancellationToken);
        return link;
    }

    private async Task<PosReceiptShareLink?> FindActiveLinkAsync(string orderGuid, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return await _main.Db.Queryable<PosReceiptShareLink>()
            .Where(link => link.OrderGuid == orderGuid && link.RevokedAtUtc == null && link.ExpiresAtUtc > now)
            .OrderByDescending(link => link.CreatedAtUtc)
            .FirstAsync(cancellationToken);
    }

    /// <summary>密文解不开（密钥环被换过等）时当作没有链接，员工重新生成即可，不让读小票整体失败。</summary>
    private PosReceiptShareLinkDto? ToDto(PosReceiptShareLink link)
    {
        try
        {
            var token = _protector.Unprotect(link.TokenProtected);
            return new PosReceiptShareLinkDto(
                PosReceiptShareTokens.BuildUrl(_options.Value.ResolveBaseUrl(), token),
                DateTime.SpecifyKind(link.ExpiresAtUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(link.CreatedAtUtc, DateTimeKind.Utc),
                link.OpenCount);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogWarning(ex, "POS receipt share link {LinkId} token could not be decrypted", link.Id);
            return null;
        }
    }

    /// <summary>打开次数只是统计：写失败只记日志，绝不影响客人看到小票。</summary>
    private async Task TryCountOpenAsync(string linkId, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            await _main.Db.Updateable<PosReceiptShareLink>()
                .SetColumns(link => link.OpenCount == link.OpenCount + 1)
                .SetColumns(link => link.LastOpenedAtUtc == now)
                .Where(link => link.Id == linkId)
                .ExecuteCommandAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "POS receipt share link {LinkId} open count update failed", linkId);
        }
    }

    private async Task<PosReceiptResult<PosReceiptSource>> LoadAsync(
        string orderGuid, IReadOnlyCollection<string>? allowedBranchCodes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orderGuid))
        {
            return PosReceiptResult<PosReceiptSource>.Fail(PosReceiptError.NotFound);
        }

        var key = orderGuid.Trim();
        var order = await _posm.SalesOrderDb.GetFirstAsync(o => o.OrderGuid == key);
        if (order is null)
        {
            return PosReceiptResult<PosReceiptSource>.Fail(PosReceiptError.NotFound);
        }

        // 与既有详情 / 发票接口同一口径：没有分配分店的账号视为不受限；否则只能看授权分店的订单。
        if (allowedBranchCodes is { Count: > 0 }
            && !string.IsNullOrEmpty(order.BranchCode)
            && !allowedBranchCodes.Contains(order.BranchCode))
        {
            return PosReceiptResult<PosReceiptSource>.Fail(PosReceiptError.Forbidden);
        }

        if (order.Status is null || !SupportedStatuses.Contains(order.Status.Value))
        {
            return PosReceiptResult<PosReceiptSource>.Fail(PosReceiptError.Unsupported);
        }

        var details = await _posm.SalesOrderDetailDb.GetListAsync(d => d.OrderGuid == order.OrderGuid);
        var payments = await _posm.PaymentDetailDb.GetListAsync(p => p.OrderGuid == order.OrderGuid);
        var bankTransactions = await _posm.BankTransactionDb.GetListAsync(b => b.OrderGuid == order.OrderGuid);
        var store = await LoadStoreProfileAsync(order.BranchCode, cancellationToken);

        var lines = details
            .OrderBy(d => d.CreatedTime ?? DateTime.MinValue)
            .Select(d => new PosReceiptLineSource(
                d.ProductName ?? string.Empty,
                // 收银机打印的是「扫码 / 查找用的编码」；POSM 明细没存，条码最接近，缺省退回商品编号。
                string.IsNullOrWhiteSpace(d.Barcode) ? d.ProductCode : d.Barcode!,
                d.Quantity ?? 0,
                d.ActualAmount ?? 0m,
                d.DiscountAmount ?? 0m))
            .ToList();

        var paymentSources = payments
            .OrderBy(p => p.CreatedTime ?? DateTime.MinValue)
            .Select(p =>
            {
                // 刷卡行带上该笔支付对应的卡摘要（类型 + 卡号末 4 位）。
                var bank = p.PaymentMethod == 2
                    ? bankTransactions.FirstOrDefault(b => !string.IsNullOrEmpty(p.PaymentGuid) && b.PaymentGuid == p.PaymentGuid)
                    : null;
                return new PosReceiptPaymentSource(
                    p.PaymentMethod,
                    p.Amount ?? 0m,
                    bank is null ? null : PosReceiptBuilder.BuildCardSummary(bank.CardType, bank.CardNumber));
            })
            .ToList();

        var bankTexts = bankTransactions
            .OrderBy(b => b.BankDateTime ?? DateTime.MinValue)
            .Select(b => b.ReceiptText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();

        return PosReceiptResult<PosReceiptSource>.Success(new PosReceiptSource(
            store,
            order.OrderGuid ?? key,
            order.OrderTime,
            order.CashierName ?? string.Empty,
            order.DeviceCode ?? string.Empty,
            order.TotalAmount ?? 0m,
            order.DiscountAmount ?? 0m,
            lines,
            paymentSources,
            bankTexts));
    }

    /// <summary>
    /// 门店小票资料：优先用收银端实际在打印的「最新下发快照」，这样在线小票与纸质小票的抬头 / 退货政策一致；
    /// 还没下发过的门店退回 Store 表当前值。
    /// </summary>
    private async Task<PosReceiptStoreProfile> LoadStoreProfileAsync(string? branchCode, CancellationToken cancellationToken)
    {
        var code = branchCode?.Trim() ?? string.Empty;
        if (code.Length == 0)
        {
            return new PosReceiptStoreProfile(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        try
        {
            var release = await _main.Db.Queryable<StoreReceiptProfileRelease>()
                .Where(r => r.StoreCode == code)
                .OrderByDescending(r => r.Version)
                .FirstAsync(cancellationToken);
            if (release is not null)
            {
                return new PosReceiptStoreProfile(
                    code,
                    release.BrandName ?? string.Empty,
                    release.StoreName,
                    release.Address ?? string.Empty,
                    release.Phone ?? string.Empty,
                    release.ABN ?? string.Empty,
                    release.ReturnPolicy ?? string.Empty);
            }

            var store = await _main.StoreDb.GetFirstAsync(s => s.StoreCode == code && !s.IsDeleted);
            if (store is not null)
            {
                return new PosReceiptStoreProfile(
                    code,
                    store.BrandName ?? string.Empty,
                    store.StoreName,
                    store.Address ?? string.Empty,
                    store.Phone ?? string.Empty,
                    store.ABN ?? string.Empty,
                    store.ReturnPolicy ?? string.Empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 门店资料只是抬头，查失败不能让整张小票打不开。
            _logger.LogWarning(ex, "Loading store receipt profile failed for {StoreCode}", code);
        }

        return new PosReceiptStoreProfile(code, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }
}
