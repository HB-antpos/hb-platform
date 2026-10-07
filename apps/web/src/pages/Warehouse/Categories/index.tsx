import {
  DeleteOutlined,
  EditOutlined,
  PictureOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Empty,
  Form,
  Image,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Spin,
  Switch,
  Tooltip,
  Tree,
  TreeSelect,
  message,
} from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { DefaultOptionType } from 'antd/es/select'
import type { DataNode } from 'antd/es/tree'
import type { TFunction } from 'i18next'
import type { Key } from 'react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getSupplierOptions } from '../../../services/domesticProductService'
import {
  batchAssignProducts,
  createWarehouseCategory,
  deleteWarehouseCategory,
  getCategoryTree,
  type SaveWarehouseCategoryPayload,
  type WarehouseCategoryNode,
  updateWarehouseCategory,
} from '../../../services/warehouseCategoryService'
import {
  getWarehouseProductsTable,
  type WarehouseProductListItem,
  type WarehouseProductsTableQuery,
} from '../../../services/warehouseProductService'
import type { SupplierOption } from '../../../types/domesticProduct'
import {
  ALL_PRODUCTS_FILTER_KEY,
  UNCATEGORIZED_PRODUCTS_FILTER_KEY,
  buildCategoryOptions,
  buildFilterCategoryTreeOptions,
  resolveCategoryProductFilterMode,
} from './categoryProductFilters'
import {
  collectAncestorGuids,
  countCategoryTree,
  filterCategoryTree,
  findCategoryPath,
  formatCategoryPathTail,
  resolveProductCategoryPath,
} from './categoryTreeView'
import { MeasuredTable } from '../../../components/MeasuredTable'
import categoriesMessagesEn from './categoriesMessages.en.json'
import categoriesMessagesZh from './categoriesMessages.zh.json'
import './categories.css'

registerPageMessages({ zh: categoriesMessagesZh, en: categoriesMessagesEn })

type FormMode = 'idle' | 'create' | 'edit'

interface WarehouseCategoryFormValues extends SaveWarehouseCategoryPayload {
  isActive: boolean
}

interface ProductQuery {
  /** 左侧选中项：全部商品 / 未分类商品哨兵值，或具体分类 GUID */
  key: string
  keyword: string
  supplierCode?: string
  page: number
  pageSize: number
}

const DEFAULT_TREE_EXPAND_LEVEL = 2
const PRODUCT_SEARCH_DEBOUNCE_MS = 300

function collectExpandedKeysToLevel(nodes: WarehouseCategoryNode[], maxLevel: number, level = 1): string[] {
  if (level > maxLevel) {
    return []
  }

  return nodes.flatMap((node) => [
    node.categoryGUID,
    ...collectExpandedKeysToLevel(node.children || [], maxLevel, level + 1),
  ])
}

function findCategory(nodes: WarehouseCategoryNode[], targetGuid?: string): WarehouseCategoryNode | undefined {
  if (!targetGuid) {
    return undefined
  }

  for (const node of nodes) {
    if (node.categoryGUID === targetGuid) {
      return node
    }

    const matched = findCategory(node.children || [], targetGuid)
    if (matched) {
      return matched
    }
  }

  return undefined
}

function collectDescendantKeys(node?: WarehouseCategoryNode): string[] {
  if (!node) {
    return []
  }

  return (node.children || []).flatMap((child) => [child.categoryGUID, ...collectDescendantKeys(child)])
}

function isQuickFilterKey(key: string) {
  return key === ALL_PRODUCTS_FILTER_KEY || key === UNCATEGORIZED_PRODUCTS_FILTER_KEY
}

