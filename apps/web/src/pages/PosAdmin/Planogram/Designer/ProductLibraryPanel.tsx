import { CloseOutlined, FilterOutlined, LockOutlined, PlusOutlined, SearchOutlined } from '@ant-design/icons'
import { useDraggable } from '@dnd-kit/core'
import { Alert, Button, Checkbox, Empty, Input, Popover, Segmented, Select, Spin, Tabs, Tooltip, message } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react'
import { useTranslation } from 'react-i18next'
import ProductListImage from '../../../../components/ProductListImage'
import { listProductSpecs } from '../../../../services/planogramService'
import type { HookModelDto, ProductSpecListItemDto, ProductSpecScopeFilter } from '../../../../types/planogram'
import { GradeChips, SupplierCategorySelect, WarehouseCategorySelect } from '../categoryFilter/CategoryFilterControls'
import { scopeFilterKey } from '../categoryFilter/categoryFilterLogic'
import { useCategoryFilter } from '../categoryFilter/useCategoryFilter'
import GradeTag from '../GradeTag'
import { resolveHookQty } from '../engine/hookQty'
import { FILTER_DEBOUNCE_MS, formatNumber, planogramErrorText, resolvePlanogramError, SEARCH_DEBOUNCE_MS } from '../planogramLogic'
import { useDebouncedValue } from '../usePlanogramRequest'
import { pickHookForProduct, productColor } from './designerLogic'

/** 左栏商品库每页条数。 */
const LIBRARY_PAGE_SIZE = 30
/** 多选上限：一次最多勾选这么多个商品「添加到方案」。 */
export const LIBRARY_SELECT_LIMIT = 200

export type LibraryStatus = 'set' | 'all'
export type LibraryTab = 'products' | 'hooks'

// ---------------------------------------------------------------------------
// 取数：搜索 + 状态筛选 + 分页追加（「加载更多」）
// ---------------------------------------------------------------------------

interface LibraryState {
  key: string
  items: ProductSpecListItemDto[]
  total: number
  page: number
  loading: boolean
  error: unknown
}

function useProductLibrary(keyword: string, status: LibraryStatus, scope: ProductSpecScopeFilter) {
  // 搜索词 / 状态 / 分类 / 等级任一变化 → 换键，从第 1 页重新取
  const key = `${keyword}|${status}|${scopeFilterKey(scope)}`
  const [state, setState] = useState<LibraryState>({ key, items: [], total: 0, page: 0, loading: true, error: null })
  const controllerRef = useRef<AbortController | null>(null)
  const nonceRef = useRef(0)

  const load = useCallback((page: number, append: boolean) => {
    controllerRef.current?.abort()
    const controller = new AbortController()
    controllerRef.current = controller
    nonceRef.current += 1
    const nonce = nonceRef.current
    setState((current) => ({ ...current, key, loading: true, error: null, ...(append ? {} : { items: [], total: 0, page: 0 }) }))
    listProductSpecs({ keyword: keyword || undefined, status, ...scope, page, pageSize: LIBRARY_PAGE_SIZE }, controller.signal).then(
      (result) => {
        if (controller.signal.aborted || nonce !== nonceRef.current) return
        setState((current) => {
          const known = new Set(append ? current.items.map((item) => item.productCode) : [])
          return { key, items: [...(append ? current.items : []), ...result.items.filter((item) => !known.has(item.productCode))], total: result.total, page, loading: false, error: null }
        })
      },
      (error: unknown) => {
        if (controller.signal.aborted || nonce !== nonceRef.current) return
        setState((current) => ({ ...current, loading: false, error }))
      },
    )
  }, [key, keyword, scope, status])

  // 搜索词 / 状态 / 分类条件变了：从第 1 页重新取；卸载时中止在途请求
  useEffect(() => {
    load(1, false)
    return () => controllerRef.current?.abort()
  }, [load])

  const loadMore = useCallback(() => {
    if (state.loading || state.items.length >= state.total) return
    load(state.page + 1, true)
  }, [load, state.items.length, state.loading, state.page, state.total])
  const reload = useCallback(() => load(1, false), [load])
  return { ...state, loadMore, reload }
}

// ---------------------------------------------------------------------------
// 商品卡片
// ---------------------------------------------------------------------------

