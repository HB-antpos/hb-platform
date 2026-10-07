namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 按券号向服务端补查这张券的真实到期时刻，供券面打印使用。
/// </summary>
public interface IVoucherExpiryLookup
{
    /// <summary>
    /// 返回券的到期时刻；券不存在、已用完/已过期（服务端只返回可用券）、离线、超时或任何失败都返回 null，
    /// 绝不抛出业务异常——券面缺日期只是少印一行，不能阻断出票。
    /// 调用方取消（<paramref name="cancellationToken"/>）时才会抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<DateTimeOffset?> FindExpiryAsync(string storeCode, string voucherCode, CancellationToken cancellationToken);
}

/// <summary>
/// 复用现有的券查询接口（GET api/v1/vouchers/{code}?storeCode=）读取 ExpiredAt。
/// 旧券（12 个月）与新券（90 天且取整到当天结束）规则不同，所以到期日必须取自服务端那张券本身，客户端不反推。
/// </summary>
public sealed class VoucherExpiryLookup(IVoucherApiClient voucherApiClient) : IVoucherExpiryLookup
{
    /// <summary>补查上限。出票是收银现场的同步动作，离线或慢网时宁可少印日期也不能让顾客干等。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<DateTimeOffset?> FindExpiryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode) || string.IsNullOrWhiteSpace(voucherCode))
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Timeout);
        try
        {
            var response = await voucherApiClient.QueryAsync(
                storeCode.Trim(),
                voucherCode.Trim(),
                timeoutSource.Token);
            return response.Found ? response.Voucher?.ExpiredAt : null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 自己的超时：当作查不到，不阻断出票（券面少印到期日）。
            ConsoleLog.WriteWarning(
                "Receipt",
                $"voucher expiry lookup timed out reason=timeout timeoutMs={(int)Timeout.TotalMilliseconds} " +
                $"voucherTail={VoucherCodeLogFormat.Tail(voucherCode)}",
                new ApplicationLogContext(Properties: new Dictionary<string, object?>
                {
                    ["storeCode"] = storeCode,
                    ["reason"] = "timeout"
                }),
                ex);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 券号本身可以直接兑付，只记后 4 位。
            ConsoleLog.WriteWarning(
                "Receipt",
                $"voucher expiry lookup failed voucherTail={VoucherCodeLogFormat.Tail(voucherCode)} error={ex.GetType().Name}",
                new ApplicationLogContext(
                    StatusCode: ex is CatalogApiException { StatusCode: { } status } ? (int)status : null,
                    Properties: new Dictionary<string, object?>
                    {
                        ["storeCode"] = storeCode,
                        ["errorCode"] = (ex as CatalogApiException)?.ErrorCode
                    }),
                ex);
            return null;
        }
    }
}
