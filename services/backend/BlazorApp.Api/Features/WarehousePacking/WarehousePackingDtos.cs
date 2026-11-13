namespace BlazorApp.Api.Features.WarehousePacking;

/// <summary>打包接口统一结果：失败时带 HTTP 状态码与错误码，控制器原样映射。</summary>
public sealed class WarehousePackingResult<T>
{
    public bool Success { get; private init; }
    public int StatusCode { get; private init; } = 200;
    public string? ErrorCode { get; private init; }
    public string? Message { get; private init; }
    public T? Data { get; private init; }

    public static WarehousePackingResult<T> Ok(T data) => new() { Success = true, Data = data };

    public static WarehousePackingResult<T> Fail(int statusCode, string errorCode, string message, T? data = default) =>
        new()
        {
            Success = false,
            StatusCode = statusCode,
            ErrorCode = errorCode,
            Message = message,
            Data = data,
        };
}

/// <summary>当前操作人；IsManager 为仓库经理 / 管理员，可做「去向不一致仍然加入」等放行操作。</summary>
public sealed record WarehousePackingOperator(string UserGuid, string Name, bool IsManager);

public static class WarehousePackingErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string NotAllowed = "PACKING_NOT_ALLOWED";
    public const string CartonNotFound = "CARTON_NOT_FOUND";
    public const string PalletNotFound = "PALLET_NOT_FOUND";
    public const string CodeNotFound = "CODE_NOT_FOUND";
    public const string CodeAmbiguous = "CODE_AMBIGUOUS";
    public const string CartonNotPacking = "CARTON_NOT_PACKING";
    public const string CartonNotSealed = "CARTON_NOT_SEALED";
    public const string CartonEmpty = "CARTON_EMPTY";
    public const string CartonOnPallet = "CARTON_ON_PALLET";
    public const string CartonOnOtherPallet = "CARTON_ON_OTHER_PALLET";
    public const string DestinationMismatch = "DESTINATION_MISMATCH";
    public const string DestinationInvalid = "DESTINATION_INVALID";
    public const string PalletNotBuilding = "PALLET_NOT_BUILDING";
    public const string PalletNotSealed = "PALLET_NOT_SEALED";
    public const string PalletEmpty = "PALLET_EMPTY";
    public const string PalletNotEmpty = "PALLET_NOT_EMPTY";
    public const string OrderNotFound = "ORDER_NOT_FOUND";
    public const string LocationNotFound = "LOCATION_NOT_FOUND";
    public const string CarrierNotFound = "CARRIER_NOT_FOUND";
    public const string CarrierNameTaken = "CARRIER_NAME_TAKEN";
    public const string QuantityOutOfRange = "QUANTITY_OUT_OF_RANGE";
}

// ---- 请求 ----

/// <summary>去向：0 未指定、1 分店、2 运输公司（此时 StoreCode 是最终收货分店，可空）。</summary>
public class WarehousePackingDestinationRequestDto
{
    public int DestinationType { get; set; }
    public string? DestinationStoreCode { get; set; }
    public string? CarrierGuid { get; set; }
}

public sealed class WarehousePackingCreateCartonRequestDto : WarehousePackingDestinationRequestDto
{
    /// <summary>可选关联的分店订货单；未指定去向时默认取订货单的分店。</summary>
    public string? OrderGuid { get; set; }
}

public sealed class WarehousePackingScanRequestDto
{
    /// <summary>扫到的码：条码、货号、商品编码或套装码。</summary>
    public string? Code { get; set; }

    /// <summary>一码多品时由界面选定后回传，跳过按码识别。</summary>
    public string? ProductCode { get; set; }

    /// <summary>本次增加的数量（件），默认 1；按中包 / 箱规加时由客户端传相应件数。</summary>
    public int? Quantity { get; set; }
}

public sealed class WarehousePackingSetQuantityRequestDto
{
    /// <summary>该商品在本箱的总数量；0 表示从箱里移除。</summary>
    public int Quantity { get; set; }
}

public sealed class WarehousePackingCartonHeaderRequestDto : WarehousePackingDestinationRequestDto
{
    public string? OrderGuid { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Remark { get; set; }
}

public sealed class WarehousePackingSealRequestDto
{
    public decimal? WeightKg { get; set; }
}

public sealed class WarehousePackingPalletHeaderRequestDto : WarehousePackingDestinationRequestDto
{
    public string? ConsignmentNo { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Remark { get; set; }
}

public sealed class WarehousePackingAddCartonRequestDto
{
    /// <summary>箱号（扫箱标签）或箱 GUID。</summary>
    public string? Code { get; set; }

