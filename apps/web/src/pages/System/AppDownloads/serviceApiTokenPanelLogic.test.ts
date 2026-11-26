import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import {
  buildServiceApiTokenEnvSnippet,
  canRevokeServiceApiToken,
  matchesServiceApiTokenScopeFilter,
  matchesServiceApiTokenStatusFilter,
  matchesServiceApiTokenTextFilter,
  resolveServiceApiTokenApiBaseUrl,
  resolveServiceApiTokenStatusColor,
} from './serviceApiTokenPanelLogic'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}: expected ${expectedJson}, got ${actualJson}`)
  }
}

const envSnippet = buildServiceApiTokenEnvSnippet(
  'https://hotbargain.vip/api/',
  ' hbsvc_test_token ',
  'mobile-ota-publisher',
)

assertEqual(
  envSnippet,
  'HBWEB_API_BASE_URL=https://hotbargain.vip/api\nHBWEB_API_TOKEN=hbsvc_test_token',
  '环境变量片段应保留既有 OTA 脚本协议并清理空白',
)
assertEqual(
  buildServiceApiTokenEnvSnippet(
    'https://hotbargain.vip/api/',
    ' hbsvc_reader_token ',
    'pos-ipad-update-decision-reader',
  ),
  'HBPOS_APP_UPDATE_DECISION_READ_TOKEN=hbsvc_reader_token',
  'iPad 决策读取 Token 必须输出 POS API 专用环境变量且不得混入发布凭据',
)
assertEqual(
  buildServiceApiTokenEnvSnippet(
    'https://hotbargain.vip/api/',
    ' hbsvc_assistant_token ',
    'pos-supplier-assistant-token-issuer',
  ),
  'HBPOS_SUPPLIER_ASSISTANT_TOKEN=hbsvc_assistant_token',
  '订货助手设备令牌签发器只输出 POS API 专用环境变量，不得混入中心地址或其他凭据',
)
assertEqual(
  buildServiceApiTokenEnvSnippet(
    'https://hotbargain.vip/api/',
    ' hbsvc_quality_token ',
    'quality-ci-reporter',
  ),
  'QUALITY_BASELINE_SERVICE_URL=https://hotbargain.vip\nQUALITY_BASELINE_SERVICE_TOKEN=hbsvc_quality_token',
  '质量 CI Reporter 必须复制脚本约定的 origin 与专用 Token 环境变量',
)
assertEqual(
  buildServiceApiTokenEnvSnippet(
    'https://hotbargain.vip/api/',
    ' hbsvc_deployment_token ',
    'deployment-acceptance-reporter',
  ),
  'PERFORMANCE_SERVICE_URL=https://hotbargain.vip\nPERFORMANCE_SERVICE_TOKEN=hbsvc_deployment_token',
  '部署验收 Reporter 必须复制发布事件脚本约定的 origin 与专用 Token 环境变量',
)
assertEqual(
  resolveServiceApiTokenApiBaseUrl('/api', 'https://hotbargain.vip/system/app-downloads'),
  'https://hotbargain.vip/api',
  '相对 API base 应按当前站点 origin 转成移动端脚本可用的绝对 URL',
)
assertEqual(
  resolveServiceApiTokenApiBaseUrl('', 'https://hotbargain.vip/'),
  'https://hotbargain.vip',
  '空 API base 应回退到当前站点 origin',
)
assertEqual(
  resolveServiceApiTokenApiBaseUrl('https://api.hotbargain.vip/api/', 'https://hotbargain.vip'),
  'https://api.hotbargain.vip/api',
  '绝对 API base 应保留并清理末尾斜杠',
)
assertEqual(resolveServiceApiTokenStatusColor('active'), 'green', 'active 状态应显示绿色')
assertEqual(resolveServiceApiTokenStatusColor('revoked'), 'red', 'revoked 状态应显示红色')
assertEqual(resolveServiceApiTokenStatusColor('expired'), 'orange', 'expired 状态应显示橙色')
assertEqual(canRevokeServiceApiToken('active'), true, '仅 active token 可撤销')
assertEqual(canRevokeServiceApiToken('revoked'), false, '已撤销 token 不再显示撤销动作')
assertEqual(
  matchesServiceApiTokenTextFilter('Mobile OTA Publisher', ' ota '),
  true,
  '文本筛选应忽略输入首尾空白与大小写',
)
assertEqual(
  matchesServiceApiTokenTextFilter('hbsvc_kpeC_zuI-Y6S', 'PEC_ZUI'),
  true,
  'Token 前缀应支持大小写不敏感的中间片段匹配',
)
assertEqual(
  matchesServiceApiTokenTextFilter('Mobile OTA Publisher', ''),
  true,
  '空文本筛选应视为未筛选',
)
assertEqual(
  matchesServiceApiTokenTextFilter('Mobile OTA Publisher', 'quality'),
  false,
  '无关文本不得误命中',
)
assertEqual(
  matchesServiceApiTokenScopeFilter(
    ['System.ManageAppDownloads', 'Service.WriteReleaseEvents'],
    'Service.WriteReleaseEvents',
  ),
  true,
  'Scope 筛选应按数组完整成员匹配',
)
assertEqual(
  matchesServiceApiTokenScopeFilter(
    ['System.ManageAppDownloads'],
    'ManageAppDownloads',
  ),
  false,
  'Scope 筛选不得按部分字符串误命中',
)
assertEqual(
  matchesServiceApiTokenStatusFilter('ACTIVE', 'active'),
  true,
  '状态筛选应在归一化大小写后精确匹配',
)
assertEqual(
  matchesServiceApiTokenStatusFilter('active', 'revoked'),
  false,
  '不同状态不得误命中',
)

const filterFixture = [
  {
    id: 'mobile-active',
    name: 'Mobile OTA Publisher',
    tokenPrefix: 'hbsvc_mobile',
    scopes: ['System.ManageAppDownloads'],
    status: 'active',
  },
  {
    id: 'deployment-revoked',
    name: 'Deployment Acceptance Reporter',
    tokenPrefix: 'hbsvc_deployment',
    scopes: ['Service.WriteReleaseEvents'],
    status: 'revoked',
  },
  {
    id: 'quality-expired',
    name: 'Quality CI Reporter',
    tokenPrefix: 'hbsvc_quality',
    scopes: ['Service.WritePerformanceMetrics'],
    status: 'expired',
  },
]
const selectedScopes = ['System.ManageAppDownloads', 'Service.WriteReleaseEvents']
const selectedStatuses = ['active', 'revoked']
const combinedMatches = filterFixture
  .filter(
    (token) =>
      matchesServiceApiTokenTextFilter(token.name, 'reporter') &&
      selectedScopes.some((scope) =>
        matchesServiceApiTokenScopeFilter(token.scopes, scope),
      ) &&
      selectedStatuses.some((status) =>
        matchesServiceApiTokenStatusFilter(token.status, status),
      ),
  )
  .map((token) => token.id)
assertDeepEqual(
  combinedMatches,
  ['deployment-revoked'],
  '同列多选应为 OR，不同列筛选应为 AND',
)

const panelSource = readFileSync(join(process.cwd(), 'src/pages/System/AppDownloads/ServiceApiTokensPanel.tsx'), 'utf8')
const typeSource = readFileSync(join(process.cwd(), 'src/types/serviceApiToken.ts'), 'utf8')
const zh = JSON.parse(readFileSync(join(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8'))
const en = JSON.parse(readFileSync(join(process.cwd(), 'src/i18n/locales/en.json'), 'utf8'))

for (const filterKey of ['name', 'tokenPrefix', 'scopes', 'status']) {
  assertEqual(
    panelSource.includes(`filteredValue: columnFilters.${filterKey}`),
    true,
    `${filterKey} 列必须接入受控筛选值`,
  )
}
assertEqual(
  panelSource.includes('matchesServiceApiTokenTextFilter(record.name'),
  true,
  '名称列必须接入文本包含筛选',
)
assertEqual(
  panelSource.includes('matchesServiceApiTokenTextFilter(record.tokenPrefix'),
  true,
  'Token 前缀列必须仅用脱敏前缀接入文本筛选',
)
assertEqual(
  panelSource.includes('matchesServiceApiTokenScopeFilter(record.scopes'),
  true,
  'Scopes 列必须接入完整成员筛选',
)
assertEqual(
  panelSource.includes('matchesServiceApiTokenStatusFilter(record.status'),
  true,
  '状态列必须接入精确状态筛选',
)
assertEqual(
  panelSource.includes('onChange={handleTableChange}'),
  true,
  '表格必须接管筛选和分页变化',
)
assertEqual(
  panelSource.includes('current: currentPage'),
  true,
  '表格必须使用受控当前页以便筛选后回到第一页',
)
assertEqual(
  panelSource.includes('clearFilters({ confirm: true, closeDropdown: true })'),
  true,
  '文本筛选重置必须确认清空受控筛选并关闭下拉框',
)

for (const purpose of ['quality-ci-reporter', 'deployment-acceptance-reporter']) {
  assertEqual(typeSource.includes(`| '${purpose}'`), true, `${purpose} 必须加入前端 purpose 类型白名单`)
  assertEqual(panelSource.includes(`value: '${purpose}'`), true, `${purpose} 必须出现在管理员签发下拉框`)
  assertEqual(
    Boolean(zh.system?.appDownloads?.serviceTokens?.purposes?.[purpose]?.label) &&
      Boolean(zh.system?.appDownloads?.serviceTokens?.purposes?.[purpose]?.description),
    true,
    `${purpose} 必须提供中文标签和用途说明`,
  )
  assertEqual(
    Boolean(en.system?.appDownloads?.serviceTokens?.purposes?.[purpose]?.label) &&
      Boolean(en.system?.appDownloads?.serviceTokens?.purposes?.[purpose]?.description),
    true,
    `${purpose} 必须提供英文标签和用途说明`,
  )
}

console.log('serviceApiTokenPanelLogic.test.ts: ok')