function buildTreeData(nodes: WarehouseCategoryNode[], t: TFunction, level = 0): DataNode[] {
  return nodes.map((node) => ({
    key: node.categoryGUID,
    title: (
      <span
        className={[
          'wh-categories-node',
          level === 0 ? 'wh-categories-node-root' : '',
          node.isActive ? '' : 'wh-categories-node-inactive',
        ].filter(Boolean).join(' ')}
      >
        <span className="wh-categories-node-name">{node.categoryName}</span>
        {node.chineseName ? <span className="wh-categories-node-cn">{node.chineseName}</span> : null}
        {/* 只标出停用分类：启用是常态，不再给每个节点挂绿色标签 */}
        {node.isActive ? null : <span className="wh-categories-node-off">{t('common.inactive')}</span>}
      </span>
    ),
    children: buildTreeData(node.children || [], t, level + 1),
  }))
}

type SupplierSelectOption = DefaultOptionType & {
  searchText?: string
}

function buildSupplierOptions(suppliers: SupplierOption[]): SupplierSelectOption[] {
  return suppliers.map((item) => ({
    value: item.code,
    label: `${item.code} - ${item.name}`,
    searchText: `${item.code} ${item.name} ${item.shopNumber ?? ''}`.toLowerCase(),
  }))
}

function filterSupplierOption(input: string, option?: DefaultOptionType) {
  return String((option as SupplierSelectOption | undefined)?.searchText ?? '')
    .includes(input.trim().toLowerCase())
}

