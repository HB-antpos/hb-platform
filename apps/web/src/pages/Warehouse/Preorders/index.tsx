import {
  ArrowRightOutlined,
  EllipsisOutlined,
  PlusOutlined,
  RightOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Alert,
  App,
  Button,
  Card,
  ConfigProvider,
  DatePicker,
  Dropdown,
  Form,
  Image,
  Input,
  InputNumber,
  Modal,
  Segmented,
  Select,
  Spin,
  Switch,
  Tooltip,
  Typography,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import enUS from 'antd/locale/en_US'
import zhCN from 'antd/locale/zh_CN'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getPreorderDateDisplay } from '../../ShopPreorder/preorderDate'
import {
  activatePreorderTemplate,
  createPreorderTemplate,
  getPreorderTemplate,
  getPreorderTemplates,
  getTemplateActivations,
  resolvePreorderItems,
  updatePreorderTemplate,
} from '../../../services/preorderService'
import { getStores } from '../../../services/storeService'
import type {
  PreorderActivationSummary,
  PreorderResolvedItem,
  PreorderTemplateDetail,
  PreorderTemplateSummary,
} from '../../../types/preorder'
import type { StoreDto } from '../../../types/store'
import {
  applyPreorderPasteTextChange,
  canSavePreorderTemplate,
  parsePreorderPaste,
  removePreorderPasteItem,
} from './preorderPaste'
import {
  beginModalRequest,
  createModalRequestGuard,
  invalidateModalRequest,
  isCurrentModalRequest,
} from './modalRequestGuard'
import {
  PREORDER_LIVE_CONCURRENCY,
  collectLiveActivations,
  countTemplatesByStatus,
  filterTemplates,
  formatCompactDate,
  formatCompactDateTime,
  formatMonthDayWeekdayTime,
  getActivationProgress,
  getActivationStatusTone,
  getActivationTiming,
  pickCurrentActivation,
  resolveLatestActivationMs,
  runWithConcurrency,
  selectLiveBatchCandidates,
  type PreorderTemplateStatusFilter,
} from './preordersPage.logic'
import preordersMessagesEn from './preordersMessages.en.json'
import preordersMessagesZh from './preordersMessages.zh.json'
import './styles.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

// 页面重设计新增的文案随页面懒注册，不放进首屏全局语言包。
registerPageMessages({ zh: preordersMessagesZh, en: preordersMessagesEn })

const { Text, Title } = Typography
const { RangePicker } = DatePicker

interface TemplateFormValues {
  name: string
  isEnabled: boolean
  notes?: string
  storeGuids: string[]
}

interface ActivationFormValues {
  range: [Dayjs, Dayjs]
  estimatedArrivalDate?: Dayjs | null
  storeGuids: string[]
}

/** 批次加载状态：done 后 complete 表示所有候选模板都拿到了批次，页头才显示期数。 */
interface LiveActivationLoadState {
  done: boolean
  complete: boolean
  /** 本轮拉过批次的模板（不在其中且有批次的模板视为没有进行中的批次）。 */
  candidateGuids: ReadonlySet<string>
}

const initialLiveActivationLoadState: LiveActivationLoadState = { done: false, complete: false, candidateGuids: new Set() }

/** 分店处理进度条（已处理 / 未提交两段）：批次卡片与历届批次共用。 */
function ActivationProgressBar({ activation, compact = false }: { activation: PreorderActivationSummary; compact?: boolean }) {
  const progress = getActivationProgress(activation)
  return (
    <span className={`wh-preorders-progress${compact ? ' wh-preorders-progress-compact' : ''}`} aria-hidden="true">
      <span className="wh-preorders-progress-fill" style={{ width: `${progress.respondedPercent}%` }} />
    </span>
  )
}

