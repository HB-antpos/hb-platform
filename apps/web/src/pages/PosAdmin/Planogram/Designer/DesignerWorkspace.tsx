import { DndContext, DragOverlay, PointerSensor, useSensor, useSensors, type DragEndEvent, type DragMoveEvent, type DragStartEvent } from '@dnd-kit/core'
import { Alert, Button, Input, Modal, message } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useBlocker, useLocation, useNavigate, type BlockerFunction } from 'react-router-dom'
import { duplicatePlan, exportPlan, getPlanVersion, listShelfModels, publishPlan } from '../../../../services/planogramService'
import type { PlanogramSnapshotDto, ProductSpecListItemDto } from '../../../../types/planogram'
import { downloadTextFile } from '../export/downloadFile'
import { extractConflictDetails, formatNumber, PLANOGRAM_PATH, planogramDesignerPath, planogramErrorText, resolvePlanogramError, type ResolvedPlanogramError } from '../planogramLogic'
import { exportJsonFileName, stringifyExportFile } from '../transferLogic'
import { usePlanogramRequest } from '../usePlanogramRequest'
import BoardCanvas, { type BoardCanvasHandle, type BoardCanvasLabels, type CanvasHintText, type CanvasSnapMode } from './BoardCanvas'
import { conflictText, type ConflictTextEnv } from './conflictText'
import DesignerHeader from './DesignerHeader'
import DesignerToolbar from './DesignerToolbar'
import {
  buildDropHint,
  buildLayoutContext,
  clampZoom,
  createDesignerItem,
  DESIGNER_MIN_EDIT_WIDTH,
  distributeRow,
  DROP_CANDIDATE_GUID,
  findCopyRightSlot,
  findFirstFreeSlot,
  MAX_PLAN_ITEMS,
  nextLocateGuid,
  pickHookForProduct,
  planBatchAdd,
  primaryConflict,
  sortChecklistItems,
  ZOOM_STEP,
  type DropHint,
} from './designerLogic'
import {
  planDragArrange,
  productBoxAt,
  ratePercent,
  type ArrangeCommitPlan,
  type ArrangePlan,
  type ArrangeRejectPlan,
  type ArrangeTarget,
  type CanvasPreviewBox,
} from './dragArrange'
import InspectorPanel from './InspectorPanel'
import ProductLibraryPanel, { LibraryCardBody, type LibraryTab } from './ProductLibraryPanel'
import { resolveHookQty } from '../engine/hookQty'
import PublishModal, { type PublishConflictEntry } from './PublishModal'
import VersionsDrawer from './VersionsDrawer'
import { useDesignerLayout, type DisplayConflict } from './useDesignerLayout'
import { selectDirty, useDesignerStore, useDesignerStoreApi } from './useDesignerStore'

// 导出弹窗（含 PDF 页面 / 版面图打印模式 / 图片加载）和历史版本预览都是低频功能：独立异步块，设计器首次打开时不加载
const ExportPdfModal = lazy(() => import('../export/ExportPdfModal'))
const VersionPreview = lazy(() => import('./VersionPreview'))

// ---------------------------------------------------------------------------
// 窄屏判断：< 1100 px 只读并提示（设计器需要三栏 + 画布，窄屏上拖拽与点选都不可靠）
// ---------------------------------------------------------------------------

function useNarrowViewport(): boolean {
  const query = `(max-width: ${DESIGNER_MIN_EDIT_WIDTH - 1}px)`
  const subscribe = useCallback((notify: () => void) => {
    if (typeof window === 'undefined' || !window.matchMedia) return () => undefined
    const media = window.matchMedia(query)
    media.addEventListener('change', notify)
    return () => media.removeEventListener('change', notify)
  }, [query])
  const getSnapshot = useCallback(() => (typeof window !== 'undefined' && window.matchMedia ? window.matchMedia(query).matches : false), [query])
  return useSyncExternalStore(subscribe, getSnapshot, () => false)
}

// ---------------------------------------------------------------------------
// 离开保护：站内跳走前确认（useBlocker）+ 关闭浏览器标签时的原生确认
// ---------------------------------------------------------------------------

