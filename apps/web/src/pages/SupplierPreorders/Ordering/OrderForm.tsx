// 供应商节日预订单 · 下单页（/supplier-preorder-ordering/:formGuid，设计图 1）
// 被授权的下单人员替自己的分店按「整箱」下单：步进/失焦即保存，截止后只读；
// 可查看其他分店的人能看到其他分店已订箱数（只读）。
// 本 PR 不含：去年进货/销售/日销曲线/折扣/展开大图/参考商品（列定义已集中在 orderingColumns.tsx，便于下一个 PR 插入）。
import { ArrowLeftOutlined, DownloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, App, Button, Input, Result, Segmented, Select, Spin, Tag } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  downloadSupplierPreorderStoreExport,
  getSupplierPreorderOrderingContext,
  getSupplierPreorderOrderingItems,
  getSupplierPreorderOrderingSummary,
  saveSupplierPreorderLine,
  submitSupplierPreorderStoreOrder,
} from '../../../services/supplierPreorderService'
import type {
  SupplierPreorderOrderingContext,
  SupplierPreorderOrderingFilter,
  SupplierPreorderOrderingItem,
  SupplierPreorderOrderingSort,
  SupplierPreorderOrderingSummary,
  SupplierPreorderSaveLineResult,
} from '../../../types/supplierPreorder'

import { createLineSaveQueue, makeLineKey, parseLineKey, type LineSaveQueue } from './lineSaveQueue.logic'
import { buildOrderingColumns } from './orderingColumns'
import './orderingMessages'
import {
  ORDERING_PAGE_SIZES,
  PAGE_SIZE_STORAGE_KEY,
  computeClockOffsetMs,
  formatAud,
  formatBusinessDate,
  formatInteger,
  getBrowserStorage,
  getDeadlineState,
  getErrorMessage,
  isAbortError,
  isDeadlinePassed,
  normalizePageSize,
  parseUtcMs,
  pickInitialStoreGuid,
  readStoredPageSize,
  safeStorageGet,
  safeStorageSet,
  storeStorageKey,
} from './orderingPage.logic'
import { DeadlineText, FormStatusTag, StoreStatusTag, formatHolidayYear } from './orderingShared'
import OrderingSummaryBar from './OrderingSummaryBar'
import './styles.css'

const NS = 'supplierPreorderOrdering'
/** 同一行连续操作的合并窗口 */
const SAVE_DEBOUNCE_MS = 400
/** 搜索框防抖 */
const SEARCH_DEBOUNCE_MS = 300
/** 截止倒计时刷新间隔 */
const CLOCK_TICK_MS = 15_000
/** setTimeout 能接受的最大延时（约 24.8 天），超过会立即触发 */
const MAX_TIMEOUT_MS = 2_147_483_647

type SaveResultWithSeq = { result: SupplierPreorderSaveLineResult; seq: number; overlapped: boolean }

/** 用最新汇总同步分店下拉里的状态与箱数/金额 */
function applySummaryToContext(context: SupplierPreorderOrderingContext, summary: SupplierPreorderOrderingSummary): SupplierPreorderOrderingContext {
  return {
    ...context,
    form: {
      ...context.form,
      myStores: context.form.myStores.map((store) =>
        store.storeGuid === summary.storeGuid
          ? {
              ...store,
              orderStatus: summary.orderStatus,
              cartons: summary.cartons,
              amount: summary.amount,
              submittedAtUtc: summary.submittedAtUtc ?? store.submittedAtUtc,
              lastEditedAtUtc: summary.lastEditedAtUtc ?? store.lastEditedAtUtc,
              lastEditedBy: summary.lastEditedBy ?? store.lastEditedBy,
            }
          : store,
      ),
    },
  }
}

export default function SupplierPreorderOrderForm() {
  const { formGuid = '' } = useParams<{ formGuid: string }>()
  // 以订单表为 key 重建页面：浏览器前进/后退切换订单表时，旧表的保存队列随组件卸载（卸载前会把待保存的值发出），不会串到新表
  return <SupplierPreorderOrderFormInner key={formGuid} formGuid={formGuid} />
}

