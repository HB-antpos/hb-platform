import {
  DeleteOutlined,
  InfoCircleOutlined,
  PlusOutlined,
  RightOutlined,
  SaveOutlined,
  SnippetsOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import { Button, Input, InputNumber, Tooltip, message } from 'antd'
import type { InputRef } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from 'react'
import type { ClipboardEvent, KeyboardEvent, ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import {
  applyBatchAddProducts,
  createDraftProduct,
  createDraftSetSubItem,
  getValidSetSubItems,
  normalizeCreateCount,
  resolveProductPrice,
} from './batchCreateRules'
import type { DraftProductItem, DraftSetSubItem } from './batchCreateRules'
import {
  applyParentColumnPaste,
  applySubItemColumnPaste,
  getNextBatchCreateEditableCell,
} from './batchCreateGridRules'
import type {
  BatchCreateEditableField,
  BatchCreateNavigationDirection,
  BatchCreatePasteField,
} from './batchCreateGridRules'
import BatchGridToolbar from './BatchGridToolbar'
import type { BatchAddValues } from './BatchGridToolbar'
import {
  addSubItem,
  applyBatchRename,
  removeProduct,
  removeSubItem,
  updateProductField,
  updateSubItemField,
} from './batchWorkspaceLogic'
import type { BatchRenameMode } from './batchWorkspaceLogic'

const PARENT_GRID_SCOPE = 'parent'
const PARENT_NORMAL_FIELDS = ['productName', 'privateLabelPrice'] as const
// 套装行多一个「创建套数」；套装价格放在展开的子项面板里，不在方向键导航范围内。
const PARENT_SET_FIELDS = ['productName', 'privateLabelPrice', 'createCount'] as const
const SUB_ITEM_FIELDS = ['productName', 'privateLabelPrice'] as const
const NAVIGATION_DIRECTION_BY_KEY: Partial<Record<string, BatchCreateNavigationDirection>> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
}

type PasteTarget =
  | { scope: 'parent'; field: BatchCreatePasteField }
  | { scope: 'subItem'; setKey: string; field: BatchCreatePasteField }

type FocusableCell = {
  focus: () => void
  select?: () => void
  input?: HTMLInputElement | null
  nativeElement?: HTMLElement | null
}

function isSamePasteTarget(left: PasteTarget | null, right: PasteTarget) {
  if (!left || left.scope !== right.scope || left.field !== right.field) return false
  return left.scope === 'parent' || (right.scope === 'subItem' && left.setKey === right.setKey)
}

const getSubItemScope = (setKey: string) => `subItem:${setKey}`
const buildCellKey = (scope: string, rowKey: string, field: BatchCreateEditableField) => `${scope}:${rowKey}:${field}`

export interface BatchGridHandle {
  /** 定位到某一行：套装行自动展开，滚动到可见区域、短暂高亮，并把焦点放到最需要补的输入框。 */
  locateRow: (rowKey: string) => void
}

interface BatchGridProps {
  products: DraftProductItem[]
  onProductsChange: (updater: (current: DraftProductItem[]) => DraftProductItem[]) => void
  expandedKeys: string[]
  onExpandedKeysChange: (updater: (current: string[]) => string[]) => void
  /** 校验不通过的行（套装没有有效子项），整行标红。 */
  invalidRowKeys: ReadonlySet<string>
  /** 未选供应商时不能保存模板（模板按供应商隔离）。 */
  canSaveTemplate: boolean
  onSaveAsTemplate: (setKey: string) => void
}

/**
 * 工作台中栏：工具栏 + 录入网格 + 底部提示。
 * 网格是受控的：所有草稿数据由工作台持有，这里只负责渲染、键盘导航与 Ctrl+V 单列粘贴。
 */
const BatchGrid = forwardRef<BatchGridHandle, BatchGridProps>(function BatchGrid(
  { products, onProductsChange, expandedKeys, onExpandedKeysChange, invalidRowKeys, canSaveTemplate, onSaveAsTemplate },
  ref,
) {
  const { t } = useTranslation()
  const rootRef = useRef<HTMLDivElement>(null)
  const cellRefs = useRef(new Map<string, FocusableCell>())
  const flashTimerRef = useRef<number>()
  const [pasteTarget, setPasteTarget] = useState<PasteTarget | null>(null)
  const [flashKey, setFlashKey] = useState<string | null>(null)

  useEffect(() => () => window.clearTimeout(flashTimerRef.current), [])

  const setCellRef = useCallback((rowKey: string, field: BatchCreateEditableField, cell: FocusableCell | null, setKey?: string) => {
    const cellKey = buildCellKey(setKey ? getSubItemScope(setKey) : PARENT_GRID_SCOPE, rowKey, field)
    if (cell) cellRefs.current.set(cellKey, cell)
    else cellRefs.current.delete(cellKey)
  }, [])

  const focusCell = useCallback((scope: string, rowKey: string, field: BatchCreateEditableField) => {
    window.requestAnimationFrame(() => {
      const cell = cellRefs.current.get(buildCellKey(scope, rowKey, field))
      if (!cell) return
      const inputElement = cell.input
        ?? (cell.nativeElement instanceof HTMLInputElement
          ? cell.nativeElement
          : cell.nativeElement?.querySelector<HTMLInputElement>('input'))
      const scrollTarget = inputElement ?? cell.nativeElement
      scrollTarget?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
      cell.focus()
      window.requestAnimationFrame(() => {
        cell.select?.()
        inputElement?.select()
      })
    })
  }, [])

  useImperativeHandle(ref, () => ({
    locateRow(rowKey: string) {
      const target = products.find((product) => product.key === rowKey)
      if (target?.productType === ProductCreationType.SET) {
        onExpandedKeysChange((keys) => (keys.includes(rowKey) ? keys : [...keys, rowKey]))
      }
      setFlashKey(rowKey)
      window.clearTimeout(flashTimerRef.current)
      flashTimerRef.current = window.setTimeout(() => setFlashKey((current) => (current === rowKey ? null : current)), 1800)
      // 展开后子项面板要等下一帧才渲染出来，所以滚动与聚焦都放到 rAF 里。
      window.requestAnimationFrame(() => {
        const root = rootRef.current
        if (!root) return
        const row = root.querySelector<HTMLElement>(`tr[data-row-key="${rowKey}"]`)
        row?.scrollIntoView({ block: 'center', behavior: 'smooth' })
        const subItemInput = root.querySelector<HTMLInputElement>(`[data-set-key="${rowKey}"] input`)
        const fallbackInput = row?.querySelector<HTMLInputElement>('input')
        ;(subItemInput ?? fallbackInput)?.focus({ preventScroll: true })
      })
    },
  }), [onExpandedKeysChange, products])

  // ---------- 方向键在单元格间切换 ----------
  const handleCellKeyDown = useCallback((
    event: KeyboardEvent<HTMLElement>,
    rowKey: string,
    field: BatchCreateEditableField,
    setKey?: string,
  ) => {
    const direction = NAVIGATION_DIRECTION_BY_KEY[event.key]
    if (!direction || event.nativeEvent.isComposing) return

    // 方向键在录入表格中专用于切换单元格，边界处也保持当前输入框焦点。
    event.preventDefault()
    event.stopPropagation()

    const navigationRows = setKey
      ? (products.find((product) => product.key === setKey)?.subItems || []).map((subItem) => ({
        rowKey: subItem.key,
        fields: SUB_ITEM_FIELDS,
      }))
      : products.map((product) => ({
        rowKey: product.key,
        fields: product.productType === ProductCreationType.SET ? PARENT_SET_FIELDS : PARENT_NORMAL_FIELDS,
      }))
    const next = getNextBatchCreateEditableCell({ rows: navigationRows, current: { rowKey, field }, direction })
    if (next) focusCell(setKey ? getSubItemScope(setKey) : PARENT_GRID_SCOPE, next.rowKey, next.field)
  }, [focusCell, products])

  // ---------- Ctrl+V 粘贴 Excel 单列 ----------
  const handleColumnPaste = useCallback((
    event: ClipboardEvent<HTMLElement>,
    target: PasteTarget,
    startRowKey?: string,
  ) => {
    const clipboardText = event.clipboardData.getData('text')
    if (!clipboardText) return

    event.preventDefault()
    event.stopPropagation()
    setPasteTarget(target)
    const result = target.scope === 'parent'
      ? applyParentColumnPaste({
        products,
        startProductKey: startRowKey,
        field: target.field,
        clipboardText,
        createProduct: createDraftProduct,
      })
      : applySubItemColumnPaste({
        products,
        setKey: target.setKey,
        startSubItemKey: startRowKey,
        field: target.field,
        clipboardText,
      })

    if (result.error === 'multiple_columns') {
      message.warning(t('productCreation.pasteMultipleColumns'))
      return
    }
    if (result.error === 'missing_target') {
      setPasteTarget(null)
      message.warning(t('productCreation.pasteTargetMissing'))
      return
    }
    onProductsChange(() => result.products)
    if (result.appliedCount + result.clearedCount + result.addedCount > 0) {
      message.success(t('productCreation.pasteResult', {
        applied: result.appliedCount,
        cleared: result.clearedCount,
        added: result.addedCount,
      }))
    }
    if (result.invalidCount > 0) {
      message.warning(t('productCreation.pasteInvalidPrices', { count: result.invalidCount }))
    }
  }, [onProductsChange, products, t])

  /** 带剪贴板图标的列头：点击选中该列（高亮），再 Ctrl+V 粘贴；焦点在列头按钮上粘贴时从第一行开始。 */
  const renderPasteTitle = (label: string, target: PasteTarget): ReactNode => {
    const selected = isSamePasteTarget(pasteTarget, target)
    const accessibleLabel = selected
      ? t('productCreation.deselectPasteColumn', { column: label })
      : t('productCreation.selectPasteColumn', { column: label })
    return (
      <button
        type="button"
        className={selected ? 'pc-paste-title pc-paste-title-on' : 'pc-paste-title'}
        aria-label={accessibleLabel}
        aria-pressed={selected}
        title={accessibleLabel}
        onClick={(event) => {
          event.stopPropagation()
          setPasteTarget((current) => (isSamePasteTarget(current, target) ? null : target))
        }}
        onPaste={(event) => handleColumnPaste(event, target)}
      >
        {label}
        <SnippetsOutlined />
      </button>
    )
  }

  // ---------- 行操作 ----------
  const addNormalRow = () => {
    const row = createDraftProduct(ProductCreationType.NORMAL, products.length)
    onProductsChange((current) => [...current, row])
    focusCell(PARENT_GRID_SCOPE, row.key, 'productName')
  }

  const addSetRow = () => {
    const row = createDraftProduct(ProductCreationType.SET, products.length)
    onProductsChange((current) => [...current, row])
    onExpandedKeysChange((keys) => [...keys, row.key])
    focusCell(PARENT_GRID_SCOPE, row.key, 'productName')
  }

  const handleBatchAdd = ({ type, count, price, mode }: BatchAddValues) => {
    const next = applyBatchAddProducts({
      products,
      selectedRowKeys: [],
      expandedRowKeys: expandedKeys,
      type,
      count,
      price,
      mode,
      createProduct: createDraftProduct,
    })
    onProductsChange(() => next.products)
    onExpandedKeysChange(() => next.expandedRowKeys)
    setPasteTarget(null)
  }

  const handleBatchRename = (mode: BatchRenameMode, value: string) => {
    onProductsChange((current) => applyBatchRename(current, mode, value))
  }

  const handleDeleteRow = (key: string) => {
    if (products.length <= 1) {
      message.warning(t('productCreation.keepAtLeastOneRow'))
      return
    }
    onProductsChange((current) => removeProduct(current, key))
    onExpandedKeysChange((keys) => keys.filter((expandedKey) => expandedKey !== key))
    setPasteTarget((target) => (target?.scope === 'subItem' && target.setKey === key ? null : target))
  }

  const toggleExpanded = (key: string) => {
    onExpandedKeysChange((keys) => (keys.includes(key) ? keys.filter((item) => item !== key) : [...keys, key]))
    setPasteTarget((target) => (target?.scope === 'subItem' && target.setKey === key ? null : target))
  }

  // ---------- 列定义 ----------
  const nameTarget: PasteTarget = { scope: 'parent', field: 'productName' }
  const priceTarget: PasteTarget = { scope: 'parent', field: 'privateLabelPrice' }

  const columns: ColumnsType<DraftProductItem> = [
    {
      title: '#',
      key: 'index',
      width: 32,
      render: (_, __, index) => <span className="pc-index">{index + 1}</span>,
    },
    {
      title: t('productCreation.type'),
      dataIndex: 'productType',
      key: 'type',
      width: 58,
      render: (type: ProductCreationType) => (
        <span className={type === ProductCreationType.SET ? 'pc-type-tag pc-type-tag-set' : 'pc-type-tag'}>
          {type === ProductCreationType.SET ? t('productCreation.set') : t('productCreation.normal')}
        </span>
      ),
    },
    {
      title: renderPasteTitle(t('domesticProducts.productName'), nameTarget),
      key: 'name',
      render: (_, record) => (
        <Input
          size="small"
          ref={(cell: InputRef | null) => setCellRef(record.key, 'productName', cell)}
          className={isSamePasteTarget(pasteTarget, nameTarget) ? 'pc-cell pc-cell-selected' : 'pc-cell'}
          value={record.productName}
          placeholder={t('productCreation.namePlaceholder')}
          aria-label={t('domesticProducts.productName')}
          onChange={(event) => onProductsChange((current) => updateProductField(current, record.key, 'productName', event.target.value))}
          onKeyDown={(event) => handleCellKeyDown(event, record.key, 'productName')}
          onPaste={(event) => handleColumnPaste(event, nameTarget, record.key)}
        />
      ),
    },
    {
      title: renderPasteTitle(`${t('productCreation.privateLabelPrice')} $`, priceTarget),
      key: 'price',
      width: 92,
      render: (_, record) => (
        <InputNumber
          size="small"
          controls={false}
          ref={(cell: FocusableCell | null) => setCellRef(record.key, 'privateLabelPrice', cell)}
          className={isSamePasteTarget(pasteTarget, priceTarget) ? 'pc-cell pc-cell-num pc-cell-selected' : 'pc-cell pc-cell-num'}
          // 套装主档没手填时显示子项零售价之和（与提交口径一致，见 resolveProductPrice）。
          value={resolveProductPrice(record)}
          min={0}
          precision={2}
          aria-label={t('productCreation.privateLabelPrice')}
          onChange={(value) => onProductsChange((current) => updateProductField(current, record.key, 'privateLabelPrice', value))}
          onKeyDown={(event) => handleCellKeyDown(event, record.key, 'privateLabelPrice')}
          onPaste={(event) => handleColumnPaste(event, priceTarget, record.key)}
        />
      ),
    },
    {
      title: t('productCreation.createCount'),
      key: 'createCount',
      width: 72,
      render: (_, record) => (
        record.productType === ProductCreationType.SET ? (
          <InputNumber
            size="small"
            controls={false}
            ref={(cell: FocusableCell | null) => setCellRef(record.key, 'createCount', cell)}
            className="pc-cell pc-cell-num pc-cell-center"
            value={record.createCount ?? 1}
            min={1}
            precision={0}
            aria-label={t('productCreation.createCount')}
            onChange={(value) => onProductsChange((current) => updateProductField(current, record.key, 'createCount', normalizeCreateCount(value)))}
            onKeyDown={(event) => handleCellKeyDown(event, record.key, 'createCount')}
          />
        ) : <span className="pc-faint">—</span>
      ),
    },
    {
      title: t('productCreation.subItemsColumn'),
      key: 'subItems',
      width: 78,
      render: (_, record) => {
        if (record.productType !== ProductCreationType.SET) return <span className="pc-faint">—</span>
        const validCount = getValidSetSubItems(record.subItems).length
        const expanded = expandedKeys.includes(record.key)
        return (
          <Button
            type="link"
            size="small"
            className={validCount === 0 ? 'pc-expand-btn pc-expand-btn-pending' : 'pc-expand-btn'}
            aria-expanded={expanded}
            onClick={() => toggleExpanded(record.key)}
          >
            <RightOutlined className={expanded ? 'pc-expand-icon pc-expand-icon-open' : 'pc-expand-icon'} />
            {validCount === 0 ? t('productCreation.subItemPending') : t('productCreation.subItemCount', { count: validCount })}
          </Button>
        )
      },
    },
    {
      title: '',
      key: 'actions',
      width: 60,
      align: 'right',
      render: (_, record) => (
        <span className="pc-row-actions">
          {invalidRowKeys.has(record.key) ? (
            <Tooltip title={t('productCreation.issueSetNeedsSubItemTip')}>
              <WarningOutlined className="pc-row-warn" />
            </Tooltip>
          ) : null}
          <Button
            type="text"
            size="small"
            danger
            icon={<DeleteOutlined />}
            disabled={products.length <= 1}
            aria-label={t('productCreation.deleteRow')}
            title={t('productCreation.deleteRow')}
            onClick={() => handleDeleteRow(record.key)}
          />
        </span>
      ),
    },
  ]

  // ---------- 套装子项面板 ----------
  const renderSubItems = (setRow: DraftProductItem) => {
    const subNameTarget: PasteTarget = { scope: 'subItem', setKey: setRow.key, field: 'productName' }
    const subPriceTarget: PasteTarget = { scope: 'subItem', setKey: setRow.key, field: 'privateLabelPrice' }
    const subColumns: ColumnsType<DraftSetSubItem> = [
      { title: '#', key: 'index', width: 30, render: (_, __, index) => <span className="pc-index">{index + 1}</span> },
      {
        title: renderPasteTitle(t('productCreation.subItemName'), subNameTarget),
        key: 'name',
        render: (_, record) => (
          <Input
            size="small"
            ref={(cell: InputRef | null) => setCellRef(record.key, 'productName', cell, setRow.key)}
            className={isSamePasteTarget(pasteTarget, subNameTarget) ? 'pc-cell pc-cell-selected' : 'pc-cell'}
            value={record.productName}
            placeholder={t('productCreation.subItemName')}
            aria-label={t('productCreation.subItemName')}
            onChange={(event) => onProductsChange((current) => updateSubItemField(current, setRow.key, record.key, 'productName', event.target.value))}
            onKeyDown={(event) => handleCellKeyDown(event, record.key, 'productName', setRow.key)}
            onPaste={(event) => handleColumnPaste(event, subNameTarget, record.key)}
          />
        ),
      },
      {
        title: renderPasteTitle(`${t('productCreation.privateLabelPrice')} $`, subPriceTarget),
        key: 'price',
        width: 98,
        render: (_, record) => (
          <InputNumber
            size="small"
            controls={false}
            ref={(cell: FocusableCell | null) => setCellRef(record.key, 'privateLabelPrice', cell, setRow.key)}
            className={isSamePasteTarget(pasteTarget, subPriceTarget) ? 'pc-cell pc-cell-num pc-cell-selected' : 'pc-cell pc-cell-num'}
            value={record.privateLabelPrice}
            min={0}
            precision={2}
            aria-label={t('productCreation.privateLabelPrice')}
            onChange={(value) => onProductsChange((current) => updateSubItemField(current, setRow.key, record.key, 'privateLabelPrice', value))}
            onKeyDown={(event) => handleCellKeyDown(event, record.key, 'privateLabelPrice', setRow.key)}
            onPaste={(event) => handleColumnPaste(event, subPriceTarget, record.key)}
          />
        ),
      },
      {
        title: '',
        key: 'actions',
        width: 40,
        align: 'right',
        render: (_, record) => (
          <Button
            type="text"
            size="small"
            danger
            icon={<DeleteOutlined />}
            aria-label={t('productCreation.deleteSubItem')}
            title={t('productCreation.deleteSubItem')}
            onClick={() => {
              onProductsChange((current) => removeSubItem(current, setRow.key, record.key))
              setPasteTarget((target) => (target?.scope === 'subItem' && target.setKey === setRow.key ? null : target))
            }}
          />
        ),
      },
    ]

    return (
      <div className="pc-sub-panel" data-set-key={setRow.key} data-testid="product-creation-sub-panel">
        <div className="pc-sub-head">
          <span className="pc-sub-title">{t('productCreation.setSubItemsTitle', { count: setRow.subItems?.length ?? 0 })}</span>
          <Button
            type="link"
            size="small"
            icon={<PlusOutlined />}
            onClick={() => onProductsChange((current) => addSubItem(current, setRow.key, createDraftSetSubItem()))}
          >
            {t('productCreation.addSubItem')}
          </Button>
          <Tooltip title={canSaveTemplate ? undefined : t('productCreation.selectSupplierFirst')}>
            {/* 禁用按钮不触发 Tooltip，所以包一层 span */}
            <span>
              <Button
                type="link"
                size="small"
                icon={<SaveOutlined />}
                disabled={!canSaveTemplate}
                onClick={() => onSaveAsTemplate(setRow.key)}
              >
                {t('productCreation.saveSetTemplate')}
              </Button>
            </span>
          </Tooltip>
          <span className="pc-grid-toolbar-spacer" />
          {/* 套装价格会作为该套装的国内价写入（后端 DomesticPrice = SetPrice），旧页面有此入口，设计稿未画出但数据仍需保留。 */}
          <label className="pc-set-price">
            <span className="pc-sub">{t('productCreation.setPrice')} $</span>
            <InputNumber
              size="small"
              controls={false}
              className="pc-cell pc-cell-num"
              value={setRow.setPrice}
              min={0}
              precision={2}
              placeholder={t('productCreation.optional')}
              title={t('productCreation.setPriceHint')}
              onChange={(value) => onProductsChange((current) => updateProductField(current, setRow.key, 'setPrice', value))}
            />
          </label>
        </div>
        <MeasuredTable<DraftSetSubItem>
          metricId="domestic-purchase.product-creation.workspace-sub-items"
          className="pc-sub-table"
          columns={subColumns}
          dataSource={setRow.subItems || []}
          rowKey="key"
          pagination={false}
          size="small"
          tableLayout="fixed"
          locale={{
            emptyText: (
              <Button
                type="link"
                size="small"
                icon={<PlusOutlined />}
                onClick={() => onProductsChange((current) => addSubItem(current, setRow.key, createDraftSetSubItem()))}
              >
                {t('productCreation.addSetSubItem')}
              </Button>
            ),
          }}
        />
      </div>
    )
  }

  return (
    <div className="pc-grid-card" ref={rootRef} data-testid="product-creation-grid">
      <BatchGridToolbar
        rowCount={products.length}
        onAddNormal={addNormalRow}
        onAddSet={addSetRow}
        onBatchAdd={handleBatchAdd}
        onBatchRename={handleBatchRename}
      />
      <MeasuredTable<DraftProductItem>
        metricId="domestic-purchase.product-creation.workspace-grid"
        className="pc-grid-table"
        columns={columns}
        dataSource={products}
        rowKey="key"
        pagination={false}
        size="small"
        tableLayout="fixed"
        rowClassName={(record) => [
          'pc-grid-row',
          invalidRowKeys.has(record.key) ? 'pc-grid-row-error' : '',
          flashKey === record.key ? 'pc-grid-row-flash' : '',
        ].filter(Boolean).join(' ')}
        expandable={{
          expandedRowKeys: expandedKeys,
          onExpandedRowsChange: (keys) => onExpandedKeysChange(() => keys.map(String)),
          rowExpandable: (record) => record.productType === ProductCreationType.SET,
          // 展开入口是「子项」列里的按钮，不要 antd 默认的展开箭头列。
          showExpandColumn: false,
          expandedRowRender: renderSubItems,
        }}
      />
      <div className="pc-grid-hint">
        <InfoCircleOutlined />
        <span>
          {t('productCreation.pasteHintLead')}
          <SnippetsOutlined className="pc-grid-hint-icon" />
          {t('productCreation.pasteHintTail')}
        </span>
      </div>
    </div>
  )
})

export default BatchGrid
