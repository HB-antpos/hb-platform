import { DownloadOutlined, HistoryOutlined, ReloadOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Empty, Grid, Select, Skeleton, Tag, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getMyobTimesheetPreview } from '../../../services/myobService'
import type { MyobCompanyOption, MyobPreviewRow, MyobTimesheetPreview } from '../../../types/myob'
import { formatAbnDisplay, extractMyobError, formatMyobTime } from './myobLogic'
import PushBatchesDrawer from './PushBatchesDrawer'
import PushConfirmModal from './PushConfirmModal'
import PushDetailDrawer from './PushDetailDrawer'
import { canOpenPushConfirm, getPushableUserGuids, getPushStateView, isPushableRow, reconcileSelection } from './myobPushLogic'
import {
  buildPeriodOptions,
  buildPreviewCsv,
  buildPreviewCsvFileName,
  buildReadinessCards,
  formatHours,
  formatHoursCell,
  formatPeriodRange,
  getPayModeTag,
  getPeriodWeeks,
  getRowClassName,
  getRowNotes,
  getRowStatusView,
  groupHints,
  splitHintMessage,
  type ReadinessCard,
} from './myobTimesheetLogic'

const PAGE_SIZE = 50

// 就绪卡状态的读屏文字（键写成字面量，供页面文案契约测试扫描）
const READINESS_STATE_KEYS: Record<ReadinessCard['tone'], string> = {
  ok: 'myob.push.readiness.state.ok',
  bad: 'myob.push.readiness.state.bad',
  warn: 'myob.push.readiness.state.warn',
  muted: 'myob.push.readiness.state.muted',
}

// 6 个口径的工时列（键写成字面量，供页面文案契约测试扫描）
const HOUR_COLUMNS = [
  { key: 'weekday', titleKey: 'myob.push.columns.weekday' },
  { key: 'saturday', titleKey: 'myob.push.columns.saturday' },
  { key: 'sunday', titleKey: 'myob.push.columns.sunday' },
  { key: 'publicHoliday', titleKey: 'myob.push.columns.publicHoliday' },
  { key: 'personalLeave', titleKey: 'myob.push.columns.personalLeave' },
  { key: 'holidayLeave', titleKey: 'myob.push.columns.holidayLeave' },
] as const

interface PushPreviewTabProps {
  /** 可选公司文件（GET /myob/companies，只含 Active）；只读。 */
  companies: MyobCompanyOption[]
  companiesLoading: boolean
  companiesError: string | null
  onReloadCompanies: () => void
  /** 当前是否正显示本页签：切回来时重新读取（映射 / 绑定可能刚在别的页签改过）。 */
  active: boolean
  /** 有员工绑定权限才显示「去绑定」。 */
  canGoBind: boolean
  /** 有连接管理权限才显示「去映射」。 */
  canGoMapping: boolean
  /** 有 Myob.Timesheet.Push 才能挑人推送 / 看推送记录 / 撤销（预览只需编辑工资）。 */
  canPush: boolean
  onGoBind: (connectionGuid: string) => void
  onGoMapping: (connectionGuid: string) => void
}

interface LoadOptions {
  periodStart: string | null
  checkMyob: boolean
  /** 切公司 / 首次读取：先清空旧数据；否则保留旧表格并只显示加载中。 */
  reset?: boolean
}

/** 把文本存成文件下载（CSV）；生成的 blob 地址点击后立即释放。 */
function downloadTextFile(fileName: string, content: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'text/csv;charset=utf-8' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}

