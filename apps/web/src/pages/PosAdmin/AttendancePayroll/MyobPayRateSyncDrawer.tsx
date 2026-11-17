import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, DatePicker, Drawer, Empty, Modal, Select, Skeleton, Tag, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { applyMyobPayRateSync, previewMyobPayRateSync } from '../../../services/myobPayRateSyncService'
import { getMyobPayrollEmployers } from '../../../services/myobService'
import type { MyobPayrollCompanyOption } from '../../../types/myob'
import type {
  MyobPayRateSyncApplyResult,
  MyobPayRateSyncCellKey,
  MyobPayRateSyncPreview,
  MyobPayRateSyncRow,
} from '../../../types/myobPayRateSync'
import { avatarColorOf, initialsOf } from '../AttendanceReview/reviewLogic'
import { extractMyobError, formatMyobTime } from '../Myob/myobLogic'
import {
  CELL_KEYS,
  buildApplyItems,
  buildDefaultSelection,
  columnState,
  countOverwrites,
  countSelection,
  failureMessageKey,
  hasApplicableCell,
  mostCommonConnection,
  pickConnection,
  reasonMessageKey,
  setColumn,
  sortSyncRows,
  summarizeKinds,
  toggleCell,
  type PayRateSyncSelection,
} from './myobPayRateSyncLogic'

interface MyobPayRateSyncDrawerProps {
  open: boolean
  storeCode?: string
  onClose: () => void
  /** 至少写入了一位员工：父组件刷新时薪表。 */
  onApplied: () => void
}

const KIND_TAG_COLOR = { Create: 'green', Fill: 'blue', Differ: 'orange', Same: 'default', Skip: 'default' } as const

const money = (value: number | null | undefined) => (value === null || value === undefined ? '—' : value.toFixed(2))

/**
 * 从 MYOB 同步转账时薪：只读 MYOB，把基础时薪写进我方时薪表（新增生效记录，不改历史、不写回 MYOB）。
 * 平日价是「同步」；周六 / 周日 / 公假是按 MYOB 工资类别倍数推算的「建议值」，默认不勾（公假价在不与平日价冲突时默认勾）。
 */
