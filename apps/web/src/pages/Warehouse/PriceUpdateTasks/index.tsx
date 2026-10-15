import { DownloadOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Button, Card, Checkbox, Col, DatePicker, Divider, Empty, Image, Input, Progress, Row, Segmented, Select, Space, Statistic, Tabs, Tag, Tooltip, Typography, message, theme } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation } from 'react-router-dom'
import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import {
  cancelStorePriceUpdateTasks,
  getStorePriceUpdateTaskSummary,
  getStorePriceUpdateTasks,
  getStorePriceUpdateTasksByProduct,
  getStorePriceUpdateTasksByStore,
  type StorePriceUpdateTask,
  type StorePriceUpdateTaskProductRow,
  type StorePriceUpdateTaskProductStore,
  type StorePriceUpdateTaskStoreRow,
  type StorePriceUpdateTaskSummary,
} from '../../../services/storePriceUpdateTaskService'
import { PRICE_UPDATE_TASKS_PATH } from '../../../utils/priceNotification'
import CancelTasksModal from './CancelTasksModal'
import {
  buildByProductCsvRows,
  buildByStoreCsvRows,
  buildCancelByIdsPayload,
  buildCancelByProductPayload,
  buildCsvContent,
  buildPriceUpdateTasksCsvFileName,
  buildPriceUpdateTasksQuery,
  buildStoreProgressSegments,
  buildTasksCsvRows,
  collectAllPages,
  compareLastCompleted,
  compareOldestPending,
  compareStoreCode,
  createDefaultPriceUpdateTasksFilters,
  countPendingStores,
  createPriceUpdateTasksRequestCoordinator,
  describeCancelInfo,
  describeLabelStatus,
  describeProductStore,
  formatLocalDateTime,
  formatPendingAge,
  formatRelativeTime,
  getCompletionRateColor,
  getHqSyncStatusMeta,
  getPendingAgeDays,
  getProductChangeLines,
  getTaskPriceChange,
  parsePriceUpdateTasksSearch,
  resolveInitiatorName,
  resolveInitiatorSourceLabel,
  storeCodeToSelection,
  toPercent,
  type PriceUpdateTaskKindFilter,
  type PriceUpdateTaskStatusFilter,
  type PriceUpdateTasksFilters,
  type PriceUpdateTasksTab,
} from './logic'
import messagesEn from './messages.en.json'
import messagesZh from './messages.zh.json'

// 本页文案随页面代码块懒注册，不进入首屏 i18n 包（首屏只保留路由标题与入口按钮两条）。
registerPageMessages({ zh: messagesZh, en: messagesEn })

const { RangePicker } = DatePicker
const I18N = 'warehouse.priceUpdateTasks'
const PRODUCT_IMAGE_FALLBACK = `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40"><rect width="40" height="40" rx="4" fill="#f5f5f5"/><circle cx="15" cy="15" r="3" fill="#c7c7c7"/><path d="M9 30l8-9 5 5 4-4 6 8H9z" fill="#d9d9d9"/></svg>',
)}`

function getErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback
}

function isAbortError(error: unknown) {
  return error instanceof DOMException && error.name === 'AbortError'
}

function downloadCsv(fileName: string, content: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'text/csv;charset=utf-8' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}

/** 待确认的撤销范围：任务明细勾选的 ID，或某商品（在当前分店筛选内）的全部未完成任务。 */
type CancelTarget =
  | { kind: 'ids'; ids: number[] }
  | { kind: 'product'; productCode: string; productName: string; storeCodes: string[]; storeCount: number }

function ProductCell({ image, name, itemNumber }: { image?: string | null; name?: string | null; itemNumber?: string | null }) {
  return (
    <Space size={8} align="center">
      <Image preview={false} src={image || PRODUCT_IMAGE_FALLBACK} fallback={PRODUCT_IMAGE_FALLBACK} width={40} height={40} style={{ objectFit: 'contain' }} />
      <div style={{ minWidth: 0 }}>
        <div>{name || '--'}</div>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>{itemNumber || '--'}</Typography.Text>
      </div>
    </Space>
  )
}