function SupplierPreorderOrderFormInner({ formGuid }: { formGuid: string }) {
  const navigate = useNavigate()
  const { t } = useTranslation()
  const { message, modal } = App.useApp()
  const storage = useMemo(() => getBrowserStorage(), [])

  // ---------- 订单表上下文 + 服务器时钟 ----------
  const [context, setContext] = useState<SupplierPreorderOrderingContext | null>(null)
  const [contextLoading, setContextLoading] = useState(true)
  const [contextError, setContextError] = useState<string | null>(null)
  const [contextReloadKey, setContextReloadKey] = useState(0)
  const [clockOffsetMs, setClockOffsetMs] = useState(0)
  const [nowMs, setNowMs] = useState(() => Date.now())
  const [storeGuid, setStoreGuid] = useState<string | null>(null)

  // ---------- 列表查询 ----------
  const [searchText, setSearchText] = useState('')
  const [keyword, setKeyword] = useState('')
  const [category, setCategory] = useState<string | undefined>(undefined)
  const [filter, setFilter] = useState<SupplierPreorderOrderingFilter>('all')
  const [sort, setSort] = useState<SupplierPreorderOrderingSort>('seq')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(() => readStoredPageSize(storage))
  const [rows, setRows] = useState<SupplierPreorderOrderingItem[]>([])
  const [total, setTotal] = useState(0)
  const [itemsLoading, setItemsLoading] = useState(false)
  const [itemsReloadKey, setItemsReloadKey] = useState(0)

  // ---------- 本店下单与汇总 ----------
  /** 本地尚未保存完成的箱数（itemGuid → 箱数），只针对当前分店 */
  const [drafts, setDrafts] = useState<Record<string, number>>({})
  /** 正在保存的行键（分店|商品） */
  const [savingKeys, setSavingKeys] = useState<ReadonlySet<string>>(() => new Set())
  const [summary, setSummary] = useState<SupplierPreorderOrderingSummary | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [exporting, setExporting] = useState(false)

  const mountedRef = useRef(true)
  const formGuidRef = useRef(formGuid)
  const storeGuidRef = useRef<string | null>(null)
  storeGuidRef.current = storeGuid
  /** 每次保存请求的全局序号；汇总只采纳序号更大的结果，避免慢返回覆盖新汇总 */
  const saveSeqRef = useRef(0)
  /** 各分店最近一次已采纳汇总对应的保存序号（分店 → 序号） */
  const lastSummarySeqRef = useRef(new Map<string, number>())
  /** 正在发送中的保存请求数；有重叠时等全部结束再拉一次汇总，避免某次返回的汇总漏算另一行 */
  const inFlightRef = useRef(0)
  const refreshSummaryRef = useRef<() => void>(() => undefined)

  const applySummary = useCallback((next: SupplierPreorderOrderingSummary) => {
    if (next.storeGuid === storeGuidRef.current) setSummary(next)
    setContext((prev) => (prev ? applySummaryToContext(prev, next) : prev))
  }, [])

  // 保存队列：回调里只读 ref，队列实例在组件生命周期内保持不变
  const queueRef = useRef<LineSaveQueue | null>(null)
  const callbacksRef = useRef({ message, t, applySummary })
  callbacksRef.current = { message, t, applySummary }
  if (!queueRef.current) {
    queueRef.current = createLineSaveQueue<SaveResultWithSeq>({
      delayMs: SAVE_DEBOUNCE_MS,
      save: async (key, value) => {
        const { storeGuid: lineStore, itemGuid } = parseLineKey(key)
        saveSeqRef.current += 1
        const seq = saveSeqRef.current
        const startedWhileBusy = inFlightRef.current > 0
        inFlightRef.current += 1
        try {
          const result = await saveSupplierPreorderLine(formGuidRef.current, lineStore, itemGuid, value)
          return { result, seq, overlapped: startedWhileBusy || saveSeqRef.current !== seq }
        } finally {
          inFlightRef.current -= 1
        }
      },
      onSuccess: (key, _value, { result, seq, overlapped }, { isLatest }) => {
        if (!mountedRef.current) return
        const { storeGuid: lineStore, itemGuid } = parseLineKey(key)
        if (seq > (lastSummarySeqRef.current.get(lineStore) ?? 0)) {
          lastSummarySeqRef.current.set(lineStore, seq)
          callbacksRef.current.applySummary(result.summary)
        }
        if (overlapped && inFlightRef.current === 0) refreshSummaryRef.current()
        // 已切换到别的分店：只更新汇总，不动当前表格
        if (lineStore !== storeGuidRef.current) return
        // 服务器值作为该行的新基准
        setRows((prev) => prev.map((row) => (row.itemGuid === itemGuid ? { ...row, myCartons: result.cartons } : row)))
        // 只有最新一次结果才清掉本地值；否则用户还在继续改，保留界面上的值
        if (isLatest) {
          setDrafts((prev) => {
            if (!(itemGuid in prev)) return prev
            const next = { ...prev }
            delete next[itemGuid]
            return next
          })
        }
      },
      onError: (key, _value, error) => {
        if (!mountedRef.current) return
        const { t: translate, message: toast } = callbacksRef.current
        toast.error(getErrorMessage(error, translate(`${NS}.table.saveFailed`)))
        const { storeGuid: lineStore, itemGuid } = parseLineKey(key)
        if (lineStore !== storeGuidRef.current) return
        // 回滚：去掉本地值，界面回到服务器基准值
        setDrafts((prev) => {
          if (!(itemGuid in prev)) return prev
          const next = { ...prev }
          delete next[itemGuid]
          return next
        })
      },
      onBusyChange: (key, busy) => {
        if (!mountedRef.current) return
        setSavingKeys((prev) => {
          const next = new Set(prev)
          if (busy) next.add(key)
          else next.delete(key)
          return next
        })
      },
    })
  }
  const queue = queueRef.current

  // 挂载/卸载：卸载时立即发出防抖中的保存，避免离开页面丢数据
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      queue.flushAll()
    }
  }, [queue])

  // 还有保存未完成时离开/刷新页面要提醒
  useEffect(() => {
    const handler = (event: BeforeUnloadEvent) => {
      if (!queue.hasBusy()) return
      queue.flushAll()
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [queue])

  // ---------- 加载上下文 ----------
  useEffect(() => {
    formGuidRef.current = formGuid
    const controller = new AbortController()
    setContextLoading(true)
    setContextError(null)
    getSupplierPreorderOrderingContext(formGuid, controller.signal)
      .then((result) => {
        const offset = computeClockOffsetMs(result.serverNowUtc, Date.now())
        setClockOffsetMs(offset)
        setNowMs(Date.now() + offset)
        setContext(result)
        const remembered = safeStorageGet(storage, storeStorageKey(formGuid))
        setStoreGuid(pickInitialStoreGuid(result.form.myStores, remembered))
      })
      .catch((error: unknown) => {
        if (isAbortError(error)) return
        setContextError(getErrorMessage(error, t(`${NS}.page.loadFailed`)))
      })
      .finally(() => {
        if (!controller.signal.aborted) setContextLoading(false)
      })
    return () => {
      controller.abort()
      // 切换到另一张订单表前，先把本表防抖中的保存发出去（此时 formGuidRef 仍是旧表）
      queue.flushAll()
    }
  }, [formGuid, contextReloadKey, storage, queue, t])

  // ---------- 截止时钟（按服务器时间校正） ----------
  const deadlineAtUtc = context?.form.deadlineAtUtc ?? null
  useEffect(() => {
    if (!deadlineAtUtc) return
    const tick = () => setNowMs(Date.now() + clockOffsetMs)
    const interval = window.setInterval(tick, CLOCK_TICK_MS)
    // 再挂一个精确到截止时刻的定时器，到点立即切只读
    const deadlineMs = parseUtcMs(deadlineAtUtc)
    const remaining = deadlineMs === null ? -1 : deadlineMs - (Date.now() + clockOffsetMs)
    const timeout = remaining > 0 && remaining < MAX_TIMEOUT_MS ? window.setTimeout(tick, remaining + 200) : undefined
    return () => {
      window.clearInterval(interval)
      if (timeout !== undefined) window.clearTimeout(timeout)
    }
  }, [deadlineAtUtc, clockOffsetMs])

  const form = context?.form ?? null
  const canViewOtherStores = Boolean(context?.canViewOtherStores)
  const selectedStore = useMemo(() => form?.myStores.find((store) => store.storeGuid === storeGuid) ?? null, [form, storeGuid])
  const deadlineState = getDeadlineState(form?.deadlineAtUtc, nowMs, form?.isLocked ?? false)
  // 有管理权限的人不受截止限制；其他人到点后本地立即切只读（服务器也会拒绝）
  const lockedByClock = Boolean(context && !context.isManager && isDeadlinePassed(form?.deadlineAtUtc, nowMs))
  const canEdit = Boolean(selectedStore?.canEdit) && !lockedByClock
  const canEditRef = useRef(canEdit)
  canEditRef.current = canEdit

  // 到点切只读时把防抖中的值发出去（服务器若已拒绝，会提示原因并回滚）
  useEffect(() => {
    if (lockedByClock) queue.flushAll()
  }, [lockedByClock, queue])

  // ---------- 加载汇总 ----------
  const [summaryReloadKey, setSummaryReloadKey] = useState(0)
  refreshSummaryRef.current = () => setSummaryReloadKey((value) => value + 1)
  useEffect(() => {
    if (!storeGuid || !formGuid) return
    const controller = new AbortController()
    const seqAtStart = saveSeqRef.current
    getSupplierPreorderOrderingSummary(formGuid, storeGuid, controller.signal)
      .then((result) => {
        // 加载期间如果已有更新的保存结果，以保存结果为准
        if ((lastSummarySeqRef.current.get(storeGuid) ?? 0) > seqAtStart) return
        lastSummarySeqRef.current.set(storeGuid, seqAtStart)
        applySummary(result)
      })
      .catch((error: unknown) => {
        if (isAbortError(error)) return
        message.error(getErrorMessage(error, t(`${NS}.summary.loadFailed`)))
      })
    return () => controller.abort()
  }, [formGuid, storeGuid, summaryReloadKey, applySummary, message, t])

  // ---------- 搜索防抖 ----------
  useEffect(() => {
    const trimmed = searchText.trim()
    if (trimmed === keyword) return
    const timer = window.setTimeout(() => {
      setKeyword(trimmed)
      setPage(1)
    }, SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [searchText, keyword])

  // ---------- 加载商品（服务端分页） ----------
  useEffect(() => {
    if (!storeGuid || !formGuid) return
    const controller = new AbortController()
    setItemsLoading(true)
    getSupplierPreorderOrderingItems(
      formGuid,
      { storeGuid, page, pageSize, keyword: keyword || undefined, category, filter, sort },
      controller.signal,
    )
      .then((result) => {
        setRows(result.items)
        setTotal(result.total)
      })
      .catch((error: unknown) => {
        if (isAbortError(error)) return
        message.error(getErrorMessage(error, t(`${NS}.table.loadFailed`)))
      })
      .finally(() => {
        if (!controller.signal.aborted) setItemsLoading(false)
      })
    return () => controller.abort()
  }, [formGuid, storeGuid, page, pageSize, keyword, category, filter, sort, itemsReloadKey, message, t])

  // ---------- 交互 ----------
  const changeStore = (nextGuid: string) => {
    if (nextGuid === storeGuid) return
    // 先把当前分店防抖中的保存发出去；行键带分店，结果仍会正确归属
    queue.flushAll()
    setDrafts({})
    setSummary(null)
    setRows([])
    setPage(1)
    setStoreGuid(nextGuid)
    safeStorageSet(storage, storeStorageKey(formGuid), nextGuid)
  }

  const onCommit = useCallback(
    (row: SupplierPreorderOrderingItem, cartons: number) => {
      const currentStore = storeGuidRef.current
      if (!currentStore || !canEditRef.current) return
      setDrafts((prev) => ({ ...prev, [row.itemGuid]: cartons }))
      queue.schedule(makeLineKey(currentStore, row.itemGuid), cartons)
    },
    [queue],
  )

  const onInvalidInput = useCallback(() => message.warning(t(`${NS}.table.invalidInput`)), [message, t])

  const columns = useMemo(
    () =>
      buildOrderingColumns({
        t,
        canViewOtherStores,
        canEdit,
        getDisplayCartons: (row) => drafts[row.itemGuid] ?? row.myCartons,
        isSaving: (itemGuid) => (storeGuid ? savingKeys.has(makeLineKey(storeGuid, itemGuid)) : false),
        onCommit,
        onInvalidInput,
      }),
    [t, canViewOtherStores, canEdit, drafts, savingKeys, storeGuid, onCommit, onInvalidInput],
  )

  const currentStoreBusy = storeGuid ? [...savingKeys].some((key) => parseLineKey(key).storeGuid === storeGuid) : false

  const everSubmitted = Boolean(summary?.submittedAtUtc ?? selectedStore?.submittedAtUtc)
  const isSubmitted = summary?.orderStatus === 'Submitted'
  // 已提交后再改会回到草稿，按钮变「重新提交」
  const submitLabel = isSubmitted ? t(`${NS}.page.submitted`) : everSubmitted ? t(`${NS}.page.resubmit`) : t(`${NS}.page.submit`)

  const handleExport = async () => {
    if (!storeGuid) return
    if (queue.hasBusy()) {
      // 导出前确保箱数都已落库
      queue.flushAll()
      message.info(t(`${NS}.page.savingWait`))
      return
    }
    setExporting(true)
    try {
      await downloadSupplierPreorderStoreExport(formGuid, storeGuid)
    } catch (error) {
      message.error(getErrorMessage(error, t(`${NS}.page.exportFailed`)))
    } finally {
      if (mountedRef.current) setExporting(false)
    }
  }

  const handleSubmit = () => {
    if (!storeGuid || !selectedStore || !summary) return
    if (currentStoreBusy) {
      queue.flushAll()
      message.info(t(`${NS}.page.savingWait`))
      return
    }
    const targetStore = storeGuid
    modal.confirm({
      title: t(`${NS}.page.submitConfirmTitle`, { store: selectedStore.storeName }),
      content: (
        <div className="sp-order-number">
          <div>
            {t(`${NS}.page.submitConfirmItems`)}：<b>{formatInteger(summary.itemCount)}</b>
          </div>
          <div>
            {t(`${NS}.page.submitConfirmCartons`)}：<b>{formatInteger(summary.cartons)}</b>
          </div>
          <div>
            {t(`${NS}.page.submitConfirmAmount`)}：<b>{formatAud(summary.amount)}</b>
          </div>
          <div className="sp-order-muted sp-order-small" style={{ marginTop: 8 }}>
            {t(`${NS}.page.submitConfirmNote`)}
          </div>
        </div>
      ),
      okText: submitLabel,
      onOk: async () => {
        setSubmitting(true)
        try {
          const result = await submitSupplierPreorderStoreOrder(formGuid, targetStore)
          // 提交结果晚于此前所有保存
          lastSummarySeqRef.current.set(targetStore, saveSeqRef.current)
          applySummary(result)
          message.success(t(`${NS}.page.submitOk`))
        } catch (error) {
          message.error(getErrorMessage(error, t(`${NS}.page.submitFailed`)))
        } finally {
          if (mountedRef.current) setSubmitting(false)
        }
      },
    })
  }

  // ---------- 渲染 ----------
  if (contextLoading && !context) {
    return (
      <div className="page-container sp-order-page">
        <div className="sp-order-state" style={{ textAlign: 'center' }}>
          <Spin />
        </div>
      </div>
    )
  }

  const backButton = (
    <Button type="link" className="sp-order-back" icon={<ArrowLeftOutlined />} onClick={() => navigate('/supplier-preorder-ordering')}>
      {t(`${NS}.page.back`)}
    </Button>
  )

  if (contextError || !context || !form) {
    return (
      <div className="page-container sp-order-page">
        {backButton}
        <div className="sp-order-state">
          <Result
            status="warning"
            title={t(`${NS}.page.loadFailed`)}
            subTitle={contextError ?? undefined}
            extra={<Button onClick={() => setContextReloadKey((value) => value + 1)}>{t(`${NS}.list.retry`)}</Button>}
          />
        </div>
      </div>
    )
  }

  if (!form.myStores.length) {
    return (
      <div className="page-container sp-order-page">
        {backButton}
        <div className="sp-order-state">
          <Result status="info" title={form.title} subTitle={t(`${NS}.page.noStores`)} />
        </div>
      </div>
    )
  }

  const filterOptions: { label: string; value: SupplierPreorderOrderingFilter }[] = [
    { label: t(`${NS}.toolbar.filterAll`), value: 'all' },
    { label: t(`${NS}.toolbar.filterMine`), value: 'mine' },
    ...(canViewOtherStores ? [{ label: t(`${NS}.toolbar.filterOthers`), value: 'others' as const }] : []),
    { label: t(`${NS}.toolbar.filterNew`), value: 'new' },
    { label: t(`${NS}.toolbar.filterMatched`), value: 'matched' },
  ]
  const sortOptions: { label: string; value: SupplierPreorderOrderingSort }[] = [
    { label: t(`${NS}.toolbar.sortSeq`), value: 'seq' },
    { label: t(`${NS}.toolbar.sortPriceAsc`), value: 'priceAsc' },
    { label: t(`${NS}.toolbar.sortPriceDesc`), value: 'priceDesc' },
    ...(canViewOtherStores ? [{ label: t(`${NS}.toolbar.sortOthersDesc`), value: 'othersDesc' as const }] : []),
  ]

  const readOnlyReason = selectedStore && !selectedStore.canEdit ? selectedStore.readOnlyReason?.trim() || t(`${NS}.page.readOnlyDefault`) : null
  // 加载时服务器判定可编辑、但本地时钟已到截止：提示刷新
  const showDeadlineReached = lockedByClock && Boolean(selectedStore?.canEdit)

  const subtitleParts = [
    form.supplierName,
    formatHolidayYear(t, form.holidayType, form.seasonYear),
    form.offerDate ? t(`${NS}.page.offerDate`, { date: formatBusinessDate(form.offerDate) }) : null,
    t(`${NS}.page.itemCount`, { value: formatInteger(form.itemCount) }),
    t(`${NS}.page.orderRule`),
  ].filter(Boolean)

  return (
    <div className="page-container sp-order-page">
      {backButton}

      <section className="sp-order-head-card">
        <div className="sp-order-head">
          <div className="sp-order-head-main">
            <div className="sp-order-head-title">
              <h1>{form.title}</h1>
              <FormStatusTag t={t} status={form.status} isLocked={form.isLocked} />
              {form.quoteNo ? <Tag>{t(`${NS}.page.quoteNo`, { value: form.quoteNo })}</Tag> : null}
              {form.containerType ? <Tag>{form.containerType}</Tag> : null}
              {form.priceTerms ? <Tag>{form.priceTerms}</Tag> : null}
            </div>
            <div className="sp-order-head-sub">{subtitleParts.join(' · ')}</div>
          </div>
          <div className="sp-order-head-actions">
            {form.deadlineAtUtc || form.isLocked ? (
              <div className="sp-order-deadline-box">
                <span className="sp-order-muted sp-order-small">{t(`${NS}.page.deadlineLabel`)}</span>
                <DeadlineText t={t} deadlineAtUtc={form.deadlineAtUtc} state={deadlineState} />
              </div>
            ) : null}
            <Select
              className="sp-order-store-select"
              value={storeGuid ?? undefined}
              onChange={changeStore}
              aria-label={t(`${NS}.page.storeLabel`)}
              prefix={<span className="sp-order-muted">{t(`${NS}.page.storeLabel`)}</span>}
              popupMatchSelectWidth={false}
              options={form.myStores.map((store) => ({
                value: store.storeGuid,
                label: (
                  <span className="sp-order-store-option">
                    <span>{store.storeName}</span>
                    <span>
                      {!store.canEdit ? <Tag>{t(`${NS}.page.storeReadOnlyTag`)}</Tag> : null}
                      <StoreStatusTag t={t} status={store.orderStatus} />
                    </span>
                  </span>
                ),
              }))}
              labelRender={({ value }) => form.myStores.find((store) => store.storeGuid === value)?.storeName ?? String(value ?? '')}
            />
            <Button icon={<DownloadOutlined />} loading={exporting} disabled={!storeGuid} onClick={handleExport}>
              {t(`${NS}.page.export`)}
            </Button>
            <Button type="primary" loading={submitting} disabled={!canEdit || !summary || isSubmitted} onClick={handleSubmit}>
              {submitLabel}
            </Button>
          </div>
        </div>

        {showDeadlineReached ? (
          <Alert
            className="sp-order-alert"
            type="warning"
            showIcon
            message={t(`${NS}.page.deadlineReached`)}
            action={
              <Button
                size="small"
                onClick={() => {
                  // 仓库约定页面不做整页刷新：只重新拉取上下文、列表和指标条
                  setContextReloadKey((value) => value + 1)
                  setItemsReloadKey((value) => value + 1)
                  setSummaryReloadKey((value) => value + 1)
                }}
              >
                {t(`${NS}.page.refresh`)}
              </Button>
            }
          />
        ) : readOnlyReason ? (
          <Alert className="sp-order-alert" type="info" showIcon message={readOnlyReason} />
        ) : null}

        <OrderingSummaryBar t={t} summary={summary} containerType={form.containerType} containerCapacityCbm={form.containerCapacityCbm} />
      </section>

      <section className="sp-order-table-card">
        <div className="sp-order-toolbar">
          <Input
            className="sp-order-search"
            allowClear
            prefix={<SearchOutlined className="sp-order-muted" />}
            placeholder={t(`${NS}.toolbar.searchPlaceholder`)}
            value={searchText}
            onChange={(event) => setSearchText(event.target.value)}
          />
          {context.categories.length ? (
            <Select
              className="sp-order-category"
              allowClear
              showSearch
              placeholder={t(`${NS}.toolbar.categoryAll`)}
              value={category}
              onChange={(value?: string) => {
                setCategory(value || undefined)
                setPage(1)
              }}
              options={context.categories.map((name) => ({ value: name, label: name }))}
            />
          ) : null}
          <Segmented<SupplierPreorderOrderingFilter>
            value={filter}
            options={filterOptions}
            onChange={(value) => {
              setFilter(value)
              setPage(1)
            }}
          />
          <Select<SupplierPreorderOrderingSort>
            className="sp-order-sort"
            value={sort}
            options={sortOptions}
            onChange={(value) => {
              setSort(value)
              setPage(1)
            }}
          />
          <Button type="link" size="small" onClick={() => { setItemsReloadKey((value) => value + 1); setSummaryReloadKey((value) => value + 1) }}>
            {t(`${NS}.page.refresh`)}
          </Button>
        </div>
        <MeasuredTable<SupplierPreorderOrderingItem>
          metricId="supplier-preorder-ordering-items"
          className="sp-order-table"
          rowKey="itemGuid"
          size="middle"
          loading={itemsLoading}
          columns={columns}
          dataSource={rows}
          scroll={{ x: canViewOtherStores ? 1060 : 860 }}
          rowClassName={(row) => ((drafts[row.itemGuid] ?? row.myCartons) > 0 ? 'sp-order-row-mine' : '')}
          locale={{ emptyText: t(`${NS}.table.empty`) }}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            showQuickJumper: true,
            pageSizeOptions: ORDERING_PAGE_SIZES.map(String),
            showTotal: (count, [from, to]) => t(`${NS}.table.total`, { total: formatInteger(count), from, to }),
            onChange: (nextPage, nextSize) => {
              const normalized = normalizePageSize(nextSize)
              if (normalized !== pageSize) {
                // 切换每页条数：记到本机，回到第 1 页
                setPageSize(normalized)
                safeStorageSet(storage, PAGE_SIZE_STORAGE_KEY, String(normalized))
                setPage(1)
                return
              }
              setPage(nextPage)
            },
          }}
        />
      </section>
    </div>
  )
}
