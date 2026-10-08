/** 显示用格式化：时间一律按手机本地时区显示（服务端返回的是 UTC）。 */

export function formatSeasonalCardMoney(value?: number | null) {
  return value == null || Number.isNaN(value) ? "--" : `$${value.toFixed(2)}`;
}

function parseDate(value?: string | null) {
  if (!value) {
    return null;
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** 完整日期时间，用于历史记录与详情。 */
export function formatSeasonalCardDateTime(value?: string | null, localeTag = "en-AU") {
  if (!value) {
    return "--";
  }
  const date = parseDate(value);
  return date ? date.toLocaleString(localeTag, { hour12: false }) : value;
}

/** 简短时间（月-日 时:分），跨年时带上年份；用于「已于 X 由 Y 填报」这类提示。 */
export function formatSeasonalCardShortDateTime(
  value?: string | null,
  localeTag = "en-AU",
  now = new Date()
) {
  const date = parseDate(value);
  if (!date) {
    return value || "--";
  }
  return date.toLocaleString(localeTag, {
    ...(date.getFullYear() === now.getFullYear() ? {} : { year: "numeric" }),
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  });
}