interface CardInfo {
  item: ProductSpecListItemDto
  language: string
  /** 按默认挂钩（没有就按左栏当前挂钩）算出的每钩挂数；没有可用挂钩时为 null。 */
  qty: number | null
  placed: number
}

function cardName(item: ProductSpecListItemDto, language: string): string {
  return language.toLowerCase().startsWith('en') && item.englishName ? item.englishName : item.productName
}

function Thumb({ item, size }: { item: ProductSpecListItemDto; size: number }) {
  const { t } = useTranslation()
  if (item.productImage) return <ProductListImage src={item.productImage} size={size} radius={6} />
  return (
    <div className="planogram-designer-thumb" style={{ width: size, height: size, background: item.hasSpec ? productColor(item.productCode) : undefined }}>
      {item.hasSpec ? cardName(item, 'zh').slice(0, 2) : t('planogram.spec.noImage')}
    </div>
  )
}

/** 卡片的静态外观：列表里的卡片与拖动时跟手的 DragOverlay 共用。 */
export function LibraryCardBody({ info, overlay }: { info: CardInfo; overlay?: boolean }) {
  const { t } = useTranslation()
  const { item } = info
  const spec = item.spec
  return (
    <>
      <Thumb item={item} size={overlay ? 34 : 38} />
      <div className="planogram-designer-card-text">
        <div className="planogram-designer-card-name">{cardName(item, info.language)}</div>
        <div className="planogram-designer-card-meta planogram-mono">{item.itemNumber || item.productCode}</div>
        {item.hasSpec && spec ? (
          <div className="planogram-designer-card-meta">
            {[
              t('planogram.designer.library.dims', { w: formatNumber(spec.widthMm), h: formatNumber(spec.heightMm), t: formatNumber(spec.depthMm) }),
              !overlay && info.qty !== null && info.qty > 0 ? t('planogram.designer.library.perHook', { count: info.qty }) : null,
              !overlay ? t('planogram.designer.library.placed', { count: info.placed }) : null,
            ].filter(Boolean).join(' · ')}
            {/* 商品等级小标签（拖动时跟手的卡片不画，保持紧凑） */}
            {overlay ? null : <> <GradeTag grade={item.grade} /></>}
          </div>
        ) : null}
      </div>
    </>
  )
}

interface LibraryCardProps {
  info: CardInfo
  disabled: boolean
  onAdd: (item: ProductSpecListItemDto) => void
  onGoSpecs: () => void
  canEdit: boolean
  /** 勾选顺序（1 起）；null = 没勾选。 */
  selectedOrder: number | null
  /** range = 按住 Shift：从上一次点的商品到这个商品整段勾上。 */
  onToggleSelect: (item: ProductSpecListItemDto, range: boolean) => void
}

