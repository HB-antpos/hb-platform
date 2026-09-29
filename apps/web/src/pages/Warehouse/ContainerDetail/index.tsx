import {
  ArrowLeftOutlined,
  AppstoreOutlined,
  CheckCircleOutlined,
  CloudUploadOutlined,
  CopyOutlined,
  DeleteOutlined,
  DownloadOutlined,
  EditOutlined,
  HistoryOutlined,
  ReloadOutlined,
  SaveOutlined,
  SearchOutlined,
  SettingOutlined,
  SnippetsOutlined,
} from '@ant-design/icons'
import {
  DndContext,
  PointerSensor,
  closestCenter,
  type DragEndEvent,
  useSensor,
  useSensors,
} from '@dnd-kit/core'
import {
  SortableContext,
  horizontalListSortingStrategy,
  useSortable,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import {
  Alert,
  Button,
  Card,
  Checkbox,
  DatePicker,
  Descriptions,
  Dropdown,
  Drawer,
  Image,
  Input,
  InputNumber,
  Modal,
  Progress,
  Radio,
  Select,
  Space,
  Spin,
  Switch,
  Tag,
  Tooltip,
  Typography,
  message,
  notification,
} from 'antd'
import type { ColumnsType, TableRef } from 'antd/es/table'
import type { FilterDropdownProps, SorterResult, TablePaginationConfig } from 'antd/es/table/interface'
import type { TFunction } from 'i18next'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useEffect, useMemo, useRef, useState, type ClipboardEvent as ReactClipboardEvent, type CSSProperties, type HTMLAttributes, type Key, type KeyboardEvent, type PointerEvent as ReactPointerEvent, type ReactNode, type UIEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import BarcodePreview from '../../../components/BarcodePreview'
import PageContainer from '../../../components/PageContainer'
import { useStableRouteContext } from '../../../hooks/useStableRouteContext'
import {
  alignDomesticProductCode,
  applyContainerFloatRateByScope,
  applyContainerPricesByScope,
  assignContainerDetailCategoryByScope,
  batchUpdateDetails,
  deleteContainerDetailsByScope,
  getContainerDetailEditingPresence,
  heartbeatContainerDetailEditingPresence,
  leaveContainerDetailEditingPresence,
  previewContainerDetailAction,
  getContainerDetail,
  queryContainerProducts,
  recalculateContainerCostsByScope,
  setContainerDetailStatusByScope,
  translateHqProductNamesByContainerNumber,
  updateContainer,
} from '../../../services/containerService'
import SupplyNoticeModal from '../../../components/SupplyNotice/SupplyNoticeModal'
import type { SupplyNoticeInput } from '../../../types/supplyNotice'
import {
  buildContainerCreateProductsOperationId,
  buildContainerSubmitOperationId,
  createContainerProductCreationJob,
  createContainerSubmitJob,
  waitForContainerProductCreationJob,
  waitForContainerSubmitJob,
  type ContainerProductCreationJob,
  type ContainerProductCreationResultItem,
} from '../../../services/containerProductCreationService'
import { exportContainerDetailsToExcel, exportContainerDetailsToPdf, type ContainerDetailExportItem, type ContainerExportOptions } from '../../../services/exportService'
import {
  buildPushProductsToHqOperationId,
  createPushProductsToHqJob,
  getPushProductsToHqJob,
  type PushProductsToHqJobResult,
} from '../../../services/posProductService'
import { createHqSyncJobPoller } from '../../../services/productHqSyncPolling'
import { upsertForActiveStores as upsertMultiCodeForActiveStores, type StoreMultiCodePriceUpsertActiveItem } from '../../../services/storeMultiCodePriceService'
import { upsertForActiveStores as upsertRetailForActiveStores, type StoreRetailPriceUpsertActiveItem } from '../../../services/storeRetailPriceService'
import { batchTranslate } from '../../../services/translationService'
import {
  getCategoryTree,
  type WarehouseCategoryNode,
} from '../../../services/warehouseCategoryService'
import {
  batchUpdateWarehouseProducts,
  detectProducts,
  type WarehouseProductBatchUpdateItem,
} from '../../../services/warehouseProductService'
import { useAuthStore } from '../../../store/auth'
import { useTabsStore } from '../../../store/tabs'
import { P } from '../../../types/permissions'
import type { ContainerDetail, ContainerDetailBatchScope, ContainerDetailEditingPresence, ContainerDomesticSetCodeItem, ContainerMain, HqTranslationResult, UpdateContainerDetailRequest, UpdateContainerRequest } from '../../../types/container'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { shouldShowDetailInitialLoading, shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'
import {
  applyPendingContainerDetailPatches,
  applyContainerDetailEnglishNameUpdates,
  applyContainerDetailColumnState,
  applyContainerDetailWarehouseStatusByProductCodes,
  buildContainerDetailQuery,
  buildContainerDetailClearEnglishNameUpdates,
  buildContainerDetailDetectionItems,
  buildContainerDetailEnglishNameUpdates,
  buildContainerDetailExportRows,
  buildContainerDetailMatchedDomesticDataUpdates,
  buildContainerDetailSaveFailureKeys,
  buildContainerDetailSuccessfulEnglishNameUpdates,
  buildPendingContainerDetailSavePlan,
  buildContainerDetailTagStats,
  buildContainerDetailHqPushSelection,
  calculateContainerFreight,
  calculateContainerDetailImportPrice,
  calculateContainerDetailTotalAmount,
  markContainerDetailUpdatesSkipRelatedProductSync,
  calculateContainerDetailTotalVolume,
  calculateContainerDetailTransportCost,
  calculateContainerDetailUnitTransportCost,
  canReuseContainerDetailInitialPage,
  CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
  CONTAINER_DETAIL_FULL_LOAD_LIMIT,
  CONTAINER_DETAIL_INITIAL_PAGE_SIZE,
  CONTAINER_DETAIL_PAGE_SIZE_OPTIONS,
  DEFAULT_CONTAINER_DETAIL_FLOAT_RATE,
  deriveContainerFreightInput,
  getContainerDetailCostMissingFields,
  getContainerDetailBatchCategoryProductCodes,
  buildContainerDetailTranslationUpdates,
  calculateContainerDetailTableScrollY,
  countContainerDetailInvalidTranslationResults,
  countSuccessfullySavedContainerDetailRows,
  extractPushToHqErrorResult,
  findContainerDetailRowsMissingCreateProductRetailPrice,
  findContainerDetailRowsMissingProductName,
  getContainerDetailEditableColumnKeysInOrder,
  getContainerDetailExportColumns,
  applyContainerDetailLocalExportValues,
  getContainerDetailBarcode,
  getContainerDetailCategoryGuid,
  getContainerDetailConflictServerValue,
  filterSuccessfullySavedContainerDetailUpdates,
  isContainerDetailActionPreviewExpired,
  getSubmittedContainerDetailFields,
  getContainerDetailEnglishName,
  getContainerDetailImageUrl,
  getContainerDetailItemNumber,
  getContainerDetailLocalProductCode,
  getContainerDetailDomesticProductCode,
  getContainerDetailRealtimeImportPrice,
  getContainerDetailRealtimeRetailPrice,
  getContainerDetailVisibleOemPrice,
  getPendingContainerDetailEnglishNameError,
  getContainerDetailMatchType,
  getContainerDetailProductCode,
  getContainerDetailProductName,
  getContainerDetailProductType,
  getContainerDetailTranslationSource,
  getContainerDetailWarehouseStatusFilterKey,
  getNextUpdateFieldSelection,
  getNextContainerDetailEditableCell,
  getUpdateFieldSelectionState,
  hasContainerDetailProductCodeConflict,
  isContainerDetailColumnOrderCustomized,
  isContainerDetailContainerNewProduct,
  isContainerDetailCreatedContainerNewProduct,
  isContainerDetailSortField,
  isValidContainerFreightVolume,
  normalizeContainerFreightInput,
  mergeContainerDetailColumnOrder,
  mergeContainerDetailLoadedItems,
  moveContainerDetailColumnOrder,
  mergeContainerDetailPatch,
  mergePendingContainerDetailPatch,
  matchesContainerDetailSelectedTags,
  prepareContainerDetailWholeExportRows,
  rollbackContainerDetailWarehouseStatuses,
  resolveContainerFreightPreview,
  settleScopedContainerDetailSave,
  resolveContainerDetailFullPage,
  resolveContainerDetailInitialPage,
  resolveContainerDetailPendingPriceOnBlur,
  CONTAINER_DETAIL_EXPORT_COLUMNS,
  ALL_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS,
  DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS,
  DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMN_KEYS,
  type ContainerDetailEditableCellDirection,
  type ContainerFreightInputMode,
  type ContainerDetailColumnFilters,
  type ContainerDetailCostMissingField,
  type ContainerDetailExportColumnKey,
  type ContainerDetailLoadMode,
  type ContainerDetailMatchTypeFilter,
  type ContainerDetailNewProductFilter,
  type ContainerDetailNumberRangeFilter,
  type ContainerDetailProductTypeFilter,
  type ContainerDetailSortField,
  type ContainerDetailSortState,
  type ContainerDetailTableColumnKey,
  type ContainerDetailTagFilter,
  type ContainerDetailTagStats,
  type ContainerDetailWarehouseStatusFilter,
  type ContainerDetailSaveValidationError,
  type PendingContainerDetailPatch,
  type PendingContainerDetailPatchMap,
  type PendingContainerDetailSavePlan,
} from './containerDetailLogic'
import { buildWarehouseCategoryLookup, formatWarehouseCategoryNodeName, getWarehouseProductCategoryTooltip } from '../Products/categoryPath'
import CategoryTreePicker from '../Products/CategoryTreePicker'
import WarehouseProductChangeHistoryDrawer from '../Products/WarehouseProductChangeHistoryDrawer'
import {
  defaultPushProductsToHqUpdateFields,
  pushProductsToHqUpdateFieldOptions,
} from '../../../types/posProduct'
import type { PushProductsToHqResult, PushProductsToHqUpdateField } from '../../../types/posProduct'
import ContainerCategoryManageModal from './ContainerCategoryManageModal'
import {
  resolveContainerCategoryTargetAfterMutation,
  resolveContainerCategorySelectionAfterRefresh,
  type ContainerCategoryChange,
} from './containerCategoryManageLogic'
import ContainerTagFilters from './ContainerTagFilters'
import {
  buildContainerDetailDraftStorageKey,
  captureContainerDetailDraftFieldBaselineTokens,
  captureContainerDetailDraftFieldVersions,
  captureSuccessfullySavedContainerDetailDraftFieldVersions,
  buildContainerDetailOverrideAcknowledgements,
  clearContainerDetailOverrideAcknowledgements,
  clearContainerDetailDraftFieldsIfVersionMatches,
  createContainerDetailDraftLocateResetPlan,
  clearContainerDetailDraftFailuresForPatches,
  countPendingContainerDetailFields,
  getContainerDetailDraftFieldFailure,
  getContainerDetailDraftExternalApplyMode,
  markContainerDetailDraftSaveFailure,
  mergeContainerDetailDraftNewerFields,
  readContainerDetailDraft,
  reconcileContainerDetailDraftFailures,
  refreshContainerDetailDraftFieldVersions,
  scopeContainerDetailRowsToContainer,
  settleContainerDetailDraftSaveSuccess,
  shouldConsumePendingContainerDetailLocate,
  shouldRetryPendingContainerDetailLocateReset,
  writeContainerDetailDraft,
  type ContainerDetailDraftFailureMap,
  type ContainerDetailDraftState,
  type ContainerDetailDraftStorage,
  type ContainerDetailVersionedOverrideAcknowledgement,
} from './containerDetailDraft'
import {
  applyContainerDetailAutoSavePatches,
  buildContainerDetailAutoSaveContextKey,
  createContainerDetailAutoSaveQueue,
  isContainerDetailAutoSaveContextCurrent,
  resolveContainerDetailAutoSaveLifecycleAction,
  type ContainerDetailAutoSaveFailure,
  type ContainerDetailAutoSaveField,
  type ContainerDetailAutoSaveIntent,
  type ContainerDetailAutoSavePatch,
  type ContainerDetailAutoSaveQueue,
  type ContainerDetailAutoSaveSnapshot,
} from './containerDetailAutoSaveQueue'
import {
  CONTAINER_DETAIL_COLUMN_PASTE_KEYS,
  CONTAINER_DETAIL_COLUMN_PASTE_TARGETS,
  buildContainerDetailColumnPastePlan,
  isContainerDetailColumnPasteKey,
  parseContainerDetailColumnPasteText,
  type ContainerDetailColumnPasteEntry,
  type ContainerDetailColumnPasteKey,
} from './containerDetailColumnPaste'
import useContainerSetCode from './useContainerSetCode'
import {
  collectCategoryExpandedKeys,
  findWarehouseCategory,
  renderColumnTitle,
  renderCompactHeader,
  renderContainerDetailCategoryCell,
  renderImportPriceCell,
  renderNumericCell,
  renderOemPriceCell,
  renderReadonlyOemPriceCell,
} from './ContainerDetailColumns'
import './index.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

type TextColumnFilterKey = 'itemNumber' | 'barcode' | 'productName' | 'englishName' | 'remark'
type NumberColumnFilterKey = 'containerPieces' | 'middlePackQuantity' | 'containerQuantity' | 'packingQuantity' | 'unitVolume' | 'domesticPrice' | 'floatRate' | 'transportCost' | 'unitTransportCost' | 'warehouseImportPrice' | 'lastOEMPrice' | 'importPrice' | 'oemPrice'
type EnumColumnFilterKey = 'productTypes' | 'newProductStates' | 'matchTypes' | 'warehouseStatus'
type PendingContainerDetailPageSavePlan = PendingContainerDetailSavePlan & {
  saveKeys: string[]
  draftContext: ContainerDetailDraftContext
  draftFieldVersionSnapshot: Record<string, string>
  expectedServerFieldTokens: Record<string, Record<string, string>>
  overrideAcknowledgements: Record<string, Record<string, string>>
}
type PendingContainerDetailSaveExecutionResult = {
  isCurrent: boolean
  successfulFieldKeys: string[]
}
type ContainerDetailFieldConflict = {
  hguid: string
  field: string
  serverValue: unknown
  submittedValue: unknown
  currentServerFieldToken: string
  code?: string
  message?: string
}
type ContainerDetailDraftContext = {
  userGuid: string
  containerGuid: string
  draftIdentity: string
}
type ContainerDetailAutoSaveContextSnapshot = ContainerDetailDraftContext & {
  rows: ContainerDetail[]
  container: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null
  fieldVersions: Record<string, string>
  fieldBaselineTokens: Record<string, string>
}
type ContainerDetailExportFormat = 'excel' | 'pdf'
type ContainerDetailExportScope = 'selectionOrFiltered' | 'wholeContainer'
type ContainerExistingProductUpdateField =
  | 'domesticPrice'
  | 'importPrice'
  | 'oemPrice'
  | 'volume'
  | 'storePurchasePrice'
  | 'storeRetailPrice'
  | 'storeMultiCodePurchasePrice'
  | 'storeMultiCodeRetailPrice'

interface UpdateFieldOption<T extends string> {
  value: T
  labelKey: string
  fallbackLabel: string
}

type BatchActionConfirmOptions = {
  danger?: boolean
  extra?: ReactNode
  beforeConfirm?: () => boolean
}

interface UpdateFieldSelectorProps<T extends string> {
  t: TFunction
  fields: readonly UpdateFieldOption<T>[]
  defaultFields: T[]
  onChange: (values: T[]) => void
  hint?: ReactNode
}

function formatDate(value?: string) {
  return value ? dayjs(value).format('YYYY-MM-DD') : '--'
}

function formatNumber(value?: number, digits = 2) {
  return value == null ? '--' : value.toLocaleString('zh-CN', { maximumFractionDigits: digits, minimumFractionDigits: digits })
}

function formatCurrency(value?: number, symbol = '$', digits = 2) {
  const formatted = formatNumber(value, digits)
  return formatted === '--' ? formatted : `${symbol}${formatted}`
}

function formatContainerDetailPresenceUser(user: ContainerDetailEditingPresence['viewers'][number], now = Date.now()) {
  const activeAt = Date.parse(user.lastActiveAt)
  if (Number.isNaN(activeAt)) return `${user.userName}（刚刚）`
  const minutes = Math.max(0, Math.floor((now - activeAt) / 60_000))
  const recent = minutes < 1 ? '刚刚' : minutes < 60 ? `${minutes} 分钟前` : dayjs(activeAt).format('HH:mm')
  return `${user.userName}（${recent}）`
}

function getContainerDetailPresenceTitle(users: ContainerDetailEditingPresence['viewers']) {
  return users.map((user) => `${user.userName}：${dayjs(user.lastActiveAt).format('YYYY-MM-DD HH:mm:ss')}`).join('；')
}

function rowKey(row: ContainerDetail) {
  return row.hguid || String(row.id)
}

function buildContainerDetailAutoSaveUpdate(
  row: ContainerDetail,
  patch: ContainerDetailAutoSavePatch,
  container: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null,
): UpdateContainerDetailRequest {
  const nextRow = mergeContainerDetailPatch(row, patch)
  const update: UpdateContainerDetailRequest = { hguid: row.hguid, ...patch }
  const updatesPackageMetric = '单件装箱数' in patch || '单件体积' in patch
  const updatesCost = updatesPackageMetric || '调整浮率' in patch

  if (updatesPackageMetric && nextRow.装柜件数 != null && nextRow.单件装箱数 != null) {
    update.装柜数量 = Number((nextRow.装柜件数 * nextRow.单件装箱数).toFixed(2))
  }

  if (updatesPackageMetric) {
    const volumeRow = mergeContainerDetailPatch(nextRow, update as Partial<ContainerDetail>)
    update.合计装柜体积 = calculateContainerDetailTotalVolume(volumeRow)
    update.合计装柜金额 = calculateContainerDetailTotalAmount(volumeRow)
  }

  if (updatesCost) {
    const pricedRow = mergeContainerDetailPatch(nextRow, update as Partial<ContainerDetail>)
    const transportCost = calculateContainerDetailTransportCost(pricedRow, container)
    update.运输成本 = transportCost
    update.进口价格 = calculateContainerDetailImportPrice(
      { ...pricedRow, 运输成本: transportCost },
      container,
      pricedRow.调整浮率 ?? DEFAULT_CONTAINER_DETAIL_FLOAT_RATE,
      transportCost,
    )
    // 包装、体积或浮率联动的进货价只更新货柜明细，不能覆盖仓库人工价。
    update.SkipRelatedProductSync = true
  }

  return update
}

function getContainerDetailDraftStorage(): ContainerDetailDraftStorage | null {
  if (typeof window === 'undefined') return null
  try {
    return window.localStorage
  } catch {
    return null
  }
}

const containerStatusOptions = [
  { value: 0, color: 'blue', labelKey: 'loaded' },
  { value: 1, color: 'orange', labelKey: 'inTransit' },
  { value: 2, color: 'success', labelKey: 'completed' },
  { value: 7, color: 'error', labelKey: 'cancelled' },
] as const

const containerExistingProductUpdateFields: readonly UpdateFieldOption<ContainerExistingProductUpdateField>[] = [
  { value: 'domesticPrice', labelKey: 'containers.updateFields.domesticPrice', fallbackLabel: '国内价格（仓库主表）' },
  { value: 'importPrice', labelKey: 'containers.updateFields.importPrice', fallbackLabel: '进口价（商品/仓库主表）' },
  { value: 'oemPrice', labelKey: 'containers.updateFields.oemPrice', fallbackLabel: '零售价（仓库主表）' },
  { value: 'volume', labelKey: 'containers.updateFields.volume', fallbackLabel: '单件体积（仓库主表）' },
  { value: 'storePurchasePrice', labelKey: 'containers.updateFields.storePurchasePrice', fallbackLabel: '分店进货价' },
  { value: 'storeRetailPrice', labelKey: 'containers.updateFields.storeRetailPrice', fallbackLabel: '分店零售价' },
  { value: 'storeMultiCodePurchasePrice', labelKey: 'containers.updateFields.storeMultiCodePurchasePrice', fallbackLabel: '分店多码进货价' },
  { value: 'storeMultiCodeRetailPrice', labelKey: 'containers.updateFields.storeMultiCodeRetailPrice', fallbackLabel: '分店多码零售价' },
]

const defaultContainerExistingProductUpdateFields: ContainerExistingProductUpdateField[] = [
  'importPrice',
  'oemPrice',
  'storePurchasePrice',
  'storeRetailPrice',
  'storeMultiCodePurchasePrice',
  'storeMultiCodeRetailPrice',
]

function UpdateFieldSelector<T extends string>({
  t,
  fields,
  defaultFields,
  onChange,
  hint,
}: UpdateFieldSelectorProps<T>) {
  const allValues = useMemo(() => fields.map((field) => field.value), [fields])
  const [selectedFields, setSelectedFields] = useState<T[]>(defaultFields)
  const { isAllSelected, isPartiallySelected } = getUpdateFieldSelectionState(selectedFields, allValues)

  const updateSelectedFields = (values: T[]) => {
    setSelectedFields(values)
    onChange(values)
  }

  return (
    <Space direction="vertical" size={8}>
      <Typography.Text strong>
        {t('containers.updateFields.title', '选择要更新的字段')}
      </Typography.Text>
      {hint}
      <Checkbox
        indeterminate={isPartiallySelected}
        checked={isAllSelected}
        onChange={(event) => updateSelectedFields(getNextUpdateFieldSelection(event.target.checked, allValues))}
      >
        {t('common.selectAll', '全选')}
      </Checkbox>
      <Checkbox.Group
        value={selectedFields}
        onChange={(values) => updateSelectedFields(values.map(String) as T[])}
      >
        <Space direction="vertical" size={4}>
          {fields.map((field) => (
            <Checkbox key={field.value} value={field.value}>
              {t(field.labelKey, field.fallbackLabel)}
            </Checkbox>
          ))}
        </Space>
      </Checkbox.Group>
    </Space>
  )
}

function getStatusTag(status: number | undefined, t: TFunction) {
  if (status == null) return <Tag>{t('containers.status.unknown')}</Tag>
  const item = containerStatusOptions.find((option) => option.value === status)
  return item ? <Tag color={item.color}>{t(`containers.status.${item.labelKey}`)}</Tag> : <Tag>{t('containers.status.unknownWithCode', { status })}</Tag>
}

function getContainerStatusText(status: number | undefined, t: TFunction) {
  if (status == null) return t('containers.status.unknown')
  const item = containerStatusOptions.find((option) => option.value === status)
  return item ? t(`containers.status.${item.labelKey}`) : t('containers.status.unknownWithCode', { status })
}

function getProductTypeLabel(value: string | undefined, t: TFunction) {
  const type = value || '普通商品'
  const map: Record<string, string> = {
    全部: 'common.all',
    普通商品: 'containers.productTypes.normal',
    套装商品: 'containers.productTypes.set',
    套装子商品: 'containers.productTypes.setChild',
    多码商品: 'containers.productTypes.multiCode',
  }
  return map[type] ? t(map[type]) : type
}

function getProductTypeTagColor(value: string | undefined) {
  if (value === '套装商品') return 'blue'
  if (value === '多码商品') return 'purple'
  if (value === '套装子商品') return 'orange'
  return 'default'
}

function getSetCodeRowKey(item: ContainerDomesticSetCodeItem) {
  return item.setProductCode || item.barcode || item.setItemNumber || ''
}

function getProductTypeFilterLabel(value: ContainerDetailProductTypeFilter, t: TFunction) {
  const map: Record<ContainerDetailProductTypeFilter, string> = {
    normal: 'containers.productTypes.normal',
    set: 'containers.productTypes.set',
    multi: 'containers.productTypes.multiCode',
    setChild: 'containers.productTypes.setChild',
  }
  return t(map[value])
}

function getMatchTypeLabel(value: ContainerDetailMatchTypeFilter, t: TFunction) {
  const map: Record<ContainerDetailMatchTypeFilter, string> = {
    productCode: 'containers.matchTypes.productCode',
    supplierItem: 'containers.matchTypes.supplierItem',
    unmatched: 'containers.matchTypes.unmatched',
  }
  return t(map[value])
}

function getMatchTypeTagColor(value: ContainerDetailMatchTypeFilter) {
  if (value === 'productCode') return 'green'
  if (value === 'supplierItem') return 'gold'
  return 'red'
}

function CopyableText({ value }: { value?: string }) {
  const { t } = useTranslation()

  if (!value) {
    return <>--</>
  }

  return (
    <span className="container-detail-nowrap container-detail-copyable">
      <Typography.Text ellipsis={{ tooltip: value }}>
        {value}
      </Typography.Text>
      <Tooltip title={t('common.copy', 'Copy')}>
        <Button
          size="small"
          type="text"
          aria-label={t('common.copyValue', 'Copy {{value}}', { value })}
          icon={<CopyOutlined />}
          className="container-detail-copy-button"
          onClick={(event) => {
            event.stopPropagation()
            void copyTextToClipboard(value)
          }}
        />
      </Tooltip>
    </span>
  )
}

// TwoLineText — 表格密集显示专用：关键文本限制两行，数字列保持单行便于快速扫读。
function TwoLineText({ value }: { value?: string }) {
  if (!value) {
    return <>--</>
  }

  return (
    <Tooltip title={value}>
      <span className="container-detail-two-line-text">{value}</span>
    </Tooltip>
  )
}



const CONTAINER_DETAIL_TABLE_SCROLL_X = 2440
const CONTAINER_DETAIL_TABLE_SCROLL_Y = 620
const CONTAINER_DETAIL_SELECTION_COLUMN_WIDTH = 56
const CONTAINER_DETAIL_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.containerDetail.columnOrder.v3'
const CONTAINER_DETAIL_COLUMN_WIDTH_STORAGE_KEY = 'hbweb_rv.containerDetail.columnWidths.v1'
const CONTAINER_DETAIL_MIN_COLUMN_WIDTH = 48
const CONTAINER_DETAIL_MAX_COLUMN_WIDTH = 420
const DEFAULT_CONTAINER_DETAIL_SORT: ContainerDetailSortState = { field: 'itemNumber', order: 'ascend' }
const CONTAINER_DETAIL_EDITABLE_COLUMN_KEYS = ['englishName', 'packingQuantity', 'unitVolume', 'middlePackQuantity', 'floatRate', 'importPrice', 'oemPrice', 'remark'] as const
const WHOLE_CONTAINER_DETAIL_EXPORT_LABEL_KEYS: Partial<Record<ContainerDetailExportColumnKey, string>> = {
  index: 'containers.columns.index',
  productName: 'containers.fields.productName',
  containerPieces: 'containers.fields.containerPieces',
  containerQuantity: 'containers.fields.containerQuantity',
}
const EMPTY_CONTAINER_DETAIL_TAG_STATS = {
  all: 0,
  new: 0,
  existing: 0,
  noOemPrice: 0,
  abnormalImport: 0,
  active: 0,
  inactive: 0,
  normal: 0,
  set: 0,
  multi: 0,
  setChild: 0,
  productCodeMatched: 0,
  supplierItemMatched: 0,
  unmatched: 0,
}

type ContainerDetailEditableColumnKey = typeof CONTAINER_DETAIL_EDITABLE_COLUMN_KEYS[number]
type ContainerDetailColumnWidthMap = Partial<Record<ContainerDetailTableColumnKey, number>>
type ContainerDetailFocusableCell = {
  focus: (options?: FocusOptions) => void
  select?: () => void
}

const containerDetailEditableDirectionByKey: Partial<Record<string, ContainerDetailEditableCellDirection>> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
}

function buildContainerDetailEditableCellKey(rowKeyValue: string, columnKey: ContainerDetailEditableColumnKey) {
  return `${rowKeyValue}:${columnKey}`
}

function clampContainerDetailColumnWidth(width: number) {
  return Math.max(CONTAINER_DETAIL_MIN_COLUMN_WIDTH, Math.min(CONTAINER_DETAIL_MAX_COLUMN_WIDTH, Math.round(width)))
}

function normalizeContainerDetailColumnWidths(value: unknown, allowedKeys: readonly ContainerDetailTableColumnKey[]): ContainerDetailColumnWidthMap {
  if (!value || typeof value !== 'object') {
    return {}
  }

  const allowedSet = new Set(allowedKeys)
  const nextWidths: ContainerDetailColumnWidthMap = {}
  Object.entries(value as Record<string, unknown>).forEach(([key, width]) => {
    if (!allowedSet.has(key as ContainerDetailTableColumnKey) || typeof width !== 'number' || !Number.isFinite(width)) {
      return
    }
    nextWidths[key as ContainerDetailTableColumnKey] = clampContainerDetailColumnWidth(width)
  })
  return nextWidths
}

function areContainerDetailColumnWidthsEqual(a: ContainerDetailColumnWidthMap, b: ContainerDetailColumnWidthMap) {
  const aKeys = Object.keys(a)
  const bKeys = Object.keys(b)
  return aKeys.length === bKeys.length && aKeys.every((key) => a[key as ContainerDetailTableColumnKey] === b[key as ContainerDetailTableColumnKey])
}

function getContainerDetailViewport() {
  if (typeof window === 'undefined') {
    return {
      height: CONTAINER_DETAIL_TABLE_SCROLL_Y,
      isSmallLandscape: false,
      isSmallPortrait: false,
    }
  }

  return {
    height: window.innerHeight,
    isSmallLandscape: window.matchMedia('(max-height: 500px) and (orientation: landscape)').matches,
    isSmallPortrait: window.matchMedia('(max-width: 767px) and (orientation: portrait)').matches,
  }
}

function useContainerDetailViewport() {
  const [viewport, setViewport] = useState(getContainerDetailViewport)

  useEffect(() => {
    const updateViewport = () => setViewport(getContainerDetailViewport())

    window.addEventListener('resize', updateViewport)
    window.addEventListener('orientationchange', updateViewport)
    return () => {
      window.removeEventListener('resize', updateViewport)
      window.removeEventListener('orientationchange', updateViewport)
    }
  }, [])

  return viewport
}

interface DraggableHeaderCellProps extends HTMLAttributes<HTMLTableCellElement> {
  'data-column-key'?: string
  'data-column-width'?: number
  onColumnResizeStart?: (columnKey: ContainerDetailTableColumnKey, width: number, event: ReactPointerEvent<HTMLSpanElement>) => void
}

function DraggableHeaderCell({ children, style, onColumnResizeStart, ...props }: DraggableHeaderCellProps) {
  const columnKey = props['data-column-key']
  const columnWidth = props['data-column-width']
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: columnKey ?? '__container-detail-static-column__',
    disabled: !columnKey,
  })

  if (!columnKey) {
    return <th style={style} {...props}>{children}</th>
  }

  const headerStyle: CSSProperties = {
    ...style,
    transform: CSS.Translate.toString(transform),
    transition,
    position: 'relative',
    zIndex: isDragging ? 3 : style?.zIndex,
    opacity: isDragging ? 0.85 : style?.opacity,
  }

  return (
    <th ref={setNodeRef} style={headerStyle} {...props}>
      <div className="container-detail-draggable-header" {...attributes} {...listeners}>
        {children}
      </div>
      <span
        className="container-detail-column-resize-handle"
        aria-hidden="true"
        onPointerDown={(event) => {
          event.preventDefault()
          event.stopPropagation()
          if (!columnKey || typeof columnWidth !== 'number') return
          onColumnResizeStart?.(columnKey as ContainerDetailTableColumnKey, columnWidth, event)
        }}
      />
    </th>
  )
}

