using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace BlazorApp.Api.Features.WarehousePicking;

internal enum WarehousePickerTicketStatus
{
    Valid,
    Invalid,
    Expired,
}

internal sealed record WarehousePickerTicketIdentity(
    string UserGuid,
    string Name,
    bool CanOverwriteMinOrderQuantity
);

internal readonly record struct WarehousePickerTicketValidation(
    WarehousePickerTicketStatus Status,
    WarehousePickerTicketIdentity? Identity
);

/// <summary>
/// 扫员工码后签发的拣货人凭证：纯设备会话没有个人账号，拣货记录要记到扫码确认的员工名下。
/// 凭证绑定签发终端（设备硬件码或账号 GUID），换一台设备使用视为无效，避免被截获后重放。
/// 只存身份与有效期，不存员工码本身。
/// </summary>
public sealed class WarehousePickerTicketProtector
{
    internal const string Purpose = "HB.WarehousePicking.PickerTicket.v1";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private const int PayloadVersion = 1;
    private const int MaxTicketLength = 4096;

    private readonly IDataProtector _protector;

    public WarehousePickerTicketProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    internal (string Ticket, DateTime ExpiresAtUtc) Issue(
        WarehousePickerTicketIdentity identity,
        string terminalKey,
        DateTime nowUtc
    )
    {
        var issuedAt = NormalizeUtc(nowUtc);
        var payload = new TicketPayload
        {
            Version = PayloadVersion,
            UserGuid = identity.UserGuid,
            Name = identity.Name,
            CanOverwriteMinOrderQuantity = identity.CanOverwriteMinOrderQuantity,
            TerminalHash = HashTerminal(terminalKey),
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = issuedAt.Add(Lifetime),
        };
        // IDataProtector.Protect(string) 输出 base64url，可直接放进请求头。
        return (_protector.Protect(JsonSerializer.Serialize(payload)), payload.ExpiresAtUtc);
    }

    internal WarehousePickerTicketValidation Validate(
        string? ticket,
        string terminalKey,
        DateTime nowUtc
    )
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > MaxTicketLength)
        {
            return new(WarehousePickerTicketStatus.Invalid, null);
        }

        TicketPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TicketPayload>(_protector.Unprotect(ticket.Trim()));
        }
        catch (Exception exception)
            when (exception is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            return new(WarehousePickerTicketStatus.Invalid, null);
        }

        if (payload == null
            || payload.Version != PayloadVersion
            || string.IsNullOrWhiteSpace(payload.UserGuid)
            || !string.Equals(payload.TerminalHash, HashTerminal(terminalKey), StringComparison.Ordinal))
        {
            return new(WarehousePickerTicketStatus.Invalid, null);
        }

        var now = NormalizeUtc(nowUtc);
        var issuedAt = NormalizeUtc(payload.IssuedAtUtc);
        var expiresAt = NormalizeUtc(payload.ExpiresAtUtc);
        // 有效期必须正好是签发时长，防止旧版本或篡改后的长期凭证。
        if (expiresAt != issuedAt.Add(Lifetime) || now < issuedAt.AddMinutes(-5))
        {
            return new(WarehousePickerTicketStatus.Invalid, null);
        }

        if (now >= expiresAt)
        {
            return new(WarehousePickerTicketStatus.Expired, null);
        }

        return new(
            WarehousePickerTicketStatus.Valid,
            new WarehousePickerTicketIdentity(
                payload.UserGuid,
                payload.Name,
                payload.CanOverwriteMinOrderQuantity
            )
        );
    }

    private static string HashTerminal(string terminalKey)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(terminalKey.Trim()));
        return Convert.ToHexString(bytes);
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private sealed class TicketPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("uid")]
        public string UserGuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mgr")]
        public bool CanOverwriteMinOrderQuantity { get; set; }

        [JsonPropertyName("term")]
        public string TerminalHash { get; set; } = string.Empty;

        [JsonPropertyName("iat")]
        public DateTime IssuedAtUtc { get; set; }

        [JsonPropertyName("exp")]
        public DateTime ExpiresAtUtc { get; set; }
    }
}
