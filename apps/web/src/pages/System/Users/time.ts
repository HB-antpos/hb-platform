import dayjs from 'dayjs'

const timezoneSuffixPattern = /(Z|[+-]\d{2}:?\d{2})$/i

function normalizeUtcTimestamp(value: string) {
  const trimmed = value.trim()
  if (!trimmed) {
    return trimmed
  }

  if (timezoneSuffixPattern.test(trimmed)) {
    return trimmed
  }

  // 后端登录时间使用 UTC 写入，SQL 读出后可能缺少 Z；前端按 UTC 解析后再显示为本地时区。
  return `${trimmed.replace(' ', 'T')}Z`
}

/** 解析后端 UTC 时间戳（可能缺少时区后缀）；无效值返回 null。 */
export function parseUserUtcTimestamp(value?: string | null) {
  if (!value) {
    return null
  }

  const parsed = dayjs(normalizeUtcTimestamp(value))
  return parsed.isValid() ? parsed : null
}

export function formatUserLocalDateTime(value?: string | null, emptyValue = '--') {
  if (!value) {
    return emptyValue
  }

  const parsed = dayjs(normalizeUtcTimestamp(value))
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : value
}