export default function ContainerDetailPage() {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const route = useStableRouteContext()
  const { active } = useKeepAliveContext()
  // 移动端布局可能复用 route element，货柜 GUID 必须每次跟随当前 URL。
  const containerGuid = route?.params.containerGuid || ''
  const viewport = useContainerDetailViewport()
  const access = useAuthStore((state) => state.access)
  const currentUserGuid = useAuthStore((state) => state.currentUser?.userGUID ?? '')
  const updateTabTitle = useTabsStore((state) => state.updateTabTitle)
  // 记录当前货柜已完成首次加载，保活 Tab 恢复时保留旧内容并静默刷新。
  const loadedContainerGuidRef = useRef<string | null>(null)
  const visibleContainerGuidRef = useRef<string | null>(null)
  const lastLoadedContainerDetailSuccessRef = useRef<{
    containerGuid: string
    queryKey: string
    generation: number
  } | null>(null)
  const headerLoadRequestIdRef = useRef(0)
  const containerDetailLoadRequestIdRef = useRef(0)
  const containerDetailReconcileGenerationRef = useRef(0)
  const [loading, setLoading] = useState(false)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailItemsTotal, setDetailItemsTotal] = useState(0)
  const [detailPagingState, setDetailPagingState] = useState<{
    containerGuid: string
    mode: ContainerDetailLoadMode
    pageNumber: number
    pageSize: number
    scopeKey: string
  }>(() => ({
    containerGuid,
    mode: 'probe',
    pageNumber: 1,
    pageSize: CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
    scopeKey: '',
  }))
  const [remoteTagStats, setRemoteTagStats] = useState<ContainerDetailTagStats>(EMPTY_CONTAINER_DETAIL_TAG_STATS)
  const [savingHeader, setSavingHeader] = useState(false)
  const savingHeaderRef = useRef(false)
  const [container, setContainer] = useState<ContainerMain | null>(null)
  const [rows, setRows] = useState<ContainerDetail[]>([])
  // 下架前的供货说明弹窗：以 Promise 形式等用户填完（或取消），单行开关与批量下架共用。
  const [supplyNoticeRequest, setSupplyNoticeRequest] = useState<{ productCount: number; resolve: (notice: SupplyNoticeInput | null) => void } | null>(null)
  const requestSupplyNotice = (productCount: number) =>
    new Promise<SupplyNoticeInput | null>((resolve) => setSupplyNoticeRequest({ productCount, resolve }))
  const [changeHistoryProduct, setChangeHistoryProduct] = useState<{
    productCode: string
    itemNumber?: string
    productName?: string
  } | null>(null)
  const [pendingDetailPatches, setPendingDetailPatches] = useState<PendingContainerDetailPatchMap>({})
  const [pendingDetailFailures, setPendingDetailFailures] = useState<ContainerDetailDraftFailureMap>({})
  const [pendingDetailConflicts, setPendingDetailConflicts] = useState<ContainerDetailFieldConflict[]>([])
  const [conflictDrawerOpen, setConflictDrawerOpen] = useState(false)
  const [editingPresence, setEditingPresence] = useState<ContainerDetailEditingPresence>({ viewers: [], editors: [] })
  const [editingPresenceAvailable, setEditingPresenceAvailable] = useState(false)
  const [isContainerDetailFieldFocused, setIsContainerDetailFieldFocused] = useState(false)
  const hasPendingConcurrencyConflicts = pendingDetailConflicts.length > 0
  const pendingDetailFieldCount = countPendingContainerDetailFields(pendingDetailPatches)
  const [selectedRowKeys, setSelectedRowKeys] = useState<Key[]>([])
  const [selectedTagFilters, setSelectedTagFilters] = useState<ContainerDetailTagFilter[]>([])
  const [categories, setCategories] = useState<WarehouseCategoryNode[]>([])
  const [categoryLoading, setCategoryLoading] = useState(false)
  const [categoryExpandedKeys, setCategoryExpandedKeys] = useState<string[]>([])
  const [columnFilters, setColumnFilters] = useState<ContainerDetailColumnFilters>({})
  // 默认按货号升序展示，保证每次打开货柜明细时列表顺序稳定。
  const [sortState, setSortState] = useState<ContainerDetailSortState>(DEFAULT_CONTAINER_DETAIL_SORT)
  const [columnOrder, setColumnOrder] = useState<ContainerDetailTableColumnKey[]>([])
  const [columnWidths, setColumnWidths] = useState<ContainerDetailColumnWidthMap>({})
  const [showReadonlyOemPrice, setShowReadonlyOemPrice] = useState(false)
  const [batchFloatRate, setBatchFloatRate] = useState<number | null>(null)
  const [batchImportPrice, setBatchImportPrice] = useState<number | null>(null)
  const [batchOemPrice, setBatchOemPrice] = useState<number | null>(null)
  const [batchFloatRateModalOpen, setBatchFloatRateModalOpen] = useState(false)
  const [batchFloatRateSaving, setBatchFloatRateSaving] = useState(false)
  const [batchPricesModalOpen, setBatchPricesModalOpen] = useState(false)
  const [batchPricesSaving, setBatchPricesSaving] = useState(false)
  const [batchModalTargetCount, setBatchModalTargetCount] = useState(0)
  const [batchModalScopeRows, setBatchModalScopeRows] = useState<ContainerDetail[]>([])
  const [batchEnglishName, setBatchEnglishName] = useState('')
  const [batchEnglishNameModalOpen, setBatchEnglishNameModalOpen] = useState(false)
  const [batchCategoryOpen, setBatchCategoryOpen] = useState(false)
  const [targetCategoryGuid, setTargetCategoryGuid] = useState<string>()
  const [batchCategorySaving, setBatchCategorySaving] = useState(false)
  const [rowCategoryOpen, setRowCategoryOpen] = useState(false)
  const [rowCategoryEditingRow, setRowCategoryEditingRow] = useState<ContainerDetail | null>(null)
  const [rowTargetCategoryGuid, setRowTargetCategoryGuid] = useState<string>()
  const [rowCategorySaving, setRowCategorySaving] = useState(false)
  const [categoryManageOpen, setCategoryManageOpen] = useState(false)
  const [categoryManageContext, setCategoryManageContext] = useState<'batch' | 'row' | null>(null)
  const [editingProductNameRowKey, setEditingProductNameRowKey] = useState<string | null>(null)
  const [editingProductNameValue, setEditingProductNameValue] = useState('')
  const [exporting, setExporting] = useState(false)
  const [exportProgress, setExportProgress] = useState(0)
  const [exportProgressMessage, setExportProgressMessage] = useState('')
  const [exportColumnModalOpen, setExportColumnModalOpen] = useState(false)
  const [exportFormat, setExportFormat] = useState<ContainerDetailExportFormat>('excel')
  const [selectedExportColumnKeys, setSelectedExportColumnKeys] = useState<ContainerDetailExportColumnKey[]>(DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS)
  const [hqTranslating, setHqTranslating] = useState(false)
  const [pushToHqLoading, setPushToHqLoading] = useState(false)
  const [detailSaveSubmitting, setDetailSaveSubmitting] = useState(false)
  // 「粘贴列」弹窗：目标列、起始行（空表示当前显示的第一行）与待解析的 Excel 列文本。
  const [columnPasteModalOpen, setColumnPasteModalOpen] = useState(false)
  const [columnPasteColumnKey, setColumnPasteColumnKey] = useState<ContainerDetailColumnPasteKey>('importPrice')
  const [columnPasteStartRowKey, setColumnPasteStartRowKey] = useState<string | null>(null)
  const [columnPasteText, setColumnPasteText] = useState('')
  const [matchDomesticDataLoading, setMatchDomesticDataLoading] = useState(false)
  const [aligningDomesticProductDetailHguid, setAligningDomesticProductDetailHguid] = useState<string | null>(null)
  const [createProductsLoading, setCreateProductsLoading] = useState(false)
  const [submitContainerLoading, setSubmitContainerLoading] = useState(false)
  const [pendingDetailSaveCount, setPendingDetailSaveCount] = useState(0)
  const [autoSaveSnapshot, setAutoSaveSnapshot] = useState<ContainerDetailAutoSaveSnapshot>({
    pendingFieldCount: 0,
    runningFieldCount: 0,
    failureCount: 0,
    unsavedFieldCount: 0,
    failures: [],
  })
  const [pendingWarehouseStatusCodes, setPendingWarehouseStatusCodes] = useState<Set<string>>(() => new Set())
  // 套装码弹窗状态 — 由 useContainerSetCode hook 管理
  const {
    setCodeModalOpen,
    setCodeModalRow,
    setCodeItems,
    setCodeLoading,
    setCodeSaving,
    changedSetCodePriceItems,
    setCodeColumns,
    openSetCodeModal,
    closeSetCodeModal,
    saveSetCodePrices,
  } = useContainerSetCode({ canEditContainer: access.canEditContainer })
  const [detailTableRenderKey, setDetailTableRenderKey] = useState(0)
  const [gridContentElement, setGridContentElement] = useState<HTMLDivElement | null>(null)
  const [toolbarElement, setToolbarElement] = useState<HTMLDivElement | null>(null)
  const [tableRegionElement, setTableRegionElement] = useState<HTMLDivElement | null>(null)
  const [detailLayoutMetrics, setDetailLayoutMetrics] = useState({
    toolbarHeight: 0,
    tableChromeHeight: 0,
  })
  const pushToHqLoadingRef = useRef(false)
  const createProductsLoadingRef = useRef(false)
  const submitContainerLoadingRef = useRef(false)
  const detailAbortControllerRef = useRef<AbortController | null>(null)
  const detailResetRequestRef = useRef<AbortController | null>(null)
  const detailStatsAbortControllerRef = useRef<AbortController | null>(null)
  const detailStatsRequestIdRef = useRef(0)
  const detailStatsTimerRef = useRef<number | null>(null)
  const lastLoadedDetailStatsKeyRef = useRef<string | null>(null)
  const reloadCurrentDetailRef = useRef<() => Promise<void>>(async () => undefined)
  const currentContainerGuidRef = useRef(containerGuid)
  currentContainerGuidRef.current = containerGuid
  const rowsRef = useRef(rows)
  rowsRef.current = rows
  const containerRef = useRef(container)
  containerRef.current = container
  const pendingDetailPatchesRef = useRef<PendingContainerDetailPatchMap>({})
  const pendingDetailFailuresRef = useRef<ContainerDetailDraftFailureMap>({})
  const pendingDetailFieldVersionsRef = useRef<Record<string, string>>({})
  const pendingDetailFieldBaselineTokensRef = useRef<Record<string, string>>({})
  const pendingDetailOverrideAcknowledgementsRef = useRef<Record<string, ContainerDetailVersionedOverrideAcknowledgement>>({})
  const clientEditingSessionIdRef = useRef(`container-detail-${crypto.randomUUID?.() ?? Math.random().toString(36).slice(2)}`)
  const [isDetailDraftMemoryOnly, setIsDetailDraftMemoryOnly] = useState(false)
  const isDetailDraftMemoryOnlyRef = useRef(false)
  const detailRowsContainerGuidRef = useRef(containerGuid)
  const pendingDetailDraftIdentityRef = useRef('')
  const autoSaveContextKeyRef = useRef('')
  const autoSaveSnapshotRef = useRef(autoSaveSnapshot)
  autoSaveSnapshotRef.current = autoSaveSnapshot
  const restoredDetailDraftNoticeKeyRef = useRef('')
  const pendingDetailSavePromisesRef = useRef<Set<Promise<unknown>>>(new Set())
  const failedDetailSaveKeysRef = useRef<Set<string>>(new Set())
  const failedPendingDetailSaveKeysRef = useRef<Set<string>>(new Set())
  const ignoreProductNameBlurRef = useRef(false)
  const autoSaveEditBaselineRef = useRef<Map<string, unknown>>(new Map())
  const detailTableRef = useRef<TableRef | null>(null)
  const lastDetailTableScrollTopRef = useRef(0)
  const containerDetailTabActiveRef = useRef(active)
  containerDetailTabActiveRef.current = active
  const wasContainerDetailTabActiveRef = useRef(active)
  const detailTableRestoreFrameRef = useRef<number | null>(null)
  const editableCellRefs = useRef<Map<string, ContainerDetailFocusableCell>>(new Map())
  const pendingEditableCellFocusKeyRef = useRef<string | null>(null)
  const pendingDraftLocateAfterAppendRef = useRef<{
    awaitingUnfilteredReset: boolean
    queryKey: string
    generation: number
  } | null>(null)
  const autoSaveSendBatchRef = useRef<(
    contextKey: string,
    intents: ContainerDetailAutoSaveIntent[],
  ) => Promise<Awaited<ReturnType<typeof batchUpdateDetails>> | void>>(async () => undefined)
  const autoSaveSnapshotHandlerRef = useRef<(
    contextKey: string,
    snapshot: ContainerDetailAutoSaveSnapshot,
  ) => void>(() => undefined)
  const autoSaveContextSnapshotsRef = useRef<Map<string, ContainerDetailAutoSaveContextSnapshot>>(new Map())
  const updateAutoSaveContextRows = (
    contextKey: string,
    nextRows: ContainerDetail[],
    nextContainer: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null,
  ) => {
    const contextSnapshot = autoSaveContextSnapshotsRef.current.get(contextKey)
    if (!contextSnapshot) return
    autoSaveContextSnapshotsRef.current.set(contextKey, {
      ...contextSnapshot,
      rows: nextRows,
      container: nextContainer,
    })
  }
  const updateCurrentAutoSaveContextDraftMetadata = (
    fieldVersions: Record<string, string>,
    fieldBaselineTokens: Record<string, string>,
  ) => {
    const contextKey = autoSaveContextKeyRef.current
    const contextSnapshot = autoSaveContextSnapshotsRef.current.get(contextKey)
    if (!contextSnapshot || !isContainerDetailAutoSaveContextCurrent(
      contextKey,
      containerGuid,
      pendingDetailDraftIdentityRef.current,
    )) return
    autoSaveContextSnapshotsRef.current.set(contextKey, {
      ...contextSnapshot,
      userGuid: currentUserGuid,
      draftIdentity: pendingDetailDraftIdentityRef.current,
      fieldVersions: { ...fieldVersions },
      fieldBaselineTokens: { ...fieldBaselineTokens },
    })
  }
  const autoSaveQueueRef = useRef<ContainerDetailAutoSaveQueue | null>(null)
  if (!autoSaveQueueRef.current) {
    autoSaveQueueRef.current = createContainerDetailAutoSaveQueue({
      sendBatch: (contextKey, intents) => autoSaveSendBatchRef.current(contextKey, intents),
      onBatchSuccess: (contextKey) => {
        if (contextKey !== autoSaveContextKeyRef.current) return
        const activeItemLoad = detailAbortControllerRef.current
        if (!activeItemLoad || activeItemLoad.signal.aborted) return
        // running patch 仍在覆盖页面时先废弃旧 item query；各请求的 finally 继续负责正常结束 loading。
        activeItemLoad.abort()
        containerDetailLoadRequestIdRef.current += 1
        containerDetailReconcileGenerationRef.current += 1
      },
      onSnapshotChange: (contextKey, snapshot) => autoSaveSnapshotHandlerRef.current(contextKey, snapshot),
      onContextDisposed: (contextKey) => autoSaveContextSnapshotsRef.current.delete(contextKey),
      getRequestErrorMessage: (error) => (
        error instanceof Error && error.message
          ? error.message
          : t('containers.messages.detailSaveFailed', '货柜明细保存失败，请稍后重试')
      ),
    })
  }
  autoSaveSnapshotHandlerRef.current = (contextKey, snapshot) => {
    if (contextKey !== autoSaveContextKeyRef.current) return
    autoSaveSnapshotRef.current = snapshot
    setAutoSaveSnapshot(snapshot)
    setPendingDetailSaveCount(
      snapshot.pendingFieldCount
      + snapshot.runningFieldCount
      + pendingDetailSavePromisesRef.current.size,
    )
  }
  if (
    isContainerDetailAutoSaveContextCurrent(
      autoSaveContextKeyRef.current,
      containerGuid,
      pendingDetailDraftIdentityRef.current,
    )
    && lastLoadedContainerDetailSuccessRef.current?.containerGuid === containerGuid
    && visibleContainerGuidRef.current === containerGuid
  ) {
    updateAutoSaveContextRows(autoSaveContextKeyRef.current, rows, container)
  }
  const [headerEditing, setHeaderEditing] = useState(false)
  const [headerForm, setHeaderForm] = useState<{
    货柜编号?: string
    装柜日期?: Dayjs | null
    预计到岸日期?: Dayjs | null
    实际到货日期?: Dayjs | null
    汇率?: number
    备注?: string
    状态?: number
  }>({})
  const [freightInputMode, setFreightInputMode] = useState<ContainerFreightInputMode>('standard68')
  const [freightInputValue, setFreightInputValue] = useState<number>()
  const [freightInputDirty, setFreightInputDirty] = useState(false)
  const freightVolumeValid = isValidContainerFreightVolume(container?.总体积)
  const freightPreviewValue = useMemo(
    () => resolveContainerFreightPreview(
      container?.运费,
      freightInputValue,
      container?.总体积,
      freightInputMode,
      freightInputDirty,
    ),
    [container?.总体积, container?.运费, freightInputDirty, freightInputMode, freightInputValue],
  )

  const handleFreightInputModeChange = (nextMode: ContainerFreightInputMode) => {
    if (nextMode === freightInputMode) return
    // 模式切换只改变报价的显示口径，保留当前换算出的最终运费和修改状态。
    setFreightInputValue(normalizeContainerFreightInput(
      deriveContainerFreightInput(freightPreviewValue, container?.总体积, nextMode),
      nextMode,
    ))
    setFreightInputMode(nextMode)
  }

  const handleFreightInputChange = (value: number | null) => {
    const nextValue = value ?? undefined
    if (Object.is(nextValue, freightInputValue)) return
    setFreightInputValue(nextValue)
    setFreightInputDirty(true)
  }
  const columnDragSensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        distance: 6,
      },
    }),
  )

  const applyPendingDetailDraftState = (
    nextState: ContainerDetailDraftState,
    persist = true,
    changedPatches: PendingContainerDetailPatch[] = [],
    reloadRemovedFieldBaseline = false,
  ) => {
    const previousPendingPatches = pendingDetailPatchesRef.current
    const previousFailures = pendingDetailFailuresRef.current
    const previousFieldVersions = pendingDetailFieldVersionsRef.current
    const previousBaselineTokens = pendingDetailFieldBaselineTokensRef.current
    const failures = reconcileContainerDetailDraftFailures(
      nextState.failures,
      nextState.pendingPatches,
    )
    const fieldVersions = refreshContainerDetailDraftFieldVersions(
      nextState.pendingPatches,
      nextState.fieldVersions ?? pendingDetailFieldVersionsRef.current,
      changedPatches,
    )
    const fieldBaselineTokens = nextState.fieldBaselineTokens ?? previousBaselineTokens
    pendingDetailPatchesRef.current = nextState.pendingPatches
    pendingDetailFailuresRef.current = failures
    pendingDetailFieldVersionsRef.current = fieldVersions
    pendingDetailFieldBaselineTokensRef.current = fieldBaselineTokens
    updateCurrentAutoSaveContextDraftMetadata(fieldVersions, fieldBaselineTokens)
    // 草稿版本变化（跨标签同步、重新编辑、字段被删除）后，旧覆盖确认不得继续复用。
    pendingDetailOverrideAcknowledgementsRef.current = Object.fromEntries(
      Object.entries(pendingDetailOverrideAcknowledgementsRef.current).filter(([key, acknowledgement]) => (
        acknowledgement.fieldVersion === fieldVersions[key]
      )),
    )
    failedPendingDetailSaveKeysRef.current = new Set(Object.keys(failures))
    setPendingDetailPatches(nextState.pendingPatches)
    setPendingDetailFailures(failures)
    if (persist) {
      const removedFieldVersions = changedPatches.reduce<Record<string, string>>((next, patch) => {
        const currentPatch = nextState.pendingPatches[patch.hguid]
        getSubmittedContainerDetailFields(patch).forEach((field) => {
          const key = `${patch.hguid}:${field}`
          const currentValue = field === '英文名称'
            ? (currentPatch?.ClearEnglishName === true ? null : currentPatch?.英文名称)
            : currentPatch?.[field as keyof PendingContainerDetailPatch]
          if (currentValue === undefined && previousFieldVersions[key]) next[key] = previousFieldVersions[key]
        })
        return next
      }, {})
      const persisted = writeContainerDetailDraft(
        getContainerDetailDraftStorage(),
        currentUserGuid,
        containerGuid,
        { pendingPatches: nextState.pendingPatches, failures, fieldVersions, fieldBaselineTokens },
        undefined,
        false,
        changedPatches,
        removedFieldVersions,
      )
      // 降级提示是 sticky：一次字段写入失败后，不能因另一个字段写成功就掩盖刷新丢失风险。
      if (!persisted) {
        isDetailDraftMemoryOnlyRef.current = true
        setIsDetailDraftMemoryOnly(true)
        if (Object.keys(removedFieldVersions).length > 0) {
          // 删除持久化失败时必须回滚被清空字段；否则刷新会复活旧值而当前页面却失去恢复入口。
          const recoveredPatches = { ...nextState.pendingPatches }
          const recoveredFailures = { ...failures }
          const recoveredVersions = { ...fieldVersions }
          const recoveredBaselineTokens = { ...fieldBaselineTokens }
          Object.keys(removedFieldVersions).forEach((fieldKey) => {
            const separatorIndex = fieldKey.lastIndexOf(':')
            const hguid = fieldKey.slice(0, separatorIndex)
            const field = fieldKey.slice(separatorIndex + 1)
            const previousPatch = previousPendingPatches[hguid]
            if (!previousPatch) return
            const patch = { ...(recoveredPatches[hguid] ?? { hguid }) }
            if (field === '英文名称') {
              patch.英文名称 = previousPatch.英文名称
              patch.ClearEnglishName = previousPatch.ClearEnglishName
            } else if (field in previousPatch) {
              ;(patch as Record<string, unknown>)[field] = previousPatch[field as keyof PendingContainerDetailPatch]
            }
            recoveredPatches[hguid] = patch
            if (previousFieldVersions[fieldKey]) recoveredVersions[fieldKey] = previousFieldVersions[fieldKey]
            if (previousBaselineTokens[fieldKey]) recoveredBaselineTokens[fieldKey] = previousBaselineTokens[fieldKey]
            const previousFailure = previousFailures[fieldKey]
            if (previousFailure) recoveredFailures[fieldKey] = previousFailure
            const previousRowFailureKey = `${hguid}:*`
            const previousRowFailure = previousFailures[previousRowFailureKey]
            if (previousRowFailure) recoveredFailures[previousRowFailureKey] = previousRowFailure
          })
          const reconciledRecoveredFailures = reconcileContainerDetailDraftFailures(recoveredFailures, recoveredPatches)
          pendingDetailPatchesRef.current = recoveredPatches
          pendingDetailFailuresRef.current = reconciledRecoveredFailures
          pendingDetailFieldVersionsRef.current = recoveredVersions
          pendingDetailFieldBaselineTokensRef.current = recoveredBaselineTokens
          updateCurrentAutoSaveContextDraftMetadata(recoveredVersions, recoveredBaselineTokens)
          failedPendingDetailSaveKeysRef.current = new Set(Object.keys(reconciledRecoveredFailures))
          setPendingDetailPatches(recoveredPatches)
          setPendingDetailFailures(reconciledRecoveredFailures)
          setRows((items) => applyPendingContainerDetailPatches(items, recoveredPatches))
        }
      } else {
        if (reloadRemovedFieldBaseline && Object.keys(removedFieldVersions).length > 0) {
          // 清空草稿字段表示取消该本地编辑；回读服务端基线并在加载完成时重新叠加同批剩余草稿。
          void reloadCurrentDetailRef.current()
        }
        if (countPendingContainerDetailFields(nextState.pendingPatches) === 0) {
          isDetailDraftMemoryOnlyRef.current = false
          setIsDetailDraftMemoryOnly(false)
        }
      }
    }
  }

  const restorePendingDetailDraft = () => {
    if (!currentUserGuid || !containerGuid) return
    const draftIdentity = buildContainerDetailDraftStorageKey(currentUserGuid, containerGuid)
    if (pendingDetailDraftIdentityRef.current === draftIdentity) return

    // 草稿命名空间变化后，旧货柜/旧用户的行必须立即失效，等当前请求成功后再叠加新草稿。
    detailRowsContainerGuidRef.current = ''
    lastLoadedContainerDetailSuccessRef.current = null
    rowsRef.current = []
    autoSaveEditBaselineRef.current.clear()
    // 用户或货柜命名空间切换时，确认覆盖必须重新由当前用户作出。
    pendingDetailOverrideAcknowledgementsRef.current = {}
    setPendingDetailConflicts([])
    setRows([])
    setSelectedRowKeys([])
    const restoredDraft = readContainerDetailDraft(
      getContainerDetailDraftStorage(),
      currentUserGuid,
      containerGuid,
    )
    const previousAutoSaveContextKey = autoSaveContextKeyRef.current
    pendingDetailDraftIdentityRef.current = draftIdentity
    const nextAutoSaveContextKey = buildContainerDetailAutoSaveContextKey(containerGuid, draftIdentity)
    if (previousAutoSaveContextKey && previousAutoSaveContextKey !== nextAutoSaveContextKey) {
      autoSaveQueueRef.current?.discardContext(previousAutoSaveContextKey)
    }
    autoSaveContextKeyRef.current = nextAutoSaveContextKey
    if (!autoSaveContextSnapshotsRef.current.has(nextAutoSaveContextKey)) {
      autoSaveContextSnapshotsRef.current.set(nextAutoSaveContextKey, {
        userGuid: currentUserGuid,
        containerGuid,
        draftIdentity,
        rows: [],
        container: null,
        fieldVersions: { ...(restoredDraft.fieldVersions ?? {}) },
        fieldBaselineTokens: { ...(restoredDraft.fieldBaselineTokens ?? {}) },
      })
    }
    const resetAutoSaveSnapshot: ContainerDetailAutoSaveSnapshot = {
      pendingFieldCount: 0,
      runningFieldCount: 0,
      failureCount: 0,
      unsavedFieldCount: 0,
      failures: [],
    }
    autoSaveSnapshotRef.current = resetAutoSaveSnapshot
    setAutoSaveSnapshot(resetAutoSaveSnapshot)
    pendingDetailSavePromisesRef.current.clear()
    failedDetailSaveKeysRef.current = new Set()
    setPendingDetailSaveCount(0)
    setDetailSaveSubmitting(false)
    applyPendingDetailDraftState(restoredDraft, false)
    isDetailDraftMemoryOnlyRef.current = false
    setIsDetailDraftMemoryOnly(false)

    if (
      active
      && restoredDraft.restored
      && restoredDetailDraftNoticeKeyRef.current !== draftIdentity
    ) {
      restoredDetailDraftNoticeKeyRef.current = draftIdentity
      message.info(t(
        'containers.messages.localDraftRestored',
        '已恢复 {{count}} 项本地未保存草稿',
        { count: countPendingContainerDetailFields(restoredDraft.pendingPatches) },
      ))
    }
  }

  useEffect(() => {
    restorePendingDetailDraft()
    const contextKey = autoSaveContextKeyRef.current
    const lifecycleAction = resolveContainerDetailAutoSaveLifecycleAction(active, contextKey)
    if (lifecycleAction === 'discard') {
      autoSaveQueueRef.current?.discardContext(contextKey)
      return
    }
    if (lifecycleAction !== 'attach') return

    const nextAutoSaveSnapshot = autoSaveQueueRef.current?.attachContext(contextKey)
    if (!nextAutoSaveSnapshot) return
    autoSaveSnapshotRef.current = nextAutoSaveSnapshot
    setAutoSaveSnapshot(nextAutoSaveSnapshot)
    setPendingDetailSaveCount(
      nextAutoSaveSnapshot.pendingFieldCount
      + nextAutoSaveSnapshot.runningFieldCount
      + pendingDetailSavePromisesRef.current.size,
    )
    // 身份或货柜变化时必须先切换草稿命名空间，再由后续加载覆盖服务端行。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, currentUserGuid, containerGuid])

  useEffect(() => () => {
    const contextKey = autoSaveContextKeyRef.current
    if (contextKey) autoSaveQueueRef.current?.discardContext(contextKey)
  }, [])

  const editingPresenceState: 'viewing' | 'editing' = hasPendingConcurrencyConflicts || pendingDetailFieldCount > 0 || autoSaveSnapshot.unsavedFieldCount > 0 || isContainerDetailFieldFocused ? 'editing' : 'viewing'

  useEffect(() => {
    if (!active || !containerGuid || !currentUserGuid) {
      setEditingPresenceAvailable(false)
      return
    }
    let cancelled = false
    // 清理回调固定本次 effect 的会话 ID，避免 eslint 与运行时都误读为会话已切换。
    const clientSessionId = clientEditingSessionIdRef.current
    const refreshPresence = async () => {
      try {
        await heartbeatContainerDetailEditingPresence(containerGuid, {
          clientSessionId,
          state: editingPresenceState,
        })
        const next = await getContainerDetailEditingPresence(containerGuid)
        if (cancelled) return
        // 后端按用户聚合同一浏览器多标签；前端仅排除自己，在线状态只作提示。
        setEditingPresence({
          viewers: next.viewers.filter((user) => user.userGuid !== currentUserGuid),
          editors: next.editors.filter((user) => user.userGuid !== currentUserGuid),
        })
        setEditingPresenceAvailable(true)
      } catch {
        // 状态租约不可用不影响草稿、保存或服务器并发校验，只隐藏提示避免制造假阻断。
        if (!cancelled) setEditingPresenceAvailable(false)
      }
    }
    void refreshPresence()
    const timer = window.setInterval(() => void refreshPresence(), 30_000)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [active, containerGuid, currentUserGuid, editingPresenceState])

  useEffect(() => {
    if (!active || !containerGuid || !currentUserGuid) return
    const clientSessionId = clientEditingSessionIdRef.current
    // editing/viewing 切换只续租；离开仅发生在卸载、货柜/用户切换或 Tab 停用，避免旧 cleanup 删除新心跳。
    return () => {
      void leaveContainerDetailEditingPresence(containerGuid, { clientSessionId }).catch(() => undefined)
    }
  }, [active, containerGuid, currentUserGuid])

  const applyExternalPendingDetailDraftState = (nextDraft: ContainerDetailDraftState) => {
    const currentDraft: ContainerDetailDraftState = {
      pendingPatches: pendingDetailPatchesRef.current,
      failures: pendingDetailFailuresRef.current,
      fieldVersions: pendingDetailFieldVersionsRef.current,
      fieldBaselineTokens: pendingDetailFieldBaselineTokensRef.current,
    }
    const applyMode = getContainerDetailDraftExternalApplyMode(currentDraft, nextDraft)
    applyPendingDetailDraftState(nextDraft, false)
    if (applyMode === 'reload') {
      // 外部清空字段时受控单元格必须回读服务端值；reload 自带货柜/查询代次保护。
      void reloadCurrentDetailRef.current()
      return
    }
    setRows((items) => applyPendingContainerDetailPatches(items, nextDraft.pendingPatches))
  }

  useEffect(() => {
    if (typeof window === 'undefined' || !currentUserGuid || !containerGuid) return
    const draftPrefix = `${buildContainerDetailDraftStorageKey(currentUserGuid, containerGuid)}:`
    const onStorage = (event: StorageEvent) => {
      if (event.storageArea !== window.localStorage || !event.key?.startsWith(draftPrefix)) return
      // 本页存在未落盘字段时不能用 storage 的旧快照替换内存，避免直接丢失当前编辑。
      if (isDetailDraftMemoryOnlyRef.current) return
      // 另一标签页更新同一字段时重新合并字段记录，禁止用当前标签的旧快照覆盖它。
      applyExternalPendingDetailDraftState(readContainerDetailDraft(window.localStorage, currentUserGuid, containerGuid))
    }
    window.addEventListener('storage', onStorage)
    return () => window.removeEventListener('storage', onStorage)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentUserGuid, containerGuid])

  const containerDetailTabTitle = container?.货柜编号 ? t('containers.detailTitleWithNumber', { number: container.货柜编号 }) : undefined
  const containerDetailTabKey = containerGuid ? `/warehouse/container/detail/${containerGuid}` : undefined

  useEffect(() => {
    if (!active || !containerDetailTabKey || !containerDetailTabTitle) {
      return
    }

    // 隐藏的 KeepAlive 旧实例也会收到全局 URL 变化；只有当前激活页能写自己的 Tab 标题。
    updateTabTitle(containerDetailTabKey, containerDetailTabTitle)
  }, [active, containerDetailTabKey, containerDetailTabTitle, updateTabTitle])

  const layoutDetailLoadMode: ContainerDetailLoadMode = detailPagingState.containerGuid === containerGuid
    ? detailPagingState.mode
    : 'probe'

  useEffect(() => {
    let frameId: number | null = null

    const measureDetailLayout = () => {
      const toolbarHeight = Math.ceil(toolbarElement?.getBoundingClientRect().height ?? 0)
      const tableHeaderHeight = Math.ceil(tableRegionElement?.querySelector('.ant-table-thead')?.getBoundingClientRect().height ?? 0)
      const tableFooterHeight = Math.ceil(tableRegionElement?.querySelector('.ant-table-footer')?.getBoundingClientRect().height ?? 0)
      const tablePaginationElement = tableRegionElement?.querySelector('.ant-table-pagination') as HTMLElement | null
      const paginationStyle = tablePaginationElement ? window.getComputedStyle(tablePaginationElement) : null
      const paginationMarginHeight = paginationStyle
        ? (Number.parseFloat(paginationStyle.marginTop) || 0) + (Number.parseFloat(paginationStyle.marginBottom) || 0)
        : 0
      const paginationHeight = Math.ceil((tablePaginationElement?.getBoundingClientRect().height ?? 0) + paginationMarginHeight)
      const tableBodyElement = tableRegionElement?.querySelector('.ant-table-body') as HTMLElement | null
      const horizontalScrollbarHeight = tableBodyElement ? Math.max(0, tableBodyElement.offsetHeight - tableBodyElement.clientHeight) : 0
      const tableChromeHeight = tableHeaderHeight + tableFooterHeight + paginationHeight + horizontalScrollbarHeight

      setDetailLayoutMetrics((current) => {
        if (
          current.toolbarHeight === toolbarHeight &&
          current.tableChromeHeight === tableChromeHeight
        ) {
          return current
        }

        return { toolbarHeight, tableChromeHeight }
      })
    }

    const scheduleMeasure = () => {
      if (frameId != null) {
        window.cancelAnimationFrame(frameId)
      }
      frameId = window.requestAnimationFrame(measureDetailLayout)
    }

    measureDetailLayout()
    scheduleMeasure()

    if (typeof ResizeObserver === 'undefined') {
      window.addEventListener('resize', scheduleMeasure)
      return () => {
        if (frameId != null) window.cancelAnimationFrame(frameId)
        window.removeEventListener('resize', scheduleMeasure)
      }
    }

    const observer = new ResizeObserver(scheduleMeasure)
    if (gridContentElement) observer.observe(gridContentElement)
    if (toolbarElement) observer.observe(toolbarElement)
    if (tableRegionElement) observer.observe(tableRegionElement)
    window.addEventListener('resize', scheduleMeasure)
    return () => {
      if (frameId != null) window.cancelAnimationFrame(frameId)
      observer.disconnect()
      window.removeEventListener('resize', scheduleMeasure)
    }
  }, [gridContentElement, tableRegionElement, toolbarElement, detailTableRenderKey, exporting, exportProgress, layoutDetailLoadMode])

  useEffect(() => {
    setCategoryLoading(true)
    getCategoryTree()
      .then((tree) => {
        setCategories(tree)
        setCategoryExpandedKeys(collectCategoryExpandedKeys(tree, 1))
      })
      .catch((error) => {
        console.error(error)
        message.error(error instanceof Error ? error.message : t('warehouse.categories.loadTreeFailed', '加载分类树失败'))
      })
      .finally(() => setCategoryLoading(false))
  }, [t])

  const categoryLookup = useMemo(() => buildWarehouseCategoryLookup(categories), [categories])
  const selectedTargetCategory = useMemo(() => findWarehouseCategory(categories, targetCategoryGuid), [categories, targetCategoryGuid])
  const selectedTargetCategoryPath = targetCategoryGuid ? getWarehouseProductCategoryTooltip({
    categoryName: selectedTargetCategory?.categoryName,
    warehouseCategoryGUID: targetCategoryGuid,
  }, categoryLookup, i18n.language) : undefined
  const selectedRowTargetCategory = useMemo(() => findWarehouseCategory(categories, rowTargetCategoryGuid), [categories, rowTargetCategoryGuid])
  const selectedRowTargetCategoryPath = rowTargetCategoryGuid ? getWarehouseProductCategoryTooltip({
    categoryName: selectedRowTargetCategory?.categoryName,
    warehouseCategoryGUID: rowTargetCategoryGuid,
  }, categoryLookup, i18n.language) : undefined
  const categoryManageActiveTargetGuid = categoryManageContext === 'batch'
    ? targetCategoryGuid
    : categoryManageContext === 'row'
      ? rowTargetCategoryGuid
      : undefined

  const openCategoryManageModal = (context: 'batch' | 'row') => {
    setCategoryManageContext(context)
    setCategoryManageOpen(true)
  }

  const closeCategoryManageModal = () => {
    setCategoryManageOpen(false)
    setCategoryManageContext(null)
  }

  const handleCategoriesChanged = (
    tree: WarehouseCategoryNode[],
    change: ContainerCategoryChange,
  ) => {
    const selection = resolveContainerCategorySelectionAfterRefresh(
      tree,
      undefined,
      categoryManageActiveTargetGuid,
      change,
    )

    setCategories(tree)
    setCategoryExpandedKeys(collectCategoryExpandedKeys(tree, 1))
    if (categoryManageContext === 'batch') {
      setTargetCategoryGuid(selection.activeTargetCategoryGuid)
    } else if (categoryManageContext === 'row') {
      setRowTargetCategoryGuid(selection.activeTargetCategoryGuid)
    }
  }

  const handleCategoryMutationCommitted = (change: ContainerCategoryChange) => {
    if (categoryManageContext === 'batch') {
      setTargetCategoryGuid((current) => resolveContainerCategoryTargetAfterMutation(current, change))
    } else if (categoryManageContext === 'row') {
      setRowTargetCategoryGuid((current) => resolveContainerCategoryTargetAfterMutation(current, change))
    }
  }

  const hasCurrentDetailPagingState = detailPagingState.containerGuid === containerGuid
  const detailLoadMode = hasCurrentDetailPagingState ? detailPagingState.mode : 'probe'
  const detailPageSize = hasCurrentDetailPagingState
    ? detailPagingState.pageSize
    : CONTAINER_DETAIL_DEFAULT_PAGE_SIZE
  const pagedDetailScopeKey = useMemo(() => JSON.stringify({
    containerGuid,
    filters: columnFilters,
    selectedTags: selectedTagFilters,
    sortState,
    pageSize: detailPageSize,
  }), [columnFilters, containerGuid, detailPageSize, selectedTagFilters, sortState])
  const detailPageNumber = hasCurrentDetailPagingState
    && detailPagingState.scopeKey === pagedDetailScopeKey
    ? detailPagingState.pageNumber
    : 1
  const detailHasMore = detailLoadMode === 'paged'
    && detailPageNumber * detailPageSize < detailItemsTotal

  const initialDetailQuery = useMemo(() => buildContainerDetailQuery({
    containerGuid,
    filters: {},
    sortState: DEFAULT_CONTAINER_DETAIL_SORT,
    pageNumber: 1,
    pageSize: CONTAINER_DETAIL_INITIAL_PAGE_SIZE,
    includeTotal: true,
    includeStats: false,
  }), [containerGuid])
  const fullDetailQuery = useMemo(() => buildContainerDetailQuery({
    containerGuid,
    filters: {},
    sortState: DEFAULT_CONTAINER_DETAIL_SORT,
    pageNumber: 1,
    pageSize: CONTAINER_DETAIL_FULL_LOAD_LIMIT,
    includeTotal: false,
    includeStats: false,
  }), [containerGuid])
  const pagedDetailQuery = useMemo(() => buildContainerDetailQuery({
    containerGuid,
    filters: columnFilters,
    selectedTags: selectedTagFilters,
    sortState,
    pageNumber: detailPageNumber,
    pageSize: detailPageSize,
    includeTotal: false,
    includeStats: false,
  }), [columnFilters, containerGuid, detailPageNumber, detailPageSize, selectedTagFilters, sortState])
  const pagedDetailStatsQuery = useMemo(() => buildContainerDetailQuery({
    containerGuid,
    filters: columnFilters,
    selectedTags: selectedTagFilters,
    pageNumber: 1,
    pageSize: detailPageSize,
    includeItems: false,
    includeTotal: true,
    includeStats: true,
  }), [columnFilters, containerGuid, detailPageSize, selectedTagFilters])
  const pagedDetailQueryKey = useMemo(() => JSON.stringify(pagedDetailQuery), [pagedDetailQuery])
  const pagedDetailStatsKey = useMemo(() => JSON.stringify(pagedDetailStatsQuery), [pagedDetailStatsQuery])
  const initialDetailQueryKey = useMemo(() => JSON.stringify(initialDetailQuery), [initialDetailQuery])
  const fullDetailQueryKey = useMemo(() => `full:${containerGuid}`, [containerGuid])
  const activeLoadQueryKey = detailLoadMode === 'probe'
    ? initialDetailQueryKey
    : detailLoadMode === 'paged'
      ? pagedDetailQueryKey
      : fullDetailQueryKey
  const pagedDetailScopeKeyRef = useRef(pagedDetailScopeKey)
  pagedDetailScopeKeyRef.current = pagedDetailScopeKey
  const pagedDetailQueryKeyRef = useRef(pagedDetailQueryKey)
  pagedDetailQueryKeyRef.current = pagedDetailQueryKey
  const pagedDetailStatsKeyRef = useRef(pagedDetailStatsKey)
  pagedDetailStatsKeyRef.current = pagedDetailStatsKey

  const loadHeader = async (showLoading = true) => {
    if (!containerGuid) {
      return
    }
    const currentRequestId = headerLoadRequestIdRef.current + 1
    headerLoadRequestIdRef.current = currentRequestId
    if (showLoading) {
      setLoading(true)
    }
    try {
      const info = await getContainerDetail(containerGuid)
      if (headerLoadRequestIdRef.current !== currentRequestId) {
        return
      }
      loadedContainerGuidRef.current = containerGuid
      visibleContainerGuidRef.current = containerGuid
      setContainer(info)
      setHeaderForm({
        货柜编号: info.货柜编号,
        装柜日期: info.装柜日期 ? dayjs(info.装柜日期) : null,
        预计到岸日期: info.预计到岸日期 ? dayjs(info.预计到岸日期) : null,
        实际到货日期: info.实际到货日期 ? dayjs(info.实际到货日期) : null,
        汇率: info.汇率,
        备注: info.备注,
        状态: info.状态,
      })
      setFreightInputMode('standard68')
      setFreightInputValue(normalizeContainerFreightInput(
        deriveContainerFreightInput(info.运费, info.总体积, 'standard68'),
        'standard68',
      ))
      setFreightInputDirty(false)
    } catch (error) {
      if (headerLoadRequestIdRef.current !== currentRequestId) {
        return
      }
      const errorMessage = error instanceof Error ? error.message : t('containers.messages.loadDetailFailed')
      if (showLoading) {
        console.error(error)
        visibleContainerGuidRef.current = null
        message.error(errorMessage)
      } else {
        console.error('货柜详情静默刷新失败', error)
      }
    } finally {
      if (headerLoadRequestIdRef.current !== currentRequestId) {
        return
      }
      if (showLoading) {
        setLoading(false)
      }
    }
  }

  const overlayPendingDetailChanges = (items: ContainerDetail[]) => {
    const loadedItemsWithDraft = applyPendingContainerDetailPatches(
      items,
      pendingDetailPatchesRef.current,
    )
    const currentAutoSaveContextKey = autoSaveContextKeyRef.current
    const shouldOverlayCurrentAutoSaves = isContainerDetailAutoSaveContextCurrent(
      currentAutoSaveContextKey,
      containerGuid,
      pendingDetailDraftIdentityRef.current,
    )
    const contextSnapshot = shouldOverlayCurrentAutoSaves
      ? autoSaveContextSnapshotsRef.current.get(currentAutoSaveContextKey)
      : undefined
    return applyContainerDetailAutoSavePatches(
      loadedItemsWithDraft,
      shouldOverlayCurrentAutoSaves
        ? autoSaveQueueRef.current?.getUnsettledPatches(currentAutoSaveContextKey) ?? {}
        : {},
      (row, patch) => buildContainerDetailAutoSaveUpdate(
        row,
        patch,
        contextSnapshot?.container ?? containerRef.current,
      ) as Partial<ContainerDetail>,
    )
  }

  const publishLoadedDetailRows = (items: ContainerDetail[]) => {
    const serverRowsByHguid = new Map(items.map((item) => [item.hguid, item]))
    const restoredConflicts: ContainerDetailFieldConflict[] = Object.values(pendingDetailPatchesRef.current).flatMap((patch) => getSubmittedContainerDetailFields(patch).flatMap((field) => {
      const hguid = patch.hguid
      const key = `${hguid}:${field}`
      const baselineToken = pendingDetailFieldBaselineTokensRef.current[key]
      const serverRow = serverRowsByHguid.get(hguid)
      const currentServerFieldToken = serverRow?.serverFieldTokens?.[field]
      const submittedValue = field === '英文名称'
        ? (patch?.ClearEnglishName === true ? null : patch?.英文名称)
        : patch?.[field as keyof PendingContainerDetailPatch]
      if (!serverRow || !currentServerFieldToken || currentServerFieldToken === baselineToken || submittedValue === undefined) return []
      return [{
        hguid,
        field,
        serverValue: getContainerDetailConflictServerValue(serverRow, field),
        submittedValue,
        currentServerFieldToken,
        code: 'CONCURRENT_FIELD_UPDATE',
        message: baselineToken
          ? undefined
          : t('containers.messages.legacyDraftRequiresReview', '旧草稿缺少服务器基线，请选择采用服务器值或保留我的值'),
      }]
    }))
    setPendingDetailConflicts((current) => {
      const incomingKeys = new Set(restoredConflicts.map((item) => `${item.hguid}:${item.field}`))
      const currentTokens = new Map<string, string>(items.flatMap((row) => Object.entries(row.serverFieldTokens ?? {}).map(([field, token]) => [`${row.hguid}:${field}`, token] as const)))
      const retained = current.filter((conflict) => {
        const key = `${conflict.hguid}:${conflict.field}`
        // 服务端已回到该草稿首次看到的版本，旧冲突提示已失效，必须自动撤销。
        if (currentTokens.get(key) === pendingDetailFieldBaselineTokensRef.current[key]) return false
        return !incomingKeys.has(key)
      })
      return restoredConflicts.length > 0 ? [...retained, ...restoredConflicts] : retained
    })
    const loadedItems = overlayPendingDetailChanges(items)
    rowsRef.current = loadedItems
    const currentAutoSaveContextKey = autoSaveContextKeyRef.current
    if (isContainerDetailAutoSaveContextCurrent(
      currentAutoSaveContextKey,
      containerGuid,
      pendingDetailDraftIdentityRef.current,
    )) {
      updateAutoSaveContextRows(currentAutoSaveContextKey, loadedItems, containerRef.current)
    }
    setRows(loadedItems)
  }

  const cancelScheduledDetailStats = () => {
    if (detailStatsTimerRef.current !== null) {
      window.clearTimeout(detailStatsTimerRef.current)
      detailStatsTimerRef.current = null
    }
    detailStatsAbortControllerRef.current?.abort()
    detailStatsAbortControllerRef.current = null
    detailStatsRequestIdRef.current += 1
  }

  const schedulePagedDetailStats = (
    query: typeof pagedDetailStatsQuery,
    statsKey: string,
  ) => {
    if (lastLoadedDetailStatsKeyRef.current === statsKey) return
    cancelScheduledDetailStats()
    const requestId = detailStatsRequestIdRef.current
    detailStatsTimerRef.current = window.setTimeout(() => {
      detailStatsTimerRef.current = null
      const controller = new AbortController()
      detailStatsAbortControllerRef.current = controller
      void queryContainerProducts(containerGuid, query, controller.signal)
        .then((result) => {
          if (
            controller.signal.aborted
            || detailStatsRequestIdRef.current !== requestId
            || pagedDetailStatsKeyRef.current !== statsKey
            || currentContainerGuidRef.current !== containerGuid
          ) return
          if (result.totalComputed !== false) {
            setDetailItemsTotal(result.itemsTotal)
          }
          if (result.statsComputed !== false) {
            setRemoteTagStats({ ...EMPTY_CONTAINER_DETAIL_TAG_STATS, ...result.tagStats })
          }
          lastLoadedDetailStatsKeyRef.current = statsKey
        })
        .catch((error) => {
          if (!controller.signal.aborted) {
            console.error('货柜明细统计加载失败', error)
          }
        })
        .finally(() => {
          if (detailStatsAbortControllerRef.current === controller) {
            detailStatsAbortControllerRef.current = null
          }
        })
    }, 0)
  }

  const prepareDetailLoad = (controller: AbortController, requestId: number) => {
    cancelScheduledDetailStats()
    detailAbortControllerRef.current?.abort()
    detailAbortControllerRef.current = controller
    detailResetRequestRef.current = controller
    containerDetailReconcileGenerationRef.current = requestId
    lastDetailTableScrollTopRef.current = 0
    setDetailLoading(true)
    restorePendingDetailDraft()

    const isContainerChange = detailRowsContainerGuidRef.current !== containerGuid
    if (!isContainerChange) return

    detailRowsContainerGuidRef.current = containerGuid
    lastLoadedDetailStatsKeyRef.current = null
    setRemoteTagStats(EMPTY_CONTAINER_DETAIL_TAG_STATS)
    setDetailItemsTotal(0)
    pendingDetailSavePromisesRef.current.clear()
    failedDetailSaveKeysRef.current = new Set()
    failedPendingDetailSaveKeysRef.current = new Set()
    setPendingDetailSaveCount(0)
    setDetailSaveSubmitting(false)
    const currentDraftIdentity = currentUserGuid
      ? buildContainerDetailDraftStorageKey(currentUserGuid, containerGuid)
      : ''
    if (!currentDraftIdentity || pendingDetailDraftIdentityRef.current !== currentDraftIdentity) {
      // 当前用户身份尚未就绪时只清空内存，不删除任何用户的本地草稿。
      applyPendingDetailDraftState({ pendingPatches: {}, failures: {} }, false)
    }
    // 只有切换货柜才清空旧行；同货柜翻页、筛选、排序或刷新失败时继续保留已显示快照。
    setRows([])
    setSelectedRowKeys([])
  }

  const loadDetailRows = async (requestedMode: 'probe' | 'paged') => {
    if (!containerGuid) return
    const initialPageScopeKey = pagedDetailScopeKey
    const initialPageWasReusable = canReuseContainerDetailInitialPage({
      filters: columnFilters,
      selectedTags: selectedTagFilters,
      sortState,
      pageSize: detailPageSize,
    })
    const currentRequestId = containerDetailLoadRequestIdRef.current + 1
    containerDetailLoadRequestIdRef.current = currentRequestId
    const controller = new AbortController()
    prepareDetailLoad(controller, currentRequestId)

    const isCurrentRequest = () => (
      !controller.signal.aborted
      && containerDetailLoadRequestIdRef.current === currentRequestId
      && currentContainerGuidRef.current === containerGuid
    )

    try {
      if (requestedMode === 'probe') {
        const initialResult = await queryContainerProducts(
          containerGuid,
          initialDetailQuery,
          controller.signal,
        )
        if (!isCurrentRequest()) return

        const resolution = resolveContainerDetailInitialPage(initialResult)
        setDetailItemsTotal(resolution.itemsTotal)

        if (resolution.mode === 'paged') {
          const canReuseInitialPage = initialPageWasReusable
            && pagedDetailScopeKeyRef.current === initialPageScopeKey
          setDetailPagingState({
            containerGuid,
            mode: 'paged',
            pageNumber: 1,
            pageSize: CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
            scopeKey: pagedDetailScopeKeyRef.current,
          })
          if (canReuseInitialPage) {
            publishLoadedDetailRows(resolution.items)
            lastLoadedContainerDetailSuccessRef.current = {
              containerGuid,
              queryKey: pagedDetailQueryKeyRef.current,
              generation: currentRequestId,
            }
            schedulePagedDetailStats(pagedDetailStatsQuery, pagedDetailStatsKeyRef.current)
          }
          return
        }

        publishLoadedDetailRows(resolution.items)
        if (!resolution.requiresFullLoad) {
          setDetailPagingState({
            containerGuid,
            mode: 'full',
            pageNumber: 1,
            pageSize: CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
            scopeKey: '',
          })
          lastLoadedContainerDetailSuccessRef.current = {
            containerGuid,
            queryKey: fullDetailQueryKey,
            generation: currentRequestId,
          }
          return
        }

        const fullResult = await queryContainerProducts(
          containerGuid,
          fullDetailQuery,
          controller.signal,
        )
        if (!isCurrentRequest()) return
        const fullResolution = resolveContainerDetailFullPage(
          fullResult,
          CONTAINER_DETAIL_FULL_LOAD_LIMIT,
        )
        setDetailItemsTotal(fullResolution.itemsTotal)

        if (fullResolution.mode === 'paged') {
          const canReuseInitialPage = initialPageWasReusable
            && pagedDetailScopeKeyRef.current === initialPageScopeKey
          setDetailPagingState({
            containerGuid,
            mode: 'paged',
            pageNumber: 1,
            pageSize: CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
            scopeKey: pagedDetailScopeKeyRef.current,
          })
          if (canReuseInitialPage) {
            publishLoadedDetailRows(fullResolution.items)
            lastLoadedContainerDetailSuccessRef.current = {
              containerGuid,
              queryKey: pagedDetailQueryKeyRef.current,
              generation: currentRequestId,
            }
            schedulePagedDetailStats(pagedDetailStatsQuery, pagedDetailStatsKeyRef.current)
          }
          return
        }

        publishLoadedDetailRows(fullResolution.items)
        setDetailPagingState({
          containerGuid,
          mode: 'full',
          pageNumber: 1,
          pageSize: CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
          scopeKey: '',
        })
        lastLoadedContainerDetailSuccessRef.current = {
          containerGuid,
          queryKey: fullDetailQueryKey,
          generation: currentRequestId,
        }
        return
      }

      const result = await queryContainerProducts(
        containerGuid,
        pagedDetailQuery,
        controller.signal,
      )
      if (!isCurrentRequest()) return
      publishLoadedDetailRows(result.items)
      setDetailPagingState({
        containerGuid,
        mode: 'paged',
        pageNumber: pagedDetailQuery.pageNumber,
        pageSize: pagedDetailQuery.pageSize,
        scopeKey: pagedDetailScopeKey,
      })
      lastLoadedContainerDetailSuccessRef.current = {
        containerGuid,
        queryKey: pagedDetailQueryKey,
        generation: currentRequestId,
      }
      schedulePagedDetailStats(pagedDetailStatsQuery, pagedDetailStatsKey)
    } catch (error) {
      if (!isCurrentRequest()) return
      console.error(error)
      message.error(error instanceof Error ? error.message : t('containers.messages.loadDetailFailed'))
    } finally {
      if (detailResetRequestRef.current === controller) {
        detailResetRequestRef.current = null
        detailAbortControllerRef.current = null
        setDetailLoading(false)
      }
    }
  }

  const loadNextDetailChunk = async () => {
    if (detailLoadMode !== 'paged' || detailLoading || !detailHasMore) return
    setDetailPagingState({
      containerGuid,
      mode: 'paged',
      pageNumber: detailPageNumber + 1,
      pageSize: detailPageSize,
      scopeKey: pagedDetailScopeKey,
    })
  }

  const reloadCurrentDetail = async () => {
    lastLoadedContainerDetailSuccessRef.current = null
    lastLoadedDetailStatsKeyRef.current = null
    await loadDetailRows(detailLoadMode === 'paged' ? 'paged' : 'probe')
  }

  const loadData = async (showLoading = true) => {
    await Promise.all([
      loadHeader(showLoading),
      reloadCurrentDetail(),
    ])
  }

  // 保存期间筛选条件可能变化；始终通过 ref 调用最新 render 的查询闭包。
  reloadCurrentDetailRef.current = reloadCurrentDetail

  useEffect(() => {
    if (!active || !currentUserGuid) return

    if (shouldSkipDetailAutoReload({
      requestedDetailId: containerGuid,
      loadedDetailId: loadedContainerGuidRef.current,
      visibleDetailId: visibleContainerGuidRef.current,
    })) {
      // 保活 Tab 切回同一货柜时复用缓存，避免自动请求导致页面闪动。
      return
    }

    // 隐藏的 KeepAlive 节点也会收到全局路由变化，必须只让当前激活节点发起请求。
    const shouldShowInitialLoading = shouldShowDetailInitialLoading({
      requestedDetailId: containerGuid,
      loadedDetailId: loadedContainerGuidRef.current,
      visibleDetailId: visibleContainerGuidRef.current,
    })
    void loadHeader(shouldShowInitialLoading)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, containerGuid, currentUserGuid])

  useEffect(() => {
    if (!active || !currentUserGuid) return

    const cancelDetailLoads = () => {
      containerDetailLoadRequestIdRef.current += 1
      containerDetailReconcileGenerationRef.current += 1
      detailAbortControllerRef.current?.abort()
      detailAbortControllerRef.current = null
      detailResetRequestRef.current = null
      setDetailLoading(false)
    }

    if (detailLoadMode === 'full') {
      return cancelDetailLoads
    }
    if (
      lastLoadedContainerDetailSuccessRef.current?.containerGuid === containerGuid
      && lastLoadedContainerDetailSuccessRef.current.queryKey === activeLoadQueryKey
    ) return cancelDetailLoads

    void loadDetailRows(detailLoadMode === 'paged' ? 'paged' : 'probe')
    return cancelDetailLoads
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, activeLoadQueryKey, currentUserGuid])

  useEffect(() => {
    // 筛选或排序改变当前结果范围；分页模式同步回第 1 页，草稿不随范围变化清空。
    setSelectedRowKeys([])
    if (detailLoadMode === 'paged' && (
      detailPagingState.containerGuid !== containerGuid
      || detailPagingState.scopeKey !== pagedDetailScopeKey
    )) {
      setDetailPagingState((current) => ({
        ...current,
        containerGuid,
        mode: 'paged',
        pageNumber: 1,
        pageSize: detailPageSize,
        scopeKey: pagedDetailScopeKey,
      }))
    }
  }, [columnFilters, containerGuid, detailLoadMode, detailPageSize, detailPagingState.containerGuid, detailPagingState.scopeKey, pagedDetailScopeKey, selectedTagFilters, sortState])

  useEffect(() => () => cancelScheduledDetailStats(), [])

  useEffect(() => {
    const wasActive = wasContainerDetailTabActiveRef.current
    wasContainerDetailTabActiveRef.current = active

    if (!active || wasActive || rows.length === 0) {
      return
    }

    const scrollTop = lastDetailTableScrollTopRef.current
    const renderFrame = window.requestAnimationFrame(() => {
      // KeepAlive 隐藏节点恢复时，AntD 虚拟表格可能沿用隐藏状态下的测量结果；切回后重挂载表格让 body 重新计算高度。
      setDetailTableRenderKey((value) => value + 1)
      detailTableRestoreFrameRef.current = window.requestAnimationFrame(() => {
        detailTableRef.current?.scrollTo?.({ top: scrollTop })
      })
    })
    detailTableRestoreFrameRef.current = renderFrame

    return () => {
      if (detailTableRestoreFrameRef.current !== null) {
        window.cancelAnimationFrame(detailTableRestoreFrameRef.current)
        detailTableRestoreFrameRef.current = null
      }
    }
  }, [active, containerGuid, rows.length])

  const baseFilteredRows = useMemo(
    () => scopeContainerDetailRowsToContainer(rows, detailRowsContainerGuidRef.current, containerGuid),
    [containerGuid, rows],
  )

  const locallyColumnFilteredRows = useMemo(
    () => applyContainerDetailColumnState(baseFilteredRows, columnFilters),
    [baseFilteredRows, columnFilters],
  )
  // 与后端统计口径一致：应用列筛选，但统计时排除 SelectedTags 自身。
  const localBaseTagStats = useMemo(
    () => buildContainerDetailTagStats(locallyColumnFilteredRows),
    [locallyColumnFilteredRows],
  )

  const applyCurrentClientFilters = (sourceRows: ContainerDetail[]) => {
    const nextTagFilteredRows = sourceRows.filter((row) => matchesContainerDetailSelectedTags(row, selectedTagFilters))
    return applyContainerDetailColumnState(nextTagFilteredRows, columnFilters, sortState)
  }

  const displayRows = useMemo(
    () => detailLoadMode === 'full'
      ? applyCurrentClientFilters(baseFilteredRows)
      : baseFilteredRows,
    // 分页模式完全信任后端的全局筛选和排序；本地只叠加未保存草稿。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [baseFilteredRows, columnFilters, detailLoadMode, selectedTagFilters, sortState],
  )
  const filteredRows = displayRows

  const setEditableCellRef = (
    rowKeyValue: string,
    columnKey: ContainerDetailEditableColumnKey,
    cell: ContainerDetailFocusableCell | null,
  ) => {
    const cellKey = buildContainerDetailEditableCellKey(rowKeyValue, columnKey)
    if (cell) {
      editableCellRefs.current.set(cellKey, cell)
      if (pendingEditableCellFocusKeyRef.current === cellKey) {
        pendingEditableCellFocusKeyRef.current = null
        window.requestAnimationFrame(() => {
          cell.focus()
          cell.select?.()
        })
      }
      return
    }
    editableCellRefs.current.delete(cellKey)
  }

  const blurActiveContainerDetailEditableCell = () => {
    const activeElement = document.activeElement
    if (activeElement instanceof HTMLElement) {
      // 创建新商品前主动结束当前输入，确保 InputNumber 的 blur 保存链路先落库。
      activeElement.blur()
    }
  }

  const handleEditableCellKeyDown = (
    row: ContainerDetail,
    columnKey: ContainerDetailEditableColumnKey,
    event: KeyboardEvent<HTMLElement>,
  ) => {
    const direction = containerDetailEditableDirectionByKey[event.key]
    if (!direction || event.nativeEvent.isComposing) {
      return
    }

    const currentRowKey = rowKey(row)
    const nextCell = getNextContainerDetailEditableCell(
      currentRowKey,
      columnKey,
      displayRows.map(rowKey),
      orderedEditableColumnKeys,
      direction,
    )
    if (!nextCell) {
      return
    }

    const nextCellKey = buildContainerDetailEditableCellKey(
      nextCell.rowKey,
      nextCell.columnKey as ContainerDetailEditableColumnKey,
    )
    event.preventDefault()
    event.currentTarget.blur()
    pendingEditableCellFocusKeyRef.current = nextCellKey
    detailTableRef.current?.scrollTo?.({ key: nextCell.rowKey })
    window.requestAnimationFrame(() => {
      // 方向键只切换焦点；自动保存列仍走 blur，价格列统一由“保存明细”落库。
      const targetCell = editableCellRefs.current.get(nextCellKey)
      if (!targetCell) {
        return
      }
      pendingEditableCellFocusKeyRef.current = null
      targetCell.focus()
      targetCell.select?.()
    })
  }

  const tagStats = detailLoadMode === 'full' ? localBaseTagStats : remoteTagStats

  const tagStatOptions = useMemo<{ value: ContainerDetailTagFilter; label: string; color?: string }[]>(() => [
    { value: 'all', label: t('containers.filters.allTags'), color: 'blue' },
    { value: 'new', label: t('containers.tags.newProduct'), color: 'cyan' },
    { value: 'existing', label: t('containers.tags.existingProduct'), color: 'purple' },
    { value: 'normal', label: t('containers.productTypes.normal'), color: 'default' },
    { value: 'set', label: t('containers.productTypes.set'), color: 'blue' },
    { value: 'multi', label: t('containers.productTypes.multiCode'), color: 'purple' },
    { value: 'setChild', label: t('containers.productTypes.setChild'), color: 'orange' },
    { value: 'noOemPrice', label: t('containers.filters.missingOemPrice'), color: 'orange' },
    { value: 'abnormalImport', label: t('containers.filters.abnormalImportPrice'), color: 'red' },
    { value: 'active', label: t('common.activeUpper'), color: 'success' },
    { value: 'inactive', label: t('common.inactiveUpper'), color: 'volcano' },
  ], [t])

  const selectedTagOptions = useMemo(
    () => tagStatOptions.filter((option) => option.value !== 'all' && selectedTagFilters.includes(option.value)),
    [selectedTagFilters, tagStatOptions],
  )

  const exportColumnOptions = useMemo(
    () => CONTAINER_DETAIL_EXPORT_COLUMNS.map((column) => ({
      label: t(column.labelKey, column.fallbackLabel),
      value: column.key,
    })),
    [t],
  )

  const setExportFormatWithDefaults = (format: ContainerDetailExportFormat) => {
    setExportFormat(format)
    setSelectedExportColumnKeys(
      format === 'pdf'
        ? DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMN_KEYS
        : DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS,
    )
  }

  const setTagFiltersFromSelect = (values: ContainerDetailTagFilter[]) => {
    setSelectedTagFilters(values.includes('all') ? [] : values)
  }

  const toggleTagFilter = (value: ContainerDetailTagFilter) => {
    if (value === 'all') {
      setSelectedTagFilters([])
      return
    }

    setSelectedTagFilters((current) => (
      current.includes(value)
        ? current.filter((item) => item !== value)
        : [...current, value]
    ))
  }

  const selectedRows = useMemo(
    () => displayRows.filter((row) => selectedRowKeys.includes(rowKey(row))),
    [displayRows, selectedRowKeys],
  )

  const hasHiddenSelectedRows = selectedRowKeys.length > 0 && selectedRows.length < selectedRowKeys.length
  const targetRows = selectedRowKeys.length ? selectedRows : displayRows

  const getRowsHguids = (scopeRows: ContainerDetail[]) => scopeRows
    .map((row) => row.hguid)
    .filter((value): value is string => Boolean(value))

  const buildDetailBatchScope = (scopeRows: ContainerDetail[] = targetRows): ContainerDetailBatchScope => ({
    selectedHguids: getRowsHguids(scopeRows),
  })

  const buildWholeContainerDetailBatchScope = (): ContainerDetailBatchScope => ({
    query: buildContainerDetailQuery({
      containerGuid,
      filters: {},
      pageNumber: 1,
      pageSize: CONTAINER_DETAIL_FULL_LOAD_LIMIT,
    }),
  })

  const getCostMissingFieldMessage = (field: ContainerDetailCostMissingField) => {
    if (field === 'exchangeRate') return t('containers.messages.missingExchangeRateForCost', '缺少汇率，无法重算成本')
    if (field === 'freight') return t('containers.messages.missingFreightForCost', '缺少运费，无法重算成本')
    return t('containers.messages.missingTotalVolumeForCost', '缺少总体积，无法重算成本')
  }

  const showCostRecalculateWarning = (fields: ContainerDetailCostMissingField[]) => {
    if (!fields.length) return false
    Modal.warning({
      title: t('containers.messages.costRecalculateMissingTitle', '无法重算成本'),
      content: (
        <Space direction="vertical" size={4}>
          {fields.map((field) => (
            <span key={field}>{getCostMissingFieldMessage(field)}</span>
          ))}
        </Space>
      ),
    })
    return true
  }

  const fetchAllRowsForCurrentQuery = async () => {
    if (detailLoadMode !== 'paged') {
      return displayRows
    }

    const allRows: ContainerDetail[] = []
    let pageNumber = 1
    let hasMore = true

    while (hasMore) {
      // 分页模式的导出和批量作用域仍由后端完整筛选，不能误缩到当前页。
      const result = await queryContainerProducts(containerGuid, buildContainerDetailQuery({
        containerGuid,
        filters: columnFilters,
        selectedTags: selectedTagFilters,
        sortState,
        pageNumber,
        pageSize: 500,
        includeTotal: false,
        includeStats: false,
      }))
      allRows.push(...result.items)
      hasMore = result.hasMore
      pageNumber += 1
    }

    return overlayPendingDetailChanges(allRows)
  }

  const fetchAllRowsForWholeContainer = async () => {
    const allRows: ContainerDetail[] = []
    let pageNumber = 1
    let hasMore = true

    while (hasMore) {
      // 整柜提交确认必须按当前货柜完整明细统计，不能沿用页面筛选或勾选范围。
      const result = await queryContainerProducts(containerGuid, buildContainerDetailQuery({
        containerGuid,
        filters: {},
        pageNumber,
        pageSize: 500,
        includeTotal: false,
        includeStats: false,
      }))
      allRows.push(...result.items)
      hasMore = result.hasMore
      pageNumber += 1
    }

    return allRows
  }

  const updateExportProgressState = (progress: number, progressMessage: string) => {
    setExportProgress(Math.max(0, Math.min(100, progress)))
    setExportProgressMessage(progressMessage)
  }

  const fetchAllRowsForWholeContainerExport = async () => {
    let allRows: ContainerDetail[] = []
    let pageNumber = 1
    let hasMore = true
    let expectedTotal: number | undefined

    while (hasMore) {
      const result = await queryContainerProducts(containerGuid, buildContainerDetailQuery({
        containerGuid,
        filters: {},
        sortState,
        pageNumber,
        pageSize: 500,
        includeTotal: pageNumber === 1,
        includeStats: false,
      }))
      allRows = mergeContainerDetailLoadedItems(allRows, result.items)
      if (pageNumber === 1 && result.totalComputed !== false) {
        expectedTotal = result.itemsTotal
      }
      hasMore = result.hasMore
      const dataProgress = hasMore
        ? Math.min(9, expectedTotal ? Math.ceil((allRows.length / Math.max(expectedTotal, 1)) * 10) : pageNumber)
        : 10
      updateExportProgressState(
        dataProgress,
        t('containers.messages.exportLoadingAllDetails', '正在加载整柜明细'),
      )
      pageNumber += 1
    }

    const rowsWithLocalEdits = applyContainerDetailLocalExportValues(allRows, baseFilteredRows)
    return prepareContainerDetailWholeExportRows(
      rowsWithLocalEdits,
      pendingDetailPatchesRef.current,
      sortState,
    )
  }

  const confirmSubmitContainer = (submitRows: ContainerDetail[]) => new Promise<boolean>((resolve) => {
    const createCount = submitRows.filter((row) => row.是否新商品).length
    const updateCount = submitRows.length - createCount

    Modal.confirm({
      title: t('containers.modals.submitContainerTitle', '提交货柜'),
      content: (
        <Space direction="vertical" size={8}>
          <Typography.Text>
            {t(
              'containers.modals.submitContainerContent',
              '确认提交当前货柜全部 {{total}} 条明细？本次将创建 {{created}} 个新商品，并更新 {{updated}} 个已有商品价格。',
              { total: submitRows.length, created: createCount, updated: updateCount },
            )}
          </Typography.Text>
          <Typography.Text type="secondary">
            {t(
              'containers.modals.submitContainerScopeHint',
              '提交范围为当前货柜全部未删除明细，不受当前筛选或勾选影响；成功后货柜状态会改为已完成。',
            )}
          </Typography.Text>
        </Space>
      ),
      okText: t('containers.actions.submitContainer', '提交货柜'),
      cancelText: t('common.cancel'),
      onOk: () => resolve(true),
      onCancel: () => resolve(false),
    })
  })

  const ensureTargetRowsVisible = () => {
    if (!hasHiddenSelectedRows) return true
    message.warning(t('containers.messages.selectedRowsHidden', '已选明细不在当前筛选结果中，请重新选择后再操作'))
    return false
  }

  const renderBatchActionContent = (
    count: number,
    extra?: ReactNode,
  ) => (
    <Space direction="vertical" size={6}>
      <Typography.Text>
        {t('containers.modals.batchActionContent', '确认对 {{count}} 条明细执行该批量操作？', { count })}
      </Typography.Text>
      {!selectedRowKeys.length ? (
        <Typography.Text type="warning">
          {t('containers.modals.batchActionAllHint', '当前未选择商品，确认后将按当前筛选范围执行全部匹配明细。')}
        </Typography.Text>
      ) : null}
      {extra}
    </Space>
  )

  const confirmBatchAction = (
    actionName: string,
    count: number,
    options: BatchActionConfirmOptions = {},
  ) => new Promise<boolean>((resolve) => {
    Modal.confirm({
      title: actionName,
      content: renderBatchActionContent(count, options.extra),
      okText: actionName,
      cancelText: t('common.cancel'),
      okButtonProps: options.danger ? { danger: true } : undefined,
      onOk: () => {
        if (options.beforeConfirm && !options.beforeConfirm()) {
          return Promise.reject()
        }
        resolve(true)
        return undefined
      },
      onCancel: () => resolve(false),
    })
  })

  const resolveBatchActionTargetRows = async () => {
    if (selectedRowKeys.length) return targetRows
    try {
      // 未勾选时确认数量和实际批量 scope 都以前端完整可见结果为准。
      return await fetchAllRowsForCurrentQuery()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.messages.loadDetailFailed', '加载货柜明细失败'))
      return null
    }
  }

  const confirmBatchRows = async (
    actionName: string,
    options: BatchActionConfirmOptions = {},
  ) => {
    if (!ensureTargetRowsVisible()) return null
    const scopedRows = await resolveBatchActionTargetRows()
    if (scopedRows == null) return null
    if (!scopedRows.length) {
      message.warning(t('containers.messages.selectBatchProducts', '请先选择要批量操作的商品'))
      return null
    }
    const confirmed = await confirmBatchAction(actionName, scopedRows.length, options)
    if (!confirmed) return null
    return scopedRows
  }

  const renderUpdateFieldSelector = <T extends string>(
    fields: readonly UpdateFieldOption<T>[],
    defaultFields: T[],
    onChange: (values: T[]) => void,
    hint?: ReactNode,
  ) => (
    <UpdateFieldSelector
      t={t}
      fields={fields}
      defaultFields={defaultFields}
      onChange={onChange}
      hint={hint}
    />
  )

  const requireSelectedUpdateFields = (selectedFields: string[]) => {
    if (selectedFields.length > 0) return true
    message.warning(t('containers.updateFields.selectAtLeastOne', '请至少选择一个更新字段'))
    return false
  }

  const confirmBatchRowsWithUpdateFields = async <T extends string>(
    actionName: string,
    fields: readonly UpdateFieldOption<T>[],
    defaultFields: T[],
    hint?: ReactNode,
  ): Promise<{ rows: ContainerDetail[]; fields: T[] } | null> => {
    let selectedFields = [...defaultFields]
    const rows = await confirmBatchRows(actionName, {
      extra: renderUpdateFieldSelector(fields, defaultFields, (values) => {
        selectedFields = values
      }, hint),
      beforeConfirm: () => requireSelectedUpdateFields(selectedFields),
    })
    return rows ? { rows, fields: selectedFields } : null
  }

  const confirmPushToHqUpdateFields = async (
    count: number,
  ): Promise<PushProductsToHqUpdateField[] | null> => {
    const actionName = t('containers.actions.pushToHq', '发送到 HQ')
    let selectedFields = [...defaultPushProductsToHqUpdateFields]
    const confirmed = await confirmBatchAction(actionName, count, {
      extra: renderUpdateFieldSelector(
        pushProductsToHqUpdateFieldOptions,
        defaultPushProductsToHqUpdateFields,
        (values) => {
          selectedFields = values
        },
        <Typography.Text type="secondary">
          {t(
            'containers.updateFields.hqCreateHint',
            '字段选择主要限制已有 HQ 记录更新；如果目标表需要新增记录，系统仍会写入创建该记录所需的完整字段。',
          )}
        </Typography.Text>,
      ),
      beforeConfirm: () => requireSelectedUpdateFields(selectedFields),
    })
    return confirmed ? selectedFields : null
  }

  const confirmPreviewedContainerDetailAction = async (
    operation: string,
    scope: ContainerDetailBatchScope,
    parameters: Record<string, unknown>,
    actionName: string,
    danger = false,
  ) => {
    const preview = await previewContainerDetailAction(containerGuid, { operation, scope, parameters })
    return new Promise<string | null>((resolve) => {
      Modal.confirm({
        title: t('containers.modals.confirmBatchPreviewTitle', '确认批量操作'),
        content: t(
          'containers.modals.confirmBatchPreviewContent',
          '{{action}}将影响 {{count}} 条明细（字段：{{fields}}）。数据变化或预览过期后需要重新确认。',
          { action: actionName, count: preview.affectedCount, fields: preview.fieldSummary.join('、') || '--' },
        ),
        okText: t('common.confirm', '确认'),
        cancelText: t('common.cancel'),
        okButtonProps: danger ? { danger: true } : undefined,
        onOk: () => resolve(preview.previewToken),
        onCancel: () => resolve(null),
      })
    })
  }

  const refreshExpiredContainerDetailActionPreview = async (
    operation: string,
    scope: ContainerDetailBatchScope,
    parameters: Record<string, unknown>,
  ) => {
    const preview = await previewContainerDetailAction(containerGuid, { operation, scope, parameters })
    Modal.info({
      title: t('containers.modals.batchPreviewChangedTitle', '批量操作数据已变化'),
      content: t('containers.modals.batchPreviewChangedContent', '最新预览影响 {{count}} 条明细（字段：{{fields}}），请再次确认后执行。', {
        count: preview.affectedCount,
        fields: preview.fieldSummary.join('、') || '--',
      }),
    })
  }

  const handleExpiredContainerDetailActionPreview = async (
    error: unknown,
    operation: string,
    scope: ContainerDetailBatchScope,
    parameters: Record<string, unknown>,
  ) => {
    if (!isContainerDetailActionPreviewExpired(error)) return false
    try {
      // 409 只刷新预览并展示新摘要，绝不在用户未再次确认时重放写请求。
      await refreshExpiredContainerDetailActionPreview(operation, scope, parameters)
    } catch (refreshError) {
      message.error(refreshError instanceof Error ? refreshError.message : t('containers.messages.batchPreviewRefreshFailed', '刷新批量预览失败'))
    }
    return true
  }

  const canCreateContainerProducts = access.canEditContainer && access.canManagePosProducts
  const canSubmitContainer = access.canEditContainer && access.canManagePosProducts
  const canBatchSetCategory = access.canEditContainer && access.canManagePosProducts
  const canAlignDomesticProductCode = access.canEditContainer && (access.isAdmin || access.hasPermission(P.Products.Edit))
  const pendingDetailPatchList = useMemo(() => Object.values(pendingDetailPatches), [pendingDetailPatches])
  const pendingDetailPatchCount = pendingDetailPatchList.length
  const conflictByFieldKey = useMemo(() => new Map(
    pendingDetailConflicts.map((conflict) => [`${conflict.hguid}:${conflict.field}`, conflict] as const),
  ), [pendingDetailConflicts])
  const autoSaveFailureMap = useMemo(() => new Map(
    autoSaveSnapshot.failures.map((failure) => [`${failure.hguid}:${failure.field}`, failure] as const),
  ), [autoSaveSnapshot.failures])

  const getAutoSaveFailure = (
    row: ContainerDetail,
    field: ContainerDetailAutoSaveField,
  ): ContainerDetailAutoSaveFailure | undefined => (
    row.hguid ? autoSaveFailureMap.get(`${row.hguid}:${field}`) : undefined
  )

  const retryFailedAutoSaves = () => {
    const contextKey = autoSaveContextKeyRef.current
    if (contextKey) autoSaveQueueRef.current?.retryFailed(contextKey)
  }

  const resolveConcurrencyConflict = (row: ContainerDetail, field: string) => (
    row.hguid ? conflictByFieldKey.get(`${row.hguid}:${field}`) : undefined
  )

  const renderConcurrentEditableField = (row: ContainerDetail, field: string, child: ReactNode) => {
    const conflict = resolveConcurrencyConflict(row, field)
    if (!conflict) return child
    return (
      <span className="container-detail-concurrent-field" title={conflict.message ?? t('containers.messages.concurrentFieldUpdate', '服务器已更新该字段')}>
        {child}
        <span className="container-detail-concurrent-field-badge">{t('containers.text.serverUpdated', '服务器已更新')}</span>
      </span>
    )
  }

  const acceptServerConflict = async (conflict: ContainerDetailFieldConflict) => {
    const fieldVersion = pendingDetailFieldVersionsRef.current[`${conflict.hguid}:${conflict.field}`]
    if (!fieldVersion) return
    const cleared = clearContainerDetailDraftFieldsIfVersionMatches(
      getContainerDetailDraftStorage(),
      currentUserGuid,
      containerGuid,
      { [`${conflict.hguid}:${conflict.field}`]: fieldVersion },
    )
    if (!cleared.persisted) {
      message.error(t('containers.messages.localDraftClearFailed', '本地草稿清空失败，已保留当前修改'))
      return
    }
    pendingDetailOverrideAcknowledgementsRef.current = clearContainerDetailOverrideAcknowledgements(
      pendingDetailOverrideAcknowledgementsRef.current,
      { [`${conflict.hguid}:${conflict.field}`]: fieldVersion },
    )
    const nextDraft = readContainerDetailDraft(getContainerDetailDraftStorage(), currentUserGuid, containerGuid)
    applyPendingDetailDraftState(nextDraft, false)
    setPendingDetailConflicts((current) => current.filter((item) => (
      item.hguid !== conflict.hguid || item.field !== conflict.field
    )))
    await reloadCurrentDetailRef.current()
  }

  const acceptAllServerConflicts = async () => {
    const fieldVersions = pendingDetailConflicts.reduce<Record<string, string>>((next, conflict) => {
      const key = `${conflict.hguid}:${conflict.field}`
      const version = pendingDetailFieldVersionsRef.current[key]
      if (version) next[key] = version
      return next
    }, {})
    if (!Object.keys(fieldVersions).length) return
    // 每个字段各自按版本清除；同用户其他标签刚写入的新草稿绝不能被整批操作误删。
    const cleared = clearContainerDetailDraftFieldsIfVersionMatches(
      getContainerDetailDraftStorage(), currentUserGuid, containerGuid, fieldVersions,
    )
    if (!cleared.persisted) {
      message.error(t('containers.messages.localDraftClearFailed', '本地草稿清空失败，已保留当前修改'))
      return
    }
    const retainedKeys = new Set(cleared.newerFieldVersionKeys)
    pendingDetailOverrideAcknowledgementsRef.current = clearContainerDetailOverrideAcknowledgements(
      pendingDetailOverrideAcknowledgementsRef.current,
      Object.fromEntries(Object.entries(fieldVersions).filter(([key]) => !retainedKeys.has(key))),
    )
    applyPendingDetailDraftState(readContainerDetailDraft(getContainerDetailDraftStorage(), currentUserGuid, containerGuid), false)
    setPendingDetailConflicts((current) => current.filter((conflict) => retainedKeys.has(`${conflict.hguid}:${conflict.field}`)))
    await reloadCurrentDetailRef.current()
  }

  const keepMineConflict = (conflict: ContainerDetailFieldConflict) => {
    Modal.confirm({
      title: t('containers.modals.confirmOverrideConcurrentField', '确认覆盖服务器更新'),
      content: t('containers.messages.confirmOverrideConcurrentField', '将以你的值覆盖刚刚看到的服务器值；若其间再次修改，系统会再次提示冲突。'),
      okText: t('containers.actions.keepMyValue', '保留我的值'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        const key = `${conflict.hguid}:${conflict.field}`
        const patch = pendingDetailPatchesRef.current[conflict.hguid]
        const fieldVersion = pendingDetailFieldVersionsRef.current[key]
        if (!patch || !fieldVersion) return
        // 只记录确认令牌，首次编辑的 expected token 绝不能前移，确保后端走覆盖审计路径。
        pendingDetailOverrideAcknowledgementsRef.current[key] = {
          token: conflict.currentServerFieldToken,
          fieldVersion,
        }
        setPendingDetailConflicts((current) => current.filter((item) => (
          item.hguid !== conflict.hguid || item.field !== conflict.field
        )))
        // 下一次保存携带新的 expected token，后端将其作为 override acknowledgement 校验。
        await savePendingDetails()
      },
    })
  }

  const keepAllMineConflicts = () => {
    Modal.confirm({
      title: t('containers.modals.confirmOverrideConcurrentFields', '确认批量覆盖服务器更新'),
      content: t('containers.messages.confirmOverrideConcurrentFields', '将以你的值覆盖刚刚看到的服务器值。任何再次变化的字段仍会单独提示冲突。'),
      okText: t('containers.actions.keepMyValue', '批量保留我的值'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        const eligible = pendingDetailConflicts.filter((conflict) => (
          Boolean(pendingDetailPatchesRef.current[conflict.hguid])
          && Boolean(pendingDetailFieldVersionsRef.current[`${conflict.hguid}:${conflict.field}`])
        ))
        if (!eligible.length) return
        eligible.forEach((conflict) => {
          const key = `${conflict.hguid}:${conflict.field}`
          const fieldVersion = pendingDetailFieldVersionsRef.current[key]
          if (!fieldVersion) return
          // 每个 ack 只确认当前看见的令牌；服务端会拒绝确认之后的再次修改。
          pendingDetailOverrideAcknowledgementsRef.current[key] = {
            token: conflict.currentServerFieldToken,
            fieldVersion,
          }
        })
        const eligibleKeys = new Set(eligible.map((conflict) => `${conflict.hguid}:${conflict.field}`))
        setPendingDetailConflicts((current) => current.filter((conflict) => !eligibleKeys.has(`${conflict.hguid}:${conflict.field}`)))
        await savePendingDetails()
      },
    })
  }

  const ensureNoPendingDetails = () => {
    if (!pendingDetailPatchCount) return true
    // 跨库和后台动作必须等待价格、英文名称等本地草稿统一落库。
    message.warning(t('containers.messages.savePendingDetailsFirst', '请先点击“保存明细”保存待提交的明细修改'))
    return false
  }

  // 多行一次性合并补丁，只触发一次 setRows；列粘贴批量写入时避免逐行重渲染。
  const patchRows = (patchesByKey: ReadonlyMap<string, Partial<ContainerDetail>>) => {
    if (!patchesByKey.size) return
    const nextRows = rowsRef.current.map((item) => {
      const patch = patchesByKey.get(rowKey(item))
      return patch ? mergeContainerDetailPatch(item, patch) : item
    })
    rowsRef.current = nextRows
    if (autoSaveContextKeyRef.current) {
      updateAutoSaveContextRows(autoSaveContextKeyRef.current, nextRows, containerRef.current)
    }
    setRows(nextRows)
  }

  const patchRow = (key: string, patch: Partial<ContainerDetail>) => {
    patchRows(new Map([[key, patch]]))
  }

  const patchAutoSaveRow = (
    row: ContainerDetail,
    patch: ContainerDetailAutoSavePatch,
  ) => {
    patchRow(rowKey(row), patch)
  }

  const captureAutoSaveEditBaseline = (
    row: ContainerDetail,
    field: ContainerDetailAutoSaveField,
    value: unknown,
  ) => {
    const key = `${rowKey(row)}:${field}`
    if (!autoSaveEditBaselineRef.current.has(key)) {
      autoSaveEditBaselineRef.current.set(key, value)
    }
  }

  const restoreAutoSaveEditBaseline = (
    row: ContainerDetail,
    field: ContainerDetailAutoSaveField,
  ) => {
    const key = `${rowKey(row)}:${field}`
    if (!autoSaveEditBaselineRef.current.has(key)) return
    const value = autoSaveEditBaselineRef.current.get(key)
    autoSaveEditBaselineRef.current.delete(key)
    patchRow(rowKey(row), { [field]: value } as Partial<ContainerDetail>)
  }

  const clearAutoSaveEditBaseline = (row: ContainerDetail, field: ContainerDetailAutoSaveField) => {
    autoSaveEditBaselineRef.current.delete(`${rowKey(row)}:${field}`)
  }

  const queuePendingDetailUpdates = (updates: PendingContainerDetailPatch[]) => {
    const baselineTokens = { ...pendingDetailFieldBaselineTokensRef.current }
    updates.forEach((update) => {
      getSubmittedContainerDetailFields(update).forEach((field) => {
        // 新一轮编辑必须显式重新确认，不能复用上一次冲突的覆盖授权。
        delete pendingDetailOverrideAcknowledgementsRef.current[`${update.hguid}:${field}`]
      })
    })
    updates.forEach((update) => {
      const row = rowsRef.current.find((item) => item.hguid === update.hguid)
      if (!row?.serverFieldTokens) return
      Object.keys(update).forEach((field) => {
        if (field === 'hguid' || field === 'ClearEnglishName') return
        const normalizedField = field === 'ClearEnglishName' ? '英文名称' : field
        const key = `${update.hguid}:${normalizedField}`
        // 同一草稿字段的基线只能取首次编辑时的服务端令牌，不能被后续刷新静默前移。
        if (!baselineTokens[key] && row.serverFieldTokens?.[normalizedField]) {
          baselineTokens[key] = row.serverFieldTokens[normalizedField]
        }
      })
      if (update.ClearEnglishName === true) {
        const key = `${update.hguid}:英文名称`
        if (!baselineTokens[key] && row.serverFieldTokens?.英文名称) baselineTokens[key] = row.serverFieldTokens.英文名称
      }
    })
    applyPendingDetailDraftState({
      pendingPatches: updates.reduce(
        (next, update) => mergePendingContainerDetailPatch(next, update),
        pendingDetailPatchesRef.current,
      ),
      // 用户修正后只清除对应字段的旧错误，其他字段失败仍保留。
      failures: clearContainerDetailDraftFailuresForPatches(
        pendingDetailFailuresRef.current,
        updates,
      ),
      fieldBaselineTokens: baselineTokens,
    }, true, updates, true)
  }

  const getCurrentDetailDraftContext = (): ContainerDetailDraftContext => ({
    userGuid: currentUserGuid,
    containerGuid,
    draftIdentity: pendingDetailDraftIdentityRef.current,
  })

  const isCurrentDetailDraftContext = (context: ContainerDetailDraftContext) => (
    Boolean(context.userGuid)
    && context.userGuid === currentUserGuid
    && context.containerGuid === currentContainerGuidRef.current
    && context.containerGuid === containerGuid
    && context.draftIdentity === pendingDetailDraftIdentityRef.current
    && context.draftIdentity === buildContainerDetailDraftStorageKey(currentUserGuid, containerGuid)
  )

  const hasDetailDraftFilter = selectedTagFilters.length > 0 || Object.values(columnFilters).some((value) => {
    if (Array.isArray(value)) return value.length > 0
    if (typeof value === 'object' && value !== null) return Object.values(value).some((item) => item != null && item !== '')
    return value != null && value !== ''
  })
  const hasRemoteDetailDraftFilter = detailLoadMode === 'paged' && hasDetailDraftFilter

  const locateFirstPendingDetailField = (scanFromFirstPage = true) => {
    const pendingLocate = pendingDraftLocateAfterAppendRef.current
    const hasMatchedPendingReset = shouldConsumePendingContainerDetailLocate({
      pendingQueryKey: pendingLocate?.queryKey ?? '',
      activeQueryKey: activeLoadQueryKey,
      loadedQueryKey: lastLoadedContainerDetailSuccessRef.current?.containerGuid === containerGuid
        ? lastLoadedContainerDetailSuccessRef.current.queryKey
        : undefined,
      pendingGeneration: pendingLocate?.generation ?? -1,
      loadedGeneration: lastLoadedContainerDetailSuccessRef.current?.containerGuid === containerGuid
        ? lastLoadedContainerDetailSuccessRef.current.generation
        : undefined,
      isResetLoading: detailLoading,
    })
    if (pendingLocate && shouldRetryPendingContainerDetailLocateReset({
      hasLocalFilter: hasDetailDraftFilter,
      awaitingUnfilteredReset: Boolean(pendingLocate?.awaitingUnfilteredReset),
      hasMatchedReset: hasMatchedPendingReset,
    })) {
      // 上一次无筛选 reset 失败时，只有用户再次点击定位才主动重试，避免后台循环请求。
      pendingLocate.queryKey = ''
      pendingLocate.generation = containerDetailReconcileGenerationRef.current + 1
      void reloadCurrentDetailRef.current()
      return
    }
    if (hasDetailDraftFilter) {
      // 定位草稿优先恢复完整数据集；仅在实际数据耗尽后才可判断商品已删除。
      pendingDraftLocateAfterAppendRef.current = createContainerDetailDraftLocateResetPlan({
        hasRemoteFilter: hasRemoteDetailDraftFilter,
        activeQueryKey: activeLoadQueryKey,
        generation: containerDetailReconcileGenerationRef.current,
      })
      setSelectedTagFilters([])
      setColumnFilters({})
      return
    }
    const fields: { field: string; columnKey: ContainerDetailEditableColumnKey }[] = [
      { field: '进口价格', columnKey: 'importPrice' },
      { field: '贴牌价格', columnKey: 'oemPrice' },
      { field: '英文名称', columnKey: 'englishName' },
    ]
    let target: { rowKey: string; columnKey: ContainerDetailEditableColumnKey } | null = null
    for (const failedOnly of [true, false]) {
      for (const row of displayRows) {
        const key = rowKey(row)
        const pendingPatch = pendingDetailPatchesRef.current[row.hguid]
        if (!pendingPatch) continue
        for (const candidate of fields) {
          const hasField = candidate.field === '进口价格'
            ? pendingPatch.进口价格 != null
            : candidate.field === '贴牌价格'
              ? pendingPatch.贴牌价格 != null
              : pendingPatch.英文名称 !== undefined || pendingPatch.ClearEnglishName === true
          const hasFailure = Boolean(getContainerDetailDraftFieldFailure(
            pendingDetailFailuresRef.current,
            row.hguid,
            candidate.field,
          ))
          if (hasField && (!failedOnly || hasFailure)) {
            target = { rowKey: key, columnKey: candidate.columnKey }
            break
          }
        }
        if (target) break
      }
      if (target) break
    }

    if (
      !target
      && detailLoadMode === 'paged'
      && scanFromFirstPage
      && detailPageNumber !== 1
    ) {
      // 用户可能从任意页点击定位；先回到第 1 页，避免只向后查找而漏掉前页草稿。
      pendingDraftLocateAfterAppendRef.current = {
        awaitingUnfilteredReset: true,
        queryKey: '',
        generation: containerDetailReconcileGenerationRef.current + 1,
      }
      setDetailPagingState({
        containerGuid,
        mode: 'paged',
        pageNumber: 1,
        pageSize: detailPageSize,
        scopeKey: pagedDetailScopeKey,
      })
      return
    }

    if (!target && !hasDetailDraftFilter && detailHasMore) {
      // 未启用筛选时草稿可能在后续页；等待下一页成功返回后再继续查找。
      pendingDraftLocateAfterAppendRef.current = {
        awaitingUnfilteredReset: true,
        queryKey: '',
        generation: containerDetailReconcileGenerationRef.current + 1,
      }
      void loadNextDetailChunk()
      return
    }

    if (!target) {
      message.warning(t(
        'containers.messages.pendingDetailNotFound',
        '未找到未保存项，可能已被删除或保存',
      ))
      return
    }

    const cellKey = buildContainerDetailEditableCellKey(target.rowKey, target.columnKey)
    pendingEditableCellFocusKeyRef.current = cellKey
    detailTableRef.current?.scrollTo?.({ key: target.rowKey })
    window.requestAnimationFrame(() => {
      const cell = editableCellRefs.current.get(cellKey)
      if (!cell) return
      pendingEditableCellFocusKeyRef.current = null
      cell.focus()
      cell.select?.()
    })
  }

  useEffect(() => {
    const pendingLocate = pendingDraftLocateAfterAppendRef.current
    if (!pendingLocate || detailLoading) return
    if (pendingLocate.awaitingUnfilteredReset) {
      if (hasDetailDraftFilter) return
      if (containerDetailReconcileGenerationRef.current < pendingLocate.generation) return
      pendingLocate.queryKey = activeLoadQueryKey
      if (!shouldConsumePendingContainerDetailLocate({
        pendingQueryKey: pendingLocate.queryKey,
        activeQueryKey: activeLoadQueryKey,
        loadedQueryKey: lastLoadedContainerDetailSuccessRef.current?.containerGuid === containerGuid
          ? lastLoadedContainerDetailSuccessRef.current.queryKey
          : undefined,
        pendingGeneration: pendingLocate.generation,
        loadedGeneration: lastLoadedContainerDetailSuccessRef.current?.containerGuid === containerGuid
          ? lastLoadedContainerDetailSuccessRef.current.generation
          : undefined,
        isResetLoading: detailLoading,
      })) {
        return
      }
      pendingLocate.awaitingUnfilteredReset = false
    }
    pendingDraftLocateAfterAppendRef.current = null
    void locateFirstPendingDetailField(false)
    // rows 变化后必须重新读取当前分页与筛选状态，直到找到目标或分页耗尽。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, detailLoading, detailHasMore, detailPageNumber, hasDetailDraftFilter, activeLoadQueryKey, containerGuid])

  const clearPendingDetailDraft = () => {
    if (!pendingDetailFieldCount || detailSaveSubmitting) return
    const draftContext = getCurrentDetailDraftContext()
    const draftFieldVersionSnapshot = captureContainerDetailDraftFieldVersions(
      pendingDetailFieldVersionsRef.current,
      Object.values(pendingDetailPatchesRef.current),
    )
    Modal.confirm({
      title: t('containers.modals.clearLocalDraftTitle', '确认清空本地草稿'),
      content: t(
        'containers.modals.clearLocalDraftContent',
        '将丢弃当前货柜 {{count}} 项未保存修改，此操作无法撤销。',
        { count: pendingDetailFieldCount },
      ),
      okText: t('containers.actions.clearDraft', '清空草稿'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        if (!isCurrentDetailDraftContext(draftContext)) {
          message.warning(t('containers.messages.draftContextChanged', '用户或货柜已切换，已取消本次草稿操作'))
          return
        }
        const cleared = clearContainerDetailDraftFieldsIfVersionMatches(
          getContainerDetailDraftStorage(),
          draftContext.userGuid,
          draftContext.containerGuid,
          draftFieldVersionSnapshot,
        )
        if (!cleared.persisted) {
          isDetailDraftMemoryOnlyRef.current = true
          setIsDetailDraftMemoryOnly(true)
          throw new Error(t('containers.messages.localDraftClearFailed', '本地草稿清空失败，已保留当前修改'))
        }
        pendingDetailOverrideAcknowledgementsRef.current = clearContainerDetailOverrideAcknowledgements(
          pendingDetailOverrideAcknowledgementsRef.current,
          Object.fromEntries(Object.entries(draftFieldVersionSnapshot).filter(([key]) => !cleared.newerFieldVersionKeys.includes(key))),
        )
        const remainingDraft = readContainerDetailDraft(
          getContainerDetailDraftStorage(),
          draftContext.userGuid,
          draftContext.containerGuid,
        )
        applyPendingDetailDraftState(remainingDraft, false)
        setRows([])
        await reloadCurrentDetailRef.current()
        message.success(t('containers.messages.localDraftCleared', '已清空本地草稿'))
      },
    })
  }

  type PendingDetailPatchItem = {
    row: ContainerDetail
    patch: Pick<Partial<ContainerDetail>, '进口价格' | '贴牌价格' | '英文名称'>
  }

  // 批量标记草稿：页面行只合并一次，localStorage 草稿也只写一次。
  const markPendingDetailPatches = (items: PendingDetailPatchItem[]) => {
    const visiblePatches = new Map<string, Partial<ContainerDetail>>()
    items.forEach(({ row, patch }) => {
      const isExistingProductRetailPatch = !row.是否新商品 && '贴牌价格' in patch
      const visiblePatch: Partial<ContainerDetail> = isExistingProductRetailPatch
        ? {
            ...patch,
            // 已有商品的零售价列绑定仓库实时价，输入后同步刷新两套字段，避免保存前显示回跳。
            warehouseOEMPrice: patch.贴牌价格,
            WarehouseOEMPrice: patch.贴牌价格,
          }
        : patch
      const key = rowKey(row)
      visiblePatches.set(key, { ...visiblePatches.get(key), ...visiblePatch })
    })
    patchRows(visiblePatches)
    const updates = items.flatMap(({ row, patch }) => (row.hguid ? [{ hguid: row.hguid, ...patch }] : []))
    if (!updates.length) return
    queuePendingDetailUpdates(updates)
  }

  const markPendingDetailPatch = (
    row: ContainerDetail,
    patch: PendingDetailPatchItem['patch'],
  ) => {
    markPendingDetailPatches([{ row, patch }])
  }

  const handlePendingImportPriceBlur = (row: ContainerDetail, rawValue: string) => {
    const currentValue = rowsRef.current.find((item) => rowKey(item) === rowKey(row))?.进口价格
    const value = resolveContainerDetailPendingPriceOnBlur(rawValue, currentValue)
    if (value === undefined) return

    // 兼容只改变原生输入值、未触发 React onChange 的粘贴工具，失焦时补入持久草稿。
    markPendingDetailPatch(row, { 进口价格: value })
  }

  const getDetailSaveFailedMessage = () => t('containers.messages.detailSaveFailed', '货柜明细保存失败，请稍后重试')

  const handleDetailSaveError = (error: unknown) => {
    message.error(error instanceof Error ? error.message : getDetailSaveFailedMessage())
  }

  const trackDetailSavePromise = <T,>(
    saveKeys: string[],
    promise: Promise<T>,
    failureKeys = failedDetailSaveKeysRef.current,
    saveContainerGuid = containerGuid,
    saveDraftIdentity = pendingDetailDraftIdentityRef.current,
  ) => {
    const trackedPromise = promise.catch((error) => {
      if (
        currentContainerGuidRef.current === saveContainerGuid
        && pendingDetailDraftIdentityRef.current === saveDraftIdentity
      ) {
        saveKeys.forEach((saveKey) => failureKeys.add(saveKey))
      }
      throw error
    }).then((value) => {
      saveKeys.forEach((saveKey) => failureKeys.delete(saveKey))
      return value
    }).finally(() => {
      pendingDetailSavePromisesRef.current.delete(trackedPromise)
      if (
        currentContainerGuidRef.current === saveContainerGuid
        && pendingDetailDraftIdentityRef.current === saveDraftIdentity
      ) {
        setPendingDetailSaveCount(
          pendingDetailSavePromisesRef.current.size
          + autoSaveSnapshotRef.current.pendingFieldCount
          + autoSaveSnapshotRef.current.runningFieldCount,
        )
      }
    })
    pendingDetailSavePromisesRef.current.add(trackedPromise)
    setPendingDetailSaveCount(
      pendingDetailSavePromisesRef.current.size
      + autoSaveSnapshotRef.current.pendingFieldCount
      + autoSaveSnapshotRef.current.runningFieldCount,
    )
    return trackedPromise
  }

  const flushContainerDetailAutoSaves = async () => {
    const contextKey = autoSaveContextKeyRef.current
    if (!contextKey) return
    await autoSaveQueueRef.current?.drain(contextKey)
  }

  const flushPendingDetailSaves = async () => {
    await flushContainerDetailAutoSaves()
    const pendingSaves = Array.from(pendingDetailSavePromisesRef.current)
    if (pendingSaves.length) {
      await Promise.all(pendingSaves)
    }
    if (
      failedDetailSaveKeysRef.current.size > 0
      || failedPendingDetailSaveKeysRef.current.size > 0
    ) {
      throw new Error(getDetailSaveFailedMessage())
    }
  }

  const drainAutoSavesBeforeAction = async () => {
    blurActiveContainerDetailEditableCell()
    try {
      await flushContainerDetailAutoSaves()
      return true
    } catch (error) {
      handleDetailSaveError(error)
      return false
    }
  }

  const refreshContainerDetail = async () => {
    if (!await drainAutoSavesBeforeAction()) return
    await loadData()
  }

  const waitForPendingDetailSavesForExport = async () => {
    try {
      await flushContainerDetailAutoSaves()
    } catch {
      // 导出允许保留自动保存失败的页面值，下面仍继续等待其他在途保存。
    }
    const pendingSaves = Array.from(pendingDetailSavePromisesRef.current)
    if (pendingSaves.length) {
      // 导出会选择性叠加页面内存值；保存失败不应阻断用户取得当前数据快照。
      await Promise.allSettled(pendingSaves)
    }
  }

  const buildAutoSaveUpdatesFromLatestRows = (
    intents: ContainerDetailAutoSaveIntent[],
    contextSnapshot: {
      rows: ContainerDetail[]
      container: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null
    },
  ) => (
    intents.map((intent) => {
      const latestRow = contextSnapshot.rows.find((item) => item.hguid === intent.hguid)
      if (!latestRow) {
        throw new Error(t('containers.messages.detailNotFound', '货柜明细不存在或已被删除'))
      }
      return buildContainerDetailAutoSaveUpdate(latestRow, intent.patch, contextSnapshot.container)
    })
  )

  autoSaveSendBatchRef.current = async (contextKey, intents) => {
    const contextSnapshot = autoSaveContextSnapshotsRef.current.get(contextKey)
    if (!contextSnapshot) {
      throw new Error(t('containers.messages.detailSaveContextMissing', '货柜明细保存上下文已失效'))
    }
    // 只在该批真正出队时读取对应上下文快照，避免连续编辑或切页后发送旧派生值。
    const rawUpdates = buildAutoSaveUpdatesFromLatestRows(intents, contextSnapshot)
    const persistedBaselineTokens = captureContainerDetailDraftFieldBaselineTokens(
      contextSnapshot.fieldBaselineTokens,
      rawUpdates,
    )
    const updates = rawUpdates.map((update) => ({
      ...update,
      expectedServerFieldTokens: {
        ...Object.fromEntries(Object.keys(update).flatMap((field) => {
        if (field === 'hguid' || field === 'ClearEnglishName') return []
        const token = contextSnapshot.rows.find((row) => row.hguid === update.hguid)?.serverFieldTokens?.[field]
        return token ? [[field, token]] : []
        })),
        // 用户直接编辑字段优先使用持久化的首次基线；派生成本字段仍使用同一快照中的服务器令牌。
        ...(persistedBaselineTokens[update.hguid] ?? {}),
      },
    }))
    const fieldVersions = captureContainerDetailDraftFieldVersions(
      contextSnapshot.fieldVersions,
      updates,
    )
    const result = await batchUpdateDetails(
      contextSnapshot.containerGuid,
      updates,
    )
    // A 货柜请求返回时页面可能已切到 B。持久化清理仍按 A 的 namespace/version 安全执行，
    // 但所有当前页面状态与 ref 只能由仍然活跃的同一自动保存上下文更新。
    const canApplyAutoSaveResultToCurrentContext = () => (
      contextKey === autoSaveContextKeyRef.current
      && contextSnapshot.userGuid === currentUserGuid
      && contextSnapshot.draftIdentity === pendingDetailDraftIdentityRef.current
    )
    const failures = [...result.validationErrors, ...result.conflicts.map((conflict) => ({
      hguid: conflict.hguid,
      field: conflict.field,
      code: conflict.code,
      message: conflict.message ?? '服务器已更新该字段',
    }))]
    if (result.conflicts.length > 0 && canApplyAutoSaveResultToCurrentContext()) {
      setPendingDetailConflicts((current) => [...current, ...result.conflicts.filter((next) => !current.some((item) => item.hguid === next.hguid && item.field === next.field))])
      setConflictDrawerOpen(true)
    }
    const successfullySavedFieldVersions = captureSuccessfullySavedContainerDetailDraftFieldVersions(
      fieldVersions, updates, failures,
    )
    if (canApplyAutoSaveResultToCurrentContext()) {
      pendingDetailOverrideAcknowledgementsRef.current = clearContainerDetailOverrideAcknowledgements(
        pendingDetailOverrideAcknowledgementsRef.current,
        successfullySavedFieldVersions,
      )
    }
    const clearResult = clearContainerDetailDraftFieldsIfVersionMatches(
      getContainerDetailDraftStorage(), contextSnapshot.userGuid, contextSnapshot.containerGuid,
      successfullySavedFieldVersions,
    )
    if (clearResult.persisted && canApplyAutoSaveResultToCurrentContext()) {
      applyPendingDetailDraftState(readContainerDetailDraft(
        getContainerDetailDraftStorage(),
        contextSnapshot.userGuid,
        contextSnapshot.containerGuid,
      ), false)
    }
    return { ...result, validationErrors: failures }
  }

  type AutoSavePatchItem = { row: ContainerDetail; patch: ContainerDetailAutoSavePatch }

  // 批量入队自动保存：草稿只写一次、页面行只合并一次，再逐行进入自动保存队列（队列内部会合批发送）。
  const enqueueAutoSavePatches = (items: AutoSavePatchItem[]) => {
    const contextKey = autoSaveContextKeyRef.current
    if (
      !access.canEditContainer
      || resolveContainerDetailAutoSaveLifecycleAction(containerDetailTabActiveRef.current, contextKey) !== 'attach'
    ) return
    const validItems = items.filter((item): item is AutoSavePatchItem & { row: { hguid: string } } => Boolean(item.row.hguid))
    if (!validItems.length) return
    // 自动保存也先进入同一份 localStorage 草稿；刷新、关闭或网络失败都不会丢字段。
    queuePendingDetailUpdates(validItems.map(({ row, patch }) => ({ hguid: row.hguid, ...patch })))
    const latestRowsByHguid = new Map(rowsRef.current.flatMap((item) => (item.hguid ? [[item.hguid, item] as const] : [])))
    const optimisticUpdates = new Map<string, UpdateContainerDetailRequest>()
    validItems.forEach(({ row, patch }) => {
      // 同一行多次出现时以已合并的乐观值为基线，保证派生成本字段按最终值计算。
      const baseRow = optimisticUpdates.has(row.hguid)
        ? mergeContainerDetailPatch(latestRowsByHguid.get(row.hguid) ?? row, optimisticUpdates.get(row.hguid) as Partial<ContainerDetail>)
        : latestRowsByHguid.get(row.hguid) ?? row
      optimisticUpdates.set(row.hguid, {
        ...optimisticUpdates.get(row.hguid),
        ...buildContainerDetailAutoSaveUpdate(baseRow, patch, containerRef.current),
      })
    })
    const nextRows = rowsRef.current.map((item) => {
      const optimisticUpdate = item.hguid ? optimisticUpdates.get(item.hguid) : undefined
      return optimisticUpdate
        ? mergeContainerDetailPatch(item, optimisticUpdate as Partial<ContainerDetail>)
        : item
    })
    rowsRef.current = nextRows
    updateAutoSaveContextRows(contextKey, nextRows, containerRef.current)
    setRows(nextRows)
    validItems.forEach(({ row, patch }) => autoSaveQueueRef.current?.enqueue(contextKey, row.hguid, patch))
  }

  const enqueueAutoSavePatch = (row: ContainerDetail, patch: ContainerDetailAutoSavePatch) => {
    enqueueAutoSavePatches([{ row, patch }])
  }

  const saveRowPatch = async (row: ContainerDetail, patch: ContainerDetailAutoSavePatch) => {
    if (!access.canEditContainer || !row.hguid) return
    // 商品名称最终写回 DomesticProduct；提交/创建商品前会 drain 此队列，确保后台读取最新值。
    enqueueAutoSavePatch(row, patch)
  }

  const buildPendingDetailSavePlan = (): PendingContainerDetailPageSavePlan | null => {
    // 直接操作刚入队时 React 尚未提交渲染；必须从同步 ref 取草稿，避免漏掉本次字段。
    const currentPendingDetailPatchList = Object.values(pendingDetailPatchesRef.current)
    if (!currentPendingDetailPatchList.length) {
      message.warning(t('containers.messages.noPendingDetails', '没有待保存的明细修改'))
      return null
    }
    const plan = buildPendingContainerDetailSavePlan(currentPendingDetailPatchList)
    const expectedServerFieldTokens = captureContainerDetailDraftFieldBaselineTokens(
      pendingDetailFieldBaselineTokensRef.current,
      plan.detailUpdates,
    )
    const overrideAcknowledgements = buildContainerDetailOverrideAcknowledgements(
      plan.detailUpdates,
      expectedServerFieldTokens,
      pendingDetailFieldVersionsRef.current,
      pendingDetailOverrideAcknowledgementsRef.current,
    )
    const detailUpdates = plan.detailUpdates.map((update) => ({
      ...update,
      expectedServerFieldTokens: expectedServerFieldTokens[update.hguid],
      overrideAcknowledgements: overrideAcknowledgements[update.hguid],
    }))
    const saveKeys = detailUpdates.flatMap((update) => (
      buildContainerDetailSaveFailureKeys(update.hguid, update)
    ))
    return {
      ...plan,
      detailUpdates,
      saveKeys,
      expectedServerFieldTokens,
      overrideAcknowledgements,
      draftContext: getCurrentDetailDraftContext(),
      draftFieldVersionSnapshot: captureContainerDetailDraftFieldVersions(
        pendingDetailFieldVersionsRef.current,
        detailUpdates,
      ),
    }
  }

  const confirmSavePendingDetails = (plan: PendingContainerDetailPageSavePlan) => new Promise<boolean>((resolve) => {
    const invalidEnglishNameCount = plan.pendingPatches.filter((patch) => (
      Boolean(getPendingContainerDetailEnglishNameError(patch))
    )).length
    Modal.confirm({
      title: t('containers.modals.savePendingDetailsTitle', '确认保存明细'),
      content: (
        <Space direction="vertical" size={8}>
          <Typography.Text strong>{t('containers.modals.savePendingDetailsUpdateTitle', '更新说明')}</Typography.Text>
          <Typography.Text>
            {t(
              'containers.modals.savePendingDetailsSummary',
              '本次涉及 {{total}} 条明细：进口价 {{importPriceCount}} 条、零售价 {{retailPriceCount}} 条、英文名称 {{englishNameCount}} 条、清除英文名称 {{clearEnglishNameCount}} 条。',
              {
                total: plan.pendingPatches.length,
                importPriceCount: plan.importPriceCount,
                retailPriceCount: plan.retailPriceCount,
                englishNameCount: plan.englishNameCount,
                clearEnglishNameCount: plan.clearEnglishNameCount,
              },
            )}
          </Typography.Text>
          {plan.retailPriceCount > 0 ? (
            <>
              <Typography.Text type="warning">
                {t('containers.modals.savePendingDetailsExistingRetailHint', '已有商品零售价会同步更新仓库商品实时零售价，并同步本货柜明细贴牌价格。')}
              </Typography.Text>
              <Typography.Text type="secondary">
                {t('containers.modals.savePendingDetailsNewRetailHint', '新商品零售价只保存到本货柜明细，后续创建商品时继续使用该价格。')}
              </Typography.Text>
            </>
          ) : null}
          {invalidEnglishNameCount > 0 ? (
            <Typography.Text type="danger">
              {t(
                'containers.modals.savePendingDetailsInvalidEnglishHint',
                '{{count}} 条英文名称为空或包含中文，本次不会保存并会继续保留。',
                { count: invalidEnglishNameCount },
              )}
            </Typography.Text>
          ) : null}
          <Typography.Text type="secondary">
            {t('containers.modals.savePendingDetailsRetryHint', '如果保存失败，待保存状态会保留，可修正后重试。')}
          </Typography.Text>
        </Space>
      ),
      okText: t('containers.actions.saveDetails', '保存明细'),
      cancelText: t('common.cancel'),
      onOk: () => {
        if (!isCurrentDetailDraftContext(plan.draftContext)) {
          message.warning(t('containers.messages.draftContextChanged', '用户或货柜已切换，已取消本次保存'))
          resolve(false)
          return
        }
        resolve(true)
      },
      onCancel: () => resolve(false),
    })
  })

  const executePendingDetailSavePlan = async (
    plan: PendingContainerDetailPageSavePlan,
  ): Promise<PendingContainerDetailSaveExecutionResult> => {
    const ignoredSaveResult: PendingContainerDetailSaveExecutionResult = {
      isCurrent: false,
      successfulFieldKeys: [],
    }
    if (!isCurrentDetailDraftContext(plan.draftContext)) {
      message.warning(t('containers.messages.draftContextChanged', '用户或货柜已切换，已取消本次保存'))
      return ignoredSaveResult
    }
    const saveContainerGuid = plan.draftContext.containerGuid
    const saveDraftIdentity = plan.draftContext.draftIdentity
    const detailRequestIdAtSaveStart = containerDetailLoadRequestIdRef.current
    const detailControllerAtSaveStart = detailAbortControllerRef.current
    let saveResponseApplied = false
    setDetailSaveSubmitting(true)
    try {
      const saveRequest = plan.detailUpdates.length
        ? trackDetailSavePromise(
            plan.saveKeys,
            batchUpdateDetails(saveContainerGuid, plan.detailUpdates),
            failedPendingDetailSaveKeysRef.current,
            saveContainerGuid,
            saveDraftIdentity,
          )
        : Promise.resolve({
            totalUpdated: 0,
            totalRequested: 0,
            validationErrors: [],
            conflicts: [],
            autoRepairedStoreGroupCount: undefined,
            autoRepairedRelationCount: undefined,
          })
      const scopedSave = await settleScopedContainerDetailSave(
        saveRequest,
        {
          saveContainerGuid,
          detailRequestIdAtSaveStart,
          abortControllerTokenAtSaveStart: detailControllerAtSaveStart,
        },
        () => ({
          containerGuid: currentContainerGuidRef.current,
          detailRequestId: containerDetailLoadRequestIdRef.current,
          abortControllerToken: detailAbortControllerRef.current,
        }),
      )
      const result = scopedSave.result
      if (
        !scopedSave.isCurrentContainer
        || pendingDetailDraftIdentityRef.current !== saveDraftIdentity
      ) {
        return ignoredSaveResult
      }
      const concurrentFieldErrors: ContainerDetailSaveValidationError[] = result.conflicts.map((conflict) => ({
        hguid: conflict.hguid,
        field: conflict.field,
        code: conflict.code,
        message: conflict.message ?? t('containers.messages.concurrentFieldUpdate', '服务器已更新该字段，请选择采用服务器值或保留我的值'),
      }))
      const validationErrors: ContainerDetailSaveValidationError[] = [
        ...plan.localValidationErrors,
        ...result.validationErrors,
        ...concurrentFieldErrors,
      ]
      if (result.conflicts.length > 0) {
        // 冲突是业务结果而非网络失败；保留草稿、在单元格标红，并让用户显式决定是否覆盖。
        setPendingDetailConflicts((current) => {
          const incomingKeys = new Set(result.conflicts.map((item) => `${item.hguid}:${item.field}`))
          return [
            ...current.filter((item) => !incomingKeys.has(`${item.hguid}:${item.field}`)),
            ...result.conflicts,
          ]
        })
        setConflictDrawerOpen(true)
      }
      if (plan.detailUpdates.length > 0 && scopedSave.shouldInvalidateDetailLoad) {
        // 保存结果优先于更早发出的查询，避免旧响应在队列清理后覆盖刚保存的本地值。
        detailControllerAtSaveStart?.abort()
        containerDetailLoadRequestIdRef.current += 1
        containerDetailReconcileGenerationRef.current += 1
        setDetailLoading(false)
      }
      const successfulEnglishNameUpdates = buildContainerDetailSuccessfulEnglishNameUpdates(
        pendingDetailPatchesRef.current,
        plan.detailUpdates,
        result.validationErrors,
      )
      if (successfulEnglishNameUpdates.length > 0) {
        // 保存期间允许继续编辑；旧响应不能覆盖新名称或新的清空意图。
        setRows((items) => applyContainerDetailEnglishNameUpdates(items, successfulEnglishNameUpdates))
      }
      const settledDraftState = settleContainerDetailDraftSaveSuccess(
        {
          pendingPatches: pendingDetailPatchesRef.current,
          failures: pendingDetailFailuresRef.current,
          fieldVersions: pendingDetailFieldVersionsRef.current,
          fieldBaselineTokens: pendingDetailFieldBaselineTokensRef.current,
        },
        plan.detailUpdates,
        validationErrors,
      )
      const successfullySavedFieldVersions = captureSuccessfullySavedContainerDetailDraftFieldVersions(
        plan.draftFieldVersionSnapshot,
        plan.detailUpdates,
        validationErrors,
      )
      const executionResult: PendingContainerDetailSaveExecutionResult = {
        isCurrent: true,
        successfulFieldKeys: Object.keys(successfullySavedFieldVersions),
      }
      pendingDetailOverrideAcknowledgementsRef.current = clearContainerDetailOverrideAcknowledgements(
        pendingDetailOverrideAcknowledgementsRef.current,
        successfullySavedFieldVersions,
      )
      const removedSavedDrafts = clearContainerDetailDraftFieldsIfVersionMatches(
        getContainerDetailDraftStorage(),
        plan.draftContext.userGuid,
        saveContainerGuid,
        successfullySavedFieldVersions,
      )
      if (!removedSavedDrafts.persisted) {
        isDetailDraftMemoryOnlyRef.current = true
        setIsDetailDraftMemoryOnly(true)
        // 删除失败时保留内存补丁；不能让已保存响应清空不可恢复的本地编辑。
        saveResponseApplied = true
        return executionResult
      }
      if (removedSavedDrafts.hasNewerFieldVersion) {
        // 另一标签页在本次保存后已写入新版本，重新读取并优先恢复它，不能复活旧值。
        const restoredDraft = readContainerDetailDraft(
          getContainerDetailDraftStorage(),
          plan.draftContext.userGuid,
          saveContainerGuid,
        )
        applyExternalPendingDetailDraftState(mergeContainerDetailDraftNewerFields(
          settledDraftState,
          restoredDraft,
          removedSavedDrafts.newerFieldVersionKeys,
        ))
        saveResponseApplied = true
        return executionResult
      }
      // 条件删除失败时保留内存状态，不能用一次普通写入伪装成“草稿已清除”。
      applyPendingDetailDraftState(settledDraftState, removedSavedDrafts.persisted, plan.pendingPatches)
      saveResponseApplied = true
      if (plan.detailUpdates.length > 0 && scopedSave.shouldReloadCurrentDetail) {
        // 后发查询可能在事务提交前读取旧快照，保存完成后用当前筛选条件立即替换。
        await reloadCurrentDetailRef.current()
      }

      const savedRowCount = countSuccessfullySavedContainerDetailRows(
        plan.detailUpdates,
        result.validationErrors,
      )

      if (savedRowCount > 0) {
        const repairedGroupCount = result.autoRepairedStoreGroupCount ?? 0
        const repairedRelationCount = result.autoRepairedRelationCount ?? 0
        message.success(repairedGroupCount > 0 || repairedRelationCount > 0
          ? t(
              'containers.messages.detailsSavedWithAutoRepair',
              '已保存 {{count}} 条明细；自动补齐 {{groupCount}} 个门店组、{{relationCount}} 条套装/多码关系',
              { count: savedRowCount, groupCount: repairedGroupCount, relationCount: repairedRelationCount },
            )
          : t('containers.messages.detailsSaved', '已保存 {{count}} 条明细', { count: savedRowCount }))
      }
      if (validationErrors.length > 0) {
        const failedRowCount = new Set(validationErrors.map((error) => error.hguid)).size
        const reasons = Array.from(new Set(validationErrors.map((error) => error.message))).join('；')
        message.warning(t(
          'containers.messages.detailFieldsNotSaved',
          '{{count}} 条明细有字段未保存：{{reasons}}',
          { count: failedRowCount, reasons },
        ))
      }
      return executionResult
    } catch (error) {
      if (
        currentContainerGuidRef.current === saveContainerGuid
        && pendingDetailDraftIdentityRef.current === saveDraftIdentity
      ) {
        if (!saveResponseApplied) {
          const draftFailureMessage = t(
            'containers.messages.detailDraftPreservedAfterFailure',
            '服务器保存失败，未保存字段已保留在本地草稿',
          )
          const failedDraftState = markContainerDetailDraftSaveFailure(
            {
              pendingPatches: pendingDetailPatchesRef.current,
              failures: pendingDetailFailuresRef.current,
            },
            plan.detailUpdates,
            draftFailureMessage,
          )
          applyPendingDetailDraftState(settleContainerDetailDraftSaveSuccess(
            failedDraftState,
            [],
            plan.localValidationErrors,
          ), true, plan.pendingPatches)
        }
        handleDetailSaveError(error)
      }
      return ignoredSaveResult
    } finally {
      if (
        currentContainerGuidRef.current === saveContainerGuid
        && pendingDetailDraftIdentityRef.current === saveDraftIdentity
      ) {
        setDetailSaveSubmitting(false)
      }
    }
  }

  const savePendingDetails = async () => {
    if (!access.canEditContainer) return
    if (!await drainAutoSavesBeforeAction()) return
    // 保存前先锁定本次提交范围，确认弹窗和实际落库共用同一份计划。
    const savePlan = buildPendingDetailSavePlan()
    if (!savePlan) return
    const confirmed = await confirmSavePendingDetails(savePlan)
    if (!confirmed) return
    await executePendingDetailSavePlan(savePlan)
  }

  const startEditingProductName = (row: ContainerDetail) => {
    if (!access.canEditContainer) return
    ignoreProductNameBlurRef.current = false
    setEditingProductNameRowKey(rowKey(row))
    setEditingProductNameValue(getContainerDetailProductName(row) ?? '')
  }

  const cancelEditingProductName = () => {
    ignoreProductNameBlurRef.current = true
    setEditingProductNameRowKey(null)
    setEditingProductNameValue('')
  }

  const commitProductNameEdit = async (row: ContainerDetail) => {
    const productName = editingProductNameValue.trim()
    ignoreProductNameBlurRef.current = true
    cancelEditingProductName()
    if (!productName) {
      message.warning(t('containers.messages.productNameRequired', '商品名称不能为空'))
      return
    }
    await saveRowPatch(row, { 商品名称: productName })
  }

  const handleProductNameEditBlur = (row: ContainerDetail) => {
    if (ignoreProductNameBlurRef.current) {
      ignoreProductNameBlurRef.current = false
      return
    }
    void commitProductNameEdit(row).catch(handleDetailSaveError)
  }

  const applyDetailUpdatesToRows = (updates: UpdateContainerDetailRequest[]) => {
    const nextRows = rowsRef.current.map((item) => {
      const match = updates.find((update) => update.hguid === item.hguid)
      return match ? mergeContainerDetailPatch(item, match as Partial<ContainerDetail>) : item
    })
    rowsRef.current = nextRows
    if (autoSaveContextKeyRef.current) {
      updateAutoSaveContextRows(autoSaveContextKeyRef.current, nextRows, containerRef.current)
    }
    setRows(nextRows)
  }

  const saveHeader = async () => {
    if (!containerGuid || !access.canEditContainer || savingHeaderRef.current) return
    savingHeaderRef.current = true
    setSavingHeader(true)
    try {
      if (!await drainAutoSavesBeforeAction()) return
      const nextContainerNumber = headerForm.货柜编号?.trim()
      if (!nextContainerNumber) {
        message.error(t('containers.placeholders.enterContainerNumber', '请输入货柜编号'))
        return
      }
      let freightComparisonContainer = container
      let nextFreight = container?.运费
      if (freightInputDirty) {
        try {
          const latestContainer = await getContainerDetail(containerGuid)
          if (!isValidContainerFreightVolume(latestContainer.总体积)) {
            message.error(t('containers.freightCalculator.invalidVolume'))
            return
          }
          const calculatedFreight = calculateContainerFreight(
            freightInputValue,
            latestContainer.总体积,
            freightInputMode,
          )
          if (calculatedFreight === undefined) {
            message.error(t('containers.freightCalculator.invalidInput'))
            return
          }
          freightComparisonContainer = latestContainer
          nextFreight = calculatedFreight
        } catch (error) {
          console.error(error)
          message.error(t('containers.freightCalculator.refreshFailed'))
          return
        }
      }
      const nextCostContainer = {
        ...freightComparisonContainer,
        汇率: headerForm.汇率,
        运费: nextFreight,
      }
      const shouldRecalculateCosts =
        (freightComparisonContainer?.汇率 ?? undefined) !== (headerForm.汇率 ?? undefined) ||
        (freightComparisonContainer?.运费 ?? undefined) !== (nextFreight ?? undefined)
      if (shouldRecalculateCosts && showCostRecalculateWarning(getContainerDetailCostMissingFields(nextCostContainer))) {
        return
      }

      const updatePayload: UpdateContainerRequest = {
        货柜编号: nextContainerNumber,
        装柜日期: headerForm.装柜日期 ? headerForm.装柜日期.format('YYYY-MM-DD') : undefined,
        预计到岸日期: headerForm.预计到岸日期 ? headerForm.预计到岸日期.format('YYYY-MM-DD') : undefined,
        实际到货日期: headerForm.实际到货日期 ? headerForm.实际到货日期.format('YYYY-MM-DD') : undefined,
        汇率: headerForm.汇率,
        运费: nextFreight,
        备注: headerForm.备注,
        状态: headerForm.状态,
      }
      try {
        await updateContainer(containerGuid, updatePayload)
      } catch (error) {
        console.error(error)
        message.error(error instanceof Error ? error.message : t('containers.messages.headerSaveFailed'))
        return
      }

      const submittedTotalVolume = freightComparisonContainer?.总体积
      // PUT 已成功即以提交结果更新本地基准，避免后续 reload 失败时旧报价被再次提交。
      setContainer((current) => current ? {
        ...current,
        总体积: submittedTotalVolume ?? current.总体积,
        汇率: headerForm.汇率,
        运费: nextFreight,
      } : current)
      setFreightInputMode('standard68')
      setFreightInputValue(normalizeContainerFreightInput(
        deriveContainerFreightInput(nextFreight, submittedTotalVolume, 'standard68'),
        'standard68',
      ))
      setFreightInputDirty(false)

      if (shouldRecalculateCosts) {
        const scope = buildWholeContainerDetailBatchScope()
        try {
          let previewToken = await confirmPreviewedContainerDetailAction(
            'recalculate-costs', scope, {}, t('containers.actions.recalculateCosts', '重算成本'),
          )
          while (previewToken) {
            try {
              const result = await recalculateContainerCostsByScope(containerGuid, scope, previewToken)
              message.success(t('containers.messages.headerSaveAndCostsRecalculated', '货柜信息已保存，已重算 {{count}} 条明细成本', { count: result.totalUpdated }))
              break
            } catch (error) {
              if (!isContainerDetailActionPreviewExpired(error)) throw error
              // 409 后重新读取预览；只有用户再次确认新令牌才会重试写入。
              previewToken = await confirmPreviewedContainerDetailAction(
                'recalculate-costs', scope, {}, t('containers.actions.recalculateCosts', '重算成本'),
              )
            }
          }
          if (!previewToken) {
            // 头部 PUT 已完成；取消重算只跳过成本写入，仍需退出编辑并刷新真实服务器状态。
            message.info(t('containers.messages.headerSavedCostsRecalculateCancelled', '货柜信息已保存，成本重算已取消'))
          }
        } catch (error) {
          console.error(error)
          const errorMessage = error instanceof Error ? error.message : t('containers.messages.costRecalculateFailed', '成本重算失败')
          message.warning(t('containers.messages.headerSavedCostsRecalculateFailed', { message: errorMessage, defaultValue: '货柜信息已保存，但成本重算失败：{{message}}' }))
        }
      } else {
        message.success(t('containers.messages.headerSaveSuccess'))
      }
      setHeaderEditing(false)
      try {
        await loadData()
      } catch (error) {
        console.error(error)
        message.error(error instanceof Error ? error.message : t('containers.messages.loadDetailFailed'))
      }
    } finally {
      savingHeaderRef.current = false
      setSavingHeader(false)
    }
  }

  const saveFloatRatePatch = async (row: ContainerDetail, value?: number) => {
    if (showCostRecalculateWarning(getContainerDetailCostMissingFields(container))) {
      return
    }

    const latestRow = rowsRef.current.find((item) => item.hguid === row.hguid) ?? row
    enqueueAutoSavePatch(row, {
      调整浮率: value ?? latestRow.调整浮率 ?? DEFAULT_CONTAINER_DETAIL_FLOAT_RATE,
    })
  }

  const savePackageMetricPatch = async (row: ContainerDetail, patch: Partial<ContainerDetail>) => {
    if (!access.canEditContainer || !row.hguid) return
    // 数量/体积会联动成本字段，缺少主表成本参数时必须阻止静默写入旧成本。
    if (showCostRecalculateWarning(getContainerDetailCostMissingFields(container))) {
      return
    }
    enqueueAutoSavePatch(row, patch as ContainerDetailAutoSavePatch)
  }

  /**
   * 把一列 Excel 值按当前显示顺序从 startRowKey 向下写入目标列。
   * 价格/英文名称进入“保存明细”草稿，其余列进入自动保存队列，与单元格手工编辑走同一条落库链路。
   */
  const applyContainerDetailColumnPaste = (
    columnKey: ContainerDetailColumnPasteKey,
    values: string[],
    startRowKey: string,
  ) => {
    if (!access.canEditContainer) return false
    const target = CONTAINER_DETAIL_COLUMN_PASTE_TARGETS[columnKey]
    const plan = buildContainerDetailColumnPastePlan({
      columnKey,
      values,
      rows: displayRows,
      startRowKey,
      getRowKey: rowKey,
    })
    if (plan.error === 'missing_target') {
      message.warning(t('containers.messages.columnPasteTargetMissing', '粘贴起始行已不在当前列表中，请重新选择'))
      return false
    }
    if (
      plan.entries.length > 0
      && target.requiresCostParameters
      && showCostRecalculateWarning(getContainerDetailCostMissingFields(container))
    ) {
      return false
    }
    const applyEntries = (entries: ContainerDetailColumnPasteEntry[]) => {
      if (!entries.length) return
      if (target.channel === 'pendingDraft') {
        markPendingDetailPatches(entries.map(({ row, field, value }) => ({
          row,
          patch: { [field]: value } as PendingDetailPatchItem['patch'],
        })))
        return
      }
      enqueueAutoSavePatches(entries.map(({ row, field, value }) => ({
        row,
        patch: { [field]: value } as ContainerDetailAutoSavePatch,
      })))
    }
    applyEntries(plan.entries)

    const summary = [t('containers.messages.columnPasteApplied', '已粘贴 {{count}} 个值', { count: plan.appliedCount })]
    if (plan.unchangedCount > 0) {
      summary.push(t('containers.messages.columnPasteUnchanged', '{{count}} 个与当前值相同', { count: plan.unchangedCount }))
    }
    if (plan.skippedBlankCount > 0) {
      summary.push(t('containers.messages.columnPasteSkippedBlank', '跳过 {{count}} 个空单元格', { count: plan.skippedBlankCount }))
    }
    if (plan.invalidCount > 0) {
      summary.push(t('containers.messages.columnPasteInvalid', '{{count}} 个无效值未应用（第 {{rows}} 行）', {
        count: plan.invalidCount,
        rows: plan.invalidRowNumbers.slice(0, 10).join('、') + (plan.invalidRowNumbers.length > 10 ? '…' : ''),
      }))
    }
    if (plan.overflowCount > 0) {
      summary.push(t('containers.messages.columnPasteOverflow', '超出列表末尾 {{count}} 行未粘贴', { count: plan.overflowCount }))
    }
    if (plan.appliedCount > 0 && target.channel === 'pendingDraft') {
      summary.push(t('containers.messages.columnPasteNeedsSave', '请点击“保存明细”落库'))
    }
    const summaryText = summary.join('，')
    if (plan.appliedCount === 0 || plan.invalidCount > 0 || plan.overflowCount > 0) {
      message.warning(summaryText)
    } else {
      message.success(summaryText)
    }
    return plan.appliedCount > 0
  }

  /** 可编辑单元格内 Ctrl+V：单个值交给原生粘贴；多行数据则从当前行向下按列填充。 */
  const handleEditableCellPaste = (
    row: ContainerDetail,
    columnKey: ContainerDetailColumnPasteKey,
    event: ReactClipboardEvent<HTMLElement>,
  ) => {
    const parsed = parseContainerDetailColumnPasteText(event.clipboardData.getData('text/plain'))
    if (!parsed.ok) {
      if (parsed.reason === 'multiple_columns') {
        event.preventDefault()
        message.warning(t('containers.messages.columnPasteMultipleColumns', '一次只能粘贴一列 Excel 数据'))
      }
      return
    }
    if (parsed.values.length <= 1) return
    event.preventDefault()
    // 先结束当前单元格编辑，让它自身的 blur 保存链路先落库，随后的批量补丁才能覆盖成粘贴值。
    event.currentTarget.blur()
    applyContainerDetailColumnPaste(columnKey, parsed.values, rowKey(row))
  }

  const openColumnPasteModal = () => {
    if (!displayRows.length) {
      message.warning(t('containers.messages.columnPasteNoRows', '当前列表没有可粘贴的明细行'))
      return
    }
    setColumnPasteStartRowKey(null)
    setColumnPasteText('')
    setColumnPasteModalOpen(true)
  }

  const closeColumnPasteModal = () => {
    setColumnPasteModalOpen(false)
    setColumnPasteText('')
  }

  const submitColumnPasteModal = () => {
    const parsed = parseContainerDetailColumnPasteText(columnPasteText)
    if (!parsed.ok) {
      message.warning(parsed.reason === 'multiple_columns'
        ? t('containers.messages.columnPasteMultipleColumns', '一次只能粘贴一列 Excel 数据')
        : t('containers.messages.columnPasteEmpty', '请先粘贴 Excel 列数据'))
      return
    }
    const startRowKey = columnPasteStartRowKey ?? (displayRows[0] ? rowKey(displayRows[0]) : '')
    if (applyContainerDetailColumnPaste(columnPasteColumnKey, parsed.values, startRowKey)) {
      closeColumnPasteModal()
    }
  }

  const columnPasteParsedValueCount = useMemo(() => {
    const parsed = parseContainerDetailColumnPasteText(columnPasteText)
    return parsed.ok ? parsed.values.length : 0
  }, [columnPasteText])

  const columnPasteStartRowOptions = useMemo(() => (
    columnPasteModalOpen
      ? displayRows.map((row, index) => ({
          value: rowKey(row),
          label: `${index + 1}. ${getContainerDetailItemNumber(row) ?? getContainerDetailProductCode(row) ?? getContainerDetailProductName(row) ?? ''}`,
        }))
      : []
  ), [columnPasteModalOpen, displayRows])

  const openBatchFloatRateModal = async () => {
    if (!ensureTargetRowsVisible()) return
    const scopedRows = await resolveBatchActionTargetRows()
    if (scopedRows == null) return
    if (!scopedRows.length) {
      message.warning(t('containers.messages.selectBatchProducts', '请先选择要批量操作的商品'))
      return
    }
    setBatchModalTargetCount(scopedRows.length)
    setBatchModalScopeRows(scopedRows)
    setBatchFloatRate(DEFAULT_CONTAINER_DETAIL_FLOAT_RATE)
    setBatchFloatRateModalOpen(true)
  }

  const submitBatchFloatRate = async () => {
    if (batchFloatRate == null) {
      message.warning(t('containers.messages.enterFloatRate', '请输入调整浮率'))
      return
    }
    if (showCostRecalculateWarning(getContainerDetailCostMissingFields(container))) {
      return
    }
    if (!await drainAutoSavesBeforeAction()) return
    setBatchFloatRateSaving(true)
    const scope = buildDetailBatchScope(batchModalScopeRows)
    const parameters = { floatRate: batchFloatRate }
    try {
      const previewToken = await confirmPreviewedContainerDetailAction(
        'apply-float-rate', scope, parameters, t('containers.actions.batchUpdateFloatRate', '批量修改浮率'),
      )
      if (!previewToken) return
      const result = await applyContainerFloatRateByScope(containerGuid, scope, batchFloatRate, previewToken)
      await reloadCurrentDetailRef.current()
      setBatchFloatRateModalOpen(false)
      setBatchModalTargetCount(0)
      setBatchModalScopeRows([])
      setBatchFloatRate(null)
      setSelectedRowKeys([])
      message.success(t('containers.messages.detailsUpdated', { count: result.totalUpdated }))
    } catch (error) {
      if (await handleExpiredContainerDetailActionPreview(error, 'apply-float-rate', scope, parameters)) {
        message.info(t('containers.messages.batchPreviewReconfirmRequired', '批量预览已更新，请再次确认后执行'))
      } else {
        message.error(error instanceof Error ? error.message : t('containers.messages.detailsUpdateFailed', '批量修改浮率失败'))
      }
    } finally {
      setBatchFloatRateSaving(false)
    }
  }

  const handleMatchDomesticData = async () => {
    if (!access.canEditContainer) return
    // 匹配结果要带「不同步已有商品」标记整体保存，开始前必须没有其它字段草稿混在同一次保存里。
    if (!ensureNoPendingDetails()) return
    const scopedRows = await confirmBatchRows(t('containers.actions.matchDomesticData'))
    if (!scopedRows) return
    if (!await drainAutoSavesBeforeAction()) return
    setMatchDomesticDataLoading(true)
    try {
      if (!scopedRows.length) {
        message.warning(t('containers.messages.noMatchableDetails'))
        return
      }

      const detectionItems = buildContainerDetailDetectionItems(scopedRows)

      if (!detectionItems.length) {
        message.warning(t('containers.messages.missingMatchableProductIdentity'))
        return
      }

      const detected = await detectProducts(detectionItems)
      const updates = buildContainerDetailMatchedDomesticDataUpdates(scopedRows, detected, container)
        .map((update) => ({ ...update, SkipRelatedProductSync: true }))
      if (!updates.length) {
        message.info(t('containers.messages.noDomesticDataToUpdate'))
        return
      }

      const writableUpdates = updates.filter((update) => (
        update.国内价格 != null ||
        update.贴牌价格 != null ||
        update.商品名称 != null ||
        update.英文名称 != null ||
        update.单件装箱数 != null ||
        update.装柜数量 != null ||
        update.单件体积 != null ||
        update.合计装柜体积 != null ||
        update.合计装柜金额 != null ||
        update.运输成本 != null ||
        update.进口价格 != null
      ))
      if (writableUpdates.length) {
        // 匹配结果先进入同一份字段草稿：实际字段的首次 server token 会随请求提交，
        // 冲突、校验失败或网络失败均保留意图而非被本地展示覆盖。
        queuePendingDetailUpdates(writableUpdates)
        const pendingSavePlan = buildPendingDetailSavePlan()
        // 草稿不保留 SkipRelatedProductSync，提交前补回，避免匹配把国内数据同步改写已有商品。
        const savePlan = pendingSavePlan
          ? { ...pendingSavePlan, detailUpdates: markContainerDetailUpdatesSkipRelatedProductSync(pendingSavePlan.detailUpdates) }
          : null
        const saveResult = savePlan
          ? await executePendingDetailSavePlan(savePlan)
          : { isCurrent: false, successfulFieldKeys: [] }
        const fullySavedUpdates = filterSuccessfullySavedContainerDetailUpdates(
          writableUpdates,
          saveResult.successfulFieldKeys,
        )
        if (fullySavedUpdates.length !== writableUpdates.length) {
          // 任意目标字段冲突或校验失败时保留草稿和当前交互；不能把检测结果伪装成已保存。
          return
        }
      }
      applyDetailUpdatesToRows(updates)
      if (!selectedRowKeys.length) {
        await reloadCurrentDetailRef.current()
      }
      const pricePatchCount = updates.filter((update) => update.国内价格 != null || update.贴牌价格 != null).length
      message.success(t('containers.messages.domesticDataMatched', { count: updates.length, priceCount: pricePatchCount }))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.messages.matchDomesticDataFailed'))
    } finally {
      setMatchDomesticDataLoading(false)
    }
  }

  const handleAlignDomesticProductCode = (row: ContainerDetail) => {
    if (!canAlignDomesticProductCode) {
      message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'))
      return
    }
    if (getContainerDetailProductType(row) === '套装子商品') {
      message.warning(t('containers.messages.alignSetChildNotSupported', '套装子商品暂不支持单独对齐编码'))
      return
    }
    const detailHguid = row.hguid
    const localProductCode = getContainerDetailLocalProductCode(row)
    const domesticProductCode = getContainerDetailDomesticProductCode(row)
    const supplierCode = row.localSupplierCode?.trim() || row.商品信息?.localSupplierCode?.trim() || '200'
    if (!detailHguid || !localProductCode || !domesticProductCode) {
      message.warning(t('containers.messages.missingAlignProductCode', '缺少国内商品编码或本地主档编码，不能对齐'))
      return
    }

    Modal.confirm({
      title: t('containers.modals.alignDomesticProductCodeTitle', '对齐国内商品编码'),
      content: (
        <Space direction="vertical" size={6}>
          <Typography.Text>
            {t(
              'containers.modals.alignDomesticProductCodeContent',
              '确认把国内商品编码 {{oldCode}} 改为本地主档编码 {{newCode}}？',
              { oldCode: domesticProductCode, newCode: localProductCode },
            )}
          </Typography.Text>
          <Typography.Text type="secondary">
            {[
              getContainerDetailItemNumber(row),
              getContainerDetailProductName(row),
            ].filter(Boolean).join(' / ')}
          </Typography.Text>
          <Typography.Text type="warning">
            {t(
              'containers.modals.alignDomesticProductCodeConflictHint',
              '如果目标国内编码已存在，后端会拒绝本次对齐，不会自动合并或覆盖。',
            )}
          </Typography.Text>
        </Space>
      ),
      okText: t('containers.actions.alignDomesticProductCode', '对齐编码'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        if (!await drainAutoSavesBeforeAction()) return
        setAligningDomesticProductDetailHguid(detailHguid)
        try {
          const result = await alignDomesticProductCode({
            detailHguid,
            expectedDomesticProductCode: domesticProductCode,
            targetProductCode: localProductCode,
            supplierCode,
          })
          message.success(
            t('containers.messages.domesticProductCodeAligned', '已对齐国内商品编码 {{oldCode}} -> {{newCode}}', {
              oldCode: result.oldProductCode || domesticProductCode,
              newCode: result.newProductCode || localProductCode,
            }),
          )
          await reloadCurrentDetailRef.current()
        } catch (error) {
          message.error(error instanceof Error ? error.message : t('containers.messages.alignDomesticProductCodeFailed', '对齐国内商品编码失败'))
          throw error
        } finally {
          setAligningDomesticProductDetailHguid(null)
        }
      },
    })
  }

  const openBatchPricesModal = async () => {
    if (!ensureTargetRowsVisible()) return
    const scopedRows = await resolveBatchActionTargetRows()
    if (scopedRows == null) return
    if (!scopedRows.length) {
      message.warning(t('containers.messages.selectBatchProducts', '请先选择要批量操作的商品'))
      return
    }
    setBatchModalTargetCount(scopedRows.length)
    setBatchModalScopeRows(scopedRows)
    setBatchImportPrice(null)
    setBatchOemPrice(null)
    setBatchPricesModalOpen(true)
  }

  const submitBatchPrices = async () => {
    if (batchImportPrice == null && batchOemPrice == null) {
      message.warning(t('containers.messages.enterImportOrOemPrice'))
      return
    }
    if (!await drainAutoSavesBeforeAction()) return
    setBatchPricesSaving(true)
    const scope = buildDetailBatchScope(batchModalScopeRows)
    const prices = {
      importPrice: batchImportPrice,
      oemPrice: batchOemPrice,
    }
    try {
      const previewToken = await confirmPreviewedContainerDetailAction(
        'apply-prices', scope, prices, t('containers.actions.batchUpdatePrices', '批量修改价格'),
      )
      if (!previewToken) return
      const result = await applyContainerPricesByScope(containerGuid, scope, prices, previewToken)
      await reloadCurrentDetailRef.current()
      setBatchPricesModalOpen(false)
      setBatchModalTargetCount(0)
      setBatchModalScopeRows([])
      setBatchImportPrice(null)
      setBatchOemPrice(null)
      setSelectedRowKeys([])
      message.success(t('containers.messages.detailPricesUpdated', { count: result.totalUpdated }))
    } catch (error) {
      if (await handleExpiredContainerDetailActionPreview(error, 'apply-prices', scope, prices)) {
        message.info(t('containers.messages.batchPreviewReconfirmRequired', '批量预览已更新，请再次确认后执行'))
      } else {
        message.error(error instanceof Error ? error.message : t('containers.messages.detailPricesUpdateFailed', '批量改价失败'))
      }
    } finally {
      setBatchPricesSaving(false)
    }
  }

  const applyActive = async (isActive: boolean) => {
    const scopedRows = await confirmBatchRows(t(isActive ? 'containers.actions.batchActivate' : 'containers.actions.batchDeactivate'))
    if (!scopedRows) return
    if (!scopedRows.length) {
      message.warning(t('containers.messages.selectProducts'))
      return
    }
    // 新商品尚未写入仓库商品表，不能调用仓库商品上下架接口。
    const newProductCount = scopedRows.filter((row) => row.是否新商品).length
    const eligibleRows = scopedRows.filter((row) => !row.是否新商品)
    if (!eligibleRows.length) {
      message.warning(t('containers.messages.newProductCannotToggleWarehouseStatus', '新商品请先创建后再上下架'))
      return
    }
    if (newProductCount > 0) {
      message.warning(t('containers.messages.newProductsSkippedForWarehouseStatus', { count: newProductCount, defaultValue: '已跳过 {{count}} 条新商品，请先创建后再上下架' }))
    }

    const productCodes = eligibleRows
      .map(getContainerDetailProductCode)
      .filter((value): value is string => Boolean(value))
      .filter((value) => !pendingWarehouseStatusCodes.has(value))
    if (!productCodes.length) {
      message.warning(
        eligibleRows.some((row) => {
          const productCode = getContainerDetailProductCode(row)
          return productCode ? pendingWarehouseStatusCodes.has(productCode) : false
        })
          ? t('containers.messages.warehouseStatusUpdating', '商品上下架正在提交，请稍后再试')
          : t('containers.messages.selectedProductsMissingCode'),
      )
      return
    }
    // 下架必须先登记供货说明（后续计划必选）；取消弹窗即放弃本次下架。
    const supplyNotice = isActive ? undefined : (await requestSupplyNotice(productCodes.length)) ?? undefined
    if (!isActive && !supplyNotice) return
    if (!await drainAutoSavesBeforeAction()) return
    const statusRows = eligibleRows.filter((row) => productCodes.includes(getContainerDetailProductCode(row) ?? ''))
    const scope = buildDetailBatchScope(statusRows)
    const parameters = { isActive }
    try {
      const previewToken = await confirmPreviewedContainerDetailAction(
        'set-status', scope, parameters, t(isActive ? 'containers.actions.batchActivate' : 'containers.actions.batchDeactivate'),
      )
      if (!previewToken) return
      await setContainerDetailStatusByScope(containerGuid, scope, isActive, previewToken, supplyNotice)
      setRows((items) => applyContainerDetailWarehouseStatusByProductCodes(items, productCodes, isActive))
      setSelectedRowKeys([])
      message.success(t(isActive ? 'containers.messages.productsActivated' : 'containers.messages.productsDeactivated', { count: productCodes.length }))
    } catch (error) {
      if (await handleExpiredContainerDetailActionPreview(error, 'set-status', scope, parameters)) {
        message.info(t('containers.messages.batchPreviewReconfirmRequired', '批量预览已更新，请再次确认后执行'))
      } else {
        message.error(error instanceof Error ? error.message : t('containers.messages.batchActiveFailed'))
      }
    }
  }

  const translateNames = async () => {
    const scopedRows = await confirmBatchRows(t('containers.actions.batchTranslate'))
    if (!scopedRows) return
    const names = Array.from(new Set(scopedRows.map(getContainerDetailTranslationSource).filter((value): value is string => Boolean(value))))
    if (!names.length) {
      message.warning(t('containers.messages.noNamesToTranslate'))
      return
    }
    const translations = await batchTranslate(names)
    const updates = buildContainerDetailTranslationUpdates(scopedRows, translations)
    const skippedInvalidCount = countContainerDetailInvalidTranslationResults(scopedRows, translations)
    if (updates.length) {
      queuePendingDetailUpdates(updates)
      setRows((items) => applyContainerDetailEnglishNameUpdates(items, updates))
      message.success(t(
        'containers.messages.namesTranslatedPending',
        '已生成 {{count}} 条英文名称，请点击“保存明细”提交',
        { count: updates.length },
      ))
    }
    if (skippedInvalidCount > 0) {
      message.warning(t('containers.messages.invalidTranslatedNamesSkipped', { count: skippedInvalidCount }))
    }
    if (!updates.length && skippedInvalidCount === 0) {
      message.success(t('containers.messages.namesTranslated', { count: 0 }))
    }
  }

  const openBatchEditEnglishName = async () => {
    if (!ensureTargetRowsVisible()) return
    const scopedRows = await resolveBatchActionTargetRows()
    if (scopedRows == null) return
    if (!scopedRows.length) {
      message.warning(t('containers.messages.selectBatchProducts', '请先选择要批量操作的商品'))
      return
    }
    setBatchModalTargetCount(scopedRows.length)
    setBatchModalScopeRows(scopedRows)
    setBatchEnglishName('')
    setBatchEnglishNameModalOpen(true)
  }

  const openBatchCategory = async () => {
    if (!ensureTargetRowsVisible()) return
    const scopedRows = await resolveBatchActionTargetRows()
    if (scopedRows == null) return
    if (!scopedRows.length) {
      message.warning(t('containers.messages.noCategoryFilterRows', '当前没有可分类的明细'))
      return
    }
    setBatchModalTargetCount(scopedRows.length)
    setBatchModalScopeRows(scopedRows)
    // 从工具栏管理分类后，保留仍存在的目标分类，打开批量分类即可直接使用。
    setTargetCategoryGuid((current) => findWarehouseCategory(categories, current)?.categoryGUID)
    // 每次打开批量分类弹窗都只展开到一级分类，避免默认露出过深的子分类。
    setCategoryExpandedKeys(collectCategoryExpandedKeys(categories, 1))
    setBatchCategoryOpen(true)
  }

  const buildContainerDetailCategoryPatch = (
    item: ContainerDetail,
    categoryGuid: string,
    category?: WarehouseCategoryNode,
    categoryPath?: string,
  ): Partial<ContainerDetail> => {
    const categoryName = category ? formatWarehouseCategoryNodeName(category, i18n.language) : undefined
    const nextCategoryPath = categoryPath || categoryName

    return {
      categoryName,
      CategoryName: categoryName,
      productCategoryName: categoryName,
      ProductCategoryName: categoryName,
      categoryPath: nextCategoryPath,
      CategoryPath: nextCategoryPath,
      categoryFullPath: nextCategoryPath,
      CategoryFullPath: nextCategoryPath,
      warehouseCategoryGUID: categoryGuid,
      WarehouseCategoryGUID: categoryGuid,
      productCategoryGUID: categoryGuid,
      ProductCategoryGUID: categoryGuid,
      商品信息: item.商品信息
        ? {
            ...item.商品信息,
            categoryName,
            CategoryName: categoryName,
            productCategoryName: categoryName,
            ProductCategoryName: categoryName,
            categoryPath: nextCategoryPath,
            CategoryPath: nextCategoryPath,
            categoryFullPath: nextCategoryPath,
            CategoryFullPath: nextCategoryPath,
            warehouseCategoryGUID: categoryGuid,
            WarehouseCategoryGUID: categoryGuid,
            productCategoryGUID: categoryGuid,
            ProductCategoryGUID: categoryGuid,
          }
        : item.商品信息,
    }
  }

  const openRowCategoryModal = (row: ContainerDetail) => {
    if (!canBatchSetCategory) {
      message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'))
      return
    }

    setRowCategoryEditingRow(row)
    setRowTargetCategoryGuid(getContainerDetailCategoryGuid(row))
    // 单行修改也沿用分类树，只默认展开一级，避免打开时树过深难扫。
    setCategoryExpandedKeys(collectCategoryExpandedKeys(categories, 1))
    setRowCategoryOpen(true)
  }

  const closeRowCategoryModal = () => {
    closeCategoryManageModal()
    setRowCategoryOpen(false)
    setRowCategoryEditingRow(null)
    setRowTargetCategoryGuid(undefined)
  }

  const handleRowCategorySave = async () => {
    if (!canBatchSetCategory) {
      message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'))
      return
    }
    if (!rowCategoryEditingRow?.hguid) {
      message.warning(t('containers.messages.selectProducts'))
      return
    }
    if (!rowTargetCategoryGuid) {
      message.warning(t('warehouse.categories.selectTargetCategory', '请选择目标分类'))
      return
    }
    if (!await drainAutoSavesBeforeAction()) return

    setRowCategorySaving(true)
    try {
      // 单行分类也走字段草稿保存，确保 RequireTokens 开启后不会 428，冲突时保留用户选择。
      queuePendingDetailUpdates([{ hguid: rowCategoryEditingRow.hguid, ProductCategoryGUID: rowTargetCategoryGuid }])
      const savePlan = buildPendingDetailSavePlan()
      const saveResult = savePlan
        ? await executePendingDetailSavePlan(savePlan)
        : { isCurrent: false, successfulFieldKeys: [] }
      if (!saveResult.successfulFieldKeys.includes(`${rowCategoryEditingRow.hguid}:ProductCategoryGUID`)) {
        // 冲突、校验或网络失败时保持分类弹窗和草稿，用户可在冲突抽屉中处理后重试。
        return
      }
      const categoryPatch = buildContainerDetailCategoryPatch(
        rowCategoryEditingRow,
        rowTargetCategoryGuid,
        selectedRowTargetCategory,
        selectedRowTargetCategoryPath,
      )
      setRows((items) =>
        items.map((item) => (
          rowKey(item) !== rowKey(rowCategoryEditingRow)
            ? item
            : mergeContainerDetailPatch(item, categoryPatch)
        )),
      )
      setRowCategoryOpen(false)
      setRowCategoryEditingRow(null)
      setRowTargetCategoryGuid(undefined)
      message.success(t('containers.messages.rowCategoryUpdated', '目标分类已更新'))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('warehouse.categories.batchAssignFailed', '分类保存失败'))
    } finally {
      setRowCategorySaving(false)
    }
  }

  const handleBatchCategorySave = async () => {
    if (!canBatchSetCategory) {
      message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'))
      return
    }
    if (!targetCategoryGuid) {
      message.warning(t('warehouse.categories.selectTargetCategory', '请选择目标分类'))
      return
    }
    if (!ensureTargetRowsVisible()) return

    const batchCategoryTargetRows = batchModalScopeRows
    const { productCodes, skippedMissingCodeCount } = getContainerDetailBatchCategoryProductCodes(batchCategoryTargetRows)
    if (!productCodes.length) {
      message.warning(t('containers.messages.noProductsForCategoryAssign', '当前目标明细没有可分类的已有商品'))
      return
    }
    if (!await drainAutoSavesBeforeAction()) return

    setBatchCategorySaving(true)
    const scope = buildDetailBatchScope(batchCategoryTargetRows.filter((row) => productCodes.includes(getContainerDetailProductCode(row) ?? '')))
    const parameters = { categoryGuid: targetCategoryGuid }
    try {
      const previewToken = await confirmPreviewedContainerDetailAction(
        'assign-category', scope, parameters, t('containers.actions.batchSetCategory', '批量分类'),
      )
      if (!previewToken) return
      // 分类也必须走货柜范围保护动作，避免绕过预览令牌与并发锁。
      await assignContainerDetailCategoryByScope(containerGuid, scope, targetCategoryGuid, previewToken)
      const productCodeSet = new Set(productCodes)
      // 保存成功后只更新当前已加载行，避免重新查询整张明细表造成等待和 loading。
      setRows((items) =>
        items.map((item) => {
          const productCode = getContainerDetailProductCode(item)
          if (!productCode || !productCodeSet.has(productCode)) return item
          return mergeContainerDetailPatch(item, buildContainerDetailCategoryPatch(item, targetCategoryGuid, selectedTargetCategory, selectedTargetCategoryPath))
        }),
      )
      setSelectedRowKeys([])
      setBatchCategoryOpen(false)
      setBatchModalTargetCount(0)
      setBatchModalScopeRows([])
      setTargetCategoryGuid(undefined)
      message.success(t('containers.messages.batchCategoryUpdated', '已设置 {{count}} 个商品分类', { count: productCodes.length }))
      if (skippedMissingCodeCount > 0) {
        message.warning(t('containers.messages.batchCategorySkippedMissingCode', '已跳过 {{count}} 条缺少商品编码的明细', { count: skippedMissingCodeCount }))
      }
    } catch (error) {
      if (await handleExpiredContainerDetailActionPreview(error, 'assign-category', scope, parameters)) {
        message.info(t('containers.messages.batchPreviewReconfirmRequired', '批量预览已更新，请再次确认后执行'))
      } else {
        message.error(error instanceof Error ? error.message : t('warehouse.categories.batchAssignFailed', '批量分类失败'))
      }
    } finally {
      setBatchCategorySaving(false)
    }
  }

  const submitBatchEditEnglishName = async () => {
    const scopedRows = batchModalScopeRows
    const updates = buildContainerDetailEnglishNameUpdates(scopedRows, batchEnglishName)
    if (!updates.length) {
      message.warning(t('containers.messages.enterValidEnglishName', '请输入不含中文的英文名称'))
      return
    }

    queuePendingDetailUpdates(updates)
    setRows((items) => applyContainerDetailEnglishNameUpdates(items, updates))
    setBatchEnglishNameModalOpen(false)
    setBatchModalTargetCount(0)
    setBatchModalScopeRows([])
    setBatchEnglishName('')
    message.success(t(
      'containers.messages.englishNamesPending',
      '已添加 {{count}} 条英文名称修改，请点击“保存明细”提交',
      { count: updates.length },
    ))
  }

  const clearEnglishNames = async () => {
    const scopedRows = await confirmBatchRows(t('containers.actions.clearEnglishNames'), { danger: true })
    if (!scopedRows) return
    const updates = buildContainerDetailClearEnglishNameUpdates(scopedRows)
    if (!updates.length) {
      message.warning(t('containers.messages.selectProducts'))
      return
    }

    queuePendingDetailUpdates(updates)
    setRows((items) => applyContainerDetailEnglishNameUpdates(
      items,
      updates.map((update) => ({ hguid: update.hguid, 英文名称: undefined })),
    ))
    message.success(t(
      'containers.messages.englishNamesClearPending',
      '已添加 {{count}} 条清除英文名称操作，请点击“保存明细”提交',
      { count: updates.length },
    ))
  }

  const showHqTranslationResult = (result: HqTranslationResult) => {
    const samples = Object.entries(result.Samples ?? {}).slice(0, 10)

    Modal.info({
      title: t('containers.modals.hqTranslationResultTitle'),
      width: 640,
      content: (
        <Space direction="vertical" size={8} style={{ width: '100%' }}>
          <Typography.Text>{t('containers.messages.hqTranslationResultSummary', {
            candidates: result.TotalCandidates ?? 0,
            translated: result.TotalTranslated ?? 0,
            skipped: result.TotalSkipped ?? 0,
            failed: result.TotalFailed ?? 0,
          })}</Typography.Text>
          {samples.length ? (
            <>
              <Typography.Text strong>{t('containers.text.hqTranslationSamples')}</Typography.Text>
              <Space direction="vertical" size={4} style={{ width: '100%' }}>
                {samples.map(([chinese, english]) => (
                  <Typography.Text key={chinese}>
                    {`${chinese} -> ${english}`}
                  </Typography.Text>
                ))}
              </Space>
            </>
          ) : null}
        </Space>
      ),
    })
  }

  const translateHqData = async () => {
    const containerNumber = container?.货柜编号?.trim()
    if (!containerNumber) {
      message.warning(t('containers.messages.missingContainerNumberForHqTranslation'))
      return
    }
    if (!await drainAutoSavesBeforeAction()) return

    setHqTranslating(true)
    message.loading({
      content: t('containers.messages.hqTranslationInProgress'),
      key: 'hq-translation',
      duration: 0,
    })

    try {
      const result = await translateHqProductNamesByContainerNumber(containerNumber)
      message.success({
        content: t('containers.messages.hqTranslationCompleted'),
        key: 'hq-translation',
      })
      showHqTranslationResult(result)
      await loadData()
    } catch (error) {
      console.error(error)
      message.error({
        content: error instanceof Error ? error.message : t('containers.messages.hqTranslationFailed'),
        key: 'hq-translation',
      })
    } finally {
      setHqTranslating(false)
    }
  }

  const renderPushToHqResultContent = (
    result: PushProductsToHqResult,
    selection: ReturnType<typeof buildContainerDetailHqPushSelection>,
  ) => {
    const errors = result.errors ?? []
    const detailStats = [
      { label: t('posAdmin.products.pushToHqAffectedRows', 'HQ影响记录'), value: result.affectedRowCount ?? 0 },
      { label: t('posAdmin.products.productsAdded', '商品新增'), value: result.productsAdded ?? 0 },
      { label: t('posAdmin.products.productsUpdated', '商品更新'), value: result.productsUpdated ?? 0 },
      { label: t('containers.text.warehouseInventoriesCreated', '仓库库存新增'), value: result.warehouseInventoriesCreated ?? 0 },
      { label: t('containers.text.warehouseInventoriesUpdated', '仓库库存更新'), value: result.warehouseInventoriesUpdated ?? 0 },
      { label: t('posAdmin.products.storeRetailPricesCreated', '门店零售价新增'), value: result.storeRetailPricesCreated ?? 0 },
      { label: t('posAdmin.products.storeRetailPricesUpdated', '门店零售价更新'), value: result.storeRetailPricesUpdated ?? 0 },
      { label: t('posAdmin.products.productSetCodesCreated', '套装编码新增'), value: result.productSetCodesCreated ?? 0 },
      { label: t('posAdmin.products.productSetCodesUpdated', '套装编码更新'), value: result.productSetCodesUpdated ?? 0 },
      { label: t('posAdmin.products.storeMultiCodesCreated', '门店多码新增'), value: result.storeMultiCodesCreated ?? 0 },
      { label: t('posAdmin.products.storeMultiCodesUpdated', '门店多码更新'), value: result.storeMultiCodesUpdated ?? 0 },
      { label: t('containers.text.skippedNewProducts', '跳过新商品'), value: selection.skippedNewProductCount },
      { label: t('containers.text.missingProductCodeRows', '缺商品编码'), value: selection.missingProductCodeCount },
    ].filter((item) => item.value > 0)
    return (
      <Space direction="vertical" size={6}>
        {result.message ? <div>{result.message}</div> : null}
        <div>
          {t('posAdmin.products.pushToHqResult', '发送完成：商品成功 {{success}}，失败 {{failed}}，合计 {{total}}', {
            success: result.successCount ?? 0,
            failed: result.failedCount ?? 0,
            total: result.totalCount ?? (result.successCount ?? 0) + (result.failedCount ?? 0),
          })}
        </div>
        <div>{t('containers.text.pushToHqSelectedLocalProducts', '本次发送候选商品：{{count}} 个', { count: selection.items.length })}</div>
        {detailStats.map((item) => (
          <div key={item.label}>{item.label}: {item.value}</div>
        ))}
        {errors.length ? (
          <div style={{ whiteSpace: 'pre-wrap' }}>
            {t('posAdmin.products.partialSyncError', '部分同步错误')}：{errors.join('\n')}
          </div>
        ) : null}
      </Space>
    )
  }

  const buildPushToHqFallbackResult = (job: PushProductsToHqJobResult): PushProductsToHqResult => ({
    successCount: 0,
    failedCount: job.errors?.length || 1,
    totalCount: job.errors?.length || 1,
    affectedRowCount: 0,
    errors: job.errors?.length ? job.errors : [job.message || t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败')],
    message: job.message,
  })

  const releasePushToHqLoading = () => {
    pushToHqLoadingRef.current = false
    setPushToHqLoading(false)
  }

  const notifyPushToHqJobFinished = (
    job: PushProductsToHqJobResult,
    selection: ReturnType<typeof buildContainerDetailHqPushSelection>,
    pushToHqNotificationKey: string,
  ) => {
    const result = job.result ?? buildPushToHqFallbackResult(job)
    const hasErrors = (result.errors ?? []).length > 0 || (result.failedCount ?? 0) > 0

    if (job.status === 'Failed') {
      notification.error({
        key: pushToHqNotificationKey,
        message: t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'),
        description: renderPushToHqResultContent(result, selection),
        duration: 0,
      })
      return
    }

    if (hasErrors) {
      notification.warning({
        key: pushToHqNotificationKey,
        message: t('posAdmin.products.pushToHqPartialSucceeded', '发送到 HQ 部分成功'),
        description: renderPushToHqResultContent(result, selection),
        duration: 0,
      })
      return
    }

    notification.success({
      key: pushToHqNotificationKey,
      message: t('posAdmin.products.pushToHqSucceeded', '发送到 HQ 完成'),
      description: renderPushToHqResultContent(result, selection),
      duration: 0,
    })
  }

  const pollPushToHqJob = (
    initialJob: PushProductsToHqJobResult,
    selection: ReturnType<typeof buildContainerDetailHqPushSelection>,
    pushToHqNotificationKey: string,
  ) => {
    if (initialJob.status === 'Succeeded' || initialJob.status === 'Failed') {
      notifyPushToHqJobFinished(initialJob, selection, pushToHqNotificationKey)
      return
    }

    const poller = createHqSyncJobPoller({
      jobId: initialJob.jobId,
      getJob: getPushProductsToHqJob,
      // 发送到 HQ 刚提交后 job 通常秒级完成：前 10 次查询每 200ms 一次，
      // 之后恢复常规 2000ms，避免长时间高频请求后端。
      initialPollIntervalMs: 200,
      initialPollAttempts: 10,
    })

    void poller.promise
      .then((job) => {
        notifyPushToHqJobFinished(job, selection, pushToHqNotificationKey)
      })
      .catch((error) => {
        const errorResult = extractPushToHqErrorResult(error)
        if (errorResult) {
          notification.error({
            key: pushToHqNotificationKey,
            message: t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'),
            description: renderPushToHqResultContent(errorResult, selection),
            duration: 0,
          })
        } else {
          notification.error({
            key: pushToHqNotificationKey,
            message: t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'),
            description: error instanceof Error ? error.message : undefined,
            duration: 0,
          })
        }
      })
  }

  const handlePushSelectedProductsToHq = async () => {
    if (pushToHqLoadingRef.current) return
    if (!access.canManagePosProducts) {
      message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'))
      return
    }
    if (!selectedRows.length) {
      message.warning(t('containers.messages.selectProducts'))
      return
    }
    if (!ensureNoPendingDetails()) return

    const selection = buildContainerDetailHqPushSelection(selectedRows)
    if (!selection.items.length) {
      message.warning(t('containers.messages.noExistingLocalProductsToPushHq', '选中明细没有可发送到 HQ 的本地已有商品'))
      return
    }

    const updateFields = await confirmPushToHqUpdateFields(selection.items.length)
    if (!updateFields) return
    if (!await drainAutoSavesBeforeAction()) return

    try {
      // 写 HQ 是跨库操作，使用即时锁防止连续点击造成重复提交。
      pushToHqLoadingRef.current = true
      setPushToHqLoading(true)
      const job = await createPushProductsToHqJob({
        operationId: buildPushProductsToHqOperationId(containerGuid, selection.productCodes, selection.items.length, updateFields),
        productCodes: selection.productCodes,
        items: selection.items,
        updateFields,
      })
      const pushToHqNotificationKey = `container-push-to-hq:${job.jobId}`
      notification.info({
        key: pushToHqNotificationKey,
        message: t('containers.messages.pushToHqJobSubmitted', '发送到 HQ 任务已提交'),
        description: t('containers.messages.pushToHqJobRunning', '后台正在写入 HQ，完成后会显示各表新增/更新数量和错误摘要。'),
        duration: 0,
      })
      setSelectedRowKeys([])
      releasePushToHqLoading()
      pollPushToHqJob(job, selection, pushToHqNotificationKey)
    } catch (error) {
      const errorResult = extractPushToHqErrorResult(error)
      if (errorResult) {
        // 发送 HQ 的结果统一收敛到右上角通知，避免弹窗打断表格编辑状态。
        notification.error({
          message: t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'),
          description: renderPushToHqResultContent(errorResult, selection),
          duration: 0,
        })
      } else {
        message.error(error instanceof Error ? error.message : t('posAdmin.products.pushToHqFailed', '发送到 HQ 失败'))
      }
    } finally {
      releasePushToHqLoading()
    }
  }

  const renderCreateProductResultItems = (items: ContainerProductCreationResultItem[]) => {
    if (!items.length) return null

    return (
      <ul style={{ margin: '4px 0 0', paddingInlineStart: 20 }}>
        {items.slice(0, 10).map((item, index) => (
          <li key={`${item.detailHguid || item.productCode || item.itemNumber || index}`}>
            <Typography.Text>
              {[item.productCode, item.itemNumber, item.reasonCode, item.message].filter(Boolean).join(' / ')}
            </Typography.Text>
          </li>
        ))}
        {items.length > 10 ? (
          <li>
            <Typography.Text type="secondary">
              {t('containers.text.moreCreateProductResultItems', '还有 {{count}} 条未显示', { count: items.length - 10 })}
            </Typography.Text>
          </li>
        ) : null}
      </ul>
    )
  }

  const showCreateProductsJobResult = (job: ContainerProductCreationJob) => {
    const result = job.result
    const description = (
      <Space direction="vertical" size={8}>
        <Typography.Text>
          {t('containers.text.createProductsJobSummary', '创建 {{created}}，跳过 {{skipped}}，失败 {{failed}}', {
            created: result.createdCount,
            skipped: result.skippedCount,
            failed: result.failedCount,
          })}
        </Typography.Text>
        {job.message ? <Typography.Text type="secondary">{job.message}</Typography.Text> : null}
        {result.skipped.length ? (
          <div>
            <Typography.Text strong>{t('containers.text.skippedRows', '跳过明细')}</Typography.Text>
            {renderCreateProductResultItems(result.skipped)}
          </div>
        ) : null}
        {result.errors.length ? (
          <div>
            <Typography.Text strong>{t('containers.text.failedRows', '失败明细')}</Typography.Text>
            {renderCreateProductResultItems(result.errors)}
          </div>
        ) : null}
      </Space>
    )

    if (job.status === 'Failed') {
      notification.error({
        message: t('containers.messages.createProductsJobFailed', '创建新商品失败'),
        description,
        duration: 0,
      })
      return
    }

    if (result.failedCount > 0 || result.errors.length > 0 || result.skippedCount > 0) {
      notification.warning({
        message: t('containers.messages.createProductsJobPartialSucceeded', '创建新商品部分完成'),
        description,
        duration: 0,
      })
      return
    }

    notification.success({
      message: t('containers.messages.createProductsJobSucceeded', '创建新商品完成'),
      description,
      duration: 0,
    })
  }

  const showSubmitContainerJobResult = (job: ContainerProductCreationJob) => {
    const result = job.result
    const description = (
      <Space direction="vertical" size={8}>
        <Typography.Text>
          {t('containers.text.submitContainerJobSummary', '提交完成：创建 {{created}}，更新 {{updated}}，跳过 {{skipped}}，失败 {{failed}}', {
            created: result.createdCount,
            updated: result.updatedCount,
            skipped: result.skippedCount,
            failed: result.failedCount,
          })}
        </Typography.Text>
        <Typography.Text type={result.containerCompleted ? 'success' : 'secondary'}>
          {result.containerCompleted
            ? t('containers.messages.containerMarkedCompleted', '货柜已标记为已完成')
            : t('containers.messages.containerNotMarkedCompleted', '货柜状态未变更')}
        </Typography.Text>
        {job.message ? <Typography.Text type="secondary">{job.message}</Typography.Text> : null}
        {result.updated.length ? (
          <div>
            <Typography.Text strong>{t('containers.text.updatedRows', '更新明细')}</Typography.Text>
            {renderCreateProductResultItems(result.updated)}
          </div>
        ) : null}
        {result.skipped.length ? (
          <div>
            <Typography.Text strong>{t('containers.text.skippedRows', '跳过明细')}</Typography.Text>
            {renderCreateProductResultItems(result.skipped)}
          </div>
        ) : null}
        {result.errors.length ? (
          <div>
            <Typography.Text strong>{t('containers.text.failedRows', '失败明细')}</Typography.Text>
            {renderCreateProductResultItems(result.errors)}
          </div>
        ) : null}
      </Space>
    )

    if (job.status === 'Failed' || result.failedCount > 0 || result.errors.length > 0) {
      notification.error({
        message: t('containers.messages.submitContainerJobFailed', '提交货柜失败'),
        description,
        duration: 0,
      })
      return
    }

    notification.success({
      message: t('containers.messages.submitContainerJobSucceeded', '提交货柜完成'),
      description,
      duration: 0,
    })
  }

  const submitContainer = async () => {
    if (submitContainerLoadingRef.current) return
    if (!canSubmitContainer) {
      message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'))
      return
    }
    if (!containerGuid) {
      message.warning(t('containers.messages.missingContainerGuid', '缺少货柜 GUID'))
      return
    }
    if (!ensureNoPendingDetails()) return
    blurActiveContainerDetailEditableCell()
    try {
      await flushPendingDetailSaves()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.messages.detailSaveFailed', '货柜明细保存失败，请稍后重试'))
      return
    }

    try {
      submitContainerLoadingRef.current = true
      setSubmitContainerLoading(true)
      const submitRows = await fetchAllRowsForWholeContainer()
      const confirmed = await confirmSubmitContainer(submitRows)
      if (!confirmed) return
      // 整柜提交由后端按当前货柜加载全部未删除明细，前端不传筛选或勾选范围。
      const job = await createContainerSubmitJob({
        operationId: buildContainerSubmitOperationId(containerGuid),
        containerGuid,
      })
      message.info(t('containers.messages.submitContainerJobSubmitted', '提交货柜任务已提交，正在后台处理'))
      const finalJob = job.status === 'Queued' || job.status === 'Running'
        ? await waitForContainerSubmitJob(job.jobId)
        : job
      showSubmitContainerJobResult(finalJob)
      if (finalJob.result.containerCompleted) {
        setSelectedRowKeys([])
        await loadData(false)
      }
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.messages.submitContainerFailed', '提交货柜失败'))
    } finally {
      submitContainerLoadingRef.current = false
      setSubmitContainerLoading(false)
    }
  }

  const createNewProducts = async () => {
    if (createProductsLoadingRef.current) return
    if (!access.canEditContainer || !access.canManagePosProducts) {
      message.warning(t('posAdmin.products.noManagePermission', '无权限管理商品'))
      return
    }
    if (!ensureNoPendingDetails()) return
    const scopedRows = await confirmBatchRows(t('containers.actions.createNewProducts'))
    if (!scopedRows) return
    const detailHguids = scopedRows.map((row) => row.hguid).filter((value): value is string => Boolean(value))
    if (!detailHguids.length) {
      message.warning(t('containers.messages.noEligibleNewProducts'))
      return
    }
    blurActiveContainerDetailEditableCell()
    try {
      await flushPendingDetailSaves()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.messages.detailSaveFailed', '货柜明细保存失败，请稍后重试'))
      return
    }
    const missingProductNameRows = findContainerDetailRowsMissingProductName(scopedRows)
    if (missingProductNameRows.length) {
      message.warning(t(
        'containers.messages.createProductsMissingProductName',
        '请填写商品名称后再创建新商品：{{items}}',
        { items: missingProductNameRows.map((row) => row.label).join('、') },
      ))
      return
    }
    const missingRetailPriceRows = findContainerDetailRowsMissingCreateProductRetailPrice(scopedRows)
    if (missingRetailPriceRows.length) {
      message.warning(t(
        'containers.messages.createProductsMissingRetailPrice',
        '请填写大于 0 的零售价后再创建新商品：{{items}}',
        { items: missingRetailPriceRows.map((row) => row.label).join('、') },
      ))
      return
    }
    const operationId = buildContainerCreateProductsOperationId(containerGuid, detailHguids)

    try {
      // 创建新商品是跨库后台任务，使用即时锁配合 operationId 防止重复提交。
      createProductsLoadingRef.current = true
      setCreateProductsLoading(true)
      const job = await createContainerProductCreationJob({
        operationId,
        containerGuid,
        detailHguids,
      })
      message.info(t('containers.messages.createProductsJobSubmitted', '创建新商品任务已提交，正在后台处理'))
      const finalJob = job.status === 'Queued' || job.status === 'Running'
        ? await waitForContainerProductCreationJob(job.jobId)
        : job
      showCreateProductsJobResult(finalJob)
      setSelectedRowKeys([])
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.messages.createProductFailed', '创建新商品失败'))
    } finally {
      createProductsLoadingRef.current = false
      setCreateProductsLoading(false)
    }
  }

  const updateExistingPurchase = async () => {
    if (!ensureNoPendingDetails()) return
    const confirmed = await confirmBatchRowsWithUpdateFields(
      t('containers.actions.updateExistingPurchase'),
      containerExistingProductUpdateFields,
      defaultContainerExistingProductUpdateFields,
    )
    if (!confirmed) return
    if (!await drainAutoSavesBeforeAction()) return
    const { rows: scopedRows, fields: updateFields } = confirmed
    const candidates = scopedRows.filter((row) => !row.是否新商品)
    if (!candidates.length) {
      message.info(t('containers.messages.noExistingProductsToUpdate'))
      return
    }
    const shouldUpdate = (field: ContainerExistingProductUpdateField) => updateFields.includes(field)
    const hasPositiveImportPrice = (row: ContainerDetail) => (row.进口价格 ?? 0) > 0
    const hasPositiveOemPrice = (row: ContainerDetail) => (getContainerDetailVisibleOemPrice(row) ?? 0) > 0
    const updates = candidates.filter((row) => {
      const code = row.商品编码 || row.商品信息?.商品编码 || ''
      // 更新已有商品时零售价使用表格可见价，已有商品取仓库实时零售价。
      return Boolean(code) && (
        (shouldUpdate('domesticPrice') && row.国内价格 != null)
        || (shouldUpdate('importPrice') && hasPositiveImportPrice(row))
        || (shouldUpdate('oemPrice') && hasPositiveOemPrice(row))
        || (shouldUpdate('volume') && row.单件体积 != null)
        || (shouldUpdate('storePurchasePrice') && hasPositiveImportPrice(row))
        || (shouldUpdate('storeRetailPrice') && hasPositiveOemPrice(row))
        || (shouldUpdate('storeMultiCodePurchasePrice') && hasPositiveImportPrice(row))
        || (shouldUpdate('storeMultiCodeRetailPrice') && hasPositiveOemPrice(row))
      )
    })
    if (!updates.length) {
      message.info(t('containers.messages.noPurchasePriceDiff'))
      return
    }
    try {
      const warehouseUpdates = updates.map((row) => {
        const item: WarehouseProductBatchUpdateItem = {
          ProductCode: row.商品编码 || row.商品信息?.商品编码,
        }
        const oemPrice = getContainerDetailVisibleOemPrice(row)
        if (shouldUpdate('domesticPrice') && row.国内价格 != null) item.DomesticPrice = row.国内价格
        if (shouldUpdate('importPrice') && hasPositiveImportPrice(row)) item.ImportPrice = row.进口价格
        if (shouldUpdate('oemPrice') && hasPositiveOemPrice(row)) item.OEMPrice = oemPrice
        if (shouldUpdate('volume') && row.单件体积 != null) item.Volume = row.单件体积
        return item
      }).filter((item) => Object.keys(item).length > 1)

      if (warehouseUpdates.length) {
        const warehouseResult = await batchUpdateWarehouseProducts(warehouseUpdates, {
          // 货柜页已把分店进货价拆成独立勾选项，避免主表进口价默认联动分店表。
          syncStorePurchasePrice: shouldUpdate('storePurchasePrice'),
        })
        const warehouseFailedCount = Number(
          warehouseResult.failedCount
          ?? warehouseResult.FailedCount
          ?? warehouseResult.failed
          ?? warehouseResult.Failed
          ?? 0,
        )
        if (warehouseFailedCount > 0) {
          // 批量更新服务会返回逐项失败；货柜链路必须在此中止，禁止继续写分店价格。
          const warehouseErrors = warehouseResult.errors ?? warehouseResult.Errors ?? []
          throw new Error(
            warehouseErrors.join('；')
            || warehouseResult.message
            || t('containers.messages.updateExistingPurchaseFailed', '更新仓库商品失败'),
          )
        }
      }

      if (shouldUpdate('storePurchasePrice') || shouldUpdate('storeRetailPrice')) {
        const retailUpdates = updates.map((row) => {
          const item: StoreRetailPriceUpsertActiveItem = {
            ProductCode: row.商品编码 || row.商品信息?.商品编码 || '',
          }
          const oemPrice = getContainerDetailVisibleOemPrice(row)
          if (shouldUpdate('storePurchasePrice') && hasPositiveImportPrice(row)) item.PurchasePrice = row.进口价格
          if (shouldUpdate('storeRetailPrice') && hasPositiveOemPrice(row)) item.StoreRetailPriceValue = oemPrice
          return item
        }).filter((item) => Object.keys(item).length > 1)
        if (retailUpdates.length) {
          await upsertRetailForActiveStores(retailUpdates)
        }
      }

      if (shouldUpdate('storeMultiCodePurchasePrice') || shouldUpdate('storeMultiCodeRetailPrice')) {
        const multiCodeUpdates = updates.map((row) => {
          const item: StoreMultiCodePriceUpsertActiveItem = {
            ProductCode: row.商品编码 || row.商品信息?.商品编码 || '',
          }
          const oemPrice = getContainerDetailVisibleOemPrice(row)
          if (shouldUpdate('storeMultiCodePurchasePrice') && hasPositiveImportPrice(row)) item.PurchasePrice = row.进口价格
          if (shouldUpdate('storeMultiCodeRetailPrice') && hasPositiveOemPrice(row)) item.MultiCodeRetailPrice = oemPrice
          return item
        }).filter((item) => Object.keys(item).length > 1)
        if (multiCodeUpdates.length) {
          await upsertMultiCodeForActiveStores(multiCodeUpdates)
        }
      }
      message.success(t('containers.messages.purchasePricesUpdated', { count: updates.length }))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.messages.purchasePricesUpdateFailed', '更新已有商品价格失败'))
    }
  }

  const deleteSelected = () => {
    if (!selectedRowKeys.length) {
      message.warning(t('containers.messages.selectDetails'))
      return
    }
    Modal.confirm({
      title: t('containers.modals.deleteDetailsTitle'),
      content: t('containers.modals.deleteDetailsContent', { count: selectedRowKeys.length }),
      okText: t('containers.actions.confirmDelete'),
      okButtonProps: { danger: true },
      onOk: async () => {
        if (!await drainAutoSavesBeforeAction()) return
        const hguids = selectedRows.map((row) => row.hguid).filter((value): value is string => Boolean(value))
        const scope = { selectedHguids: hguids }
        const previewToken = await confirmPreviewedContainerDetailAction(
          'delete-details', scope, {}, t('containers.actions.deleteDetails', '删除明细'), true,
        )
        if (!previewToken) return
        try {
          await deleteContainerDetailsByScope(containerGuid, scope, previewToken)
          setRows((items) => items.filter((item) => !hguids.includes(item.hguid)))
          setSelectedRowKeys([])
          message.success(t('containers.messages.detailsDeleted', { count: hguids.length }))
        } catch (error) {
          if (await handleExpiredContainerDetailActionPreview(error, 'delete-details', scope, {})) {
            message.info(t('containers.messages.batchPreviewReconfirmRequired', '批量预览已更新，请再次确认后执行'))
            return
          }
          throw error
        }
      },
    })
  }

  const confirmExportAllRows = () => new Promise<boolean>((resolve) => {
    Modal.confirm({
      title: t('containers.modals.exportAllDetailsTitle', '导出全部匹配商品'),
      content: t(
        'containers.modals.exportAllDetailsContent',
        '未选择商品，将按当前筛选和排序导出全部匹配商品，共 {{count}} 条。是否继续？',
        { count: detailItemsTotal },
      ),
      okText: t('common.confirm', '确认'),
      cancelText: t('common.cancel'),
      onOk: () => resolve(true),
      onCancel: () => resolve(false),
    })
  })

  const confirmExportWholeContainer = () => new Promise<boolean>((resolve) => {
    Modal.confirm({
      title: t('containers.modals.exportWholeContainerTitle', '全部导出（含图片）'),
      content: t(
        'containers.modals.exportWholeContainerContent',
        '将忽略当前勾选和筛选，导出本货柜全部明细及商品图片，并保留当前排序。数据较多时需要一些时间，是否继续？',
      ),
      okText: t('common.confirm', '确认'),
      cancelText: t('common.cancel'),
      onOk: () => resolve(true),
      onCancel: () => resolve(false),
    })
  })

  const exportDetails = async (
    columnKeys: ContainerDetailExportColumnKey[] = DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS,
    format: ContainerDetailExportFormat = 'excel',
    exportScope: ContainerDetailExportScope = 'selectionOrFiltered',
  ) => {
    if (exportScope !== 'wholeContainer' && !ensureTargetRowsVisible()) return
    if (exportScope === 'wholeContainer') {
      const confirmed = await confirmExportWholeContainer()
      if (!confirmed) return
    } else if (!selectedRowKeys.length) {
      const confirmed = await confirmExportAllRows()
      if (!confirmed) return
    }
    setExporting(true)
    updateExportProgressState(
      1,
      exportScope === 'wholeContainer'
        ? t('containers.messages.exportLoadingAllDetails', '正在加载整柜明细')
        : format === 'pdf'
          ? t('containers.messages.exportPreparingImages', '正在准备商品图片')
          : t('containers.messages.exportWritingWorkbook', '正在写入 Excel 表格'),
    )
    try {
      // 下拉菜单点击会先触发输入框 blur；先等待自动保存结束，失败值仍由后续页面快照选择性叠加。
      if (exportScope === 'wholeContainer') {
        await waitForPendingDetailSavesForExport()
      }
      const exportRows = exportScope === 'wholeContainer'
        ? await fetchAllRowsForWholeContainerExport()
        : selectedRowKeys.length ? targetRows : await fetchAllRowsForCurrentQuery()
      if (!exportRows.length) {
        message.warning(t('containers.messages.noDataToExport'))
        return
      }
      const exportColumns = getContainerDetailExportColumns(columnKeys)
      const items: ContainerDetailExportItem[] = buildContainerDetailExportRows(
        exportRows,
        {
          getProductTypeLabel: (value) => getProductTypeLabel(value, t),
          getMatchTypeLabel: (value) => getMatchTypeLabel(value, t),
          newProductLabel: t('containers.tags.newProduct', '新商品'),
          existingProductLabel: t('containers.tags.existingProduct', '已有商品'),
          activeLabel: t('common.activeUpper', '上架'),
          inactiveLabel: t('common.inactiveUpper', '下架'),
          missingNumericValue: exportScope === 'wholeContainer' ? '' : 0,
        },
      )
      const baseSummaryRows: NonNullable<ContainerExportOptions['summary']>['rows'] = [
        [
          { label: t('containers.fields.containerNumber'), value: container?.货柜编号 || '--' },
          { label: t('containers.fields.loadingDate'), value: formatDate(container?.装柜日期) },
          { label: t('containers.fields.estimatedArrival'), value: formatDate(container?.预计到岸日期) },
        ],
        [
          { label: t('containers.fields.status'), value: getContainerStatusText(container?.状态, t) },
          { label: t('containers.fields.actualArrival'), value: formatDate(container?.实际到货日期) },
          { label: t('containers.fields.exchangeRate'), value: container?.汇率 ?? '--', valueType: container?.汇率 == null ? 'text' : 'number' },
        ],
        [
          { label: t('containers.fields.freight'), value: container?.运费 ?? '--', valueType: container?.运费 == null ? 'text' : 'money' },
          { label: t('containers.fields.totalVolume'), value: container?.总体积 ?? '--', valueType: container?.总体积 == null ? 'text' : 'volume' },
          { label: t('containers.fields.remark'), value: container?.备注 || '--' },
        ],
      ]
      // PDF 对外分享时不展示汇率和运费金额；Excel 仍保留完整核对信息。
      const summaryRows: NonNullable<ContainerExportOptions['summary']>['rows'] = format === 'pdf'
        ? [
            [
              { label: t('containers.fields.containerNumber'), value: container?.货柜编号 || '--' },
              { label: t('containers.fields.loadingDate'), value: formatDate(container?.装柜日期) },
              { label: t('containers.fields.estimatedArrival'), value: formatDate(container?.预计到岸日期) },
            ],
            [
              { label: t('containers.fields.status'), value: getContainerStatusText(container?.状态, t) },
              { label: t('containers.fields.actualArrival'), value: formatDate(container?.实际到货日期) },
              { label: t('containers.fields.totalVolume'), value: container?.总体积 ?? '--', valueType: container?.总体积 == null ? 'text' : 'volume' },
            ],
            [
              { label: t('containers.fields.remark'), value: container?.备注 || '--' },
            ],
          ]
        : baseSummaryRows
      const exportOptions: ContainerExportOptions = {
        columns: exportColumns.map((column) => ({
          header: t(
            exportScope === 'wholeContainer'
              ? WHOLE_CONTAINER_DETAIL_EXPORT_LABEL_KEYS[column.key] ?? column.labelKey
              : column.labelKey,
            column.fallbackLabel,
          ),
          key: column.key,
          width: column.width,
          valueType: column.valueType,
          currencySymbol: column.key === 'domesticPrice' ? '¥' as const : undefined,
        })),
        summary: {
          title: t('containers.export.summaryTitle', '货柜主表信息'),
          rows: summaryRows,
        },
        fileName: `${container?.货柜编号 || t('containers.detailTitle')}_${t('containers.export.detailSuffix')}`,
        productImageDownloadFailedText: t('containers.export.productImageDownloadFailed', '图片下载失败'),
        productImageMissingText: t('containers.export.productImageMissing', '无图片'),
        onProgress: (progress: number) => {
          const hasImageColumns = exportColumns.some((column) => column.key === 'productImage' || column.key === 'barcodeImage')
          const progressMessage = progress >= 100
            ? t('containers.messages.exportComplete', '导出完成')
            : format === 'pdf' && progress >= 70
              ? t('containers.messages.exportGeneratingPdf', '正在生成 PDF 文件')
            : progress >= 95
              ? t('containers.messages.exportGeneratingFile', '正在生成 Excel 文件')
              : progress >= 70 || !hasImageColumns
                ? t('containers.messages.exportWritingWorkbook', '正在写入 Excel 表格')
                : t('containers.messages.exportPreparingImages', '正在准备商品图片')
          updateExportProgressState(progress, progressMessage)
        },
      }
      let failedProductImageCount = 0
      if (format === 'pdf') {
        await exportContainerDetailsToPdf(items, exportOptions)
      } else {
        const result = await exportContainerDetailsToExcel(items, exportOptions)
        failedProductImageCount = result.failedProductImageCount
      }
      if (format === 'excel' && failedProductImageCount > 0) {
        message.warning(t(
          'containers.messages.detailsExportedWithImageFailures',
          '已导出 {{count}} 条明细，其中 {{failed}} 张商品图片下载失败',
          { count: items.length, failed: failedProductImageCount },
        ))
      } else {
        message.success(
          format === 'pdf'
            ? t('containers.messages.detailsPdfExported', '已导出 {{count}} 条明细 PDF', { count: items.length })
            : t('containers.messages.detailsExported', { count: items.length }),
        )
      }
    } catch {
      message.error(t('containers.messages.detailsExportFailed', '导出失败，请稍后重试'))
    } finally {
      setExporting(false)
      setExportProgress(0)
      setExportProgressMessage('')
    }
  }

  const clearColumnFilter = (key: keyof ContainerDetailColumnFilters) => {
    setColumnFilters((current) => {
      const next = { ...current }
      delete next[key]
      return next
    })
  }

  const hasNumberRangeFilter = (value?: ContainerDetailNumberRangeFilter) => value?.min != null || value?.max != null
  const hasCustomSortState = sortState.field !== DEFAULT_CONTAINER_DETAIL_SORT.field || sortState.order !== DEFAULT_CONTAINER_DETAIL_SORT.order
  const hasActiveColumnState = Object.values(columnFilters).some((value) => {
    if (Array.isArray(value)) return value.length > 0
    if (value && typeof value === 'object') return hasNumberRangeFilter(value as ContainerDetailNumberRangeFilter)
    return typeof value === 'string' ? Boolean(value.trim()) : value != null
  }) || hasCustomSortState

  const filterIcon = (active?: boolean) => <SearchOutlined style={{ color: active ? '#1677ff' : undefined }} />

  const makeTextFilterDropdown = (key: TextColumnFilterKey, placeholder: string) => function ContainerDetailTextFilterDropdown({ confirm }: FilterDropdownProps) {
    return (
      <div className="container-detail-column-filter" onKeyDown={(event) => event.stopPropagation()}>
        <Input
          value={(columnFilters[key] as string | undefined) ?? ''}
          allowClear
          placeholder={placeholder}
          onChange={(event) => setColumnFilters((current) => ({ ...current, [key]: event.target.value }))}
          onPressEnter={() => confirm()}
        />
        <Space>
          <Button size="small" type="primary" onClick={() => confirm()}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
          <Button size="small" onClick={() => {
            clearColumnFilter(key)
            confirm()
          }}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
        </Space>
      </div>
    )
  }

  const makeNumberRangeFilterDropdown = (key: NumberColumnFilterKey) => function ContainerDetailNumberRangeFilterDropdown({ confirm }: FilterDropdownProps) {
    const value = (columnFilters[key] as ContainerDetailNumberRangeFilter | undefined) ?? {}
    const updateRange = (patch: ContainerDetailNumberRangeFilter) => {
      const next = { ...value, ...patch }
      setColumnFilters((current) => ({ ...current, [key]: next }))
    }

    return (
      <div className="container-detail-column-filter" onKeyDown={(event) => event.stopPropagation()}>
        <Space.Compact>
          <InputNumber
            value={value.min}
            placeholder={t('containers.placeholders.minValue', '最小值')}
            controls={false}
            onChange={(nextValue) => updateRange({ min: nextValue == null ? undefined : Number(nextValue) })}
          />
          <InputNumber
            value={value.max}
            placeholder={t('containers.placeholders.maxValue', '最大值')}
            controls={false}
            onChange={(nextValue) => updateRange({ max: nextValue == null ? undefined : Number(nextValue) })}
          />
        </Space.Compact>
        <Space>
          <Button size="small" type="primary" onClick={() => confirm()}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
          <Button size="small" onClick={() => {
            clearColumnFilter(key)
            confirm()
          }}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
        </Space>
      </div>
    )
  }

  const makeEnumFilterDropdown = (key: EnumColumnFilterKey, options: { value: string; label: string }[]) => function ContainerDetailEnumFilterDropdown({ confirm }: FilterDropdownProps) {
    return (
      <div className="container-detail-column-filter" onKeyDown={(event) => event.stopPropagation()}>
        <Select
          mode="multiple"
          value={(columnFilters[key] as string[] | undefined) ?? []}
          allowClear
          style={{ minWidth: 180 }}
          options={options}
          onChange={(values) => setColumnFilters((current) => ({ ...current, [key]: values } as ContainerDetailColumnFilters))}
        />
        <Space>
          <Button size="small" type="primary" onClick={() => confirm()}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
          <Button size="small" onClick={() => {
            clearColumnFilter(key)
            confirm()
          }}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
        </Space>
      </div>
    )
  }

  const makeSortProps = (field: ContainerDetailSortField) => ({
    key: field,
    sorter: true,
    sortOrder: sortState?.field === field ? sortState.order : null,
  })

  const textFilterProps = (key: TextColumnFilterKey, placeholder: string) => ({
    filterDropdown: makeTextFilterDropdown(key, placeholder),
    filterIcon,
    filtered: Boolean((columnFilters[key] as string | undefined)?.trim()),
  })

  const numberFilterProps = (key: NumberColumnFilterKey) => ({
    filterDropdown: makeNumberRangeFilterDropdown(key),
    filterIcon,
    filtered: hasNumberRangeFilter(columnFilters[key] as ContainerDetailNumberRangeFilter | undefined),
  })

  const enumFilterProps = (key: EnumColumnFilterKey, options: { value: string; label: string }[]) => ({
    filterDropdown: makeEnumFilterDropdown(key, options),
    filterIcon,
    filtered: Boolean((columnFilters[key] as string[] | undefined)?.length),
  })

  const handleTableChange = (pagination: TablePaginationConfig, _filters: Record<string, unknown>, sorter: SorterResult<ContainerDetail> | SorterResult<ContainerDetail>[]) => {
    const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const nextSortField = nextSorter?.columnKey ?? nextSorter?.field
    const nextSortState: ContainerDetailSortState = isContainerDetailSortField(nextSortField)
      && (nextSorter.order === 'ascend' || nextSorter.order === 'descend')
      ? { field: nextSortField, order: nextSorter.order }
      : DEFAULT_CONTAINER_DETAIL_SORT
    const sortChanged = nextSortState.field !== sortState.field || nextSortState.order !== sortState.order

    if (sortChanged) {
      setSortState(nextSortState)
    }

    if (detailLoadMode !== 'paged') return

    const nextPageSize = pagination.pageSize ?? detailPageSize
    const pageSizeChanged = nextPageSize !== detailPageSize
    setSelectedRowKeys([])
    setDetailPagingState({
      containerGuid,
      mode: 'paged',
      pageNumber: sortChanged || pageSizeChanged ? 1 : pagination.current ?? 1,
      pageSize: nextPageSize,
      // 新 pageSize/排序会生成新 scopeKey；先置空可保证本 render 就按第 1 页请求。
      scopeKey: sortChanged || pageSizeChanged ? '' : pagedDetailScopeKey,
    })
  }

  const handleDetailTableScroll = (event: UIEvent<HTMLDivElement>) => {
    const target = event.currentTarget
    lastDetailTableScrollTopRef.current = target.scrollTop
  }

  const handleWarehouseStatusChange = async (row: ContainerDetail, isActive: boolean) => {
    if (!access.canEditContainer) return
    // 新商品尚未写入仓库商品表，不能调用仓库商品上下架接口。
    if (row.是否新商品) {
      message.warning(t('containers.messages.newProductCannotToggleWarehouseStatus', '新商品请先创建后再上下架'))
      return
    }
    const productCode = getContainerDetailProductCode(row)
    if (!productCode) {
      message.warning(t('containers.messages.selectedProductsMissingCode'))
      return
    }
    const supplyNotice = isActive ? undefined : (await requestSupplyNotice(1)) ?? undefined
    if (!isActive && !supplyNotice) return
    if (!await drainAutoSavesBeforeAction()) return

    const statusRows = rows
      .filter((item) => getContainerDetailProductCode(item) === productCode)
    const previousStatuses = statusRows
      .map((item) => ({ key: rowKey(item), warehouseIsActive: item.warehouseIsActive }))
    const scope = buildDetailBatchScope(statusRows)
    const parameters = { isActive }

    try {
      const previewToken = await confirmPreviewedContainerDetailAction(
        'set-status', scope, parameters, t(isActive ? 'containers.actions.batchActivate' : 'containers.actions.batchDeactivate'),
      )
      if (!previewToken) return
      setPendingWarehouseStatusCodes((codes) => new Set(codes).add(productCode))
      setRows((items) => applyContainerDetailWarehouseStatusByProductCodes(items, [productCode], isActive))
      await setContainerDetailStatusByScope(containerGuid, scope, isActive, previewToken, supplyNotice)
      message.success(t(isActive ? 'containers.messages.productsActivated' : 'containers.messages.productsDeactivated', { count: 1 }))
    } catch (error) {
      setRows((items) => rollbackContainerDetailWarehouseStatuses(items, previousStatuses, rowKey))
      if (await handleExpiredContainerDetailActionPreview(error, 'set-status', scope, parameters)) {
        message.info(t('containers.messages.batchPreviewReconfirmRequired', '批量预览已更新，请再次确认后执行'))
      } else {
        message.error(error instanceof Error ? error.message : t('containers.messages.batchActiveFailed'))
      }
    } finally {
      setPendingWarehouseStatusCodes((codes) => {
        const next = new Set(codes)
        next.delete(productCode)
        return next
      })
    }
  }

  const renderProductTypeTag = (row: ContainerDetail) => {
    const productType = getContainerDetailProductType(row)
    const label = getProductTypeLabel(productType, t)
    const canOpenSetCodes = productType === '套装商品' && Boolean(getContainerDetailProductCode(row))

    return (
      <Tag
        color={getProductTypeTagColor(productType)}
        className={canOpenSetCodes ? 'container-detail-product-type-tag-clickable' : undefined}
        role={canOpenSetCodes ? 'button' : undefined}
        tabIndex={canOpenSetCodes ? 0 : undefined}
        onClick={canOpenSetCodes ? () => openSetCodeModal(row) : undefined}
        onKeyDown={canOpenSetCodes ? (event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault()
            openSetCodeModal(row)
          }
        } : undefined}
      >
        {label}
      </Tag>
    )
  }

  const readonlyOemPriceColumn: ColumnsType<ContainerDetail>[number] = {
    // 只读快览列只展示后端按新/已有商品分流后的来源价。
    key: 'readonlyOemPrice',
    title: renderCompactHeader(t('containers.fields.oemPrice')),
    width: 96,
    align: 'right',
    render: (_, row) => renderReadonlyOemPriceCell(row),
  }

  const baseColumns: ColumnsType<ContainerDetail> = [
    { key: 'index', title: renderCompactHeader(t('containers.columns.index')), width: 56, fixed: 'left', render: (_v, _r, index) => renderNumericCell(index + 1) },
    {
      key: 'image',
      title: renderCompactHeader(t('containers.columns.image')),
      width: 64,
      fixed: 'left',
      render: (_, row) => {
        const imageUrl = getContainerDetailImageUrl(row)

        return imageUrl ? (
          <Image
            className="container-detail-product-image"
            width={40}
            height={40}
            src={imageUrl}
            alt={row.商品信息?.货号 || row.商品信息?.商品名称 || ''}
            preview={{ mask: t('containers.actions.previewImage', '查看大图') }}
          />
        ) : (
          <span style={{ color: '#999' }}>{t('containers.empty.noImage')}</span>
        )
      },
    },
    {
      title: renderColumnTitle('itemNumber', t('containers.fields.itemNumber')),
      width: 156,
      fixed: 'left',
      ...makeSortProps('itemNumber'),
      ...textFilterProps('itemNumber', t('containers.placeholders.filterItemNumber')),
      render: (_, row) => {
        const productCode = getContainerDetailProductCode(row)
        const itemNumber = getContainerDetailItemNumber(row)
        const productName = getContainerDetailProductName(row)
        const canViewHistory = access.canManageWarehouseProducts && Boolean(productCode) && !row.是否新商品

        return (
          <span className="container-detail-copyable">
            <span style={{ minWidth: 0, flex: '1 1 auto', overflow: 'hidden' }}>
              <CopyableText value={itemNumber} />
            </span>
            {canViewHistory ? (
              <Tooltip title={t('containers.actions.viewProductHistory', '查看商品修改记录')}>
                <Button
                  type="text"
                  size="small"
                  aria-label={t('containers.actions.viewProductHistory', '查看商品修改记录')}
                  icon={<HistoryOutlined />}
                  onClick={(event) => {
                    event.stopPropagation()
                    setChangeHistoryProduct({ productCode: productCode!, itemNumber, productName })
                  }}
                />
              </Tooltip>
            ) : null}
          </span>
        )
      },
    },
    {
      title: renderColumnTitle('englishName', t('containers.fields.englishName')),
      width: 180,
      ...makeSortProps('englishName'),
      ...textFilterProps('englishName', t('containers.placeholders.filterEnglishName', '英文名称过滤')),
      render: (_, row) => {
        if (!access.canEditContainer) return <TwoLineText value={getContainerDetailEnglishName(row)} />
        const pendingPatch = row.hguid ? pendingDetailPatches[row.hguid] : undefined
        const validationError = getPendingContainerDetailEnglishNameError(pendingPatch)
        const saveFailure = getContainerDetailDraftFieldFailure(
          pendingDetailFailures,
          row.hguid,
          '英文名称',
        )
        const concurrencyConflict = resolveConcurrencyConflict(row, '英文名称')
        const validationMessage = concurrencyConflict?.message ?? saveFailure?.message ?? (validationError === 'CONTAINS_CHINESE'
          ? t('containers.messages.englishNameContainsChinese', '英文名称不能包含中文')
          : validationError === 'EMPTY_ENGLISH_NAME'
            ? t('containers.messages.emptyEnglishNameNotSaved', '空白英文名称不会保存，如需清空请使用“清除英文名称”')
            : undefined)
        return renderConcurrentEditableField(row, '英文名称', (
          <Input.TextArea
            ref={(cell) => setEditableCellRef(rowKey(row), 'englishName', cell)}
            className="container-detail-english-name-input"
            value={getContainerDetailEnglishName(row) ?? ''}
            autoSize={{ minRows: 1, maxRows: 2 }}
            style={{ resize: 'none' }}
            status={validationError || saveFailure || concurrencyConflict ? 'error' : undefined}
            aria-invalid={Boolean(validationError || saveFailure || concurrencyConflict)}
            aria-label={validationMessage
              ? `${t('containers.fields.englishName')}：${validationMessage}`
              : t('containers.fields.englishName')}
            title={validationMessage}
            onChange={(event) => markPendingDetailPatch(row, { 英文名称: event.target.value })}
            onKeyDown={(event) => handleEditableCellKeyDown(row, 'englishName', event)}
            onPaste={(event) => handleEditableCellPaste(row, 'englishName', event)}
          />
        ))
      },
    },
    {
      key: 'categoryName',
      title: renderCompactHeader(t('containers.fields.category', '分类')),
      width: 120,
      render: (_, row) => {
        const categoryCell = renderContainerDetailCategoryCell(row, categoryLookup, i18n.language)
        if (!canBatchSetCategory) return renderConcurrentEditableField(row, 'ProductCategoryGUID', categoryCell)

        return renderConcurrentEditableField(row, 'ProductCategoryGUID', (
          <button
            type="button"
            className="container-detail-category-button"
            onClick={(event) => {
              event.stopPropagation()
              openRowCategoryModal(row)
            }}
          >
            {categoryCell}
          </button>
        ))
      },
    },
    {
      title: renderColumnTitle('containerPieces', t('containers.fields.containerPieces')),
      dataIndex: '装柜件数',
      width: 76,
      align: 'right',
      ...makeSortProps('containerPieces'),
      ...numberFilterProps('containerPieces'),
      render: (v) => renderNumericCell(v ?? '--'),
    },
    {
      title: renderColumnTitle('packingQuantity', t('containers.fields.packingQuantity', '单件装箱数')),
      dataIndex: '单件装箱数',
      width: 88,
      align: 'right',
      ...makeSortProps('packingQuantity'),
      ...numberFilterProps('packingQuantity'),
      render: (_value, row) => {
        const saveFailure = getAutoSaveFailure(row, '单件装箱数')
        const concurrencyConflict = resolveConcurrencyConflict(row, '单件装箱数')
        return access.canEditContainer ? renderConcurrentEditableField(row, '单件装箱数', (
          <InputNumber
            ref={(cell) => setEditableCellRef(rowKey(row), 'packingQuantity', cell)}
            value={row.单件装箱数}
            keyboard={false}
            min={0}
            precision={0}
            step={1}
            controls={false}
            style={{ width: 72 }}
            status={saveFailure || concurrencyConflict ? 'error' : undefined}
            aria-invalid={Boolean(saveFailure || concurrencyConflict)}
            title={concurrencyConflict?.message ?? saveFailure?.message}
            onFocus={() => captureAutoSaveEditBaseline(row, '单件装箱数', row.单件装箱数)}
            onChange={(value) => patchAutoSaveRow(row, { 单件装箱数: value == null ? undefined : Number(value) })}
            onBlur={(event) => {
              if (!event.target.value.trim()) {
                restoreAutoSaveEditBaseline(row, '单件装箱数')
                return
              }
              clearAutoSaveEditBaseline(row, '单件装箱数')
              void savePackageMetricPatch(row, { 单件装箱数: Number(event.target.value) }).catch(handleDetailSaveError)
            }}
            onKeyDown={(event) => handleEditableCellKeyDown(row, 'packingQuantity', event)}
            onPaste={(event) => handleEditableCellPaste(row, 'packingQuantity', event)}
          />
        )) : renderConcurrentEditableField(row, '单件装箱数', renderNumericCell(formatNumber(row.单件装箱数, 0)))
      },
    },
    {
      title: renderColumnTitle('containerQuantity', t('containers.fields.containerQuantity')),
      dataIndex: '装柜数量',
      width: 76,
      align: 'right',
      ...makeSortProps('containerQuantity'),
      ...numberFilterProps('containerQuantity'),
      render: (v) => renderNumericCell(v ?? '--'),
    },
    {
      title: renderColumnTitle('unitVolume', t('containers.fields.unitVolume', '单件体积')),
      dataIndex: '单件体积',
      width: 86,
      align: 'right',
      ...makeSortProps('unitVolume'),
      ...numberFilterProps('unitVolume'),
      render: (_value, row) => {
        const saveFailure = getAutoSaveFailure(row, '单件体积')
        const concurrencyConflict = resolveConcurrencyConflict(row, '单件体积')
        return access.canEditContainer ? renderConcurrentEditableField(row, '单件体积', (
          <InputNumber
            ref={(cell) => setEditableCellRef(rowKey(row), 'unitVolume', cell)}
            value={row.单件体积}
            keyboard={false}
            min={0}
            precision={3}
            controls={false}
            style={{ width: 72 }}
            status={saveFailure || concurrencyConflict ? 'error' : undefined}
            aria-invalid={Boolean(saveFailure || concurrencyConflict)}
            title={concurrencyConflict?.message ?? saveFailure?.message}
            onFocus={() => captureAutoSaveEditBaseline(row, '单件体积', row.单件体积)}
            onChange={(value) => patchAutoSaveRow(row, { 单件体积: value == null ? undefined : Number(value) })}
            onBlur={(event) => {
              if (!event.target.value.trim()) {
                restoreAutoSaveEditBaseline(row, '单件体积')
                return
              }
              clearAutoSaveEditBaseline(row, '单件体积')
              void savePackageMetricPatch(row, { 单件体积: Number(event.target.value) }).catch(handleDetailSaveError)
            }}
            onKeyDown={(event) => handleEditableCellKeyDown(row, 'unitVolume', event)}
            onPaste={(event) => handleEditableCellPaste(row, 'unitVolume', event)}
          />
        )) : renderConcurrentEditableField(row, '单件体积', renderNumericCell(formatNumber(row.单件体积, 3)))
      },
    },
    {
      title: renderColumnTitle('domesticPrice', t('containers.fields.domesticPrice')),
      dataIndex: '国内价格',
      width: 86,
      align: 'right',
      ...makeSortProps('domesticPrice'),
      ...numberFilterProps('domesticPrice'),
      render: (v, row) => renderConcurrentEditableField(row, '国内价格', renderNumericCell(formatCurrency(v, '¥'))),
    },
    {
      title: renderColumnTitle('transportCost', t('containers.fields.transportCost')),
      dataIndex: '运输成本',
      width: 86,
      align: 'right',
      ...makeSortProps('transportCost'),
      ...numberFilterProps('transportCost'),
      render: (v, row) => renderConcurrentEditableField(row, '运输成本', renderNumericCell(formatCurrency(v, '$'))),
    },
    {
      title: renderColumnTitle('unitTransportCost', t('containers.fields.unitTransportCost', '单件运输成本')),
      width: 104,
      align: 'right',
      ...makeSortProps('unitTransportCost'),
      ...numberFilterProps('unitTransportCost'),
      render: (_, row) => renderNumericCell(formatCurrency(calculateContainerDetailUnitTransportCost(row), '$')),
    },
    {
      title: renderColumnTitle('floatRate', t('containers.fields.floatRate')),
      dataIndex: '调整浮率',
      width: 96,
      align: 'right',
      ...makeSortProps('floatRate'),
      ...numberFilterProps('floatRate'),
      render: (_value, row) => {
        const saveFailure = getAutoSaveFailure(row, '调整浮率')
        const concurrencyConflict = resolveConcurrencyConflict(row, '调整浮率')
        return access.canEditContainer ? renderConcurrentEditableField(row, '调整浮率', (
          <InputNumber
            ref={(cell) => setEditableCellRef(rowKey(row), 'floatRate', cell)}
            value={row.调整浮率}
            keyboard={false}
            precision={2}
            controls={false}
            style={{ width: 78 }}
            status={saveFailure || concurrencyConflict ? 'error' : undefined}
            aria-invalid={Boolean(saveFailure || concurrencyConflict)}
            title={concurrencyConflict?.message ?? saveFailure?.message}
            onFocus={() => captureAutoSaveEditBaseline(row, '调整浮率', row.调整浮率)}
            onChange={(value) => patchAutoSaveRow(row, { 调整浮率: value == null ? undefined : Number(value) })}
            onBlur={(event) => {
              if (!event.target.value.trim()) {
                restoreAutoSaveEditBaseline(row, '调整浮率')
                return
              }
              clearAutoSaveEditBaseline(row, '调整浮率')
              const value = Number(event.target.value)
              void saveFloatRatePatch(row, value).catch(handleDetailSaveError)
            }}
            onKeyDown={(event) => handleEditableCellKeyDown(row, 'floatRate', event)}
            onPaste={(event) => handleEditableCellPaste(row, 'floatRate', event)}
          />
        )) : renderConcurrentEditableField(row, '调整浮率', renderNumericCell(formatNumber(row.调整浮率, 2)))
      },
    },
    {
      title: renderColumnTitle('middlePackQuantity', t('containers.fields.middlePackQuantity', '中包数')),
      dataIndex: '中包数',
      width: 76,
      align: 'right',
      ...makeSortProps('middlePackQuantity'),
      ...numberFilterProps('middlePackQuantity'),
      render: (_value, row) => {
        const saveFailure = getAutoSaveFailure(row, '中包数')
        const concurrencyConflict = resolveConcurrencyConflict(row, '中包数')
        return access.canEditContainer ? renderConcurrentEditableField(row, '中包数', (
          <InputNumber
            ref={(cell) => setEditableCellRef(rowKey(row), 'middlePackQuantity', cell)}
            value={row.中包数}
            keyboard={false}
            min={0}
            precision={0}
            controls={false}
            style={{ width: 68 }}
            status={saveFailure || concurrencyConflict ? 'error' : undefined}
            aria-invalid={Boolean(saveFailure || concurrencyConflict)}
            title={concurrencyConflict?.message ?? saveFailure?.message}
            onFocus={() => captureAutoSaveEditBaseline(row, '中包数', row.中包数)}
            onChange={(value) => patchAutoSaveRow(row, { 中包数: value == null ? undefined : Number(value) })}
            onBlur={(event) => {
              if (!event.target.value.trim()) {
                restoreAutoSaveEditBaseline(row, '中包数')
                return
              }
              clearAutoSaveEditBaseline(row, '中包数')
              void saveRowPatch(row, { 中包数: Number(event.target.value) }).catch(handleDetailSaveError)
            }}
            onKeyDown={(event) => handleEditableCellKeyDown(row, 'middlePackQuantity', event)}
            onPaste={(event) => handleEditableCellPaste(row, 'middlePackQuantity', event)}
          />
        )) : renderConcurrentEditableField(row, '中包数', renderNumericCell(row.中包数 ?? '--'))
      },
    },
    {
      title: renderColumnTitle('warehouseImportPrice', t('containers.fields.warehouseImportPrice', '实时进货价')),
      dataIndex: 'warehouseImportPrice',
      width: 112,
      align: 'right',
      ...makeSortProps('warehouseImportPrice'),
      ...numberFilterProps('warehouseImportPrice'),
      render: (_value, row) => renderNumericCell(formatCurrency(getContainerDetailRealtimeImportPrice(row), '$')),
    },
    {
      title: renderColumnTitle('importPrice', t('containers.fields.importPrice')),
      dataIndex: '进口价格',
      width: 96,
      align: 'right',
      ...makeSortProps('importPrice'),
      ...numberFilterProps('importPrice'),
      render: (_value, row) => {
        const saveFailure = getContainerDetailDraftFieldFailure(
          pendingDetailFailures,
          row.hguid,
          '进口价格',
        )
        const concurrencyConflict = resolveConcurrencyConflict(row, '进口价格')
        return access.canEditContainer
          ? renderConcurrentEditableField(row, '进口价格', renderImportPriceCell(row, (
            <InputNumber
              ref={(cell) => setEditableCellRef(rowKey(row), 'importPrice', cell)}
              value={row.进口价格}
              keyboard={false}
              min={0}
              prefix="$"
              precision={2}
              controls={false}
              style={{ width: 78 }}
              status={saveFailure || concurrencyConflict ? 'error' : undefined}
              aria-invalid={Boolean(saveFailure || concurrencyConflict)}
              aria-label={saveFailure || concurrencyConflict
                ? `${t('containers.fields.importPrice')}：${concurrencyConflict?.message ?? saveFailure?.message}`
                : t('containers.fields.importPrice')}
              title={concurrencyConflict?.message ?? saveFailure?.message}
              onChange={(value) => markPendingDetailPatch(row, { 进口价格: value == null ? undefined : Number(value) })}
              onBlur={(event) => handlePendingImportPriceBlur(row, event.currentTarget.value)}
              onKeyDown={(event) => handleEditableCellKeyDown(row, 'importPrice', event)}
              onPaste={(event) => handleEditableCellPaste(row, 'importPrice', event)}
            />
          )))
          : renderConcurrentEditableField(row, '进口价格', renderImportPriceCell(row))
      },
    },
    {
      title: renderColumnTitle('oemPrice', t('containers.fields.oemPrice')),
      dataIndex: '贴牌价格',
      width: 96,
      align: 'right',
      ...makeSortProps('oemPrice'),
      ...numberFilterProps('oemPrice'),
      render: (_value, row) => {
        const saveFailure = getContainerDetailDraftFieldFailure(
          pendingDetailFailures,
          row.hguid,
          '贴牌价格',
        )
        const concurrencyConflict = resolveConcurrencyConflict(row, '贴牌价格')
        return access.canEditContainer ? renderConcurrentEditableField(row, '贴牌价格', (
          <InputNumber
            ref={(cell) => setEditableCellRef(rowKey(row), 'oemPrice', cell)}
            value={getContainerDetailVisibleOemPrice(row)}
            keyboard={false}
            min={0}
            prefix="$"
            precision={2}
            controls={false}
            style={{ width: 78 }}
            status={saveFailure || concurrencyConflict ? 'error' : undefined}
            aria-invalid={Boolean(saveFailure || concurrencyConflict)}
            aria-label={saveFailure || concurrencyConflict
              ? `${t('containers.fields.oemPrice')}：${concurrencyConflict?.message ?? saveFailure?.message}`
              : t('containers.fields.oemPrice')}
            title={concurrencyConflict?.message ?? saveFailure?.message}
            onChange={(value) => markPendingDetailPatch(row, { 贴牌价格: value == null ? undefined : Number(value) })}
            onKeyDown={(event) => handleEditableCellKeyDown(row, 'oemPrice', event)}
            onPaste={(event) => handleEditableCellPaste(row, 'oemPrice', event)}
          />
        )) : renderConcurrentEditableField(row, '贴牌价格', renderOemPriceCell(row))
      },
    },
    {
      title: renderColumnTitle('lastOEMPrice', t('containers.fields.lastOEMPrice', '实时零售价')),
      width: 104,
      align: 'right',
      ...makeSortProps('lastOEMPrice'),
      ...numberFilterProps('lastOEMPrice'),
      render: (_, row) => renderNumericCell(formatCurrency(getContainerDetailRealtimeRetailPrice(row), '$')),
    },
    {
      title: renderColumnTitle('newProduct', t('containers.fields.newProduct')),
      width: 72,
      ...makeSortProps('newProduct'),
      ...enumFilterProps('newProductStates', (['new', 'existing'] as ContainerDetailNewProductFilter[]).map((value) => ({
        value,
        label: value === 'new' ? t('containers.tags.newProduct') : t('containers.tags.existingProduct'),
      }))),
      render: (_, row) => {
        if (!isContainerDetailContainerNewProduct(row)) return <Tag>{t('containers.tags.existing')}</Tag>
        const newTag = <Tag color="blue">{t('containers.tags.new')}</Tag>
        // 列宽有限：本柜已建档的新品沿用同一个「新」标签，悬停说明已建档，避免与未建档混淆。
        return isContainerDetailCreatedContainerNewProduct(row)
          ? <Tooltip title={t('containers.tags.newCreatedHint')}>{newTag}</Tooltip>
          : newTag
      },
    },
    {
      title: renderColumnTitle('productType', t('containers.fields.productType')),
      width: 92,
      ...makeSortProps('productType'),
      ...enumFilterProps('productTypes', (['normal', 'set', 'multi', 'setChild'] as ContainerDetailProductTypeFilter[]).map((value) => ({ value, label: getProductTypeFilterLabel(value, t) }))),
      render: (_, row) => renderProductTypeTag(row),
    },
    {
      title: renderColumnTitle('matchType', t('containers.fields.matchType')),
      width: 148,
      ...makeSortProps('matchType'),
      ...enumFilterProps('matchTypes', (['productCode', 'supplierItem', 'unmatched'] as ContainerDetailMatchTypeFilter[]).map((value) => ({
        value,
        label: getMatchTypeLabel(value, t),
      }))),
      render: (_, row) => {
        const matchType = getContainerDetailMatchType(row)
        const localProductCode = getContainerDetailLocalProductCode(row)
        const isCandidate = hasContainerDetailProductCodeConflict(row) && Boolean(localProductCode)
        const canAlignCandidate =
          isCandidate
          && getContainerDetailProductType(row) !== '套装子商品'
          && canAlignDomesticProductCode

        return (
          <Space direction="vertical" size={2}>
            <Tag color={getMatchTypeTagColor(matchType)}>{getMatchTypeLabel(matchType, t)}</Tag>
            {canAlignCandidate ? (
              <Button
                type="link"
                size="small"
                onClick={() => handleAlignDomesticProductCode(row)}
                loading={aligningDomesticProductDetailHguid === row.hguid}
              >
                {t('containers.actions.alignDomesticProductCode', '对齐编码')}
              </Button>
            ) : null}
          </Space>
        )
      },
    },
    {
      title: renderColumnTitle('barcode', t('containers.fields.barcode')),
      width: 170,
      ...makeSortProps('barcode'),
      ...textFilterProps('barcode', t('containers.placeholders.filterBarcode', '条码过滤')),
      render: (_, row) => {
        const barcode = getContainerDetailBarcode(row)

        return barcode ? (
          <Space size={4} wrap={false} className="container-detail-barcode-cell">
            <BarcodePreview value={barcode} showText showCopy={false} options={{ height: 24 }} />
            <Tooltip title={t('common.copy', 'Copy')}>
              <Button
                size="small"
                type="text"
                aria-label={t('common.copyValue', 'Copy {{value}}', { value: barcode })}
                icon={<CopyOutlined />}
                className="container-detail-icon-button"
                onClick={(event) => {
                  event.stopPropagation()
                  void copyTextToClipboard(barcode)
                }}
              />
            </Tooltip>
          </Space>
        ) : '--'
      },
    },
    ...(showReadonlyOemPrice ? [readonlyOemPriceColumn] : []),
    {
      title: renderColumnTitle('productName', t('containers.fields.productName')),
      width: 180,
      ...makeSortProps('productName'),
      ...textFilterProps('productName', t('containers.placeholders.filterProductName', '商品名称过滤')),
      render: (_, row) => {
        const key = rowKey(row)
        const saveFailure = getAutoSaveFailure(row, '商品名称')
        const concurrencyConflict = resolveConcurrencyConflict(row, '商品名称')
        if (access.canEditContainer && editingProductNameRowKey === key) {
          return renderConcurrentEditableField(row, '商品名称', (
            <Input.TextArea
              autoFocus
              className="container-detail-product-name-input"
              value={editingProductNameValue}
              autoSize={{ minRows: 1, maxRows: 2 }}
              style={{ resize: 'none' }}
              status={saveFailure || concurrencyConflict ? 'error' : undefined}
              aria-invalid={Boolean(saveFailure || concurrencyConflict)}
              title={concurrencyConflict?.message ?? saveFailure?.message}
              onChange={(event) => setEditingProductNameValue(event.target.value)}
              onBlur={() => handleProductNameEditBlur(row)}
              onKeyDown={(event) => {
                if (event.key === 'Escape') {
                  event.preventDefault()
                  cancelEditingProductName()
                  return
                }
                if (event.key === 'Enter' && !event.shiftKey) {
                  event.preventDefault()
                  void commitProductNameEdit(row).catch(handleDetailSaveError)
                }
              }}
            />
          ))
        }

        return renderConcurrentEditableField(row, '商品名称', (
          <div
            className={[
              access.canEditContainer ? 'container-detail-product-name-editable' : '',
              saveFailure ? 'container-detail-auto-save-failed' : '',
              concurrencyConflict ? 'container-detail-concurrent-field-input' : '',
            ].filter(Boolean).join(' ') || undefined}
            aria-invalid={Boolean(saveFailure || concurrencyConflict)}
            title={concurrencyConflict?.message ?? saveFailure?.message}
            onDoubleClick={() => startEditingProductName(row)}
          >
            <TwoLineText value={getContainerDetailProductName(row)} />
          </div>
        ))
      },
    },
    {
      title: renderColumnTitle('warehouseStatus', t('containers.fields.warehouseStatus')),
      width: 100,
      ...makeSortProps('warehouseStatus'),
      ...enumFilterProps('warehouseStatus', (['active', 'inactive'] as ContainerDetailWarehouseStatusFilter[]).map((value) => ({
        value,
        label: value === 'active' ? t('common.activeUpper') : t('common.inactiveUpper'),
      }))),
      render: (_, row) => {
        const isActive = getContainerDetailWarehouseStatusFilterKey(row) === 'active'
        const productCode = getContainerDetailProductCode(row)
        const isWarehouseStatusPending = productCode ? pendingWarehouseStatusCodes.has(productCode) : false
        const warehouseStatusDisabledMessage = isWarehouseStatusPending
          ? ''
          : row.是否新商品
            ? t('containers.messages.newProductCannotToggleWarehouseStatus', '新商品请先创建后再上下架')
            : !productCode
              ? t('containers.messages.selectedProductsMissingCode')
              : ''

        return access.canEditContainer ? renderConcurrentEditableField(row, 'IsActive', (
          <Tooltip title={warehouseStatusDisabledMessage}>
            <Switch
              size="small"
              checked={isActive}
              checkedChildren={t('common.activeUpper')}
              unCheckedChildren={t('common.inactiveUpper')}
              loading={isWarehouseStatusPending}
              disabled={row.是否新商品 || !productCode || isWarehouseStatusPending}
              onChange={(checked) => void handleWarehouseStatusChange(row, checked)}
            />
          </Tooltip>
        )) : renderConcurrentEditableField(row, 'IsActive', (
          <Tag color={isActive ? 'success' : 'default'}>{isActive ? t('common.activeUpper') : t('common.inactiveUpper')}</Tag>
        ))
      },
    },
    {
      title: renderColumnTitle('remark', t('containers.fields.remark')),
      width: 160,
      ...makeSortProps('remark'),
      ...textFilterProps('remark', t('containers.placeholders.filterRemark', '备注过滤')),
      render: (_, row) => {
        const saveFailure = getAutoSaveFailure(row, '备注')
        const concurrencyConflict = resolveConcurrencyConflict(row, '备注')
        return access.canEditContainer ? renderConcurrentEditableField(row, '备注', (
          <Input
            ref={(cell) => setEditableCellRef(rowKey(row), 'remark', cell)}
            value={row.备注 ?? ''}
            status={saveFailure || concurrencyConflict ? 'error' : undefined}
            aria-invalid={Boolean(saveFailure || concurrencyConflict)}
            title={concurrencyConflict?.message ?? saveFailure?.message}
            onChange={(event) => patchAutoSaveRow(row, { 备注: event.target.value })}
            onBlur={(event) => void saveRowPatch(row, { 备注: event.target.value }).catch(handleDetailSaveError)}
            onKeyDown={(event) => handleEditableCellKeyDown(row, 'remark', event)}
            onPaste={(event) => handleEditableCellPaste(row, 'remark', event)}
          />
        )) : renderConcurrentEditableField(row, '备注', row.备注 || '--')
      },
    },
  ]

  const draggableColumnKeys = baseColumns.map((column) => String(column.key) as ContainerDetailTableColumnKey)
  const isColumnOrderCustomized = isContainerDetailColumnOrderCustomized(columnOrder, draggableColumnKeys)
  const isColumnWidthCustomized = Object.keys(columnWidths).length > 0
  const isColumnSettingsCustomized = isColumnOrderCustomized || isColumnWidthCustomized

  useEffect(() => {
    setColumnOrder((current) => {
      let savedOrder: unknown[] | null = null
      if (!current.length && typeof window !== 'undefined') {
        try {
          const raw = localStorage.getItem(CONTAINER_DETAIL_COLUMN_ORDER_STORAGE_KEY)
          savedOrder = raw ? JSON.parse(raw) : null
        } catch {
          savedOrder = null
        }
      }

      // 列顺序持久化只影响业务列；选择列仍交给 rowSelection，新增/废弃列在这里自动兼容。
      const nextOrder = mergeContainerDetailColumnOrder(current.length ? current : savedOrder, draggableColumnKeys)
      if (current.length === nextOrder.length && current.every((key, index) => key === nextOrder[index])) {
        return current
      }
      return nextOrder
    })
  }, [draggableColumnKeys.join('|')])

  useEffect(() => {
    setColumnWidths((current) => {
      let savedWidths: unknown = null
      if (!Object.keys(current).length && typeof window !== 'undefined') {
        try {
          const raw = localStorage.getItem(CONTAINER_DETAIL_COLUMN_WIDTH_STORAGE_KEY)
          savedWidths = raw ? JSON.parse(raw) : null
        } catch {
          savedWidths = null
        }
      }

      // 列宽持久化只保留当前业务列，避免旧列或临时列污染新表头布局。
      const nextWidths = normalizeContainerDetailColumnWidths(Object.keys(current).length ? current : savedWidths, draggableColumnKeys)
      if (areContainerDetailColumnWidthsEqual(current, nextWidths)) {
        return current
      }
      return nextWidths
    })
  }, [draggableColumnKeys.join('|')])

  const handleColumnDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return
    setColumnOrder((current) => {
      const nextOrder = moveContainerDetailColumnOrder(current, active.id, over.id)
      try {
        localStorage.setItem(CONTAINER_DETAIL_COLUMN_ORDER_STORAGE_KEY, JSON.stringify(nextOrder))
      } catch {
        // localStorage 不可用时不影响当前页面内拖拽排序。
      }
      return nextOrder
    })
  }

  const persistColumnWidths = (nextWidths: ContainerDetailColumnWidthMap) => {
    try {
      localStorage.setItem(CONTAINER_DETAIL_COLUMN_WIDTH_STORAGE_KEY, JSON.stringify(nextWidths))
    } catch {
      // localStorage 不可用时不影响当前页面内拖拽列宽。
    }
  }

  const handleColumnResizeStart = (columnKey: ContainerDetailTableColumnKey, startWidth: number, event: ReactPointerEvent<HTMLSpanElement>) => {
    const startX = event.clientX
    const previousCursor = document.body.style.cursor
    const previousUserSelect = document.body.style.userSelect

    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'

    const handlePointerMove = (pointerEvent: PointerEvent) => {
      const nextWidth = clampContainerDetailColumnWidth(startWidth + pointerEvent.clientX - startX)
      setColumnWidths((current) => {
        const nextWidths = { ...current, [columnKey]: nextWidth }
        persistColumnWidths(nextWidths)
        return nextWidths
      })
    }

    const stopResize = () => {
      document.removeEventListener('pointermove', handlePointerMove)
      document.removeEventListener('pointerup', stopResize)
      document.removeEventListener('pointercancel', stopResize)
      document.body.style.cursor = previousCursor
      document.body.style.userSelect = previousUserSelect
    }

    // 使用 document 级监听，指针拖出表头区域时仍能连续调整列宽。
    document.addEventListener('pointermove', handlePointerMove)
    document.addEventListener('pointerup', stopResize, { once: true })
    document.addEventListener('pointercancel', stopResize, { once: true })
  }

  const resetColumnOrder = () => {
    setColumnOrder(draggableColumnKeys)
    setColumnWidths({})
    try {
      localStorage.removeItem(CONTAINER_DETAIL_COLUMN_ORDER_STORAGE_KEY)
      localStorage.removeItem(CONTAINER_DETAIL_COLUMN_WIDTH_STORAGE_KEY)
    } catch {
      // localStorage 不可用时仍恢复当前页面内的默认列设置。
    }
    message.success(t('containers.messages.columnOrderReset', '列设置已恢复默认'))
  }

  const orderedBaseColumns = useMemo(() => {
    const activeOrder = columnOrder.length ? columnOrder : draggableColumnKeys
    const columnMap = new Map(baseColumns.map((column) => [String(column.key), column]))
    return activeOrder
      .map((key) => columnMap.get(key))
      .filter((column): column is ColumnsType<ContainerDetail>[number] => Boolean(column))
  }, [baseColumns, columnOrder, draggableColumnKeys])

  const orderedEditableColumnKeys = useMemo(
    () => getContainerDetailEditableColumnKeysInOrder(
      // 方向键导航必须跟随当前页面列顺序，而不是固定的默认可编辑列顺序。
      orderedBaseColumns.map((column) => String(column.key)),
      CONTAINER_DETAIL_EDITABLE_COLUMN_KEYS,
    ),
    [orderedBaseColumns],
  )

  // 小屏竖屏时取消 AntD 固定列，避免固定选择列和左侧列占掉主要阅读空间。
  const columns = (viewport.isSmallPortrait
    ? orderedBaseColumns.map((column) => ({ ...column, fixed: undefined }))
    : orderedBaseColumns
  ).map((column) => ({
    ...column,
    width: columnWidths[String(column.key) as ContainerDetailTableColumnKey] ?? column.width,
    onHeaderCell: () => {
      const columnKey = String(column.key) as ContainerDetailTableColumnKey
      const width = columnWidths[columnKey] ?? column.width
      return {
        'data-column-key': columnKey,
        'data-column-width': typeof width === 'number' ? width : CONTAINER_DETAIL_MIN_COLUMN_WIDTH,
        onColumnResizeStart: handleColumnResizeStart,
      } as DraggableHeaderCellProps
    },
  })) as ColumnsType<ContainerDetail>
  const tableScrollX = Math.max(
    CONTAINER_DETAIL_TABLE_SCROLL_X,
    CONTAINER_DETAIL_SELECTION_COLUMN_WIDTH + columns.reduce((total, column) => {
      const width = typeof column.width === 'number' ? column.width : Number(column.width)
      return total + (Number.isFinite(width) ? width : 0)
    }, 0),
  )
  const isSmallScreen = viewport.isSmallPortrait || viewport.isSmallLandscape
  const tableScrollY = calculateContainerDetailTableScrollY({
    viewportHeight: viewport.height,
    toolbarHeight: detailLayoutMetrics.toolbarHeight,
    tableChromeHeight: detailLayoutMetrics.tableChromeHeight,
    isSmallLandscape: viewport.isSmallLandscape,
    isSmallPortrait: viewport.isSmallPortrait,
    maxScrollY: CONTAINER_DETAIL_TABLE_SCROLL_Y,
  })
  const pageClassName = [
    'container-detail-page',
    isSmallScreen ? 'container-detail-page-small' : '',
    viewport.isSmallPortrait ? 'container-detail-page-small-portrait' : '',
    viewport.isSmallLandscape ? 'container-detail-page-small-landscape' : '',
  ].filter(Boolean).join(' ')

  return (
    <PageContainer title={container?.货柜编号 ? t('containers.detailTitleWithNumber', { number: container.货柜编号 }) : t('menu.containerDetail')}>
      <Modal
        title={t('containers.modals.batchUpdateFloatRateTitle', '批量修改浮率')}
        open={batchFloatRateModalOpen}
        okText={t('containers.actions.batchUpdateFloatRate', '批量修改浮率')}
        cancelText={t('common.cancel')}
        confirmLoading={batchFloatRateSaving}
        onOk={() => void submitBatchFloatRate()}
        onCancel={() => {
          setBatchFloatRateModalOpen(false)
          setBatchModalTargetCount(0)
          setBatchModalScopeRows([])
          setBatchFloatRate(null)
        }}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          {renderBatchActionContent(batchModalTargetCount)}
          <InputNumber
            autoFocus
            value={batchFloatRate}
            placeholder={t('containers.fields.floatRate')}
            precision={2}
            controls={false}
            style={{ width: '100%' }}
            onChange={setBatchFloatRate}
          />
        </Space>
      </Modal>
      <Modal
        title={t('containers.modals.batchUpdatePricesTitle', '批量修改价格')}
        open={batchPricesModalOpen}
        okText={t('containers.actions.batchUpdatePrices', '批量修改价格')}
        cancelText={t('common.cancel')}
        confirmLoading={batchPricesSaving}
        onOk={() => void submitBatchPrices()}
        onCancel={() => {
          setBatchPricesModalOpen(false)
          setBatchModalTargetCount(0)
          setBatchModalScopeRows([])
          setBatchImportPrice(null)
          setBatchOemPrice(null)
        }}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          {renderBatchActionContent(batchModalTargetCount)}
          <InputNumber
            autoFocus
            value={batchImportPrice}
            placeholder={t('containers.fields.importPrice')}
            min={0}
            prefix="$"
            precision={2}
            controls={false}
            style={{ width: '100%' }}
            onChange={setBatchImportPrice}
          />
          <InputNumber
            value={batchOemPrice}
            placeholder={t('containers.fields.oemPrice')}
            min={0}
            prefix="$"
            precision={2}
            controls={false}
            style={{ width: '100%' }}
            onChange={setBatchOemPrice}
          />
        </Space>
      </Modal>
      <Modal
        title={t('containers.modals.batchEditEnglishNameTitle')}
        open={batchEnglishNameModalOpen}
        okText={t('containers.actions.batchEditEnglishName')}
        onOk={() => void submitBatchEditEnglishName()}
        onCancel={() => {
          setBatchEnglishNameModalOpen(false)
          setBatchModalTargetCount(0)
          setBatchModalScopeRows([])
          setBatchEnglishName('')
        }}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          {renderBatchActionContent(batchModalTargetCount)}
          <Input
            autoFocus
            value={batchEnglishName}
            placeholder={t('containers.placeholders.batchEnglishName')}
            onChange={(event) => setBatchEnglishName(event.target.value)}
            onPressEnter={() => void submitBatchEditEnglishName()}
          />
        </Space>
      </Modal>
      <Modal
        title={t('containers.modals.batchCategoryTitle', '批量设置分类')}
        open={batchCategoryOpen}
        width={640}
        destroyOnHidden
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={batchCategorySaving}
        okButtonProps={{ disabled: !targetCategoryGuid || categoryLoading || !categories.length }}
        onCancel={() => {
          closeCategoryManageModal()
          setBatchCategoryOpen(false)
          setBatchModalTargetCount(0)
          setBatchModalScopeRows([])
          setTargetCategoryGuid(undefined)
        }}
        onOk={() => void handleBatchCategorySave()}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          {renderBatchActionContent(batchModalTargetCount)}
          <Typography.Text type="secondary">
            {t('containers.modals.batchCategoryContent', '请选择目标分类，确认后会把当前目标明细对应的仓库商品设置到该分类。')}
          </Typography.Text>
          {selectedTargetCategory ? (
            <Tag color="blue">
              {t('warehouse.categories.targetCategory', '目标分类')}: {selectedTargetCategoryPath || formatWarehouseCategoryNodeName(selectedTargetCategory, i18n.language)}
            </Tag>
          ) : null}
          <CategoryTreePicker
            categories={categories}
            selectedKey={targetCategoryGuid}
            expandedKeys={categoryExpandedKeys}
            onExpand={setCategoryExpandedKeys}
            onSelect={setTargetCategoryGuid}
            language={i18n.language}
            t={t}
            maxHeight={360}
          />
        </Space>
      </Modal>
      <Modal
        title={t('containers.modals.rowCategoryTitle', '目标分类修改')}
        open={rowCategoryOpen}
        width={640}
        destroyOnHidden
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={rowCategorySaving}
        okButtonProps={{ disabled: !rowTargetCategoryGuid || categoryLoading || !categories.length }}
        onCancel={closeRowCategoryModal}
        onOk={() => void handleRowCategorySave()}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          {rowCategoryEditingRow ? (
            <Typography.Text type="secondary">
              {getContainerDetailItemNumber(rowCategoryEditingRow) ?? getContainerDetailProductName(rowCategoryEditingRow) ?? '--'}
            </Typography.Text>
          ) : null}
          {selectedRowTargetCategory ? (
            <Tag color="blue">
              {t('warehouse.categories.targetCategory', '目标分类')}: {selectedRowTargetCategoryPath || formatWarehouseCategoryNodeName(selectedRowTargetCategory, i18n.language)}
            </Tag>
          ) : null}
          <CategoryTreePicker
            categories={categories}
            selectedKey={rowTargetCategoryGuid}
            expandedKeys={categoryExpandedKeys}
            onExpand={setCategoryExpandedKeys}
            onSelect={setRowTargetCategoryGuid}
            language={i18n.language}
            t={t}
            maxHeight={360}
          />
        </Space>
      </Modal>
      <SupplyNoticeModal
        open={Boolean(supplyNoticeRequest)}
        mode="delist"
        productCount={supplyNoticeRequest?.productCount ?? 0}
        onCancel={() => { supplyNoticeRequest?.resolve(null); setSupplyNoticeRequest(null) }}
        onSubmit={(notice) => { supplyNoticeRequest?.resolve(notice); setSupplyNoticeRequest(null) }}
      />
      {access.canManageWarehouseCategories ? (
        <ContainerCategoryManageModal
          open={categoryManageOpen}
          categories={categories}
          language={i18n.language}
          activeTargetCategoryGuid={categoryManageActiveTargetGuid}
          onCancel={closeCategoryManageModal}
          onMutationCommitted={handleCategoryMutationCommitted}
          onCategoriesChanged={handleCategoriesChanged}
        />
      ) : null}
      <div className={pageClassName}>
      <Spin spinning={loading}>
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          {!containerGuid ? <Alert type="warning" showIcon message={t('containers.messages.missingContainerGuid')} /> : null}
          <Card>
            <Space style={{ width: '100%', justifyContent: 'space-between' }} wrap>
              <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/warehouse/containers')}>{t('containers.actions.backToList')}</Button>
              <Space wrap>
                <Button icon={<ReloadOutlined />} onClick={() => void refreshContainerDetail()}>{t('common.refresh')}</Button>
                {access.canEditContainer ? (
                  headerEditing ? (
                    <Button type="primary" icon={<SaveOutlined />} loading={savingHeader} onClick={() => void saveHeader()}>{t('containers.actions.saveContainer')}</Button>
                  ) : (
                    <Button icon={<EditOutlined />} onClick={() => setHeaderEditing(true)}>{t('containers.actions.editContainer')}</Button>
                  )
                ) : null}
              </Space>
            </Space>
            <Descriptions bordered size="small" column={4} style={{ marginTop: 16 }}>
              <Descriptions.Item label={t('containers.fields.containerNumber')}>
                {headerEditing ? (
                  <Input
                    value={headerForm.货柜编号}
                    onChange={(event) => setHeaderForm((prev) => ({ ...prev, 货柜编号: event.target.value }))}
                  />
                ) : container?.货柜编号 || '--'}
              </Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.loadingDate')}>
                {headerEditing ? (
                  <DatePicker allowClear={false} value={headerForm.装柜日期} onChange={(value) => setHeaderForm((prev) => ({ ...prev, 装柜日期: value }))} />
                ) : formatDate(container?.装柜日期)}
              </Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.estimatedArrival')}>
                {headerEditing ? (
                  <DatePicker allowClear={false} value={headerForm.预计到岸日期} onChange={(value) => setHeaderForm((prev) => ({ ...prev, 预计到岸日期: value }))} />
                ) : formatDate(container?.预计到岸日期)}
              </Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.status')}>
                {headerEditing ? (
                  <Select
                    value={headerForm.状态}
                    style={{ minWidth: 120 }}
                    options={containerStatusOptions.map((option) => ({
                      value: option.value,
                      label: t(`containers.status.${option.labelKey}`),
                    }))}
                    onChange={(value) => setHeaderForm((prev) => ({ ...prev, 状态: value }))}
                  />
                ) : getStatusTag(container?.状态, t)}
              </Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.actualArrival')}>
                {headerEditing ? <DatePicker value={headerForm.实际到货日期} onChange={(value) => setHeaderForm((prev) => ({ ...prev, 实际到货日期: value }))} /> : formatDate(container?.实际到货日期)}
              </Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.exchangeRate')}>
                {headerEditing ? <InputNumber value={headerForm.汇率} precision={4} controls={false} onChange={(value) => setHeaderForm((prev) => ({ ...prev, 汇率: value ?? undefined }))} /> : formatNumber(container?.汇率, 4)}
              </Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.freight')}>
                {headerEditing ? (
                  <div className="container-detail-freight-calculator">
                    <Radio.Group
                      className="container-detail-freight-mode"
                      aria-label={t('containers.fields.freight')}
                      size="small"
                      optionType="button"
                      buttonStyle="solid"
                      value={freightInputMode}
                      options={[
                        {
                          value: 'standard68',
                          label: t('containers.freightCalculator.modes.standard68'),
                        },
                        {
                          value: 'perCbm',
                          label: t('containers.freightCalculator.modes.perCbm'),
                        },
                      ]}
                      onChange={(event) => handleFreightInputModeChange(event.target.value as ContainerFreightInputMode)}
                    />
                    <InputNumber
                      className="container-detail-freight-input"
                      aria-label={t(`containers.freightCalculator.modes.${freightInputMode}`)}
                      value={freightInputValue}
                      min={0}
                      precision={freightInputMode === 'perCbm' ? 4 : 2}
                      step={freightInputMode === 'perCbm' ? 0.0001 : 0.01}
                      controls={false}
                      disabled={!freightVolumeValid}
                      placeholder={t(`containers.freightCalculator.placeholders.${freightInputMode}`)}
                      onChange={handleFreightInputChange}
                    />
                    {!freightVolumeValid ? (
                      <Typography.Text role="alert" type="danger" className="container-detail-freight-feedback">
                        {t('containers.freightCalculator.invalidVolume')}
                      </Typography.Text>
                    ) : freightInputDirty && freightPreviewValue === undefined ? (
                      <Typography.Text role="alert" type="danger" className="container-detail-freight-feedback">
                        {t('containers.freightCalculator.invalidInput')}
                      </Typography.Text>
                    ) : (
                      <Typography.Text role="status" aria-live="polite" type="secondary" className="container-detail-freight-feedback">
                        {t('containers.freightCalculator.preview', {
                          volume: formatNumber(container?.总体积, 4),
                          freight: formatNumber(freightPreviewValue),
                        })}
                      </Typography.Text>
                    )}
                  </div>
                ) : formatNumber(container?.运费)}
              </Descriptions.Item>
              {/* 合计金额来自货柜主表汇总，编辑态也保持只读。 */}
              <Descriptions.Item label={t('containers.fields.domesticPriceTotal')}>{formatCurrency(container?.合计金额, '¥')}</Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.totalVolume')}>{formatNumber(container?.总体积, 4)}</Descriptions.Item>
              <Descriptions.Item label={t('containers.fields.remark')} span={3}>
                {headerEditing ? <Input.TextArea value={headerForm.备注} rows={2} onChange={(event) => setHeaderForm((prev) => ({ ...prev, 备注: event.target.value }))} /> : container?.备注 || '--'}
              </Descriptions.Item>
            </Descriptions>
          </Card>

          <Card className="container-detail-grid-card">
            <div ref={setGridContentElement} className="container-detail-grid-content">
              <div ref={setToolbarElement} className="container-detail-sticky-controls">
                <div className="container-detail-toolbar">
                  <div className="container-detail-action-row">
                    <Space wrap size={[6, 6]} className="container-detail-action-group">
                      <Dropdown
                        disabled={exporting}
                        menu={{
                          items: [
                            { key: 'excel', label: t('containers.actions.exportExcel', '导出 Excel') },
                            { key: 'pdf', label: t('containers.actions.exportPdf', '导出 PDF') },
                            { type: 'divider' },
                            { key: 'allExcelWithImages', label: t('containers.actions.exportAllExcelWithImages', '全部导出（含图片）') },
                          ],
                          onClick: ({ key }) => {
                            if (key === 'excel') {
                              void exportDetails(DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS, 'excel')
                            }
                            if (key === 'pdf') {
                              void exportDetails(DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMN_KEYS, 'pdf')
                            }
                            if (key === 'allExcelWithImages') {
                              void exportDetails(ALL_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS, 'excel', 'wholeContainer')
                            }
                          },
                        }}
                      >
                        <Button size="small" icon={<DownloadOutlined />} loading={exporting}>
                          {t('common.export')}
                        </Button>
                      </Dropdown>
                      <Dropdown
                        menu={{
                          items: [
                            { key: 'columns', label: t('containers.actions.selectExportColumns', '选择导出列') },
                          ],
                          onClick: ({ key }) => {
                            if (key === 'columns') {
                              setExportFormatWithDefaults('excel')
                              setExportColumnModalOpen(true)
                            }
                          },
                        }}
                      >
                        <Button size="small">{t('containers.actions.exportOptions', '导出选项')}</Button>
                      </Dropdown>
                      {isColumnSettingsCustomized ? (
                        <Button size="small" icon={<ReloadOutlined />} onClick={resetColumnOrder}>
                          {t('containers.actions.resetColumns', '重置列')}
                        </Button>
                      ) : null}
                      {access.canEditContainer ? (
                        <Button size="small" loading={hqTranslating} onClick={() => void translateHqData()}>
                          {t('containers.actions.translateHqData')}
                        </Button>
                      ) : null}
                      {canSubmitContainer ? (
                        <Tooltip title={pendingDetailSaveCount > 0 || pendingDetailPatchCount > 0 ? t('containers.messages.savePendingDetailsFirst', '请先点击“保存明细”保存待提交的明细修改') : ''}>
                          <Button
                            size="small"
                            type="primary"
                            icon={<CheckCircleOutlined />}
                            loading={submitContainerLoading}
                            disabled={submitContainerLoading || pendingDetailSaveCount > 0 || pendingDetailPatchCount > 0}
                            onClick={() => void submitContainer()}
                          >
                            {t('containers.actions.submitContainer', '提交货柜')}
                          </Button>
                        </Tooltip>
                      ) : null}
                      {access.canManagePosProducts ? (
                        <Tooltip title={!selectedRowKeys.length ? t('containers.messages.selectProducts') : ''}>
                          <Button
                            size="small"
                            icon={<CloudUploadOutlined />}
                            loading={pushToHqLoading}
                            disabled={!selectedRowKeys.length || pushToHqLoading}
                            onClick={() => void handlePushSelectedProductsToHq()}
                          >
                            {t('containers.actions.pushToHq', '发送到 HQ')}
                          </Button>
                        </Tooltip>
                      ) : null}
                      {access.canEditContainer ? (
                        <Dropdown
                          menu={{
                            items: [
                              { key: 'batchFloatRate', label: t('containers.actions.batchUpdateFloatRate', '批量修改浮率'), disabled: batchFloatRateSaving },
                              { key: 'batchPrices', label: t('containers.actions.batchUpdatePrices', '批量修改价格'), disabled: batchPricesSaving },
                              { key: 'matchDomesticData', label: t('containers.actions.matchDomesticData'), disabled: matchDomesticDataLoading },
                              { type: 'divider' },
                              { key: 'translate', label: t('containers.actions.batchTranslate') },
                              { key: 'editEnglishName', label: t('containers.actions.batchEditEnglishName') },
                              { key: 'clearEnglishName', label: t('containers.actions.clearEnglishNames') },
                              ...(canBatchSetCategory
                                ? [{ key: 'batchCategory', icon: <AppstoreOutlined />, label: t('containers.actions.batchSetCategory', '批量分类') }]
                                : []),
                              ...(canCreateContainerProducts
                                ? [{ key: 'createNew', label: t('containers.actions.createNewProducts'), disabled: createProductsLoading || pendingDetailSaveCount > 0 }]
                                : []),
                              { key: 'updatePurchase', label: t('containers.actions.updateExistingPurchase') },
                              { key: 'active', label: t('containers.actions.batchActivate') },
                              { key: 'inactive', label: t('containers.actions.batchDeactivate') },
                            ],
                            onClick: ({ key }) => {
                              if (key === 'batchFloatRate') void openBatchFloatRateModal()
                              if (key === 'batchPrices') void openBatchPricesModal()
                              if (key === 'matchDomesticData') void handleMatchDomesticData()
                              if (key === 'translate') void translateNames()
                              if (key === 'editEnglishName') void openBatchEditEnglishName()
                              if (key === 'clearEnglishName') void clearEnglishNames()
                              if (key === 'batchCategory') void openBatchCategory()
                              if (key === 'createNew') void createNewProducts()
                              if (key === 'updatePurchase') void updateExistingPurchase()
                              if (key === 'active') void applyActive(true)
                              if (key === 'inactive') void applyActive(false)
                            },
                          }}
                        >
                          <Button size="small">{t('containers.actions.batchActions')}</Button>
                        </Dropdown>
                      ) : null}
                      {access.canManageWarehouseCategories ? (
                        <Button
                          size="small"
                          icon={<SettingOutlined />}
                          onClick={() => openCategoryManageModal('batch')}
                        >
                          {t('containers.actions.manageCategories', '管理分类')}
                        </Button>
                      ) : null}
                      {access.canDeleteContainer ? <Button size="small" danger icon={<DeleteOutlined />} onClick={deleteSelected}>{t('containers.actions.deleteDetails')}</Button> : null}
                    </Space>
                    <Space wrap size={[8, 4]} className="container-detail-action-meta">
                      <Typography.Text type="secondary" className="container-detail-loaded-count">
                        {detailLoadMode === 'full'
                          ? t('containers.text.fullRows', '已完整加载 {{count}} 条', { count: rows.length })
                          : t('containers.text.pageRows', '当前页 {{loaded}} / 共 {{total}} 条', {
                              loaded: rows.length,
                              total: detailItemsTotal,
                            })}
                        {filteredRows.length !== rows.length ? ` ${t('containers.text.visibleRows', '当前可见 {{count}} 条', { count: filteredRows.length })}` : ''}
                        {detailLoading ? ` ${t('common.loading', '加载中')}` : ''}
                      </Typography.Text>
                      {hasActiveColumnState ? (
                        <Button size="small" className="container-detail-compact-button" onClick={() => {
                          setColumnFilters({})
                          setSortState(DEFAULT_CONTAINER_DETAIL_SORT)
                        }}>
                          {t('containers.actions.clearColumnFilters', '清空列过滤')}
                        </Button>
                      ) : null}
                      <Space size={6} className="container-detail-readonly-toggle">
                        <Typography.Text type="secondary">{t('containers.actions.showReadonlyOemPrice', '只读零售价')}</Typography.Text>
                        <Switch
                          size="small"
                          checked={showReadonlyOemPrice}
                          onChange={setShowReadonlyOemPrice}
                        />
                      </Space>
                    </Space>
                  </div>

                  {editingPresenceAvailable && (editingPresence.viewers.length > 0 || editingPresence.editors.length > 0) ? (
                    <Space size={6} wrap className="container-detail-editing-presence" aria-live="polite">
                      {editingPresence.viewers.length > 0 ? (
                        <Typography.Text type="secondary" title={getContainerDetailPresenceTitle(editingPresence.viewers)}>
                          {t('containers.text.otherViewers', '正在查看：{{names}}', { names: editingPresence.viewers.map((user) => formatContainerDetailPresenceUser(user)).join('、') })}
                        </Typography.Text>
                      ) : null}
                      {editingPresence.editors.length > 0 ? (
                        <Typography.Text type="warning" title={getContainerDetailPresenceTitle(editingPresence.editors)}>
                          {t('containers.text.otherEditors', '正在编辑：{{names}}', { names: editingPresence.editors.map((user) => formatContainerDetailPresenceUser(user)).join('、') })}
                        </Typography.Text>
                      ) : null}
                    </Space>
                  ) : null}

                  {access.canEditContainer ? (
                    <Space wrap size={[6, 6]} className="container-detail-bulk-row">
                      <Button
                        size="small"
                        icon={<SaveOutlined />}
                        loading={detailSaveSubmitting}
                        disabled={!pendingDetailPatchCount || detailSaveSubmitting}
                        onClick={() => void savePendingDetails()}
                      >
                        {t('containers.actions.saveDetails', '保存明细')}{pendingDetailPatchCount ? ` (${pendingDetailPatchCount})` : ''}
                      </Button>
                      <Tooltip title={t('containers.text.columnPasteTooltip', '把 Excel 一列数据按当前显示顺序填入目标列；也可在单元格内直接 Ctrl+V 粘贴多行')}>
                        <Button
                          size="small"
                          icon={<SnippetsOutlined />}
                          disabled={detailLoading || !displayRows.length}
                          onClick={openColumnPasteModal}
                        >
                          {t('containers.actions.pasteColumn', '粘贴列')}
                        </Button>
                      </Tooltip>
                      {autoSaveSnapshot.unsavedFieldCount > 0 ? (
                        <Space
                          size={4}
                          className="container-detail-auto-save-meta"
                          aria-live="polite"
                        >
                          <Typography.Text type={autoSaveSnapshot.failureCount > 0 ? 'danger' : 'secondary'}>
                            {autoSaveSnapshot.failureCount > 0
                              ? t(
                                  'containers.text.autoSaveFailedFields',
                                  '{{count}} 项未保存',
                                  { count: autoSaveSnapshot.failureCount },
                                )
                              : t(
                                  'containers.text.autoSavingFields',
                                  '正在保存 {{count}} 项',
                                  { count: autoSaveSnapshot.unsavedFieldCount },
                                )}
                          </Typography.Text>
                          {autoSaveSnapshot.failureCount > 0 ? (
                            <Button size="small" type="link" danger onClick={retryFailedAutoSaves}>
                              {t('containers.actions.retryAutoSave', '重试')}
                            </Button>
                          ) : null}
                        </Space>
                      ) : null}
                      {pendingDetailFieldCount > 0 ? (
                        <Space size={4} className="container-detail-draft-meta">
                          <Typography.Text type={Object.keys(pendingDetailFailures).length > 0 ? 'danger' : 'warning'}>
                            {t(
                              'containers.text.unsavedDetailFields',
                              '{{count}} 项未保存',
                              { count: pendingDetailFieldCount },
                            )}
                          </Typography.Text>
                          {isDetailDraftMemoryOnly ? (
                            <Typography.Text type="danger">
                              {t('containers.text.localDraftMemoryOnly', '仅当前页面内存保存，刷新或关闭页面会丢失')}
                            </Typography.Text>
                          ) : null}
                          <Button size="small" type="link" onClick={() => locateFirstPendingDetailField()}>
                            {t('containers.actions.locateUnsavedDetail', '定位未保存项')}
                          </Button>
                          <Button
                            size="small"
                            type="link"
                            danger
                            disabled={detailSaveSubmitting}
                            onClick={clearPendingDetailDraft}
                          >
                            {t('containers.actions.clearDraft', '清空草稿')}
                          </Button>
                        </Space>
                      ) : null}
                      {hasPendingConcurrencyConflicts ? (
                        <Button size="small" danger onClick={() => setConflictDrawerOpen(true)}>
                          {t('containers.actions.reviewConcurrencyConflicts', '并发冲突 {{count}} 项', { count: pendingDetailConflicts.length })}
                        </Button>
                      ) : null}
                    </Space>
                  ) : null}

                  <ContainerTagFilters
                    tagStatOptions={tagStatOptions}
                    tagStats={tagStats}
                    selectedTagFilters={selectedTagFilters}
                    selectedTagOptions={selectedTagOptions}
                    onToggleTagFilter={toggleTagFilter}
                    onSetTagFilters={setTagFiltersFromSelect}
                  />

                  {exporting ? (
                    <div className="container-detail-export-progress">
                      <Typography.Text type="secondary">{exportProgressMessage}</Typography.Text>
                      <Progress percent={exportProgress} size="small" />
                    </div>
                  ) : null}
                </div>
              </div>

              <div
                ref={setTableRegionElement}
                className="container-detail-table-region"
                onFocusCapture={() => setIsContainerDetailFieldFocused(true)}
                onBlurCapture={(event) => {
                  if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
                    setIsContainerDetailFieldFocused(false)
                  }
                }}
              >
                <DndContext sensors={columnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>
                  <SortableContext items={columnOrder} strategy={horizontalListSortingStrategy}>
                    <MeasuredTable metricId="warehouse.container-detail.table-1"
                      key={`${containerGuid}-${detailTableRenderKey}`}
                      ref={detailTableRef}
                      className="container-detail-table"
                      rowKey={rowKey}
                      rowClassName={(_, index) => index % 2 === 1 ? 'container-detail-row-striped' : ''}
                      size="small"
                      components={{ header: { cell: DraggableHeaderCell } }}
                      columns={columns}
                      dataSource={displayRows}
                      loading={detailLoading}
                      rowSelection={{
                        selectedRowKeys,
                        onChange: setSelectedRowKeys,
                        fixed: !viewport.isSmallPortrait,
                        // 紧凑表格中默认选择列过窄，显式留出复选框点击空间。
                        columnWidth: CONTAINER_DETAIL_SELECTION_COLUMN_WIDTH,
                      }}
                      pagination={detailLoadMode === 'paged' ? {
                        current: detailPageNumber,
                        pageSize: detailPageSize,
                        total: detailItemsTotal,
                        size: 'small',
                        showSizeChanger: true,
                        pageSizeOptions: [...CONTAINER_DETAIL_PAGE_SIZE_OPTIONS],
                        showTotal: (total) => t('containers.text.paginationTotal', '共 {{total}} 条', { total }),
                        position: ['bottomRight'],
                      } : false}
                      virtual
                      scroll={{ x: tableScrollX, y: tableScrollY }}
                      onChange={handleTableChange}
                      onScroll={handleDetailTableScroll}
                      footer={() => (
                        <Space direction="vertical" size={2}>
                          <Typography.Text type="secondary">{t('containers.formulas.transportCost', '运输成本 = 运费 × 明细体积 ÷ 装柜数量 ÷ 总体积')}</Typography.Text>
                          <Typography.Text type="secondary">{t('containers.formulas.importPrice', '进口价格 = ((国内价格 ÷ 汇率 + 运输成本) × 调整浮率 × 10) ÷ 11')}</Typography.Text>
                        </Space>
                      )}
                    />
                  </SortableContext>
                </DndContext>
              </div>
            </div>
          </Card>
        </Space>
      </Spin>
      <Modal
        title={t('containers.modals.columnPasteTitle', '粘贴 Excel 列数据')}
        open={columnPasteModalOpen}
        width={560}
        okText={t('containers.actions.applyColumnPaste', '填充')}
        cancelText={t('common.cancel')}
        okButtonProps={{ disabled: !columnPasteText.trim() }}
        onOk={submitColumnPasteModal}
        onCancel={closeColumnPasteModal}
        destroyOnHidden
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            {t('containers.modals.columnPasteHint', '从 Excel 复制一列后粘贴到下方。数据将按当前显示顺序从起始行向下逐行填充，空单元格保持原值；进口价格、零售价、英文名称需再点击“保存明细”落库。')}
          </Typography.Text>
          <Space wrap size={[12, 8]}>
            <Space size={6}>
              <Typography.Text>{t('containers.modals.columnPasteTargetColumn', '目标列')}</Typography.Text>
              <Select<ContainerDetailColumnPasteKey>
                size="small"
                style={{ width: 150 }}
                value={columnPasteColumnKey}
                onChange={(value) => {
                  if (isContainerDetailColumnPasteKey(value)) setColumnPasteColumnKey(value)
                }}
                options={CONTAINER_DETAIL_COLUMN_PASTE_KEYS.map((key) => ({
                  value: key,
                  label: t(`containers.fields.${key}`),
                }))}
              />
            </Space>
            <Space size={6}>
              <Typography.Text>{t('containers.modals.columnPasteStartRow', '起始行')}</Typography.Text>
              <Select<string>
                size="small"
                style={{ width: 240 }}
                showSearch
                optionFilterProp="label"
                value={columnPasteStartRowKey ?? columnPasteStartRowOptions[0]?.value}
                onChange={(value) => setColumnPasteStartRowKey(value)}
                options={columnPasteStartRowOptions}
              />
            </Space>
          </Space>
          <Input.TextArea
            value={columnPasteText}
            autoSize={{ minRows: 8, maxRows: 14 }}
            placeholder={t('containers.modals.columnPastePlaceholder', '在此粘贴 Excel 单列数据，每行一个值')}
            onChange={(event) => setColumnPasteText(event.target.value)}
          />
          <Typography.Text type="secondary">
            {t('containers.modals.columnPasteSummary', '共 {{count}} 行数据，当前列表可见 {{visible}} 行', {
              count: columnPasteParsedValueCount,
              visible: displayRows.length,
            })}
          </Typography.Text>
        </Space>
      </Modal>
      <Modal
        title={t('containers.setCode.pricesTitle', {
          item: setCodeModalRow ? getContainerDetailItemNumber(setCodeModalRow) ?? getContainerDetailProductCode(setCodeModalRow) ?? '' : '',
        })}
        open={setCodeModalOpen}
        width={680}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        okButtonProps={{
          disabled: !access.canEditContainer || changedSetCodePriceItems.length === 0,
          loading: setCodeSaving,
        }}
        onOk={() => void saveSetCodePrices()}
        onCancel={closeSetCodeModal}
        destroyOnHidden
      >
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          {setCodeModalRow ? (
            <Typography.Text type="secondary">
              {getContainerDetailProductName(setCodeModalRow) ?? '--'}
            </Typography.Text>
          ) : null}
          <MeasuredTable metricId="warehouse.container-detail.table-2"
            rowKey={getSetCodeRowKey}
            size="small"
            columns={setCodeColumns}
            dataSource={setCodeItems}
            loading={setCodeLoading}
            pagination={false}
            scroll={{ x: 520 }}
          />
        </Space>
      </Modal>
      <Drawer
        title={t('containers.modals.concurrencyConflictsTitle', '货柜明细并发冲突')}
        open={conflictDrawerOpen}
        onClose={() => setConflictDrawerOpen(false)}
        width={480}
      >
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          {pendingDetailConflicts.length > 1 ? (
            <Space wrap>
              <Button size="small" onClick={() => void acceptAllServerConflicts()}>
                {t('containers.actions.useAllServerValues', '批量采用服务器值')}
              </Button>
              <Button size="small" danger onClick={keepAllMineConflicts}>
                {t('containers.actions.keepAllMyValues', '批量保留我的值')}
              </Button>
            </Space>
          ) : null}
          {pendingDetailConflicts.map((conflict) => (
            <Card size="small" key={`${conflict.hguid}:${conflict.field}`}>
              <Space direction="vertical" size={6} style={{ width: '100%' }}>
                <Typography.Text strong>{conflict.field}</Typography.Text>
                <Typography.Text type="secondary">{t('containers.text.serverValue', '服务器值')}：{String(conflict.serverValue ?? '--')}</Typography.Text>
                <Typography.Text>{t('containers.text.myValue', '我的值')}：{String(conflict.submittedValue ?? '--')}</Typography.Text>
                <Space>
                  <Button size="small" onClick={() => void acceptServerConflict(conflict)}>{t('containers.actions.useServerValue', '采用服务器值')}</Button>
                  <Button size="small" danger onClick={() => keepMineConflict(conflict)}>{t('containers.actions.keepMyValue', '保留我的值')}</Button>
                </Space>
              </Space>
            </Card>
          ))}
        </Space>
      </Drawer>
      <WarehouseProductChangeHistoryDrawer
        open={Boolean(changeHistoryProduct)}
        productCode={changeHistoryProduct?.productCode}
        itemNumber={changeHistoryProduct?.itemNumber}
        productName={changeHistoryProduct?.productName}
        onClose={() => setChangeHistoryProduct(null)}
      />
      <Modal
        title={t('containers.modals.exportColumnsTitle', '选择导出列')}
        open={exportColumnModalOpen}
        okText={exportFormat === 'pdf' ? t('containers.actions.exportPdf', '导出 PDF') : t('containers.actions.exportExcel', '导出 Excel')}
        cancelText={t('common.cancel')}
        okButtonProps={{ disabled: selectedExportColumnKeys.length === 0, loading: exporting }}
        onOk={() => {
          setExportColumnModalOpen(false)
          void exportDetails(selectedExportColumnKeys, exportFormat)
        }}
        onCancel={() => setExportColumnModalOpen(false)}
      >
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Space size={8}>
            <Typography.Text type="secondary">{t('containers.actions.exportFormat', '导出格式')}</Typography.Text>
            <Radio.Group
              size="small"
              value={exportFormat}
              onChange={(event) => setExportFormatWithDefaults(event.target.value)}
              optionType="button"
              buttonStyle="solid"
              options={[
                { label: t('containers.actions.exportExcel', '导出 Excel'), value: 'excel' },
                { label: t('containers.actions.exportPdf', '导出 PDF'), value: 'pdf' },
              ]}
            />
          </Space>
          <Typography.Text type="secondary">
            {selectedRowKeys.length
              ? t('containers.text.exportSelectedRowsHint', '将导出已选择的 {{count}} 个商品。', { count: selectedRowKeys.length })
              : t('containers.text.exportAllRowsHint', '未选择商品时，将按当前筛选和排序导出全部匹配商品。')}
          </Typography.Text>
          <Space wrap>
            <Button size="small" onClick={() => setSelectedExportColumnKeys(CONTAINER_DETAIL_EXPORT_COLUMNS.map((column) => column.key))}>
              {t('containers.actions.selectAllExportColumns', '全选')}
            </Button>
            <Button size="small" onClick={() => setSelectedExportColumnKeys(
              exportFormat === 'pdf' ? DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMN_KEYS : DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS,
            )}>
              {t('containers.actions.resetDefaultExportColumns', '恢复默认')}
            </Button>
          </Space>
          <Checkbox.Group
            value={selectedExportColumnKeys}
            options={exportColumnOptions}
            onChange={(values) => setSelectedExportColumnKeys(values as ContainerDetailExportColumnKey[])}
          />
        </Space>
      </Modal>
      </div>
    </PageContainer>
  )
}