export default function PreordersPage() {
  const { message } = App.useApp()
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const [form] = Form.useForm<TemplateFormValues>()
  const [activationForm] = Form.useForm<ActivationFormValues>()
  const [templates, setTemplates] = useState<PreorderTemplateSummary[]>([])
  const [templatesLoaded, setTemplatesLoaded] = useState(false)
  const [stores, setStores] = useState<StoreDto[]>([])
  const [loading, setLoading] = useState(false)
  const [editorOpen, setEditorOpen] = useState(false)
  const [editorLoading, setEditorLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState<PreorderTemplateDetail | null>(null)
  const [pasteText, setPasteText] = useState('')
  const [pasteErrors, setPasteErrors] = useState<string[]>([])
  const [items, setItems] = useState<PreorderResolvedItem[]>([])
  const [resolving, setResolving] = useState(false)
  const [activationOpen, setActivationOpen] = useState(false)
  const [activationLoading, setActivationLoading] = useState(false)
  const [activating, setActivating] = useState(false)
  const [activationTemplate, setActivationTemplate] = useState<PreorderTemplateDetail | null>(null)
  // 历届批次由原「批次」弹窗改为模板行内展开，一次只展开一个模板，沿用同一个请求守卫。
  const [expandedTemplateGuid, setExpandedTemplateGuid] = useState<string | null>(null)
  const [historyLoading, setHistoryLoading] = useState(false)
  // 按模板缓存的批次列表：首屏批次概览与行内历届批次共用，同一接口返回的是该模板的全部批次。
  const [activationsByTemplate, setActivationsByTemplate] = useState<Record<string, PreorderActivationSummary[]>>({})
  const [liveLoadState, setLiveLoadState] = useState<LiveActivationLoadState>(initialLiveActivationLoadState)
  const [keyword, setKeyword] = useState('')
  const [statusFilter, setStatusFilter] = useState<PreorderTemplateStatusFilter>('all')
  const [nowMs, setNowMs] = useState(() => Date.now())
  const editorRequestGuardRef = useRef(createModalRequestGuard())
  const pasteRequestGuardRef = useRef(createModalRequestGuard())
  const activationRequestGuardRef = useRef(createModalRequestGuard())
  const historyRequestGuardRef = useRef(createModalRequestGuard())
  const liveRequestGuardRef = useRef(createModalRequestGuard())
  const language = i18n.resolvedLanguage || i18n.language
  const dateTimeFormatter = useMemo(
    () => new Intl.DateTimeFormat(language, { dateStyle: 'medium', timeStyle: 'short' }),
    [language],
  )
  const antdLocale = i18n.resolvedLanguage === 'en' ? enUS : zhCN

  /**
   * 拉「进行中与待开始」批次：只对候选模板用现有「按模板查批次」接口并发拉取（并发 4）。
   * 每次重新加载模板都会作废上一轮；单个模板失败静默降级，只是页头不再显示期数。
   */
  const loadLiveActivations = useCallback(async (nextTemplates: PreorderTemplateSummary[]) => {
    const requestToken = beginModalRequest(liveRequestGuardRef.current)
    const { templateGuids, truncated } = selectLiveBatchCandidates(nextTemplates, Date.now())
    setLiveLoadState(initialLiveActivationLoadState)
    const results = await runWithConcurrency(templateGuids, PREORDER_LIVE_CONCURRENCY, (templateGuid) => {
      if (requestToken.signal.aborted) return Promise.reject(new DOMException('Aborted', 'AbortError'))
      return getTemplateActivations(templateGuid, requestToken.signal)
    })
    if (!isCurrentModalRequest(liveRequestGuardRef.current, requestToken)) return
    const loaded: Record<string, PreorderActivationSummary[]> = {}
    let failedCount = 0
    results.forEach((result, index) => {
      if (result.status === 'fulfilled') loaded[templateGuids[index]] = result.value
      else failedCount += 1
    })
    setActivationsByTemplate((current) => ({ ...current, ...loaded }))
    setNowMs(Date.now())
    setLiveLoadState({
      done: true,
      complete: failedCount === 0 && !truncated,
      candidateGuids: new Set(templateGuids),
    })
  }, [])

  const loadTemplates = useCallback(async () => {
    setLoading(true)
    try {
      const nextTemplates = await getPreorderTemplates()
      setTemplates(nextTemplates)
      setTemplatesLoaded(true)
      void loadLiveActivations(nextTemplates)
    } catch {
      message.error(t('warehouse.preorders.templateLoadFailed'))
    } finally {
      setLoading(false)
    }
  }, [loadLiveActivations, message, t])

  useEffect(() => {
    void loadTemplates()
    const loadStores = async () => {
      const allStores: StoreDto[] = []
      let page = 1
      let total = 0
      do {
        const result = await getStores({ page, pageSize: 100, isActive: true, sortField: 'storeName', sortOrder: 'asc' })
        allStores.push(...result.items)
        total = result.total
        if (!result.items.length) break
        page += 1
      } while (allStores.length < total)
      return allStores
    }
    void loadStores()
      .then(setStores)
      .catch(() => message.warning(t('warehouse.preorders.storeLoadFailed')))
  }, [loadTemplates, message, t])

  useEffect(() => () => {
    invalidateModalRequest(editorRequestGuardRef.current)
    invalidateModalRequest(pasteRequestGuardRef.current)
    invalidateModalRequest(activationRequestGuardRef.current)
    invalidateModalRequest(historyRequestGuardRef.current)
    invalidateModalRequest(liveRequestGuardRef.current)
  }, [])

  // 「剩 X 天」按日历天计算，页面长时间开着时每分钟刷新一次当前时间。
  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), 60_000)
    return () => window.clearInterval(timer)
  }, [])

  const storeOptions = useMemo(() => stores.map((store) => ({
    value: store.storeGUID,
    label: `${store.storeName || store.storeCode} (${store.storeCode})`,
  })), [stores])

  const openCreate = () => {
    // 新开创建弹窗前先使正在加载的旧模板失效，避免 A 模板慢响应覆盖创建表单。
    invalidateModalRequest(editorRequestGuardRef.current)
    invalidateModalRequest(pasteRequestGuardRef.current)
    setEditing(null)
    setItems([])
    setPasteText('')
    setPasteErrors([])
    setEditorLoading(false)
    setResolving(false)
    setSaving(false)
    form.resetFields()
    form.setFieldsValue({ name: '', isEnabled: true, notes: '', storeGuids: [] })
    setEditorOpen(true)
  }

  const closeEditor = () => {
    invalidateModalRequest(editorRequestGuardRef.current)
    invalidateModalRequest(pasteRequestGuardRef.current)
    setEditorOpen(false)
    setEditorLoading(false)
    setResolving(false)
    setSaving(false)
    setEditing(null)
    setItems([])
    setPasteText('')
    setPasteErrors([])
    form.resetFields()
  }

  const openEdit = async (row: PreorderTemplateSummary) => {
    const requestToken = beginModalRequest(editorRequestGuardRef.current)
    invalidateModalRequest(pasteRequestGuardRef.current)
    setEditing(null)
    setItems([])
    setPasteText('')
    setPasteErrors([])
    setSaving(false)
    setResolving(false)
    form.resetFields()
    setEditorLoading(true)
    setEditorOpen(true)
    try {
      const detail = await getPreorderTemplate(row.templateGuid, requestToken.signal)
      if (!isCurrentModalRequest(editorRequestGuardRef.current, requestToken)) return
      setEditing(detail)
      setItems(detail.items.map((item, index) => ({ ...item, lineNumber: index + 1, valid: true })))
      setPasteText('')
      setPasteErrors([])
      form.setFieldsValue({
        name: detail.name,
        isEnabled: detail.isEnabled,
        notes: detail.notes,
        storeGuids: detail.stores.map((store) => store.storeGuid),
      })
    } catch {
      if (isCurrentModalRequest(editorRequestGuardRef.current, requestToken)) {
        closeEditor()
        message.error(t('warehouse.preorders.templateDetailLoadFailed'))
      }
    } finally {
      if (isCurrentModalRequest(editorRequestGuardRef.current, requestToken)) {
        setEditorLoading(false)
      }
    }
  }

  const resolvePaste = async () => {
    const parsed = parsePreorderPaste(
      pasteText,
      (key, values) => t(`warehouse.preorders.paste.${key}`, values),
    )
    invalidateModalRequest(pasteRequestGuardRef.current)
    setPasteErrors(parsed.errors)
    if (parsed.errors.length) {
      setResolving(false)
      return
    }
    const requestToken = beginModalRequest(pasteRequestGuardRef.current)
    setResolving(true)
    try {
      const resolved = await resolvePreorderItems(parsed.rows, requestToken.signal)
      if (!isCurrentModalRequest(pasteRequestGuardRef.current, requestToken)) return
      setItems(resolved)
      const errors = resolved.filter((item) => !item.valid).map((item) => {
        const messageKey = item.errorCode === 'PREORDER_INVALID_REQUEST'
          ? 'invalidRequest'
          : item.errorCode === 'PREORDER_MOQ_CONFLICT'
            ? 'resolvedMoqConflict'
            : item.errorCode === 'PREORDER_ITEM_AMBIGUOUS'
              ? 'itemAmbiguous'
              : 'itemNotFound'
        return t('warehouse.preorders.paste.resolvedError', {
          lineNumber: item.lineNumber,
          message: t(`warehouse.preorders.paste.${messageKey}`),
        })
      })
      setPasteErrors(errors)
      if (!errors.length) message.success(t('warehouse.preorders.paste.parsedCount', { count: resolved.length }))
    } catch {
      if (isCurrentModalRequest(pasteRequestGuardRef.current, requestToken)) {
        message.error(t('warehouse.preorders.paste.resolveFailed'))
      }
    } finally {
      if (isCurrentModalRequest(pasteRequestGuardRef.current, requestToken)) {
        setResolving(false)
      }
    }
  }

  const saveTemplate = async () => {
    const requestVersion = editorRequestGuardRef.current.version
    const values = await form.validateFields()
    if (editorRequestGuardRef.current.version !== requestVersion) return
    if (!canSavePreorderTemplate(items, pasteErrors)) {
      if (!pasteErrors.length) {
        setPasteErrors([t('warehouse.preorders.paste.validItemRequired')])
      }
      return
    }
    setSaving(true)
    try {
      const payload = {
        ...values,
        notes: values.notes?.trim() || undefined,
        expectedRevision: editing?.revision,
        items: items.map((item, index) => ({
          productCode: item.productCode!,
          minimumOrderQuantity: item.minimumOrderQuantity,
          sortOrder: index,
        })),
      }
      if (editing) await updatePreorderTemplate(editing.templateGuid, payload)
      else await createPreorderTemplate(payload)
      if (editorRequestGuardRef.current.version !== requestVersion) return
      message.success(editing ? t('warehouse.preorders.templateUpdated') : t('warehouse.preorders.templateCreated'))
      closeEditor()
      await loadTemplates()
    } catch {
      if (editorRequestGuardRef.current.version === requestVersion) {
        message.error(t('warehouse.preorders.templateSaveFailed'))
      }
    } finally {
      if (editorRequestGuardRef.current.version === requestVersion) {
        setSaving(false)
      }
    }
  }

  const openActivation = async (row: PreorderTemplateSummary) => {
    const requestToken = beginModalRequest(activationRequestGuardRef.current)
    setActivationTemplate(null)
    setActivationLoading(true)
    setActivating(false)
    activationForm.resetFields()
    setActivationOpen(true)
    try {
      const detail = await getPreorderTemplate(row.templateGuid, requestToken.signal)
      if (!isCurrentModalRequest(activationRequestGuardRef.current, requestToken)) return
      setActivationTemplate(detail)
      activationForm.setFieldsValue({
        range: [dayjs().add(5, 'minute'), dayjs().add(7, 'day')],
        estimatedArrivalDate: null,
        storeGuids: detail.stores.map((store) => store.storeGuid),
      })
    } catch {
      if (isCurrentModalRequest(activationRequestGuardRef.current, requestToken)) {
        setActivationOpen(false)
        setActivationTemplate(null)
        message.error(t('warehouse.preorders.templateDetailLoadFailed'))
      }
    } finally {
      if (isCurrentModalRequest(activationRequestGuardRef.current, requestToken)) {
        setActivationLoading(false)
      }
    }
  }

  const closeActivationModal = () => {
    invalidateModalRequest(activationRequestGuardRef.current)
    setActivationOpen(false)
    setActivationLoading(false)
    setActivating(false)
    setActivationTemplate(null)
    activationForm.resetFields()
  }

  const createActivation = async () => {
    if (!activationTemplate) return
    const requestVersion = activationRequestGuardRef.current.version
    const values = await activationForm.validateFields()
    if (activationRequestGuardRef.current.version !== requestVersion) return
    if (!values.range[1].isAfter(values.range[0])) {
      message.warning(t('warehouse.preorders.endAfterStart'))
      return
    }
    setActivating(true)
    try {
      await activatePreorderTemplate(activationTemplate.templateGuid, {
        expectedRevision: activationTemplate.revision,
        startAtUtc: values.range[0].toISOString(),
        endAtUtc: values.range[1].toISOString(),
        // DateOnly 必须直接发送日历日期，禁止经 UTC 转换造成前后偏移。
        estimatedArrivalDate: values.estimatedArrivalDate?.format('YYYY-MM-DD') ?? null,
        storeGuids: values.storeGuids,
      })
      if (activationRequestGuardRef.current.version !== requestVersion) return
      message.success(t('warehouse.preorders.activationCreated'))
      closeActivationModal()
      await loadTemplates()
    } catch {
      if (activationRequestGuardRef.current.version === requestVersion) {
        message.error(t('warehouse.preorders.activationFailedOverlap'))
      }
    } finally {
      if (activationRequestGuardRef.current.version === requestVersion) {
        setActivating(false)
      }
    }
  }

  const openHistory = async (row: PreorderTemplateSummary) => {
    const requestToken = beginModalRequest(historyRequestGuardRef.current)
    setExpandedTemplateGuid(row.templateGuid)
    setHistoryLoading(true)
    try {
      // 每次展开都重新拉一次：缓存里已有该模板批次时先显示缓存，响应回来后刷新。
      const next = await getTemplateActivations(row.templateGuid, requestToken.signal)
      if (!isCurrentModalRequest(historyRequestGuardRef.current, requestToken)) return
      setActivationsByTemplate((current) => ({ ...current, [row.templateGuid]: next }))
      setNowMs(Date.now())
    } catch {
      if (isCurrentModalRequest(historyRequestGuardRef.current, requestToken)) {
        message.error(t('warehouse.preorders.historyLoadFailed'))
      }
    } finally {
      if (isCurrentModalRequest(historyRequestGuardRef.current, requestToken)) {
        setHistoryLoading(false)
      }
    }
  }

  const closeHistory = () => {
    invalidateModalRequest(historyRequestGuardRef.current)
    setExpandedTemplateGuid(null)
    setHistoryLoading(false)
  }

  const toggleHistory = (row: PreorderTemplateSummary) => {
    if (expandedTemplateGuid === row.templateGuid) closeHistory()
    else void openHistory(row)
  }

  const openActivationDetail = (activationGuid: string) => navigate(`/warehouse/preorders/activations/${activationGuid}`)
  const templateNameByGuid = useMemo(
    () => new Map(templates.map((template) => [template.templateGuid, template.name])),
    [templates],
  )
  const templateCounts = useMemo(() => countTemplatesByStatus(templates), [templates])
  const visibleTemplates = useMemo(
    () => filterTemplates(templates, keyword, statusFilter),
    [keyword, statusFilter, templates],
  )
  const liveActivations = useMemo(() => collectLiveActivations(activationsByTemplate), [activationsByTemplate])
  const activeCount = liveActivations.filter((activation) => activation.status === 'Active').length
  const scheduledCount = liveActivations.length - activeCount
  // 批次数据完整取得后才在页头显示期数，部分失败时宁可不显示也不给出偏小的数字。
  const subtitle = templatesLoaded
    ? [
        t('warehouseUi.preorders.subtitleTemplates', { count: templates.length }),
        ...(liveLoadState.done && liveLoadState.complete
          ? [
              t('warehouseUi.preorders.subtitleActive', { count: activeCount }),
              t('warehouseUi.preorders.subtitleScheduled', { count: scheduledCount }),
            ]
          : []),
      ].join(' · ')
    : undefined

  const getStatusLabel = (status: PreorderActivationSummary['status']) => t(`warehouse.preorders.activationStatus.${status}`)

  const formatTimingText = (activation: PreorderActivationSummary) => {
    const timing = getActivationTiming(activation, nowMs)
    if (timing.kind === 'closesToday') {
      return t('warehouseUi.preorders.timingClosesToday', { time: dayjs(activation.endAtUtc).format('HH:mm') })
    }
    if (timing.kind === 'closesIn') {
      return t('warehouseUi.preorders.timingClosesIn', {
        time: formatMonthDayWeekdayTime(Date.parse(activation.endAtUtc), language),
        count: timing.days,
      })
    }
    if (timing.kind === 'starts') {
      return t('warehouseUi.preorders.timingStarts', {
        time: formatMonthDayWeekdayTime(Date.parse(activation.startAtUtc), language),
        count: timing.durationDays,
      })
    }
    return ''
  }

  /** 「当前批次」列：已拿到该模板批次时显示当前批次；确认没有时显示「无 · 共 N 期」；拿不到时只显示总期数。 */
  const renderCurrentBatch = (row: PreorderTemplateSummary) => {
    const loadedActivations = activationsByTemplate[row.templateGuid]
    const current = loadedActivations ? pickCurrentActivation(loadedActivations) : undefined
    if (current) {
      const timing = getActivationTiming(current, nowMs)
      const progress = getActivationProgress(current)
      const brief = timing.kind === 'closesIn'
        ? `${t('warehouseUi.preorders.briefDaysLeft', { count: timing.days })} · ${progress.responded}/${progress.target}`
        : timing.kind === 'closesToday'
          ? `${t('warehouseUi.preorders.briefClosesToday')} · ${progress.responded}/${progress.target}`
          : timing.kind === 'starts'
            ? t('warehouseUi.preorders.briefStarts', { date: formatCompactDate(Date.parse(current.startAtUtc), nowMs) })
            : ''
      return (
        <div className="wh-preorders-current">
          <StatusPill tone={getActivationStatusTone(current.status)}>
            {t('warehouseUi.preorders.currentBadge', { sequence: current.sequenceNumber, status: getStatusLabel(current.status) })}
          </StatusPill>
          {brief ? <span className={`wh-preorders-current-brief${timing.urgent ? ' is-urgent' : ''}`}>{brief}</span> : null}
        </div>
      )
    }
    const knownNone = Boolean(loadedActivations)
      || row.activationCount <= 0
      || (liveLoadState.done && !liveLoadState.candidateGuids.has(row.templateGuid))
    return (
      <span className="wh-preorders-muted">
        {knownNone
          ? t('warehouseUi.preorders.currentNone', { count: row.activationCount })
          : t('warehouseUi.preorders.currentUnknown', { count: row.activationCount })}
      </span>
    )
  }

  const columns: ColumnsType<PreorderTemplateSummary> = [
    {
      title: t('warehouseUi.preorders.colTemplate'),
      key: 'template',
      render: (_, row) => (
        <div className="wh-preorders-template">
          <div className="wh-preorders-template-line">
            <span className={`wh-preorders-template-name${row.isEnabled ? '' : ' is-disabled'}`}>{row.name}</span>
            <span className="wh-preorders-muted wh-preorders-small">{t('warehouseUi.preorders.revisionShort', { revision: row.revision })}</span>
          </div>
          <div className="wh-preorders-template-notes">{row.notes?.trim() || t('warehouseUi.preorders.noNotes')}</div>
        </div>
      ),
    },
    {
      title: t('warehouse.preorders.status'),
      key: 'status',
      width: 88,
      render: (_, row) => (
        <StatusPill tone={row.isEnabled ? 'green' : 'gray'}>
          {row.isEnabled ? t('warehouse.preorders.enabled') : t('warehouse.preorders.disabled')}
        </StatusPill>
      ),
    },
    { title: t('warehouse.preorders.products'), dataIndex: 'itemCount', width: 72, align: 'right', className: 'wh-preorders-number' },
    { title: t('warehouse.preorders.defaultStores'), dataIndex: 'storeCount', width: 88, align: 'right', className: 'wh-preorders-number' },
    { title: t('warehouseUi.preorders.colCurrentBatch'), key: 'currentBatch', width: 250, render: (_, row) => renderCurrentBatch(row) },
    {
      title: t('warehouseUi.preorders.colLatestActivation'),
      key: 'latestActivation',
      width: 104,
      className: 'wh-preorders-number',
      render: (_, row) => {
        const latestMs = resolveLatestActivationMs(row, activationsByTemplate[row.templateGuid])
        return latestMs === undefined ? '--' : formatCompactDate(latestMs, nowMs)
      },
    },
    {
      title: t('warehouseUi.preorders.colUpdated'),
      dataIndex: 'updatedAt',
      width: 116,
      className: 'wh-preorders-number',
      render: (value?: string) => {
        const updatedMs = value ? Date.parse(value) : Number.NaN
        return Number.isFinite(updatedMs) ? formatCompactDateTime(updatedMs, nowMs) : '--'
      },
    },
    {
      title: t('warehouse.preorders.actions'),
      key: 'actions',
      width: 220,
      fixed: 'right',
      align: 'right',
      render: (_, row) => {
        const expanded = expandedTemplateGuid === row.templateGuid
        return (
          <div className="wh-preorders-row-actions">
            {/* 与原规则一致：只有模板停用时禁用激活。 */}
            <Tooltip title={row.isEnabled ? undefined : t('warehouseUi.preorders.activateDisabledHint')}>
              <Button size="small" type="primary" disabled={!row.isEnabled} onClick={() => void openActivation(row)}>
                {t('warehouse.preorders.activateNew')}
              </Button>
            </Tooltip>
            <Button size="small" autoInsertSpace={false} onClick={() => void openEdit(row)}>{t('warehouse.preorders.edit')}</Button>
            <Dropdown
              trigger={['click']}
              menu={{
                items: [{
                  key: 'history',
                  label: expanded ? t('warehouseUi.preorders.hideHistory') : t('warehouseUi.preorders.showHistory'),
                  disabled: row.activationCount <= 0,
                }],
                onClick: () => toggleHistory(row),
              }}
            >
              <Button size="small" type="text" icon={<EllipsisOutlined />} aria-label={t('warehouseUi.preorders.moreActions')} />
            </Dropdown>
          </div>
        )
      },
    },
  ]

  const historyColumns: ColumnsType<PreorderActivationSummary> = [
    { title: t('warehouse.preorders.periodNumber'), dataIndex: 'sequenceNumber', width: 80, render: (value) => <span className="wh-preorders-strong">{t('warehouse.preorders.period', { sequence: value })}</span> },
    { title: t('warehouse.preorders.activationNumber'), dataIndex: 'activationNumber', width: 150 },
    { title: t('warehouse.preorders.status'), dataIndex: 'status', width: 100, render: (status: PreorderActivationSummary['status']) => <StatusPill tone={getActivationStatusTone(status)}>{getStatusLabel(status)}</StatusPill> },
    { title: t('warehouse.preorders.effectiveTime'), width: 280, className: 'wh-preorders-number', render: (_, row) => `${dateTimeFormatter.format(new Date(row.startAtUtc))} — ${dateTimeFormatter.format(new Date(row.endAtUtc))}` },
    { title: t('warehouse.preorders.estimatedArrivalDate'), dataIndex: 'estimatedArrivalDate', width: 120, className: 'wh-preorders-number', render: (value) => getPreorderDateDisplay(value) ?? '--' },
    {
      title: t('warehouseUi.preorders.colStoreResponses'),
      key: 'progress',
      width: 180,
      render: (_, row) => {
        const progress = getActivationProgress(row)
        return (
          <div className="wh-preorders-history-progress">
            <ActivationProgressBar activation={row} compact />
            <span className="wh-preorders-number">{progress.responded}/{progress.target}</span>
          </div>
        )
      },
    },
    { title: '', key: 'view', width: 72, align: 'right', render: (_, row) => <Button type="link" size="small" onClick={() => openActivationDetail(row.activationGuid)}>{t('warehouse.preorders.view')}</Button> },
  ]

  const renderLiveCard = (activation: PreorderActivationSummary) => {
    const timing = getActivationTiming(activation, nowMs)
    const progress = getActivationProgress(activation)
    const arrival = getPreorderDateDisplay(activation.estimatedArrivalDate)
    // 进行中且还有分店没提交时，用琥珀色提醒追进度。
    const pendingAlert = activation.status === 'Active' && progress.pending > 0
    return (
      <article key={activation.activationGuid} className="wh-preorders-live-card">
        <div className="wh-preorders-live-head">
          <div className="wh-preorders-live-title">
            <div className="wh-preorders-live-name">{templateNameByGuid.get(activation.templateGuid) || activation.templateName}</div>
            <div className="wh-preorders-muted wh-preorders-small wh-preorders-number">
              {t('warehouseUi.preorders.liveMeta', { sequence: activation.sequenceNumber })}
              {arrival ? ` · ${t('warehouseUi.preorders.liveArrival', { date: arrival })}` : ''}
            </div>
            {/* 批次号可能很长（含日期与哈希后缀），单独一行并省略，避免把期号与到货日期挤断 */}
            <div className="wh-preorders-muted wh-preorders-small wh-preorders-number wh-preorders-live-batch" title={activation.activationNumber}>
              {activation.activationNumber}
            </div>
          </div>
          <StatusPill tone={getActivationStatusTone(activation.status)}>{getStatusLabel(activation.status)}</StatusPill>
        </div>
        <div className="wh-preorders-live-figures">
          <span className={`wh-preorders-small${timing.urgent ? ' is-urgent' : ''}`}>{formatTimingText(activation)}</span>
          <span className="wh-preorders-small wh-preorders-live-responded">
            <strong>{progress.responded}</strong> {t('warehouseUi.preorders.respondedOf', { target: progress.target })}
          </span>
        </div>
        <ActivationProgressBar activation={activation} />
        <div className="wh-preorders-live-legend">
          <span><span className="wh-preorders-swatch wh-preorders-swatch-responded" />{t('warehouseUi.preorders.legendResponded', { count: progress.responded })}</span>
          <span className={pendingAlert ? 'is-urgent' : undefined}>
            <span className="wh-preorders-swatch wh-preorders-swatch-pending" />{t('warehouseUi.preorders.legendPending', { count: progress.pending })}
          </span>
        </div>
        <div className="wh-preorders-live-foot">
          <Button type="link" size="small" className="wh-preorders-live-link" onClick={() => openActivationDetail(activation.activationGuid)}>
            {t('warehouseUi.preorders.viewBatch')}<ArrowRightOutlined />
          </Button>
        </div>
      </article>
    )
  }

  return (
    <ConfigProvider locale={antdLocale}>
      <PageContainer
        compact
        title={t('warehouse.preorders.title')}
        subtitle={subtitle}
        extra={<Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>{t('warehouse.preorders.createTemplate')}</Button>}
      >
      {liveActivations.length ? (
        <section className="wh-preorders-live" aria-label={t('warehouseUi.preorders.liveTitle')}>
          <div className="wh-preorders-section-head">
            <h2 className="wh-preorders-section-title">{t('warehouseUi.preorders.liveTitle')}</h2>
            <span className="wh-preorders-muted wh-preorders-small">{t('warehouseUi.preorders.liveHint')}</span>
          </div>
          <div className="wh-preorders-live-grid">
            {liveActivations.map(renderLiveCard)}
          </div>
        </section>
      ) : null}

      <section className="wh-preorders-card" aria-label={t('warehouseUi.preorders.tableTitle')}>
        <div className="wh-preorders-toolbar">
          <h2 className="wh-preorders-section-title">{t('warehouseUi.preorders.tableTitle')}</h2>
          <Input
            allowClear
            prefix={<SearchOutlined />}
            className="wh-preorders-search"
            placeholder={t('warehouseUi.preorders.searchPlaceholder')}
            aria-label={t('warehouseUi.preorders.searchPlaceholder')}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
          />
          <Segmented<PreorderTemplateStatusFilter>
            aria-label={t('warehouseUi.preorders.statusFilter')}
            value={statusFilter}
            onChange={(value) => setStatusFilter(value)}
            options={[
              { value: 'all', label: t('warehouseUi.preorders.filterAll', { count: templateCounts.all }) },
              { value: 'enabled', label: t('warehouseUi.preorders.filterEnabled', { count: templateCounts.enabled }) },
              { value: 'disabled', label: t('warehouseUi.preorders.filterDisabled', { count: templateCounts.disabled }) },
            ]}
          />
        </div>
        <MeasuredTable
          metricId="warehouse.preorders.table-1"
          className="wh-preorders-table"
          rowKey="templateGuid"
          columns={columns}
          dataSource={visibleTemplates}
          loading={loading}
          scroll={{ x: 1100 }}
          pagination={false}
          expandable={{
            columnWidth: 40,
            expandedRowKeys: expandedTemplateGuid ? [expandedTemplateGuid] : [],
            rowExpandable: (row) => row.activationCount > 0,
            expandIcon: ({ expanded, expandable, record }) => expandable ? (
              <button
                type="button"
                className={`wh-preorders-expand${expanded ? ' is-expanded' : ''}`}
                aria-expanded={expanded}
                aria-label={expanded
                  ? t('warehouseUi.preorders.collapseHistory', { name: record.name })
                  : t('warehouseUi.preorders.expandHistory', { name: record.name })}
                onClick={() => toggleHistory(record)}
              >
                <RightOutlined />
              </button>
            ) : null,
            expandedRowRender: (row) => (
              <div className="wh-preorders-history">
                <MeasuredTable
                  metricId="warehouse.preorders.table-3"
                  size="small"
                  loading={historyLoading && !activationsByTemplate[row.templateGuid]}
                  rowKey="activationGuid"
                  dataSource={activationsByTemplate[row.templateGuid] ?? []}
                  pagination={false}
                  scroll={{ x: 880 }}
                  locale={{ emptyText: t('warehouseUi.preorders.historyEmpty') }}
                  columns={historyColumns}
                />
              </div>
            ),
          }}
        />
        <div className="wh-preorders-card-foot">
          {t('warehouseUi.preorders.totalTemplates', { count: visibleTemplates.length })}
        </div>
      </section>

      <Modal title={editing ? t('warehouse.preorders.editTemplateTitle', { revision: editing.revision }) : t('warehouse.preorders.createTemplateTitle')} open={editorOpen} width={1120} confirmLoading={saving} okButtonProps={{ disabled: editorLoading || resolving || !canSavePreorderTemplate(items, pasteErrors) }} onOk={() => void saveTemplate()} onCancel={closeEditor} okText={t('warehouse.preorders.saveTemplate')} destroyOnClose>
        <Card loading={editorLoading} bordered={false}>
          <Form form={form} layout="vertical" initialValues={{ isEnabled: true }}>
            <div className="preorder-template-grid">
              <Form.Item name="name" label={t('warehouse.preorders.templateName')} rules={[{ required: true, message: t('warehouse.preorders.templateNameRequired') }]}><Input maxLength={100} /></Form.Item>
              <Form.Item name="isEnabled" label={t('warehouse.preorders.allowActivation')} valuePropName="checked"><Switch checkedChildren={t('warehouse.preorders.enabled')} unCheckedChildren={t('warehouse.preorders.disabled')} /></Form.Item>
            </div>
            <Form.Item name="storeGuids" label={t('warehouse.preorders.defaultVisibleStores')} rules={[{ required: true, message: t('warehouse.preorders.selectOneStore') }]}><Select mode="multiple" showSearch optionFilterProp="label" options={storeOptions} maxTagCount="responsive" /></Form.Item>
            <Form.Item name="notes" label={t('warehouse.preorders.notes')}><Input.TextArea rows={2} maxLength={500} showCount /></Form.Item>
          </Form>
          <div className="preorder-paste-grid">
            <div>
              <Title level={5}>{t('warehouse.preorders.paste.title')}</Title>
              <Text type="secondary">{t('warehouse.preorders.paste.help')}</Text>
              <Input.TextArea
                value={pasteText}
                onChange={(event) => {
                  const nextState = applyPreorderPasteTextChange(
                    { text: pasteText, items, errors: pasteErrors },
                    event.target.value,
                  )
                  if (nextState.text === pasteText) return

                  // 粘贴内容变更即废弃旧解析，避免慢响应覆盖新内容或新模板。
                  invalidateModalRequest(pasteRequestGuardRef.current)
                  setResolving(false)
                  setPasteText(nextState.text)
                  setItems(nextState.items)
                  setPasteErrors(nextState.errors)
                }}
                rows={9}
                placeholder={t('warehouse.preorders.paste.placeholder')}
              />
              <Button type="primary" ghost loading={resolving} onClick={() => void resolvePaste()} style={{ marginTop: 8 }}>{t('warehouse.preorders.paste.parseAndPreview')}</Button>
              {pasteErrors.length ? <Alert style={{ marginTop: 12 }} type="error" showIcon message={t('warehouse.preorders.paste.resolveIssues')} description={pasteErrors.map((error) => <div key={error}>{error}</div>)} /> : null}
            </div>
            <MeasuredTable<PreorderResolvedItem> metricId="warehouse.preorders.table-2"
              size="small"
              rowKey={(row) => `${row.lineNumber}-${row.itemNumber}`}
              dataSource={items}
              pagination={false}
              scroll={{ y: 330 }}
              columns={[
                { title: t('warehouse.preorders.image'), dataIndex: 'productImage', width: 60, render: (src) => <Image src={src} width={36} height={36} style={{ objectFit: 'contain' }} fallback="/placeholder-product.svg" /> },
                { title: t('warehouse.preorders.itemNumber'), dataIndex: 'itemNumber', width: 130 },
                { title: t('warehouse.preorders.name'), dataIndex: 'productName', ellipsis: true },
                { title: t('warehouse.preorders.importPrice'), dataIndex: 'importPrice', width: 85, align: 'right', render: (value) => value === undefined ? '--' : `$${value.toFixed(2)}` },
                { title: t('warehouse.preorders.retailPrice'), dataIndex: 'retailPrice', width: 85, align: 'right', render: (value) => value === undefined ? '--' : `$${value.toFixed(2)}` },
                { title: 'MOQ', dataIndex: 'minimumOrderQuantity', width: 90, render: (value, _, index) => <InputNumber min={1} precision={0} value={value} onChange={(next) => setItems((current) => current.map((item, itemIndex) => itemIndex === index ? { ...item, minimumOrderQuantity: Number(next || 1) } : item))} /> },
                {
                  title: '',
                  width: 60,
                  render: (_, row) => <Button type="link" danger onClick={() => {
                    const nextState = removePreorderPasteItem(
                      { items, errors: pasteErrors },
                      row.lineNumber,
                      t('warehouse.preorders.paste.linePrefix', { lineNumber: row.lineNumber }),
                    )
                    setItems(nextState.items)
                    setPasteErrors(nextState.errors)
                  }}>{t('warehouse.preorders.remove')}</Button>,
                },
              ]}
            />
          </div>
        </Card>
      </Modal>

      <Modal title={t('warehouse.preorders.activateTitle', { name: activationTemplate?.name || '' })} open={activationOpen} onCancel={closeActivationModal} onOk={() => void createActivation()} confirmLoading={activating} okButtonProps={{ disabled: activationLoading || !activationTemplate }} okText={t('warehouse.preorders.confirmActivate')}>
        <Spin spinning={activationLoading}>
          <Alert type="info" showIcon message={t('warehouse.preorders.snapshotNotice')} style={{ marginBottom: 16 }} />
          <Form form={activationForm} layout="vertical">
            <Form.Item name="range" label={t('warehouse.preorders.effectiveTime')} rules={[{ required: true }]}><RangePicker showTime style={{ width: '100%' }} disabledDate={(date) => date.endOf('day').isBefore(dayjs())} /></Form.Item>
            <Form.Item name="estimatedArrivalDate" label={t('warehouse.preorders.estimatedArrivalDate')}><DatePicker allowClear format="YYYY-MM-DD" style={{ width: '100%' }} /></Form.Item>
            <Form.Item name="storeGuids" label={t('warehouse.preorders.targetStores')} rules={[{ required: true, message: t('warehouse.preorders.selectOneStore') }]}><Select mode="multiple" showSearch optionFilterProp="label" options={storeOptions} maxTagCount="responsive" /></Form.Item>
          </Form>
        </Spin>
      </Modal>

      </PageContainer>
    </ConfigProvider>
  )
}
