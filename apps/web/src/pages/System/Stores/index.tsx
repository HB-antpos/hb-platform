import {
  CloudSyncOutlined,
  EditOutlined,
  EllipsisOutlined,
  LoadingOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
  TeamOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Drawer,
  Dropdown,
  Form,
  Input,
  Modal,
  Segmented,
  Select,
  Space,
  Switch,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { FilterValue, SorterResult } from 'antd/es/table/interface'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { HasPermission, usePermission } from '../../../components/Access'
import PageContainer from '../../../components/PageContainer'
import ActiveFilterBar from '../../../components/listToolbar/ActiveFilterBar'
import type { ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import { P } from '../../../types/permissions'
import { batchUpdateStores, createStore, getNextStoreCode, getStoreByGuid, getStores, syncStoreToHq, updateStore } from '../../../services/storeService'
import type { CreateStoreDto, StoreDetailDto, StoreDto, UpdateStoreDto } from '../../../types/store'
import { RequestError } from '../../../utils/request'
import StoreFormFields from './StoreFormFields'
import StoreUserManagement from './StoreUserManagement'
import {
  BatchUpdateRequestError,
  buildBatchUpdateStoresRequest,
  shouldClearStoreSelection,
  type BatchUpdateStoreFormValues,
} from './batchUpdateLogic'
import {
  KNOWN_STORE_BRANDS,
  cashRegisterFilterFromValue,
  cashRegisterFilterToValue,
  formatTimestampText,
  mergeBrandNames,
  type CashRegisterFilterValue,
} from './storeListLogic'
import {
  UNSET_STORE_TIME_ZONE_FILTER,
  formatStoreTimeZoneId,
  formatStoreTimeZoneShort,
  storeTimeZoneOptions,
} from './timeZoneOptions'
import {
  DEFAULT_SYSTEM_LIST_PAGE_SIZE,
  createLatestRequestGuard,
  resolveSystemListPagination,
  runLatestGuardedRequest,
} from '../listPagination'
import { MeasuredTable } from '../../../components/MeasuredTable'
import './stores.css'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import storesMessagesEn from './storesMessages.en.json'
import storesMessagesZh from './storesMessages.zh.json'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: storesMessagesZh, en: storesMessagesEn })

const brandTagPalette = [
  { background: '#e6f4ff', borderColor: '#91caff', color: '#0958d9' },
  { background: '#f6ffed', borderColor: '#b7eb8f', color: '#389e0d' },
  { background: '#fff7e6', borderColor: '#ffd591', color: '#d46b08' },
  { background: '#f9f0ff', borderColor: '#d3adf7', color: '#722ed1' },
  { background: '#e6fffb', borderColor: '#87e8de', color: '#08979c' },
  { background: '#fff1f0', borderColor: '#ffa39e', color: '#cf1322' },
]

const brandTagStyleByName: Record<string, (typeof brandTagPalette)[number]> = {
  'hot bargain': brandTagPalette[0],
  'discount general': brandTagPalette[1],
  'dollar king': brandTagPalette[5],
}

function getBrandTagStyle(brandName: string) {
  // 常见品牌固定配色，避免列表里不同品牌因为 hash 碰撞显示成同色。
  const normalizedName = brandName.trim().toLowerCase()
  const knownStyle = brandTagStyleByName[normalizedName]
  if (knownStyle) {
    return knownStyle
  }

  // 未知品牌仍按名称稳定取色，分页和刷新后不会跳色。
  const hash = Array.from(normalizedName).reduce((sum, char) => (sum * 31 + char.charCodeAt(0)) >>> 0, 0)
  return brandTagPalette[hash % brandTagPalette.length]
}

function renderBrandName(value?: string) {
  const brandName = value?.trim()
  if (!brandName) {
    return <Typography.Text type="secondary">--</Typography.Text>
  }

  return (
    <Tag bordered={false} style={{ ...getBrandTagStyle(brandName), marginInlineEnd: 0 }}>
      {brandName}
    </Tag>
  )
}

type StoreSortOrder = 'ascend' | 'descend' | null

/** 列表查询条件。overrides 里显式写 undefined 表示「清除该筛选」，所以用对象合并而不是默认参数。 */
interface StoreListQuery {
  page: number
  pageSize: number
  brand?: string
  isActive?: boolean
  timeZone?: string
  sortBy?: string
  sortOrder: StoreSortOrder
}

type StoreFilterPatch = Partial<Pick<StoreListQuery, 'brand' | 'isActive' | 'timeZone'>>

function getApiErrorCode(error: unknown) {
  if (!(error instanceof RequestError)) {
    return undefined
  }

  const payload = error.payload
  return typeof payload === 'object' && payload !== null && 'errorCode' in payload
    ? String(payload.errorCode)
    : undefined
}

