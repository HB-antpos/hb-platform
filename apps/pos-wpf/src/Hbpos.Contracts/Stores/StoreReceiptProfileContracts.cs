namespace Hbpos.Contracts.Stores;

/// <summary>
/// 当前设备门店的小票资料。仅 Address 与 ReturnPolicy 允许 CR/LF/TAB；
/// 其余字段（StoreCode/StoreName/BrandName/Phone/Abn）拒绝任何控制字符
/// （含 DEL）。所有字段均拒绝其他不可打印控制字符；空字符串与 null 均为有效值。
/// <para>
/// Version/PublishedAt 为总部「下发」快照的版本信息：Version=0 表示从未下发（内容取自门店当前值），
/// Version&gt;=1 表示内容是该版本的下发快照。两个成员放在末尾并带默认值，旧调用点与旧服务端
/// （JSON 里没有 version）都按「从未下发」处理。
/// </para>
/// <para>
/// VoucherTerms / InstallmentTerms 是退款代金券与进行中分期小票底部「使用说明 / 分期条款」的正文（一行一条，
/// 标题 VOUCHER TERMS / INSTALLMENT TERMS 由收银端固定印）；null 或空白＝收银端按内置默认文案打印。
/// 与 ReturnPolicy 一样允许 CR/LF/TAB、拒绝其他控制字符，长度上限各 600；放在末尾并带默认值，
/// 旧服务端（JSON 里没有这两个字段）与旧客户端（忽略未知字段）互相兼容。
/// </para>
/// </summary>
public sealed record StoreReceiptProfileDto(
    string StoreCode,
    string StoreName,
    string? BrandName,
    string? Address,
    string? Phone,
    string? Abn,
    string? ReturnPolicy,
    int Version = 0,
    DateTimeOffset? PublishedAt = null,
    string? VoucherTerms = null,
    string? InstallmentTerms = null);

/// <summary>
/// 收银端按版本号轮询的同步结果：Changed=false 时 Profile 为 null（本机已是最新或从未下发），
/// Changed=true 时 Profile 为最新下发快照。
/// </summary>
public sealed record StoreReceiptProfileSyncDto(
    bool Changed,
    int Version,
    StoreReceiptProfileDto? Profile);

/// <summary>收银端写入本机后的回执：告知服务端本设备已应用到哪个版本。</summary>
public sealed record StoreReceiptProfileAckRequest(int Version);

/// <summary>回执写入后服务端记录的该设备已应用版本（单调不降，故可能大于请求版本）。</summary>
public sealed record StoreReceiptProfileAckResultDto(int AppliedVersion);
