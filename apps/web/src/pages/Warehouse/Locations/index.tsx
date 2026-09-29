import {
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Button,
  Card,
  Form,
  Image,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { TableProps } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { FilterDropdownProps, SorterResult, SortOrder } from 'antd/es/table/interface'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import './compact.css'
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
import { copyTextToClipboard } from '../../../utils/clipboard'
import {
  buildComparableFilterTokens,
  buildLocationFilterQuery,
  buildTextFilterTokens,
  normalizeLocationTableFilters,
  parseComparableFilterTokens,
  parseTextFilterTokens,
  setFilterValues,
  type ComparableFilterMode,
  type TextFilterMode,
  type WarehouseLocationColumnFilters,
} from './columnFilters'
import {
  buildSelectedLocationProductBindings,
  coordinateBatchUnbindLocationProducts,
  hasUnbindableProducts,
} from './bulkUnbindSelection'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface LocationFormValues {
  locationCode: string
  locationBarcode?: string
  locationType?: number
  status?: number
}

function getLocationTypeOptions(t: (key: string, options?: Record<string, unknown>) => string) {
  return [
    { value: 2, label: t('warehouseLocations.storageLocation') },
    { value: 1, label: t('warehouseLocations.pickingLocation') },
  ]
}

function getStatusOptions(t: (key: string, options?: Record<string, unknown>) => string) {
  return [
    { value: 1, label: t('common.enable') },
    { value: 0, label: t('common.disable') },
  ]
}

function getUsageOptions(t: (key: string, options?: Record<string, unknown>) => string) {
  return [
    { value: true, label: t('common.used') },
    { value: false, label: t('common.unused') },
  ]
}

function formatLocationType(
  value: number | null | undefined,
  options: Array<{ value: number; label: string }>,
) {
  return options.find((item) => item.value === value)?.label || '--'
}

function formatStatus(value: number | null | undefined, t: (key: string) => string) {
  return value === 1 ? <Tag color="success">{t('common.enable')}</Tag> : <Tag>{t('common.disable')}</Tag>
}

function formatDateTime(value?: string) {
  if (!value) {
    return '--'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleString('zh-CN', { hour12: false })
}

type ProductTextField = 'itemNumber' | 'productName'
type LocationSortBy =
  | 'LocationCode'
  | 'LocationType'
  | 'LocationBarcode'
  | 'Status'
  | 'Usage'
  | 'UpdatedAt'
  | 'UpdatedBy'

const DEFAULT_LOCATION_SORT_BY: LocationSortBy = 'LocationCode'
const DEFAULT_LOCATION_SORT_ORDER: SortOrder = 'ascend'
const WAREHOUSE_LOCATION_SELECTION_COLUMN_WIDTH = 56
const WAREHOUSE_LOCATION_MIN_TABLE_SCROLL_X = 1480

const LOCATION_SORT_FIELD_MAP: Record<string, LocationSortBy> = {
  locationCode: 'LocationCode',
  locationType: 'LocationType',
  locationBarcode: 'LocationBarcode',
  status: 'Status',
  usage: 'Usage',
  updatedAt: 'UpdatedAt',
  updatedBy: 'UpdatedBy',
}

function toApiSortDirection(order: SortOrder): 'asc' | 'desc' {
  return order === 'descend' ? 'desc' : 'asc'
}

function getSortField(sorter: SorterResult<LocationItem>) {
  const field = Array.isArray(sorter.field) ? sorter.field.join('.') : sorter.field
  const key = sorter.columnKey
  const candidate = field || key
  return candidate ? String(candidate) : undefined
}

function getProductFieldValues(products: LocationProduct[], field: ProductTextField | 'productBarcode') {
  if (!products?.length) {
    return []
  }

  return products.map((item) => item[field]).filter((value): value is string => Boolean(value))
}

function renderCopyableProducts(
  products: LocationProduct[],
  field: 'itemNumber' | 'productName',
  t: (key: string) => string,
) {
  const values = getProductFieldValues(products, field)
  if (!values.length) {
    return '--'
  }

  const display = values.slice(0, 3).join('，')
  const suffix = values.length > 3 ? ` +${values.length - 3}` : ''
  const fullText = values.join('，')

  return (
    <Space size={4} className="warehouse-locations-copyable-cell warehouse-locations-nowrap">
      <span className="warehouse-locations-copyable-content">
        <Tooltip title={fullText}>
          <Typography.Text className="warehouse-locations-nowrap">{`${display}${suffix}`}</Typography.Text>
        </Tooltip>
      </span>
      <Tooltip title={t('common.copy')}>
        <Button
          size="small"
          type="text"
          className="warehouse-locations-copy-button"
          icon={<CopyOutlined />}
          onClick={() => void copyTextToClipboard(fullText)}
        />
      </Tooltip>
    </Space>
  )
}

function renderCopyableProductBarcodes(
  products: LocationProduct[],
  t: (key: string) => string,
) {
  const barcodeProducts = products.filter((product) => Boolean(product.productBarcode))
  const values = barcodeProducts.map((product) => product.productBarcode).filter((value): value is string => Boolean(value))
  if (!values.length) {
    return '--'
  }

  const suffix = values.length > 3 ? ` +${values.length - 3}` : ''
  const fullText = values.join('，')

  return (
    <Space size={4} className="warehouse-locations-barcode-cell warehouse-locations-nowrap">
      <span className="warehouse-locations-barcode-content">
        <Tooltip title={fullText}>
          <Space size={3} className="warehouse-locations-product-barcode-list">
            {barcodeProducts.slice(0, 3).map((product, index) => (
              <span
                key={`${product.productBarcode}-${index}`}
                className="warehouse-locations-product-barcode-item"
              >
                <BarcodePreview
                  value={product.productBarcode}
                  align="left"
                  className="warehouse-locations-product-barcode-preview"
                  compactCopy={false}
                  gap={2}
                  options={{ height: 18, width: 1, margin: 0 }}
                  showCopy={false}
                  textNoWrap
                />
              </span>
            ))}
            {suffix ? <Typography.Text className="warehouse-locations-nowrap">{suffix}</Typography.Text> : null}
          </Space>
        </Tooltip>
      </span>
      <Tooltip title={t('common.copy')}>
        <Button
          size="small"
          type="text"
          className="warehouse-locations-copy-button"
          icon={<CopyOutlined />}
          onClick={() => void copyTextToClipboard(fullText)}
        />
      </Tooltip>
    </Space>
  )
}

function renderLocationProductName(products: LocationProduct[]) {
  const values = getProductFieldValues(products, 'productName')
  if (!values.length) {
    return '--'
  }

  const fullText = values.join('，')

  return (
    <Tooltip title={fullText}>
      <span className="warehouse-locations-two-line-text">{fullText}</span>
    </Tooltip>
  )
}

function renderProductImages(products: LocationProduct[]) {
  const images = products?.map((item) => item.productImage).filter((value): value is string => Boolean(value)) ?? []

  if (!images.length) {
    return '--'
  }

  return (
    <Image.PreviewGroup>
      <Space size={4}>
        {images.slice(0, 3).map((image, index) => (
          <Image
            key={`${image}-${index}`}
            src={image}
            width={32}
            height={32}
            style={{ borderRadius: 4, objectFit: 'cover' }}
          />
        ))}
        {images.length > 3 ? <Tag>+{images.length - 3}</Tag> : null}
      </Space>
    </Image.PreviewGroup>
  )
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
  const [data, setData] = useState<LocationItem[]>([])
  const [locationCodeKeyword, setLocationCodeKeyword] = useState('')
  const [locationBarcodeKeyword, setLocationBarcodeKeyword] = useState('')
  const [updatedByKeyword, setUpdatedByKeyword] = useState('')
  const [locationTypeFilter, setLocationTypeFilter] = useState<number | null | undefined>(undefined)
  const [usageFilter, setUsageFilter] = useState<boolean | null | undefined>(undefined)
  const [columnFilters, setColumnFilters] = useState<WarehouseLocationColumnFilters>({})
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [sortBy, setSortBy] = useState<LocationSortBy>(DEFAULT_LOCATION_SORT_BY)
  const [sortOrder, setSortOrder] = useState<SortOrder>(DEFAULT_LOCATION_SORT_ORDER)
  const { access } = useAuthStore()
  const locationTypeOptions = getLocationTypeOptions(t)
  const statusOptions = getStatusOptions(t)
  const usageOptions = getUsageOptions(t)

  const loadData = async (
    nextPage = page,
    nextPageSize = pageSize,
    nextLocationType = locationTypeFilter,
    nextUsage = usageFilter,
    nextLocationCode = locationCodeKeyword,
    nextLocationBarcode = locationBarcodeKeyword,
    nextUpdatedBy = updatedByKeyword,
    nextSortBy = sortBy,
    nextSortOrder = sortOrder,
    nextColumnFilters = columnFilters,
  ) => {
    setLoading(true)
    const effectiveSortBy = nextSortBy || DEFAULT_LOCATION_SORT_BY
    const effectiveSortOrder = nextSortOrder || DEFAULT_LOCATION_SORT_ORDER
    const locationFilterQuery = buildLocationFilterQuery(nextColumnFilters)
    try {
      const result = await getLocationList({
        locationType: locationFilterQuery.locationType ?? nextLocationType,
        isUsed: locationFilterQuery.isUsed ?? nextUsage,
        locationCode: locationFilterQuery.locationCode || nextLocationCode || undefined,
        locationBarcode: locationFilterQuery.locationBarcode || nextLocationBarcode || undefined,
        updatedBy: locationFilterQuery.updatedBy || nextUpdatedBy || undefined,
        status: locationFilterQuery.status,
        filters: locationFilterQuery.filters,
        pageNumber: nextPage,
        pageSize: nextPageSize,
        sortBy: effectiveSortBy,
        sortDirection: toApiSortDirection(effectiveSortOrder),
      })

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
      setColumnFilters(nextColumnFilters)
      return result.items
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouseLocations.loadFailed'))
      return undefined
    } finally {
      setLoading(false)
    }
  }

  const loadDataWithColumnFilters = (
    nextPage = page,
    nextPageSize = pageSize,
    nextColumnFilters = columnFilters,
    nextSortBy = sortBy,
    nextSortOrder = sortOrder,
  ) =>
    loadData(
      nextPage,
      nextPageSize,
      null,
      null,
      '',
      '',
      '',
      nextSortBy,
      nextSortOrder,
      nextColumnFilters,
    )

  useEffect(() => {
    void loadData(1, pageSize)
  }, [])

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
        message.success(t('warehouseLocations.updateSuccess'))
      } else {
        await createLocation(values as CreateLocationParams)
        message.success(t('warehouseLocations.createSuccess'))
      }

      handleCloseModal()
      void loadDataWithColumnFilters(editingItem ? page : 1, pageSize)
    } catch (error) {
      if (error instanceof Error) {
        message.error(error.message || t('warehouseLocations.saveFailed'))
      }
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async (record: LocationItem) => {
    try {
      await deleteLocation(record.locationGuid)
      message.success(t('warehouseLocations.deleteSuccess'))
      void loadDataWithColumnFilters(page, pageSize)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouseLocations.deleteFailed'))
    }
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
            refresh: () => loadDataWithColumnFilters(page, pageSize),
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

          if (shouldApplyPatchedData && patchedData) {
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

  const mergeTopFilters = (filters: WarehouseLocationColumnFilters = columnFilters) => {
    let nextFilters = filters
    nextFilters = setFilterValues(nextFilters, 'locationCode', buildTextFilterTokens('contains', locationCodeKeyword))
    nextFilters = setFilterValues(nextFilters, 'locationBarcode', buildTextFilterTokens('contains', locationBarcodeKeyword))
    nextFilters = setFilterValues(nextFilters, 'updatedBy', buildTextFilterTokens('contains', updatedByKeyword))
    nextFilters = setFilterValues(nextFilters, 'locationType', locationTypeFilter === null || locationTypeFilter === undefined ? undefined : [locationTypeFilter])
    nextFilters = setFilterValues(nextFilters, 'usage', usageFilter === null || usageFilter === undefined ? undefined : [usageFilter])
    return nextFilters
  }

  const syncTopFiltersFromColumns = (filters: WarehouseLocationColumnFilters) => {
    setLocationCodeKeyword(parseTextFilterTokens(filters.locationCode).value)
    setLocationBarcodeKeyword(parseTextFilterTokens(filters.locationBarcode).value)
    setUpdatedByKeyword(parseTextFilterTokens(filters.updatedBy).value)
    setLocationTypeFilter(filters.locationType?.[0] === undefined ? undefined : Number(filters.locationType[0]))
    setUsageFilter(filters.usage?.[0] === undefined ? undefined : filters.usage[0] === 'true')
  }

  const handleTableChange: TableProps<LocationItem>['onChange'] = (pagination, filters, sorter, extra) => {
    const currentSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const field = getSortField(currentSorter)
    const nextSortBy = field ? LOCATION_SORT_FIELD_MAP[field] : DEFAULT_LOCATION_SORT_BY
    const nextSortOrder = currentSorter?.order || DEFAULT_LOCATION_SORT_ORDER

    if (extra.action === 'filter') {
      const nextColumnFilters = normalizeLocationTableFilters(filters)
      syncTopFiltersFromColumns(nextColumnFilters)
      void loadDataWithColumnFilters(
        1,
        pagination.pageSize || pageSize,
        nextColumnFilters,
        nextSortBy,
        nextSortOrder,
      )
      return
    }

    if (extra.action === 'sort') {
      void loadDataWithColumnFilters(
        1,
        pagination.pageSize || pageSize,
        columnFilters,
        nextSortBy,
        nextSortOrder,
      )
      return
    }

    if (extra.action === 'paginate') {
      void loadDataWithColumnFilters(pagination.current || page, pagination.pageSize || pageSize)
    }
  }

  const filterIcon = (filtered?: boolean) => <SearchOutlined style={{ color: filtered ? '#1677ff' : undefined }} />
  const filterDropdownStyle = { padding: 8, width: 240 }
  const textFilterModeOptions: Array<{ label: string; value: TextFilterMode }> = [
    { label: t('warehouse.filterMode.contains', '包含'), value: 'contains' },
    { label: t('warehouse.filterMode.equals', '等于'), value: 'eq' },
    { label: t('warehouse.filterMode.startsWith', '开头是'), value: 'starts' },
    { label: t('warehouse.filterMode.endsWith', '结尾是'), value: 'ends' },
  ]
  const comparableFilterModeOptions: Array<{ label: string; value: ComparableFilterMode }> = [
    { label: t('warehouse.filterMode.equals', '等于'), value: 'eq' },
    { label: t('warehouse.filterMode.range', '范围'), value: 'range' },
    { label: t('warehouse.filterMode.greaterOrEqual', '大于等于'), value: 'gte' },
    { label: t('warehouse.filterMode.lessOrEqual', '小于等于'), value: 'lte' },
  ]

  const renderColumnFilterPanel = (content: JSX.Element, onApply: () => void, onReset: () => void) => (
    <div style={filterDropdownStyle} onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Space direction="vertical" style={{ width: '100%' }}>
        {content}
        <Space>
          <Button size="small" onClick={onReset}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
          <Button size="small" type="primary" onClick={onApply}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
        </Space>
      </Space>
    </div>
  )

  const buildTextFilterDropdown = (filterKey: string, placeholder: string) => ({ confirm, selectedKeys, setSelectedKeys, clearFilters }: FilterDropdownProps) => {
    const values = (selectedKeys.length ? selectedKeys : columnFilters[filterKey] ?? []).map((value) => String(value))
    const parsedFilter = parseTextFilterTokens(values)
    const updateFilter = (mode: TextFilterMode, value?: string) => {
      setSelectedKeys(buildTextFilterTokens(mode, value))
    }

    return renderColumnFilterPanel(
      <>
        <Select size="small" value={parsedFilter.mode} options={textFilterModeOptions} onChange={(mode) => updateFilter(mode, parsedFilter.value)} />
        <Input
          autoFocus
          allowClear
          value={parsedFilter.value}
          placeholder={placeholder}
          onChange={(event) => updateFilter(parsedFilter.mode, event.target.value)}
          onPressEnter={() => confirm()}
        />
      </>,
      () => confirm(),
      () => {
        setSelectedKeys([])
        clearFilters?.()
        confirm()
      },
    )
  }

  const buildDateFilterDropdown = (filterKey: string) => ({ confirm, selectedKeys, setSelectedKeys, clearFilters }: FilterDropdownProps) => {
    const values = (selectedKeys.length ? selectedKeys : columnFilters[filterKey] ?? []).map((value) => String(value))
    const parsedFilter = parseComparableFilterTokens(values)
    const updateFilter = (mode: ComparableFilterMode, nextValue: { value?: string; min?: string; max?: string }) => {
      setSelectedKeys(buildComparableFilterTokens(mode, nextValue))
    }

    return renderColumnFilterPanel(
      <>
        <Select
          size="small"
          value={parsedFilter.mode}
          options={comparableFilterModeOptions}
          onChange={(mode) => updateFilter(mode, {
            value: parsedFilter.value || parsedFilter.min || parsedFilter.max,
            min: parsedFilter.min,
            max: parsedFilter.max,
          })}
        />
        {parsedFilter.mode === 'range' ? (
          <>
            <Input type="date" value={parsedFilter.min} onChange={(event) => updateFilter('range', { min: event.target.value || undefined, max: parsedFilter.max || undefined })} />
            <Input type="date" value={parsedFilter.max} onChange={(event) => updateFilter('range', { min: parsedFilter.min || undefined, max: event.target.value || undefined })} />
          </>
        ) : (
          <Input type="date" value={parsedFilter.value} onChange={(event) => updateFilter(parsedFilter.mode, { value: event.target.value || undefined })} />
        )}
      </>,
      () => confirm(),
      () => {
        setSelectedKeys([])
        clearFilters?.()
        confirm()
      },
    )
  }

  const textFilterProps = (filterKey: string, placeholder: string) => ({
    filterDropdown: buildTextFilterDropdown(filterKey, placeholder),
    filterIcon,
    filtered: Boolean(columnFilters[filterKey]?.[0]?.trim()),
    filteredValue: columnFilters[filterKey] ?? null,
  })

  const dateFilterProps = (filterKey: string) => ({
    filterDropdown: buildDateFilterDropdown(filterKey),
    filterIcon,
    filtered: Boolean(columnFilters[filterKey]?.length),
    filteredValue: columnFilters[filterKey] ?? null,
  })

  const enumFilterProps = (filterKey: string, options: Array<{ text: string; value: string | number }>) => ({
    filters: options,
    filterIcon,
    filtered: Boolean(columnFilters[filterKey]?.length),
    filteredValue: columnFilters[filterKey] ?? null,
  })

  const queryWithCurrentFilters = () => {
    const nextColumnFilters = mergeTopFilters()
    setColumnFilters(nextColumnFilters)
    void loadDataWithColumnFilters(1, pageSize, nextColumnFilters, sortBy, sortOrder)
  }

  const rowSelection: TableProps<LocationItem>['rowSelection'] = {
    // 显式固定选择列宽度，并与左侧序号列共同固定，避免虚拟横向滚动时两列叠在一起。
    fixed: true,
    columnWidth: WAREHOUSE_LOCATION_SELECTION_COLUMN_WIDTH,
    selectedRowKeys,
    onChange: (nextSelectedRowKeys) => setSelectedRowKeys(nextSelectedRowKeys),
    getCheckboxProps: (record) => ({
      disabled: !hasUnbindableProducts(record),
    }),
  }

  const columns: ColumnsType<LocationItem> = [
    {
      title: t('column.index'),
      key: 'index',
      width: 56,
      fixed: 'left',
      render: (_, __, index) => (page - 1) * pageSize + index + 1,
    },
    {
      title: t('column.locationCode'),
      dataIndex: 'locationCode',
      sorter: true,
      sortOrder: sortBy === 'LocationCode' ? sortOrder : null,
      width: 150,
      ...textFilterProps('locationCode', t('warehouseLocations.searchLocationCode')),
      render: (value: string | undefined) => (
        <BarcodePreview value={value} align="left" compactCopy textNoWrap />
      ),
    },
    {
      title: t('column.locationType'),
      dataIndex: 'locationType',
      sorter: true,
      sortOrder: sortBy === 'LocationType' ? sortOrder : null,
      width: 86,
      ...enumFilterProps('locationType', locationTypeOptions.map((option) => ({ text: String(option.label), value: option.value }))),
      render: (value: number | null | undefined) => formatLocationType(value, locationTypeOptions),
    },
    {
      title: t('column.locationBarcode'),
      dataIndex: 'locationBarcode',
      sorter: true,
      sortOrder: sortBy === 'LocationBarcode' ? sortOrder : null,
      width: 150,
      ...textFilterProps('locationBarcode', t('warehouseLocations.searchLocationBarcode')),
      render: (value: string | undefined) => (
        <BarcodePreview value={value} align="left" compactCopy textNoWrap />
      ),
    },
    {
      title: t('column.locationStatus'),
      dataIndex: 'status',
      sorter: true,
      sortOrder: sortBy === 'Status' ? sortOrder : null,
      width: 76,
      ...enumFilterProps('status', statusOptions.map((option) => ({ text: String(option.label), value: option.value }))),
      render: (value: number | null | undefined) => formatStatus(value, t),
    },
    {
      title: t('column.usageStatus'),
      key: 'usage',
      sorter: true,
      sortOrder: sortBy === 'Usage' ? sortOrder : null,
      width: 86,
      ...enumFilterProps('usage', usageOptions.map((option) => ({ text: String(option.label), value: String(option.value) }))),
      render: (_, record) =>
        record.products?.length ? <Tag color="processing">{t('common.used')}</Tag> : <Tag>{t('common.unused')}</Tag>,
    },
    {
      title: t('column.itemNumber'),
      key: 'itemNumbers',
      width: 118,
      ...textFilterProps('productItemNumber', t('column.itemNumber')),
      render: (_, record) => renderCopyableProducts(record.products, 'itemNumber', t),
    },
    {
      title: t('column.productBarcode'),
      key: 'productBarcodes',
      width: 150,
      ...textFilterProps('productBarcode', t('column.productBarcode')),
      render: (_, record) => renderCopyableProductBarcodes(record.products, t),
    },
    {
      title: t('column.productName'),
      key: 'productNames',
      width: 190,
      ...textFilterProps('productName', t('column.productName')),
      render: (_, record) => renderLocationProductName(record.products),
    },
    {
      title: t('column.image'),
      key: 'productImages',
      width: 112,
      render: (_, record) => renderProductImages(record.products),
    },
    {
      title: t('column.updateTime'),
      dataIndex: 'updatedAt',
      sorter: true,
      sortOrder: sortBy === 'UpdatedAt' ? sortOrder : null,
      width: 150,
      ...dateFilterProps('updatedAt'),
      render: (value: string | undefined) => (
        <span className="warehouse-locations-nowrap">{formatDateTime(value)}</span>
      ),
    },
    {
      title: t('column.updater'),
      dataIndex: 'updatedBy',
      sorter: true,
      sortOrder: sortBy === 'UpdatedBy' ? sortOrder : null,
      width: 100,
      ...textFilterProps('updatedBy', t('warehouseLocations.searchUpdatedBy')),
      render: (value: string | undefined) => (
        <span className="warehouse-locations-nowrap">{value || '--'}</span>
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 132,
      fixed: 'right',
      render: (_, record) => (
        <Space size={4}>
          <Button type="link" icon={<EditOutlined />} onClick={() => handleEdit(record)}>
            {t('common.edit')}
          </Button>
          <Popconfirm
            title={t('warehouseLocations.confirmDelete')}
            description={t('warehouseLocations.deleteIrreversible')}
            okText={t('common.delete')}
            cancelText={t('common.cancel')}
            onConfirm={() => void handleDelete(record)}
          >
            <Button danger type="link" icon={<DeleteOutlined />}>
              {t('common.delete')}
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  const selectionColumnWidth = access.canManageWarehouseLocations
    ? WAREHOUSE_LOCATION_SELECTION_COLUMN_WIDTH
    : 0
  const tableScrollX = Math.max(
    WAREHOUSE_LOCATION_MIN_TABLE_SCROLL_X,
    selectionColumnWidth + columns.reduce((total, column) => {
      const width = typeof column.width === 'number' ? column.width : Number(column.width)
      return total + (Number.isFinite(width) ? width : 0)
    }, 0),
  )

  return (
    <PageContainer
      title={t('warehouseLocations.title')}
      subtitle={t('warehouseLocations.subtitle')}
      extra={
        // 「从HQ更新货位」（HQ → HBweb）已于 2026-09-29 停用，页头只保留新建货位。
        access.canManageWarehouse ? (
          <Space>
            <Button type="primary" icon={<PlusOutlined />} onClick={handleCreate}>
              {t('warehouseLocations.newLocation')}
            </Button>
          </Space>
        ) : null
      }
    >
      <Card>
        <Space wrap style={{ marginBottom: 16 }}>
          <Input
            placeholder={t('warehouseLocations.searchLocationCode')}
            value={locationCodeKeyword}
            onChange={(event) => setLocationCodeKeyword(event.target.value)}
            style={{ width: 180 }}
            allowClear
          />
          <Input
            placeholder={t('warehouseLocations.searchLocationBarcode')}
            value={locationBarcodeKeyword}
            onChange={(event) => setLocationBarcodeKeyword(event.target.value)}
            style={{ width: 180 }}
            allowClear
          />
          <Input
            placeholder={t('warehouseLocations.searchUpdatedBy')}
            value={updatedByKeyword}
            onChange={(event) => setUpdatedByKeyword(event.target.value)}
            style={{ width: 160 }}
            allowClear
          />
          <Space>
            <Typography.Text>{t('warehouseLocations.locationTypeFilter')}</Typography.Text>
            <Select
              value={locationTypeFilter}
              onChange={setLocationTypeFilter}
              options={locationTypeOptions}
              placeholder={t('warehouseLocations.allTypes')}
              allowClear
              style={{ width: 160 }}
            />
          </Space>
          <Space>
            <Typography.Text>{t('warehouseLocations.usageFilter')}</Typography.Text>
            <Select
              value={usageFilter}
              onChange={setUsageFilter}
              options={usageOptions}
              placeholder={t('warehouseLocations.allUsageStatus')}
              allowClear
              style={{ width: 160 }}
            />
          </Space>
          <Button type="primary" icon={<SearchOutlined />} onClick={queryWithCurrentFilters}>
            {t('common.query')}
          </Button>
          <Button
            icon={<ReloadOutlined />}
            onClick={() => {
              setLocationCodeKeyword('')
              setLocationBarcodeKeyword('')
              setUpdatedByKeyword('')
              setLocationTypeFilter(undefined)
              setUsageFilter(undefined)
              setColumnFilters({})
              setSortBy(DEFAULT_LOCATION_SORT_BY)
              setSortOrder(DEFAULT_LOCATION_SORT_ORDER)
              // 顶部重置同时清空列头过滤，避免请求继续携带旧条件。
              void loadDataWithColumnFilters(1, pageSize, {}, DEFAULT_LOCATION_SORT_BY, DEFAULT_LOCATION_SORT_ORDER)
            }}
          >
            {t('common.reset')}
          </Button>
        </Space>

        {access.canManageWarehouseLocations ? (
          <Space size="small" wrap style={{ marginBottom: 12 }}>
            <Typography.Text type="secondary">
              {t('warehouseLocations.selectedLocations', {
                locations: selectedLocations.length,
                products: selectedBindings.length,
              })}
            </Typography.Text>
            <Button
              danger
              size="small"
              icon={<DeleteOutlined />}
              disabled={!selectedBindings.length || loading || batchUnbinding}
              loading={batchUnbinding}
              onClick={handleBatchUnbind}
            >
              {t('warehouseLocations.batchUnbind')}
            </Button>
          </Space>
        ) : null}

        <MeasuredTable metricId="warehouse.locations.table-1"
          rowKey="locationGuid"
          rowSelection={access.canManageWarehouseLocations ? rowSelection : undefined}
          className="warehouse-locations-compact-table"
          virtual
          loading={loading}
          columns={columns}
          dataSource={data}
          size="small"
          scroll={{ x: tableScrollX, y: 600 }}
          onChange={handleTableChange}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
          }}
        />
      </Card>

      <Modal
        title={editingItem ? t('warehouseLocations.editTitle') : t('warehouseLocations.createTitle')}
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
            rules={[{ required: true, message: t('warehouseLocations.enterLocationCode') }]}
          >
            <Input placeholder={t('warehouseLocations.enterLocationCode')} />
          </Form.Item>
          <Form.Item name="locationBarcode" label={t('column.locationBarcode')}>
            <Input placeholder={t('warehouseLocations.enterLocationBarcode')} />
          </Form.Item>
          <Form.Item name="locationType" label={t('column.locationType')}>
            <Select placeholder={t('warehouseLocations.selectLocationType')} options={locationTypeOptions} />
          </Form.Item>
          <Form.Item name="status" label={t('column.locationStatus')}>
            <Select placeholder={t('warehouseLocations.selectLocationStatus')} options={statusOptions} />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  )
}