export default function WarehouseCategoriesPage() {
  const { t, i18n } = useTranslation()
  const [form] = Form.useForm<WarehouseCategoryFormValues>()
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [productLoading, setProductLoading] = useState(false)
  const [assigning, setAssigning] = useState(false)
  const [categories, setCategories] = useState<WarehouseCategoryNode[]>([])
  const [suppliers, setSuppliers] = useState<SupplierOption[]>([])
  const [supplierLoading, setSupplierLoading] = useState(false)
  const [expandedKeys, setExpandedKeys] = useState<string[]>([])
  const [treeKeyword, setTreeKeyword] = useState('')
  const [searchExpandedKeys, setSearchExpandedKeys] = useState<string[]>([])
  // 左侧选中项同时决定右侧信息卡和商品列表范围；默认「全部商品」并自动加载，不再出现空表等查询。
  const [selectedKey, setSelectedKey] = useState<string>(ALL_PRODUCTS_FILTER_KEY)
  const [formMode, setFormMode] = useState<FormMode>('idle')
  const [modalOpen, setModalOpen] = useState(false)
  const [productKeywordInput, setProductKeywordInput] = useState('')
  const [productKeyword, setProductKeyword] = useState('')
  const [productSupplierCode, setProductSupplierCode] = useState<string>()
  const [productPage, setProductPage] = useState(1)
  const [productPageSize, setProductPageSize] = useState(20)
  const [productTotal, setProductTotal] = useState(0)
  const [products, setProducts] = useState<WarehouseProductListItem[]>([])
  const [selectedProductCodes, setSelectedProductCodes] = useState<Key[]>([])
  // 批量移动的目标分类与左侧筛选完全分离：查看 A 分类时也能把商品移到任意分类。
  const [moveTargetGuid, setMoveTargetGuid] = useState<string>()
  const productRequestSeqRef = useRef(0)
  const treeLoadedRef = useRef(false)

  const selectedCategory = useMemo(
    () => findCategory(categories, selectedKey),
    [categories, selectedKey],
  )
  const selectedCategoryGuid = selectedCategory?.categoryGUID
  const selectedCategoryPath = useMemo(
    () => findCategoryPath(categories, selectedCategoryGuid),
    [categories, selectedCategoryGuid],
  )
  const treeStats = useMemo(() => countCategoryTree(categories), [categories])
  const treeView = useMemo(() => filterCategoryTree(categories, treeKeyword), [categories, treeKeyword])
  const isTreeSearching = treeKeyword.trim().length > 0
  const treeData = useMemo(() => buildTreeData(treeView.nodes, t), [treeView.nodes, t])

  useEffect(() => {
    // 搜索词变化时展开命中项的全部祖先；搜索期间用户手动收起/展开不影响原展开状态。
    setSearchExpandedKeys(treeView.expandedKeys)
  }, [treeView])

  const disallowedParentKeys = useMemo(() => {
    if (!selectedCategory || formMode !== 'edit') {
      return new Set<string>()
    }

    return new Set([selectedCategory.categoryGUID, ...collectDescendantKeys(selectedCategory)])
  }, [formMode, selectedCategory])

  const parentOptions = useMemo(
    () => buildCategoryOptions(categories).filter((item) => !disallowedParentKeys.has(String(item.value))),
    [categories, disallowedParentKeys],
  )
  const moveTargetOptions = useMemo(
    () =>
      buildFilterCategoryTreeOptions(categories, t, i18n.language).filter(
        (option) => !isQuickFilterKey(option.value),
      ),
    [categories, i18n.language, t],
  )
  const supplierOptions = useMemo(() => buildSupplierOptions(suppliers), [suppliers])

  const loadProducts = async (query: ProductQuery) => {
    const requestSeq = productRequestSeqRef.current + 1
    productRequestSeqRef.current = requestSeq
    const filterMode = resolveCategoryProductFilterMode(query.key)

    setProductLoading(true)
    try {
      // 三种范围统一走仓库商品表格接口：关键词同时匹配货号和商品名称，分类查询含子分类，返回 total。
      const result = await getWarehouseProductsTable({
        page: query.page,
        pageSize: query.pageSize,
        searchText: query.keyword || undefined,
        supplierCode: query.supplierCode || undefined,
        categoryFilter: filterMode.type === 'category' ? undefined : filterMode.type,
        categoryGuid: filterMode.type === 'category' ? filterMode.categoryGuid : undefined,
      } satisfies WarehouseProductsTableQuery)

      // 切换分类或改筛选后，先发出的请求晚到时不能覆盖当前列表。
      if (requestSeq !== productRequestSeqRef.current) {
        return
      }
      setProducts(result.items)
      setProductTotal(result.total)
      setProductPage(result.page)
      setProductPageSize(result.pageSize)
    } catch (error) {
      if (requestSeq !== productRequestSeqRef.current) {
        return
      }
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.categories.loadProductsFailed'))
    } finally {
      if (requestSeq === productRequestSeqRef.current) {
        setProductLoading(false)
      }
    }
  }

  const loadSuppliers = async () => {
    setSupplierLoading(true)
    try {
      setSuppliers(await getSupplierOptions())
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('productCreation.loadSupplierListFailed'))
    } finally {
      setSupplierLoading(false)
    }
  }

  /**
   * 重新加载分类树并定位选中项。
   * nextSelectedKey 不传时保持当前选中；传入的分类已不存在（如刚被删除）时回到「全部商品」。
   */
  const loadTree = async (nextSelectedKey?: string) => {
    setLoading(true)
    try {
      const tree = await getCategoryTree()
      setCategories(tree)

      const isFirstLoad = !treeLoadedRef.current
      treeLoadedRef.current = true
      const targetKey = nextSelectedKey ?? selectedKey
      const targetCategory = findCategory(tree, targetKey)
      const ancestorKeys = targetCategory ? collectAncestorGuids(tree, targetCategory.categoryGUID) : []
      // 首次加载默认展开到第 2 级；之后保留用户的展开状态，只补上新选中节点的祖先。
      setExpandedKeys((current) =>
        Array.from(new Set([
          ...(isFirstLoad ? collectExpandedKeysToLevel(tree, DEFAULT_TREE_EXPAND_LEVEL) : current),
          ...ancestorKeys,
        ])),
      )

      if (targetCategory) {
        setSelectedKey(targetCategory.categoryGUID)
        return
      }

      if (isQuickFilterKey(targetKey)) {
        setSelectedKey(targetKey)
        return
      }

      setSelectedKey(ALL_PRODUCTS_FILTER_KEY)
      setFormMode('idle')
      setModalOpen(false)
      form.resetFields()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.categories.loadTreeFailed'))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadTree()
    void loadSuppliers()
  }, [])

  useEffect(() => {
    // 关键词防抖：停止输入约 300ms 后才查询，避免每个字符都请求一次。
    const timer = window.setTimeout(() => setProductKeyword(productKeywordInput.trim()), PRODUCT_SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [productKeywordInput])

  useEffect(() => {
    // 选中节点、关键词、供应商任一变化即查第 1 页；勾选随新查询清空（与原「查询」按钮一致），翻页仍保留勾选。
    setSelectedProductCodes([])
    void loadProducts({
      key: selectedKey,
      keyword: productKeyword,
      supplierCode: productSupplierCode,
      page: 1,
      pageSize: productPageSize,
    })
  }, [selectedKey, productKeyword, productSupplierCode])

  const handleCreateRoot = () => {
    setFormMode('create')
    setModalOpen(true)
    form.setFieldsValue({
      categoryName: '',
      chineseName: '',
      parentGUID: undefined,
      isActive: true,
      remarks: '',
    })
  }

  const handleCreateChild = () => {
    if (!selectedCategory) {
      message.warning(t('warehouse.categories.selectParentFirst'))
      return
    }

    setFormMode('create')
    setModalOpen(true)
    form.setFieldsValue({
      categoryName: '',
      chineseName: '',
      parentGUID: selectedCategory.categoryGUID,
      isActive: true,
      remarks: '',
    })
  }

  const handleEditCategory = () => {
    if (!selectedCategory) {
      message.warning(t('warehouse.categories.selectEditFirst'))
      return
    }

    setFormMode('edit')
    setModalOpen(true)
    form.setFieldsValue({
      categoryName: selectedCategory.categoryName,
      chineseName: selectedCategory.chineseName,
      parentGUID: selectedCategory.parentGUID,
      isActive: selectedCategory.isActive,
      remarks: selectedCategory.remarks,
    })
  }

  const handleCloseModal = () => {
    setModalOpen(false)
    if (selectedCategory) {
      setFormMode('edit')
      form.setFieldsValue({
        categoryName: selectedCategory.categoryName,
        chineseName: selectedCategory.chineseName,
        parentGUID: selectedCategory.parentGUID,
        isActive: selectedCategory.isActive,
        remarks: selectedCategory.remarks,
      })
      return
    }

    setFormMode('idle')
    form.resetFields()
  }

  const handleSave = async () => {
    try {
      const values = await form.validateFields()
      setSaving(true)

      if (formMode === 'create') {
        const created = await createWarehouseCategory(values)
        message.success(t('warehouse.categories.createSuccess'))
        setModalOpen(false)
        await loadTree(created.categoryGUID)
        return
      }

      if (!selectedCategoryGuid) {
        message.warning(t('warehouse.categories.selectEditFirst'))
        return
      }

      const updated = await updateWarehouseCategory(selectedCategoryGuid, values)
      message.success(t('warehouse.categories.updateSuccess'))
      setModalOpen(false)
      await loadTree(updated.categoryGUID)
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }

      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.categories.saveFailed'))
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async () => {
    if (!selectedCategory) {
      return
    }

    try {
      setSaving(true)
      const parentGuid = selectedCategory.parentGUID
      await deleteWarehouseCategory(selectedCategory.categoryGUID)
      message.success(t('warehouse.categories.deleteSuccess'))
      // 删除后选中父分类；顶级分类被删时回到「全部商品」。
      await loadTree(parentGuid || ALL_PRODUCTS_FILTER_KEY)
    } catch (error) {
      // 有关联商品等情况由后端拒绝，原样提示后端返回的原因。
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.categories.deleteFailed'))
    } finally {
      setSaving(false)
    }
  }

  const currentProductQuery = (page = productPage, pageSize = productPageSize): ProductQuery => ({
    key: selectedKey,
    keyword: productKeyword,
    supplierCode: productSupplierCode,
    page,
    pageSize,
  })

  const handleProductTableChange = (pagination: TablePaginationConfig) => {
    void loadProducts(currentProductQuery(pagination.current ?? 1, pagination.pageSize ?? productPageSize))
  }

  const handleBatchAssign = async () => {
    const targetCategoryGuid = moveTargetGuid

    if (!targetCategoryGuid) {
      message.warning(t('warehouse.categories.selectTargetFirst'))
      return
    }

    if (!selectedProductCodes.length) {
      message.warning(t('warehouse.categories.selectProductsFirst'))
      return
    }

    try {
      setAssigning(true)
      await batchAssignProducts(targetCategoryGuid, selectedProductCodes.map(String))
      const targetCategory = findCategory(categories, targetCategoryGuid)
      message.success(t('warehouse.categories.batchUpdateSuccess', {
        count: selectedProductCodes.length,
        categoryName: targetCategory?.categoryName || t('warehouse.categories.targetCategory'),
      }))
      setSelectedProductCodes([])
      // 分类树不含商品数据，移动商品后只需刷新当前页商品。
      await loadProducts(currentProductQuery())
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.categories.batchUpdateFailed'))
    } finally {
      setAssigning(false)
    }
  }

  const renderProductCell = (record: WarehouseProductListItem) => (
    <div className="wh-categories-product">
      {record.productImage ? (
        <Image
          src={record.productImage}
          alt={record.name || record.itemNumber || record.productCode}
          width={36}
          height={36}
          loading="lazy"
          className="wh-categories-product-image"
          preview={{ mask: '' }}
        />
      ) : (
        <span className="wh-categories-product-placeholder" aria-hidden="true">
          <PictureOutlined />
        </span>
      )}
      <div className="wh-categories-product-text">
        <div className="wh-categories-product-code">{record.itemNumber || record.productCode}</div>
        <div className="wh-categories-product-name" title={record.name || undefined}>
          {record.name || '--'}
        </div>
      </div>
    </div>
  )

  const renderSupplierCell = (record: WarehouseProductListItem) => {
    const name = record.domesticSupplierName || record.localSupplierName
    const code = record.domesticSupplierCode || record.localSupplierCode
    if (!name && !code) {
      return '--'
    }
    return (
      <div className="wh-categories-two-line">
        <div className="wh-categories-two-line-main" title={name || undefined}>{name || code}</div>
        {name && code ? <div className="wh-categories-two-line-sub">{code}</div> : null}
      </div>
    )
  }

  const renderCategoryCell = (record: WarehouseProductListItem) => {
    const path = resolveProductCategoryPath(
      categories,
      { categoryGuid: record.warehouseCategoryGUID, categoryName: record.categoryName },
      selectedCategoryGuid,
    )
    if (!path.length) {
      return <span className="wh-categories-muted">{t('warehouseUi.categories.uncategorizedProducts')}</span>
    }
    return (
      <span className="wh-categories-path-cell" title={path.join(' › ')}>
        {formatCategoryPathTail(path)}
      </span>
    )
  }

  const productColumns: ColumnsType<WarehouseProductListItem> = [
    {
      title: t('warehouseUi.categories.columnProduct'),
      dataIndex: 'itemNumber',
      width: 280,
      render: (_value, record) => renderProductCell(record),
    },
    {
      title: t('warehouseUi.categories.columnSupplier'),
      key: 'domesticSupplier',
      width: 190,
      render: (_value, record) => renderSupplierCell(record),
    },
    {
      title: t('warehouseUi.categories.columnCategory'),
      key: 'currentCategory',
      width: 190,
      render: (_value, record) => renderCategoryCell(record),
    },
    {
      title: t('warehouseUi.categories.columnStatus'),
      dataIndex: 'isActive',
      width: 96,
      render: (value: boolean) => (
        <StatusPill tone={value ? 'green' : 'gray'}>{value ? t('common.active') : t('common.inactive')}</StatusPill>
      ),
    },
  ]

  const renderQuickItem = (key: string, label: string) => (
    <button
      type="button"
      className={`wh-categories-quick-item${selectedKey === key ? ' wh-categories-quick-item-active' : ''}`}
      aria-pressed={selectedKey === key}
      onClick={() => setSelectedKey(key)}
    >
      {label}
    </button>
  )

  const renderSelectedInfo = () => {
    if (!selectedCategory) {
      const isUncategorized = selectedKey === UNCATEGORIZED_PRODUCTS_FILTER_KEY
      return (
        <div className="wh-categories-info-body">
          <div className="wh-categories-info-title">
            <h2>
              {isUncategorized
                ? t('warehouseUi.categories.uncategorizedProducts')
                : t('warehouseUi.categories.allProducts')}
            </h2>
          </div>
          <div className="wh-categories-info-meta">
            {isUncategorized
              ? t('warehouseUi.categories.uncategorizedHint')
              : t('warehouseUi.categories.allProductsHint')}
          </div>
        </div>
      )
    }

    const parentNames = selectedCategoryPath.slice(0, -1).map((node) => node.categoryName)
    const childCount = selectedCategory.children?.length ?? 0

    return (
      <>
        <div className="wh-categories-info-body">
          <div className="wh-categories-info-path">
            {parentNames.length ? `${parentNames.join(' › ')} ›` : t('warehouseUi.categories.topLevel')}
          </div>
          <div className="wh-categories-info-title">
            <h2>{selectedCategory.categoryName}</h2>
            {selectedCategory.chineseName ? (
              <span className="wh-categories-info-cn">{selectedCategory.chineseName}</span>
            ) : null}
            <StatusPill tone={selectedCategory.isActive ? 'green' : 'gray'}>
              {selectedCategory.isActive ? t('common.active') : t('common.inactive')}
            </StatusPill>
          </div>
          <div className="wh-categories-info-meta">
            <span>
              {t('warehouseUi.categories.childCount')} <strong>{childCount}</strong>
            </span>
            {selectedCategory.remarks ? (
              <span>{t('warehouseUi.categories.remarks', { text: selectedCategory.remarks })}</span>
            ) : null}
          </div>
        </div>
        <div className="wh-categories-info-actions">
          <Button icon={<PlusOutlined />} onClick={handleCreateChild}>
            {t('warehouseUi.categories.addChild')}
          </Button>
          <Button icon={<EditOutlined />} onClick={handleEditCategory}>
            {t('warehouseUi.categories.edit')}
          </Button>
          {childCount > 0 ? (
            // 有子分类时前端直接禁用；商品数前端未知，交给后端拒绝并原样提示。
            <Tooltip title={t('warehouseUi.categories.deleteDisabledHasChildren')}>
              <Button danger icon={<DeleteOutlined />} disabled>
                {t('warehouseUi.categories.delete')}
              </Button>
            </Tooltip>
          ) : (
            <Popconfirm
              title={t('warehouse.categories.confirmDelete')}
              description={t('warehouse.categories.deleteBlockedHint')}
              onConfirm={() => void handleDelete()}
            >
              <Button danger icon={<DeleteOutlined />} loading={saving}>
                {t('warehouseUi.categories.delete')}
              </Button>
            </Popconfirm>
          )}
        </div>
      </>
    )
  }

  return (
    <PageContainer
      compact
      title={t('warehouse.categories.title')}
      subtitle={
        categories.length
          ? t('warehouseUi.categories.subtitle', { top: treeStats.topLevel, total: treeStats.total })
          : undefined
      }
      extra={
        <Button type="primary" icon={<PlusOutlined />} onClick={handleCreateRoot}>
          {t('warehouse.categories.addTopCategory')}
        </Button>
      }
    >
      <div className="wh-categories-layout">
        <aside className="wh-categories-panel wh-categories-tree-panel" aria-label={t('warehouseUi.categories.treeRegion')}>
          <div className="wh-categories-tree-search">
            <Input
              allowClear
              prefix={<SearchOutlined />}
              value={treeKeyword}
              onChange={(event) => setTreeKeyword(event.target.value)}
              placeholder={t('warehouseUi.categories.treeSearchPlaceholder')}
              aria-label={t('warehouseUi.categories.treeSearchPlaceholder')}
            />
            <Tooltip title={t('warehouseUi.categories.refreshTree')}>
              <Button
                icon={<ReloadOutlined />}
                aria-label={t('warehouseUi.categories.refreshTree')}
                loading={loading}
                onClick={() => void loadTree()}
              />
            </Tooltip>
          </div>
          <div className="wh-categories-quick-list">
            {renderQuickItem(ALL_PRODUCTS_FILTER_KEY, t('warehouseUi.categories.allProducts'))}
            {renderQuickItem(UNCATEGORIZED_PRODUCTS_FILTER_KEY, t('warehouseUi.categories.uncategorizedProducts'))}
          </div>
          <div className="wh-categories-divider" />
          <Spin spinning={loading}>
            <div className="wh-categories-tree-scroll">
              {treeData.length ? (
                <Tree
                  blockNode
                  className="wh-categories-tree"
                  selectedKeys={selectedCategoryGuid ? [selectedCategoryGuid] : []}
                  expandedKeys={isTreeSearching ? searchExpandedKeys : expandedKeys}
                  onExpand={(keys) =>
                    (isTreeSearching ? setSearchExpandedKeys : setExpandedKeys)(keys.map(String))
                  }
                  onSelect={(keys) => {
                    // 再次点击已选节点时 antd 会取消选中，这里保持当前选中不变。
                    if (typeof keys[0] === 'string') {
                      setSelectedKey(keys[0])
                    }
                  }}
                  treeData={treeData}
                />
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={
                    isTreeSearching && categories.length
                      ? t('warehouseUi.categories.noTreeMatch')
                      : t('warehouse.categories.noCategoryData')
                  }
                />
              )}
            </div>
          </Spin>
        </aside>

        <div className="wh-categories-main">
          <section
            className="wh-categories-panel wh-categories-info"
            aria-label={t('warehouseUi.categories.currentRegion')}
          >
            {renderSelectedInfo()}
          </section>

          <section
            className="wh-categories-panel wh-categories-products"
            aria-label={t('warehouseUi.categories.productsRegion')}
          >
            <div className="wh-categories-products-toolbar">
              <Input
                allowClear
                prefix={<SearchOutlined />}
                value={productKeywordInput}
                onChange={(event) => setProductKeywordInput(event.target.value)}
                placeholder={t('warehouseUi.categories.productSearchPlaceholder')}
                aria-label={t('warehouseUi.categories.productSearchPlaceholder')}
                className="wh-categories-products-search"
              />
              <Select
                allowClear
                showSearch
                value={productSupplierCode}
                onChange={(value?: string) => setProductSupplierCode(value || undefined)}
                loading={supplierLoading}
                options={supplierOptions}
                filterOption={filterSupplierOption}
                placeholder={t('warehouseUi.categories.supplierPlaceholder')}
                className="wh-categories-products-supplier"
                popupMatchSelectWidth={300}
              />
              <span className="wh-categories-spacer" />
              <span className="wh-categories-products-total">
                {t('warehouseUi.categories.totalCount', { count: productTotal })}
              </span>
              <Tooltip title={t('warehouseUi.categories.refreshProducts')}>
                <Button
                  icon={<ReloadOutlined />}
                  aria-label={t('warehouseUi.categories.refreshProducts')}
                  onClick={() => void loadProducts(currentProductQuery())}
                />
              </Tooltip>
            </div>

            <div className="wh-categories-selection">
              <SelectionActionBar
                selectedCount={selectedProductCodes.length}
                onClearSelection={() => setSelectedProductCodes([])}
              >
                <span>{t('warehouseUi.categories.moveTo')}</span>
                <TreeSelect
                  size="small"
                  showSearch
                  allowClear
                  value={moveTargetGuid}
                  onChange={(value?: string) => setMoveTargetGuid(value || undefined)}
                  treeData={moveTargetOptions}
                  treeNodeFilterProp="searchText"
                  placeholder={t('warehouseUi.categories.moveTargetPlaceholder')}
                  className="wh-categories-move-target"
                  popupMatchSelectWidth={320}
                  listHeight={360}
                />
                <Button
                  size="small"
                  type="primary"
                  disabled={!moveTargetGuid}
                  loading={assigning}
                  onClick={() => void handleBatchAssign()}
                >
                  {t('warehouseUi.categories.moveCount', { count: selectedProductCodes.length })}
                </Button>
              </SelectionActionBar>
            </div>

            <MeasuredTable<WarehouseProductListItem> metricId="warehouse.categories.table-1"
              className="wh-categories-table"
              rowKey="productCode"
              loading={productLoading}
              columns={productColumns}
              dataSource={products}
              rowSelection={{
                selectedRowKeys: selectedProductCodes,
                onChange: setSelectedProductCodes,
                preserveSelectedRowKeys: true,
              }}
              onChange={handleProductTableChange}
              scroll={{ x: 800 }}
              pagination={{
                current: productPage,
                pageSize: productPageSize,
                total: productTotal,
                showSizeChanger: true,
                showQuickJumper: true,
                showTotal: (count) => t('warehouseUi.categories.paginationTotal', { count }),
              }}
            />
          </section>
        </div>
      </div>
      <Modal
        title={formMode === 'create' ? t('warehouse.categories.newCategory') : t('warehouse.categories.editCategory')}
        open={modalOpen}
        confirmLoading={saving}
        onOk={() => void handleSave()}
        onCancel={handleCloseModal}
        destroyOnHidden
        width={720}
      >
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          {formMode === 'create' ? (
            <Alert
              type="info"
              showIcon
              message={
                selectedCategory && form.getFieldValue('parentGUID') === selectedCategory.categoryGUID
                  ? t('warehouse.categories.addingChildFor', { name: selectedCategory.categoryName })
                  : t('warehouse.categories.addingTopCategory')
              }
            />
          ) : null}

          <Form
            form={form}
            layout="vertical"
            initialValues={{ isActive: true }}
          >
            <Form.Item
              label={t('warehouse.categories.categoryName')}
              name="categoryName"
              rules={[
                { required: true, message: t('warehouse.categories.enterCategoryName') },
                { max: 100, message: t('warehouse.categories.categoryNameMax') },
              ]}
            >
              <Input maxLength={100} placeholder={t('warehouse.categories.enterCategoryName')} />
            </Form.Item>

            <Form.Item label={t('warehouse.categories.chineseName')} name="chineseName" rules={[{ max: 100, message: t('warehouse.categories.chineseNameMax') }]}>
              <Input maxLength={100} placeholder={t('warehouse.categories.enterChineseName')} />
            </Form.Item>

            <Form.Item label={t('warehouse.categories.parent')} name="parentGUID">
              <Select
                allowClear
                showSearch
                placeholder={t('warehouse.categories.topCategoryWhenEmpty')}
                options={parentOptions}
                optionFilterProp="label"
              />
            </Form.Item>

            <Form.Item label={t('warehouse.categories.isActive')} name="isActive" valuePropName="checked">
              <Switch checkedChildren={t('common.active')} unCheckedChildren={t('common.inactive')} />
            </Form.Item>

            <Form.Item label={t('warehouse.categories.remarks')} name="remarks" rules={[{ max: 500, message: t('warehouse.categories.remarksMax') }]}>
              <Input.TextArea rows={4} maxLength={500} showCount placeholder={t('common.enterRemarks')} />
            </Form.Item>
          </Form>
        </Space>
      </Modal>
    </PageContainer>
  )
}