export default function SystemStoresPage() {
  const { t } = useTranslation()
  const canEditStores = usePermission(P.Stores.Edit)
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [data, setData] = useState<StoreDto[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_SYSTEM_LIST_PAGE_SIZE)
  const [total, setTotal] = useState(0)
  const mainListRequestGuardRef = useRef(createLatestRequestGuard())
  const [brandFilter, setBrandFilter] = useState<string | undefined>()
  const [isActiveFilter, setIsActiveFilter] = useState<boolean | undefined>()
  const [timeZoneFilter, setTimeZoneFilter] = useState<string | undefined>()
  const [sortBy, setSortBy] = useState<string | undefined>()
  const [sortOrder, setSortOrder] = useState<StoreSortOrder>(null)
  const [knownBrands, setKnownBrands] = useState<string[]>(() => [...KNOWN_STORE_BRANDS])
  const [overview, setOverview] = useState<{ total: number; enabled: number } | null>(null)
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailStore, setDetailStore] = useState<StoreDetailDto | null>(null)
  const [editOpen, setEditOpen] = useState(false)
  const [editLoading, setEditLoading] = useState(false)
  const [editingStore, setEditingStore] = useState<StoreDetailDto | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [createSaving, setCreateSaving] = useState(false)
  const [selectedStoreGuids, setSelectedStoreGuids] = useState<string[]>([])
  const [batchEditOpen, setBatchEditOpen] = useState(false)
  const [batchEditSaving, setBatchEditSaving] = useState(false)
  const [storeCodeLoading, setStoreCodeLoading] = useState(false)
  const [syncingStoreGuids, setSyncingStoreGuids] = useState<Set<string>>(() => new Set())
  const [storeUserOpen, setStoreUserOpen] = useState(false)
  const [storeUserTarget, setStoreUserTarget] = useState<StoreDto | null>(null)
  const [form] = Form.useForm<UpdateStoreDto>()
  const [createForm] = Form.useForm<CreateStoreDto>()
  const [batchEditForm] = Form.useForm<BatchUpdateStoreFormValues>()
  const applyTimeZoneId = Form.useWatch('applyTimeZoneId', batchEditForm)
  const applyAbn = Form.useWatch('applyAbn', batchEditForm)
  const applyBrandName = Form.useWatch('applyBrandName', batchEditForm)
  const applyIsActive = Form.useWatch('applyIsActive', batchEditForm)
  const batchIsActive = Form.useWatch('isActive', batchEditForm)
  const applyReturnPolicy = Form.useWatch('applyReturnPolicy', batchEditForm)

  const loadData = async (overrides: Partial<StoreListQuery> = {}) => {
    const query: StoreListQuery = {
      page,
      pageSize,
      brand: brandFilter,
      isActive: isActiveFilter,
      timeZone: timeZoneFilter,
      sortBy,
      sortOrder,
      ...overrides,
    }

    await runLatestGuardedRequest(mainListRequestGuardRef.current, () => getStores({
        page: query.page,
        pageSize: query.pageSize,
        search: keyword || undefined,
        brandName: query.brand || undefined,
        isActive: query.isActive,
        timeZoneId: query.timeZone,
        sortField: query.sortBy,
        sortOrder: query.sortOrder === 'ascend' ? 'asc' : query.sortOrder === 'descend' ? 'desc' : undefined,
      }), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
        setBrandFilter(query.brand)
        setIsActiveFilter(query.isActive)
        setTimeZoneFilter(query.timeZone)
        setSortBy(query.sortBy)
        setSortOrder(query.sortOrder ?? null)
        setKnownBrands((previous) => mergeBrandNames(previous, result.items))
      },
      onError: (error) => {
        console.error(error)
        message.error(t('system.stores.loadListFailed'))
      },
      // 旧请求结束时不能关闭较新请求的 loading。
      onSettled: () => setLoading(false),
    })
  }

  // 页头「共 N 家 · 已启用收银 M」：与当前筛选无关的全局概览，用两个 pageSize=1 的轻量请求取 total。
  const loadOverview = async () => {
    try {
      const [all, enabled] = await Promise.all([
        getStores({ page: 1, pageSize: 1 }),
        getStores({ page: 1, pageSize: 1, isActive: true }),
      ])
      setOverview({ total: all.total, enabled: enabled.total })
    } catch (error) {
      // 概览只是辅助信息，失败时保持页面可用，不打扰用户。
      console.error(error)
    }
  }

  useEffect(() => {
    void loadData({ page: 1, pageSize })
    void loadOverview()
    return () => {
      mainListRequestGuardRef.current.invalidate()
    }
  }, [])

  const clearStoreSelection = () => setSelectedStoreGuids([])

  const handleQuery = () => {
    if (shouldClearStoreSelection('query')) {
      clearStoreSelection()
    }
    void loadData({ page: 1, pageSize })
  }

  // 工具栏筛选：改变数据集，回到第一页并清空（可能已被筛掉的）隐藏选择。
  const applyFilters = (patch: StoreFilterPatch) => {
    if (shouldClearStoreSelection('filter')) {
      clearStoreSelection()
    }
    void loadData({ page: 1, pageSize, ...patch })
  }

  const reloadStoreDetail = async (storeGuid: string) => {
    const detail = await getStoreByGuid(storeGuid)
    setDetailStore(detail)
    return detail
  }

  const loadNextStoreCode = async () => {
    setStoreCodeLoading(true)
    try {
      const nextCode = await getNextStoreCode()
      createForm.setFieldValue('storeCode', nextCode)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('system.stores.loadNextStoreCodeFailed'))
    } finally {
      setStoreCodeLoading(false)
    }
  }

  const handleOpenCreate = () => {
    createForm.resetFields()
    // 新建分店默认不启用收银系统，避免刚录入资料就进入 POS 可用范围。
    createForm.setFieldsValue({ isActive: false })
    setCreateOpen(true)
    void loadNextStoreCode()
  }

  const handleCreateSubmit = async () => {
    try {
      const values = await createForm.validateFields()
      setCreateSaving(true)
      const created = await createStore({ ...values, isActive: values.isActive ?? false })
      message.success(t('system.stores.createSuccess'))
      setCreateOpen(false)
      createForm.resetFields()
      setDetailStore(created)
      setDetailOpen(true)
      void loadData({ page: 1, pageSize })
      void loadOverview()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }

      console.error(error)
      message.error(
        getApiErrorCode(error) === 'DUPLICATE_STORE_CODE'
          ? t('system.stores.duplicateStoreCode')
          : error instanceof Error ? error.message : t('system.stores.createFailed'),
      )
    } finally {
      setCreateSaving(false)
    }
  }

  const handleViewDetail = async (record: StoreDto) => {
    setDetailOpen(true)
    setDetailLoading(true)
    setDetailStore(null)
    try {
      const detail = await getStoreByGuid(record.storeGUID)
      setDetailStore(detail)
    } catch (error) {
      console.error(error)
      message.error(t('system.stores.loadDetailFailed'))
      setDetailOpen(false)
    } finally {
      setDetailLoading(false)
    }
  }

  const handleEdit = async (record: StoreDto) => {
    setEditOpen(true)
    setEditLoading(true)
    setEditingStore(null)
    form.resetFields()
    try {
      const detail = await getStoreByGuid(record.storeGUID)
      setEditingStore(detail)
      form.setFieldsValue({
        storeName: detail.storeName,
        storeCode: detail.storeCode,
        description: detail.description,
        address: detail.address,
        contactPhone: detail.contactPhone,
        contactEmail: detail.contactEmail,
        abn: detail.abn,
        brandName: detail.brandName,
        timeZoneId: detail.timeZoneId,
        returnPolicy: detail.returnPolicy,
        isActive: detail.isActive,
      })
    } catch (error) {
      console.error(error)
      message.error(t('system.stores.loadEditFailed'))
      setEditOpen(false)
    } finally {
      setEditLoading(false)
    }
  }

  const handleEditSubmit = async () => {
    if (!editingStore) {
      return
    }

    try {
      const values = await form.validateFields()
      setEditLoading(true)
      const updated = await updateStore(editingStore.storeGUID, values)
      message.success(t('system.stores.updateSuccess'))
      setEditOpen(false)
      setEditingStore(updated)
      form.resetFields()
      if (detailStore?.storeGUID === updated.storeGUID) {
        setDetailStore((current) => (current ? { ...current, ...updated } : updated))
      }
      void loadData()
      void loadOverview()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }
      console.error(error)
      message.error(t('system.stores.updateFailed'))
    } finally {
      setEditLoading(false)
    }
  }

  const handleOpenBatchEdit = () => {
    if (selectedStoreGuids.length === 0) {
      return
    }

    batchEditForm.resetFields()
    batchEditForm.setFieldsValue({
      applyTimeZoneId: false,
      applyAbn: false,
      applyBrandName: false,
      applyIsActive: false,
      isActive: false,
      applyReturnPolicy: false,
    })
    setBatchEditOpen(true)
  }

  const handleCloseBatchEdit = () => {
    if (batchEditSaving) {
      return
    }
    setBatchEditOpen(false)
    batchEditForm.resetFields()
  }

  const handleBatchEditSubmit = async () => {
    try {
      const values = await batchEditForm.validateFields()
      const payload = buildBatchUpdateStoresRequest(selectedStoreGuids, values)
      setBatchEditSaving(true)
      const result = await batchUpdateStores(payload)

      message.success(t('system.stores.batchUpdateSuccess', { count: result.updatedCount }))
      setBatchEditOpen(false)
      batchEditForm.resetFields()
      clearStoreSelection()
      await loadData()
      void loadOverview()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }

      if (error instanceof BatchUpdateRequestError) {
        const messageKey = {
          INVALID_TARGETS: 'system.stores.batchSelectionLimit',
          NO_FIELDS_SELECTED: 'system.stores.batchSelectFieldRequired',
          TIME_ZONE_REQUIRED: 'system.stores.batchTimeZoneRequired',
          IS_ACTIVE_REQUIRED: 'system.stores.batchCashRegisterRequired',
        }[error.code]
        message.warning(t(messageKey ?? 'system.stores.batchUpdateFailed'))
        return
      }

      console.error(error)
      message.error(error instanceof Error ? error.message : t('system.stores.batchUpdateFailed'))
    } finally {
      setBatchEditSaving(false)
    }
  }

  const handleOpenStoreUsers = (store: StoreDto) => {
    setStoreUserTarget(store)
    setStoreUserOpen(true)
  }

  const handleSyncStoreToHq = async (store: StoreDto) => {
    setSyncingStoreGuids((previous) => {
      const next = new Set(previous)
      next.add(store.storeGUID)
      return next
    })
    try {
      // 手动同步当前选中的分店，避免HQ短暂不可用影响普通创建/编辑保存。
      await syncStoreToHq(store.storeGUID)
      message.success(t('system.stores.syncHqSuccess'))
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('system.stores.syncHqFailed'))
    } finally {
      setSyncingStoreGuids((previous) => {
        const next = new Set(previous)
        next.delete(store.storeGUID)
        return next
      })
    }
  }

  const brandFilterOptions = useMemo(() => {
    const brands = new Set(knownBrands)
    if (brandFilter) {
      brands.add(brandFilter)
    }

    return Array.from(brands)
      .sort((a, b) => a.localeCompare(b))
      .map((brandName) => ({ label: brandName, value: brandName }))
  }, [brandFilter, knownBrands])

  // 下拉里展示全称便于区分，选中后输入框里用短名，避免挤掉前缀文字。
  const timeZoneFilterOptions = useMemo(() => [
    ...storeTimeZoneOptions.map((option) => ({
      label: option.label,
      short: formatStoreTimeZoneShort(option.value),
      value: option.value,
    })),
    { label: t('system.stores.timeZoneUnset'), short: t('system.stores.timeZoneUnset'), value: UNSET_STORE_TIME_ZONE_FILTER },
  ], [t])

  // 不用 useMemo：onRemove 会走到 loadData，它读取 keyword / 排序等状态；缓存会让回调拿到旧值。
  const activeFilterItems = (() => {
    const items: ActiveFilterItem[] = []
    if (brandFilter) {
      items.push({
        key: 'brand',
        label: t('system.stores.brandName'),
        value: brandFilter,
        source: 'toolbar',
        onRemove: () => applyFilters({ brand: undefined }),
      })
    }
    if (timeZoneFilter) {
      items.push({
        key: 'timeZone',
        label: t('system.stores.timeZone'),
        value: timeZoneFilter === UNSET_STORE_TIME_ZONE_FILTER ? t('system.stores.timeZoneUnset') : formatStoreTimeZoneShort(timeZoneFilter),
        source: 'toolbar',
        onRemove: () => applyFilters({ timeZone: undefined }),
      })
    }
    if (isActiveFilter !== undefined) {
      items.push({
        key: 'isActive',
        label: t('system.stores.cashRegisterShort'),
        value: isActiveFilter ? t('system.stores.cashEnabled') : t('system.stores.cashDisabled'),
        source: 'toolbar',
        onRemove: () => applyFilters({ isActive: undefined }),
      })
    }
    return items
  })()

  // 表头只保留排序；筛选都在工具栏里，生效条件有统一的标签条展示。
  const handleTableChange = (
    pagination: TablePaginationConfig,
    _filters: Record<string, FilterValue | null>,
    sorter: SorterResult<StoreDto> | SorterResult<StoreDto>[],
    extra: { action: 'paginate' | 'sort' | 'filter' },
  ) => {
    const currentSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const rawField = currentSorter?.field || currentSorter?.column?.dataIndex
    const field = Array.isArray(rawField) ? rawField.join('.') : rawField ? String(rawField) : undefined
    const order = currentSorter?.order as StoreSortOrder | undefined
    const nextSortBy = field && order ? field : undefined
    const nextSortOrder = field && order ? order : null

    // 表格排序都走服务端查询，避免分页后只在当前页内处理数据。
    const nextPagination = resolveSystemListPagination(extra.action, pagination, pageSize)
    void loadData({
      page: nextPagination.page,
      pageSize: nextPagination.pageSize,
      sortBy: nextSortBy,
      sortOrder: nextSortOrder,
    })
  }

  const columns: ColumnsType<StoreDto> = [
    {
      title: t('system.stores.storeName'),
      dataIndex: 'storeName',
      width: 156,
      fixed: 'left',
      sorter: true,
      sortOrder: sortBy === 'storeName' ? sortOrder : null,
      render: (value: string) => <span className="sys-store-name">{value}</span>,
    },
    {
      title: t('system.stores.codeColumn'),
      dataIndex: 'storeCode',
      width: 72,
      fixed: 'left',
      sorter: true,
      sortOrder: sortBy === 'storeCode' ? sortOrder : null,
      render: (value: string) => <span className="sys-store-mono">{value}</span>,
    },
    {
      // 品牌与 ABN 同属「主体信息」，合并成两行；ABN 不再占独立列，列表照样能核对商业号码。
      title: t('system.stores.brandAbn'),
      dataIndex: 'brandName',
      width: 152,
      sorter: true,
      sortOrder: sortBy === 'brandName' ? sortOrder : null,
      render: (value: string | undefined, record) => (
        !value?.trim() && !record.abn ? (
          <span className="sys-store-faint">--</span>
        ) : (
          <div className="sys-store-two">
            {renderBrandName(value)}
            <span className={record.abn ? 'sys-store-sub sys-store-mono' : 'sys-store-sub sys-store-faint'}>{record.abn || '--'}</span>
          </div>
        )
      ),
    },
    {
      title: t('system.stores.timeZone'),
      dataIndex: 'timeZoneId',
      width: 132,
      render: (value?: string) => (
        value ? (
          <Tooltip title={formatStoreTimeZoneId(value)}>
            <span style={{ whiteSpace: 'nowrap' }}>{formatStoreTimeZoneShort(value)}</span>
          </Tooltip>
        ) : (
          <span className="sys-store-faint">{t('system.stores.timeZoneUnset')}</span>
        )
      ),
    },
    {
      title: t('system.stores.contactPhone'),
      dataIndex: 'contactPhone',
      width: 120,
      sorter: true,
      sortOrder: sortBy === 'contactPhone' ? sortOrder : null,
      render: (value?: string) => (value ? <span className="sys-store-mono sys-store-ellipsis" title={value}>{value}</span> : <span className="sys-store-faint">--</span>),
    },
    {
      title: t('system.stores.address'),
      dataIndex: 'address',
      sorter: true,
      sortOrder: sortBy === 'address' ? sortOrder : null,
      // 地址列按业务要求完整展示；不设固定宽度，吃掉其它列之外的剩余空间并允许换行。
      render: (value?: string) => (value ? <span className="sys-store-address">{value}</span> : <span className="sys-store-faint">--</span>),
    },
    {
      title: t('system.stores.usersColumn'),
      dataIndex: 'totalUsers',
      width: 76,
      sorter: true,
      sortOrder: sortBy === 'totalUsers' ? sortOrder : null,
      render: (value: number | undefined, record) => (
        <Tooltip title={t('system.stores.detailUsers')}>
          <Button type="link" size="small" icon={<TeamOutlined />} style={{ paddingInline: 0 }} onClick={() => handleOpenStoreUsers(record)}>
            {record.activeUsers ?? 0} / {value ?? 0}
          </Button>
        </Tooltip>
      ),
    },
    {
      title: t('system.stores.cashRegisterColumn'),
      dataIndex: 'isActive',
      width: 84,
      sorter: true,
      sortOrder: sortBy === 'isActive' ? sortOrder : null,
      render: (value: boolean) => (
        <span className={value ? 'sys-store-status sys-store-status-on' : 'sys-store-status'}>
          {value ? t('system.stores.cashEnabled') : t('system.stores.cashDisabled')}
        </span>
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 124,
      fixed: 'right',
      align: 'right',
      render: (_, record) => (
        <div className="sys-store-actions">
          <Button size="small" type="link" onClick={() => void handleViewDetail(record)}>
            {t('system.stores.viewDetail')}
          </Button>
          <HasPermission code={P.Stores.Edit}>
            <Button size="small" type="link" onClick={() => void handleEdit(record)}>
              {t('common.edit')}
            </Button>
          </HasPermission>
          {/* 低频操作收进「更多」：同步到 HQ 需要编辑权限；管理用户与点击用户数入口等价，所有人可见。 */}
          <Dropdown
            trigger={['click']}
            menu={{
              items: [
                ...(canEditStores
                  ? [{
                      key: 'sync',
                      label: t('system.stores.syncHq'),
                      icon: syncingStoreGuids.has(record.storeGUID) ? <LoadingOutlined /> : <CloudSyncOutlined />,
                      disabled: syncingStoreGuids.has(record.storeGUID),
                    }]
                  : []),
                { key: 'users', label: t('system.stores.manageUsers'), icon: <TeamOutlined /> },
              ],
              onClick: ({ key }) => {
                if (key === 'sync') {
                  void handleSyncStoreToHq(record)
                } else if (key === 'users') {
                  handleOpenStoreUsers(record)
                }
              },
            }}
          >
            <Button size="small" type="link" icon={<EllipsisOutlined />} aria-label={t('system.stores.moreActions')} />
          </Dropdown>
        </div>
      ),
    },
  ]

  return (
    <PageContainer
      compact
      title={t('system.stores.pageTitle')}
      subtitle={overview ? t('system.stores.totalSummary', { total: overview.total, enabled: overview.enabled }) : undefined}
      extra={(
        <HasPermission code={P.Stores.Create}>
          <Button type="primary" icon={<PlusOutlined />} onClick={handleOpenCreate}>
            {t('system.stores.createStore')}
          </Button>
        </HasPermission>
      )}
    >
      <Card>
        <div className="sys-store-toolbar">
          <Input
            placeholder={`${t('system.stores.searchPlaceholder')} · ${t('common.listToolbar.searchEnterHint', '回车查询')}`}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
            onPressEnter={handleQuery}
            prefix={<SearchOutlined />}
            style={{ width: 300 }}
            allowClear
          />
          <Select
            allowClear
            prefix={t('system.stores.brandName')}
            placeholder={t('system.stores.filterAll')}
            style={{ width: 210 }}
            value={brandFilter}
            options={brandFilterOptions}
            onChange={(value?: string) => applyFilters({ brand: value || undefined })}
          />
          <Select
            allowClear
            prefix={t('system.stores.timeZone')}
            placeholder={t('system.stores.filterAll')}
            style={{ width: 210 }}
            optionLabelProp="short"
            value={timeZoneFilter}
            options={timeZoneFilterOptions}
            onChange={(value?: string) => applyFilters({ timeZone: value || undefined })}
          />
          {/* 标签与分段控件包成一组，窄屏换行时不会把「收银」孤零零留在上一行 */}
          <span className="sys-store-filter-group">
            <span className="sys-store-sub">{t('system.stores.cashRegisterShort')}</span>
            <Segmented<CashRegisterFilterValue>
              value={cashRegisterFilterToValue(isActiveFilter)}
              options={[
                { label: t('system.stores.filterAll'), value: 'all' },
                { label: t('system.stores.cashEnabled'), value: 'enabled' },
                { label: t('system.stores.cashDisabled'), value: 'disabled' },
              ]}
              onChange={(value) => applyFilters({ isActive: cashRegisterFilterFromValue(value) })}
            />
          </span>
          <Tooltip title={t('common.refresh')}>
            <Button icon={<ReloadOutlined />} aria-label={t('common.refresh')} onClick={() => void loadData()} />
          </Tooltip>
        </div>

        {activeFilterItems.length > 0 || selectedStoreGuids.length > 0 ? (
          <div className="sys-store-bars">
            {activeFilterItems.length > 0 ? (
              <ActiveFilterBar items={activeFilterItems} onClearAll={() => applyFilters({ brand: undefined, isActive: undefined, timeZone: undefined })} />
            ) : null}
            {/* 勾选后才出现的批量操作条：原先常驻的灰色「批量修改」和「已选 0 家分店」不再占位。 */}
            <SelectionActionBar selectedCount={selectedStoreGuids.length} onClearSelection={clearStoreSelection}>
              <HasPermission code={P.Stores.Edit}>
                <Button size="small" icon={<EditOutlined />} onClick={handleOpenBatchEdit}>
                  {t('system.stores.batchEdit')}
                </Button>
              </HasPermission>
            </SelectionActionBar>
          </div>
        ) : null}

        <MeasuredTable metricId="system.stores.table-1"
          className="sys-store-table"
          rowKey="storeGUID"
          rowSelection={canEditStores ? {
            selectedRowKeys: selectedStoreGuids,
            preserveSelectedRowKeys: true,
            fixed: true,
            columnWidth: 44,
            onChange: (selectedRowKeys) => {
              const nextStoreGuids = selectedRowKeys.map(String)
              if (nextStoreGuids.length > 100) {
                message.warning(t('system.stores.batchSelectionLimit'))
                return
              }
              setSelectedStoreGuids(nextStoreGuids)
            },
          } : undefined}
          loading={loading}
          columns={columns}
          dataSource={data}
          tableLayout="fixed"
          scroll={{ x: 1160 }}
          onChange={handleTableChange}
          rowClassName={() => 'sys-store-row-clickable'}
          onRow={(record) => ({
            // 整行可点开详情；行内按钮、勾选框、下拉菜单等自带交互的元素不触发，避免重复打开。
            onClick: (event) => {
              if ((event.target as HTMLElement).closest('button, a, .ant-checkbox-wrapper, .ant-table-selection-column, .ant-dropdown')) {
                return
              }
              void handleViewDetail(record)
            },
          })}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
          }}
        />
      </Card>

      <Modal
        title={t('system.stores.batchEditTitle')}
        open={batchEditOpen}
        onCancel={handleCloseBatchEdit}
        onOk={() => void handleBatchEditSubmit()}
        okText={t('system.stores.batchConfirm', { count: selectedStoreGuids.length })}
        confirmLoading={batchEditSaving}
        cancelButtonProps={{ disabled: batchEditSaving }}
        closable={!batchEditSaving}
        keyboard={!batchEditSaving}
        maskClosable={!batchEditSaving}
        width={680}
        destroyOnHidden
      >
        <Form
          form={batchEditForm}
          layout="vertical"
          initialValues={{
            applyTimeZoneId: false,
            applyAbn: false,
            applyBrandName: false,
            applyIsActive: false,
            isActive: false,
            applyReturnPolicy: false,
          }}
          autoComplete="off"
        >
          <Alert
            type="info"
            showIcon
            message={t('system.stores.batchEditSummary', { count: selectedStoreGuids.length })}
            description={(
              <Space direction="vertical" size={2}>
                <span>{t('system.stores.batchEditHint')}</span>
                <Typography.Text type="secondary">
                  {t('system.stores.batchTextClearHint')}
                </Typography.Text>
              </Space>
            )}
            style={{ marginBottom: 16 }}
          />

          <div style={{ border: '1px solid #f0f0f0', borderRadius: 6, paddingInline: 16 }}>
            <div style={{ display: 'grid', gridTemplateColumns: '180px minmax(0, 1fr)', gap: 16, alignItems: 'center', paddingBlock: 12, borderBottom: '1px solid #f5f5f5' }}>
              <Form.Item name="applyTimeZoneId" valuePropName="checked" noStyle>
                <Checkbox autoFocus>
                  {t('system.stores.batchModifyField', { field: t('system.stores.timeZone') })}
                </Checkbox>
              </Form.Item>
              <Form.Item
                name="timeZoneId"
                rules={applyTimeZoneId ? [{ required: true, message: t('system.stores.batchTimeZoneRequired') }] : []}
                style={{ marginBottom: 0 }}
              >
                <Select
                  disabled={!applyTimeZoneId}
                  options={storeTimeZoneOptions}
                  placeholder={t('system.stores.timeZoneRequired')}
                />
              </Form.Item>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '180px minmax(0, 1fr)', gap: 16, alignItems: 'center', paddingBlock: 12, borderBottom: '1px solid #f5f5f5' }}>
              <Form.Item name="applyAbn" valuePropName="checked" noStyle>
                <Checkbox>
                  {t('system.stores.batchModifyField', { field: t('system.stores.abn') })}
                </Checkbox>
              </Form.Item>
              <Form.Item
                name="abn"
                rules={applyAbn ? [{ max: 20, message: t('system.stores.abnMaxLength') }] : []}
                style={{ marginBottom: 0 }}
              >
                <Input disabled={!applyAbn} allowClear />
              </Form.Item>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '180px minmax(0, 1fr)', gap: 16, alignItems: 'center', paddingBlock: 12, borderBottom: '1px solid #f5f5f5' }}>
              <Form.Item name="applyBrandName" valuePropName="checked" noStyle>
                <Checkbox>
                  {t('system.stores.batchModifyField', { field: t('system.stores.brandName') })}
                </Checkbox>
              </Form.Item>
              <Form.Item
                name="brandName"
                rules={applyBrandName ? [{ max: 100, message: t('system.stores.brandNameMaxLength') }] : []}
                style={{ marginBottom: 0 }}
              >
                <Input disabled={!applyBrandName} allowClear />
              </Form.Item>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '180px minmax(0, 1fr)', gap: 16, alignItems: 'center', paddingBlock: 12, borderBottom: '1px solid #f5f5f5' }}>
              <Form.Item name="applyIsActive" valuePropName="checked" noStyle>
                <Checkbox>
                  {t('system.stores.batchModifyField', { field: t('system.stores.cashRegisterEnabled') })}
                </Checkbox>
              </Form.Item>
              <Form.Item name="isActive" valuePropName="checked" style={{ marginBottom: 0 }}>
                <Switch
                  disabled={!applyIsActive}
                  checkedChildren={t('common.active')}
                  unCheckedChildren={t('common.inactive')}
                />
              </Form.Item>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '180px minmax(0, 1fr)', gap: 16, alignItems: 'start', paddingBlock: 12 }}>
              <Form.Item name="applyReturnPolicy" valuePropName="checked" noStyle>
                <Checkbox>
                  {t('system.stores.batchModifyField', { field: t('system.stores.returnPolicy') })}
                </Checkbox>
              </Form.Item>
              <Form.Item
                name="returnPolicy"
                rules={applyReturnPolicy ? [{ max: 500, message: t('system.stores.returnPolicyMaxLength') }] : []}
                style={{ marginBottom: 0 }}
              >
                <Input.TextArea disabled={!applyReturnPolicy} rows={3} allowClear />
              </Form.Item>
            </div>
          </div>

          {applyIsActive && batchIsActive === false ? (
            <Alert
              type="warning"
              showIcon
              message={t('system.stores.batchCashRegisterDisableWarning')}
              style={{ marginTop: 16 }}
            />
          ) : null}
        </Form>
      </Modal>

      <Drawer
        rootClassName="sys-store-drawer"
        title={
          detailStore ? (
            <div className="sys-store-drawer-head">
              <span className="sys-store-drawer-title">{detailStore.storeName}</span>
              <div className="sys-store-drawer-meta">
                <Tag bordered={false} className="sys-store-mono" style={{ marginInlineEnd: 0 }}>{detailStore.storeCode}</Tag>
                {renderBrandName(detailStore.brandName)}
                <span className={detailStore.isActive ? 'sys-store-status sys-store-status-on' : 'sys-store-status'}>
                  {detailStore.isActive ? t('system.stores.cashRegisterOn') : t('system.stores.cashRegisterOff')}
                </span>
              </div>
            </div>
          ) : t('system.stores.detailTitleShort')
        }
        width={660}
        open={detailOpen}
        onClose={() => {
          setDetailOpen(false)
          setDetailStore(null)
        }}
        destroyOnHidden
        closable={{ placement: 'end' }}
        footer={
          detailStore ? (
            <div className="sys-store-drawer-footer">
              <HasPermission code={P.Stores.Edit}>
                <Button
                  icon={<CloudSyncOutlined />}
                  loading={syncingStoreGuids.has(detailStore.storeGUID)}
                  onClick={() => void handleSyncStoreToHq(detailStore)}
                >
                  {t('system.stores.syncHq')}
                </Button>
                <Button icon={<TeamOutlined />} onClick={() => handleOpenStoreUsers(detailStore)}>
                  {t('system.stores.manageUsers')}
                </Button>
              </HasPermission>
              <span className="sys-store-drawer-footer-spacer" />
              <Button onClick={() => { setDetailOpen(false); setDetailStore(null) }}>{t('common.close')}</Button>
              <HasPermission code={P.Stores.Edit}>
                <Button type="primary" icon={<EditOutlined />} onClick={() => void handleEdit(detailStore)}>
                  {t('system.stores.editTitleShort')}
                </Button>
              </HasPermission>
            </div>
          ) : null
        }
      >
        {detailLoading ? (
          <Typography.Text type="secondary">{t('system.stores.loadingDetail')}</Typography.Text>
        ) : !detailStore ? (
          <Typography.Text type="danger">{t('system.stores.notFound')}</Typography.Text>
        ) : (
          <>
            <div className="sys-store-stats">
              <div>
                <div className="sys-store-stat-key">{t('system.stores.detailUsers')}</div>
                <div className="sys-store-stat-value">
                  <Button type="link" style={{ paddingInline: 0, fontSize: 18, fontWeight: 650, height: 'auto' }} onClick={() => handleOpenStoreUsers(detailStore)}>
                    {detailStore.activeUsers ?? 0}
                  </Button>
                  <small> / {detailStore.totalUsers ?? 0} {t('system.stores.peopleUnit')}</small>
                </div>
              </div>
              <div>
                <div className="sys-store-stat-key">{t('system.stores.timeZone')}</div>
                <div className="sys-store-stat-value sys-store-stat-value-sm">
                  <Tooltip title={formatStoreTimeZoneId(detailStore.timeZoneId)}>
                    <span>{formatStoreTimeZoneShort(detailStore.timeZoneId)}</span>
                  </Tooltip>
                </div>
              </div>
              <div>
                <div className="sys-store-stat-key">{t('system.users.updatedAt')}</div>
                <div className="sys-store-stat-value sys-store-stat-value-sm">{formatTimestampText(detailStore.updatedAt)}</div>
              </div>
            </div>

            <section className="sys-store-detail-section">
              <h4 className="sys-store-section-title">{t('system.stores.detailBasic')}</h4>
              <dl className="sys-store-dl">
                <dt>{t('system.stores.abn')}</dt>
                <dd className="sys-store-mono">{detailStore.abn || '--'}</dd>
                <dt>{t('system.stores.contactPhone')}</dt>
                <dd>{detailStore.contactPhone || '--'}</dd>
                <dt>{t('system.stores.contactEmail')}</dt>
                <dd>{detailStore.contactEmail || '--'}</dd>
                <dt>{t('system.stores.address')}</dt>
                <dd>{detailStore.address || '--'}</dd>
                <dt>{t('column.description')}</dt>
                <dd>{detailStore.description || '--'}</dd>
                <dt>{t('column.createTime')}</dt>
                <dd>{formatTimestampText(detailStore.createdAt)}</dd>
              </dl>
            </section>

            <section className="sys-store-detail-section">
              <h4 className="sys-store-section-title">{t('system.stores.returnPolicy')}</h4>
              <div className="sys-store-panel" style={{ whiteSpace: 'pre-wrap' }}>{detailStore.returnPolicy || '--'}</div>
            </section>
          </>
        )}
      </Drawer>

      <Modal
        title={t('system.stores.createTitle')}
        open={createOpen}
        onCancel={() => {
          setCreateOpen(false)
          createForm.resetFields()
        }}
        onOk={() => void handleCreateSubmit()}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={createSaving}
        width={780}
        styles={{ body: { maxHeight: '70vh', overflowY: 'auto', paddingInline: 4 } }}
        destroyOnHidden
      >
        <Form form={createForm} layout="vertical" initialValues={{ isActive: false }} autoComplete="off">
          {/* 新建与编辑共用同一套表单分区与前端校验。 */}
          <StoreFormFields onRegenerateStoreCode={() => void loadNextStoreCode()} storeCodeLoading={storeCodeLoading} />
        </Form>
      </Modal>

      <Modal
        title={editingStore ? t('system.stores.editTitle', { name: editingStore.storeCode }) : t('system.stores.editTitleShort')}
        open={editOpen}
        onCancel={() => {
          setEditOpen(false)
          setEditingStore(null)
          form.resetFields()
        }}
        onOk={() => void handleEditSubmit()}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={editLoading}
        width={780}
        styles={{ body: { maxHeight: '70vh', overflowY: 'auto', paddingInline: 4 } }}
        destroyOnHidden
      >
        <Form form={form} layout="vertical" autoComplete="off">
          <StoreFormFields />
        </Form>
      </Modal>

      <StoreUserManagement
        open={storeUserOpen}
        store={storeUserTarget}
        onClose={() => {
          setStoreUserOpen(false)
          setStoreUserTarget(null)
        }}
        onChanged={() => {
          if (storeUserTarget) {
            if (detailStore?.storeGUID === storeUserTarget.storeGUID) {
              void reloadStoreDetail(storeUserTarget.storeGUID)
            }
            void loadData()
          }
        }}
      />
    </PageContainer>
  )
}