    /// <summary>箱的去向与托盘不一致时仍然加入；只有仓库经理 / 管理员可用。</summary>
    public bool AllowDestinationMismatch { get; set; }
}

public sealed class WarehousePackingLocationRequestDto
{
    /// <summary>货位编码或货位条码；为空表示清除暂存货位。</summary>
    public string? LocationCode { get; set; }
}

public sealed class WarehousePackingCarrierSaveRequestDto
{
    public string? Name { get; set; }
    public string? Remark { get; set; }
    public bool IsActive { get; set; } = true;
}

// ---- 返回 ----

public sealed class WarehousePackingDestinationDto
{
    public int DestinationType { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public string? CarrierGuid { get; set; }
    public string? CarrierName { get; set; }
}

public class WarehousePackingCartonSummaryDto
{
    public string CartonGuid { get; set; } = string.Empty;
    public string CartonNo { get; set; } = string.Empty;
    public int Status { get; set; }
    public string OriginName { get; set; } = string.Empty;
    public WarehousePackingDestinationDto Destination { get; set; } = new();
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
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WarehousePackingCartonItemDto
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

public sealed class WarehousePackingCartonDetailDto : WarehousePackingCartonSummaryDto
{
    public string? Remark { get; set; }

    /// <summary>最近改动的在前，装箱界面把刚扫入的商品置顶。</summary>
    public List<WarehousePackingCartonItemDto> Items { get; set; } = new();

    /// <summary>本次扫码 / 改数量影响到的商品，界面据此高亮。</summary>
    public string? ChangedProductCode { get; set; }

    /// <summary>一码多品（CODE_AMBIGUOUS）时的候选商品，界面选定后带 ProductCode 重新提交。</summary>
    public List<WarehousePackingProductCandidateDto>? Candidates { get; set; }
}

public sealed class WarehousePackingCartonListDto
{
    public List<WarehousePackingCartonSummaryDto> Items { get; set; } = new();
    public int PackingCount { get; set; }
    public int SealedCount { get; set; }

    /// <summary>已封箱但既没上托盘的散箱数。</summary>
    public int LooseSealedCount { get; set; }
}

public class WarehousePackingPalletSummaryDto
{
    public string PalletGuid { get; set; } = string.Empty;
    public string PalletNo { get; set; } = string.Empty;
    public int Status { get; set; }
    public string OriginName { get; set; } = string.Empty;
    public WarehousePackingDestinationDto Destination { get; set; } = new();
    public string? ConsignmentNo { get; set; }
    public string? LocationCode { get; set; }
    public decimal? WeightKg { get; set; }
    public int CartonCount { get; set; }
    public int SkuCount { get; set; }
    public int PieceCount { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string? SealedByName { get; set; }
    public DateTime? SealedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WarehousePackingPalletProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    public int Quantity { get; set; }
    public int CartonCount { get; set; }
}

public sealed class WarehousePackingPalletDetailDto : WarehousePackingPalletSummaryDto
{
    public string? Remark { get; set; }
    public List<WarehousePackingCartonSummaryDto> Cartons { get; set; } = new();

    /// <summary>按商品汇总（同一货号跨箱合计）。</summary>
    public List<WarehousePackingPalletProductDto> Products { get; set; } = new();

    /// <summary>加入托盘被拦下时带回的那只箱，界面用来说明原因。</summary>
    public WarehousePackingCartonSummaryDto? BlockedCarton { get; set; }
}

public sealed class WarehousePackingPalletListDto
{
    public List<WarehousePackingPalletSummaryDto> Items { get; set; } = new();
    public int BuildingCount { get; set; }
    public int SealedCount { get; set; }
}

public sealed class WarehousePackingResolveDto
{
    /// <summary>carton 或 pallet。</summary>
    public string Kind { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string No { get; set; } = string.Empty;
}

public sealed class WarehousePackingProductCandidateDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
}

public sealed class WarehousePackingCarrierDto
{
    public string CarrierGuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Remark { get; set; }
    public bool IsActive { get; set; }
}

public sealed class WarehousePackingOrderOptionDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public int FlowStatus { get; set; }
    public DateTime? OrderDate { get; set; }
}

public sealed class WarehousePackingEventDto
{
    public int Action { get; set; }
    public string? Detail { get; set; }
    public string OperatorName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class WarehousePackingLocateRowDto
{
    public string CartonGuid { get; set; } = string.Empty;
    public string CartonNo { get; set; } = string.Empty;
    public int CartonStatus { get; set; }
    public string? PalletGuid { get; set; }
    public string? PalletNo { get; set; }

    /// <summary>箱在托盘上时取托盘的货位，否则取箱自己的暂存货位。</summary>
    public string? LocationCode { get; set; }
    public string OriginName { get; set; } = string.Empty;
    public WarehousePackingDestinationDto Destination { get; set; } = new();
    public int Quantity { get; set; }
    public string? OrderNo { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WarehousePackingLocateDto
{
    public WarehousePackingProductCandidateDto Product { get; set; } = new();
    public int? MiddlePackageQuantity { get; set; }
    public int? PackingQuantity { get; set; }

    /// <summary>现有仓库库存，仅作对照；打包不改它。</summary>
    public int? StockQuantity { get; set; }
    public int PackedQuantity { get; set; }
    public int CartonCount { get; set; }
    public int PalletCount { get; set; }
    public List<WarehousePackingLocateRowDto> Rows { get; set; } = new();
}
