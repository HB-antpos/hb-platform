import type {
  ServiceApiTokenPurpose,
  ServiceApiTokenStatus,
} from '../../../types/serviceApiToken'

export function resolveServiceApiTokenApiBaseUrl(baseUrl: string, origin?: string) {
  const trimmed = baseUrl.trim()
  if (!trimmed) {
    return (origin ?? '').trim().replace(/\/+$/, '')
  }

  try {
    // 关键位置：Vite API base 常见配置是 /api，复制给移动端脚本前必须转成绝对 URL。
    const resolved = new URL(trimmed, origin || undefined)
    resolved.search = ''
    resolved.hash = ''
    return resolved.toString().replace(/\/+$/, '')
  } catch {
    return trimmed.replace(/\/+$/, '')
  }
}

export function buildServiceApiTokenEnvSnippet(
  baseUrl: string,
  token: string,
  purpose: ServiceApiTokenPurpose,
) {
  if (purpose === 'pos-ipad-update-decision-reader') {
    return `HBPOS_APP_UPDATE_DECISION_READ_TOKEN=${token.trim()}`
  }
  // 大屏收银 App 内订货助手：只写进 POS API 的环境变量，POS API 凭它为已登记设备换设备令牌。
  if (purpose === 'pos-supplier-assistant-token-issuer') {
    return `HBPOS_SUPPLIER_ASSISTANT_TOKEN=${token.trim()}`
  }

  const normalizedBaseUrl = baseUrl.trim().replace(/\/+$/, '')
  const reporterServiceUrl = (() => {
    try {
      return new URL(normalizedBaseUrl).origin
    } catch {
      return normalizedBaseUrl
    }
  })()
  if (purpose === 'quality-ci-reporter') {
    return [
      `QUALITY_BASELINE_SERVICE_URL=${reporterServiceUrl}`,
      `QUALITY_BASELINE_SERVICE_TOKEN=${token.trim()}`,
    ].join('\n')
  }
  if (purpose === 'deployment-acceptance-reporter') {
    return [
      `PERFORMANCE_SERVICE_URL=${reporterServiceUrl}`,
      `PERFORMANCE_SERVICE_TOKEN=${token.trim()}`,
    ].join('\n')
  }

  return [`HBWEB_API_BASE_URL=${normalizedBaseUrl}`, `HBWEB_API_TOKEN=${token.trim()}`].join('\n')
}

function normalizeServiceApiTokenFilterValue(value: unknown) {
  return String(value ?? '').trim().toLowerCase()
}

export function matchesServiceApiTokenTextFilter(value: unknown, filterValue: unknown) {
  const normalizedFilter = normalizeServiceApiTokenFilterValue(filterValue)
  if (!normalizedFilter) {
    return true
  }

  return normalizeServiceApiTokenFilterValue(value).includes(normalizedFilter)
}

export function matchesServiceApiTokenScopeFilter(
  scopes: readonly string[] | null | undefined,
  filterValue: unknown,
) {
  const expectedScope = String(filterValue ?? '').trim()
  if (!expectedScope) {
    return true
  }

  return (scopes ?? []).some((scope) => scope === expectedScope)
}

export function matchesServiceApiTokenStatusFilter(status: unknown, filterValue: unknown) {
  const expectedStatus = normalizeServiceApiTokenFilterValue(filterValue)
  if (!expectedStatus) {
    return true
  }

  return normalizeServiceApiTokenFilterValue(status) === expectedStatus
}

export function resolveServiceApiTokenStatusColor(status?: ServiceApiTokenStatus | null) {
  switch ((status ?? '').toLowerCase()) {
    case 'active':
      return 'green'
    case 'revoked':
      return 'red'
    case 'expired':
      return 'orange'
    default:
      return 'default'
  }
}

export function canRevokeServiceApiToken(status?: ServiceApiTokenStatus | null) {
  return (status ?? '').toLowerCase() === 'active'
}
