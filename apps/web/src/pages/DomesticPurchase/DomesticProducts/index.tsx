import {
  DeleteOutlined,
  DownOutlined,
  DownloadOutlined,
  GiftOutlined,
  LoadingOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import { Button, Card, Dropdown, Input, Popconfirm, Segmented, Select, Tooltip, message } from 'antd'
import type { MenuProps } from 'antd'
import type { TablePaginationConfig } from 'antd/es/table'
import type { FilterValue, SorterResult } from 'antd/es/table/interface'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import ActiveFilterBar from '../../../components/listToolbar/ActiveFilterBar'
import type { ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import ToolbarMenuButton from '../../../components/listToolbar/ToolbarMenuButton'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  batchDeleteDomesticProducts,
  getDomesticProductsGrid,
  getSupplierOptions,
} from '../../../services/domesticProductService'
import { exportDomesticProductsToExcel } from '../../../services/exportService'
import { useAuthStore } from '../../../store/auth'
import type { DomesticProductGridQuery, DomesticProductItem, SupplierOption } from '../../../types/domesticProduct'
import { ProductType } from '../../../types/domesticProduct'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import ProductDetailDrawer from './ProductDetailDrawer'
import ProductFormModal, { type ProductSavedResult } from './ProductFormModal'
import { buildProductColumns } from './productColumns'
import SetItemsModal, { type SetItemsSavedResult } from './SetItemsModal'
import {
  DEFAULT_PAGE_SIZE,
  DEFAULT_SORT_FIELD,
  DEFAULT_SORT_ORDER,
  LIST_COLUMN_WIDTHS,
  LIST_TABLE_MIN_WIDTH,
  PAGE_SIZE_OPTIONS,
  applyFormValuesToItem,
  buildSupplierOptions,
  describeSupplier,
  filterSupplierOption,
  resolveExportPageSize,
  resolveTableChange,
  shouldIgnoreRowClick,
  statusFromSegment,
  statusToSegment,
  typeFromSegment,
  typeToSegment,
  type SortOrderValue,
  type StatusSegment,
  type TypeSegment,
} from './domesticProductsLogic'
import './domesticProducts.css'
import domesticProductsMessagesEn from './domesticProductsMessages.en.json'
import domesticProductsMessagesZh from './domesticProductsMessages.zh.json'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: domesticProductsMessagesZh, en: domesticProductsMessagesEn })

type ProductFilterPatch = Partial<Pick<DomesticProductGridQuery, 'searchText' | 'supplierCode' | 'productType' | 'isActive'>>
type ExportScope = 'filtered' | 'selected'

/**
 * 搜索框：输入过程中的草稿只存在这里，回车才提交为「已生效关键词」。
 * 单独成组件是为了不让每敲一个字符都重渲染整张 50 行的表格（含缩略图）。
 */
function SearchInput({
  value,
  placeholder,
  enterHint,
  onSubmit,
}: {
  value: string
  placeholder: string
  /** 草稿与已生效关键词不一致时在输入框右侧提示「回车」，其余时候不占位。 */
  enterHint: string
  onSubmit: (value: string) => void
}) {
  const [draft, setDraft] = useState(value)

  // 已生效关键词被外部改变（点标签上的 × / 清空全部）时，输入框同步。
  useEffect(() => {
    setDraft(value)
  }, [value])

  return (
    <Input
      value={draft}
      allowClear
      prefix={<SearchOutlined />}
      placeholder={placeholder}
      style={{ width: 240 }}
      suffix={draft.trim() !== value ? <span className="dp-enter-hint">{enterHint}</span> : null}
      data-testid="domestic-products-search"
      onChange={(event) => {
        setDraft(event.target.value)
        // 点清除按钮（事件类型为 click）立即复原列表；手动删字不触发，等回车。
        if (event.type === 'click' && !event.target.value && value) {
          onSubmit('')
        }
      }}
      onPressEnter={() => onSubmit(draft.trim())}
    />
  )
}

