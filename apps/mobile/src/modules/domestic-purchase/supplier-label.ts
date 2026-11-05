/**
 * 国内供应商（ChinaSupplier，代码形如 HB196）的显示文本：「代码 + 分隔符 + 名称」。
 *
 * 没有「查看国内供应商名称」权限的账号，后端把名称返回为 null，这里只显示代码，
 * 不留下「HB196 - 」「HB196 · 」这类残留；名称与代码相同时也只显示一次代码。
 * 只用于国内供应商，本地 / 澳洲供应商不要走这里。
 */
export function formatDomesticSupplierLabel(
  supplierCode: string | null | undefined,
  supplierName: string | null | undefined,
  separator = " - ",
): string {
  const code = (supplierCode ?? "").trim();
  const name = (supplierName ?? "").trim();
  if (!name || name.toLowerCase() === code.toLowerCase()) {
    return code;
  }
  return code ? `${code}${separator}${name}` : name;
}
