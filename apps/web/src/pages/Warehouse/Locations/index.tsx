import {
  BarcodeOutlined,
  CloseOutlined,
  DeleteOutlined,
  DisconnectOutlined,
  EditOutlined,
  MoreOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  DatePicker,
  Dropdown,
  Form,
  Image,
  Input,
  Modal,
  Popover,
  Progress,
  Segmented,
  Select,
  Space,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { TableProps } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { SorterResult, SortOrder } from 'antd/es/table/interface'
import dayjs, { type Dayjs } from 'dayjs'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import ActiveFilterBar, { type ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import MoreFiltersButton from '../../../components/listToolbar/MoreFiltersButton'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  batchUnbindLocationProducts,
  createLocation,
  deleteLocation,
  getLocationList,
  updateLocation,
} from '../../../services/locationService'
import { useAuthStore } from '../../../store/auth'
import BarcodePreview from '../../../components/BarcodePreview'
import type {
  CreateLocationParams,
  LocationItem,
  LocationProduct,
  UpdateLocationParams,
} from '../../../types/location'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { buildLocationFilterQuery, type TextFilterMode } from './columnFilters'
import {
  buildSelectedLocationProductBindings,
  coordinateBatchUnbindLocationProducts,
  hasUnbindableProducts,
} from './bulkUnbindSelection'
import {
  EMPTY_LOCATION_FILTERS,
  LOCATION_SEARCH_FIELDS,
  LOCATION_TYPE_PICKING,
  LOCATION_TYPE_STORAGE,
  OTHER_ZONE_KEY,
  buildLocationListColumnFilters,
  buildZoneRack,
  collectLocationDistribution,
  countMoreLocationFilters,
  getUsageRatePercent,
  summarizeLocationZones,
  type LocationDistributionResult,
  type LocationListFilters,
  type LocationSearchField,
  type RackCell,
} from './locationsLogic'
import { MeasuredTable } from '../../../components/MeasuredTable'
import './locations.css'
import locationsMessagesEn from './locationsMessages.en.json'
import locationsMessagesZh from './locationsMessages.zh.json'

registerPageMessages({ zh: locationsMessagesZh, en: locationsMessagesEn })

interface LocationFormValues {
  locationCode: string
  locationBarcode?: string
  locationType?: number
  status?: number
}

type LocationSortBy =
  | 'LocationCode'
  | 'LocationType'
  | 'Status'
  | 'Usage'
  | 'UpdatedAt'

const DEFAULT_LOCATION_SORT_BY: LocationSortBy = 'LocationCode'
const DEFAULT_LOCATION_SORT_ORDER: SortOrder = 'ascend'

// 服务端排序字段白名单：表格列 key → 接口 SortBy。「绑定商品」列按使用状态（Usage）排序。
const LOCATION_SORT_FIELD_MAP: Record<string, LocationSortBy> = {
  locationCode: 'LocationCode',
  locationType: 'LocationType',
  status: 'Status',
  products: 'Usage',
  updatedAt: 'UpdatedAt',
}

interface LocationListQuery {
  page: number
  pageSize: number
  sortBy: LocationSortBy
  sortOrder: SortOrder
  filters: LocationListFilters
}

/** 列表请求结果：superseded 表示被更新的请求取代，调用方不应再用本地补丁覆盖新数据。 */
type LocationListLoadOutcome =
  | { status: 'success'; items: LocationItem[] }
  | { status: 'failed' }
  | { status: 'superseded' }

interface DistributionState {
  status: 'idle' | 'loading' | 'ready' | 'error'
  loaded: number
  total?: number
  result?: LocationDistributionResult
  loadedAt?: Dayjs
  /** 新建/删除/解绑后统计可能过期，只提示不自动重拉（全量读取较重）。 */
  stale: boolean
}

const DISTRIBUTION_PREFERENCE_KEY = 'hbweb.warehouseLocations.distributionOpen'

// 只记住本机是否展开货位分布；读写失败（隐私模式等）按收起处理。
function readDistributionPreference() {
  try {
    return window.localStorage.getItem(DISTRIBUTION_PREFERENCE_KEY) === '1'
  } catch {
    return false
  }
}

function writeDistributionPreference(open: boolean) {
  try {
    window.localStorage.setItem(DISTRIBUTION_PREFERENCE_KEY, open ? '1' : '0')
  } catch {
    // 忽略：偏好只是便利功能
  }
}

function toApiSortDirection(order: SortOrder): 'asc' | 'desc' {
  return order === 'descend' ? 'desc' : 'asc'
}

function getSortField(sorter: SorterResult<LocationItem>) {
  const field = Array.isArray(sorter.field) ? sorter.field.join('.') : sorter.field
  const key = sorter.columnKey
  const candidate = key || field
  return candidate ? String(candidate) : undefined
}

function formatDateTime(value?: string) {
  if (!value) return '--'
  const date = dayjs(value)
  return date.isValid() ? date.format('YYYY-MM-DD HH:mm') : value
}

function formatCount(value: number) {
  return value.toLocaleString('en-US')
}

export default function WarehouseLocationsPage() {
  const { t } = useTranslation()
  const [form] = Form.useForm<LocationFormValues>()
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)
  const [editingItem, setEditingItem] = useState<LocationItem | null>(null)
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  const [batchUnbinding, setBatchUnbinding] = useState(false)
  const [unbindingKeys, setUnbindingKeys] = useState<string[]>([])
  const [data, setData] = useState<LocationItem[]>([])
  const [filters, setFilters] = useState<LocationListFilters>(EMPTY_LOCATION_FILTERS)
  const [keywordDraft, setKeywordDraft] = useState('')
  const [moreOpen, setMoreOpen] = useState(false)
  const [moreDraft, setMoreDraft] = useState<LocationListFilters>(EMPTY_LOCATION_FILTERS)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [sortBy, setSortBy] = useState<LocationSortBy>(DEFAULT_LOCATION_SORT_BY)
  const [sortOrder, setSortOrder] = useState<SortOrder>(DEFAULT_LOCATION_SORT_ORDER)
  const [summaryCounts, setSummaryCounts] = useState<{ total: number; used: number } | null>(null)
  const [summaryVersion, setSummaryVersion] = useState(0)
  const [distributionOpen, setDistributionOpen] = useState(readDistributionPreference)
  const [distribution, setDistribution] = useState<DistributionState>({ status: 'idle', loaded: 0, stale: false })
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const countsRequestGuardRef = useRef(createLatestRequestGuard())
  const distributionRequestGuardRef = useRef(createLatestRequestGuard())
  const keywordTimerRef = useRef<number | undefined>(undefined)
  const mountedRef = useRef(false)
  const latestApplyFiltersRef = useRef<(patch: Partial<LocationListFilters>) => void>(() => undefined)
  const { access } = useAuthStore()
  const canManageLocations = access.canManageWarehouseLocations
  const locationTypeOptions = [
    { value: LOCATION_TYPE_STORAGE, label: t('warehouseLocations.storageLocation') },
    { value: LOCATION_TYPE_PICKING, label: t('warehouseLocations.pickingLocation') },
  ]
  const statusOptions = [
    { value: 1, label: t('warehouseUi.locations.statusEnabled') },
    { value: 0, label: t('warehouseUi.locations.statusDisabled') },
  ]
  const searchFieldLabels: Record<LocationSearchField, string> = {
    locationCode: t('warehouseUi.locations.fieldLocationCode'),
    locationBarcode: t('warehouseUi.locations.fieldLocationBarcode'),
    productItemNumber: t('warehouseUi.locations.fieldItemNumber'),
    productBarcode: t('warehouseUi.locations.fieldProductBarcode'),
    productName: t('warehouseUi.locations.fieldProductName'),
  }
  const textFilterModeOptions: Array<{ label: string; value: TextFilterMode }> = [
    { label: t('warehouse.filterMode.contains', '包含'), value: 'contains' },
    { label: t('warehouse.filterMode.equals', '等于'), value: 'eq' },
    { label: t('warehouse.filterMode.startsWith', '开头是'), value: 'starts' },
    { label: t('warehouse.filterMode.endsWith', '结尾是'), value: 'ends' },
  ]

  const loadData = async (query: Partial<LocationListQuery> = {}): Promise<LocationListLoadOutcome> => {
    if (!mountedRef.current) return { status: 'superseded' }

    const next: LocationListQuery = { page, pageSize, sortBy, sortOrder, filters, ...query }
    const effectiveSortBy = next.sortBy || DEFAULT_LOCATION_SORT_BY
    const effectiveSortOrder = next.sortOrder || DEFAULT_LOCATION_SORT_ORDER
    const locationFilterQuery = buildLocationFilterQuery(buildLocationListColumnFilters(next.filters))
    let outcome: LocationListLoadOutcome = { status: 'superseded' }

    // 搜索防抖、分段切换、翻页可能并发，只允许最后一次请求写入列表、分页和 loading。
    await runLatestGuardedRequest(
      listRequestGuardRef.current,
      () =>
        getLocationList({
          isUsed: locationFilterQuery.isUsed,
          filters: locationFilterQuery.filters,
          pageNumber: next.page,
          pageSize: next.pageSize,
          sortBy: effectiveSortBy,
          sortDirection: toApiSortDirection(effectiveSortOrder),
        }),
      {
        onStart: () => setLoading(true),
        onSuccess: (result) => {
          setData(result.items)
          // 翻页、筛选或刷新后，只保留当前结果中仍可解绑的货位，避免跨页误操作。
          const selectableLocationGuids = new Set(
            result.items
              .filter(hasUnbindableProducts)
              .map((item) => item.locationGuid),
          )
          setSelectedRowKeys((currentKeys) =>
            currentKeys.filter((key) => selectableLocationGuids.has(String(key))),
          )
          setTotal(result.total)
          setPage(result.pageNumber)
          setPageSize(result.pageSize)
          setSortBy(effectiveSortBy)
          setSortOrder(effectiveSortOrder)
          outcome = { status: 'success', items: result.items }
        },
        onError: (error) => {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('warehouseUi.locations.loadFailed'))
          outcome = { status: 'failed' }
        },
        onSettled: () => setLoading(false),
      },
    )

    return outcome
  }

  const applyFilters = (patch: Partial<LocationListFilters>) => {
    const nextFilters = { ...filters, ...patch }
    setFilters(nextFilters)
    void loadData({ page: 1, filters: nextFilters })
  }

  useLayoutEffect(() => {
    mountedRef.current = true

    return () => {
      mountedRef.current = false
      listRequestGuardRef.current.invalidate()
      countsRequestGuardRef.current.invalidate()
      distributionRequestGuardRef.current.invalidate()
      window.clearTimeout(keywordTimerRef.current)
    }
  }, [])

  useLayoutEffect(() => {
    latestApplyFiltersRef.current = applyFilters
  })

  useEffect(() => {
    void loadData({ page: 1 })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 页头计数：总数与已使用数各发一次 pageSize=1 的请求，与列表筛选无关；失败时不显示副标题。
  useEffect(() => {
    void runLatestGuardedRequest(
      countsRequestGuardRef.current,
      async () => {
        const [all, used] = await Promise.all([
          getLocationList({ pageNumber: 1, pageSize: 1 }),
          getLocationList({ isUsed: true, pageNumber: 1, pageSize: 1 }),
        ])
        return { total: all.total, used: used.total }
      },
      {
        onSuccess: setSummaryCounts,
        onError: (error) => {
          console.error(error)
          setSummaryCounts(null)
        },
      },
    )
  }, [summaryVersion])

  // 货位分布需要读取全部货位（生产约 4,600 个、3,500 条商品关联），按需加载并分页顺序读取。
  const loadDistribution = () => {
    const requestId = distributionRequestGuardRef.current.begin()
    const isCancelled = () => !distributionRequestGuardRef.current.isLatest(requestId)
    setDistribution((current) => ({ ...current, status: 'loading', loaded: 0 }))

    void collectLocationDistribution(
      async (pageNumber, size) => {
        const result = await getLocationList({ pageNumber, pageSize: size, sortBy: 'LocationCode', sortDirection: 'asc' })
        return { items: result.items, total: result.total }
      },
      {
        isCancelled,
        onProgress: (loaded, totalCount) => {
          if (!isCancelled()) setDistribution((current) => ({ ...current, loaded, total: totalCount }))
        },
      },
    )
      .then((result) => {
        if (!result || isCancelled()) return
        setDistribution({
          status: 'ready',
          loaded: result.entries.length,
          total: result.total,
          result,
          loadedAt: dayjs(),
          stale: false,
        })
      })
      .catch((error) => {
        if (isCancelled()) return
        console.error(error)
        setDistribution((current) => ({ ...current, status: 'error' }))
      })
  }

  useEffect(() => {
    // 上次展开过分布的用户，再次进入页面时自动加载；之后的展开由按钮处理。
    if (distributionOpen) loadDistribution()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const toggleDistribution = (open: boolean) => {
    setDistributionOpen(open)
    writeDistributionPreference(open)
    if (open) {
      // 没有统计结果、或货位有变动后再展开时才重新读取；已有新鲜结果直接展示。
      if (distribution.status !== 'loading' && (!distribution.result || distribution.stale)) loadDistribution()
      return
    }
    if (distribution.status === 'loading') {
      // 收起时停止还没读完的分页，已有统计结果保留。
      distributionRequestGuardRef.current.invalidate()
      setDistribution((current) => ({ ...current, status: current.result ? 'ready' : 'idle' }))
    }
  }

  /** 新建、编辑、删除、解绑后：重新统计页头计数，并提示货位分布可能过期。 */
  const refreshSummaries = () => {
    setSummaryVersion((version) => version + 1)
    setDistribution((current) => (current.result ? { ...current, stale: true } : current))
  }

  const handleCreate = () => {
    setEditingItem(null)
    form.resetFields()
    form.setFieldsValue({
      locationType: 2,
      status: 1,
    })
    setModalOpen(true)
  }

  const handleEdit = (record: LocationItem) => {
    setEditingItem(record)
    form.setFieldsValue({
      locationCode: record.locationCode,
      locationBarcode: record.locationBarcode,
      locationType: record.locationType ?? 2,
      status: record.status ?? 1,
    })
    setModalOpen(true)
  }

  const handleCloseModal = () => {
    setModalOpen(false)
    setEditingItem(null)
    form.resetFields()
  }

  const handleSave = async () => {
    try {
      const values = await form.validateFields()
      setSaving(true)

      if (editingItem) {
        await updateLocation(editingItem.locationGuid, values as UpdateLocationParams)
        message.success(t('warehouseUi.locations.updateSuccess'))
      } else {
        await createLocation(values as CreateLocationParams)
        message.success(t('warehouseUi.locations.createSuccess'))
      }

      handleCloseModal()
      refreshSummaries()
      void loadData({ page: editingItem ? page : 1 })
    } catch (error) {
      if (error instanceof Error) {
        message.error(error.message || t('warehouseUi.locations.saveFailed'))
      }
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async (record: LocationItem) => {
    try {
      await deleteLocation(record.locationGuid)
      message.success(t('warehouseUi.locations.deleteSuccess'))
      refreshSummaries()
      void loadData()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouseUi.locations.deleteFailed'))
    }
  }

  const confirmDelete = (record: LocationItem) => {
    Modal.confirm({
      title: t('warehouseUi.locations.deleteTitle', { code: record.locationCode || record.locationGuid }),
      content: t('warehouseUi.locations.deleteContent'),
      okText: t('common.delete'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => handleDelete(record),
    })
  }

  // 单个商品解绑：复用批量解绑同一个 DELETE /locations/{guid}/products/{productCode} 接口，只传一条关联。
  const confirmUnbindProduct = (record: LocationItem, product: LocationProduct) => {
    const productCode = product.productCode?.trim()
    if (!productCode) return
    const bindingKey = JSON.stringify([record.locationGuid, productCode])
    const locationLabel = record.locationCode || record.locationGuid
    const productLabel = product.itemNumber || product.productName || productCode

    Modal.confirm({
      title: t('warehouseUi.locations.unbindTitle', { location: locationLabel, product: productLabel }),
      content: t('warehouseUi.locations.unbindContent'),
      okText: t('warehouseUi.locations.unbindOk'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        setUnbindingKeys((keys) => (keys.includes(bindingKey) ? keys : [...keys, bindingKey]))
        try {
          const result = await batchUnbindLocationProducts([{ locationGuid: record.locationGuid, productCode }])
          if (result.succeeded.length) {
            message.success(t('warehouseUi.locations.unbindSuccess', { location: locationLabel, product: productLabel }))
            refreshSummaries()
            void loadData()
          } else {
            message.error(t('warehouseUi.locations.unbindFailed', { message: result.failed[0]?.message ?? '' }))
          }
        } finally {
          setUnbindingKeys((keys) => keys.filter((key) => key !== bindingKey))
        }
      },
    })
  }

  const selectedLocationGuidSet = new Set(selectedRowKeys.map((key) => String(key)))
  const selectedLocations = data.filter((location) => selectedLocationGuidSet.has(location.locationGuid))
  const selectedBindings = buildSelectedLocationProductBindings(data, selectedRowKeys)

  const handleBatchUnbind = () => {
    // 固定本次确认时的关联快照，防止弹窗打开后列表状态变化导致请求内容漂移。
    const bindings = [...selectedBindings]
    const locationCount = selectedLocations.length

    Modal.confirm({
      title: t('warehouseLocations.batchUnbindTitle'),
      content: t('warehouseLocations.batchUnbindContent', {
        locations: locationCount,
        products: bindings.length,
      }),
      okText: t('warehouseLocations.batchUnbindConfirm'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        setBatchUnbinding(true)
        let refreshOutcome: LocationListLoadOutcome['status'] | undefined
        try {
          const {
            result,
            nextSelectedRowKeys,
            patchedData,
            shouldApplyPatchedData,
          } = await coordinateBatchUnbindLocationProducts({
            bindings,
            locations: data,
            unbind: batchUnbindLocationProducts,
            refresh: async () => {
              const outcome = await loadData()
              refreshOutcome = outcome.status
              return outcome.status === 'success' ? outcome.items : undefined
            },
          })
          const succeeded = result.succeeded.length
          const failed = result.failed.length

          if (failed === 0) {
            message.success(t('warehouseLocations.batchUnbindSuccess', { succeeded }))
          } else if (succeeded > 0) {
            message.warning(t('warehouseLocations.batchUnbindPartialFailed', { succeeded, failed }))
          } else {
            message.error(t('warehouseLocations.batchUnbindFailed', { failed }))
          }

          if (succeeded > 0) refreshSummaries()
          // 刷新被更新的请求取代时，新请求会带回服务端最新数据，不能再用旧快照的本地补丁覆盖它。
          if (shouldApplyPatchedData && patchedData && refreshOutcome !== 'superseded') {
            setData(patchedData)
          }
          setSelectedRowKeys(nextSelectedRowKeys)
        } catch (error) {
          console.error(error)
          message.error(t('warehouseLocations.batchUnbindFailed', { failed: bindings.length }))
        } finally {
          setBatchUnbinding(false)
        }
      },
    })
  }

  const handleTableChange: TableProps<LocationItem>['onChange'] = (pagination, _filters, sorter, extra) => {
    const currentSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const field = getSortField(currentSorter)
    const nextSortBy = (field && LOCATION_SORT_FIELD_MAP[field]) || DEFAULT_LOCATION_SORT_BY
    const nextSortOrder = currentSorter?.order || DEFAULT_LOCATION_SORT_ORDER

    if (extra.action === 'sort') {
      void loadData({
        page: 1,
        pageSize: pagination.pageSize || pageSize,
        sortBy: nextSortBy,
        sortOrder: nextSortOrder,
      })
      return
    }

    if (extra.action === 'paginate') {
      void loadData({ page: pagination.current || page, pageSize: pagination.pageSize || pageSize })
    }
  }

  // 关键字防抖：停顿 300ms 后生效；回车或清空立即生效。
  const handleKeywordChange = (value: string) => {
    setKeywordDraft(value)
    window.clearTimeout(keywordTimerRef.current)
    if (!value) {
      latestApplyFiltersRef.current({ keyword: '' })
      return
    }
    keywordTimerRef.current = window.setTimeout(() => latestApplyFiltersRef.current({ keyword: value.trim() }), 300)
  }

  const applyKeywordNow = () => {
    window.clearTimeout(keywordTimerRef.current)
    applyFilters({ keyword: keywordDraft.trim() })
  }

  const handleSearchFieldChange = (searchField: LocationSearchField) => {
    // 没有关键字时切换字段不影响结果，不发请求。
    if (filters.keyword) {
      applyFilters({ searchField })
    } else {
      setFilters((current) => ({ ...current, searchField }))
    }
  }

  const applyMoreFilters = (draft: LocationListFilters) => {
    setMoreOpen(false)
    applyFilters({
      searchMode: draft.searchMode,
      status: draft.status,
      updatedBy: draft.updatedBy?.trim() || undefined,
      updatedAtFrom: draft.updatedAtFrom,
      updatedAtTo: draft.updatedAtTo,
    })
  }

  const clearAllFilters = () => {
    window.clearTimeout(keywordTimerRef.current)
    setKeywordDraft('')
    // 清空全部也清掉区/排快捷筛选和更多筛选，但保留当前搜索字段的选择。
    applyFilters({ ...EMPTY_LOCATION_FILTERS, searchField: filters.searchField })
  }

  const selectZone = (zone: string) => {
    // 再点一次已选中的区（且没选排）取消区筛选；已选排时点区卡片回到整区。
    if (filters.zone === zone && !filters.zoneRow) {
      applyFilters({ zone: undefined, zoneRow: undefined })
    } else {
      applyFilters({ zone, zoneRow: undefined })
    }
  }

  const selectZoneRow = (zone: string, rowText: string) => {
    applyFilters({ zone, zoneRow: filters.zone === zone && filters.zoneRow === rowText ? undefined : rowText })
  }

  const formatRangeValue = (min?: string, max?: string) => {
    if (min && max) return t('warehouseUi.locations.rangeBetween', { min, max })
    if (min) return t('warehouseUi.locations.rangeAtLeast', { value: min })
    return t('warehouseUi.locations.rangeAtMost', { value: max })
  }

  const activeFilterItems: ActiveFilterItem[] = []
  if (filters.keyword) {
    const modeLabel = filters.searchMode === 'contains' ? '' : `${textFilterModeOptions.find((option) => option.value === filters.searchMode)?.label} `
    activeFilterItems.push({
      key: 'keyword',
      label: searchFieldLabels[filters.searchField],
      value: `${modeLabel}${filters.keyword}`,
      source: 'toolbar',
      onRemove: () => {
        window.clearTimeout(keywordTimerRef.current)
        setKeywordDraft('')
        applyFilters({ keyword: '' })
      },
    })
  }
  if (filters.zone) {
    activeFilterItems.push({
      key: 'zone',
      label: t('warehouseUi.locations.chipZone'),
      value: filters.zoneRow
        ? t('warehouseUi.locations.zoneRowValue', { zone: filters.zone, row: filters.zoneRow })
        : t('warehouseUi.locations.zoneValue', { zone: filters.zone }),
      source: 'toolbar',
      onRemove: () => applyFilters({ zone: undefined, zoneRow: undefined }),
    })
  }
  if (filters.locationType !== undefined) {
    activeFilterItems.push({
      key: 'locationType',
      label: t('warehouseUi.locations.chipType'),
      value: filters.locationType === LOCATION_TYPE_PICKING ? t('warehouseUi.locations.typePicking') : t('warehouseUi.locations.typeStorage'),
      source: 'toolbar',
      onRemove: () => applyFilters({ locationType: undefined }),
    })
  }
  if (filters.usage !== undefined) {
    activeFilterItems.push({
      key: 'usage',
      label: t('warehouseUi.locations.chipUsage'),
      value: filters.usage ? t('warehouseUi.locations.usageUsed') : t('warehouseUi.locations.usageEmpty'),
      source: 'toolbar',
      onRemove: () => applyFilters({ usage: undefined }),
    })
  }
  if (filters.status !== undefined) {
    activeFilterItems.push({
      key: 'status',
      label: t('warehouseUi.locations.chipStatus'),
      value: filters.status === 1 ? t('warehouseUi.locations.statusEnabled') : t('warehouseUi.locations.statusDisabled'),
      source: 'toolbar',
      onRemove: () => applyFilters({ status: undefined }),
    })
  }
  if (filters.updatedBy) {
    activeFilterItems.push({
      key: 'updatedBy',
      label: t('warehouseUi.locations.chipUpdatedBy'),
      value: filters.updatedBy,
      source: 'toolbar',
      onRemove: () => applyFilters({ updatedBy: undefined }),
    })
  }
  if (filters.updatedAtFrom || filters.updatedAtTo) {
    activeFilterItems.push({
      key: 'updatedAt',
      label: t('warehouseUi.locations.chipUpdatedAt'),
      value: formatRangeValue(filters.updatedAtFrom, filters.updatedAtTo),
      source: 'toolbar',
      onRemove: () => applyFilters({ updatedAtFrom: undefined, updatedAtTo: undefined }),
    })
  }

  const zoneSummary = useMemo(
    () => (distribution.result ? summarizeLocationZones(distribution.result.entries) : undefined),
    [distribution.result],
  )
  const zoneRack = useMemo(
    () =>
      distribution.result && filters.zone && filters.zone !== OTHER_ZONE_KEY
        ? buildZoneRack(distribution.result.entries, filters.zone)
        : undefined,
    [distribution.result, filters.zone],
  )

  const rowSelection: TableProps<LocationItem>['rowSelection'] = {
    fixed: true,
    columnWidth: 40,
    selectedRowKeys,
    onChange: (nextSelectedRowKeys) => setSelectedRowKeys(nextSelectedRowKeys),
    getCheckboxProps: (record) => ({
      disabled: !hasUnbindableProducts(record),
    }),
  }

  const renderBarcodePopover = (record: LocationItem) => (
    <div className="wh-locations-barcode-popover">
      <div>
        <div className="wh-locations-barcode-label">{t('column.locationCode')}</div>
        <BarcodePreview value={record.locationCode} align="left" compactCopy textNoWrap />
      </div>
      <div>
        <div className="wh-locations-barcode-label">{t('column.locationBarcode')}</div>
        <BarcodePreview value={record.locationBarcode} align="left" compactCopy textNoWrap />
      </div>
    </div>
  )

  const renderProductLine = (record: LocationItem, product: LocationProduct, index: number) => {
    const productCode = product.productCode?.trim()
    const bindingKey = JSON.stringify([record.locationGuid, productCode])
    const productLabel = product.itemNumber || product.productName || productCode || ''
    return (
      <div className="wh-locations-product" key={`${productCode ?? 'product'}-${index}`}>
        {product.productImage ? (
          <Image src={product.productImage} width={24} height={24} className="wh-locations-thumb" alt="" />
        ) : (
          <span className="wh-locations-thumb wh-locations-thumb-empty" aria-hidden="true" />
        )}
        <Typography.Text
          className="wh-locations-item-number"
          copyable={product.itemNumber ? { text: product.itemNumber } : false}
        >
          {product.itemNumber || '--'}
        </Typography.Text>
        {/* 商品条码只读 productBarcode，缺失时不回退显示货号 */}
        {product.productBarcode ? <span className="wh-locations-sub">{product.productBarcode}</span> : null}
        <span className="wh-locations-product-name" title={product.productName}>
          {product.productName || '--'}
        </span>
        {canManageLocations && productCode ? (
          <Tooltip title={t('warehouseUi.locations.unbindTooltip')}>
            <Button
              type="text"
              size="small"
              className="wh-locations-unbind"
              icon={<CloseOutlined />}
              loading={unbindingKeys.includes(bindingKey)}
              disabled={batchUnbinding}
              aria-label={t('warehouseUi.locations.unbindAria', {
                location: record.locationCode || record.locationGuid,
                product: productLabel,
              })}
              onClick={() => confirmUnbindProduct(record, product)}
            />
          </Tooltip>
        ) : null}
      </div>
    )
  }

  const columns: ColumnsType<LocationItem> = [
    {
      title: t('warehouseUi.locations.colLocation'),
      dataIndex: 'locationCode',
      key: 'locationCode',
      sorter: true,
      sortOrder: sortBy === 'LocationCode' ? sortOrder : null,
      width: 190,
      render: (value: string | undefined, record) => (
        <div>
          <div className="wh-locations-code-line">
            <span className={`wh-locations-code${record.status === 1 ? '' : ' wh-locations-code-disabled'}`}>{value || '--'}</span>
            {/* 条码图收进 Popover：列表只显示文字，需要扫码或复制时再展开两种条码 */}
            <Popover trigger="click" placement="right" content={renderBarcodePopover(record)}>
              <Button
                type="text"
                size="small"
                className="wh-locations-barcode-button"
                icon={<BarcodeOutlined />}
                aria-label={t('warehouseUi.locations.previewBarcode', { code: value || record.locationGuid })}
              />
            </Popover>
          </div>
          <div className="wh-locations-sub">{record.locationBarcode || '--'}</div>
        </div>
      ),
    },
    {
      title: t('warehouseUi.locations.colType'),
      dataIndex: 'locationType',
      key: 'locationType',
      sorter: true,
      sortOrder: sortBy === 'LocationType' ? sortOrder : null,
      width: 92,
      render: (value: number | null | undefined) =>
        value === LOCATION_TYPE_PICKING ? (
          <span className="wh-locations-type wh-locations-type-picking">{t('warehouseUi.locations.typePicking')}</span>
        ) : value === LOCATION_TYPE_STORAGE ? (
          <span className="wh-locations-type wh-locations-type-storage">{t('warehouseUi.locations.typeStorage')}</span>
        ) : (
          '--'
        ),
    },
    {
      title: t('warehouseUi.locations.colStatus'),
      dataIndex: 'status',
      key: 'status',
      sorter: true,
      sortOrder: sortBy === 'Status' ? sortOrder : null,
      width: 84,
      render: (value: number | null | undefined) =>
        value === 1 ? (
          <span className="wh-locations-status-on">{t('warehouseUi.locations.statusEnabled')}</span>
        ) : (
          <span className="wh-locations-status-off">{t('warehouseUi.locations.statusDisabled')}</span>
        ),
    },
    {
      title: t('warehouseUi.locations.colProducts'),
      key: 'products',
      sorter: true,
      sortOrder: sortBy === 'Usage' ? sortOrder : null,
      width: 460,
      render: (_, record) =>
        record.products?.length ? (
          <Image.PreviewGroup>
            <div className="wh-locations-products">
              {record.products.map((product, index) => renderProductLine(record, product, index))}
            </div>
          </Image.PreviewGroup>
        ) : (
          <span className="wh-locations-empty">{t('warehouseUi.locations.emptyLocation')}</span>
        ),
    },
    {
      title: t('warehouseUi.locations.colUpdated'),
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      sorter: true,
      sortOrder: sortBy === 'UpdatedAt' ? sortOrder : null,
      width: 140,
      render: (value: string | undefined, record) => (
        <div className="wh-locations-updated">
          <div>{record.updatedBy || '--'}</div>
          <div className="wh-locations-sub">{formatDateTime(value)}</div>
        </div>
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 76,
      fixed: 'right',
      align: 'right',
      render: (_, record) =>
        // 行内编辑/删除与批量解绑同一权限：货位管理（canManageWarehouseLocations）。
        canManageLocations ? (
          <div className="wh-locations-actions">
            <Tooltip title={t('common.edit')}>
              <Button
                type="text"
                size="small"
                icon={<EditOutlined />}
                aria-label={t('warehouseUi.locations.editAria', { code: record.locationCode || record.locationGuid })}
                onClick={() => handleEdit(record)}
              />
            </Tooltip>
            <Dropdown
              trigger={['click']}
              menu={{
                items: [{ key: 'delete', danger: true, icon: <DeleteOutlined />, label: t('common.delete') }],
                onClick: () => confirmDelete(record),
              }}
            >
              <Button
                type="text"
                size="small"
                icon={<MoreOutlined />}
                aria-label={t('warehouseUi.locations.moreAria', { code: record.locationCode || record.locationGuid })}
              />
            </Dropdown>
          </div>
        ) : null,
    },
  ]

  const selectionColumnWidth = canManageLocations ? 40 : 0
  const tableScrollX = selectionColumnWidth + columns.reduce((sum, column) => sum + (typeof column.width === 'number' ? column.width : 0), 0)

  const subtitle = summaryCounts
    ? t('warehouseUi.locations.subtitle', {
        total: formatCount(summaryCounts.total),
        used: formatCount(summaryCounts.used),
        empty: formatCount(Math.max(summaryCounts.total - summaryCounts.used, 0)),
      })
    : undefined

  const renderRackCell = (cell: RackCell | null, zone: string, rowText: string, columnIndex: number) => {
    if (!cell) return <span key={`none-${columnIndex}`} className="wh-locations-rack-cell wh-locations-rack-none" aria-hidden="true" />
    const code = `${zone}-${rowText}-${cell.columnText}`
    const title =
      cell.state === 'off'
        ? t('warehouseUi.locations.cellTitleOff', { code })
        : cell.disabledLevels
          ? t('warehouseUi.locations.cellTitleWithDisabled', {
              code,
              used: cell.usedLevels,
              enabled: cell.enabledLevels,
              disabled: cell.disabledLevels,
            })
          : t('warehouseUi.locations.cellTitle', { code, used: cell.usedLevels, enabled: cell.enabledLevels })
    return <span key={code} role="img" aria-label={title} title={title} className={`wh-locations-rack-cell wh-locations-rack-${cell.state}`} />
  }

  const renderDistributionBody = () => {
    if (!distributionOpen) {
      return (
        <span className="wh-locations-hint">
          {summaryCounts
            ? t('warehouseUi.locations.distCost', { total: formatCount(summaryCounts.total) })
            : t('warehouseUi.locations.distCostUnknown')}
        </span>
      )
    }
    if (!zoneSummary) {
      if (distribution.status === 'error') {
        return (
          <Alert
            type="warning"
            showIcon
            message={t('warehouseUi.locations.distFailed')}
            action={
              <Button size="small" onClick={loadDistribution}>
                {t('warehouseUi.locations.distRetry')}
              </Button>
            }
          />
        )
      }
      return (
        <div>
          <span className="wh-locations-hint">
            {t('warehouseUi.locations.distLoading', {
              loaded: formatCount(distribution.loaded),
              total: distribution.total === undefined ? '…' : formatCount(distribution.total),
            })}
          </span>
          <Progress
            percent={distribution.total ? Math.round((distribution.loaded / distribution.total) * 100) : 0}
            size="small"
            showInfo={false}
          />
        </div>
      )
    }

    const zoneCards = [zoneSummary.all, ...zoneSummary.zones]
    const gridColumns = zoneRack ? `52px repeat(${zoneRack.columns.length}, minmax(20px, 1fr))` : ''

    return (
      <>
        <div className="wh-locations-zones">
          {zoneCards.map((summary) => {
            const isAll = summary.key === 'all'
            const isOther = summary.key === OTHER_ZONE_KEY
            const active = isAll ? !filters.zone : filters.zone === summary.key
            const rate = getUsageRatePercent(summary)
            const name = isAll
              ? t('warehouseUi.locations.zoneAll')
              : isOther
                ? t('warehouseUi.locations.zoneOther')
                : t('warehouseUi.locations.zoneName', { zone: summary.key })
            return (
              <button
                key={summary.key}
                type="button"
                aria-pressed={active}
                disabled={isOther}
                title={
                  isOther
                    ? t('warehouseUi.locations.zoneOtherTitle')
                    : t('warehouseUi.locations.zoneTitle', { total: summary.total, used: summary.used, disabled: summary.disabled })
                }
                className={`wh-locations-zone${active ? ' wh-locations-zone-active' : ''}`}
                onClick={() => (isAll ? applyFilters({ zone: undefined, zoneRow: undefined }) : selectZone(summary.key))}
              >
                <span className="wh-locations-zone-head">
                  <span className="wh-locations-zone-name">{name}</span>
                  <span className="wh-locations-zone-total">{t('warehouseUi.locations.zoneTotal', { count: formatCount(summary.total) })}</span>
                </span>
                <span className="wh-locations-bar" aria-hidden="true">
                  <span className="wh-locations-bar-fill" style={{ width: `${rate}%` }} />
                </span>
                <span className="wh-locations-zone-usage">
                  {t('warehouseUi.locations.zoneUsage', { rate, empty: formatCount(summary.total - summary.used) })}
                </span>
              </button>
            )
          })}
        </div>
        {zoneRack ? (
          <div className="wh-locations-rack">
            <div className="wh-locations-rack-head">
              <h3 className="wh-locations-dist-title">{t('warehouseUi.locations.rackTitle', { zone: zoneRack.zone })}</h3>
              <span className="wh-locations-hint">{t('warehouseUi.locations.rackHint')}</span>
              <span className="wh-locations-legend">
                <span className="wh-locations-legend-item">
                  <span className="wh-locations-legend-swatch wh-locations-rack-cell wh-locations-rack-full" aria-hidden="true" />
                  {t('warehouseUi.locations.legendFull')}
                </span>
                <span className="wh-locations-legend-item">
                  <span className="wh-locations-legend-swatch wh-locations-rack-cell wh-locations-rack-partial" aria-hidden="true" />
                  {t('warehouseUi.locations.legendPartial')}
                </span>
                <span className="wh-locations-legend-item">
                  <span className="wh-locations-legend-swatch wh-locations-rack-cell wh-locations-rack-empty" aria-hidden="true" />
                  {t('warehouseUi.locations.legendEmpty')}
                </span>
                <span className="wh-locations-legend-item">
                  <span className="wh-locations-legend-swatch wh-locations-rack-cell wh-locations-rack-off" aria-hidden="true" />
                  {t('warehouseUi.locations.legendOff')}
                </span>
              </span>
            </div>
            <div className="wh-locations-rack-scroll">
              <div className="wh-locations-rack-grid" style={{ minWidth: 52 + zoneRack.columns.length * 23 }}>
                <div className="wh-locations-rack-line" style={{ gridTemplateColumns: gridColumns }}>
                  <span />
                  {zoneRack.columns.map((column) => (
                    <span key={column} className="wh-locations-rack-col-head">
                      {String(column).padStart(2, '0')}
                    </span>
                  ))}
                </div>
                {zoneRack.rows.map((row) => {
                  const rowActive = filters.zoneRow === row.rowText
                  return (
                    <div
                      key={row.rowText}
                      className={`wh-locations-rack-line${rowActive ? ' wh-locations-rack-line-active' : ''}`}
                      style={{ gridTemplateColumns: gridColumns }}
                    >
                      <button
                        type="button"
                        className="wh-locations-rack-row-button"
                        aria-pressed={rowActive}
                        aria-label={t('warehouseUi.locations.rowAria', { zone: zoneRack.zone, row: row.rowText })}
                        onClick={() => selectZoneRow(zoneRack.zone, row.rowText)}
                      >
                        {t('warehouseUi.locations.rowLabel', { row: row.rowText })}
                      </button>
                      {row.cells.map((cell, columnIndex) => renderRackCell(cell, zoneRack.zone, row.rowText, columnIndex))}
                    </div>
                  )
                })}
              </div>
            </div>
          </div>
        ) : (
          <span className="wh-locations-hint">{t('warehouseUi.locations.rackSelectHint')}</span>
        )}
      </>
    )
  }

  return (
    <PageContainer
      compact
      title={t('warehouseLocations.title')}
      subtitle={subtitle}
      extra={
        // 「从HQ更新货位」（HQ → HBweb）已于 2026-09-29 停用，页头只保留新建货位。
        access.canManageWarehouse ? (
          <Button type="primary" icon={<PlusOutlined />} onClick={handleCreate}>
            {t('warehouseUi.locations.newLocation')}
          </Button>
        ) : null
      }
    >
      <div className="wh-locations-stack">
        <section className="wh-locations-dist" aria-label={t('warehouseUi.locations.distTitle')}>
          <div className="wh-locations-dist-head">
            <h2 className="wh-locations-dist-title">{t('warehouseUi.locations.distTitle')}</h2>
            <span className="wh-locations-hint">{t('warehouseUi.locations.distHint')}</span>
            <span className="wh-locations-dist-spacer" />
            {distributionOpen && distribution.result?.truncated ? (
              <span className="wh-locations-stale">
                {t('warehouseUi.locations.distTruncated', { count: formatCount(distribution.result.entries.length) })}
              </span>
            ) : null}
            {distributionOpen && distribution.stale ? (
              <span className="wh-locations-stale">{t('warehouseUi.locations.distStale')}</span>
            ) : null}
            {distributionOpen && distribution.status === 'loading' && zoneSummary ? (
              <span className="wh-locations-hint">
                {t('warehouseUi.locations.distLoading', {
                  loaded: formatCount(distribution.loaded),
                  total: distribution.total === undefined ? '…' : formatCount(distribution.total),
                })}
              </span>
            ) : null}
            {distributionOpen && distribution.status === 'ready' && distribution.loadedAt ? (
              <span className="wh-locations-hint">
                {t('warehouseUi.locations.distLoadedAt', { time: distribution.loadedAt.format('HH:mm') })}
              </span>
            ) : null}
            {distributionOpen ? (
              <>
                {distribution.status === 'ready' ? (
                  <Button size="small" icon={<ReloadOutlined />} onClick={loadDistribution}>
                    {t('warehouseUi.locations.distRefresh')}
                  </Button>
                ) : null}
                <Button size="small" type="text" onClick={() => toggleDistribution(false)}>
                  {t('warehouseUi.locations.distCollapse')}
                </Button>
              </>
            ) : (
              <Button size="small" onClick={() => toggleDistribution(true)}>
                {t('warehouseUi.locations.distLoad')}
              </Button>
            )}
          </div>
          {renderDistributionBody()}
        </section>

        <Card className="wh-locations-list">
          <div className="wh-locations-toolbar">
            <Space.Compact className="wh-locations-search">
              <Select<LocationSearchField>
                className="wh-locations-search-field"
                aria-label={t('warehouseUi.locations.searchFieldAria')}
                value={filters.searchField}
                options={LOCATION_SEARCH_FIELDS.map((field) => ({ value: field, label: searchFieldLabels[field] }))}
                onChange={handleSearchFieldChange}
              />
              <Input
                allowClear
                value={keywordDraft}
                prefix={<SearchOutlined />}
                aria-label={t('warehouseUi.locations.searchAria')}
                placeholder={t('warehouseUi.locations.searchPlaceholder', { field: searchFieldLabels[filters.searchField] })}
                onChange={(event) => handleKeywordChange(event.target.value)}
                onPressEnter={applyKeywordNow}
              />
            </Space.Compact>
            <Segmented<string>
              aria-label={t('warehouseUi.locations.typeAria')}
              value={filters.locationType === undefined ? 'all' : String(filters.locationType)}
              options={[
                { label: t('warehouseUi.locations.typeAll'), value: 'all' },
                { label: t('warehouseUi.locations.typePicking'), value: String(LOCATION_TYPE_PICKING) },
                { label: t('warehouseUi.locations.typeStorage'), value: String(LOCATION_TYPE_STORAGE) },
              ]}
              onChange={(value) => applyFilters({ locationType: value === 'all' ? undefined : Number(value) })}
            />
            <Segmented<string>
              aria-label={t('warehouseUi.locations.usageAria')}
              value={filters.usage === undefined ? 'all' : filters.usage ? 'used' : 'empty'}
              options={[
                { label: t('warehouseUi.locations.usageAll'), value: 'all' },
                { label: t('warehouseUi.locations.usageUsed'), value: 'used' },
                { label: t('warehouseUi.locations.usageEmpty'), value: 'empty' },
              ]}
              onChange={(value) => applyFilters({ usage: value === 'all' ? undefined : value === 'used' })}
            />
            <MoreFiltersButton
              activeCount={countMoreLocationFilters(filters)}
              open={moreOpen}
              onOpenChange={(open) => {
                // 打开时用已生效条件初始化草稿；不点「应用」直接关闭则丢弃修改。
                if (open) setMoreDraft(filters)
                setMoreOpen(open)
              }}
            >
              <div className="wh-locations-more-field">
                <span className="wh-locations-more-label">{t('warehouseUi.locations.moreMatchMode')}</span>
                <Select<TextFilterMode>
                  aria-label={t('warehouseUi.locations.moreMatchMode')}
                  value={moreDraft.searchMode}
                  options={textFilterModeOptions}
                  onChange={(searchMode) => setMoreDraft((draft) => ({ ...draft, searchMode }))}
                />
              </div>
              <div className="wh-locations-more-field">
                <span className="wh-locations-more-label">{t('warehouseUi.locations.moreStatus')}</span>
                <Select<number>
                  allowClear
                  aria-label={t('warehouseUi.locations.moreStatus')}
                  placeholder={t('warehouseUi.locations.statusAll')}
                  value={moreDraft.status}
                  options={statusOptions}
                  onChange={(status) => setMoreDraft((draft) => ({ ...draft, status: status ?? undefined }))}
                />
              </div>
              <div className="wh-locations-more-field">
                <span className="wh-locations-more-label">{t('warehouseUi.locations.moreUpdatedBy')}</span>
                <Input
                  allowClear
                  aria-label={t('warehouseUi.locations.moreUpdatedBy')}
                  placeholder={t('warehouseUi.locations.moreUpdatedByPlaceholder')}
                  value={moreDraft.updatedBy ?? ''}
                  onChange={(event) => setMoreDraft((draft) => ({ ...draft, updatedBy: event.target.value || undefined }))}
                  onPressEnter={() => applyMoreFilters(moreDraft)}
                />
              </div>
              <div className="wh-locations-more-field">
                <span className="wh-locations-more-label">{t('warehouseUi.locations.moreUpdatedAt')}</span>
                <DatePicker.RangePicker
                  aria-label={t('warehouseUi.locations.moreUpdatedAt')}
                  allowEmpty={[true, true]}
                  value={
                    moreDraft.updatedAtFrom || moreDraft.updatedAtTo
                      ? [
                          moreDraft.updatedAtFrom ? dayjs(moreDraft.updatedAtFrom) : null,
                          moreDraft.updatedAtTo ? dayjs(moreDraft.updatedAtTo) : null,
                        ]
                      : null
                  }
                  onChange={(value) =>
                    setMoreDraft((draft) => ({
                      ...draft,
                      updatedAtFrom: value?.[0]?.format('YYYY-MM-DD'),
                      updatedAtTo: value?.[1]?.format('YYYY-MM-DD'),
                    }))
                  }
                />
              </div>
              <div className="wh-locations-more-actions">
                <Button size="small" onClick={() => applyMoreFilters(EMPTY_LOCATION_FILTERS)}>
                  {t('warehouseUi.locations.moreReset')}
                </Button>
                <Button size="small" type="primary" onClick={() => applyMoreFilters(moreDraft)}>
                  {t('warehouseUi.locations.moreApply')}
                </Button>
              </div>
            </MoreFiltersButton>
            <span className="wh-locations-toolbar-spacer" />
            <Tooltip title={t('warehouseUi.locations.refresh')}>
              <Button
                icon={<ReloadOutlined />}
                aria-label={t('warehouseUi.locations.refresh')}
                onClick={() => {
                  setSummaryVersion((version) => version + 1)
                  void loadData()
                }}
              />
            </Tooltip>
          </div>

          {activeFilterItems.length ? <ActiveFilterBar items={activeFilterItems} onClearAll={clearAllFilters} /> : null}

          {canManageLocations ? (
            <SelectionActionBar selectedCount={selectedLocations.length} onClearSelection={() => setSelectedRowKeys([])}>
              <span>{t('warehouseUi.locations.selectionLinks', { count: selectedBindings.length })}</span>
              <Button
                danger
                size="small"
                icon={<DisconnectOutlined />}
                disabled={!selectedBindings.length || loading || batchUnbinding}
                loading={batchUnbinding}
                onClick={handleBatchUnbind}
              >
                {t('warehouseUi.locations.unbindAll')}
              </Button>
            </SelectionActionBar>
          ) : null}

          <MeasuredTable metricId="warehouse.locations.table-1"
            rowKey="locationGuid"
            rowSelection={access.canManageWarehouseLocations ? rowSelection : undefined}
            className="wh-locations-table"
            loading={loading}
            columns={columns}
            dataSource={data}
            scroll={{ x: tableScrollX }}
            onChange={handleTableChange}
            pagination={{
              current: page,
              pageSize,
              total,
              showSizeChanger: true,
              showTotal: (value) => t('warehouseUi.locations.paginationTotal', { count: formatCount(value) }),
            }}
          />
        </Card>
      </div>

      <Modal
        title={editingItem ? t('warehouseUi.locations.editTitle') : t('warehouseUi.locations.createTitle')}
        open={modalOpen}
        onOk={() => void handleSave()}
        onCancel={handleCloseModal}
        confirmLoading={saving}
        destroyOnHidden
        okText={t('common.save')}
        cancelText={t('common.cancel')}
      >
        <Form form={form} layout="vertical" preserve={false}>
          <Form.Item
            name="locationCode"
            label={t('column.locationCode')}
            rules={[{ required: true, message: t('warehouseUi.locations.enterLocationCode') }]}
          >
            <Input placeholder={t('warehouseUi.locations.enterLocationCode')} />
          </Form.Item>
          <Form.Item name="locationBarcode" label={t('column.locationBarcode')}>
            <Input placeholder={t('warehouseUi.locations.enterLocationBarcode')} />
          </Form.Item>
          <Form.Item name="locationType" label={t('column.locationType')}>
            <Select placeholder={t('warehouseUi.locations.selectLocationType')} options={locationTypeOptions} />
          </Form.Item>
          <Form.Item name="status" label={t('warehouseUi.locations.fieldStatus')}>
            <Select placeholder={t('warehouseUi.locations.selectLocationStatus')} options={statusOptions} />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  )
}