export default function PriceUpdateTasksPage() {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const location = useLocation()
  const { active } = useKeepAliveContext()
  const hydratedLocationKeyRef = useRef(location.key)
  const coordinatorRef = useRef(createPriceUpdateTasksRequestCoordinator())
  const initialState = useMemo(
    () => parsePriceUpdateTasksSearch(location.search),
    // 跳转参数只在首次打开时用于初始化；保活页再次被链接激活时由下方 effect 处理。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  )
  const initialFilters = useMemo<PriceUpdateTasksFilters>(() => ({
    ...createDefaultPriceUpdateTasksFilters(),
    keyword: initialState.keyword,
    storeCodes: storeCodeToSelection(initialState.storeCode),
  }), [initialState])

  const [tab, setTab] = useState<PriceUpdateTasksTab>(initialState.tab)
  const [draftFilters, setDraftFilters] = useState(initialFilters)
  const [appliedFilters, setAppliedFilters] = useState(initialFilters)
  const [refreshToken, setRefreshToken] = useState(0)
  const [stores, setStores] = useState<StoreOption[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [exporting, setExporting] = useState(false)

  const [summary, setSummary] = useState<StorePriceUpdateTaskSummary | null>(null)
  const [storeRows, setStoreRows] = useState<StorePriceUpdateTaskStoreRow[]>([])

  const [onlyIncomplete, setOnlyIncomplete] = useState(true)
  const [productPaging, setProductPaging] = useState({ page: 1, pageSize: 30 })
  const [productRows, setProductRows] = useState<StorePriceUpdateTaskProductRow[]>([])
  const [productTotal, setProductTotal] = useState(0)

  const [taskStatus, setTaskStatus] = useState<PriceUpdateTaskStatusFilter>(initialState.hqSyncFailedOnly ? 'All' : 'Pending')
  const [hqSyncFailedOnly, setHqSyncFailedOnly] = useState(initialState.hqSyncFailedOnly)
  const [taskPaging, setTaskPaging] = useState({ page: 1, pageSize: 50 })
  const [taskRows, setTaskRows] = useState<StorePriceUpdateTask[]>([])
  const [taskTotal, setTaskTotal] = useState(0)
  const [taskHqSyncEnabled, setTaskHqSyncEnabled] = useState(false)

  // 手动撤销：任务明细勾选的 ID（仅当前页）与待确认的撤销范围。
  const [selectedTaskIds, setSelectedTaskIds] = useState<number[]>([])
  const [cancelTarget, setCancelTarget] = useState<CancelTarget | null>(null)
  const [cancelling, setCancelling] = useState(false)

  const appliedStoreCodes = appliedFilters.storeCodes
  const baseQuery = useMemo(() => buildPriceUpdateTasksQuery(appliedFilters), [appliedFilters])
  const taskQuery = useMemo(() => ({
    ...baseQuery,
    status: taskStatus,
    ...(hqSyncFailedOnly ? { hqSyncFailedOnly: true } : {}),
  }), [baseQuery, hqSyncFailedOnly, taskStatus])

  useEffect(() => {
    getActiveStores().then(setStores).catch(() => setStores([]))
  }, [])

  const load = useCallback(async () => {
    const { requestId, signal } = coordinatorRef.current.start()
    setLoading(true)
    setError(null)
    try {
      if (tab === 'by-store') {
        const [nextSummary, nextRows] = await Promise.all([
          getStorePriceUpdateTaskSummary(baseQuery, { signal }),
          getStorePriceUpdateTasksByStore(baseQuery, { signal }),
        ])
        if (!coordinatorRef.current.isLatest(requestId)) return
        setSummary(nextSummary)
        setStoreRows(nextRows)
      } else if (tab === 'by-product') {
        const result = await getStorePriceUpdateTasksByProduct({ ...baseQuery, onlyIncomplete, ...productPaging }, { signal })
        if (!coordinatorRef.current.isLatest(requestId)) return
        setProductRows(result.items)
        setProductTotal(result.total)
      } else {
        const result = await getStorePriceUpdateTasks({ ...taskQuery, ...taskPaging }, { signal })
        if (!coordinatorRef.current.isLatest(requestId)) return
        setTaskRows(result.items)
        // 列表重新加载后，旧勾选可能指向已不在当前页/已变状态的任务，直接清空。
        setSelectedTaskIds([])
        setTaskTotal(result.total)
        setTaskHqSyncEnabled(result.hqSyncEnabled)
      }
    } catch (loadError) {
      if (isAbortError(loadError) || !coordinatorRef.current.isLatest(requestId)) return
      setError(loadError)
    } finally {
      if (coordinatorRef.current.isLatest(requestId)) setLoading(false)
    }
    // refreshToken 只用于「刷新」按钮强制重新请求。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, baseQuery, taskQuery, onlyIncomplete, productPaging, taskPaging, refreshToken])

  useEffect(() => { void load() }, [load])
  useEffect(() => () => { coordinatorRef.current.dispose() }, [])

  const applyFilters = useCallback((next: PriceUpdateTasksFilters) => {
    setDraftFilters(next)
    setAppliedFilters({ ...next, keyword: next.keyword.trim(), initiatorName: next.initiatorName.trim() })
    setProductPaging((current) => ({ ...current, page: 1 }))
    setTaskPaging((current) => ({ ...current, page: 1 }))
  }, [])

  useEffect(() => {
    // 保活页面被「查看各店执行情况」等链接再次激活时，以 URL 参数覆盖旧筛选；普通切回页签（无参数）保持原状。
    if (!active || location.pathname !== PRICE_UPDATE_TASKS_PATH || location.key === hydratedLocationKeyRef.current) return
    hydratedLocationKeyRef.current = location.key
    if (!location.search) return
    const linked = parsePriceUpdateTasksSearch(location.search)
    setTab(linked.tab)
    setHqSyncFailedOnly(linked.hqSyncFailedOnly)
    if (linked.hqSyncFailedOnly) setTaskStatus('All')
    applyFilters({ ...createDefaultPriceUpdateTasksFilters(), keyword: linked.keyword, storeCodes: storeCodeToSelection(linked.storeCode) })
  }, [active, applyFilters, location.key, location.pathname, location.search])

  const openTasksTab = (overrides: { storeCode?: string; hqSyncFailedOnly?: boolean; kind?: PriceUpdateTaskKindFilter }) => {
    setHqSyncFailedOnly(overrides.hqSyncFailedOnly === true)
    // 同步失败的任务可能已完成或仍未完成，必须看全部状态。
    setTaskStatus(overrides.hqSyncFailedOnly ? 'All' : 'Pending')
    applyFilters({
      ...draftFilters,
      ...(overrides.storeCode !== undefined ? { storeCodes: storeCodeToSelection(overrides.storeCode) } : {}),
      ...(overrides.kind !== undefined ? { kind: overrides.kind } : {}),
    })
    setTab('tasks')
  }

  const handleConfirmCancel = async (note: string) => {
    if (!cancelTarget) return
    setCancelling(true)
    try {
      const payload = cancelTarget.kind === 'ids'
        ? buildCancelByIdsPayload(cancelTarget.ids, note)
        : buildCancelByProductPayload(cancelTarget.productCode, cancelTarget.storeCodes, note)
      const result = await cancelStorePriceUpdateTasks(payload)
      if (result.cancelledCount === 0) {
        message.warning(t(`${I18N}.cancel.nothing`))
      } else if (result.skippedCount > 0) {
        message.warning(t(`${I18N}.cancel.partial`, { cancelled: result.cancelledCount, skipped: result.skippedCount }))
      } else {
        message.success(t(`${I18N}.cancel.success`, { count: result.cancelledCount }))
      }
      setCancelTarget(null)
      setSelectedTaskIds([])
      // 状态变了：各页签的统计与列表都要重新取（只有成功撤销才需要，但「已被处理」时重新取也能让界面追上真实状态）。
      setRefreshToken((value) => value + 1)
    } catch (cancelError) {
      message.error(getErrorMessage(cancelError, t(`${I18N}.cancel.failed`)))
    } finally {
      setCancelling(false)
    }
  }

  const handleExport = async () => {
    setExporting(true)
    try {
      const now = new Date()
      let rows: unknown[][]
      if (tab === 'by-store') {
        rows = buildByStoreCsvRows(storeRows, now, t)
      } else if (tab === 'by-product') {
        // by-product 接口 pageSize 上限 100。
        const all = await collectAllPages((page, pageSize) => getStorePriceUpdateTasksByProduct({ ...baseQuery, onlyIncomplete, page, pageSize }), 100)
        rows = buildByProductCsvRows(all, t)
      } else {
        const all = await collectAllPages((page, pageSize) => getStorePriceUpdateTasks({ ...taskQuery, page, pageSize }), 200)
        rows = buildTasksCsvRows(all, taskHqSyncEnabled, t)
      }
      if (rows.length <= 1) {
        message.warning(t(`${I18N}.export.empty`))
        return
      }
      downloadCsv(buildPriceUpdateTasksCsvFileName(t(`${I18N}.export.fileName`), tab, appliedFilters), buildCsvContent(rows))
      message.success(t(`${I18N}.export.success`, { count: rows.length - 1 }))
    } catch (exportError) {
      message.error(getErrorMessage(exportError, t(`${I18N}.export.failed`)))
    } finally {
      setExporting(false)
    }
  }

  const stateColors = useMemo<Record<StorePriceUpdateTaskProductStore['state'], string>>(() => ({
    Completed: token.colorSuccess,
    LabelOnly: token.colorInfo,
    PriceUpdate: token.colorWarning,
    Skipped: token.colorBorder,
  }), [token])
  const stateTagColors: Record<StorePriceUpdateTaskProductStore['state'], string> = {
    Completed: 'success', LabelOnly: 'processing', PriceUpdate: 'warning', Skipped: 'default',
  }

  const storeColumns = useMemo<ColumnsType<StorePriceUpdateTaskStoreRow>>(() => {
    const now = new Date()
    return [
      {
        title: t(`${I18N}.columns.store`), key: 'store', width: 220,
        sorter: compareStoreCode,
        render: (_, row) => <Space size={6}><Typography.Text type="secondary">{row.storeCode}</Typography.Text><span>{row.storeName || '--'}</span></Space>,
      },
      {
        // 数值/时间列首次点击先降序（最大/最新在前），更符合「找积压最多的店」的用法。
        title: t(`${I18N}.columns.pendingPriceUpdate`), key: 'pendingPriceUpdate', width: 120, align: 'right',
        sorter: (a, b) => a.pendingPriceUpdateCount - b.pendingPriceUpdateCount, sortDirections: ['descend', 'ascend'],
        render: (_, row) => (
          <Typography.Text strong={row.pendingPriceUpdateCount > 0} style={{ color: row.pendingPriceUpdateCount > 0 ? token.colorWarning : token.colorTextQuaternary }}>
            {row.pendingPriceUpdateCount}
          </Typography.Text>
        ),
      },
      {
        title: t(`${I18N}.columns.pendingLabelOnly`), key: 'pendingLabelOnly', width: 120, align: 'right',
        sorter: (a, b) => a.pendingLabelOnlyCount - b.pendingLabelOnlyCount, sortDirections: ['descend', 'ascend'],
        render: (_, row) => (
          <Typography.Text strong={row.pendingLabelOnlyCount > 0} style={{ color: row.pendingLabelOnlyCount > 0 ? token.colorInfo : token.colorTextQuaternary }}>
            {row.pendingLabelOnlyCount}
          </Typography.Text>
        ),
      },
      {
        title: t(`${I18N}.columns.completed`), dataIndex: 'completedCount', key: 'completed', width: 100, align: 'right',
        sorter: (a, b) => a.completedCount - b.completedCount, sortDirections: ['descend', 'ascend'],
      },
      {
        title: t(`${I18N}.columns.completionRate`), key: 'completionRate', width: 200,
        sorter: (a, b) => a.completionRate - b.completionRate, sortDirections: ['descend', 'ascend'],
        render: (_, row) => {
          const color = getCompletionRateColor(row.completionRate)
          const strokeColor = color === 'red' ? token.colorError : color === 'orange' ? token.colorWarning : token.colorSuccess
          return <Progress percent={toPercent(row.completionRate)} size="small" strokeColor={strokeColor} status="normal" />
        },
      },
      {
        title: t(`${I18N}.columns.oldestPending`), key: 'oldestPending', width: 130,
        // 无未完成任务（显示 --）的店无论升降序都排在最后。
        sorter: (a, b, sortOrder) => compareOldestPending(a, b, sortOrder), sortDirections: ['descend', 'ascend'],
        render: (_, row) => {
          const days = getPendingAgeDays(row.oldestPendingAtUtc, now)
          const overdue = days !== null && summary !== null && days >= summary.overdueDays
          return <Typography.Text type={overdue ? 'danger' : undefined}>{formatPendingAge(days, t)}</Typography.Text>
        },
      },
      {
        title: t(`${I18N}.columns.lastCompleted`), key: 'lastCompleted', width: 200,
        sorter: (a, b, sortOrder) => compareLastCompleted(a, b, sortOrder), sortDirections: ['descend', 'ascend'],
        render: (_, row) => row.lastCompletedAtUtc
          ? <Tooltip title={formatLocalDateTime(row.lastCompletedAtUtc)}><span>{resolveInitiatorName(row.lastCompletedBy, t)} · {formatRelativeTime(row.lastCompletedAtUtc, now, t)}</span></Tooltip>
          : '--',
      },
    ]
  }, [summary, t, token])

  const productColumns = useMemo<ColumnsType<StorePriceUpdateTaskProductRow>>(() => {
    const now = new Date()
    return [
      {
        title: t(`${I18N}.columns.product`), key: 'product', width: 300,
        render: (_, row) => <ProductCell image={row.productImage} name={row.productName} itemNumber={row.itemNumber} />,
      },
      {
        title: t(`${I18N}.columns.change`), key: 'change', width: 200,
        render: (_, row) => (
          <div>
            {getProductChangeLines(row, t).map((line) => <div key={line}>{line}</div>)}
            {row.changeCount > 1 ? <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t(`${I18N}.change.count`, { count: row.changeCount })}</Typography.Text> : null}
          </div>
        ),
      },
      {
        title: t(`${I18N}.columns.initiator`), key: 'initiator', width: 220,
        render: (_, row) => (
          <div>
            <div>{resolveInitiatorName(row.initiatorName, t)}</div>
            <Tooltip title={formatLocalDateTime(row.initiatedAtUtc)}>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {resolveInitiatorSourceLabel(row.initiatorSource, row.initiatorReference, t)} · {formatRelativeTime(row.initiatedAtUtc, now, t)}
              </Typography.Text>
            </Tooltip>
          </div>
        ),
      },
      {
        title: t(`${I18N}.columns.progress`), key: 'progress', width: 320,
        render: (_, row) => (
          <Space size={8} align="center" style={{ width: '100%' }}>
            <div style={{ display: 'flex', gap: 2, flex: 1, minWidth: 160 }}>
              {buildStoreProgressSegments(row.stores).map((segment) => (
                <Tooltip key={segment.storeCode} title={`${segment.storeName} · ${t(`${I18N}.state.${segment.state}`)}`}>
                  <span style={{ flex: 1, minWidth: 4, height: 10, borderRadius: 2, background: stateColors[segment.state] }} />
                </Tooltip>
              ))}
            </div>
            <Typography.Text style={{ whiteSpace: 'nowrap' }}>{row.completedStoreCount} / {row.storeCount}</Typography.Text>
          </Space>
        ),
      },
      {
        // 撤销范围 = 当前分店筛选内该商品的全部未完成任务；没有未完成分店的商品不显示入口。
        title: t(`${I18N}.columns.actions`), key: 'actions', width: 120, fixed: 'right',
        render: (_, row) => {
          const storeCount = countPendingStores(row)
          if (storeCount === 0) return '--'
          return (
            <Button type="link" danger size="small" onClick={() => setCancelTarget({
              kind: 'product',
              productCode: row.productCode,
              productName: row.productName || row.itemNumber || row.productCode,
              storeCodes: appliedStoreCodes,
              storeCount,
            })}>
              {t(`${I18N}.cancel.rowAction`)}
            </Button>
          )
        },
      },
    ]
  }, [appliedStoreCodes, stateColors, t])

  const taskColumns = useMemo<ColumnsType<StorePriceUpdateTask>>(() => [
    {
      title: t(`${I18N}.columns.image`), key: 'image', width: 64,
      render: (_, task) => <Image preview={false} src={task.productImage || PRODUCT_IMAGE_FALLBACK} fallback={PRODUCT_IMAGE_FALLBACK} width={40} height={40} style={{ objectFit: 'contain' }} />,
    },
    {
      title: t(`${I18N}.columns.product`), key: 'product', width: 220,
      render: (_, task) => <div><div>{task.productName || '--'}</div><Typography.Text type="secondary" style={{ fontSize: 12 }}>{task.itemNumber || '--'}</Typography.Text></div>,
    },
    {
      title: t(`${I18N}.columns.store`), key: 'store', width: 160,
      render: (_, task) => <Space size={6}><Typography.Text type="secondary">{task.storeCode}</Typography.Text><span>{task.storeName || ''}</span></Space>,
    },
    {
      title: t(`${I18N}.columns.kind`), key: 'kind', width: 100,
      render: (_, task) => <Tag color={task.kind === 'PriceUpdate' ? 'warning' : 'processing'}>{t(`${I18N}.kind.${task.kind}`)}</Tag>,
    },
    {
      title: t(`${I18N}.columns.priceChange`), key: 'priceChange', width: 260,
      render: (_, task) => {
        const change = getTaskPriceChange(task, t)
        return (
          <div>
            <div><Typography.Text type="secondary">{change.fromLabel} </Typography.Text><Typography.Text delete={change.strikeFrom}>{change.from}</Typography.Text></div>
            <div><Typography.Text type="secondary">{change.toLabel} </Typography.Text><Typography.Text strong>{change.to}</Typography.Text></div>
          </div>
        )
      },
    },
    {
      title: t(`${I18N}.columns.initiatorAndSource`), key: 'initiator', width: 180,
      render: (_, task) => (
        <div>
          <div>{resolveInitiatorName(task.initiatorName, t)}</div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>{resolveInitiatorSourceLabel(task.initiatorSource, task.initiatorReference, t)}</Typography.Text>
        </div>
      ),
    },
    { title: t(`${I18N}.columns.initiatedAt`), key: 'initiatedAt', width: 150, render: (_, task) => formatLocalDateTime(task.initiatedAtUtc) },
    {
      title: t(`${I18N}.columns.status`), key: 'status', width: 100,
      render: (_, task) => {
        const tag = <Tag color={task.status === 'Completed' ? 'success' : task.status === 'Cancelled' ? 'default' : 'warning'}>{t(`${I18N}.status.${task.status}`)}</Tag>
        // 已取消任务悬浮显示原因（系统自动 / 手动撤销）、撤销人、时间与备注。
        const info = describeCancelInfo(task, t)
        return info.length > 0 ? <Tooltip title={info.map((line) => <div key={line}>{line}</div>)}>{tag}</Tooltip> : tag
      },
    },
    { title: t(`${I18N}.columns.completedBy`), key: 'completedBy', width: 120, render: (_, task) => task.completedBy ? resolveInitiatorName(task.completedBy, t) : '--' },
    { title: t(`${I18N}.columns.completedAt`), key: 'completedAt', width: 150, render: (_, task) => formatLocalDateTime(task.completedAtUtc) },
    { title: t(`${I18N}.columns.labelStatus`), key: 'labelStatus', width: 140, render: (_, task) => describeLabelStatus(task, t) },
    // 未启用总部同步的部署不展示任何总部同步相关 UI。
    ...(taskHqSyncEnabled ? [{
      title: t(`${I18N}.columns.hqSync`), key: 'hqSync', width: 120,
      render: (_: unknown, task: StorePriceUpdateTask) => {
        const meta = getHqSyncStatusMeta(task.hqSyncStatus, t)
        return meta ? <Tag color={meta.color}>{meta.label}</Tag> : '--'
      },
    }] : []),
  ], [t, taskHqSyncEnabled])

  const renderEmpty = (description: string) => error
    ? <Empty description={t(`${I18N}.loadFailed`)}><Button size="small" icon={<ReloadOutlined />} onClick={() => setRefreshToken((value) => value + 1)}>{t('common.retry', '重试')}</Button></Empty>
    : <Empty description={description} />

  const renderProductStores = (row: StorePriceUpdateTaskProductRow) => {
    const now = new Date()
    return (
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 8 }}>
        {row.stores.map((store) => (
          <div
            key={store.storeCode}
            style={{
              padding: '8px 12px',
              borderRadius: token.borderRadius,
              border: `1px ${store.state === 'Skipped' ? 'dashed' : 'solid'} ${token.colorBorderSecondary}`,
              background: token.colorBgContainer,
            }}
          >
            <Space size={6} style={{ display: 'flex', justifyContent: 'space-between' }}>
              <Typography.Text strong ellipsis>{store.storeName || store.storeCode}</Typography.Text>
              <Tag color={stateTagColors[store.state]} style={{ marginInlineEnd: 0 }}>{t(`${I18N}.state.${store.state}`)}</Tag>
            </Space>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{describeProductStore(store, now, t)}</Typography.Text>
          </div>
        ))}
      </div>
    )
  }

  const rangeValue: [Dayjs, Dayjs] = [dayjs(draftFilters.startDate), dayjs(draftFilters.endDate)]
  const storeOptions = useMemo(() => stores.map((store) => ({ value: store.value, label: `${store.value} ${store.label}` })), [stores])
  // 「全选」只统计下拉里真实存在的分店（URL 带来的已停用分店不计入），避免勾选状态与选项对不上。
  const selectedStoreCount = useMemo(() => {
    const picked = new Set(draftFilters.storeCodes)
    return storeOptions.filter((option) => picked.has(option.value)).length
  }, [draftFilters.storeCodes, storeOptions])
  const allStoresSelected = storeOptions.length > 0 && selectedStoreCount === storeOptions.length
  const someStoresSelected = selectedStoreCount > 0
  const toggleAllStores = () => setDraftFilters((current) => ({
    ...current,
    storeCodes: allStoresSelected ? [] : storeOptions.map((option) => option.value),
  }))

  const summaryCards = summary ? (
    <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
      {/* 需改价与待换标签是两类不同的门店动作（改系统价 vs 换货架标签），各自独立成卡，点击直达对应明细。 */}
      <Col flex="1 1 200px">
        <Card size="small" hoverable onClick={() => openTasksTab({ kind: 'PriceUpdate' })} style={{ borderTop: `3px solid ${token.colorWarning}` }}>
          <Statistic title={t(`${I18N}.summary.priceUpdate`)} value={summary.pendingPriceUpdateCount} valueStyle={{ color: token.colorWarning }} />
          <Typography.Text type="secondary">{t(`${I18N}.summary.priceUpdateHint`)}</Typography.Text>
        </Card>
      </Col>
      <Col flex="1 1 200px">
        <Card size="small" hoverable onClick={() => openTasksTab({ kind: 'LabelOnly' })} style={{ borderTop: `3px solid ${token.colorInfo}` }}>
          <Statistic title={t(`${I18N}.summary.labelOnly`)} value={summary.pendingLabelOnlyCount} valueStyle={{ color: token.colorInfo }} />
          <Typography.Text type="secondary">{t(`${I18N}.summary.labelOnlyHint`)}</Typography.Text>
        </Card>
      </Col>
      <Col flex="1 1 200px">
        <Card size="small">
          <Statistic title={t(`${I18N}.summary.completed`)} value={summary.completedCount} />
          <Typography.Text type="secondary">{t(`${I18N}.summary.completedHint`, { percent: toPercent(summary.completionRate) })}</Typography.Text>
        </Card>
      </Col>
      <Col flex="1 1 200px">
        <Card size="small">
          <Statistic title={t(`${I18N}.summary.overdue`, { days: summary.overdueDays })} value={summary.overdueCount} valueStyle={{ color: token.colorError }} />
          <Typography.Text type="secondary">{t(`${I18N}.summary.overdueHint`, { count: summary.overdueStoreCount })}</Typography.Text>
        </Card>
      </Col>
      {summary.hqSyncEnabled ? (
        <Col flex="1 1 200px">
          <Card size="small" hoverable onClick={() => openTasksTab({ hqSyncFailedOnly: true })}>
            <Statistic title={t(`${I18N}.summary.hqSyncFailed`)} value={summary.hqSyncFailedCount} valueStyle={{ color: token.colorWarning }} />
            <Typography.Text type="secondary">{t(`${I18N}.summary.hqSyncFailedHint`)}</Typography.Text>
          </Card>
        </Col>
      ) : null}
    </Row>
  ) : null

  return (
    <PageContainer
      title={t(`${I18N}.title`)}
      subtitle={t(`${I18N}.subtitle`)}
      extra={(
        <Space>
          <Button icon={<ReloadOutlined />} onClick={() => setRefreshToken((value) => value + 1)}>{t('common.refresh', '刷新')}</Button>
          <Button icon={<DownloadOutlined />} loading={exporting} onClick={() => void handleExport()}>{t(`${I18N}.export.button`)}</Button>
        </Space>
      )}
    >
      <Card size="small">
        <Space wrap size={[8, 8]} style={{ marginBottom: 12 }}>
          <RangePicker allowClear={false} value={rangeValue} onChange={(range) => {
            const [start, end] = range ?? []
            if (!start || !end) return
            setDraftFilters((current) => ({ ...current, startDate: start.format('YYYY-MM-DD'), endDate: end.format('YYYY-MM-DD') }))
          }} />
          <Select mode="multiple" allowClear showSearch optionFilterProp="label" maxTagCount="responsive" placeholder={t(`${I18N}.filters.store`)} style={{ width: 280 }} options={storeOptions}
            value={draftFilters.storeCodes} onChange={(storeCodes) => setDraftFilters((current) => ({ ...current, storeCodes }))}
            popupRender={(menu) => (
              <>
                <div style={{ padding: '4px 12px 0' }}>
                  <Checkbox checked={allStoresSelected} indeterminate={someStoresSelected && !allStoresSelected} onChange={toggleAllStores}>
                    {t(`${I18N}.filters.selectAllStores`, { selected: selectedStoreCount, total: storeOptions.length })}
                  </Checkbox>
                </div>
                <Divider style={{ margin: '4px 0' }} />
                {menu}
              </>
            )} />
          <Select style={{ width: 128 }} value={draftFilters.kind} onChange={(kind) => setDraftFilters((current) => ({ ...current, kind }))} options={[
            { value: '', label: t(`${I18N}.filters.allKinds`) },
            { value: 'PriceUpdate', label: t(`${I18N}.kind.PriceUpdate`) },
            { value: 'LabelOnly', label: t(`${I18N}.kind.LabelOnly`) },
          ]} />
          <Input allowClear placeholder={t(`${I18N}.filters.initiator`)} style={{ width: 140 }} value={draftFilters.initiatorName}
            onChange={(event) => setDraftFilters((current) => ({ ...current, initiatorName: event.target.value }))} onPressEnter={() => applyFilters(draftFilters)} />
          <Input allowClear placeholder={t(`${I18N}.filters.keyword`)} style={{ width: 220 }} value={draftFilters.keyword}
            onChange={(event) => setDraftFilters((current) => ({ ...current, keyword: event.target.value }))} onPressEnter={() => applyFilters(draftFilters)} />
          <Button type="primary" icon={<SearchOutlined />} onClick={() => applyFilters(draftFilters)}>{t('common.search')}</Button>
          <Button onClick={() => { setHqSyncFailedOnly(false); applyFilters(createDefaultPriceUpdateTasksFilters()) }}>{t('common.reset', '重置')}</Button>
        </Space>
        {error ? <Typography.Text type="danger" style={{ display: 'block', marginBottom: 8 }}>{getErrorMessage(error, t(`${I18N}.loadFailed`))}</Typography.Text> : null}
        <Tabs
          activeKey={tab}
          onChange={(key) => setTab(key as PriceUpdateTasksTab)}
          items={[
            {
              key: 'by-store',
              label: t(`${I18N}.tabs.byStore`),
              children: (
                <>
                  {summaryCards}
                  <MeasuredTable metricId="warehouse.price-update-tasks.by-store"
                    size="small" loading={loading} rowKey="storeCode" columns={storeColumns} dataSource={storeRows}
                    locale={{ emptyText: renderEmpty(t(`${I18N}.empty`)) }} scroll={{ x: 1000 }} pagination={false}
                    onRow={(row) => ({ onClick: () => openTasksTab({ storeCode: row.storeCode }), style: { cursor: 'pointer' } })}
                  />
                </>
              ),
            },
            {
              key: 'by-product',
              label: t(`${I18N}.tabs.byProduct`),
              children: (
                <>
                  <Space wrap size={[16, 8]} style={{ marginBottom: 12, display: 'flex', justifyContent: 'space-between' }}>
                    <Space size={8}>
                      <Typography.Text type="secondary">{t(`${I18N}.progressFilter.label`)}</Typography.Text>
                      <Segmented value={onlyIncomplete ? 'incomplete' : 'all'} options={[
                        { value: 'incomplete', label: t(`${I18N}.progressFilter.incomplete`) },
                        { value: 'all', label: t(`${I18N}.progressFilter.all`) },
                      ]} onChange={(value) => { setOnlyIncomplete(value === 'incomplete'); setProductPaging((current) => ({ ...current, page: 1 })) }} />
                    </Space>
                    <Space size={12}>
                      {(['Completed', 'LabelOnly', 'PriceUpdate'] as const).map((state) => (
                        <Space key={state} size={4}>
                          <span style={{ display: 'inline-block', width: 10, height: 10, borderRadius: 2, background: stateColors[state] }} />
                          <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t(`${I18N}.state.${state}`)}</Typography.Text>
                        </Space>
                      ))}
                    </Space>
                  </Space>
                  <MeasuredTable metricId="warehouse.price-update-tasks.by-product"
                    size="small" loading={loading} rowKey="productCode" columns={productColumns} dataSource={productRows}
                    locale={{ emptyText: renderEmpty(t(`${I18N}.empty`)) }} scroll={{ x: 1220 }}
                    expandable={{ expandedRowRender: renderProductStores, rowExpandable: (row) => row.stores.length > 0 }}
                    pagination={{
                      current: productPaging.page, pageSize: productPaging.pageSize, total: productTotal, showSizeChanger: true, pageSizeOptions: [20, 30, 50, 100],
                      showTotal: (count) => t(`${I18N}.total`, { count }),
                      onChange: (page, pageSize) => setProductPaging({ page, pageSize }),
                    }}
                  />
                </>
              ),
            },
            {
              key: 'tasks',
              label: t(`${I18N}.tabs.tasks`),
              children: (
                <>
                  <Space wrap size={[16, 8]} style={{ marginBottom: 12 }}>
                    <Segmented value={taskStatus} options={[
                      { value: 'Pending', label: t(`${I18N}.status.Pending`) },
                      { value: 'Completed', label: t(`${I18N}.status.Completed`) },
                      { value: 'All', label: t(`${I18N}.status.All`) },
                    ]} onChange={(value) => { setTaskStatus(value as PriceUpdateTaskStatusFilter); setTaskPaging((current) => ({ ...current, page: 1 })) }} />
                    <Segmented value={appliedFilters.kind} options={[
                      { value: '', label: t(`${I18N}.filters.allKinds`) },
                      { value: 'PriceUpdate', label: <Space size={4}><span style={{ display: 'inline-block', width: 8, height: 8, borderRadius: 2, background: token.colorWarning }} />{t(`${I18N}.kind.PriceUpdate`)}</Space> },
                      { value: 'LabelOnly', label: <Space size={4}><span style={{ display: 'inline-block', width: 8, height: 8, borderRadius: 2, background: token.colorInfo }} />{t(`${I18N}.kind.LabelOnly`)}</Space> },
                    ]} onChange={(value) => applyFilters({ ...draftFilters, kind: value as PriceUpdateTaskKindFilter })} />
                    {taskHqSyncEnabled || hqSyncFailedOnly ? (
                      <Checkbox checked={hqSyncFailedOnly} onChange={(event) => { setHqSyncFailedOnly(event.target.checked); setTaskPaging((current) => ({ ...current, page: 1 })) }}>
                        {t(`${I18N}.filters.hqSyncFailedOnly`)}
                      </Checkbox>
                    ) : null}
                    <Button danger disabled={selectedTaskIds.length === 0} onClick={() => setCancelTarget({ kind: 'ids', ids: selectedTaskIds })}>
                      {t(`${I18N}.cancel.button`, { count: selectedTaskIds.length })}
                    </Button>
                  </Space>
                  <MeasuredTable metricId="warehouse.price-update-tasks.tasks"
                    size="small" loading={loading} rowKey="id" columns={taskColumns} dataSource={taskRows}
                    // 只有未完成的任务可以撤销；已完成/已取消行的勾选框置灰。
                    rowSelection={{
                      selectedRowKeys: selectedTaskIds,
                      onChange: (keys) => setSelectedTaskIds(keys.map(Number)),
                      getCheckboxProps: (task) => ({ disabled: task.status !== 'Pending' }),
                    }}
                    locale={{ emptyText: renderEmpty(t(`${I18N}.empty`)) }} scroll={{ x: 1800 }}
                    pagination={{
                      current: taskPaging.page, pageSize: taskPaging.pageSize, total: taskTotal, showSizeChanger: true, pageSizeOptions: [20, 50, 100, 200],
                      showTotal: (count) => t(`${I18N}.total`, { count }),
                      onChange: (page, pageSize) => setTaskPaging({ page, pageSize }),
                    }}
                  />
                </>
              ),
            },
          ]}
        />
      </Card>
      <CancelTasksModal
        open={cancelTarget !== null}
        loading={cancelling}
        description={cancelTarget?.kind === 'product'
          ? t(`${I18N}.cancel.byProduct`, { name: cancelTarget.productName, count: cancelTarget.storeCount })
          : t(`${I18N}.cancel.byIds`, { count: cancelTarget?.ids.length ?? 0 })}
        onConfirm={(note) => void handleConfirmCancel(note)}
        onClose={() => setCancelTarget(null)}
      />
    </PageContainer>
  )
}
