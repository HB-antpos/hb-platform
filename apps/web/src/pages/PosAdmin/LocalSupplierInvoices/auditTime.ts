const TIMEZONE_SUFFIX_PATTERN = /(Z|[+-]\d{2}:?\d{2})$/i

function normalizeAuditTimestamp(value: string) {
  const trimmed = value.trim()
  if (TIMEZONE_SUFFIX_PATTERN.test(trimmed)) {
    return trimmed
  }

  // 兼容旧接口：无时区后缀的审计时间实际为 UTC，新接口自带的时区信息则保持原语义。
  return `${trimmed.replace(' ', 'T')}Z`
}

export function formatLocalSupplierInvoiceAuditTime(value?: string | null) {
  if (!value?.trim()) {
    return '--'
  }

  const date = new Date(normalizeAuditTimestamp(value))
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('zh-CN', { hour12: false })
}

function padTwo(value: number) {
  return String(value).padStart(2, '0')
}

/**
 * 列表用的紧凑审计时间：今年的显示「月-日 时:分」，往年的只显示日期；完整时间放在悬停提示里。
 * 解析口径与 formatLocalSupplierInvoiceAuditTime 一致（无时区后缀按 UTC）。
 */
export function formatLocalSupplierInvoiceAuditTimeCompact(value?: string | null, now: Date = new Date()) {
  if (!value?.trim()) {
    return '--'
  }

  const date = new Date(normalizeAuditTimestamp(value))
  if (Number.isNaN(date.getTime())) return value
  const day = `${padTwo(date.getMonth() + 1)}-${padTwo(date.getDate())}`
  if (date.getFullYear() !== now.getFullYear()) {
    return `${date.getFullYear()}-${day}`
  }
  return `${day} ${padTwo(date.getHours())}:${padTwo(date.getMinutes())}`
}