export default function DomesticProductsPage() {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [formOpen, setFormOpen] = useState(false)
  const [editingItem, setEditingItem] = useState<DomesticProductItem | null>(null)
  const [suppliers, setSuppliers] = useState<SupplierOption[]>([])
  const [data, setData] = useState<DomesticProductItem[]>([])
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  // searchText 是「已生效」的关键词；输入中的草稿在 SearchInput 里。
  const [searchText, setSearchText] = useState('')
  const [supplierCode, setSupplierCode] = useState<string>()
  const [productType, setProductType] = useState<ProductType>()
  const [isActive, setIsActive] = useState<boolean>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [total, setTotal] = useState(0)
  const [sortField, setSortField] = useState(DEFAULT_SORT_FIELD)
  const [sortOrder, setSortOrder] = useState<SortOrderValue>(DEFAULT_SORT_ORDER)
  // 页头「共 N 件」：与筛选无关的全库数量，单独取，不拿列表 total（后端 total 不含供应商/类型/状态筛选）。
  const [catalogTotal, setCatalogTotal] = useState<number>()
  const [exporting, setExporting] = useState(false)
  const [exportProgress, setExportProgress] = useState(0)
  const [exportMessage, setExportMessage] = useState('')
  const [deleting, setDeleting] = useState(false)
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailRecord, setDetailRecord] = useState<DomesticProductItem | null>(null)
  const [setItemsOpen, setSetItemsOpen] = useState(false)
  const [setItemsProduct, setSetItemsProduct] = useState<DomesticProductItem | null>(null)
  const [setItemsVersion, setSetItemsVersion] = useState(0)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const exportingRef = useRef(false)
  const mountedRef = useRef(false)
  const desiredGridQueryRef = useRef<DomesticProductGridQuery>({
    page,
    pageSize,
    searchText,
    supplierCode,
    productType,
    isActive,
    sortField,
    sortOrder,
  })
  const { access } = useAuthStore()

  const buildGridQuery = (overrides: Partial<DomesticProductGridQuery> = {}): DomesticProductGridQuery => ({
    page,
    pageSize,
    searchText,
    supplierCode,
    productType,
    isActive,
    sortField,
    sortOrder,
    ...overrides,
  })

  /** 列表查询。overrides 里显式写 undefined 表示「清除该筛选」，所以用对象合并而不是默认参数。 */
  const loadData = async (overrides: Partial<DomesticProductGridQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    const query = buildGridQuery(overrides)
    // 请求一开始就发布目标查询，mutation 晚完成时不依赖旧的成功页码。
    desiredGridQueryRef.current = query

    await runLatestGuardedRequest(
      listRequestGuardRef.current,
      () => getDomesticProductsGrid(query),
      {
        onStart: () => setLoading(true),
        onSuccess: (result) => {
          // total 原样使用：后端 countQuery 只含关键词条件，前端不做任何「修正」。
          setData(result.items)
          setTotal(result.total)
          setPage(result.page)
          setPageSize(result.pageSize)
          setSelectedRowKeys([])
        },
        onError: (error) => {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('domesticProducts.loadFailed', '加载国内商品失败'))
        },
        // 旧请求结束时不能关闭较新请求的 loading。
        onSettled: () => setLoading(false),
      },
    )
  }

  const latestLoadDataRef = useRef(loadData)

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
  })

  const refreshDesiredGrid = (overrides: Partial<DomesticProductGridQuery> = {}) =>
    latestLoadDataRef.current({ ...desiredGridQueryRef.current, ...overrides })

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      listRequestGuardRef.current.invalidate()
    }
  }, [])

  const loadCatalogTotal = async () => {
    try {
      const result = await getDomesticProductsGrid({ page: 1, pageSize: 1 })
      if (mountedRef.current) {
        setCatalogTotal(result.total)
      }
    } catch (error) {
      // 总数只是辅助信息，失败时保持页面可用，不打扰用户。
      console.error(error)
    }
  }

  useEffect(() => {
    void Promise.all([
      loadData({ page: 1 }),
      loadCatalogTotal(),
      getSupplierOptions()
        .then(setSuppliers)
        .catch((error) => {
          console.error(error)
          message.error(t('domesticProducts.loadSuppliersFailed', '加载供应商列表失败'))
        }),
    ])
    // 只在挂载时加载一次。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 列表刷新回来后，详情抽屉里的商品以服务端数据为准（保存后的乐观合并在这里被校正）。
  useEffect(() => {
    setDetailRecord((current) => {
      if (!current) {
        return current
      }
      return data.find((item) => item.id === current.id) ?? current
    })
  }, [data])

  /** 工具栏筛选：即时生效，回到第一页；筛选改变数据集，旧的勾选随之清空（loadData 成功时清）。 */
  const applyFilters = (patch: ProductFilterPatch) => {
    if ('searchText' in patch) {
      setSearchText(patch.searchText ?? '')
    }
    if ('supplierCode' in patch) {
      setSupplierCode(patch.supplierCode)
    }
    if ('productType' in patch) {
      setProductType(patch.productType)
    }
    if ('isActive' in patch) {
      setIsActive(patch.isActive)
    }
    void loadData({ page: 1, ...patch })
  }

  const handleTableChange = (
    pagination: TablePaginationConfig,
    _filters: Record<string, FilterValue | null>,
    sorter: SorterResult<DomesticProductItem> | SorterResult<DomesticProductItem>[],
    extra: { action: 'paginate' | 'sort' | 'filter' },
  ) => {
    const change = resolveTableChange(extra.action, pagination, sorter, pageSize)
    if (change.sortField) {
      setSortField(change.sortField)
      setSortOrder(change.sortOrder ?? DEFAULT_SORT_ORDER)
    }
    void loadData(change)
  }

  const handleOpenCreate = () => {
    setEditingItem(null)
    setFormOpen(true)
  }

  const handleOpenEdit = (record: DomesticProductItem) => {
    setEditingItem(record)
    setFormOpen(true)
  }

  const handleOpenDetail = (record: DomesticProductItem) => {
    setDetailRecord(record)
    setDetailOpen(true)
  }

  const handleOpenSetItems = (record: DomesticProductItem) => {
    setSetItemsProduct(record)
    setSetItemsOpen(true)
  }

  const handleProductSaved = (result: ProductSavedResult) => {
    setFormOpen(false)
    if (result.mode === 'edit') {
      // 抽屉里的商品先带上刚保存的值，列表刷新回来后再以服务端数据为准。
      setDetailRecord((current) =>
        current && current.id === result.product.id ? applyFormValuesToItem(current, result.values) : current,
      )
      void refreshDesiredGrid()
    } else {
      void refreshDesiredGrid({ page: 1 })
      void loadCatalogTotal()
    }
  }

  const handleSetItemsSaved = (result: SetItemsSavedResult) => {
    const productId = setItemsProduct?.id
    setSetItemsOpen(false)
    // 主码的国内价 / 零售价被同步成子项合计：抽屉先显示新合计，并重新拉取子项明细。
    setDetailRecord((current) =>
      current && current.id === productId
        ? {
            ...current,
            domesticPrice: result.syncedDomesticPrice ?? current.domesticPrice,
            labelPrice: result.syncedLabelPrice ?? current.labelPrice,
          }
        : current,
    )
    setSetItemsVersion((version) => version + 1)
    void refreshDesiredGrid()
  }

  const handleBatchDelete = async () => {
    if (deleting) {
      return
    }

    const deletingCount = selectedRowKeys.length
    try {
      setDeleting(true)
      await batchDeleteDomesticProducts(selectedRowKeys.map(String))
      message.success(t('domesticProducts.batchDeleteSuccess', '已删除 {{count}} 个国内商品', { count: deletingCount }))
      void refreshDesiredGrid({ page: 1 })
      void loadCatalogTotal()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('domesticProducts.batchDeleteFailed', '批量删除国内商品失败'))
    } finally {
      setDeleting(false)
    }
  }

  const handleExport = async (scope: ExportScope, includeLabelPrice: boolean) => {
    // 导出进行中不再重复发起（按钮 loading 之外再加一道 ref 保险，避免连点两次菜单项）。
    if (exportingRef.current) {
      return
    }
    exportingRef.current = true

    try {
      setExporting(true)
      setExportProgress(0)
      setExportMessage(t('domesticProducts.preparingExport', '准备导出...'))

      let productsToExport: DomesticProductItem[]
      if (scope === 'selected') {
        productsToExport = data.filter((item) => selectedRowKeys.includes(item.id))
      } else {
        if (!total) {
          message.warning(t('domesticProducts.noDataToExport', '没有可导出的商品数据'))
          return
        }

        // 沿用当前筛选与排序，一次取回全部结果；total 只用作 pageSize 上限。
        const result = await getDomesticProductsGrid(buildGridQuery({ page: 1, pageSize: resolveExportPageSize(total) }))
        productsToExport = result.items
      }

      if (!productsToExport.length) {
        message.warning(t('domesticProducts.noDataToExport', '没有可导出的商品数据'))
        return
      }

      await exportDomesticProductsToExcel(productsToExport, {
        includeLabelPrice,
        fileName: t('domesticProducts.exportFileName', '国内商品'),
        onProgress: (progress, nextMessage) => {
          setExportProgress(progress)
          setExportMessage(nextMessage)
        },
      })

      message.success(t('domesticProducts.exportSuccess', '导出成功'))
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('domesticProducts.exportFailed', '导出失败'))
    } finally {
      exportingRef.current = false
      setExporting(false)
      setExportProgress(0)
      setExportMessage('')
    }
  }

  const supplierOptions = useMemo(() => buildSupplierOptions(suppliers), [suppliers])

  const typeShortLabel = (type: ProductType) =>
    type === ProductType.SET
      ? t('domesticProducts.typeShortSet')
      : type === ProductType.MULTICODE
        ? t('domesticProducts.typeShortMulti')
        : t('domesticProducts.typeShortNormal')

  // 不用 useMemo：onRemove 会走到 loadData，它读取当前筛选 / 排序状态；缓存会让回调拿到旧值。
  const activeFilterItems = (() => {
    const items: ActiveFilterItem[] = []
    if (searchText) {
      items.push({
        key: 'keyword',
        label: t('domesticProducts.filterKeyword'),
        value: searchText,
        source: 'toolbar',
        onRemove: () => applyFilters({ searchText: '' }),
      })
    }
    if (supplierCode) {
      items.push({
        key: 'supplier',
        label: t('domesticProducts.supplier', '供应商'),
        value: describeSupplier(suppliers, supplierCode),
        source: 'toolbar',
        onRemove: () => applyFilters({ supplierCode: undefined }),
      })
    }
    if (productType !== undefined) {
      items.push({
        key: 'type',
        label: t('domesticProducts.filterType'),
        value: typeShortLabel(productType),
        source: 'toolbar',
        onRemove: () => applyFilters({ productType: undefined }),
      })
    }
    if (isActive !== undefined) {
      items.push({
        key: 'status',
        label: t('domesticProducts.status', '状态'),
        value: isActive ? t('common.enable', '启用') : t('common.disable', '停用'),
        source: 'toolbar',
        onRemove: () => applyFilters({ isActive: undefined }),
      })
    }
    return items
  })()

  // 勾选后「导出选中」的下拉：是否带零售价列沿用原导出弹窗里的选项。
  const selectedExportMenuItems: MenuProps['items'] = [
    { key: 'plain', label: t('domesticProducts.exportMenuSelected'), icon: <DownloadOutlined /> },
    { key: 'rrp', label: t('domesticProducts.exportMenuSelectedRrp'), icon: <DownloadOutlined /> },
  ]

  const rowMenu = (record: DomesticProductItem): MenuProps => ({
    items: [
      { key: 'detail', label: t('domesticProducts.rowViewDetail') },
      ...(record.productType === ProductType.SET && access.canWriteProduct
        ? [{ key: 'setItems', label: t('domesticProducts.setItems', '套装子项'), icon: <GiftOutlined /> }]
        : []),
      ...(record.itemNumber ? [{ key: 'copyNo', label: t('domesticProducts.rowCopyItemNumber') }] : []),
      ...(record.barcode ? [{ key: 'copyBarcode', label: t('domesticProducts.rowCopyBarcode') }] : []),
    ],
    onClick: ({ key, domEvent }) => {
      domEvent.stopPropagation()
      if (key === 'detail') {
        handleOpenDetail(record)
      } else if (key === 'setItems') {
        handleOpenSetItems(record)
      } else if (key === 'copyNo') {
        void copyTextToClipboard(record.itemNumber)
      } else if (key === 'copyBarcode') {
        void copyTextToClipboard(record.barcode)
      }
    },
  })

  const columns = buildProductColumns({
    t,
    sortField,
    sortOrder,
    canWrite: access.canWriteProduct,
    currentYear: new Date().getFullYear(),
    onEdit: handleOpenEdit,
    getRowMenu: rowMenu,
  })

  return (
    <PageContainer
      compact
      title={t('domesticProducts.pageTitle', '国内商品')}
      subtitle={
        catalogTotal === undefined
          ? undefined
          : t('domesticProducts.totalSummary', { count: catalogTotal.toLocaleString('en-US') })
      }
      extra={
        access.canWriteProduct ? (
          <Button type="primary" icon={<PlusOutlined />} onClick={handleOpenCreate}>
            {t('domesticProducts.createProduct', '新建商品')}
          </Button>
        ) : undefined
      }
    >
      <Card className="dp-card" styles={{ body: { padding: 0 } }}>
        <div className="dp-toolbar" data-testid="domestic-products-toolbar">
          <SearchInput
            value={searchText}
            placeholder={t('domesticProducts.searchBoxPlaceholder')}
            enterHint={t('domesticProducts.searchEnterKey')}
            onSubmit={(value) => applyFilters({ searchText: value })}
          />
          <Select
            allowClear
            showSearch
            prefix={t('domesticProducts.supplier', '供应商')}
            placeholder={t('common.all', '全部')}
            style={{ width: 200 }}
            value={supplierCode}
            options={supplierOptions}
            optionLabelProp="shortLabel"
            filterOption={filterSupplierOption}
            onChange={(value?: string) => applyFilters({ supplierCode: value || undefined })}
          />
          {/* 标签与分段控件包成一组，窄屏换行时不会把标签孤零零留在上一行 */}
          <span className="dp-filter-group">
            <span className="dp-sub">{t('domesticProducts.filterType')}</span>
            <Segmented<TypeSegment>
              value={typeToSegment(productType)}
              options={[
                { label: t('common.all', '全部'), value: 'all' },
                { label: t('domesticProducts.typeShortNormal'), value: 'normal' },
                { label: t('domesticProducts.typeShortSet'), value: 'set' },
                { label: t('domesticProducts.typeShortMulti'), value: 'multi' },
              ]}
              onChange={(value) => applyFilters({ productType: typeFromSegment(value) })}
            />
          </span>
          <span className="dp-filter-group">
            <span className="dp-sub">{t('domesticProducts.status', '状态')}</span>
            <Segmented<StatusSegment>
              value={statusToSegment(isActive)}
              options={[
                { label: t('common.all', '全部'), value: 'all' },
                { label: t('common.enable', '启用'), value: 'active' },
                { label: t('common.disable', '停用'), value: 'inactive' },
              ]}
              onChange={(value) => applyFilters({ isActive: statusFromSegment(value) })}
            />
          </span>
          <Tooltip title={t('common.refresh', '刷新')}>
            <Button icon={<ReloadOutlined />} aria-label={t('common.refresh', '刷新')} onClick={() => void loadData()} />
          </Tooltip>
          <span className="dp-toolbar-spacer" />
          {exporting ? (
            <span className="dp-export-progress" role="status">
              {t('domesticProducts.exportProgress', { message: exportMessage, progress: exportProgress })}
            </span>
          ) : null}
          <ToolbarMenuButton
            label={t('common.export', '导出')}
            icon={<DownloadOutlined />}
            loading={exporting}
            actions={[
              {
                key: 'filtered',
                label: t('domesticProducts.exportMenuFiltered'),
                disabled: exporting,
                onClick: () => void handleExport('filtered', false),
              },
              {
                key: 'filteredRrp',
                label: t('domesticProducts.exportMenuFilteredRrp'),
                disabled: exporting,
                onClick: () => void handleExport('filtered', true),
              },
            ]}
          />
        </div>

        {activeFilterItems.length > 0 || selectedRowKeys.length > 0 ? (
          <div className="dp-bars" data-testid="domestic-products-bars">
            {activeFilterItems.length > 0 ? (
              <ActiveFilterBar
                items={activeFilterItems}
                onClearAll={() =>
                  applyFilters({ searchText: '', supplierCode: undefined, productType: undefined, isActive: undefined })
                }
              />
            ) : null}
            {/* 勾选后才出现的批量操作条：导出选中、批量删除不再混在筛选行里。 */}
            <SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={() => setSelectedRowKeys([])}>
              <Dropdown
                trigger={['click']}
                menu={{
                  items: selectedExportMenuItems,
                  onClick: ({ key }) => void handleExport('selected', key === 'rrp'),
                }}
              >
                {/* 不用 Button 的 loading：loading 时 antd 会吞掉点击，菜单就打不开了；只把图标换成转圈。 */}
                <Button size="small" icon={exporting ? <LoadingOutlined /> : <DownloadOutlined />} aria-busy={exporting || undefined}>
                  {t('domesticProducts.exportMenuSelected')}
                  <DownOutlined />
                </Button>
              </Dropdown>
              {access.canDeleteProduct ? (
                <Popconfirm
                  title={t('domesticProducts.confirmBatchDelete', '确认批量删除选中的商品吗？')}
                  description={t('domesticProducts.selectedRecordsWarning', '已选择 {{count}} 条记录，删除后不可恢复。', {
                    count: selectedRowKeys.length,
                  })}
                  okText={t('common.delete', '删除')}
                  cancelText={t('common.cancel', '取消')}
                  okButtonProps={{ danger: true, loading: deleting }}
                  onConfirm={() => handleBatchDelete()}
                >
                  <Button size="small" danger icon={<DeleteOutlined />} loading={deleting}>
                    {t('domesticProducts.batchDelete', '批量删除')}
                  </Button>
                </Popconfirm>
              ) : null}
            </SelectionActionBar>
          </div>
        ) : null}

        <MeasuredTable
          metricId="domestic-purchase.domestic-products.table-2"
          className="dp-table"
          rowKey="id"
          loading={loading}
          columns={columns}
          dataSource={data}
          tableLayout="fixed"
          rowSelection={{
            columnWidth: LIST_COLUMN_WIDTHS.selection,
            selectedRowKeys,
            onChange: setSelectedRowKeys,
          }}
          // 不再写死高度 620 / 虚拟滚动：整页滚动。scroll.x 是弹性商品列的最小宽度推算出的表格最小宽度，
          // 1280 视口可用宽度约 1000，所以这里必须 ≤ 1000，才不会出现横向滚动条。
          scroll={{ x: LIST_TABLE_MIN_WIDTH }}
          rowClassName={() => 'dp-row-clickable'}
          onRow={(record) => ({
            // 整行可点开详情；行内按钮、勾选框、下拉菜单、缩略图预览等自带交互的元素不触发。
            onClick: (event) => {
              if (shouldIgnoreRowClick(event.target as Element, event.currentTarget)) {
                return
              }
              handleOpenDetail(record)
            },
          })}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            pageSizeOptions: PAGE_SIZE_OPTIONS,
            showTotal: (value) => t('common.total', { count: value }),
          }}
          onChange={handleTableChange}
          data-testid="domestic-products-table"
        />
      </Card>

      <ProductDetailDrawer
        open={detailOpen}
        product={detailRecord}
        canWrite={access.canWriteProduct}
        setItemsVersion={setItemsVersion}
        onClose={() => setDetailOpen(false)}
        onEdit={handleOpenEdit}
        onEditSetItems={handleOpenSetItems}
      />

      <ProductFormModal
        open={formOpen}
        product={editingItem}
        suppliers={suppliers}
        onClose={() => setFormOpen(false)}
        onSaved={handleProductSaved}
      />

      <SetItemsModal
        open={setItemsOpen}
        product={setItemsProduct}
        canEdit={access.canWriteProduct}
        onClose={() => setSetItemsOpen(false)}
        onSaved={handleSetItemsSaved}
      />
    </PageContainer>
  )
}
