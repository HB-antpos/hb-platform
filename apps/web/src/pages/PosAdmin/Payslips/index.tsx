import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Empty, Input, Select, Skeleton, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getMyobPayslipCompanies, getMyobPayslips } from '../../../services/myobPayslipService'
import { useAuthStore } from '../../../store/auth'
import type {
  MyobPayslipCompany,
  MyobPayslipKey,
  MyobPayslipList,
  MyobPayslipRow,
} from '../../../types/myobPayslip'
import { extractMyobError, formatMyobTime } from '../Myob/myobLogic'

import PayslipDrawer, { PayslipStoreTags } from './PayslipDrawer'
import {
  PAYSLIP_DEFAULT_PRESET,
  PAYSLIP_PRESETS,
  allCompaniesFailed,
  classifyPayslipError,
  firstFailureKind,
  formatMoney,
  formatPayslipPeriod,
  matchPayslipPreset,
  mergePayslipStoreOptions,
  payslipRowKey,
  resolvePayslipPreset,
  sydneyToday,
  validatePayslipRange,
  type PayslipErrorKind,
  type PayslipPresetKey,
  type PayslipStoreOption,
} from './payslipLogic'
import messagesEn from './payslipMessages.en.json'
import messagesZh from './payslipMessages.zh.json'
import './payslips.css'

registerPageMessages({ zh: messagesZh, en: messagesEn })

const SEARCH_DEBOUNCE_MS = 300
const PAGE_SIZE = 20
const FORBIDDEN_CODE = 'MYOB_PAYSLIP_FORBIDDEN'
const MYOB_PAGE = '/pos-admin/myob'

type View = 'all' | 'unassigned'

/** 后端对「没有权限」的响应：HTTP 403（或错误码 MYOB_PAYSLIP_FORBIDDEN）。 */
function isForbidden(err: unknown): boolean {
  const status = err && typeof err === 'object' && 'status' in err ? (err as { status?: unknown }).status : undefined
  return status === 403 || extractMyobError(err, '').code === FORBIDDEN_CODE
}

/** 错误类型 → 文案键（静态映射，契约测试能核对每个键都存在）。 */
function failedMessageKey(kind: PayslipErrorKind) {
  switch (kind) {
    case 'reauth':
      return 'payslips.states.failed.reauth'
    case 'forbidden':
      return 'payslips.states.failed.forbidden'
    case 'unavailable':
      return 'payslips.states.failed.unavailable'
    default:
      return 'payslips.states.failed.other'
  }
}

function companyKindMessageKey(kind: PayslipErrorKind) {
  switch (kind) {
    case 'reauth':
      return 'payslips.companies.kinds.reauth'
    case 'forbidden':
      return 'payslips.companies.kinds.forbidden'
    case 'unavailable':
      return 'payslips.companies.kinds.unavailable'
    default:
      return 'payslips.companies.kinds.other'
  }
}

function presetLabelKey(preset: PayslipPresetKey) {
  switch (preset) {
    case 'last2Weeks':
      return 'payslips.filters.presets.last2Weeks'
    case 'last3Months':
      return 'payslips.filters.presets.last3Months'
    case 'thisFinancialYear':
      return 'payslips.filters.presets.thisFinancialYear'
    default:
      return 'payslips.filters.presets.lastFinancialYear'
  }
}

