import { Segmented } from 'antd'
import { lazy, Suspense, useEffect, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'

import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { useAuthStore } from '../../../store/auth'
import legacyEmployeeLogsMessagesEn from '../LegacyEmployeeLogs/legacyEmployeeLogsMessages.en.json'
import legacyEmployeeLogsMessagesZh from '../LegacyEmployeeLogs/legacyEmployeeLogsMessages.zh.json'

import {
  EMPLOYEE_LOG_SOURCE_PARAM,
  EMPLOYEE_LOG_SOURCE_STORAGE_KEY,
  resolveEmployeeLogSource,
  type EmployeeLogSource,
} from './employeeLogsSource'

// 两个来源共用风险、核查文案（规则编号相同）。
registerPageMessages({ zh: legacyEmployeeLogsMessagesZh, en: legacyEmployeeLogsMessagesEn })

const LegacyEmployeeLogsPage = lazy(() => import('../LegacyEmployeeLogs'))
const PosOperationLogsPage = lazy(() => import('../OperationLogs'))

function readRememberedSource() {
  try {
    return localStorage.getItem(EMPLOYEE_LOG_SOURCE_STORAGE_KEY)
  } catch {
    return null
  }
}

function rememberSource(source: EmployeeLogSource) {
  try {
    localStorage.setItem(EMPLOYEE_LOG_SOURCE_STORAGE_KEY, source)
  } catch {
    // 浏览器存储不可用时只是不记住上次的来源。
  }
}

/**
 * 员工操作日志合并页：顶部切换老收银 / 新收银，两个来源各自保留筛选与表格。
 * 只显示当前账号有权限的来源；只有一个来源时不显示切换。来源写进地址栏，便于分享与刷新后保持。
 */
export default function PosAdminEmployeeLogsPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const [searchParams, setSearchParams] = useSearchParams()
  const canLegacy = access.canViewLegacyEmployeeLogs
  const canPos = access.canViewOperationAudits
  const source = resolveEmployeeLogSource({
    requested: searchParams.get(EMPLOYEE_LOG_SOURCE_PARAM),
    remembered: readRememberedSource(),
    canLegacy,
    canPos,
  })

  useEffect(() => {
    if (source) rememberSource(source)
  }, [source])

  const switchSource = (next: EmployeeLogSource) => {
    const params = new URLSearchParams(searchParams)
    params.set(EMPLOYEE_LOG_SOURCE_PARAM, next)
    setSearchParams(params, { replace: true })
  }

  const switcher = useMemo(
    () => (canLegacy && canPos && source ? (
      <Segmented<EmployeeLogSource>
        aria-label={t('legacyEmployeeLogs.source.switchLabel')}
        value={source}
        options={[
          { value: 'legacy', label: t('legacyEmployeeLogs.source.legacy') },
          { value: 'pos', label: t('legacyEmployeeLogs.source.pos') },
        ]}
        onChange={switchSource}
      />
    ) : undefined),
    // switchSource 依赖当前地址栏参数，随 searchParams 变化重建即可。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [canLegacy, canPos, searchParams, source, t],
  )

  if (!source) return null
  const header = {
    title: t('legacyEmployeeLogs.source.title'),
    subtitle: t(source === 'legacy' ? 'legacyEmployeeLogs.source.legacySubtitle' : 'legacyEmployeeLogs.source.posSubtitle'),
    switcher,
  }

  return (
    <Suspense fallback={null}>
      {source === 'legacy' ? <LegacyEmployeeLogsPage header={header} /> : <PosOperationLogsPage header={header} />}
    </Suspense>
  )
}
