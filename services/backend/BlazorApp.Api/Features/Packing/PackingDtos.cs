namespace BlazorApp.Api.Features.Packing;

/// <summary>打包接口统一结果：失败时带 HTTP 状态码与错误码，控制器原样映射。</summary>
public sealed class PackingResult<T>
{
    public bool Success { get; private init; }
    public int StatusCode { get; private init; } = 200;
    public string? ErrorCode { get; private init; }
    public string? Message { get; private init; }
    public T? Data { get; private init; }

    public static PackingResult<T> Ok(T data) => new() { Success = true, Data = data };

    public static PackingResult<T> Fail(int statusCode, string errorCode, string message, T? data = default) =>
        new()
        {
            Success = false,
            StatusCode = statusCode,
            ErrorCode = errorCode,
            Message = message,
            Data = data,
        };
}

/// <summary>账号能操作的一个所属地点：一家分店，或总仓。</summary>
public sealed record PackingOwner(string Code, string Name, bool IsWarehouse);

/// <summary>
/// 当前操作人。Owners 是账号能操作的全部地点（店员只有本店，店长是所管分店，管理员是全部分店加总仓）；
/// IsManager 为店长 / 仓库经理 / 管理员，才能重新开箱、重新开托、改存放位置名称；IsAdmin 才能改运输公司。
/// </summary>
public sealed record PackingOperator(
    string UserGuid,
    string Name,
    bool IsManager,
    bool IsAdmin,
    IReadOnlyList<PackingOwner> Owners
)
{
    public bool Has(string? ownerCode) =>
        !string.IsNullOrWhiteSpace(ownerCode)
        && Owners.Any(owner => string.Equals(owner.Code, ownerCode.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class PackingErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string NotAllowed = "PACKING_NOT_ALLOWED";
    public const string OwnerRequired = "OWNER_REQUIRED";
    public const string OwnerNotAllowed = "OWNER_NOT_ALLOWED";
    public const string CartonNotFound = "CARTON_NOT_FOUND";
    public const string PalletNotFound = "PALLET_NOT_FOUND";
    public const string CodeNotFound = "CODE_NOT_FOUND";
    public const string CodeAmbiguous = "CODE_AMBIGUOUS";
    public const string CartonNotPacking = "CARTON_NOT_PACKING";
    public const string CartonNotSealed = "CARTON_NOT_SEALED";
    public const string CartonEmpty = "CARTON_EMPTY";
    public const string CartonOnPallet = "CARTON_ON_PALLET";
    public const string CartonOnOtherPallet = "CARTON_ON_OTHER_PALLET";
    public const string CartonInTransit = "CARTON_IN_TRANSIT";
    public const string DestinationMismatch = "DESTINATION_MISMATCH";
    public const string DestinationInvalid = "DESTINATION_INVALID";
    public const string PalletNotBuilding = "PALLET_NOT_BUILDING";
    public const string PalletNotSealed = "PALLET_NOT_SEALED";
    public const string PalletEmpty = "PALLET_EMPTY";
    public const string PalletNotEmpty = "PALLET_NOT_EMPTY";
    public const string PalletInTransit = "PALLET_IN_TRANSIT";
    public const string OrderNotFound = "ORDER_NOT_FOUND";
    public const string LocationNotFound = "LOCATION_NOT_FOUND";
    public const string LocationNameTaken = "LOCATION_NAME_TAKEN";
    public const string CarrierNotFound = "CARRIER_NOT_FOUND";
    public const string CarrierNameTaken = "CARRIER_NAME_TAKEN";
    public const string QuantityOutOfRange = "QUANTITY_OUT_OF_RANGE";
    public const string NotShippable = "NOT_SHIPPABLE";
    public const string NotInTransit = "NOT_IN_TRANSIT";
    public const string NotForYou = "NOT_FOR_YOU";
    public const string CountMismatch = "COUNT_MISMATCH";
}

// ---- 请求 ----

/// <summary>
/// 去向：0 未指定、1 本地存放、2 其他分店（DestinationCode 是分店编码）、3 总仓。
/// CarrierGuid 是「怎么送」：为空表示自己送 / 对方来取。
/// </summary>
public class PackingDestinationRequestDto
{
    public int DestinationType { get; set; }
    public string? DestinationCode { get; set; }
    public string? CarrierGuid { get; set; }
}

public sealed class PackingCreateCartonRequestDto : PackingDestinationRequestDto
{
    /// <summary>在哪个地点建箱；账号只有一个地点时可以不传。</summary>
    public string? OwnerCode { get; set; }

    /// <summary>可选关联的分店订货单（仓库按订货单装箱用）；分店一期不用。</summary>
    public string? OrderGuid { get; set; }
}

public sealed class PackingCreatePalletRequestDto : PackingDestinationRequestDto
{
    public string? OwnerCode { get; set; }
}

public sealed class PackingScanRequestDto
{
    /// <summary>扫到的码：条码、货号、商品编码、套装码或本店多码。</summary>
    public string? Code { get; set; }

    /// <summary>一码多品时由界面选定后回传，跳过按码识别。</summary>
    public string? ProductCode { get; set; }

    /// <summary>本次增加的数量（件），默认 1；按中包 / 箱规加时由客户端传相应件数。</summary>
    public int? Quantity { get; set; }
}

public sealed class PackingSetQuantityRequestDto
{
    /// <summary>该商品在本箱的总数量；0 表示从箱里移除。</summary>
    public int Quantity { get; set; }
}

public sealed class PackingCartonHeaderRequestDto : PackingDestinationRequestDto
{
    public string? OrderGuid { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Remark { get; set; }
}

public sealed class PackingSealRequestDto
{
    public decimal? WeightKg { get; set; }
}

public sealed class PackingPalletHeaderRequestDto : PackingDestinationRequestDto
{
    public string? ConsignmentNo { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Remark { get; set; }
}

public sealed class PackingAddCartonRequestDto
{
    /// <summary>箱号（扫箱标签）或箱 GUID。</summary>
    public string? Code { get; set; }

    /// <summary>箱的去向与托盘不一致时仍然加入；只有店长 / 仓库经理 / 管理员可用。</summary>
    public bool AllowDestinationMismatch { get; set; }
}

public sealed class PackingLocationRequestDto
{
    /// <summary>存放位置名称或位置条码；为空表示清除。</summary>
    public string? LocationCode { get; set; }
}

public sealed class PackingCarrierSaveRequestDto
{
    public string? Name { get; set; }
    public string? Remark { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class PackingLocationSaveRequestDto
{
    /// <summary>新建时必填：这个位置属于哪家分店。</summary>
    public string? OwnerCode { get; set; }

    public string? Name { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class PackingReceiveCartonRequestDto
{
    /// <summary>签收后放到哪个存放位置（可空）。</summary>
    public string? LocationCode { get; set; }
}

public sealed class PackingReceivePalletRequestDto
{
    /// <summary>
    /// 整托签收时必填：收货人数过的实到箱数。必须等于托盘上还在路上的箱数，否则 409 COUNT_MISMATCH，
    /// 改为用 CartonGuids 逐箱签收。
    /// </summary>
    public int? ExpectedCartonCount { get; set; }

    /// <summary>只签收其中这几只箱（逐箱核对）；为空表示整托签收。</summary>
    public List<string>? CartonGuids { get; set; }

    public string? LocationCode { get; set; }

    /// <summary>托盘标签丢了、是扫箱标签找到托盘时，传扫到的箱号，写进操作记录方便追溯。</summary>
    public string? ViaCartonNo { get; set; }
}

// ---- 返回 ----

public sealed class PackingOwnerDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsWarehouse { get; set; }
}

public sealed class PackingScopeDto
{
    public List<PackingOwnerDto> Owners { get; set; } = new();
    public bool IsManager { get; set; }
    public bool IsAdmin { get; set; }
}

public sealed class PackingDestinationDto
{
    public int DestinationType { get; set; }

    /// <summary>收货地点编码（分店编码或总仓编码）；本地存放 / 未指定时为空。</summary>
    public string? Code { get; set; }

    public string? Name { get; set; }
    public string? CarrierGuid { get; set; }
    public string? CarrierName { get; set; }
}

public class PackingCartonSummaryDto
{
    public string CartonGuid { get; set; } = string.Empty;
    public string CartonNo { get; set; } = string.Empty;
    public int Status { get; set; }
    public string OwnerCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string OriginCode { get; set; } = string.Empty;

    /// <summary>标签上的 FROM。</summary>
    public string OriginName { get; set; } = string.Empty;

    public PackingDestinationDto Destination { get; set; } = new();
    public int SkuCount { get; set; }
    public int PieceCount { get; set; }
    public string? PalletGuid { get; set; }
    public string? PalletNo { get; set; }
    public string? LocationCode { get; set; }
    public decimal? WeightKg { get; set; }
    public string? OrderGuid { get; set; }
    public string? OrderNo { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string? SealedByName { get; set; }
    public DateTime? SealedAtUtc { get; set; }
    public string? DispatchedByName { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public string? ReceivedByName { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class PackingCartonItemDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
    public int Quantity { get; set; }
    public int? MiddlePackageQuantity { get; set; }
    public int? PackingQuantity { get; set; }

    /// <summary>关联订货单时：该商品在订货单上的配货数（未配货时取订货数）；商品不在订货单上为空。</summary>
    public decimal? OrderQuantity { get; set; }

    /// <summary>关联订货单时：同一订货单的其他箱里已装的数量。</summary>
    public int PackedInOtherCartons { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class PackingCartonDetailDto : PackingCartonSummaryDto
{
    public string? Remark { get; set; }

    /// <summary>最近改动的在前，装箱界面把刚扫入的商品置顶。</summary>
    public List<PackingCartonItemDto> Items { get; set; } = new();

    /// <summary>本次扫码 / 改数量影响到的商品，界面据此高亮。</summary>
    public string? ChangedProductCode { get; set; }

    /// <summary>一码多品（CODE_AMBIGUOUS）时的候选商品，界面选定后带 ProductCode 重新提交。</summary>
    public List<PackingProductCandidateDto>? Candidates { get; set; }
}

public sealed class PackingCartonListDto
{
    public List<PackingCartonSummaryDto> Items { get; set; } = new();

    /// <summary>装箱中的箱数（本地点）。</summary>
    public int PackingCount { get; set; }

    /// <summary>本地存放：已封箱、所属地点是本地点、没有在路上的箱数。</summary>
    public int StoredCount { get; set; }

    /// <summary>已发出：本地点发出、还在路上或对方已签收的箱数。</summary>
    public int OutgoingCount { get; set; }

    /// <summary>待签收：发给本地点、还没签收的托盘和散箱批数。</summary>
    public int IncomingCount { get; set; }
}

public class PackingPalletSummaryDto
{
    public string PalletGuid { get; set; } = string.Empty;
    public string PalletNo { get; set; } = string.Empty;
    public int Status { get; set; }
    public string OwnerCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string OriginCode { get; set; } = string.Empty;
    public string OriginName { get; set; } = string.Empty;
    public PackingDestinationDto Destination { get; set; } = new();
    public string? ConsignmentNo { get; set; }
    public string? LocationCode { get; set; }
    public decimal? WeightKg { get; set; }
    public int CartonCount { get; set; }

    /// <summary>托盘上还没被签收的箱数；托盘在路上时用来显示「已核对 x / n」。</summary>
    public int InTransitCartonCount { get; set; }

    public int SkuCount { get; set; }
    public int PieceCount { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string? SealedByName { get; set; }
    public DateTime? SealedAtUtc { get; set; }
    public string? DispatchedByName { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public string? ReceivedByName { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class PackingPalletProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    public int Quantity { get; set; }
    public int CartonCount { get; set; }
}

public sealed class PackingPalletDetailDto : PackingPalletSummaryDto
{
    public string? Remark { get; set; }
    public List<PackingCartonSummaryDto> Cartons { get; set; } = new();

    /// <summary>按商品汇总（同一货号跨箱合计）。</summary>
    public List<PackingPalletProductDto> Products { get; set; } = new();

    /// <summary>加入托盘被拦下时带回的那只箱，界面用来说明原因。</summary>
    public PackingCartonSummaryDto? BlockedCarton { get; set; }

    /// <summary>签收整托数量对不上（COUNT_MISMATCH）时带回托盘上实际还在路上的箱数。</summary>
    public int? ActualInTransitCartonCount { get; set; }
}

public sealed class PackingPalletListDto
{
    public List<PackingPalletSummaryDto> Items { get; set; } = new();
    public int BuildingCount { get; set; }
    public int StoredCount { get; set; }
}

public sealed class PackingResolveDto
{
    /// <summary>carton 或 pallet。</summary>
    public string Kind { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string No { get; set; } = string.Empty;

    /// <summary>扫到箱号时：箱所在托盘（可空）。</summary>
    public string? PalletGuid { get; set; }
}

public sealed class PackingProductCandidateDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
}

public sealed class PackingCarrierDto
{
    public string CarrierGuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Remark { get; set; }
    public bool IsActive { get; set; }
}

public sealed class PackingLocationDto
{
    public string LocationGuid { get; set; } = string.Empty;
    public string OwnerCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>位置标签上的条码；总仓货位取货位条码。</summary>
    public string Barcode { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}

public sealed class PackingOrderOptionDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public int FlowStatus { get; set; }
    public DateTime? OrderDate { get; set; }
}

public sealed class PackingEventDto
{
    public int Action { get; set; }
    public string? Detail { get; set; }
    public string OperatorName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>待签收 / 最近签收列表里的一行：一个托盘，或一只散箱。</summary>
public sealed class PackingIncomingItemDto
{
    /// <summary>pallet 或 carton。</summary>
    public string Kind { get; set; } = string.Empty;

    public string Guid { get; set; } = string.Empty;
    public string No { get; set; } = string.Empty;
    public string OriginCode { get; set; } = string.Empty;
    public string OriginName { get; set; } = string.Empty;
    public string? CarrierName { get; set; }
    public string? ConsignmentNo { get; set; }
    public int CartonCount { get; set; }

    /// <summary>托盘上已经签收的箱数；散箱为 0。</summary>
    public int ReceivedCartonCount { get; set; }

    public int PieceCount { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public string? DispatchedByName { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public string? ReceivedByName { get; set; }
}

public sealed class PackingIncomingDto
{
    public List<PackingIncomingItemDto> Pending { get; set; } = new();
    public List<PackingIncomingItemDto> Recent { get; set; } = new();
}

/// <summary>扫码签收：扫托盘标签或箱标签后，界面据此展示要核对的内容。</summary>
public sealed class PackingReceiveLookupDto
{
    /// <summary>pallet 或 carton；扫到托盘上的箱时也是 pallet（整托签收）。</summary>
    public string Kind { get; set; } = string.Empty;

    public PackingPalletDetailDto? Pallet { get; set; }
    public PackingCartonDetailDto? Carton { get; set; }

    /// <summary>通过箱标签找到托盘（托盘标签丢了）时，扫到的那只箱。</summary>
    public string? ScannedCartonGuid { get; set; }

    public string? ScannedCartonNo { get; set; }
    public bool ViaCartonLabel { get; set; }

    public int InTransitCartonCount { get; set; }
    public bool CanReceive { get; set; }

    /// <summary>不能签收的原因：NOT_IN_TRANSIT / NOT_FOR_YOU。</summary>
    public string? BlockReason { get; set; }
}

public sealed class PackingLocateRowDto
{
    public string CartonGuid { get; set; } = string.Empty;
    public string CartonNo { get; set; } = string.Empty;
    public int CartonStatus { get; set; }
    public string? PalletGuid { get; set; }
    public string? PalletNo { get; set; }

    /// <summary>箱在托盘上时取托盘的位置，否则取箱自己的存放位置。</summary>
    public string? LocationCode { get; set; }

    public string OwnerCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string OriginName { get; set; } = string.Empty;
    public PackingDestinationDto Destination { get; set; } = new();
    public int Quantity { get; set; }
    public string? OrderNo { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// stored 在本地点存放；transit 在路上（本地点发出或发给本地点）；
    /// elsewhere 本地点发出、已被对方签收；other 其他地点的箱（只有店长 / 管理员查全部分店时出现）。
    /// </summary>
    public string Group { get; set; } = string.Empty;
}

public sealed class PackingLocateDto
{
    public PackingProductCandidateDto Product { get; set; } = new();
    public int? MiddlePackageQuantity { get; set; }
    public int? PackingQuantity { get; set; }

    /// <summary>仅总仓有意义：现有仓库库存，用来对照；打包不改它。</summary>
    public int? StockQuantity { get; set; }

    public int PackedQuantity { get; set; }
    public int CartonCount { get; set; }
    public int PalletCount { get; set; }
    public List<PackingLocateRowDto> Rows { get; set; } = new();
}
