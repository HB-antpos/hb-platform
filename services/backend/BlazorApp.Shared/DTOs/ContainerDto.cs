using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 货柜主表DTO
    /// </summary>
    public class ContainerMainDto
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        public int ID { get; set; }

        /// <summary>
        /// 全局唯一标识
        /// </summary>
        public string? HGUID { get; set; }

        /// <summary>
        /// 货柜编号
        /// </summary>
        public string? 货柜编号 { get; set; }

        /// <summary>
        /// 装柜日期
        /// </summary>
        public DateTime? 装柜日期 { get; set; }

        /// <summary>
        /// 预计到岸日期
        /// </summary>
        public DateTime? 预计到岸日期 { get; set; }

        /// <summary>
        /// 实际到货日期
        /// </summary>
        public DateTime? 实际到货日期 { get; set; }

        /// <summary>
        /// 合计件数
        /// </summary>
        public decimal? 合计件数 { get; set; }

        /// <summary>
        /// 合计数量
        /// </summary>
        public decimal? 合计数量 { get; set; }

        /// <summary>
        /// 合计金额
        /// </summary>
        public decimal? 合计金额 { get; set; }

        /// <summary>
        /// 总体积
        /// </summary>
        public decimal? 总体积 { get; set; }

        /// <summary>
        /// 成本浮率
        /// </summary>
        public decimal? 成本浮率 { get; set; }

        /// <summary>
        /// 汇率
        /// </summary>
        public decimal? 汇率 { get; set; }

        /// <summary>
        /// 运费
        /// </summary>
        public decimal? 运费 { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string? 备注 { get; set; }

        /// <summary>
        /// 状态
        /// </summary>
        public int? 状态 { get; set; }

        /// <summary>
        /// 货柜详情列表
        /// </summary>
        public List<ContainerDetailDto> Details { get; set; } = new List<ContainerDetailDto>();
    }

    /// <summary>
    /// 货柜详情DTO
    /// </summary>
    public class ContainerDetailDto
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        public int ID { get; set; }

        /// <summary>
        /// 全局唯一标识
        /// </summary>
        public string? HGUID { get; set; }

        /// <summary>
        /// 当前服务端可编辑字段的乐观并发令牌。客户端保存时必须原样带回对应字段。
        /// </summary>
        public Dictionary<string, string> ServerFieldTokens { get; set; } = new();

        /// <summary>
        /// 主表GUID
        /// </summary>
        public string? 主表GUID { get; set; }

        /// <summary>
        /// 商品编码
        /// </summary>
        public string? 商品编码 { get; set; }

        /// <summary>
        /// 本地供应商编码
        /// </summary>
        public string? LocalSupplierCode { get; set; }

        /// <summary>
        /// 仓库分类GUID（来自本地商品主数据）
        /// </summary>
        public string? ProductCategoryGUID { get; set; }

        /// <summary>
        /// 仓库分类名称（来自仓库分类表）
        /// </summary>
        public string? ProductCategoryName { get; set; }

        /// <summary>
        /// 装柜类型
        /// </summary>
        public string? 装柜类型 { get; set; }

        /// <summary>
        /// 商品类型
        /// </summary>
        public string? 商品类型 { get; set; }

        /// <summary>
        /// 套装数量
        /// </summary>
        public decimal? 套装数量 { get; set; }

        /// <summary>
        /// 装柜件数
        /// </summary>
        public decimal? 装柜件数 { get; set; }

        /// <summary>
        /// 中包数（仓库商品最小订货量）
        /// </summary>
        public decimal? 中包数 { get; set; }

        /// <summary>
        /// 装柜数量
        /// </summary>
        public decimal? 装柜数量 { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? 国内价格 { get; set; }

        /// <summary>
        /// 调整浮率
        /// </summary>
        public decimal? 调整浮率 { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? 进口价格 { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? 贴牌价格 { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        public decimal? 单件装箱数 { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        public decimal? 单件体积 { get; set; }

        /// <summary>
        /// 合计装柜金额
        /// </summary>
        public decimal? 合计装柜金额 { get; set; }

        /// <summary>
        /// 合计装柜体积
        /// </summary>
        public decimal? 合计装柜体积 { get; set; }

        /// <summary>
        /// 运输成本
        /// </summary>
        public decimal? 运输成本 { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string? 备注 { get; set; }

        /// <summary>
        /// 商品信息
        /// </summary>
        public ContainerProductInfoDto? 商品信息 { get; set; }

        public bool 是否新商品 { get; set; }

        /// <summary>
        /// 是否本柜新品：本地主档尚未建档，或主档正是由本货柜「创建新商品/提交货柜」建立。
        /// 是否新商品 在建档后即变为 false，本字段在建档后仍保持 true，仅用于展示与筛选；
        /// 建档、零售价来源等操作判断仍以 是否新商品 为准。
        /// </summary>
        public bool IsContainerNewProduct { get; set; }

        /// <summary>
        /// 上次进货价格（货柜明细快照）
        /// </summary>
        public decimal? LastImportPrice { get; set; }

        /// <summary>
        /// 上次零售价（货柜明细快照）
        /// </summary>
        public decimal? LastOEMPrice { get; set; }

        /// <summary>
        /// 实时进货价（仓库商品表 ImportPrice）
        /// </summary>
        public decimal? WarehouseImportPrice { get; set; }

        /// <summary>
        /// 实时零售价（仓库商品表 OEMPrice）
        /// </summary>
        public decimal? WarehouseOEMPrice { get; set; }

        /// <summary>
        /// 只读零售价：新商品取国内商品表，已有商品取仓库商品表；不参与明细业务价保存。
        /// </summary>
        public decimal? ReadonlyOemPrice { get; set; }

        /// <summary>
        /// 仓库商品是否上架
        /// </summary>
        public bool? WarehouseIsActive { get; set; }

        // 以下字段只用于在同一查询快照中签发并发令牌，绝不序列化给客户端。
        [JsonIgnore]
        public decimal? ServerTokenDomesticMiddlePackQuantity { get; set; }

        [JsonIgnore]
        public string? ServerTokenTargetCategoryGuid { get; set; }

        [JsonIgnore]
        public string? ServerTokenLocalCategoryGuid { get; set; }

        [JsonIgnore]
        public string? ServerTokenLocalProductName { get; set; }

        [JsonIgnore]
        public string? ServerTokenLocalEnglishName { get; set; }

        [JsonIgnore]
        public string? ServerTokenDomesticEnglishName { get; set; }

        [JsonIgnore]
        public decimal? ServerTokenLocalPurchasePrice { get; set; }

        [JsonIgnore]
        public decimal? ServerTokenLocalRetailPrice { get; set; }

        [JsonIgnore]
        public bool? ServerTokenDetailIsActive { get; set; }

        /// <summary>
        /// 服务端权威匹配方式：productCode | supplierItem | unmatched。
        /// </summary>
        public string MatchType { get; set; } = "unmatched";

        public string? LocalProductCode { get; set; }
        public string? DomesticProductCode { get; set; }
        public bool HasProductCodeConflict { get; set; }
        public string? ConflictReason { get; set; }
    }

    /// <summary>
    /// 货柜商品信息DTO
    /// </summary>
    public class ContainerProductInfoDto
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        public string? 商品编码 { get; set; }

        /// <summary>
        /// 本地供应商编码
        /// </summary>
        public string? LocalSupplierCode { get; set; }

        /// <summary>
        /// 仓库分类GUID（来自本地商品主数据）
        /// </summary>
        public string? ProductCategoryGUID { get; set; }

        /// <summary>
        /// 仓库分类名称（来自仓库分类表）
        /// </summary>
        public string? ProductCategoryName { get; set; }

        /// <summary>
        /// 货号
        /// </summary>
        public string? 货号 { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? 商品名称 { get; set; }

        /// <summary>
        /// 英文名称
        /// </summary>
        public string? 英文名称 { get; set; }

        /// <summary>
        /// 商品图片
        /// </summary>
        public string? 商品图片 { get; set; }

        public string? 条形码 { get; set; }

        /// <summary>
        /// 零售价格
        /// </summary>
        public decimal? 零售价格 { get; set; }

        /// <summary>
        /// 商品规格
        /// </summary>
        public string? 商品规格 { get; set; }

        /// <summary>
        /// 单位
        /// </summary>
        public string? 单位 { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        public decimal? 单件装箱数 { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        public decimal? 单件体积 { get; set; }

        /// <summary>
        /// 商品类型
        /// </summary>
        public string? 商品类型 { get; set; }

        /// <summary>
        /// 套装数量
        /// </summary>
        public decimal? 套装数量 { get; set; }
    }

    /// <summary>
    /// 货柜明细中查看国内套装多码价格的只读项
    /// </summary>
    public class ContainerDomesticSetCodeDto
    {
        public string? ProductCode { get; set; }
        public string? ItemNumber { get; set; }
        public int? ProductType { get; set; }
        public string? SetProductCode { get; set; }
        public string? SetItemNumber { get; set; }
        public string? Barcode { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? PurchasePrice { get; set; }
    }

    /// <summary>
    /// 货柜明细弹窗批量更新国内套装多码价格请求
    /// </summary>
    public class UpdateContainerDomesticSetCodePricesRequestDto
    {
        public List<UpdateContainerDomesticSetCodePriceItemDto> Items { get; set; } = new();
    }

    public class UpdateContainerDomesticSetCodePriceItemDto
    {
        public string? SetProductCode { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? PurchasePrice { get; set; }
    }

    /// <summary>
    /// 货柜明细数字区间筛选
    /// </summary>
    public class ContainerDetailNumberRangeDto
    {
        public decimal? Min { get; set; }
        public decimal? Max { get; set; }
    }

    /// <summary>
    /// 货柜明细服务端查询请求（小货柜整表加载，大货柜按需分页）
    /// </summary>
    public class ContainerDetailQueryDto
    {
        public string ContainerGuid { get; set; } = string.Empty;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 100;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? Remark { get; set; }
        public List<string> ProductTypes { get; set; } = new();
        public List<string> NewProductStates { get; set; } = new();
        public List<string> MatchTypes { get; set; } = new();
        public List<string> WarehouseStatus { get; set; } = new();
        public ContainerDetailNumberRangeDto? ContainerPieces { get; set; }
        public decimal? ContainerPiecesMin { get; set; }
        public decimal? ContainerPiecesMax { get; set; }
        public ContainerDetailNumberRangeDto? MiddlePackQuantity { get; set; }
        public decimal? MiddlePackQuantityMin { get; set; }
        public decimal? MiddlePackQuantityMax { get; set; }
        public ContainerDetailNumberRangeDto? ContainerQuantity { get; set; }
        public decimal? ContainerQuantityMin { get; set; }
        public decimal? ContainerQuantityMax { get; set; }
        public ContainerDetailNumberRangeDto? PackingQuantity { get; set; }
        public decimal? PackingQuantityMin { get; set; }
        public decimal? PackingQuantityMax { get; set; }
        public ContainerDetailNumberRangeDto? UnitVolume { get; set; }
        public decimal? UnitVolumeMin { get; set; }
        public decimal? UnitVolumeMax { get; set; }
        public ContainerDetailNumberRangeDto? DomesticPrice { get; set; }
        public decimal? DomesticPriceMin { get; set; }
        public decimal? DomesticPriceMax { get; set; }
        public ContainerDetailNumberRangeDto? FloatRate { get; set; }
        public decimal? FloatRateMin { get; set; }
        public decimal? FloatRateMax { get; set; }
        public ContainerDetailNumberRangeDto? TransportCost { get; set; }
        public decimal? TransportCostMin { get; set; }
        public decimal? TransportCostMax { get; set; }
        public ContainerDetailNumberRangeDto? UnitTransportCost { get; set; }
        public decimal? UnitTransportCostMin { get; set; }
        public decimal? UnitTransportCostMax { get; set; }
        public ContainerDetailNumberRangeDto? WarehouseImportPrice { get; set; }
        public decimal? WarehouseImportPriceMin { get; set; }
        public decimal? WarehouseImportPriceMax { get; set; }
        public ContainerDetailNumberRangeDto? LastOEMPrice { get; set; }
        public decimal? LastOEMPriceMin { get; set; }
        public decimal? LastOEMPriceMax { get; set; }
        public ContainerDetailNumberRangeDto? ImportPrice { get; set; }
        public decimal? ImportPriceMin { get; set; }
        public decimal? ImportPriceMax { get; set; }
        public ContainerDetailNumberRangeDto? OemPrice { get; set; }
        public decimal? OemPriceMin { get; set; }
        public decimal? OemPriceMax { get; set; }
        public List<string> SelectedTags { get; set; } = new();
        public string? SortBy { get; set; }
        public string? SortOrder { get; set; }
        public bool IncludeItems { get; set; } = true;
        public bool IncludeTotal { get; set; } = true;
        public bool IncludeStats { get; set; } = true;
    }

    /// <summary>
    /// 货柜明细标签统计（基于当前服务端筛选口径）
    /// </summary>
    public class ContainerDetailTagStatsDto
    {
        public int All { get; set; }
        public int New { get; set; }
        public int Existing { get; set; }
        public int Normal { get; set; }
        public int Set { get; set; }
        public int Multi { get; set; }
        public int SetChild { get; set; }
        public int NoOemPrice { get; set; }
        public int AbnormalImport { get; set; }
        public int Active { get; set; }
        public int Inactive { get; set; }
        /// <summary>本次进口价格高于仓库实时进货价的明细数</summary>
        public int PriceUp { get; set; }
        /// <summary>本次进口价格低于仓库实时进货价的明细数</summary>
        public int PriceDown { get; set; }
        public int ProductCodeMatched { get; set; }
        public int SupplierItemMatched { get; set; }
        public int Unmatched { get; set; }
    }

    /// <summary>
    /// 货柜明细服务端查询响应
    /// </summary>
    public class ContainerDetailQueryResultDto
    {
        public List<ContainerDetailDto> Items { get; set; } = new();
        public int ItemsTotal { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public bool HasMore { get; set; }
        public bool TotalComputed { get; set; } = true;
        public bool StatsComputed { get; set; } = true;
        public ContainerDetailTagStatsDto TagStats { get; set; } = new();
    }

    /// <summary>
    /// 货柜明细批量操作作用范围
    /// </summary>
    public class ContainerDetailBatchScopeDto
    {
        public List<string> SelectedHguids { get; set; } = new();
        public ContainerDetailQueryDto? Query { get; set; }
        public string? PreviewToken { get; set; }
    }

    public class ContainerDetailBatchPreviewRequestDto
    {
        public string Operation { get; set; } = string.Empty;
        public ContainerDetailBatchScopeDto Scope { get; set; } = new();
        public Dictionary<string, JsonElement> Parameters { get; set; } = new();
    }

    public class ContainerDetailBatchPreviewResultDto
    {
        public int AffectedCount { get; set; }
        public List<string> FieldSummary { get; set; } = new();
        public string PreviewToken { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }

    public class ContainerDetailPresenceHeartbeatDto
    {
        public string ClientSessionId { get; set; } = string.Empty;
        public string State { get; set; } = "viewing";
    }

    public class ContainerDetailPresenceUserDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public DateTime LastActiveAt { get; set; }
    }

    public class ContainerDetailPresenceDto
    {
        public List<ContainerDetailPresenceUserDto> Viewers { get; set; } = new();
        public List<ContainerDetailPresenceUserDto> Editors { get; set; } = new();
    }

    /// <summary>
    /// 货柜明细批量调浮率请求
    /// </summary>
    public class ContainerDetailApplyFloatRateRequestDto : ContainerDetailBatchScopeDto
    {
        public decimal? FloatRate { get; set; }
    }

    /// <summary>
    /// 货柜明细批量改价请求
    /// </summary>
    public class ContainerDetailApplyPricesRequestDto : ContainerDetailBatchScopeDto
    {
        public decimal? ImportPrice { get; set; }
        public decimal? OemPrice { get; set; }
    }

    /// <summary>
    /// 按筛选范围批量上下架。IsActive 必填，且执行必须携带预览令牌。
    /// </summary>
    public class ContainerDetailSetStatusRequestDto : ContainerDetailBatchScopeDto
    {
        public bool? IsActive { get; set; }

        /// <summary>下架时随请求登记的供货说明；上架时忽略。不参与预览指纹（不改变作用范围）。</summary>
        public WarehouseProductSupplyNoticeInputDto? SupplyNotice { get; set; }
    }

    /// <summary>
    /// 按筛选范围批量分配分类；CategoryGuid 为 null 表示清除分类。
    /// </summary>
    public class ContainerDetailAssignCategoryRequestDto : ContainerDetailBatchScopeDto
    {
        public string? CategoryGuid { get; set; }
    }

    /// <summary>
    /// 更新货柜明细DTO
    /// </summary>
    public class UpdateContainerDetailDto
    {
        /// <summary>
        /// 明细GUID
        /// </summary>
        public string HGUID { get; set; } = string.Empty;

        /// <summary>
        /// 开始编辑时读取到的字段令牌；仅校验本次实际提交的字段。
        /// </summary>
        public Dictionary<string, string> ExpectedServerFieldTokens { get; set; } = new();

        /// <summary>
        /// 用户确认“保留我的值”时携带的服务器当前令牌。
        /// </summary>
        public Dictionary<string, string> OverrideAcknowledgements { get; set; } = new();

        /// <summary>
        /// 调整浮率
        /// </summary>
        public decimal? 调整浮率 { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? 国内价格 { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? 进口价格 { get; set; }

        /// <summary>
        /// 运输成本
        /// </summary>
        public decimal? 运输成本 { get; set; }

        /// <summary>
        /// 商品名称（商品信息）
        /// </summary>
        public string? 商品名称 { get; set; }

        /// <summary>
        /// 英文名称（商品信息）
        /// </summary>
        public string? 英文名称 { get; set; }

        /// <summary>
        /// 清空英文名称（商品信息）
        /// </summary>
        public bool? ClearEnglishName { get; set; }

        /// <summary>
        /// 目标仓库分类GUID
        /// </summary>
        public string? ProductCategoryGUID { get; set; }

        /// <summary>
        /// 清空目标仓库分类；与 null 的“未提交字段”语义分开，供批量分类操作安全表达。
        /// </summary>
        public bool? ClearProductCategory { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? 贴牌价格 { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        public decimal? 单件装箱数 { get; set; }

        /// <summary>
        /// 中包数（写回仓库商品最小订货量）
        /// </summary>
        public decimal? 中包数 { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        public decimal? 单件体积 { get; set; }

        /// <summary>
        /// 装柜数量
        /// </summary>
        public decimal? 装柜数量 { get; set; }

        /// <summary>
        /// 合计装柜体积
        /// </summary>
        public decimal? 合计装柜体积 { get; set; }

        /// <summary>
        /// 合计装柜金额
        /// </summary>
        public decimal? 合计装柜金额 { get; set; }

        /// <summary>
        /// 是否上架
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 货柜明细备注；null 表示不修改，空字符串表示清空。
        /// </summary>
        public string? 备注 { get; set; }

        /// <summary>
        /// 跳过商品主数据和关联价格表同步，仅更新货柜明细
        /// </summary>
        public bool? SkipRelatedProductSync { get; set; }
    }

    /// <summary>
    /// React 货柜明细批量更新结果。
    /// </summary>
    public class ContainerDetailBatchUpdateResultDto
    {
        public int TotalUpdated { get; set; }
        public int TotalRequested { get; set; }
        public int AutoRepairedStoreGroupCount { get; set; }
        public int AutoRepairedRelationCount { get; set; }
        public List<ContainerDetailBatchUpdateValidationErrorDto> ValidationErrors { get; set; } =
            new();
        public List<ContainerDetailFieldConflictDto> Conflicts { get; set; } = new();
    }

    /// <summary>
    /// React 货柜明细批量更新的字段级校验错误。
    /// </summary>
    public class ContainerDetailBatchUpdateValidationErrorDto
    {
        public string HGUID { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 单个字段的乐观并发冲突；其它字段仍可正常提交。
    /// </summary>
    public class ContainerDetailFieldConflictDto
    {
        public string HGUID { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public object? ServerValue { get; set; }
        public object? SubmittedValue { get; set; }
        public string CurrentServerFieldToken { get; set; } = string.Empty;
    }

    /// <summary>
    /// 人工确认后，把国内商品编码对齐到本地主档商品编码。
    /// </summary>
    public class AlignDomesticProductCodeRequestDto
    {
        public string DetailHguid { get; set; } = string.Empty;
        public string ExpectedDomesticProductCode { get; set; } = string.Empty;
        public string TargetProductCode { get; set; } = string.Empty;
        /// <summary>
        /// 澳洲本地供应商代码（Product.LocalSupplierCode，如 200），只用于校验本地主档；
        /// 不是 DomesticProduct.SupplierCode（中国供应商代码）。
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;
    }

    public class AlignDomesticProductCodeResultDto
    {
        public string OldProductCode { get; set; } = string.Empty;
        public string NewProductCode { get; set; } = string.Empty;
        public int UpdatedDomesticProducts { get; set; }
        public int UpdatedContainerDetails { get; set; }
        public int UpdatedDomesticSetProducts { get; set; }
        public int UpdatedProductGrades { get; set; }
        public int UpdatedDomesticProductCreationLogs { get; set; }
    }

    /// <summary>
    /// 创建货柜DTO
    /// </summary>
    public class CreateContainerDto
    {
        /// <summary>
        /// 货柜编号
        /// </summary>
        public string 货柜编号 { get; set; } = string.Empty;

        /// <summary>
        /// 装柜日期
        /// </summary>
        public DateTime? 装柜日期 { get; set; }

        /// <summary>
        /// 预计到岸日期
        /// </summary>
        public DateTime? 预计到岸日期 { get; set; }

        /// <summary>
        /// 汇率
        /// </summary>
        public decimal? 汇率 { get; set; }

        /// <summary>
        /// 运费
        /// </summary>
        public decimal? 运费 { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string? 备注 { get; set; }
    }

    /// <summary>
    /// 货柜查询请求DTO
    /// </summary>
    public class ContainerQueryRequest
    {
        /// <summary>
        /// 日期过滤类型 - 预计到岸日期 或 实际到货日期
        /// </summary>
        public string DateType { get; set; } = "预计到岸日期";

        /// <summary>
        /// 开始日期
        /// </summary>
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// 结束日期
        /// </summary>
        public DateTime? EndDate { get; set; }

        /// <summary>
        /// 页码
        /// </summary>
        public int Page { get; set; } = 1;

        /// <summary>
        /// 页大小
        /// </summary>
        public int PageSize { get; set; } = 20;

        /// <summary>
        /// 货号过滤
        /// </summary>
        public string? ItemNumberFilter { get; set; }

        /// <summary>
        /// 柜内商品关键字：货号、商品名称、英文名称或条码任一包含即命中该货柜
        /// </summary>
        public string? ProductKeyword { get; set; }

        /// <summary>
        /// 货柜编号列头过滤
        /// </summary>
        public string? ContainerNumberFilter { get; set; }

        /// <summary>
        /// 装柜日期列头过滤开始日期
        /// </summary>
        public DateTime? LoadingDateStart { get; set; }

        /// <summary>
        /// 装柜日期列头过滤结束日期
        /// </summary>
        public DateTime? LoadingDateEnd { get; set; }

        /// <summary>
        /// 预计到岸日期列头过滤开始日期
        /// </summary>
        public DateTime? EstimatedArrivalDateStart { get; set; }

        /// <summary>
        /// 预计到岸日期列头过滤结束日期
        /// </summary>
        public DateTime? EstimatedArrivalDateEnd { get; set; }

        /// <summary>
        /// 实际到货日期列头过滤开始日期
        /// </summary>
        public DateTime? ActualArrivalDateStart { get; set; }

        /// <summary>
        /// 实际到货日期列头过滤结束日期
        /// </summary>
        public DateTime? ActualArrivalDateEnd { get; set; }

        /// <summary>
        /// 合计件数列头过滤最小值
        /// </summary>
        public decimal? TotalPiecesMin { get; set; }

        /// <summary>
        /// 合计件数列头过滤最大值
        /// </summary>
        public decimal? TotalPiecesMax { get; set; }

        /// <summary>
        /// 合计金额列头过滤最小值
        /// </summary>
        public decimal? TotalAmountMin { get; set; }

        /// <summary>
        /// 合计金额列头过滤最大值
        /// </summary>
        public decimal? TotalAmountMax { get; set; }

        /// <summary>
        /// 总体积列头过滤最小值
        /// </summary>
        public decimal? TotalVolumeMin { get; set; }

        /// <summary>
        /// 总体积列头过滤最大值
        /// </summary>
        public decimal? TotalVolumeMax { get; set; }

        /// <summary>
        /// 状态列头过滤
        /// </summary>
        public List<int>? Statuses { get; set; }

        /// <summary>
        /// 排序字段
        /// </summary>
        public string? SortBy { get; set; } = "货号";

        /// <summary>
        /// 排序方向
        /// </summary>
        public string? SortDirection { get; set; } = "asc";
    }

    /// <summary>
    /// 货柜列表响应DTO
    /// </summary>
    public class ContainerListResponse
    {
        /// <summary>
        /// 货柜列表
        /// </summary>
        public List<ContainerMainDto> Containers { get; set; } = new List<ContainerMainDto>();

        /// <summary>
        /// 总数量
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// 页码
        /// </summary>
        public int Page { get; set; }

        /// <summary>
        /// 页大小
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// 总页数
        /// </summary>
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }

    /// <summary>
    /// 即将到货货柜商品响应DTO（用于前端 Coming Soon 页面）
    /// </summary>
    public class ComingSoonContainerDto
    {
        /// <summary>
        /// 货柜编号
        /// </summary>
        public string? 货柜编号 { get; set; }

        /// <summary>
        /// 货柜编码
        /// </summary>
        public string? 货柜编码 { get; set; }

        /// <summary>
        /// 装柜日期
        /// </summary>
        public DateTime? 装柜日期 { get; set; }

        /// <summary>
        /// 预计到岸日期
        /// </summary>
        public DateTime? 预计到岸日期 { get; set; }

        /// <summary>
        /// 实际到货日期
        /// </summary>
        public DateTime? 实际到货日期 { get; set; }

        /// <summary>
        /// 货柜状态
        /// </summary>
        public int? 状态 { get; set; }

        /// <summary>
        /// 商品列表
        /// </summary>
        public List<ComingSoonProductDto> 商品列表 { get; set; } = new List<ComingSoonProductDto>();
    }

    /// <summary>
    /// 即将到货商品DTO（用于前端 Coming Soon 页面）
    /// </summary>
    public class ComingSoonProductDto
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        public string? 商品编码 { get; set; }

        /// <summary>
        /// 货号
        /// </summary>
        public string? 货号 { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? 商品名称 { get; set; }

        /// <summary>
        /// 英文名称
        /// </summary>
        public string? 英文名称 { get; set; }

        /// <summary>
        /// 商品图片
        /// </summary>
        public string? 商品图片 { get; set; }

        /// <summary>
        /// 装柜数量
        /// </summary>
        public decimal? 装柜数量 { get; set; }
    }
}
