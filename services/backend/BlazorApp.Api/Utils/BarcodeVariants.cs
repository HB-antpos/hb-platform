namespace BlazorApp.Api.Utils
{
    /// <summary>
    /// 条码前导 0 变体。iOS 相机（expo-camera）会把「以 0 开头的 EAN-13」去掉前导 0 变成 12 位，
    /// 而库里可能按 13 位存放（反之亦然）。
    /// 调用方必须「精确匹配无结果才回退」：生产库存在 12 位 X 与 13 位 0+X 分属不同商品的情况，
    /// 不能把两种写法当作等价一起查。
    /// </summary>
    public static class BarcodeVariants
    {
        /// <summary>
        /// 纯数字条码的前导 0 变体：12 位补一个 0 → 13 位；13 位且以 0 开头则去掉它 → 12 位。
        /// 其它长度（含 11/14 位）或含非数字字符返回 null，不参与变体匹配。
        /// </summary>
        public static string? GetLeadingZeroVariant(string? barcode)
        {
            if (barcode is null || barcode.Length is not (12 or 13) || !barcode.All(char.IsAsciiDigit))
            {
                return null;
            }

            if (barcode.Length == 12)
            {
                return "0" + barcode;
            }

            return barcode[0] == '0' ? barcode[1..] : null;
        }

        /// <summary>
        /// 箱码（GTIN-14 / ITF-14，印在外箱 / 中包上的 14 位码）换算回单件商品的 EAN-13：
        /// 第 1 位是包装层级指示符，中间 12 位是单件条码去掉校验位，末位是 14 位口径的校验位。
        /// 去掉首尾两位、按 EAN-13 规则补算校验位即得单件条码，例如 19333527854091 → 9333527854094。
        /// 只认指示符 0–8 且 14 位校验位正确的码（9 是变量计量商品，不代表固定包装）；不满足返回 null。
        /// </summary>
        public static string? GetCaseCodeItemBarcode(string? code)
        {
            if (code is null || code.Length != 14 || !code.All(char.IsAsciiDigit) || code[0] == '9')
            {
                return null;
            }

            // 校验位不对说明不是标准箱码（可能是普通 14 位自编码或扫错），不做换算，避免误命中别的商品。
            if (ComputeGs1CheckDigit(code.AsSpan(0, 13)) != code[13] - '0')
            {
                return null;
            }

            var body = code.Substring(1, 12);
            return body + (char)('0' + ComputeGs1CheckDigit(body.AsSpan()));
        }

        /// <summary>
        /// 精确匹配无结果时依次尝试的条码写法（按优先级）：前导 0 变体 → 箱码换算出的单件条码 → 该单件条码的前导 0 变体。
        /// 调用方应逐个查询、命中即停，且只比对条码列；精确命中（包括库里本就登记的 14 位码）永远优先。
        /// </summary>
        public static IReadOnlyList<string> GetLookupFallbacks(string? barcode)
        {
            var fallbacks = new List<string>();
            var leadingZero = GetLeadingZeroVariant(barcode);
            if (leadingZero != null)
            {
                fallbacks.Add(leadingZero);
            }

            var itemBarcode = GetCaseCodeItemBarcode(barcode);
            if (itemBarcode != null)
            {
                fallbacks.Add(itemBarcode);
                // 指示符后紧跟 0 时单件码是「0 开头的 EAN-13」，库里可能按 12 位 UPC-A 存放。
                var itemLeadingZero = GetLeadingZeroVariant(itemBarcode);
                if (itemLeadingZero != null)
                {
                    fallbacks.Add(itemLeadingZero);
                }
            }

            return fallbacks;
        }

        /// <summary>GS1 校验位：从最右一位起，奇数位 ×3、偶数位 ×1，求和后补到 10 的倍数。</summary>
        private static int ComputeGs1CheckDigit(ReadOnlySpan<char> digits)
        {
            var sum = 0;
            for (var i = 0; i < digits.Length; i++)
            {
                var digit = digits[digits.Length - 1 - i] - '0';
                sum += i % 2 == 0 ? digit * 3 : digit;
            }

            return (10 - sum % 10) % 10;
        }
    }
}
