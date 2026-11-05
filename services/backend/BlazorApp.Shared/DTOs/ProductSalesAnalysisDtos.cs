using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 「国内供应商引用列表」专用 JSON 转换器。<see cref="ProductSalesSupplierRefDto"/> 被澳洲与国内两个列表共用，
    /// 不能直接在 Name 属性上挂遮罩（会连澳洲供应商名称一起遮掉），所以只挂在 ChinaSuppliers 列表属性上：
    /// 遮罩作用域内每一项只输出代码，作用域外与读取原样。输出的是临时副本，不改动缓存里的原对象。
    /// </summary>
    public sealed class ChinaSupplierRefListJsonConverter : JsonConverter<List<ProductSalesSupplierRefDto>>
    {
        // 转换器是挂在属性上的，这里按类型再走一次默认序列化不会递归回本转换器。
        public override List<ProductSalesSupplierRefDto>? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => JsonSerializer.Deserialize<List<ProductSalesSupplierRefDto>>(ref reader, options);

        public override void Write(
            Utf8JsonWriter writer,
            List<ProductSalesSupplierRefDto> value,
            JsonSerializerOptions options
        )
        {
            var output = DomesticSupplierInfoMasking.IsMaskingActive
                ? value.Select(item => new ProductSalesSupplierRefDto { Code = item.Code }).ToList()
                : value;
            JsonSerializer.Serialize(writer, output, options);
        }
    }

    /// <summary>
    /// 商品日分店统计的只读完整性状态。
    /// 旧报表接口保留原有 data 形状，仅在外层附加此状态的字段。
    /// </summary>
    public class ProductReportStatisticStatusDto
    {
        public string StatisticStatus { get; set; } = "Pending";
        public string? StatisticMessage { get; set; }
        public DateTime? StatisticUpdatedAt { get; set; }
        public string CacheVersion { get; set; } = "none";
    }

    /// <summary>
    /// 旧商品报表 GET 接口的统一外层包络。
    /// 保留 data 的既有业务形状，同时无条件提供统计完整性元数据。
    /// </summary>
    public class ProductReportResponseDto<T>
    {
        public bool Success { get; set; } = true;
        public T? Data { get; set; }
        public string? StatisticStatus { get; set; }
        public string? StatisticMessage { get; set; }
        public DateTime? StatisticUpdatedAt { get; set; }
        public string? CacheVersion { get; set; }
    }

    /// <summary>
    /// 商品销量分析通用请求。
    /// 所有 POST 接口共用同一请求体，按端点取用相关字段。
    /// </summary>
    public class ProductSalesAnalysisRequest
    {
        public ProductSalesAnalysisFilterDto Filter { get; set; } = new();

        public ProductSalesAnalysisSelectionDto Selection { get; set; } = new();

        public ProductSalesAnalysisScopeDto? Scope { get; set; }

        /// <summary>branch-daily 接口下的分店代码</summary>
        public string? BranchCode { get; set; }

        public int PageNumber { get; set; } = 1;

        public int PageSize { get; set; } = 50;

        /// <summary>排序字段：quantity / salesAmount / productCode</summary>
        public string? SortBy { get; set; }

        /// <summary>排序方向：asc / desc</summary>
        public string? SortDirection { get; set; }
    }

    /// <summary>日期与商品/供应商过滤条件。</summary>
    public class ProductSalesAnalysisFilterDto
    {
        /// <summary>开始日期（包含首尾）</summary>
        public DateTime StartDate { get; set; }

        /// <summary>结束日期（包含首尾）</summary>
        public DateTime EndDate { get; set; }

        /// <summary>货号/条码/中英文名称关键字</summary>
        public string? Keyword { get; set; }

        public List<string>? AustralianSupplierCodes { get; set; }

        public List<string>? ChinaSupplierCodes { get; set; }
    }

    /// <summary>跨分页商品选择语义。</summary>
    public class ProductSalesAnalysisSelectionDto
    {
        public string Mode { get; set; } = "allFiltered";

        public List<string>? IncludedProductCodes { get; set; }

        public List<string>? ExcludedProductCodes { get; set; }
    }

    /// <summary>右栏/每日查询范围。</summary>
    public class ProductSalesAnalysisScopeDto
    {
        public string? Mode { get; set; }

        public string? ProductCode { get; set; }
    }

    /// <summary>
    /// 商品销量分析统一响应包裹。
    /// </summary>
    public class ProductSalesAnalysisResponse<T>
    {
        public string? StatisticStatus { get; set; }
        public string? StatisticMessage { get; set; }
        public DateTime? StatisticUpdatedAt { get; set; }
        public string? CacheVersion { get; set; }
        public T? Data { get; set; }
    }

    /// <summary>
    /// 分页数据。
    /// </summary>
    public class ProductSalesAnalysisPagedDto<T>
    {
        public List<T> Items { get; set; } = new();
        public int Total { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>销量/金额指标。</summary>
    public class ProductSalesAnalysisMetricsDto
    {
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
        public decimal? AverageUnitPrice { get; set; }
    }

    /// <summary>供应商 code/name 引用。</summary>
    public class ProductSalesSupplierRefDto
    {
        public string Code { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    /// <summary>商品销量行。</summary>
    public class ProductSalesProductRowDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? ImageUrl { get; set; }
        public List<ProductSalesSupplierRefDto> AustralianSuppliers { get; set; } = new();
        [JsonConverter(typeof(ChinaSupplierRefListJsonConverter))]
        public List<ProductSalesSupplierRefDto> ChinaSuppliers { get; set; } = new();
        public bool ChinaSupplierUnmapped { get; set; }
        public ProductSalesAnalysisMetricsDto Metrics { get; set; } = new();
    }

    /// <summary>商品每日销量。</summary>
    public class ProductSalesDailyDto
    {
        public DateTime Date { get; set; }
        public ProductSalesAnalysisMetricsDto Metrics { get; set; } = new();
    }

    /// <summary>分店销量汇总。</summary>
    public class ProductSalesBranchDto
    {
        public string BranchCode { get; set; } = string.Empty;
        public string? BranchName { get; set; }
        public ProductSalesAnalysisMetricsDto Metrics { get; set; } = new();
    }

    /// <summary>分店每日销量。</summary>
    public class ProductSalesBranchDailyDto
    {
        public DateTime Date { get; set; }
        public ProductSalesAnalysisMetricsDto Metrics { get; set; } = new();
    }

    /// <summary>供应商选项。</summary>
    public class ProductSalesAnalysisOptionsDto
    {
        public List<ProductSalesSupplierRefDto> AustralianSuppliers { get; set; } = new();
        [JsonConverter(typeof(ChinaSupplierRefListJsonConverter))]
        public List<ProductSalesSupplierRefDto> ChinaSuppliers { get; set; } = new();
    }
}