function LibraryCard({ info, disabled, onAdd, onGoSpecs, canEdit, selectedOrder, onToggleSelect }: LibraryCardProps) {
  const { t } = useTranslation()
  const { item } = info
  const draggable = item.hasSpec && !disabled
  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({
    id: `library:${item.productCode}`,
    data: { item },
    disabled: !draggable,
  })

  if (!item.hasSpec) {
    // 未设陈列参数：灰显，不能拖；有编辑权限时给「补充参数」入口（跳回参数页签）
    return (
      <div className="planogram-designer-card is-unset">
        <Thumb item={item} size={38} />
        <div className="planogram-designer-card-text">
          <div className="planogram-designer-card-name">{cardName(item, info.language)}</div>
          <div className="planogram-designer-card-meta planogram-mono">{item.itemNumber || item.productCode} <GradeTag grade={item.grade} /></div>
          <div className="planogram-designer-card-meta planogram-warn-text">
            {t('planogram.designer.library.unset')}
            {canEdit ? <> · <Button type="link" size="small" className="planogram-inline-link" onClick={onGoSpecs}>{t('planogram.designer.library.addSpec')}</Button></> : null}
          </div>
        </div>
        <LockOutlined className="planogram-designer-card-grip" aria-hidden="true" />
      </div>
    )
  }
  const selected = selectedOrder !== null
  // Ctrl / ⌘ 点卡片逐个加选，Shift 点卡片整段选（普通点击仍是拖拽 / 什么都不做，不改变原有手感）
  const handleCardClick = (event: ReactMouseEvent<HTMLDivElement>) => {
    if (disabled) return
    if (event.shiftKey || event.metaKey || event.ctrlKey) {
      event.preventDefault()
      onToggleSelect(item, event.shiftKey)
    }
  }
  return (
    <div
      ref={setNodeRef}
      className={`planogram-designer-card${isDragging ? ' is-dragging' : ''}${disabled ? ' is-disabled' : ''}${selected ? ' is-selected' : ''}`}
      {...attributes}
      {...listeners}
      aria-label={t('planogram.designer.library.dragAria', { name: cardName(item, info.language) })}
      onClick={handleCardClick}
      // 按住 Shift 点选时浏览器会把两次点击之间的文字整段选中：阻止按下时的默认行为（不影响复选框勾选与拖拽）
      onMouseDown={(event) => {
        if (event.shiftKey) event.preventDefault()
      }}
      onKeyDown={(event) => {
        if (event.key === 'Enter' && !disabled) {
          event.preventDefault()
          onAdd(item)
        }
      }}
    >
      {disabled ? null : (
        // 复选框：不让 dnd-kit 把按下复选框当成拖动开始；角标是放置顺序
        <span
          className="planogram-designer-card-check"
          title={t('planogram.designer.library.selectTip')}
          onPointerDown={(event) => event.stopPropagation()}
          onClick={(event) => event.stopPropagation()}
          // 复选框上按回车 / 空格只管勾选，不冒泡到卡片触发「添加到第一个空位」
          onKeyDown={(event) => event.stopPropagation()}
        >
          <Checkbox
            checked={selected}
            aria-label={t('planogram.designer.library.selectAria', { name: cardName(item, info.language) })}
            onChange={(event) => onToggleSelect(item, Boolean((event.nativeEvent as MouseEvent | undefined)?.shiftKey))}
          />
          {selected ? <span className="planogram-designer-card-order" aria-hidden="true">{selectedOrder}</span> : null}
        </span>
      )}
      <LibraryCardBody info={info} />
      {disabled ? null : (
        <Button
          type="text"
          size="small"
          className="planogram-designer-card-add"
          icon={<PlusOutlined />}
          aria-label={t('planogram.designer.library.addAria', { name: cardName(item, info.language) })}
          title={t('planogram.designer.library.add')}
          // 不让 dnd-kit 把点「＋」当成拖动开始
          onPointerDown={(event) => event.stopPropagation()}
          onClick={() => onAdd(item)}
        />
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// 面板
// ---------------------------------------------------------------------------

interface ProductLibraryPanelProps {
  tab: LibraryTab
  onTabChange: (tab: LibraryTab) => void
  /** 启用中的挂钩型号（新条目只能用启用的型号）。 */
  hooks: readonly HookModelDto[]
  currentHookGuid: string | null
  onCurrentHookChange: (guid: string) => void
  placedCounts: ReadonlyMap<string, number>
  /** 页面只读：卡片不能拖，也没有「＋」。 */
  readOnly: boolean
  canEdit: boolean
  language: string
  onAdd: (item: ProductSpecListItemDto) => void
  /** 多选「添加到方案」：按勾选顺序放好，返回没放下的商品货号（它们保持勾选）。 */
  onAddMany: (items: readonly ProductSpecListItemDto[]) => string[]
  /** 鼠标移到「添加到方案」上时预览落点（null = 收起预览）。 */
  onPreviewMany: (items: readonly ProductSpecListItemDto[] | null) => void
  onGoSpecs: () => void
}

export default function ProductLibraryPanel(props: ProductLibraryPanelProps) {
  const { tab, onTabChange, hooks, currentHookGuid, onCurrentHookChange, placedCounts, readOnly, canEdit, language, onAdd, onAddMany, onPreviewMany, onGoSpecs } = props
  const { t, i18n } = useTranslation()
  const [keywordDraft, setKeywordDraft] = useState('')
  const [status, setStatus] = useState<LibraryStatus>('set')
  const keyword = useDebouncedValue(keywordDraft.trim(), SEARCH_DEBOUNCE_MS)
  // 分类 / 等级筛选：条件进入商品库请求（仍是 status=set / 全部）；分类数据在第一次打开「筛选」弹层时才加载，失败不影响列表
  const categoryFilter = useCategoryFilter({ onLoadError: () => { message.error(t('planogram.categoryFilter.loadFailed')) } })
  const scope = useDebouncedValue(categoryFilter.scope, FILTER_DEBOUNCE_MS)
  const [filterOpen, setFilterOpen] = useState(false)
  const library = useProductLibrary(keyword, status, scope)
  const hooksByGuid = new Map(hooks.map((hook) => [hook.guid, hook]))

  // ---- 多选：按勾选顺序保存整行参数（翻页 / 换筛选后已选仍保留），上限 LIBRARY_SELECT_LIMIT ----
  const [selection, setSelection] = useState<ProductSpecListItemDto[]>([])
  const anchorRef = useRef<string | null>(null)
  const selectedOrder = useMemo(() => new Map(selection.map((item, index) => [item.productCode, index + 1])), [selection])
  useEffect(() => {
    if (readOnly) setSelection([])
  }, [readOnly])
  const applySelection = useCallback((next: ProductSpecListItemDto[]) => {
    if (next.length > LIBRARY_SELECT_LIMIT) {
      message.warning(t('planogram.designer.library.selectLimit', { count: LIBRARY_SELECT_LIMIT }))
      setSelection(next.slice(0, LIBRARY_SELECT_LIMIT))
      return
    }
    setSelection(next)
  }, [t])
  const toggleSelect = useCallback((item: ProductSpecListItemDto, range: boolean) => {
    if (readOnly || !item.hasSpec) return
    const anchor = anchorRef.current
    anchorRef.current = item.productCode
    const known = new Set(selection.map((entry) => entry.productCode))
    if (range && anchor && anchor !== item.productCode) {
      // Shift：从上一次点的商品到这一个，按列表顺序整段勾上（已勾的保持原顺序）
      const from = library.items.findIndex((entry) => entry.productCode === anchor)
      const to = library.items.findIndex((entry) => entry.productCode === item.productCode)
      if (from >= 0 && to >= 0) {
        const span = from <= to ? library.items.slice(from, to + 1) : library.items.slice(to, from + 1).reverse()
        applySelection([...selection, ...span.filter((entry) => entry.hasSpec && !known.has(entry.productCode))])
        return
      }
    }
    applySelection(known.has(item.productCode) ? selection.filter((entry) => entry.productCode !== item.productCode) : [...selection, item])
  }, [applySelection, library.items, readOnly, selection])
  const selectableItems = library.items.filter((item) => item.hasSpec)
  const selectAllListed = () => {
    const known = new Set(selection.map((entry) => entry.productCode))
    applySelection([...selection, ...selectableItems.filter((item) => !known.has(item.productCode))])
  }
  const clearSelection = () => {
    onPreviewMany(null)
    setSelection([])
    anchorRef.current = null
  }
  const addSelected = () => {
    onPreviewMany(null)
    const remaining = new Set(onAddMany(selection))
    // 放不下的保持勾选（可加节 / 腾位后再添加）；放好的取消勾选
    setSelection((current) => current.filter((item) => remaining.has(item.productCode)))
  }

  const errorText = (() => {
    if (!library.error) return null
    const resolved = resolvePlanogramError(library.error)
    return resolved.kind === 'error' ? planogramErrorText(resolved, i18n.language, (key) => t(key)) : null
  })()

  const cardInfo = (item: ProductSpecListItemDto): CardInfo => {
    const guid = pickHookForProduct(item.spec?.defaultHookGuid, hooks, currentHookGuid)
    const hook = guid ? hooksByGuid.get(guid) : undefined
    const qty = hook && item.spec ? resolveHookQty(hook, item.spec.depthMm, item.spec.hookQtyOverrides).effective : null
    return { item, language, qty, placed: placedCounts.get(item.productCode) ?? 0 }
  }

  const productsPane = (
    <div className="planogram-designer-library-pane">
      <div className="planogram-designer-library-filters">
        <div className="planogram-designer-library-searchrow">
          <Input
            allowClear
            prefix={<SearchOutlined aria-hidden="true" />}
            placeholder={t('planogram.designer.library.search')}
            aria-label={t('planogram.designer.library.search')}
            value={keywordDraft}
            onChange={(event) => setKeywordDraft(event.target.value)}
          />
          {/* 「筛选」按钮 + 弹层：有条件时按钮高亮并显示条数；弹层里是仓库分类 / 本地供应商分类 / 商品等级 */}
          <Popover
            trigger="click"
            placement="bottomLeft"
            open={filterOpen}
            onOpenChange={(next) => {
              setFilterOpen(next)
              if (next) categoryFilter.ensureLoaded()
            }}
            overlayClassName="planogram-filter-popover"
            content={(
              <div className="planogram-filter-pop">
                <div className="planogram-filter-pop-title">{t('planogram.designer.library.filterTitle')}</div>
                <div className="planogram-filter-pop-label">{t('planogram.categoryFilter.warehouseWithChildren')}</div>
                <WarehouseCategorySelect filter={categoryFilter} variant="popover" />
                <div className="planogram-filter-pop-label">{t('planogram.categoryFilter.supplier')}</div>
                <SupplierCategorySelect filter={categoryFilter} variant="popover" />
                <div className="planogram-filter-pop-label">{t('planogram.grade.title')}</div>
                <GradeChips filter={categoryFilter} size="sm" />
                <div className="planogram-filter-pop-foot">
                  <span className="planogram-muted-text planogram-small" aria-live="polite">
                    {library.loading ? '…' : t('planogram.designer.library.matchCount', { count: library.total })}
                  </span>
                  <span className="planogram-filter-pop-actions">
                    <Button size="small" disabled={!categoryFilter.hasActive} onClick={categoryFilter.clear}>{t('planogram.designer.library.filterClear')}</Button>
                    <Button size="small" type="primary" onClick={() => setFilterOpen(false)}>{t('planogram.designer.library.filterDone')}</Button>
                  </span>
                </div>
              </div>
            )}
          >
            {/* 窄栏里按钮只放图标（有条件时再加条数），悬浮提示「筛选」；读屏用 aria-label */}
            <Tooltip title={filterOpen ? undefined : t('planogram.designer.library.filter')}>
              <Button
                className={`planogram-designer-filter-btn${categoryFilter.hasActive ? ' is-active' : ''}`}
                icon={<FilterOutlined />}
                aria-label={t('planogram.designer.library.filter')}
                aria-expanded={filterOpen}
              >
                {categoryFilter.hasActive ? categoryFilter.activeCount : null}
              </Button>
            </Tooltip>
          </Popover>
        </div>
        <div className="planogram-designer-library-statusrow">
          <Segmented<LibraryStatus>
            size="small"
            value={status}
            onChange={setStatus}
            aria-label={t('planogram.spec.statusLabel')}
            options={[
              { label: t('planogram.spec.status.set'), value: 'set' },
              { label: t('planogram.designer.library.all'), value: 'all' },
            ]}
          />
          {readOnly ? null : (
            <Button type="link" size="small" className="planogram-inline-link" disabled={selectableItems.length === 0} onClick={selectAllListed}>
              {t('planogram.designer.library.selectAll')}
            </Button>
          )}
        </div>
        {categoryFilter.chips.length > 0 ? (
          <div className="planogram-designer-library-chips">
            {categoryFilter.chips.map((chip) => (
              <span key={chip.id} className="planogram-designer-chip">
                <span className="planogram-designer-chip-text" title={chip.label}>{chip.label}</span>
                <button
                  type="button"
                  className="planogram-designer-chip-x"
                  aria-label={t('planogram.designer.library.removeChip', { name: chip.label })}
                  onClick={() => categoryFilter.removeGroup(chip.id)}
                >
                  <CloseOutlined aria-hidden="true" />
                </button>
              </span>
            ))}
          </div>
        ) : null}
      </div>
      <div className="planogram-designer-library-list" role="list" aria-busy={library.loading}>
        {errorText ? <Alert type="error" showIcon message={errorText} action={<a onClick={library.reload}>{t('planogram.common.retry')}</a>} /> : null}
        {library.items.map((item) => (
          <div role="listitem" key={item.productCode}>
            <LibraryCard
              info={cardInfo(item)}
              disabled={readOnly}
              onAdd={onAdd}
              onGoSpecs={onGoSpecs}
              canEdit={canEdit}
              selectedOrder={selectedOrder.get(item.productCode) ?? null}
              onToggleSelect={toggleSelect}
            />
          </div>
        ))}
        {library.loading ? <div className="planogram-designer-library-loading"><Spin size="small" /></div> : null}
        {!library.loading && !library.error && library.items.length === 0 ? (
          // 空结果文案：有分类 / 等级条件时说「没有符合条件的商品」，否则才是「还没设置陈列参数」
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={categoryFilter.hasActive
              ? t('planogram.designer.library.emptyFiltered')
              : status === 'set' ? t('planogram.designer.library.emptySet') : t('planogram.spec.empty')}
          />
        ) : null}
        {!library.loading && library.items.length < library.total ? (
          <Button block size="small" onClick={library.loadMore}>{t('planogram.designer.library.loadMore', { shown: library.items.length, total: library.total })}</Button>
        ) : null}
      </div>
      {selection.length > 0 && !readOnly ? (
        <div className="planogram-designer-selection-bar" role="region" aria-label={t('planogram.designer.library.selected', { count: selection.length })}>
          <span className="planogram-designer-selection-count">{t('planogram.designer.library.selected', { count: selection.length })}</span>
          <Button
            type="primary"
            size="small"
            icon={<PlusOutlined />}
            onClick={addSelected}
            // 鼠标移上去 / 键盘聚焦时在画布上用绿色虚线预览落点
            onMouseEnter={() => onPreviewMany(selection)}
            onMouseLeave={() => onPreviewMany(null)}
            onFocus={() => onPreviewMany(selection)}
            onBlur={() => onPreviewMany(null)}
          >
            {t('planogram.designer.library.addSelected')}
          </Button>
          <Button type="text" size="small" onClick={clearSelection}>{t('planogram.designer.library.clearSelected')}</Button>
        </div>
      ) : null}
      <div className="planogram-designer-library-hookbar">
        <span className="planogram-muted-text">{t('planogram.designer.library.currentHook')}</span>
        <Select
          size="small"
          className="planogram-designer-hook-select"
          value={currentHookGuid ?? undefined}
          onChange={onCurrentHookChange}
          disabled={readOnly || hooks.length === 0}
          aria-label={t('planogram.designer.library.currentHook')}
          placeholder={t('planogram.designer.library.noHooks')}
          options={hooks.map((hook) => ({ value: hook.guid, label: t('planogram.designer.hookOption', { name: hook.name, arm: formatNumber(hook.armLengthMm) }) }))}
        />
      </div>
    </div>
  )

  const hooksPane = (
    <div className="planogram-designer-library-pane">
      <p className="planogram-designer-library-tip">{t('planogram.designer.library.hooksTip')}</p>
      <div className="planogram-designer-library-list" role="radiogroup" aria-label={t('planogram.designer.library.currentHook')}>
        {hooks.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('planogram.spec.hookTable.empty')} /> : null}
        {hooks.map((hook) => {
          const selected = hook.guid === currentHookGuid
          return (
            <button
              key={hook.guid}
              type="button"
              role="radio"
              aria-checked={selected}
              disabled={readOnly}
              className={`planogram-designer-hook-card${selected ? ' is-selected' : ''}`}
              onClick={() => onCurrentHookChange(hook.guid)}
            >
              <span className="planogram-designer-hook-name">{hook.name}</span>
              <span className="planogram-designer-card-meta planogram-mono">{hook.code}</span>
              <span className="planogram-designer-card-meta">
                {[
                  hook.prongCount === 2 ? t('planogram.hook.prong.double') : t('planogram.hook.prong.single'),
                  t('planogram.designer.library.hookArm', { arm: formatNumber(hook.armLengthMm), drop: formatNumber(hook.dropMm) }),
                  hook.hasPriceTagArm ? t('planogram.designer.library.hookTag', { w: formatNumber(hook.tagWidthMm), h: formatNumber(hook.tagHeightMm) }) : t('planogram.designer.library.hookNoTag'),
                  hook.loadKg ? t('planogram.designer.library.hookLoad', { kg: formatNumber(hook.loadKg) }) : null,
                ].filter(Boolean).join(' · ')}
              </span>
            </button>
          )
        })}
      </div>
    </div>
  )

  return (
    <div className="planogram-designer-library">
      <Tabs
        size="small"
        className="planogram-designer-library-tabs"
        activeKey={tab}
        onChange={(key) => onTabChange(key as LibraryTab)}
        items={[
          { key: 'products', label: t('planogram.designer.library.tabProducts'), children: productsPane },
          { key: 'hooks', label: t('planogram.designer.library.tabHooks'), children: hooksPane },
        ]}
      />
    </div>
  )
}
