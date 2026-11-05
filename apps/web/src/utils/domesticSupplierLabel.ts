/**
 * 国内供应商（ChinaSupplier，代码形如 HB196）显示文本工具。
 *
 * 账号没有「查看国内供应商名称」权限时，后端返回的名称是 null；经各服务层归一后可能变成
 * 空串、undefined，或被兜底成代码本身。这里统一按「没有名称」处理：只显示供应商代码，
 * 不留下「HB196 - 」「 · HB196」「HB196 (HB196)」这类残留。
 *
 * 只用于国内供应商；澳洲 / 本地供应商不走这里。
 */
type MaybeText = string | null | undefined

function clean(value: MaybeText): string {
  return typeof value === 'string' ? value.trim() : ''
}

/** 是否有可显示的名称：空白、与代码相同（服务层用代码兜底）都算没有。 */
export function hasDomesticSupplierName(code: MaybeText, name: MaybeText): boolean {
  const nameText = clean(name)
  return nameText !== '' && nameText !== clean(code)
}

/** 单值显示：有名称用名称，否则回退到代码；两者都没有时返回兜底。 */
export function pickDomesticSupplierName(code: MaybeText, name: MaybeText, fallback = '-'): string {
  return clean(name) || clean(code) || fallback
}

/** 代码在前：`HB196 - 名称`；没有名称时只返回代码。 */
export function formatDomesticSupplierLabel(
  code: MaybeText,
  name: MaybeText,
  separator = ' - ',
  fallback = '-',
): string {
  const codeText = clean(code)
  if (!codeText || !hasDomesticSupplierName(code, name)) {
    return pickDomesticSupplierName(code, name, fallback)
  }
  return `${codeText}${separator}${clean(name)}`
}

/**
 * 名称在前：默认 `名称 · HB196`；传 `separator = ' ('`、`suffix = ')'` 可得到 `名称 (HB196)`。
 * 没有名称时只返回代码。
 */
export function formatDomesticSupplierNameFirst(
  code: MaybeText,
  name: MaybeText,
  separator = ' · ',
  suffix = '',
  fallback = '-',
): string {
  const codeText = clean(code)
  if (!codeText || !hasDomesticSupplierName(code, name)) {
    return pickDomesticSupplierName(code, name, fallback)
  }
  return `${clean(name)}${separator}${codeText}${suffix}`
}