export default function PayslipsPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const access = useAuthStore((state) => state.access)
  const canViewAll = access.canViewAllMyobPayslips

  const [today] = useState(() => sydneyToday())
  const [range, setRange] = useState(() => resolvePayslipPreset(PAYSLIP_DEFAULT_PRESET, today))
  const [companyGuid, setCompanyGuid] = useState<string | undefined>(undefined)
  const [storeCode, setStoreCode] = useState<string | undefined>(undefined)
  const [keyword, setKeyword] = useState('')
  const [debouncedKeyword, setDebouncedKeyword] = useState('')
  const [view, setView] = useState<View>('all')
  const [companies, setCompanies] = useState<MyobPayslipCompany[]>([])
  const [storeOptions, setStoreOptions] = useState<PayslipStoreOption[]>([])
  const [data, setData] = useState<MyobPayslipList | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [forbidden, setForbidden] = useState(false)
  const [opened, setOpened] = useState<MyobPayslipKey | null>(null)
  const requestSeq = useRef(0)
  // 「全部」标签的张数：在未归属视图里数据只含未归属行，所以沿用最近一次全部视图的总数。
  const lastAllCount = useRef(0)

  const rangeState = validatePayslipRange(range.from, range.to)
  const activePreset = matchPayslipPreset(range.from, range.to, today)

  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedKeyword(keyword), SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [keyword])

  // 公司下拉：读不到不影响主流程（下拉为空，仍可看「全部公司」）。
  useEffect(() => {
    let cancelled = false
    getMyobPayslipCompanies()
      .then((items) => {
        if (!cancelled) {
          setCompanies(items)
        }
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [])

  const load = useCallback(
    async (refresh = false) => {
      if (rangeState !== 'ok') {
        setLoading(false)
        return
      }
      const seq = ++requestSeq.current
      setLoading(true)
      try {
        const result = await getMyobPayslips({
          connectionGuid: companyGuid,
          storeCode,
          from: range.from,
          to: range.to,
          keyword: debouncedKeyword,
          onlyUnassigned: view === 'unassigned',
          refresh,
        })
        if (seq === requestSeq.current) {
          if (view === 'all') {
            lastAllCount.current = result.totalCount
          }
          setData(result)
          setError(null)
          setForbidden(false)
          // 店长不能按管辖外的分店过滤：下拉只保留管辖分店；全部分店口径不限。
          setStoreOptions((current) =>
            mergePayslipStoreOptions(current, result.rows, result.canViewAllStores ? null : (access.managedStoreCodes() ?? [])),
          )
        }
      } catch (err) {
        if (seq === requestSeq.current) {
          setData(null)
          if (isForbidden(err)) {
            setForbidden(true)
            setError(null)
          } else {
            setError(extractMyobError(err, t('payslips.states.loadFailed')).message)
          }
        }
      } finally {
        if (seq === requestSeq.current) {
          setLoading(false)
        }
      }
    },
    [access, companyGuid, debouncedKeyword, range.from, range.to, rangeState, storeCode, t, view],
  )

  useEffect(() => {
    void load()
  }, [load])

  // 离开页面时让在途请求作废，并丢掉内存里的工资数据（组件卸载后 state 本就释放，这里防止卸载后回写）。
  useEffect(
    () => () => {
      requestSeq.current += 1
    },
    [],
  )

  const failedCompanies = useMemo(() => (data ? data.companies.filter((company) => !company.ok) : []), [data])
  const wholePageFailed = data !== null && allCompaniesFailed(data.companies)
  const unassignedView = view === 'unassigned'

  const clearFilters = () => {
    setCompanyGuid(undefined)
    setStoreCode(undefined)
    setKeyword('')
    setDebouncedKeyword('')
    setView('all')
    setRange(resolvePayslipPreset(PAYSLIP_DEFAULT_PRESET, today))
  }

  const openRow = (row: MyobPayslipRow) =>
    setOpened({
      connectionGuid: row.connectionGuid,
      employeeUid: row.employeeUid,
      paymentDate: row.paymentDate,
      periodStart: row.periodStart,
    })

  const columns: ColumnsType<MyobPayslipRow> = [
    {
      title: unassignedView ? t('payslips.columns.employeeMyob') : t('payslips.columns.employee'),
      key: 'employee',
      width: 190,
      render: (_, row) => (
        <>
          <span className="payslip-person-name">{row.employeeName || '—'}</span>
          {row.displayId ? <span className="payslip-mono">{row.displayId}</span> : null}
        </>
      ),
    },
    ...(unassignedView
      ? []
      : [
          {
            title: t('payslips.columns.stores'),
            key: 'stores',
            render: (_: unknown, row: MyobPayslipRow) => (
              <div className="payslip-store-tags">
                {/* 未绑定 / 无法确定分店的行没有分店标签，用状态标签占位，避免一格空白让人误以为漏了数据 */}
                {row.placement === 'Unbound' ? <Tag color="orange">{t('payslips.placement.Unbound')}</Tag> : null}
                {row.placement === 'Unknown' ? <Tag color="red">{t('payslips.placement.Unknown')}</Tag> : null}
                <PayslipStoreTags stores={row.stores} inferred={row.placement === 'Inferred'} />
              </div>
            ),
          },
        ]),
    {
      title: t('payslips.columns.company'),
      dataIndex: 'companyName',
      key: 'company',
      render: (value: string) => shortCompanyName(value),
    },
    { title: t('payslips.columns.paymentDate'), dataIndex: 'paymentDate', key: 'paymentDate', width: 120 },
    {
      title: t('payslips.columns.period'),
      key: 'period',
      width: 140,
      render: (_, row) => formatPayslipPeriod(row.periodStart, row.periodEnd),
    },
    {
      title: t('payslips.columns.gross'),
      key: 'gross',
      align: 'right',
      render: (_, row) => <span className="payslip-money">{formatMoney(row.grossPay)}</span>,
    },
    ...(unassignedView
      ? []
      : [
          {
            title: t('payslips.columns.tax'),
            key: 'tax',
            align: 'right' as const,
            render: (_: unknown, row: MyobPayslipRow) => <span className="payslip-money">{formatMoney(row.taxTotal)}</span>,
          },
        ]),
    {
      title: t('payslips.columns.net'),
      key: 'net',
      align: 'right',
      render: (_, row) => (
        <b className="payslip-money">{formatMoney(row.netPay)}</b>
      ),
    },
    ...(unassignedView
      ? [
          {
            title: t('payslips.columns.reason'),
            key: 'reason',
            width: 220,
            render: (_: unknown, row: MyobPayslipRow) =>
              row.placement === 'Unbound' ? (
                <>
                  <Tag color="orange">{t('payslips.placement.Unbound')}</Tag>
                  <span className="payslip-reason-hint">{t('payslips.placement.UnboundHint')}</span>
                </>
              ) : (
                <>
                  <Tag color="red">{t('payslips.placement.Unknown')}</Tag>
                  <span className="payslip-reason-hint">{t('payslips.placement.UnknownHint')}</span>
                </>
              ),
          },
        ]
      : []),
    {
      title: t('payslips.columns.actions'),
      key: 'actions',
      width: unassignedView ? 150 : 80,
      render: (_, row) => (
        <>
          {unassignedView && row.placement === 'Unbound' && access.canBindMyobEmployees ? (
            <Button
              type="link"
              size="small"
              aria-label={t('payslips.actions.goBindAria', { name: row.employeeName })}
              onClick={() => navigate(MYOB_PAGE)}
            >
              {t('payslips.actions.goBind')}
            </Button>
          ) : null}
          {unassignedView && row.placement === 'Unknown' && access.canEditAttendancePayroll ? (
            <Button type="link" size="small" onClick={() => navigate(MYOB_PAGE)}>
              {t('payslips.actions.goPayroll')}
            </Button>
          ) : null}
          <Button
            type="link"
            size="small"
            aria-label={t('payslips.actions.detailAria', { name: row.employeeName, date: row.paymentDate })}
            onClick={() => openRow(row)}
          >
            {t('payslips.actions.detail')}
          </Button>
        </>
      ),
    },
  ]

  // ───────────────────────── 整页状态 ─────────────────────────

  if (forbidden) {
    return (
      <PageContainer title={t('payslips.title')} subtitle={t('payslips.subtitle')}>
        <section className="payslip-card">
          <div className="payslip-state">
            <h3>{t('payslips.states.noPermission.title')}</h3>
            <p>{t('payslips.states.noPermission.description')}</p>
            <div className="payslip-state-actions">
              <Button onClick={() => navigate(-1)}>{t('payslips.states.noPermission.back')}</Button>
            </div>
          </div>
        </section>
      </PageContainer>
    )
  }

  const headerActions = (
    <div className="payslip-head-actions">
      {data?.fetchedAt ? <span className="payslip-fetched">{t('payslips.fetchedAt', { time: formatMyobTime(data.fetchedAt) })}</span> : null}
      <Button icon={<ReloadOutlined />} loading={loading} disabled={rangeState !== 'ok'} onClick={() => void load(true)}>
        {t('payslips.reload')}
      </Button>
    </div>
  )

  return (
    <PageContainer title={t('payslips.title')} subtitle={t('payslips.subtitle')}>
      <div className="payslip-page">
        <section className="payslip-card" aria-labelledby="payslip-title">
          <div className="payslip-card-head">
            <div>
              <h2 className="payslip-card-title" id="payslip-title">
                {unassignedView ? t('payslips.unassignedPage.title') : t('payslips.cardTitle')}
              </h2>
              <div className="payslip-card-hint">{unassignedView ? t('payslips.unassignedPage.hint') : t('payslips.cardHint')}</div>
            </div>
            {unassignedView ? (
              <Button onClick={() => setView('all')}>← {t('payslips.view.back')}</Button>
            ) : (
              headerActions
            )}
          </div>

          <div className="payslip-filters">
            <Select
              allowClear
              className="payslip-company-select"
              value={companyGuid}
              options={companies.map((company) => ({ value: company.connectionGuid, label: company.connectionName }))}
              placeholder={t('payslips.filters.allCompanies')}
              aria-label={t('payslips.filters.company')}
              onChange={(value) => setCompanyGuid(value ?? undefined)}
            />
            {unassignedView ? null : (
              <Select
                allowClear
                showSearch
                optionFilterProp="label"
                className="payslip-store-select"
                value={storeCode}
                options={storeOptions}
                placeholder={t('payslips.filters.allStores')}
                aria-label={t('payslips.filters.store')}
                onChange={(value) => setStoreCode(value ?? undefined)}
              />
            )}
            <DatePicker.RangePicker
              className="payslip-range"
              allowClear={false}
              value={[dayjs(range.from), dayjs(range.to)]}
              placeholder={[t('payslips.filters.dateFrom'), t('payslips.filters.dateTo')]}
              aria-label={t('payslips.filters.dateRange')}
              disabledDate={(date) => date.isAfter(dayjs(today), 'day')}
              status={rangeState === 'ok' ? undefined : 'error'}
              onChange={(values) => {
                if (values?.[0] && values[1]) {
                  setRange({ from: values[0].format('YYYY-MM-DD'), to: values[1].format('YYYY-MM-DD') })
                }
              }}
            />
            <div className="payslip-presets" role="group" aria-label={t('payslips.filters.presetAria')}>
              {PAYSLIP_PRESETS.map((preset) => (
                <Tag.CheckableTag key={preset} checked={activePreset === preset} onChange={() => setRange(resolvePayslipPreset(preset, today))}>
                  {t(presetLabelKey(preset))}
                </Tag.CheckableTag>
              ))}
            </div>
            {canViewAll && data && !wholePageFailed ? (
              <div className="payslip-views" role="group" aria-label={t('payslips.view.aria')}>
                <Tag.CheckableTag checked={view === 'all'} onChange={() => setView('all')}>
                  {t('payslips.view.all', { count: unassignedView ? lastAllCount.current : data.totalCount })}
                </Tag.CheckableTag>
                <Tag.CheckableTag checked={unassignedView} onChange={() => setView('unassigned')}>
                  {t('payslips.view.unassigned', { count: data.unassignedCount })}
                </Tag.CheckableTag>
              </div>
            ) : null}
            <span className="payslip-filter-spacer" />
            <Input
              allowClear
              className="payslip-search"
              prefix={<SearchOutlined />}
              value={keyword}
              placeholder={t('payslips.filters.search')}
              aria-label={t('payslips.filters.search')}
              onChange={(event) => setKeyword(event.target.value)}
            />
          </div>

          {rangeState !== 'ok' ? (
            <Alert
              className="payslip-alert"
              type="warning"
              showIcon
              message={
                rangeState === 'reversed'
                  ? t('payslips.filters.rangeError.reversed')
                  : rangeState === 'tooLong'
                    ? t('payslips.filters.rangeError.tooLong')
                    : t('payslips.filters.rangeError.incomplete')
              }
            />
          ) : null}

          {error ? (
            <Alert
              className="payslip-alert"
              type="error"
              showIcon
              message={t('payslips.states.loadFailed')}
              description={error}
              action={
                <Button size="small" onClick={() => void load(true)}>
                  {t('payslips.retry')}
                </Button>
              }
            />
          ) : null}

          {loading && !data ? <Skeleton active paragraph={{ rows: 8 }} /> : null}

          {data && wholePageFailed ? (
            <div className="payslip-state">
              <h3>{t('payslips.states.failed.title')}</h3>
              <p>{t(failedMessageKey(firstFailureKind(data.companies)))}</p>
              <div className="payslip-state-actions">
                <Button type="primary" loading={loading} onClick={() => void load(true)}>
                  {t('payslips.states.failed.retry')}
                </Button>
                {access.canAccessMyob ? <Button onClick={() => navigate(MYOB_PAGE)}>{t('payslips.states.failed.goConnect')}</Button> : null}
              </div>
            </div>
          ) : null}

          {data && !wholePageFailed ? (
            <>
              {failedCompanies.map((company) => (
                <Alert
                  key={company.connectionGuid}
                  className="payslip-alert"
                  type="error"
                  showIcon
                  message={t('payslips.companies.failedTitle', { name: company.connectionName })}
                  description={
                    <>
                      {t(companyKindMessageKey(classifyPayslipError(company.errorCode, company.errorMessage)))}
                      {classifyPayslipError(company.errorCode, company.errorMessage) === 'reauth' && access.canManageMyobConnections ? (
                        <>
                          {' '}
                          <Button type="link" size="small" onClick={() => navigate(MYOB_PAGE)}>
                            {t('payslips.companies.goConnect')}
                          </Button>
                        </>
                      ) : null}
                    </>
                  }
                />
              ))}

              {unassignedView ? (
                <Alert
                  className="payslip-alert"
                  type="warning"
                  showIcon
                  message={
                    <>
                      <b>{t('payslips.unassignedPage.howToTitle')}</b>
                      {t('payslips.unassignedPage.howTo')}
                    </>
                  }
                />
              ) : (
                <Alert
                  className="payslip-alert"
                  type="info"
                  showIcon
                  message={
                    <>
                      <b>{t('payslips.notice.title')}</b>
                      {t('payslips.notice.body')}
                    </>
                  }
                />
              )}

              <div className="payslip-summary" aria-label={t('payslips.summary.scope')}>
                <div className="payslip-summary-item">
                  <span>{t('payslips.summary.count')}</span>
                  <b>{t('payslips.summary.unitCount', { count: data.totalCount })}</b>
                </div>
                <div className="payslip-summary-item">
                  <span>{t('payslips.summary.employees')}</span>
                  <b>{t('payslips.summary.unitPeople', { count: data.employeeCount })}</b>
                </div>
                <div className="payslip-summary-item">
                  <span>{t('payslips.summary.gross')}</span>
                  <b>{formatMoney(data.grossTotal)}</b>
                </div>
                <div className="payslip-summary-item">
                  <span>{t('payslips.summary.net')}</span>
                  <b>{formatMoney(data.netTotal)}</b>
                </div>
                <span className="payslip-summary-scope">{t('payslips.summary.scope')}</span>
              </div>

              <MeasuredTable<MyobPayslipRow>
                metricId="pos-admin.myob.payslips"
                rowKey={payslipRowKey}
                size="middle"
                columns={columns}
                dataSource={data.rows}
                loading={loading}
                scroll={{ x: 'max-content' }}
                pagination={{
                  pageSize: PAGE_SIZE,
                  showSizeChanger: false,
                  hideOnSinglePage: true,
                  showTotal: (total) => t('payslips.total', { total }),
                }}
                locale={{
                  emptyText: (
                    <div className="payslip-state">
                      <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={false} />
                      <h3>{t('payslips.states.empty.title')}</h3>
                      <p>{t('payslips.states.empty.description')}</p>
                      <div className="payslip-state-actions">
                        <Button type="primary" onClick={clearFilters}>
                          {t('payslips.states.empty.clear')}
                        </Button>
                        {access.canBindMyobEmployees ? (
                          <Button onClick={() => navigate(MYOB_PAGE)}>{t('payslips.states.empty.goBind')}</Button>
                        ) : null}
                      </div>
                    </div>
                  ),
                }}
              />

              {!unassignedView && canViewAll && data.unassignedCount > 0 ? (
                <Alert
                  className="payslip-alert"
                  style={{ marginTop: 12 }}
                  type="warning"
                  showIcon
                  message={
                    <>
                      <b>{t('payslips.unassignedBar.message', { count: data.unassignedCount })}</b>
                      {t('payslips.unassignedBar.detail')}{' '}
                      <Button type="link" size="small" style={{ padding: 0 }} onClick={() => setView('unassigned')}>
                        {t('payslips.unassignedBar.link')}
                      </Button>
                    </>
                  }
                />
              ) : null}
            </>
          ) : null}
        </section>
      </div>

      <PayslipDrawer target={opened} onClose={() => setOpened(null)} />
    </PageContainer>
  )
}

/** 公司全称太长，列表里去掉 Pty Ltd 后缀与「Hot Bargain」前缀会丢信息，所以只去掉 Pty Ltd。 */
function shortCompanyName(name: string): string {
  return name.replace(/\s+Pty\.?\s+Ltd\.?$/i, '') || '—'
}