export default function MyobPayRateSyncDrawer({ open, storeCode, onClose, onApplied }: MyobPayRateSyncDrawerProps) {
  const { t } = useTranslation()
  const [companies, setCompanies] = useState<MyobPayrollCompanyOption[]>([])
  const [connectionGuid, setConnectionGuid] = useState<string | null>(null)
  const [companiesLoaded, setCompaniesLoaded] = useState(false)
  const [preview, setPreview] = useState<MyobPayRateSyncPreview | null>(null)
  const [selection, setSelection] = useState<PayRateSyncSelection>({})
  const [effectiveFrom, setEffectiveFrom] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [applying, setApplying] = useState(false)
  const [result, setResult] = useState<MyobPayRateSyncApplyResult | null>(null)
  const effectiveTouched = useRef(false)
  const previewSeq = useRef(0)

  // 打开时读该分店可选的发薪公司（用发薪公司接口：只要「工资编辑」权限即可，不依赖「公司连接管理」）
  useEffect(() => {
    if (!open || !storeCode) return
    let cancelled = false
    setCompaniesLoaded(false)
    setPreview(null)
    setSelection({})
    setResult(null)
    setError(null)
    effectiveTouched.current = false
    setEffectiveFrom(null)
    void getMyobPayrollEmployers({ storeCode })
      .then((list) => {
        if (cancelled) return
        setCompanies(list.companies)
        setConnectionGuid(pickConnection(list.companies, mostCommonConnection(list.rows)))
        setCompaniesLoaded(true)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        setError(extractMyobError(err, t('attendancePayroll.rates.sync.loadFailed')).message)
        setCompaniesLoaded(true)
      })
    return () => {
      cancelled = true
    }
  }, [open, storeCode, t])

  const loadPreview = useCallback(
    async (refresh: boolean) => {
      if (!storeCode || !connectionGuid) return
      const seq = ++previewSeq.current
      setLoading(true)
      setError(null)
      try {
        const data = await previewMyobPayRateSync({
          connectionGuid,
          storeCode,
          effectiveFrom: effectiveTouched.current && effectiveFrom ? effectiveFrom : undefined,
          refresh,
        })
        if (seq !== previewSeq.current) return
        setPreview(data)
        setSelection(buildDefaultSelection(data.rows))
        // 用户没改过日期就跟随后端给的默认（下一个未锁定周期起始日）
        if (!effectiveTouched.current) setEffectiveFrom(data.effectiveFrom)
      } catch (err) {
        if (seq !== previewSeq.current) return
        setPreview(null)
        setError(extractMyobError(err, t('attendancePayroll.rates.sync.loadFailed')).message)
      } finally {
        if (seq === previewSeq.current) setLoading(false)
      }
    },
    // effectiveFrom 只在「用户改过」时才随预览发出，且改日期本身不触发重读，故不列入依赖
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [connectionGuid, storeCode, t],
  )

  useEffect(() => {
    if (open && connectionGuid) void loadPreview(false)
  }, [open, connectionGuid, loadPreview])

  const rows = useMemo(() => sortSyncRows(preview?.rows ?? []), [preview])
  const kinds = useMemo(() => summarizeKinds(rows), [rows])
  const picked = useMemo(() => countSelection(selection, rows), [rows, selection])
  const actionableCount = useMemo(() => rows.filter(hasApplicableCell).length, [rows])

  const confirmApply = () => {
    if (!preview || !connectionGuid || !storeCode || !effectiveFrom) return
    const items = buildApplyItems(selection, rows)
    if (!items.length) return
    const overwrites = countOverwrites(selection, rows)
    Modal.confirm({
      title: t('attendancePayroll.rates.sync.confirmTitle', { employees: picked.employees, cells: picked.cells }),
      content: (
        <div className="atp-sync-confirm">
          <div>{t('attendancePayroll.rates.sync.confirmEffective', { date: effectiveFrom })}</div>
          {overwrites > 0 ? <div className="atp-negative">{t('attendancePayroll.rates.sync.confirmOverwrite', { n: overwrites })}</div> : null}
          <div className="atp-muted">{t('attendancePayroll.rates.sync.confirmNote')}</div>
        </div>
      ),
      okText: t('attendancePayroll.rates.sync.apply'),
      cancelText: t('attendancePayroll.rates.cancel'),
      onOk: async () => {
        setApplying(true)
        try {
          const outcome = await applyMyobPayRateSync({ connectionGuid, storeCode, effectiveFrom, items })
          setResult(outcome)
          if (outcome.written.length) {
            message.success(t('attendancePayroll.rates.sync.applied', { n: outcome.written.length }))
            onApplied()
          }
          // 写入后重读一次：已写入的行会变成「已一致」，失败的行保持可重试
          await loadPreview(true)
        } catch (err) {
          message.error(extractMyobError(err, t('attendancePayroll.messages.actionFailed')).message)
        } finally {
          setApplying(false)
        }
      },
    })
  }

  const renderCell = (row: MyobPayRateSyncRow, key: MyobPayRateSyncCellKey) => {
    if (row.kind === 'Skip') return <span className="atp-muted">—</span>
    const cell = row[key]
    if (cell.suggested === null) {
      return (
        <Tooltip title={t(reasonMessageKey(cell.unavailableReason))}>
          <span className="atp-muted">— <span className="atp-small">{cell.unavailableReason ? t('attendancePayroll.rates.sync.cannotCompute') : ''}</span></span>
        </Tooltip>
      )
    }
    if (!cell.canApply) {
      return <span className="atp-muted atp-sync-same">{money(cell.suggested)} ✓</span>
    }
    const checked = (selection[row.userGuid] ?? []).includes(key)
    return (
      <Checkbox checked={checked} onChange={(event) => setSelection((current) => toggleCell(current, row, key, event.target.checked))}>
        <span className="atp-sync-diff">
          <span className="atp-muted">{cell.ourRate === null ? t('attendancePayroll.rates.sync.empty') : money(cell.ourRate)}</span>
          {' → '}
          <strong>{money(cell.suggested)}</strong>
          {cell.multiplier !== null ? <span className="atp-muted atp-small"> ×{cell.multiplier}</span> : null}
        </span>
      </Checkbox>
    )
  }

  const columnTitle = (key: MyobPayRateSyncCellKey) => {
    const state = columnState(selection, rows, key)
    return (
      <span className="atp-sync-col-title">
        {t(`attendancePayroll.tierLong.${key}`)}
        {state.total > 0 ? (
          <Checkbox
            checked={state.selected === state.total}
            indeterminate={state.selected > 0 && state.selected < state.total}
            aria-label={t('attendancePayroll.rates.sync.selectColumn', { column: t(`attendancePayroll.tierLong.${key}`) })}
            onChange={(event) => setSelection((current) => setColumn(current, rows, key, event.target.checked))}
          />
        ) : null}
      </span>
    )
  }

  const columns: ColumnsType<MyobPayRateSyncRow> = [
    {
      title: t('attendancePayroll.rates.columns.employee'),
      key: 'employee',
      width: 170,
      fixed: 'left',
      render: (_, row) => (
        <div className="atp-person">
          <span className="atp-sync-avatar" style={{ background: avatarColorOf(row.userName) }}>{initialsOf(row.userName)}</span>
          <div>
            <div className="atp-person-name">{row.userName}</div>
            <div className="atp-muted atp-small">{row.employmentType ? t(`attendancePayroll.employmentType.${row.employmentType}`, row.employmentType) : ''}</div>
          </div>
        </div>
      ),
    },
    {
      title: t('attendancePayroll.rates.sync.columns.status'),
      key: 'status',
      width: 150,
      render: (_, row) => (
        <div>
          <Tag className="atp-tag" color={KIND_TAG_COLOR[row.kind]}>{t(`attendancePayroll.rates.sync.kind.${row.kind}`)}</Tag>
          {row.kind === 'Skip' ? <div className="atp-muted atp-small">{t(reasonMessageKey(row.skipReason))}</div> : null}
        </div>
      ),
    },
    {
      title: t('attendancePayroll.rates.sync.columns.myob'),
      key: 'myob',
      width: 110,
      align: 'right',
      render: (_, row) => (
        <div>
          <span>{money(row.myobHourlyRate)}</span>
          {row.rounded && row.myobHourlyRate !== null ? (
            <div className="atp-muted atp-small">{t('attendancePayroll.rates.sync.rawRate', { rate: row.myobHourlyRate })}</div>
          ) : null}
        </div>
      ),
    },
    ...CELL_KEYS.map((key) => ({
      title: columnTitle(key),
      key,
      width: 175,
      render: (_: unknown, row: MyobPayRateSyncRow) => renderCell(row, key),
    })),
  ]

  const failed = result?.failed ?? []
  const noCompany = companiesLoaded && !companies.length

  return (
    <Drawer
      open={open}
      width="min(1180px, 96vw)"
      title={t('attendancePayroll.rates.sync.title')}
      onClose={onClose}
      destroyOnClose
      footer={(
        <div className="atp-drawer-foot atp-sync-foot">
          <span className="atp-muted">{t('attendancePayroll.rates.sync.selected', { employees: picked.employees, cells: picked.cells })}</span>
          <div className="atp-sync-foot-actions">
            <Button onClick={onClose}>{t('attendancePayroll.rates.sync.close')}</Button>
            <Button type="primary" loading={applying} disabled={picked.cells === 0 || !effectiveFrom} onClick={confirmApply}>
              {t('attendancePayroll.rates.sync.apply')}
            </Button>
          </div>
        </div>
      )}
    >
      <div className="atp-drawer-body">
        <div className="atp-sync-toolbar">
          <div className="atp-field">
            <span className="atp-field-label">{t('attendancePayroll.rates.sync.company')}</span>
            <Select
              style={{ minWidth: 240 }}
              value={connectionGuid ?? undefined}
              options={companies.map((company) => ({ value: company.connectionGuid, label: company.name }))}
              disabled={companies.length <= 1}
              onChange={(value) => {
                setResult(null)
                setConnectionGuid(value)
              }}
            />
          </div>
          <div className="atp-field">
            <span className="atp-field-label">{t('attendancePayroll.rates.effectiveFrom')}</span>
            <DatePicker
              allowClear={false}
              value={effectiveFrom ? dayjs(effectiveFrom) : null}
              onChange={(value) => {
                if (!value) return
                effectiveTouched.current = true
                setEffectiveFrom(value.format('YYYY-MM-DD'))
              }}
            />
          </div>
          <Button icon={<ReloadOutlined />} loading={loading} disabled={!connectionGuid} onClick={() => void loadPreview(true)}>
            {t('attendancePayroll.rates.sync.refresh')}
          </Button>
        </div>

        <Alert type="info" showIcon message={t('attendancePayroll.rates.sync.intro')} />

        {error ? <Alert type="error" showIcon message={error} /> : null}

        {failed.length ? (
          <Alert
            type="warning"
            showIcon
            message={t('attendancePayroll.rates.sync.failedTitle', { n: failed.length })}
            description={(
              <ul className="atp-sync-failed">
                {failed.map((item) => {
                  const key = failureMessageKey(item.code)
                  return <li key={item.userGuid}><strong>{item.userName}</strong>：{key ? t(key) : item.message || item.code}</li>
                })}
              </ul>
            )}
          />
        ) : null}

        {noCompany ? (
          <Empty description={t('attendancePayroll.rates.sync.noCompany')} />
        ) : !preview && loading ? (
          <Skeleton active paragraph={{ rows: 6 }} />
        ) : preview ? (
          <>
            <div className="atp-sync-summary">
              {(['Create', 'Fill', 'Differ', 'Same', 'Skip'] as const).filter((kind) => kinds[kind] > 0).map((kind) => (
                <Tag key={kind} className="atp-tag" color={KIND_TAG_COLOR[kind]}>{t(`attendancePayroll.rates.sync.kind.${kind}`)} {kinds[kind]}</Tag>
              ))}
              <span className="atp-muted atp-small">
                {t('attendancePayroll.rates.sync.fetchedAt', { time: formatMyobTime(preview.fetchedAt) })}
                {preview.notBoundCount > 0 ? ` · ${t('attendancePayroll.rates.sync.notBound', { n: preview.notBoundCount })}` : ''}
              </span>
            </div>
            {rows.length ? (
              <MeasuredTable<MyobPayRateSyncRow>
                metricId="pos-admin.attendance-payroll.myob-rate-sync"
                className="atp-sync-table"
                rowKey="userGuid"
                size="small"
                columns={columns}
                dataSource={rows}
                pagination={false}
                loading={loading}
                scroll={{ x: 1130 }}
                rowClassName={(row) => (hasApplicableCell(row) ? '' : 'atp-sync-row-idle')}
              />
            ) : (
              <Empty description={t('attendancePayroll.rates.sync.noRows')} />
            )}
            {actionableCount === 0 && rows.length ? <span className="atp-muted atp-small">{t('attendancePayroll.rates.sync.nothingToDo')}</span> : null}
          </>
        ) : null}
      </div>
    </Drawer>
  )
}
