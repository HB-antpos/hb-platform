const MONTH_ABBREVIATIONS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/**
 * 商品卡片日期统一显示为澳洲习惯的「日 月份缩写 年」（如 4 Jun 2024），中英文界面一致、不随浏览器地区变化，
 * 避免 2024/6/4、6/4/2024 这类数字写法被误读成 4 月 6 日。
 * 只取字符串开头的 YYYY-MM-DD：来货日期是后端按分店时区截好的纯日期，订货日期是墙钟时间，
 * 不经过 new Date 解析，就不会因 UTC/本地时区换算差一天。
 */
export function formatShopCardDate(value?: string | null): string | null {
  const dateOnly = value?.trim().match(/^(\d{4})-(\d{2})-(\d{2})/)
  if (!dateOnly) {
    return null
  }

  const month = MONTH_ABBREVIATIONS[Number(dateOnly[2]) - 1]
  const day = Number(dateOnly[3])
  if (!month || day < 1 || day > 31) {
    return null
  }

  return `${day} ${month} ${dateOnly[1]}`
}