function LeaveGuard() {
  const { t } = useTranslation()
  const dirty = useDesignerStore(selectDirty)
  const { active } = useKeepAliveContext()
  const location = useLocation()
  // 本页实例的路径在挂载时确定；保活页面被隐藏后全局地址会变，只有「从本页离开」才拦，别的页面之间的跳转不受影响
  const ownPathRef = useRef(location.pathname)
  const stateRef = useRef({ dirty, active })
  stateRef.current = { dirty, active }

  const blocker = useBlocker(
    useCallback<BlockerFunction>(
      ({ currentLocation, nextLocation }) =>
        stateRef.current.active && stateRef.current.dirty && currentLocation.pathname === ownPathRef.current && nextLocation.pathname !== currentLocation.pathname,
      [],
    ),
  )
  const prompted = useRef(false)
  useEffect(() => {
    if (blocker.state !== 'blocked') {
      prompted.current = false
      return
    }
    if (prompted.current) return
    prompted.current = true
    Modal.confirm({
      title: t('planogram.designer.leave.title'),
      content: t('planogram.designer.leave.content'),
      okText: t('planogram.designer.leave.ok'),
      cancelText: t('planogram.designer.leave.cancel'),
      okButtonProps: { danger: true },
      onOk: () => blocker.proceed?.(),
      onCancel: () => blocker.reset?.(),
    })
  }, [blocker, t])

  useEffect(() => {
    if (!dirty || !active) return undefined
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [active, dirty])
  return null
}

// ---------------------------------------------------------------------------
// 主体
// ---------------------------------------------------------------------------

interface DesignerWorkspaceProps {
  /** Planogram.Edit；没有时整页只读。 */
  canEdit: boolean
  /** Planogram.Manage：发布。 */
  canManage: boolean
  /** Planogram.View：版本历史、导出 PDF。 */
  canView: boolean
  /** 并发冲突后「重新载入」：丢弃本地改动，重新取服务端最新版本。 */
  onReload: () => void
}

interface ActiveDrag {
  item: ProductSpecListItemDto
  hookModelGuid: string
}

const NO_PLAN: ArrangePlan = { kind: 'none' }

function sameHint(a: DropHint | null, b: DropHint | null): boolean {
  if (a === b) return true
  if (!a || !b) return false
  return a.ok === b.ok && a.bayIndex === b.bayIndex && a.holeCol === b.holeCol && a.holeRow === b.holeRow && a.productCode === b.productCode
}

/** 设计器主体：必须在 DesignerStoreProvider 内使用（页面入口提供）。 */
export default function DesignerWorkspace({ canEdit, canManage, canView, onReload }: DesignerWorkspaceProps) {
  const { t, i18n } = useTranslation()
  const language = i18n.language
  const navigate = useNavigate()
  const storeApi = useDesignerStoreApi()

  const header = useDesignerStore((state) => state.header)
  const doc = useDesignerStore((state) => state.doc)
  const selectedGuid = useDesignerStore((state) => state.selectedGuid)
  const specs = useDesignerStore((state) => state.specs)
  const hooks = useDesignerStore((state) => state.hooks)
  const shelfModels = useDesignerStore((state) => state.shelfModels)
  const saveStatus = useDesignerStore((state) => state.saveStatus)
  const stale = useDesignerStore((state) => state.stale)
  const canUndo = useDesignerStore((state) => state.past.length > 0)
  const canRedo = useDesignerStore((state) => state.future.length > 0)
  const dirty = useDesignerStore(selectDirty)
  const layout = useDesignerLayout()
  const { ctx, shelf } = layout

  // ---- 批 3：发布 / 版本 / 预览 / 导出 ----
  const [publishOpen, setPublishOpen] = useState(false)
  const [publishBusy, setPublishBusy] = useState(false)
  const [publishFailure, setPublishFailure] = useState<{ total: number; entries: PublishConflictEntry[] } | null>(null)
  /** 别的操作（发布）发现方案状态已变（已发布过 / 已归档）：提示重新载入，沿用 PLANOGRAM_STALE 的重新载入流程。 */
  const [reloadNotice, setReloadNotice] = useState<'alreadyPublished' | 'archived' | null>(null)
  const [versionsOpen, setVersionsOpen] = useState(false)
  const [versionsRefresh, setVersionsRefresh] = useState(0)
  const [preview, setPreview] = useState<PlanogramSnapshotDto | null>(null)
  const [previewLoading, setPreviewLoading] = useState<number | null>(null)
  const [exportTarget, setExportTarget] = useState<{ initialVersion: number | null } | null>(null)
  const [exportingJson, setExportingJson] = useState(false)
  const previewing = preview !== null

  // ---- 只读：没有编辑权限 / 已归档 / 窄屏 ----
  const narrow = useNarrowViewport()
  const archived = header.status === 2
  const readOnly = !canEdit || archived || narrow
  useEffect(() => {
    storeApi.getState().setReadOnly(readOnly)
  }, [readOnly, storeApi])

  // ---- 货架型号目录（工具栏下拉用，启用的 + 当前方案在用的）----
  const shelfRequest = usePlanogramRequest('designer-shelf-models', (signal) => listShelfModels({ isActive: true, page: 1, pageSize: 100 }, signal))
  useEffect(() => {
    if (shelfRequest.data) storeApi.getState().setShelfModels(shelfRequest.data.items)
  }, [shelfRequest.data, storeApi])

  // ---- 视图选项 ----
  const [zoom, setZoom] = useState(40)
  const [showHoles, setShowHoles] = useState(true)
  const [showRulers, setShowRulers] = useState(true)
  const [showTags, setShowTags] = useState(true)
  // 商品图：每次打开设计器都是开（不记忆上次的选择）
  const [showImages, setShowImages] = useState(true)
  const [snapMode, setSnapMode] = useState<CanvasSnapMode>('hole')
  /** 多选「添加到方案」的落点预览（鼠标移到按钮上时）。 */
  const [batchPreview, setBatchPreview] = useState<CanvasPreviewBox[] | null>(null)
  const [libraryTab, setLibraryTab] = useState<LibraryTab>('products')
  const [announce, setAnnounce] = useState('')
  const canvasRef = useRef<BoardCanvasHandle | null>(null)
  const rootRef = useRef<HTMLDivElement | null>(null)

  // 启用中的挂钩型号与「当前挂钩」（左栏选择，新放的商品没有默认挂钩时用它）
  const activeHooks = useMemo(() => hooks.filter((hook) => hook.isActive), [hooks])
  const [currentHookPick, setCurrentHookPick] = useState<string | null>(null)
  const currentHookGuid = useMemo(() => {
    if (currentHookPick && activeHooks.some((hook) => hook.guid === currentHookPick)) return currentHookPick
    // 默认：第一个双钩带价签的型号（设计图的默认），没有就取第一个启用的
    return (activeHooks.find((hook) => hook.prongCount === 2 && hook.hasPriceTagArm) ?? activeHooks[0])?.guid ?? null
  }, [activeHooks, currentHookPick])

  const placedCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const item of doc.items) counts.set(item.productCode, (counts.get(item.productCode) ?? 0) + 1)
    return counts
  }, [doc.items])

  // 首次载入后适应窗口（容器不可见 / 没有尺寸时 fitZoom 返回当前值，不改变缩放）
  const fittedRef = useRef(false)
  useEffect(() => {
    if (fittedRef.current || !ctx) return undefined
    const frame = window.requestAnimationFrame(() => {
      const fit = canvasRef.current?.fitZoom()
      if (fit !== undefined) {
        fittedRef.current = true
        setZoom(clampZoom(fit))
      }
    })
    return () => window.cancelAnimationFrame(frame)
  }, [ctx])

  const textEnv: ConflictTextEnv = useMemo(
    () => ({ bayCount: doc.bayCount, gapXMm: doc.gapXMm, gapYMm: doc.gapYMm, holeCols: shelf?.holeCols ?? 0, holeRows: shelf?.holeRows ?? 0 }),
    [doc.bayCount, doc.gapXMm, doc.gapYMm, shelf?.holeCols, shelf?.holeRows],
  )
  const conflictLabel = useCallback((conflict: DisplayConflict) => conflictText(t, conflict, textEnv), [t, textEnv])

  // 商品显示名：英文界面优先英文名。新加的商品参数还没进 store 时直接用手里的参数行（不要回退成货号）
  const itemLabel = useCallback((info: ProductSpecListItemDto | undefined, code: string) => {
    if (!info) return code
    return language.toLowerCase().startsWith('en') && info.englishName ? info.englishName : info.productName || code
  }, [language])

  // ---- 画布文案 ----
  const labels = useMemo<BoardCanvasLabels>(() => ({
    group: t('planogram.designer.canvas.aria'),
    bayTitle: (bay, widthMm) => t('planogram.designer.canvas.bayTitle', { bay, width: formatNumber(widthMm) }),
    base: t('planogram.designer.canvas.base', { depth: formatNumber(shelf?.baseDepthMm ?? 0) }),
    specChanged: t('planogram.designer.inspector.specChanged'),
    float: {
      aria: t('planogram.designer.float.aria'),
      copyRight: t('planogram.designer.float.copyRight'),
      distribute: t('planogram.designer.float.distribute'),
      remove: t('planogram.designer.float.remove'),
    },
    itemTitle: ({ name, code, qty, bay, col, row, conflicts, specChanged }) =>
      [
        t('planogram.designer.canvas.itemTitle', { name, code, qty, bay, col, row }),
        ...conflicts,
        specChanged ? t('planogram.designer.inspector.specChanged') : '',
      ].filter(Boolean).join(' | '),
  }), [shelf?.baseDepthMm, t])

  const announceText = useCallback((text: string) => setAnnounce(text), [])

  // ---- 构造一个包含「新商品参数」的临时布局上下文：新商品的参数还没进 store，拖入判定与找空位要先算它 ----
  const ctxWithSpec = useCallback((item: ProductSpecListItemDto) => {
    if (!shelf) return null
    return buildLayoutContext({ shelf, hooks, specs: { ...specs, [item.productCode]: item }, bayCount: doc.bayCount, gapXMm: doc.gapXMm, gapYMm: doc.gapYMm })
  }, [doc.bayCount, doc.gapXMm, doc.gapYMm, hooks, shelf, specs])

  // ---- 新增 / 复制 / 均分 / 删除 ----
  const addProduct = useCallback((item: ProductSpecListItemDto, slot: { bayIndex: number; holeCol: number; holeRow: number }, hookModelGuid: string, perHookQty: number | null = null) => {
    const state = storeApi.getState()
    if (state.doc.items.length >= MAX_PLAN_ITEMS) {
      message.warning(t('planogram.designer.messages.tooManyItems', { count: MAX_PLAN_ITEMS }))
      return false
    }
    state.addItem(createDesignerItem({ productCode: item.productCode, hookModelGuid, ...slot, perHookQty }), item)
    return true
  }, [storeApi, t])

  /** 左栏「＋」/ 回车：放到第一个能放下的空位（不依赖拖拽，键盘用户也能用）。 */
  const handleAdd = useCallback((item: ProductSpecListItemDto) => {
    if (readOnly) return
    const hookModelGuid = pickHookForProduct(item.spec?.defaultHookGuid, activeHooks, currentHookGuid)
    const context = ctxWithSpec(item)
    if (!hookModelGuid || !context) {
      message.warning(t('planogram.designer.messages.noHook'))
      return
    }
    const slot = findFirstFreeSlot(context, doc.items, { productCode: item.productCode, hookModelGuid })
    if (!slot) {
      message.warning(t('planogram.designer.messages.noRoom'))
      return
    }
    if (addProduct(item, slot, hookModelGuid)) {
      announceText(t('planogram.designer.messages.added', { name: itemLabel(item, item.productCode), bay: slot.bayIndex, col: slot.holeCol, row: slot.holeRow }))
      canvasRef.current?.scrollToItem(storeApi.getState().selectedGuid ?? '')
    }
  }, [activeHooks, addProduct, announceText, ctxWithSpec, currentHookGuid, doc.items, itemLabel, readOnly, storeApi, t])

  // ---- 多选添加：按勾选顺序逐个找第一个空位（与单个「＋」同一规则），整批一次撤销 ----
  const batchContext = useCallback((list: readonly ProductSpecListItemDto[]) => {
    if (!shelf) return null
    const merged: Record<string, ProductSpecListItemDto> = { ...specs }
    for (const item of list) merged[item.productCode] = item
    return buildLayoutContext({ shelf, hooks, specs: merged, bayCount: doc.bayCount, gapXMm: doc.gapXMm, gapYMm: doc.gapYMm })
  }, [doc.bayCount, doc.gapXMm, doc.gapYMm, hooks, shelf, specs])
  const batchEntries = useCallback((list: readonly ProductSpecListItemDto[]) => list.map((item) => ({
    productCode: item.productCode,
    hookModelGuid: pickHookForProduct(item.spec?.defaultHookGuid, activeHooks, currentHookGuid),
  })), [activeHooks, currentHookGuid])

  const handlePreviewMany = useCallback((list: readonly ProductSpecListItemDto[] | null) => {
    const context = list && list.length > 0 && !readOnly ? batchContext(list) : null
    if (!list || !context) {
      setBatchPreview(null)
      return
    }
    const outcomes = planBatchAdd(context, storeApi.getState().doc.items, batchEntries(list), { makeGuid: (index) => `preview-${index}` })
    setBatchPreview(outcomes.flatMap((outcome, index) => {
      if (outcome.status !== 'ok') return []
      const box = productBoxAt(context, outcome.item, outcome.slot)
      return box ? [{ ...box, key: `batch-${index}`, tone: 'ghost' as const }] : []
    }))
  }, [batchContext, batchEntries, readOnly, storeApi])

  const handleAddMany = useCallback((list: readonly ProductSpecListItemDto[]): string[] => {
    setBatchPreview(null)
    const all = list.map((item) => item.productCode)
    if (readOnly || list.length === 0) return all
    const context = batchContext(list)
    const entries = batchEntries(list)
    if (!context || entries.every((entry) => !entry.hookModelGuid)) {
      message.warning(t('planogram.designer.messages.noHook'))
      return all
    }
    const state = storeApi.getState()
    const outcomes = planBatchAdd(context, state.doc.items, entries)
    const added = outcomes.flatMap((outcome) => (outcome.status === 'ok' ? [outcome.item] : []))
    const remaining = list.filter((_, index) => outcomes[index].status !== 'ok').map((item) => item.productCode)
    if (added.length === 0) {
      message.warning(outcomes.some((outcome) => outcome.status === 'tooMany')
        ? t('planogram.designer.messages.tooManyItems', { count: MAX_PLAN_ITEMS })
        : t('planogram.designer.messages.batchNone', { count: list.length }))
      return remaining
    }
    state.registerSpecs(list.filter((_, index) => outcomes[index].status === 'ok'))
    state.replaceItems([...state.doc.items, ...added], added[added.length - 1].guid)
    const afterDoc = storeApi.getState().doc
    const text = remaining.length === 0
      ? t('planogram.designer.messages.batchAdded', { count: added.length })
      : outcomes.some((outcome) => outcome.status === 'tooMany')
        ? t('planogram.designer.messages.batchTooMany', { added: added.length, failed: remaining.length, max: MAX_PLAN_ITEMS })
        : t('planogram.designer.messages.batchPartial', { added: added.length, failed: remaining.length })
    // 提示里带「撤销」：只在整批添加之后没有别的改动时才撤销，避免撤掉后来的编辑
    const key = `planogram-batch-${Date.now()}`
    const undoBatch = () => {
      const current = storeApi.getState()
      if (current.doc === afterDoc) current.undo()
      message.destroy(key)
    }
    message.open({
      key,
      type: remaining.length > 0 ? 'warning' : 'success',
      duration: remaining.length > 0 ? 8 : 4,
      content: (
        <span>
          {text}
          <Button type="link" size="small" className="planogram-inline-link" onClick={undoBatch}>{t('planogram.designer.messages.batchUndo')}</Button>
        </span>
      ),
    })
    announceText(text)
    window.requestAnimationFrame(() => canvasRef.current?.scrollToItem(added[0].guid))
    return remaining
  }, [announceText, batchContext, batchEntries, readOnly, storeApi, t])

  const handleCopyRight = useCallback(() => {
    if (readOnly || !ctx || !selectedGuid) return
    const source = doc.items.find((item) => item.guid === selectedGuid)
    if (!source) return
    const slot = findCopyRightSlot(ctx, doc.items, selectedGuid)
    if (!slot) {
      message.warning(t('planogram.designer.messages.copyRightNone'))
      return
    }
    const info = specs[source.productCode]
    if (!info) return
    if (addProduct(info, slot, source.hookModelGuid, source.perHookQty)) {
      announceText(t('planogram.designer.messages.copied', { bay: slot.bayIndex, col: slot.holeCol, row: slot.holeRow }))
      canvasRef.current?.scrollToItem(storeApi.getState().selectedGuid ?? '')
    }
  }, [addProduct, announceText, ctx, doc.items, readOnly, selectedGuid, specs, storeApi, t])

  const handleDistribute = useCallback(() => {
    if (readOnly || !ctx || !selectedGuid) return
    const result = distributeRow(ctx, doc.items, selectedGuid)
    if (!result.ok) {
      const key = {
        tooFew: 'planogram.designer.messages.distributeTooFew',
        noRoom: 'planogram.designer.messages.distributeNoRoom',
        conflict: 'planogram.designer.messages.distributeConflict',
        invalid: 'planogram.designer.messages.distributeInvalid',
      }[result.reason]
      message.warning(t(key))
      return
    }
    if (result.moved === 0) {
      message.info(t('planogram.designer.messages.distributeSame'))
      return
    }
    storeApi.getState().replaceItems(result.items, selectedGuid)
    announceText(t('planogram.designer.messages.distributed', { count: result.moved }))
  }, [announceText, ctx, doc.items, readOnly, selectedGuid, storeApi, t])

  const handleRemove = useCallback(() => {
    if (readOnly || !selectedGuid) return
    storeApi.getState().removeItem(selectedGuid)
    announceText(t('planogram.designer.messages.removed'))
  }, [announceText, readOnly, selectedGuid, storeApi, t])

  // ---- 冲突定位 ----
  const locate = useCallback((guid?: string) => {
    const target = guid ?? nextLocateGuid(layout.locateList, selectedGuid)
    if (!target) return
    storeApi.getState().select(target)
    canvasRef.current?.scrollToItem(target)
  }, [layout.locateList, selectedGuid, storeApi])

  // ---- 对调 / 插入：提示文案与提交 ----
  const nameOfGuid = useCallback((guid: string) => {
    const item = storeApi.getState().doc.items.find((candidate) => candidate.guid === guid)
    return item ? itemLabel(specs[item.productCode], item.productCode) : ''
  }, [itemLabel, specs, storeApi])
  // 拖动气泡里用货号指明压住的是哪件：同系列商品名前缀往往一样（「Tissue Paper …」），截短后分不出；画布方块上印的也是货号
  const codeOfGuid = useCallback((guid: string) => {
    const item = storeApi.getState().doc.items.find((candidate) => candidate.guid === guid)
    if (!item) return ''
    return specs[item.productCode]?.itemNumber || item.productCode
  }, [specs, storeApi])
  const targetsText = useCallback((targets: readonly ArrangeTarget[]) => targets
    .map((target) => t('planogram.designer.hint.target', { name: codeOfGuid(target.guid), rate: ratePercent(target.rate) }))
    .join(t('planogram.designer.hint.listSep')), [codeOfGuid, t])
  const insertSummary = useCallback((plan: Extract<ArrangePlan, { kind: 'insert' }>) => [
    plan.shiftCount > 0 ? t('planogram.designer.hint.shiftCount', { count: plan.shiftCount }) : null,
    plan.wrapCount > 0 ? t('planogram.designer.hint.wrapCount', { count: plan.wrapCount }) : null,
  ].filter(Boolean).join(t('planogram.designer.hint.partSep')), [t])
  const rejectArrangeText = useCallback((plan: ArrangeRejectPlan, key: 'hint' | 'messages') => {
    if (plan.reason === 'noRoom') return t(`planogram.designer.${key}.arrangeNoRoom`)
    return t(plan.action === 'swap' ? `planogram.designer.${key}.arrangeSwapConflict` : `planogram.designer.${key}.arrangeInsertConflict`)
  }, [t])

  const handleArrange = useCallback((plan: ArrangeCommitPlan, draggedGuid: string) => {
    const draggedName = nameOfGuid(draggedGuid)
    let text: string
    if (plan.kind === 'swap') {
      text = t('planogram.designer.messages.swapped', { a: draggedName, b: nameOfGuid(plan.target.guid) })
    } else {
      const summary = insertSummary(plan)
      text = [t('planogram.designer.messages.inserted', { name: draggedName }), summary].filter(Boolean).join(t('planogram.designer.hint.partSep'))
    }
    storeApi.getState().replaceItems(plan.items, draggedGuid)
    announceText(text)
    // 有商品被换到下一行时明确提示（不在视线里，容易没注意到）
    if (plan.kind === 'insert' && plan.wrapCount > 0) message.info(text)
  }, [announceText, insertSummary, nameOfGuid, storeApi, t])

  const handleRejectArrange = useCallback((plan: ArrangeRejectPlan) => {
    message.warning(rejectArrangeText(plan, 'messages'))
  }, [rejectArrangeText])

  // ---- 落点提示文案 ----
  const describeHint = useCallback((hint: DropHint, plan: ArrangePlan = NO_PLAN): CanvasHintText => {
    if (plan.kind === 'swap') {
      return { ok: true, line1: `⇄ ${t('planogram.designer.hint.swap', { target: targetsText([plan.target]) })}`, line2: t('planogram.designer.hint.swapDetail') }
    }
    if (plan.kind === 'insert') {
      const where = t(plan.position === 'between' ? 'planogram.designer.hint.insertBetween' : plan.position === 'before' ? 'planogram.designer.hint.insertBefore' : 'planogram.designer.hint.insertAfter')
      return {
        ok: true,
        line1: `↦ ${t('planogram.designer.hint.insert', { targets: targetsText(plan.targets) })}`,
        line2: [where, insertSummary(plan)].filter(Boolean).join(t('planogram.designer.hint.partSep')),
      }
    }
    if (plan.kind === 'reject') return { ok: false, line1: `✕ ${rejectArrangeText(plan, 'hint')}`, line2: targetsText(plan.targets) }
    if (plan.kind === 'noop') return { ok: true, line1: `↺ ${t('planogram.designer.hint.noop')}` }
    const at = t('planogram.designer.hint.at', { bay: hint.bayIndex, col: hint.holeCol, row: hint.holeRow })
    // 从左栏拖入的新商品，参数还没进 store：用正在拖动的那一行
    const dragging = dragRef.current?.item
    const info = specs[hint.productCode] ?? (dragging?.productCode === hint.productCode ? dragging : undefined)
    if (hint.ok) {
      const spec = info?.spec
      const hook = hooks.find((candidate) => candidate.guid === hint.hookModelGuid)
      const qty = hook && spec ? resolveHookQty(hook, spec.depthMm, spec.hookQtyOverrides).effective : 0
      const name = itemLabel(info, hint.productCode)
      return {
        ok: true,
        line1: `✓ ${t('planogram.designer.hint.ok')}  ${at}`,
        line2: spec ? [name, `${formatNumber(spec.widthMm)}×${formatNumber(spec.heightMm)}`, qty > 0 ? t('planogram.designer.library.perHook', { count: qty }) : null].filter(Boolean).join(' · ') : name,
      }
    }
    const primary = primaryConflict(hint.conflicts)
    // 「自由」吸附：不可放的位置也会放下（之后标红），气泡如实说明，不再写「不可放置」
    const label = snapMode === 'free' ? t('planogram.designer.hint.free') : t('planogram.designer.hint.no')
    return { ok: false, line1: `✕ ${label}  ${at}`, line2: primary ? conflictText(t, primary, textEnv) : undefined }
  }, [hooks, insertSummary, itemLabel, rejectArrangeText, snapMode, specs, t, targetsText, textEnv])

  const handleRejectMove = useCallback((hint: DropHint) => {
    const primary = primaryConflict(hint.conflicts)
    message.warning(t('planogram.designer.messages.rejected', { reason: primary ? conflictText(t, primary, textEnv) : '' }))
  }, [t, textEnv])

  const handleMoveItem = useCallback((guid: string, slot: { bayIndex: number; holeCol: number; holeRow: number }) => {
    storeApi.getState().moveItem(guid, slot)
    announceText(t('planogram.designer.messages.moved', { bay: slot.bayIndex, col: slot.holeCol, row: slot.holeRow }))
  }, [announceText, storeApi, t])

  // ---- 从左栏拖到画布（dnd-kit） ----
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }))
  const [activeDrag, setActiveDrag] = useState<ActiveDrag | null>(null)
  const [externalHint, setExternalHint] = useState<DropHint | null>(null)
  const [externalPlan, setExternalPlan] = useState<ArrangePlan | null>(null)
  const dragRef = useRef<{ item: ProductSpecListItemDto; hookModelGuid: string | null; hint: DropHint | null; plan: ArrangePlan } | null>(null)
  // 拖动期间的真实指针位置：dnd-kit 给的 delta 会把拖动源所在列表的滚动量也算进去（列表一滚，「起点 + delta」就偏了），
  // 所以在捕获阶段直接记录 pointermove 的坐标，换算画布落点只用它
  const pointerRef = useRef<{ x: number; y: number } | null>(null)
  const trackPointer = useCallback((event: PointerEvent) => {
    pointerRef.current = { x: event.clientX, y: event.clientY }
  }, [])
  useEffect(() => () => window.removeEventListener('pointermove', trackPointer, true), [trackPointer])

  const handleDragStart = useCallback((event: DragStartEvent) => {
    const item = (event.active.data.current as { item?: ProductSpecListItemDto } | undefined)?.item
    if (!item) return
    const hookModelGuid = pickHookForProduct(item.spec?.defaultHookGuid, activeHooks, currentHookGuid)
    dragRef.current = { item, hookModelGuid, hint: null, plan: NO_PLAN }
    const activator = event.activatorEvent as PointerEvent | MouseEvent | null
    pointerRef.current = activator && 'clientX' in activator ? { x: activator.clientX, y: activator.clientY } : null
    window.addEventListener('pointermove', trackPointer, true)
    // 画布按 store 里的商品参数画落点虚影：还没放过的新商品要先登记参数，否则虚影画不出来（只登记目录，不进明细、不算改动）
    storeApi.getState().registerSpecs([item])
    setActiveDrag({ item, hookModelGuid: hookModelGuid ?? '' })
  }, [activeHooks, currentHookGuid, storeApi, trackPointer])

  const handleDragMove = useCallback((event: DragMoveEvent) => {
    const drag = dragRef.current
    const activator = event.activatorEvent as PointerEvent | MouseEvent | TouchEvent | undefined
    if (!drag || !drag.hookModelGuid || !activator || !layout.ctx) return
    const start = 'clientX' in activator ? { x: activator.clientX, y: activator.clientY } : { x: activator.touches?.[0]?.clientX ?? 0, y: activator.touches?.[0]?.clientY ?? 0 }
    const pointer = pointerRef.current ?? { x: start.x + event.delta.x, y: start.y + event.delta.y }
    const point = canvasRef.current?.clientToShelfPoint(pointer.x, pointer.y) ?? null
    let hint: DropHint | null = null
    let plan: ArrangePlan = NO_PLAN
    const context = point ? ctxWithSpec(drag.item) : null
    if (point && context) hint = buildDropHint(context, layout.placed, point, { productCode: drag.item.productCode, hookModelGuid: drag.hookModelGuid })
    if (sameHint(drag.hint, hint)) return
    // 新商品压住已放的商品：一律按插入处理（预览用占位 guid，松手时再用真正的 guid 重算一次）
    if (hint && context) {
      const candidate = createDesignerItem({ productCode: drag.item.productCode, hookModelGuid: drag.hookModelGuid, bayIndex: hint.bayIndex, holeCol: hint.holeCol, holeRow: hint.holeRow, guid: DROP_CANDIDATE_GUID })
      plan = planDragArrange({ ctx: context, items: storeApi.getState().doc.items, hint, dragged: candidate, isNew: true })
    }
    drag.hint = hint
    drag.plan = plan
    setExternalHint(hint)
    setExternalPlan(plan)
  }, [ctxWithSpec, layout.ctx, layout.placed, storeApi])

  const finishExternalDrag = useCallback(() => {
    window.removeEventListener('pointermove', trackPointer, true)
    pointerRef.current = null
    dragRef.current = null
    setActiveDrag(null)
    setExternalHint(null)
    setExternalPlan(null)
  }, [trackPointer])

  const handleDragEnd = useCallback((_event: DragEndEvent) => {
    const drag = dragRef.current
    const hint = drag?.hint ?? null
    finishExternalDrag()
    if (!drag || !hint || !drag.hookModelGuid || readOnly) return
    if (drag.plan.kind === 'reject') {
      handleRejectArrange(drag.plan)
      return
    }
    if (drag.plan.kind === 'insert') {
      if (storeApi.getState().doc.items.length >= MAX_PLAN_ITEMS) {
        message.warning(t('planogram.designer.messages.tooManyItems', { count: MAX_PLAN_ITEMS }))
        return
      }
      const context = ctxWithSpec(drag.item)
      const created = createDesignerItem({ productCode: drag.item.productCode, hookModelGuid: drag.hookModelGuid, bayIndex: hint.bayIndex, holeCol: hint.holeCol, holeRow: hint.holeRow })
      const plan = context ? planDragArrange({ ctx: context, items: storeApi.getState().doc.items, hint, dragged: created, isNew: true }) : NO_PLAN
      if (plan.kind === 'insert') {
        storeApi.getState().registerSpecs([drag.item])
        handleArrange(plan, created.guid)
      } else if (plan.kind === 'reject') {
        handleRejectArrange(plan)
      }
      return
    }
    // 严格吸附：不可放就回弹并提示原因；「自由」模式放到最近孔位，冲突之后标红
    if (!hint.ok && snapMode === 'hole') {
      handleRejectMove(hint)
      return
    }
    if (addProduct(drag.item, { bayIndex: hint.bayIndex, holeCol: hint.holeCol, holeRow: hint.holeRow }, drag.hookModelGuid)) {
      announceText(t('planogram.designer.messages.added', { name: itemLabel(drag.item, drag.item.productCode), bay: hint.bayIndex, col: hint.holeCol, row: hint.holeRow }))
    }
  }, [addProduct, announceText, ctxWithSpec, finishExternalDrag, handleArrange, handleRejectArrange, handleRejectMove, itemLabel, readOnly, snapMode, storeApi, t])

  // ---- 键盘：方向键移动一个孔位、Delete 删除、撤销重做、保存 ----
  const handleKeyDown = useCallback((event: ReactKeyboardEvent<HTMLDivElement>) => {
    // 预览历史版本时设计器主体只是被隐藏：编辑快捷键（保存 / 撤销 / 删除 / 方向键）不能作用到隐藏的草稿上
    if (previewing) return
    const target = event.target as HTMLElement
    const mod = event.metaKey || event.ctrlKey
    const state = storeApi.getState()
    // 保存快捷键在哪里按都生效（输入框里按 Ctrl+S 也不该弹出浏览器的「保存网页」）
    if (mod && event.key.toLowerCase() === 's' && !target.closest('.ant-modal')) {
      event.preventDefault()
      void handleSaveRef.current()
      return
    }
    // 在输入框 / 下拉 / 可编辑区里按键由它们自己处理（否则 Delete 会删掉选中的商品、方向键会乱移，Ctrl+Z 也该撤销文字而不是画布）
    if (target.closest('input, textarea, select, [contenteditable="true"], .ant-select, .ant-input-number, .ant-modal')) return
    if (mod && event.key.toLowerCase() === 'z') {
      event.preventDefault()
      if (event.shiftKey) state.redo()
      else state.undo()
      return
    }
    if (mod && event.key.toLowerCase() === 'y') {
      event.preventDefault()
      state.redo()
      return
    }
    if (mod || event.altKey || readOnly) return
    if (event.key === 'Escape') {
      state.select(null)
      return
    }
    if (!selectedGuid) return
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault()
      handleRemove()
      return
    }
    const delta = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key]
    if (!delta || !shelf) return
    event.preventDefault()
    const item = state.doc.items.find((candidate) => candidate.guid === selectedGuid)
    if (!item) return
    const hook = hooks.find((candidate) => candidate.guid === item.hookModelGuid)
    const maxCol = shelf.holeCols - (hook?.holeSpanX ?? 1) + 1
    const maxRow = shelf.holeRows - (hook?.holeSpanY ?? 1) + 1
    const col = item.holeCol + delta[0]
    const row = item.holeRow + delta[1]
    // 键盘一次一个孔位，只要不出孔阵就允许（中间经过冲突位置是正常的，冲突会即时标红，可撤销）
    if (col < 1 || row < 1 || col > maxCol || row > maxRow) return
    state.moveItem(item.guid, { bayIndex: item.bayIndex, holeCol: col, holeRow: row })
    announceText(t('planogram.designer.messages.moved', { bay: item.bayIndex, col, row }))
    canvasRef.current?.scrollToItem(item.guid)
  }, [announceText, handleRemove, hooks, previewing, readOnly, selectedGuid, shelf, storeApi, t])

  // ---- 保存 / 另存为 ----
  const showSaveError = useCallback((resolved: ResolvedPlanogramError) => {
    if (resolved.kind === 'abort') return
    if (resolved.code === 'PLANOGRAM_ARCHIVED') {
      message.error(t('planogram.errors.archived'))
      return
    }
    message.error(planogramErrorText(resolved, language, (key) => t(key)))
  }, [language, t])

  const handleSave = useCallback(async (): Promise<boolean> => {
    const outcome = await storeApi.getState().save(layout.local?.conflicts)
    switch (outcome.kind) {
      case 'ok':
        message.success(t('planogram.designer.messages.saved'))
        if (outcome.differs) {
          message.warning(t('planogram.designer.messages.saveDiff', { local: outcome.localConflictCount ?? 0, server: outcome.serverConflictCount }))
        }
        return true
      case 'skipped':
        if (outcome.reason === 'noName') message.warning(t('planogram.designer.messages.nameRequired'))
        else if (outcome.reason === 'tooManyItems') message.warning(t('planogram.designer.messages.tooManyItems', { count: MAX_PLAN_ITEMS }))
        else if (outcome.reason === 'noShelf') message.warning(t('planogram.designer.messages.noShelf'))
        return false
      case 'stale':
        // 「方案已被他人更新」的对话框由 stale 状态驱动（下面的 Modal），这里不再弹 toast
        return false
      default:
        showSaveError(outcome.resolved)
        return false
    }
  }, [layout.local?.conflicts, showSaveError, storeApi, t])
  const handleSaveRef = useRef(handleSave)
  handleSaveRef.current = handleSave

  const [saveAsOpen, setSaveAsOpen] = useState(false)
  const [saveAsName, setSaveAsName] = useState('')
  const [saveAsBusy, setSaveAsBusy] = useState(false)
  const openSaveAs = () => {
    setSaveAsName(`${doc.name.trim()}${t('planogram.common.copySuffix')}`)
    setSaveAsOpen(true)
  }
  const submitSaveAs = async () => {
    const name = saveAsName.trim()
    if (!name || saveAsBusy) return
    setSaveAsBusy(true)
    try {
      // 另存为复制的是服务端已保存的版本：有未保存改动时先保存，再复制，新方案才包含当前看到的内容
      if (selectDirty(storeApi.getState()) && !(await handleSave())) return
      const created = await duplicatePlan(header.guid, name)
      message.success(t('planogram.designer.messages.savedAs', { name: created.header.name }))
      setSaveAsOpen(false)
      navigate(planogramDesignerPath(created.header.guid))
    } catch (error) {
      const resolved = resolvePlanogramError(error)
      if (resolved.kind === 'error') message.error(planogramErrorText(resolved, language, (key) => t(key)))
    } finally {
      setSaveAsBusy(false)
    }
  }

  // ---- 发布 ----
  const errorItemCount = layout.stats.conflictCount
  const publishBlockedReason = doc.items.length === 0
    ? t('planogram.publish.blockedEmpty')
    : errorItemCount > 0
      ? t('planogram.publish.blockedConflicts', { count: errorItemCount })
      : null
  // 已发布且没有新改动：没有可发布的内容（后端会返回 PLANOGRAM_ALREADY_PUBLISHED）
  const publishDisabledReason = publishBlockedReason ?? (header.status === 1 && !dirty ? t('planogram.publish.blockedPublished', { version: header.publishedVersion ?? header.version }) : null)

  const startPublish = () => {
    if (!dirty) {
      setPublishFailure(null)
      setPublishOpen(true)
      return
    }
    // 发布的是服务端已保存的内容：有未保存改动先提示保存，保存成功再弹发布确认
    Modal.confirm({
      title: t('planogram.publish.saveFirstTitle'),
      content: t('planogram.publish.saveFirstContent'),
      okText: t('planogram.publish.saveAndContinue'),
      cancelText: t('planogram.common.cancel'),
      onOk: async () => {
        if (await handleSave()) {
          setPublishFailure(null)
          setPublishOpen(true)
        }
      },
    })
  }

  const confirmPublish = async () => {
    if (publishBusy) return
    setPublishBusy(true)
    try {
      const detail = await publishPlan(header.guid, storeApi.getState().rowVersion)
      // 发布成功：刷新详情（状态 / 版本号 / rowVersion），设计器内容不动
      storeApi.getState().applyServerDetail(detail)
      setPublishOpen(false)
      setVersionsRefresh((value) => value + 1)
      message.success(t('planogram.publish.success', { version: detail.header.publishedVersion ?? detail.header.version }))
    } catch (error) {
      const resolved = resolvePlanogramError(error)
      if (resolved.kind === 'abort') return
      switch (resolved.code) {
        case 'PLANOGRAM_HAS_CONFLICTS': {
          // 服务端按存库内容复算后仍有 error 级冲突：列出前几条，可「定位」
          const conflicts = extractConflictDetails(resolved.details).filter((conflict) => conflict.severity === 'error')
          const entries = conflicts.map<PublishConflictEntry>((conflict) => {
            const item = doc.items.find((candidate) => candidate.guid === conflict.itemGuid)
            const name = item ? itemLabel(specs[item.productCode], item.productCode) : conflict.itemGuid
            const position = item ? t('planogram.designer.hint.at', { bay: item.bayIndex, col: item.holeCol, row: item.holeRow }) : ''
            return { guid: conflict.itemGuid, title: position ? `${name} · ${position}` : name, reason: conflictLabel(conflict) }
          })
          setPublishFailure({ total: Math.max(entries.length, 1), entries })
          break
        }
        case 'PLANOGRAM_EMPTY':
          setPublishOpen(false)
          message.error(t('planogram.errors.empty'))
          break
        case 'PLANOGRAM_ALREADY_PUBLISHED':
          setPublishOpen(false)
          setReloadNotice('alreadyPublished')
          break
        case 'PLANOGRAM_STALE':
          setPublishOpen(false)
          storeApi.getState().markStale()
          break
        case 'PLANOGRAM_ARCHIVED':
          setPublishOpen(false)
          setReloadNotice('archived')
          break
        default:
          message.error(planogramErrorText(resolved, language, (key) => t(key)))
      }
    } finally {
      setPublishBusy(false)
    }
  }

  const closePublish = () => {
    setPublishOpen(false)
    setPublishFailure(null)
  }

  // ---- 版本历史与只读预览 ----
  const openPreview = async (version: number) => {
    if (previewLoading !== null) return
    setPreviewLoading(version)
    try {
      const snapshot = await getPlanVersion(header.guid, version)
      setPreview(snapshot)
      setVersionsOpen(false)
    } catch (error) {
      const resolved = resolvePlanogramError(error)
      if (resolved.kind === 'error') message.error(planogramErrorText(resolved, language, (key) => t(key)))
    } finally {
      setPreviewLoading(null)
    }
  }

  const previewConflictLabel = useCallback((conflict: DisplayConflict) => {
    const env: ConflictTextEnv = preview
      ? { bayCount: preview.plan.bayCount, gapXMm: preview.plan.gapXMm, gapYMm: preview.plan.gapYMm, holeCols: preview.shelfModel.holeCols, holeRows: preview.shelfModel.holeRows }
      : textEnv
    return conflictText(t, conflict, env)
  }, [preview, t, textEnv])

  // ---- 导出 JSON（保存版本的内容：型号编码 + 商品货号，不带 guid） ----
  const doExportJson = async () => {
    setExportingJson(true)
    try {
      const file = await exportPlan(header.guid)
      downloadTextFile(exportJsonFileName(storeApi.getState().header.name), stringifyExportFile(file))
      message.success(t('planogram.transfer.exported'))
    } catch (error) {
      const resolved = resolvePlanogramError(error)
      if (resolved.kind === 'error') message.error(planogramErrorText(resolved, language, (key) => t(key)))
    } finally {
      setExportingJson(false)
    }
  }

  const startExportJson = () => {
    if (exportingJson) return
    if (!dirty) {
      void doExportJson()
      return
    }
    Modal.confirm({
      title: t('planogram.transfer.saveFirstTitle'),
      content: t('planogram.transfer.saveFirstContent'),
      okText: t('planogram.transfer.saveAndExport'),
      cancelText: t('planogram.common.cancel'),
      onOk: async () => {
        if (await handleSave()) await doExportJson()
      },
    })
  }

  // ---- 画布 Tab 选择：按 节 → 行 → 列 顺序在条目间移动 ----
  const handleTabSelect = useCallback((direction: 1 | -1) => {
    const order = sortChecklistItems(doc.items)
    if (order.length === 0) return false
    const index = selectedGuid ? order.findIndex((item) => item.guid === selectedGuid) : -1
    const next = index < 0 ? (direction === 1 ? 0 : order.length - 1) : index + direction
    if (next < 0 || next >= order.length) return false
    storeApi.getState().select(order[next].guid)
    canvasRef.current?.scrollToItem(order[next].guid)
    return true
  }, [doc.items, selectedGuid, storeApi])

  const handleZoomChange = useCallback((value: number) => setZoom(clampZoom(value)), [])
  const handleZoomWheel = useCallback((direction: 1 | -1) => setZoom((current) => clampZoom(current + direction * ZOOM_STEP)), [])
  const handleFit = useCallback(() => {
    const fit = canvasRef.current?.fitZoom()
    if (fit !== undefined) setZoom(clampZoom(fit))
  }, [])

  const goSpecs = useCallback(() => navigate(`${PLANOGRAM_PATH}?tab=specs`), [navigate])
  const goList = useCallback(() => navigate(PLANOGRAM_PATH), [navigate])

  return (
    <div ref={rootRef} className="planogram-designer" onKeyDown={handleKeyDown}>
      <LeaveGuard />
      <DesignerHeader
        header={header}
        name={doc.name}
        dirty={dirty}
        readOnly={readOnly}
        canEdit={canEdit && !archived}
        canView={canView}
        canPublish={canManage && !archived}
        publishDisabledReason={publishDisabledReason}
        previewing={previewing}
        canUndo={canUndo}
        canRedo={canRedo}
        saving={saveStatus === 'saving'}
        exportingJson={exportingJson}
        onBack={goList}
        onUndo={() => storeApi.getState().undo()}
        onRedo={() => storeApi.getState().redo()}
        onSaveAs={openSaveAs}
        onSave={() => void handleSave()}
        onPublish={startPublish}
        onVersions={() => setVersionsOpen(true)}
        onExportPdf={() => setExportTarget({ initialVersion: null })}
        onExportJson={startExportJson}
      />
      {narrow ? <Alert type="warning" showIcon message={t('planogram.designer.narrow')} /> : null}
      {archived && !narrow ? <Alert type="info" showIcon message={t('planogram.designer.archivedNote')} /> : null}
      {!canEdit && !archived && !narrow ? <Alert type="info" showIcon message={t('planogram.designer.viewOnly')} /> : null}
      <div className="planogram-designer-main" hidden={previewing}>
        <DesignerToolbar
          shelfModels={shelfModels}
          shelfModelGuid={doc.shelfModelGuid}
          bayCount={doc.bayCount}
          zoom={zoom}
          showHoles={showHoles}
          showRulers={showRulers}
          showTags={showTags}
          showImages={showImages}
          snapMode={snapMode}
          conflictCount={layout.stats.conflictCount}
          readOnly={readOnly}
          onShelfChange={(guid) => storeApi.getState().setHeader({ shelfModelGuid: guid })}
          onBayCountChange={(count) => storeApi.getState().setHeader({ bayCount: count })}
          onZoomChange={handleZoomChange}
          onFit={handleFit}
          onToggle={(key, value) => {
            if (key === 'holes') setShowHoles(value)
            else if (key === 'rulers') setShowRulers(value)
            else if (key === 'images') setShowImages(value)
            else setShowTags(value)
          }}
          onSnapChange={setSnapMode}
          onLocate={() => locate()}
        />
        {/* autoScroll 关掉：它只会滚动拖动源所在的商品库列表（往画布拖时指针靠近列表上下沿，列表会自己滚走），对放到画布没有帮助 */}
        <DndContext sensors={sensors} autoScroll={false} onDragStart={handleDragStart} onDragMove={handleDragMove} onDragEnd={handleDragEnd} onDragCancel={finishExternalDrag}>
          <div className="planogram-designer-work">
            {narrow ? null : (
              <ProductLibraryPanel
                tab={libraryTab}
                onTabChange={setLibraryTab}
                hooks={activeHooks}
                currentHookGuid={currentHookGuid}
                onCurrentHookChange={setCurrentHookPick}
                placedCounts={placedCounts}
                readOnly={readOnly}
                canEdit={canEdit}
                language={language}
                onAdd={handleAdd}
                onAddMany={handleAddMany}
                onPreviewMany={handlePreviewMany}
                onGoSpecs={goSpecs}
              />
            )}
            <div className="planogram-designer-canvas-card">
              <BoardCanvas
                ref={canvasRef}
                ctx={ctx}
                items={doc.items}
                placed={layout.placed}
                specs={specs}
                conflictsByGuid={layout.conflictsByGuid}
                selectedGuid={selectedGuid}
                zoom={zoom}
                showHoles={showHoles}
                showRulers={showRulers}
                showTags={showTags}
                showImages={showImages}
                snapMode={snapMode}
                readOnly={readOnly}
                language={language}
                labels={labels}
                conflictText={conflictLabel}
                externalHint={externalHint}
                externalPlan={externalPlan}
                previewBoxes={batchPreview}
                describeHint={describeHint}
                onSelect={(guid) => storeApi.getState().select(guid)}
                onMoveItem={handleMoveItem}
                onRejectMove={handleRejectMove}
                onArrange={handleArrange}
                onRejectArrange={handleRejectArrange}
                onCopyRight={handleCopyRight}
                onDistribute={handleDistribute}
                onRemove={handleRemove}
                onZoomWheel={handleZoomWheel}
                onTabSelect={handleTabSelect}
                emptyText={shelfRequest.loading ? t('planogram.designer.canvas.loading') : t('planogram.designer.canvas.noShelf')}
              />
              <div className="planogram-designer-legend">
                <span><i className="is-placed" />{t('planogram.designer.legend.placed')}</span>
                <span><i className="is-selected" />{t('planogram.designer.legend.selected')}</span>
                <span><i className="is-conflict" />{t('planogram.designer.legend.conflict')}</span>
                <span><i className="is-ok" />{t('planogram.designer.legend.drop')}</span>
                {shelf ? (
                  <span className="planogram-designer-legend-end">
                    {t('planogram.designer.legend.board', {
                      w: formatNumber(shelf.bayWidthMm - shelf.uprightWidthMm),
                      h: formatNumber(shelf.totalHeightMm - shelf.baseTopHeightMm),
                      pitch: formatNumber(shelf.holePitchXMm),
                      cols: shelf.holeCols,
                      rows: shelf.holeRows,
                    })}
                  </span>
                ) : null}
              </div>
            </div>
            {narrow ? null : (
              <InspectorPanel layout={layout} readOnly={readOnly} language={language} onCopyRight={handleCopyRight} onDistribute={handleDistribute} onRemove={handleRemove} onLocate={locate} />
            )}
          </div>
          <DragOverlay dropAnimation={{ duration: 180 }}>
            {activeDrag ? (
              <div className="planogram-designer-card is-overlay">
                <LibraryCardBody overlay info={{ item: activeDrag.item, language, qty: null, placed: 0 }} />
              </div>
            ) : null}
          </DragOverlay>
        </DndContext>
      </div>
      {preview ? (
        <Suspense fallback={null}>
          <VersionPreview
            snapshot={preview}
            language={language}
            labels={labels}
            conflictText={previewConflictLabel}
            onBack={() => setPreview(null)}
            onExportPdf={() => setExportTarget({ initialVersion: preview.version })}
          />
        </Suspense>
      ) : null}
      <div className="planogram-designer-sr" role="status" aria-live="polite">{announce}</div>

      <Modal
        open={saveAsOpen}
        title={t('planogram.designer.saveAsTitle')}
        okText={t('planogram.designer.saveAs')}
        cancelText={t('planogram.common.cancel')}
        confirmLoading={saveAsBusy}
        okButtonProps={{ disabled: !saveAsName.trim() }}
        onOk={() => void submitSaveAs()}
        onCancel={() => setSaveAsOpen(false)}
        destroyOnHidden
        maskClosable={false}
      >
        <div className="planogram-field-note">{dirty ? t('planogram.designer.saveAsDirty') : t('planogram.designer.saveAsHint')}</div>
        <Input
          autoFocus
          value={saveAsName}
          maxLength={100}
          aria-label={t('planogram.plans.newModal.name')}
          onChange={(event) => setSaveAsName(event.target.value)}
          onPressEnter={() => void submitSaveAs()}
        />
      </Modal>

      <Modal
        open={stale}
        title={t('planogram.designer.stale.title')}
        okText={t('planogram.designer.stale.reload')}
        cancelText={t('planogram.designer.stale.keep')}
        okButtonProps={{ danger: true }}
        onOk={onReload}
        onCancel={() => storeApi.getState().dismissStale()}
        maskClosable={false}
      >
        <p>{t('planogram.designer.stale.content')}</p>
      </Modal>

      {publishOpen ? (
        <PublishModal
          version={header.version}
          previousVersion={header.publishedVersion ?? null}
          stats={layout.stats}
          blockedReason={publishBlockedReason}
          busy={publishBusy}
          failure={publishFailure}
          onConfirm={() => void confirmPublish()}
          onClose={closePublish}
          onLocate={(guid) => {
            closePublish()
            locate(guid)
          }}
        />
      ) : null}

      <Modal
        open={reloadNotice !== null}
        title={reloadNotice === 'archived' ? t('planogram.publish.archivedTitle') : t('planogram.publish.alreadyTitle')}
        okText={t('planogram.designer.stale.reload')}
        cancelText={t('planogram.designer.stale.keep')}
        okButtonProps={{ danger: true }}
        onOk={onReload}
        onCancel={() => setReloadNotice(null)}
        maskClosable={false}
      >
        <p>{reloadNotice === 'archived' ? t('planogram.publish.archivedContent') : t('planogram.publish.alreadyContent')}</p>
      </Modal>

      {versionsOpen ? (
        <VersionsDrawer
          planGuid={header.guid}
          publishedVersion={header.publishedVersion ?? null}
          refreshKey={versionsRefresh}
          previewVersion={preview?.version ?? null}
          loadingVersion={previewLoading}
          onPreview={(version) => void openPreview(version)}
          onExportPdf={(version) => {
            setVersionsOpen(false)
            setExportTarget({ initialVersion: version })
          }}
          onClose={() => setVersionsOpen(false)}
        />
      ) : null}

      {exportTarget ? (
        <Suspense fallback={null}>
          <ExportPdfModal
            planGuid={header.guid}
            planName={header.name}
            dirty={dirty}
            initialVersion={exportTarget.initialVersion}
            onClose={() => setExportTarget(null)}
          />
        </Suspense>
      ) : null}
    </div>
  )
}
