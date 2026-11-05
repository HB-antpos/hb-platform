import { ExclamationCircleOutlined, FilterOutlined, InfoCircleOutlined, ReloadOutlined, SearchOutlined, EditOutlined, UnorderedListOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Input, Segmented, Tag, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import ProductListImage from '../../../../components/ProductListImage'
import { listProductSpecs } from '../../../../services/planogramService'
import type {
  HookModelDto,
  PlanogramPaged,
  ProductSpecBatchFilter,
  ProductSpecListItemDto,
  ProductSpecListQuery,
  ProductSpecStatusFilter,
} from '../../../../types/planogram'
import { GradeChips, SupplierCategorySelect, WarehouseCategorySelect } from '../categoryFilter/CategoryFilterControls'
import { scopeFilterKey, toBatchFilter } from '../categoryFilter/categoryFilterLogic'
import { useCategoryFilter } from '../categoryFilter/useCategoryFilter'
import GradeTag from '../GradeTag'
import {
  DEFAULT_PAGE_SIZE,
  effectivePage,
  EMPTY_CELL,
  FILTER_DEBOUNCE_MS,
  formatDimensions,
  formatNumber,
  formatUpdatedAt,
  listHookQty,
  PAGE_SIZE_OPTIONS,
  planogramErrorText,
  resolvePlanogramError,
  SEARCH_DEBOUNCE_MS,
  type PagingState,
} from '../planogramLogic'
import { useDebouncedValue, usePlanogramRequest } from '../usePlanogramRequest'
import BatchEditModal from './BatchEditModal'
import {
  BATCH_MAX_TARGETS,
  EMPTY_BATCH_SELECTION,
  selectionAdd,
  selectionKeys,
  selectionRemove,
  selectionSyncRows,
  type BatchSelection,
} from './batchLogic'
import ProductSpecDrawer from './ProductSpecDrawer'

interface ProductSpecsProps {
  /** Planogram.Edit：补充 / 修改参数、批量修改；否则只读查看（没有勾选列、没有批量入口）。 */
  canEdit: boolean
  /** Planogram.Manage：清除参数。 */
  canManage: boolean
  /** 全部挂钩型号（含停用），由页面统一加载。 */
  hooks: HookModelDto[]
  hooksReady: boolean
  hooksFailed: boolean
  onReloadHooks: () => void
}

/** 未设参数的行把「包装 … 每钩挂数」五列并成一格，显示一句提示。 */
const UNSET_SPAN = 5

/** 打开批量修改弹窗时冻结的现场：目标（勾选 / 当前筛选）与列表总数在弹窗打开期间不再变化。 */
interface BatchSession {
  id: number
  initial: 'selected' | 'filter'
  selection: BatchSelection
  filter: ProductSpecBatchFilter
  total: number
}

/**
 * 商品陈列参数：一排筛选条（搜索 / 状态 / 仓库分类 / 本地供应商分类 / 等级 / 清除）+ 列表（带缩略图）+ 右侧抽屉编辑；
 * 有编辑权限时多一列勾选（跨页保留，最多 2000 个）和蓝色操作条，可「批量修改参数」（已选）或「按当前筛选的全部 N 个批量修改」。
 */
export default function ProductSpecs({ canEdit, canManage, hooks, hooksReady, hooksFailed, onReloadHooks }: ProductSpecsProps) {
  const { t, i18n } = useTranslation()
  const [keywordDraft, setKeywordDraft] = useState('')
  const [status, setStatus] = useState<ProductSpecStatusFilter>('all')
  const [drawer, setDrawer] = useState<ProductSpecListItemDto | null>(null)
  const keyword = useDebouncedValue(keywordDraft.trim(), SEARCH_DEBOUNCE_MS)
  // 分类 / 等级条件：基础数据（分类树、供应商）在用户第一次打开下拉时才加载；加载失败提示一条，下拉里可重试，不影响列表
  const categoryFilter = useCategoryFilter({
    onLoadError: () => { message.error(t('planogram.categoryFilter.loadFailed')) },
  })
  // 连点几个分类时只发最后一次请求（与搜索框的防抖同一个思路）
  const scope = useDebouncedValue(categoryFilter.scope, FILTER_DEBOUNCE_MS)
  // 关键字 / 状态 / 分类 / 等级任一变化（防抖后）→ 有效页码回到第 1 页（见 effectivePage）
  const filterKey = `${keyword}|${status}|${scopeFilterKey(scope)}`
  const [paging, setPaging] = useState<PagingState>({ page: 1, pageSize: DEFAULT_PAGE_SIZE, filterKey })
  const page = effectivePage(paging, filterKey)

  const query = useMemo<ProductSpecListQuery>(
    () => ({ keyword: keyword || undefined, status, ...scope, page, pageSize: paging.pageSize }),
    [keyword, page, paging.pageSize, scope, status],
  )
  const request = usePlanogramRequest<PlanogramPaged<ProductSpecListItemDto>>(
    JSON.stringify(query),
    (signal) => listProductSpecs(query, signal),
  )

  const hooksByGuid = useMemo(() => new Map(hooks.map((hook) => [hook.guid, hook])), [hooks])

  // ---------- 跨页勾选 ----------
  const [selection, setSelection] = useState<BatchSelection>(EMPTY_BATCH_SELECTION)
  const selectedKeys = useMemo(() => selectionKeys(selection), [selection])
  const data = request.data

  // 列表刷新后用当前页同步「是否已设」标记（比如在抽屉里给已勾选的商品补了参数）
  useEffect(() => {
    if (data) setSelection((current) => selectionSyncRows(current, data.items))
  }, [data])

  const changeSelection = useCallback((rows: ProductSpecListItemDto[], selected: boolean) => {
    if (!selected) {
      setSelection((current) => selectionRemove(current, rows.map((row) => row.productCode)))
      return
    }
    const result = selectionAdd(selection, rows)
    if (result.rejected > 0) message.warning(t('planogram.spec.selection.limit', { max: BATCH_MAX_TARGETS }))
    setSelection(result.selection)
  }, [selection, t])
  const clearSelection = useCallback(() => setSelection(EMPTY_BATCH_SELECTION), [])

  // ---------- 批量修改弹窗 ----------
  const [batch, setBatch] = useState<BatchSession | null>(null)
  const batchIdRef = useRef(0)
  const openBatch = (initial: 'selected' | 'filter') => {
    // 把现场冻结下来：打开期间列表再刷新、总数再变，都不影响这次批量
    batchIdRef.current += 1
    setBatch({
      id: batchIdRef.current,
      initial,
      selection,
      filter: toBatchFilter({ keyword, status }, scope),
      total: data?.total ?? 0,
    })
    onReloadHooks()
  }
  const closeBatch = (executed: boolean) => {
    setBatch(null)
    if (executed) {
      // 批量改过：已勾选的商品参数已变，清空勾选并刷新列表
      clearSelection()
      request.reload()
    }
  }

  const openDrawer = useCallback((item: ProductSpecListItemDto) => {
    // 打开时顺手刷新挂钩型号目录：型号可能刚在另一个页签改过
    onReloadHooks()
    // 复制一份：同一行再次打开也是新的 target 对象，抽屉表单据此重建
    setDrawer({ ...item })
  }, [onReloadHooks])

  const columns = useMemo<ColumnsType<ProductSpecListItemDto>>(() => {
    // 未设参数的行：第一个并格列显示提示，后面四列让位
    const spanCell = (index: number) => (row: ProductSpecListItemDto) => ({
      colSpan: row.hasSpec ? 1 : index === 0 ? UNSET_SPAN : 0,
    })
    const quantity = (item: ProductSpecListItemDto) => {
      const resolved = listHookQty(item, hooksByGuid)
      if (!resolved) return <span className="planogram-muted-text">{EMPTY_CELL}</span>
      const { result } = resolved
      const effective = result.effective > 0 ? result.effective : EMPTY_CELL
      if (result.source === 'override') {
        return (
          <span>
            <strong>{effective}</strong>
            <Tag color="gold" className="planogram-mini-tag">{t('planogram.spec.manualTag')}</Tag>
            {result.auto !== result.effective ? (
              <span className="planogram-muted-text">{t('planogram.spec.autoValue', { value: result.auto })}</span>
            ) : null}
          </span>
        )
      }
      return <span><strong>{effective}</strong> <span className="planogram-muted-text">{t('planogram.spec.autoTag')}</span></span>
    }
    return [
      {
        title: t('planogram.spec.col.image'),
        key: 'image',
        width: 56,
        render: (_: unknown, row) => (
          row.productImage
            ? <ProductListImage src={row.productImage} size={36} radius={6} />
            : <div className="planogram-thumb">{t('planogram.spec.noImage')}</div>
        ),
      },
      {
        // 货号（itemNumber）为主、条码为辅；内部 productCode 不再大字显示，放在悬浮提示里
        title: t('planogram.spec.col.code'),
        key: 'code',
        render: (_: unknown, row) => (
          <Tooltip title={t('planogram.spec.internalCode', { code: row.productCode })} placement="topLeft">
            <div>
              <div className="planogram-mono planogram-item-no">{row.itemNumber || row.productCode}</div>
              <div className="planogram-mono planogram-muted-text planogram-small">{row.barcode || EMPTY_CELL}</div>
            </div>
          </Tooltip>
        ),
      },
      {
        title: t('planogram.spec.col.name'),
        key: 'name',
        render: (_: unknown, row) => (
          <div>
            <div>{row.productName}</div>
            {row.englishName ? <div className="planogram-muted-text planogram-small">{row.englishName}</div> : null}
          </div>
        ),
      },
      {
        title: t('planogram.spec.col.grade'),
        key: 'grade',
        render: (_: unknown, row) => <GradeTag grade={row.grade} />,
      },
      {
        // 仓库分类完整路径（「 > 」分隔）按原样显示，过长省略并在悬浮里给全路径
        title: t('planogram.spec.col.warehouse'),
        key: 'warehouse',
        render: (_: unknown, row) => (
          row.warehouseCategoryPath
            ? (
              <Tooltip title={row.warehouseCategoryPath} placement="topLeft">
                <span className="planogram-warehouse-path">{row.warehouseCategoryPath}</span>
              </Tooltip>
            )
            : <span className="planogram-muted-text">{EMPTY_CELL}</span>
        ),
      },
      {
        title: t('planogram.spec.col.size'),
        key: 'size',
        onCell: spanCell(0),
        render: (_: unknown, row) => (
          row.hasSpec && row.spec
            ? formatDimensions([row.spec.widthMm, row.spec.heightMm, row.spec.depthMm])
            : (
              <span className="planogram-unset-text">
                <ExclamationCircleOutlined aria-hidden="true" /> {t('planogram.spec.unsetRow')}
              </span>
            )
        ),
      },
      {
        title: t('planogram.spec.col.hangOffset'),
        key: 'hangOffset',
        onCell: spanCell(1),
        render: (_: unknown, row) => (row.spec ? `${formatNumber(row.spec.hangHoleOffsetMm)} mm` : null),
      },
      {
        title: t('planogram.spec.col.weight'),
        key: 'weight',
        onCell: spanCell(2),
        render: (_: unknown, row) => (row.spec?.unitWeightG ? `${row.spec.unitWeightG} g` : EMPTY_CELL),
      },
      {
        title: t('planogram.spec.col.defaultHook'),
        key: 'defaultHook',
        onCell: spanCell(3),
        render: (_: unknown, row) => (row.spec?.defaultHookGuid ? hooksByGuid.get(row.spec.defaultHookGuid)?.name ?? EMPTY_CELL : EMPTY_CELL),
      },
      {
        title: t('planogram.spec.col.qty'),
        key: 'qty',
        onCell: spanCell(4),
        render: (_: unknown, row) => quantity(row),
      },
      { title: t('planogram.common.col.referenced'), dataIndex: 'referencedPlanCount', key: 'referenced', align: 'right' },
      {
        title: t('planogram.spec.col.updated'),
        key: 'updated',
        render: (_: unknown, row) => <span className="planogram-muted-text planogram-small">{row.hasSpec ? formatUpdatedAt(row.updatedAt) : EMPTY_CELL}</span>,
      },
      {
        title: t('planogram.common.col.actions'),
        key: 'actions',
        fixed: 'right',
        render: (_: unknown, row) => {
          if (!row.hasSpec) {
            // 未设参数：有编辑权限才给「补充参数」；只读账号没有可做的事
            return canEdit
              ? <Button type="link" size="small" onClick={() => openDrawer(row)}>{t('planogram.spec.addSpec')}</Button>
              : <span className="planogram-muted-text">{EMPTY_CELL}</span>
          }
          return (
            <Button type="link" size="small" onClick={() => openDrawer(row)}>
              {canEdit ? t('planogram.common.edit') : t('planogram.common.view')}
            </Button>
          )
        },
      },
    ]
  }, [canEdit, hooksByGuid, openDrawer, t])

  const errorText = useMemo(() => {
    if (!request.error) return null
    const resolved = resolvePlanogramError(request.error)
    return resolved.kind === 'error' ? planogramErrorText(resolved, i18n.language, (key) => t(key)) : null
  }, [i18n.language, request.error, t])

  const hasAnyFilter = keywordDraft.trim() !== '' || status !== 'all' || categoryFilter.hasActive
  const clearAllFilters = () => {
    setKeywordDraft('')
    setStatus('all')
    categoryFilter.clear()
  }
  const total = data?.total ?? 0

  return (
    <div className="planogram-pane">
      <div className="planogram-toolbar">
        <Input
          className="planogram-search is-wide"
          allowClear
          prefix={<SearchOutlined aria-hidden="true" />}
          placeholder={t('planogram.spec.searchPlaceholder')}
          aria-label={t('planogram.spec.searchPlaceholder')}
          value={keywordDraft}
          onChange={(event) => setKeywordDraft(event.target.value)}
        />
        <Segmented<ProductSpecStatusFilter>
          value={status}
          onChange={setStatus}
          aria-label={t('planogram.spec.statusLabel')}
          options={[
            { label: t('planogram.spec.status.all'), value: 'all' },
            { label: t('planogram.spec.status.set'), value: 'set' },
            { label: t('planogram.spec.status.unset'), value: 'unset' },
          ]}
        />
        <WarehouseCategorySelect filter={categoryFilter} />
        <SupplierCategorySelect filter={categoryFilter} />
        <div className="planogram-filter-grades">
          <span className="planogram-filter-caption">{t('planogram.grade.label')}</span>
          <GradeChips filter={categoryFilter} />
        </div>
        <Button type="link" className="planogram-filter-clear" icon={<FilterOutlined />} disabled={!hasAnyFilter} onClick={clearAllFilters}>
          {t('planogram.categoryFilter.clear')}
        </Button>
        <div className="planogram-toolbar-actions">
          <Tooltip title={t('planogram.common.refresh')}>
            <Button icon={<ReloadOutlined />} aria-label={t('planogram.common.refresh')} loading={request.loading} onClick={request.reload} />
          </Tooltip>
        </div>
      </div>
      {canEdit ? (
        // 批量入口只在有编辑权限时渲染：勾选后是蓝色操作条；没勾选时是灰色的一行，仍能直接「按当前筛选的全部」批量改
        <div className={`planogram-selbar${selectedKeys.length > 0 ? '' : ' is-idle'}`} role="region" aria-label={t('planogram.spec.selection.region')}>
          <InfoCircleOutlined className="planogram-selbar-icon" aria-hidden="true" />
          {selectedKeys.length > 0 ? (
            <>
              <span>{t('planogram.spec.selection.selected', { count: selectedKeys.length })}</span>
              <Button type="primary" size="small" icon={<EditOutlined />} onClick={() => openBatch('selected')}>
                {t('planogram.spec.selection.batchEdit')}
              </Button>
              <Button type="link" size="small" onClick={clearSelection}>{t('planogram.spec.selection.clear')}</Button>
              <span className="planogram-selbar-or">{t('planogram.spec.selection.or')}</span>
            </>
          ) : <span className="planogram-selbar-idle-text">{t('planogram.spec.selection.idle')}</span>}
          <Button size="small" icon={<UnorderedListOutlined />} disabled={total === 0} onClick={() => openBatch('filter')}>
            {t('planogram.spec.selection.byFilter', { count: total })}
          </Button>
        </div>
      ) : null}
      {errorText ? <Alert type="error" showIcon message={errorText} action={<a onClick={request.reload}>{t('planogram.common.retry')}</a>} /> : null}
      <MeasuredTable<ProductSpecListItemDto>
        metricId="pos-admin.planogram.product-specs"
        rowKey="productCode"
        size="middle"
        columns={columns}
        dataSource={data?.items ?? []}
        loading={request.loading}
        scroll={{ x: 'max-content' }}
        rowSelection={canEdit ? {
          selectedRowKeys: selectedKeys,
          columnWidth: 40,
          onSelect: (record, selected) => changeSelection([record], selected),
          onSelectAll: (selected, _rows, changeRows) => changeSelection(changeRows, selected),
        } : undefined}
        rowClassName={(row) => (selection.has(row.productCode) ? 'planogram-row-picked' : row.hasSpec ? '' : 'planogram-row-unset')}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={hasAnyFilter ? t('planogram.spec.emptyFiltered') : t('planogram.spec.empty')} /> }}
        pagination={{
          current: page,
          pageSize: paging.pageSize,
          total,
          showSizeChanger: true,
          pageSizeOptions: PAGE_SIZE_OPTIONS,
          showTotal: (count) => t('planogram.common.total', { total: count }),
          onChange: (nextPage, pageSize) => setPaging({ page: nextPage, pageSize, filterKey }),
        }}
      />
      <ProductSpecDrawer
        target={drawer}
        hooks={hooks}
        hooksReady={hooksReady}
        hooksFailed={hooksFailed}
        onReloadHooks={onReloadHooks}
        canEdit={canEdit}
        canManage={canManage}
        onClose={() => setDrawer(null)}
        onSaved={() => {
          setDrawer(null)
          request.reload()
        }}
      />
      {canEdit && batch ? (
        <BatchEditModal
          key={batch.id}
          open
          initialTarget={batch.initial}
          selection={batch.selection}
          filter={batch.filter}
          filterTotal={batch.total}
          hooks={hooks}
          hooksReady={hooksReady}
          hooksFailed={hooksFailed}
          onReloadHooks={onReloadHooks}
          onClose={closeBatch}
        />
      ) : null}
    </div>
  )
}
