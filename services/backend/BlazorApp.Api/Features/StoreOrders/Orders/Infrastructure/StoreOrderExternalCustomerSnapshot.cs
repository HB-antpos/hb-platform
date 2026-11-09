namespace BlazorApp.Api.Features.StoreOrders.Orders.Infrastructure;

/// <summary>
/// 外购客户名称快照，2026-11-09 取自 HQ 的 CPT_DIC_外购客户信息表（全表 23 行）。
/// 历史订货单用 HGUID 作分店标识，HQ 断开后靠这份快照继续显示客户名称。
/// </summary>
internal static class StoreOrderExternalCustomerSnapshot
{
    internal static readonly IReadOnlyDictionary<string, string> NamesByGuid =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAD5CC27-1C3C-4A38-9A97-0CA9CDA39F47"] = "Ada - Sala Bay",
            ["0F8C7508-2111-46DC-A7D2-AC2F9B77DF65"] = "Ada - Tanilba Bay",
            ["BE604CBF-6DEB-4916-BE54-55637B955F70"] = "Ada - Taree",
            ["6BB2C3B0-FF85-4955-A271-2994EC3E5652"] = "Ada - Tas - Glenorchy",
            ["0691EB70-5A00-4CA7-B590-BDB1D820D6FD"] = "Ada - Tas - Kingston",
            ["0E045EB0-658A-4156-B4F3-A19C9D61A692"] = "Ada - Tas - Roshy Park",
            ["421CD036-500B-4D29-A357-50C6D8C1D4A5"] = "Ada - Tasmania",
            ["669C0A86-31BC-4EDF-9D4C-216E9E312CB1"] = "Beyand",
            ["6A82C54A-B0E8-4146-A96D-F2A38734FB3F"] = "BONDI",
            ["ABC8F21F-9031-47D9-BFD7-A9A55AD2183B"] = "BURWOOD",
            ["CFE1CC0C-3BB2-40B5-AE55-C6DF4D82EA7C"] = "Charles",
            ["E02A4F03-A550-4F0A-B40A-8712F4824B66"] = "David W",
            ["EBDE05B0-D529-4414-B603-36638F2D668E"] = "ERINA FAIR",
            ["E837C4A1-2DF0-49FA-8DDB-35D59D218CC7"] = "Fion",
            ["70F5A2C0-1FC8-400F-9552-63443D72EAB0"] = "HOME CO.",
            ["439C1A4C-2D2E-4EEF-991C-0ED9A339B5F2"] = "Hunter Charlestown DG",
            ["9459B52B-8705-47B3-9BE4-8BC5390FCC6F"] = "Hunter City",
            ["E522D3B1-C63E-4A37-B159-37B44C9EE629"] = "Hunter RF",
            ["6E70B4A4-8417-4766-B92E-631260493CF6"] = "Kotara",
            ["6B43D1FF-6D58-4A24-A1BC-5FEAB50AB90C"] = "Macquaire",
            ["7AECF30E-E846-4E00-AEBA-008A79E9DC9F"] = "Tommy",
            ["F86D462C-54BF-421A-A33C-FFFD18555053"] = "Victor",
            ["7AE36D06-D41C-4DE1-A2E2-D8E9544452F7"] = "WAREHOUSE",
        };
}
