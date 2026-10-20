namespace Hbpos.Contracts.Square;

/// <summary>收银机向 Hbpos API 领取直连 Square 的凭据；是否允许由总部按门店开关控制（默认关闭）。</summary>
public sealed record SquareDirectCredentialRequest(string Environment);

/// <summary>
/// 直连凭据。AccessToken 只进收银机本机的加密存储，绝不写日志。
/// CredentialVersion：令牌最近一次更新的 Unix 毫秒时间戳，收银机据此判断本机副本是否过期（总部换令牌后版本变化）。
/// ValidUntil：本机副本最迟到此时刻必须重新领取——这样总部关掉开关后，收银机最迟一个周期内就会删掉令牌。
/// </summary>
public sealed record SquareDirectCredentialResponse(
    string Environment,
    string AccessToken,
    long CredentialVersion,
    DateTimeOffset ValidUntil);

/// <summary>收银机直连创建结账后，向 Hbpos API 登记这笔结账属于哪台 POS（服务端向 Square 核实后才登记）。</summary>
public sealed record SquareDirectOriginRequest(string Environment);

public sealed record SquareDirectOriginResponse(
    string CheckoutId,
    string Environment,
    string StoreCode,
    string DeviceCode);
