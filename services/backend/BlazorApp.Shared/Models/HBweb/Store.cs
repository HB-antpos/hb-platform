using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 门店实体类，表示系统中的门店信息
    /// </summary>
    public class Store : BaseEntity
    {
        /// <summary>
        /// 门店全局唯一标识符
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsNullable = false)]
        public string StoreGUID { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 门店名称
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 100)]
        public string StoreName { get; set; } = string.Empty;

        /// <summary>
        /// 门店代码
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>
        /// 门店地址
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 500)]
        public string? Address { get; set; }

        /// <summary>
        /// 门店考勤使用的 IANA 时区；为空时按既有地址和名称规则推导。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 80)]
        public string? TimeZoneId { get; set; }

        /// <summary>
        /// 退换货政策；可空，最长 500 字符，由 Web 分店维护表单录入。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 500)]
        public string? ReturnPolicy { get; set; }

        /// <summary>
        /// 代金券使用说明（印在退款代金券券面「VOUCHER TERMS」下方，一行一条）；可空，最长 600 字符。
        /// 为空表示收银端按内置默认文案打印。与 ReturnPolicy 一样由 Web 分店维护表单录入、点「下发」后才到收银端。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 600)]
        public string? VoucherTerms { get; set; }

        /// <summary>
        /// 分期条款（印在进行中分期小票「INSTALLMENT TERMS」下方，一行一条）；可空，最长 600 字符。
        /// 为空表示收银端按内置默认文案打印。下发口径同 <see cref="VoucherTerms"/>。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 600)]
        public string? InstallmentTerms { get; set; }

        /// <summary>
        /// 联系邮箱
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 100)]
        public string? ContactEmail { get; set; }

        /// <summary>
        /// 澳大利亚商业号码
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 20)]
        public string? ABN { get; set; }

        /// <summary>
        /// 品牌名称
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 100)]
        public string? BrandName { get; set; }

        /// <summary>
        /// 联系电话
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 200)]
        public string? Phone { get; set; }

        /// <summary>
        /// 是否激活状态
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 门店关联的用户列表（多对多导航属性）
        /// </summary>
        [Navigate(typeof(UserStore), nameof(UserStore.StoreGUID), nameof(UserStore.UserGUID))]
        public List<User>? Users { get; set; }
    }
}