export default function PushPreviewTab({
  companies,
  companiesLoading,
  companiesError,
  onReloadCompanies,
  active,
  canGoBind,
  canGoMapping,
  canPush,
  onGoBind,
  onGoMapping,
}: PushPreviewTabProps) {
  const { t } = useTranslation()
  // 窄屏（<768px）整表横向滚动，不固定列
  const wide = Grid.useBreakpoint().md !== false

  const [pickedGuid, setPickedGuid] = useState<string | null>(null)
  const selectedGuid = useMemo(
    () => (pickedGuid && companies.some((item) => item.connectionGuid === pickedGuid) ? pickedGuid : (companies[0]?.connectionGuid ?? null)),
    [companies, pickedGuid],
  )
  const [preview, setPreview] = useState<MyobTimesheetPreview | null>(null)
  const [loading, setLoading] = useState(false)
  const [checking, setChecking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [acknowledged, setAcknowledged] = useState(false)
  const [detailUserGuid, setDetailUserGuid] = useState<string | null>(null)
  // 挑人推送：默认选中所有可推送的人；预览刷新后把已选收敛到仍然可推送的人
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [batchesOpen, setBatchesOpen] = useState(false)
  const requestSeq = useRef(0)
  // 用户选的周期（null = 后端默认的上一个已结束周期）；用 ref 保证公司切换 / 重新激活时读到最新值
  const periodRef = useRef<string | null>(null)

  const load = useCallback(
    async (guid: string, options: LoadOptions) => {
      const seq = ++requestSeq.current
      setLoading(true)
      setChecking(options.checkMyob)
      if (options.reset) {
        setPreview(null)
      }
      try {
        const data = await getMyobTimesheetPreview(guid, { periodStart: options.periodStart, checkMyob: options.checkMyob })
        if (seq === requestSeq.current) {
          setPreview(data)
          setError(null)
          // 数据变了，「我已知晓」要重新勾；选择回到「全部可推送的人」
          setAcknowledged(false)
          setSelected(new Set(getPushableUserGuids(data)))
        }
      } catch (err) {
        if (seq === requestSeq.current) {
          setError(extractMyobError(err, t('myob.push.loadFailed')).message)
        }
      } finally {
        if (seq === requestSeq.current) {
          setLoading(false)
          setChecking(false)
        }
      }
    },
    [t],
  )

  // 首次进入 / 切换公司：清空旧数据后按当前周期重新读取（默认不检查 MYOB，那一步慢）
  useEffect(() => {
    setError(null)
    setDetailUserGuid(null)
    setAcknowledged(false)
    setSelected(new Set())
    setConfirmOpen(false)
    setBatchesOpen(false)
    if (selectedGuid) {
      void load(selectedGuid, { periodStart: periodRef.current, checkMyob: false, reset: true })
    } else {
      requestSeq.current += 1
      setPreview(null)
      setLoading(false)
      setChecking(false)
    }
  }, [selectedGuid, load])

  // 从别的页签切回来：映射 / 绑定 / 锁定状态可能已变，静默重新读取（保留旧表格，不清空）
  const wasActive = useRef(active)
  useEffect(() => {
    if (active && !wasActive.current && selectedGuid) {
      void load(selectedGuid, { periodStart: periodRef.current, checkMyob: false })
    }
    wasActive.current = active
  }, [active, selectedGuid, load])

  const handlePeriodChange = (value: string) => {
    periodRef.current = value
    if (selectedGuid) {
      setDetailUserGuid(null)
      void load(selectedGuid, { periodStart: value, checkMyob: false })
    }
  }

  const reloadPreview = () => {
    if (selectedGuid) {
      void load(selectedGuid, { periodStart: preview?.periodStart || periodRef.current, checkMyob: false })
    }
  }

  const checkMyobStatus = () => {
    if (selectedGuid) {
      void load(selectedGuid, { periodStart: preview?.periodStart || periodRef.current, checkMyob: true })
    }
  }

  const handleExport = () => {
    if (!preview) {
      return
    }
    const csv = buildPreviewCsv(preview, (key, values) => t(key, values) as string)
    downloadTextFile(buildPreviewCsvFileName(preview.connectionName, preview.periodStart), csv)
  }

  // ───────────────────────── 渲染 ─────────────────────────

  const rows = preview?.rows ?? []
  const detailRow = rows.find((row) => row.userGuid === detailUserGuid) ?? null
  const periodOptions = buildPeriodOptions(preview).map((period) => ({
    value: period.start,
    label: `${formatPeriodRange(period.start, period.end)}${t('myob.push.weeksSuffix', { count: getPeriodWeeks(period) })}`,
  }))
  const hintGroups = useMemo(() => groupHints(preview?.hints ?? []), [preview])
  const readinessCards = useMemo(() => (preview ? buildReadinessCards(preview) : []), [preview])
  const checkedAlready = preview?.readiness.myobChecked === true
  const effectiveSelected = useMemo(() => reconcileSelection(preview, selected), [preview, selected])
  const selectedCount = effectiveSelected.size
  const pushAllowed = canOpenPushConfirm(preview, canPush, selectedCount, acknowledged)
  const pushableGuids = useMemo(() => getPushableUserGuids(preview), [preview])
  // 推送 / 撤销完成后：按当前周期重新读取（保留「已检查 MYOB」状态，推送状态列和 MYOB 现状立刻更新）
  const refreshAfterPush = () => {
    if (selectedGuid) {
      void load(selectedGuid, { periodStart: preview?.periodStart || periodRef.current, checkMyob: checkedAlready })
    }
  }

  const columns: ColumnsType<MyobPreviewRow> = [
    {
      title: t('myob.push.columns.employee'),
      key: 'employee',
      width: 280,
      render: (_, row) => {
        const tag = getPayModeTag(row)
        const notes = getRowNotes(row)
        return (
          <>
            <span className="myob-person-name">{row.userName}</span>
            <span className="myob-person-tags">
              {tag ? <Tag>{t(tag.labelKey, { hours: tag.hours ?? '' })}</Tag> : null}
              {row.isCrossCompany ? <Tag color="blue">{t('myob.push.cross')}</Tag> : null}
            </span>
            {notes.map((note) => (
              <span key={note.key} className={note.warn ? 'myob-status-note myob-push-note-warn' : 'myob-status-note'}>
                {t(note.key, note.values)}
              </span>
            ))}
          </>
        )
      },
    },
    {
      title: t('myob.push.columns.stores'),
      key: 'stores',
      width: 150,
      render: (_, row) =>
        row.storeLabels.length > 0 ? (
          row.storeLabels.map((label) => <div key={label}>{label}</div>)
        ) : (
          <span className="myob-muted">—</span>
        ),
    },
    ...HOUR_COLUMNS.map(({ key, titleKey }) => ({
      title: t(titleKey),
      key,
      width: 84,
      align: 'right' as const,
      render: (_: unknown, row: MyobPreviewRow) => <HoursCell value={row.pushed[key]} />,
    })),
    {
      title: t('myob.push.columns.pushedTotal'),
      key: 'pushedTotal',
      width: 104,
      align: 'right' as const,
      render: (_, row) => (
        <b className={`myob-push-hours${Math.abs(row.pushedTotal) < 0.005 ? ' myob-muted' : ''}`}>{formatHoursCell(row.pushedTotal)}</b>
      ),
    },
    {
      title: t('myob.push.columns.pushState'),
      key: 'pushState',
      width: 150,
      render: (_, row) => {
        // 被拦截 / 没有可推送工时的行，没有记录就不占位
        if (row.push.state === 'NotPushed' && row.push.lastStatus === null) {
          return <span className="myob-muted">—</span>
        }
        const view = getPushStateView(row.push)
        const tag = (
          <Tag color={view.tone} style={{ marginInlineEnd: 0 }} tabIndex={view.tipKey ? 0 : undefined}>
            {t(view.labelKey)}
          </Tag>
        )
        return (
          <div className="myob-push-status">
            {view.tipKey ? <Tooltip title={t(view.tipKey)}>{tag}</Tooltip> : tag}
            {view.lastAt ? <span className="myob-sub">{formatMyobTime(view.lastAt)}</span> : null}
          </div>
        )
      },
    },
    {
      title: t('myob.push.columns.status'),
      key: 'status',
      width: 230,
      fixed: wide ? ('right' as const) : undefined,
      render: (_, row) => {
        const view = getRowStatusView(row)
        const tag = (
          <Tag
            color={view.tone}
            style={{ marginInlineEnd: 0 }}
            tabIndex={view.detail ? 0 : undefined}
            aria-label={view.detail ? `${t(view.labelKey)}：${view.detail}` : undefined}
          >
            {t(view.labelKey)}
          </Tag>
        )
        return (
          <div className="myob-push-status">
            {view.detail ? <Tooltip title={<span style={{ whiteSpace: 'pre-line' }}>{view.detail}</span>}>{tag}</Tooltip> : tag}
            {view.extraBlockers > 0 ? <span className="myob-sub myob-push-extra">{t('myob.push.moreBlockers', { count: view.extraBlockers })}</span> : null}
            {view.blockerCode === 'NOT_BOUND' && canGoBind && selectedGuid ? (
              <Button type="link" size="small" aria-label={t('myob.push.goBindAria', { name: row.userName })} onClick={() => onGoBind(selectedGuid)}>
                {t('myob.push.goBind')}
              </Button>
            ) : null}
            {row.detail.days.length > 0 ? (
              <Button type="link" size="small" aria-label={t('myob.push.detailAria', { name: row.userName })} onClick={() => setDetailUserGuid(row.userGuid)}>
                {t('myob.push.detail')}
              </Button>
            ) : null}
          </div>
        )
      },
    },
  ]

  const companyBar = (
    <div className="myob-companies myob-push-companies" role="group" aria-label={t('myob.push.company.aria')}>
      {companies.map((item) => {
        const on = item.connectionGuid === selectedGuid
        return (
          <button
            key={item.connectionGuid}
            type="button"
            aria-pressed={on}
            aria-label={t('myob.employees.company.select', { name: item.name })}
            className={`myob-company${on ? ' myob-company-on' : ''}`}
            onClick={() => setPickedGuid(item.connectionGuid)}
          >
            <span className="myob-company-icon" aria-hidden="true">
              {item.name.trim().charAt(0).toUpperCase() || '?'}
            </span>
            <span className="myob-company-body">
              <span className="myob-company-name" title={item.name}>
                {item.name}
              </span>
              <span className="myob-sub">
                {on && preview
                  ? t('myob.push.company.count', { count: preview.rows.length })
                  : item.abn
                    ? t('myob.employees.company.abn', { abn: formatAbnDisplay(item.abn) })
                    : t('myob.employees.company.noAbn')}
              </span>
            </span>
          </button>
        )
      })}
    </div>
  )

  return (
    <div className="myob-tabpane">
      <section className="myob-card" aria-labelledby="myob-push-title">
        <div className="myob-card-head">
          <div>
            <h2 className="myob-card-title" id="myob-push-title">
              {t('myob.push.title')}
            </h2>
            <div className="myob-card-hint">{t('myob.push.hint')}</div>
          </div>
          <div className="myob-push-period">
            <label htmlFor="myob-push-period-select" className="myob-muted">
              {t('myob.push.period')}
            </label>
            <Select
              id="myob-push-period-select"
              className="myob-push-period-select"
              value={preview?.periodStart || undefined}
              options={periodOptions}
              loading={loading && !preview}
              disabled={!selectedGuid || periodOptions.length === 0}
              placeholder={t('myob.push.periodPlaceholder')}
              onChange={handlePeriodChange}
            />
          </div>
        </div>

        {companiesError && companies.length === 0 ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('myob.employees.company.loadFailed')}
            description={companiesError}
            action={
              <Button size="small" onClick={onReloadCompanies}>
                {t('myob.employees.company.retry')}
              </Button>
            }
          />
        ) : null}

        {companiesLoading && companies.length === 0 ? (
          <Skeleton active paragraph={{ rows: 1 }} />
        ) : companies.length === 0 ? (
          !companiesError ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.push.company.empty')} /> : null
        ) : (
          companyBar
        )}

        {selectedGuid ? (
          <>
            <div className="myob-push-toolbar">
              <Button icon={<SafetyCertificateOutlined />} loading={checking} disabled={loading && !checking} onClick={checkMyobStatus}>
                {checkedAlready ? t('myob.push.recheck') : t('myob.push.check')}
              </Button>
              <span className="myob-sub myob-push-toolbar-hint">{t('myob.push.checkHint')}</span>
              <span className="myob-toolbar-spacer" />
              {canPush ? (
                <Button icon={<HistoryOutlined />} onClick={() => setBatchesOpen(true)}>
                  {t('myob.push.batches.open')}
                </Button>
              ) : null}
              <Button icon={<ReloadOutlined />} loading={loading && !checking} disabled={checking} onClick={reloadPreview}>
                {t('myob.push.reload')}
              </Button>
            </div>

            {error ? (
              <Alert
                type="error"
                showIcon
                style={{ marginBottom: 12 }}
                message={t('myob.push.loadFailed')}
                description={error}
                action={
                  <Button size="small" onClick={reloadPreview}>
                    {t('myob.push.retry')}
                  </Button>
                }
              />
            ) : null}

            {!preview ? (
              loading ? <Skeleton active paragraph={{ rows: 6 }} /> : null
            ) : (
              <>
                {/* 就绪卡：颜色之外有符号与文字 */}
                <ul className="myob-push-steps" aria-label={t('myob.push.readiness.aria')}>
                  {readinessCards.map((card) => (
                    <ReadinessItem key={card.id} card={card} canGoMapping={canGoMapping} onGoMapping={() => selectedGuid && onGoMapping(selectedGuid)} />
                  ))}
                </ul>

                {preview.hints.length > 0 ? (
                  <div className="myob-push-hints" role="note">
                    <b>{t('myob.push.hints.title', { count: preview.hints.length })}</b>
                    {hintGroups.map((group) => (
                      <div key={group.code} className="myob-push-hint-group">
                        <div className="myob-push-hint-group-title">{t(group.labelKey)}</div>
                        <ul>
                          {group.items.map((hint, index) => {
                            const parts = splitHintMessage(hint)
                            return (
                              <li key={`${hint.userGuid}-${hint.code}-${index}`}>
                                {parts.before}
                                {parts.name ? <b>{parts.name}</b> : null}
                                {parts.separated ? t('myob.push.hints.nameSep') : null}
                                {parts.after}
                              </li>
                            )
                          })}
                        </ul>
                      </div>
                    ))}
                    <Checkbox checked={acknowledged} onChange={(event) => setAcknowledged(event.target.checked)}>
                      {t('myob.push.hints.ack')}
                    </Checkbox>
                  </div>
                ) : null}

                <MeasuredTable<MyobPreviewRow>
                  metricId="pos-admin.myob.push-preview"
                  rowKey="userGuid"
                  size="middle"
                  columns={columns}
                  dataSource={rows}
                  loading={loading}
                  rowClassName={getRowClassName}
                  rowSelection={
                    canPush && preview.pushEnabled
                      ? {
                          selectedRowKeys: [...effectiveSelected],
                          onChange: (keys) => setSelected(new Set(keys.map(String))),
                          // 被拦截 / 没有转账工时 / MYOB 里已发薪的行不能选
                          getCheckboxProps: (row) => ({ disabled: !isPushableRow(row), 'aria-label': t('myob.push.selectAria', { name: row.userName }) }),
                          preserveSelectedRowKeys: true,
                        }
                      : undefined
                  }
                  scroll={{ x: 'max-content' }}
                  pagination={{
                    pageSize: PAGE_SIZE,
                    showSizeChanger: false,
                    hideOnSinglePage: true,
                    showTotal: (total) => t('myob.push.total', { total }),
                  }}
                  locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.push.empty')} /> }}
                />

                {rows.length > 0 ? (
                  <div className="myob-push-footer">
                    <span className="myob-push-summary">
                      {t('myob.push.summary.ready', { count: preview.totals.readyCount })}
                      <b>{t('myob.push.summary.hours', { hours: formatHours(preview.totals.readyHours) })}</b>
                      <span className="myob-push-summary-sep" aria-hidden="true">
                        |
                      </span>
                      {t('myob.push.summary.blockedPrefix')}
                      <b className={preview.totals.blockedCount > 0 ? 'myob-push-bad' : undefined}>
                        {t('myob.push.summary.people', { count: preview.totals.blockedCount })}
                      </b>
                      {t('myob.push.summary.blockedSuffix')}
                      <span className="myob-push-summary-sep" aria-hidden="true">
                        |
                      </span>
                      {t('myob.push.summary.noTransfer', { count: preview.totals.noTransferCount })}
                    </span>
                    <span className="myob-push-actions">
                      <Button icon={<DownloadOutlined />} onClick={handleExport}>
                        {t('myob.push.export')}
                      </Button>
                      {canPush && preview.pushEnabled ? (
                        <span className="myob-push-select">
                          <Button type="link" size="small" onClick={() => setSelected(new Set(pushableGuids))}>
                            {t('myob.push.selectAll', { count: pushableGuids.length })}
                          </Button>
                          <Button type="link" size="small" disabled={selectedCount === 0} onClick={() => setSelected(new Set())}>
                            {t('myob.push.selectNone')}
                          </Button>
                        </span>
                      ) : null}
                      <Tooltip title={!canPush ? t('myob.push.noPermissionTip') : preview.pushEnabled ? undefined : t('myob.push.pushDisabledTip')}>
                        {/* 禁用按钮收不到鼠标事件，Tooltip 套在外层 span 上 */}
                        <span>
                          <Button type="primary" disabled={!pushAllowed} aria-describedby="myob-push-disabled-note" onClick={() => setConfirmOpen(true)}>
                            {t('myob.push.pushButton', { count: selectedCount })}
                          </Button>
                        </span>
                      </Tooltip>
                      {!preview.pushEnabled ? (
                        <span className="myob-sub myob-push-disabled-note" id="myob-push-disabled-note">
                          {t('myob.push.pushDisabledNote')}
                        </span>
                      ) : null}
                    </span>
                  </div>
                ) : null}

                <div className="myob-sub myob-push-range">
                  {t('myob.push.range', { range: formatPeriodRange(preview.periodStart, preview.periodEnd), name: preview.connectionName })}
                </div>
              </>
            )}
          </>
        ) : null}
      </section>

      <PushDetailDrawer row={detailRow} preview={preview} onClose={() => setDetailUserGuid(null)} />

      {canPush && preview ? (
        <>
          <PushConfirmModal
            open={confirmOpen}
            preview={preview}
            selectedUserGuids={[...effectiveSelected]}
            hintsAcknowledged={acknowledged}
            onClose={(changed) => {
              setConfirmOpen(false)
              if (changed) {
                refreshAfterPush()
              }
            }}
            onOpenBatches={() => {
              setConfirmOpen(false)
              setBatchesOpen(true)
              refreshAfterPush()
            }}
          />
          <PushBatchesDrawer
            open={batchesOpen}
            connectionGuid={preview.connectionGuid}
            connectionName={preview.connectionName}
            periodStart={preview.periodStart}
            periodEnd={preview.periodEnd}
            onClose={() => setBatchesOpen(false)}
            onUndone={refreshAfterPush}
          />
        </>
      ) : null}
    </div>
  )
}

/** 工时单元格：0 显示灰色「—」，其余两位小数，右对齐等宽数字。 */
function HoursCell({ value }: { value: number }) {
  const zero = Math.abs(value) < 0.005
  return <span className={`myob-push-hours${zero ? ' myob-muted' : ''}`}>{formatHoursCell(value)}</span>
}

function ReadinessItem({ card, canGoMapping, onGoMapping }: { card: ReadinessCard; canGoMapping: boolean; onGoMapping: () => void }) {
  const { t } = useTranslation()
  return (
    <li className={`myob-step myob-step-${card.tone}`} data-step={card.id}>
      <span className="myob-step-symbol" aria-hidden="true">
        {card.symbol}
      </span>
      <div className="myob-step-body">
        {/* 符号对读屏不可见，用隐藏文字把状态读出来 */}
        <span className="myob-sr-only">{t(READINESS_STATE_KEYS[card.tone])}</span>
        <b>{t(card.titleKey, card.titleValues)}</b>
        {card.subKey ? <span className="myob-step-sub">{t(card.subKey, card.subValues)}</span> : null}
        {card.subText ? <span className="myob-step-sub">{card.subText}</span> : null}
        {card.subItems?.map((item) => (
          <span className="myob-step-sub" key={item.label}>
            {t('myob.push.readiness.unlocked.item', { label: item.label, count: item.count })}
          </span>
        ))}
        {card.action === 'goMapping' && canGoMapping ? (
          <Button type="link" size="small" className="myob-step-action" onClick={onGoMapping}>
            {t('myob.push.readiness.category.go')}
          </Button>
        ) : null}
      </div>
    </li>
  )
}
