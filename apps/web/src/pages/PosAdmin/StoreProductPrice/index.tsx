import {
  Button,
  Card,
  Checkbox,
  Form,
  Image,
  Input,
  InputNumber,
  message,
  Modal,
  Progress,
  Select,
  Space,
  Spin,
  Switch,
  Tag,
  Tooltip,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import BarcodePreview from '../../../components/BarcodePreview'
import PageContainer from '../../../components/PageContainer'
import { getActiveLocalSuppliers } from '../../../services/localSupplierService'
import {
  batchUpdateStoreRetailPrices,
  getStorePriceTransferJob,
  getStoreProductPriceGrid,
  startStorePriceTransferJob,
  syncToOtherStores,
} from '../../../services/storeProductPriceService'
import {
  createHqSyncJobPoller,
  HqProductSyncPollingCancelledError,
  HqProductSyncPollingTimeoutError,
} from '../../../services/productHqSyncPolling'
import { getActiveStores } from '../../../services/storeService'
import type {
  BatchUpdateStoreRetailPriceDto,
  CopyProgressDto,
  StoreProductPriceQueryDto,
  StorePriceTransferJobDto,
  StorePriceTransferRequest,
  StorePriceTransferResult,
  SyncToOtherStoresDto,
} from '../../../types/storeProductPrice'
import { CopyOutlined, PrinterOutlined, SwapOutlined } from '@ant-design/icons'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { discountRateToDecimal, formatDiscountRate } from '../../../utils/discountRate'
import { useAuthStore } from '../../../store/auth'
import { formatPaginationTotalText } from './pagination'
import { MeasuredTable } from '../../../components/MeasuredTable'

import type { PromoPosterProduct } from './promoPosterLogic'
import PromoPosterModal from './PromoPosterModal'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  buildStoreProductPriceRows,
  getSingleStoreCode,
  groupProductCodesByStore,
  isSameStoreSelection,
  resolveSelectAllState,
  SINGLE_STORE_SORT_FIELDS,
  type StoreProductPriceRow,
} from './multiStoreSelection'
import { createLatestRequestGate } from './latestRequestGate'
import multiStoreMessagesEn from './multiStoreMessages.en.json'
import multiStoreMessagesZh from './multiStoreMessages.zh.json'

// 多分店相关文案随本页代码块懒注册，不进首屏 i18n 包
registerPageMessages({ zh: multiStoreMessagesZh, en: multiStoreMessagesEn })

type DataType = StoreProductPriceRow
const PRICE_TRANSFER_POLL_TIMEOUT_MS = 45 * 60 * 1000

const productTypeMap: Record<number, { labelKey: string; color: string }> = {
  0: { labelKey: 'posAdmin.productPrice.normalProduct', color: 'default' },
  1: { labelKey: 'posAdmin.productPrice.weighProduct', color: 'blue' },
  2: { labelKey: 'posAdmin.productPrice.multiCodeProductType', color: 'purple' },
}

function isFormValidationError(error: unknown): error is { errorFields: unknown[] } {
  return (
    typeof error === 'object' &&
    error !== null &&
    Array.isArray((error as { errorFields?: unknown }).errorFields)
  )
}

function getPriceTransferErrors(job: StorePriceTransferJobDto) {
  return Array.from(new Set([...(job.errors ?? []), ...(job.result?.errors ?? [])]))
}

function getPriceTransferHandledCount(result?: StorePriceTransferResult) {
  return result ? result.totalProcessed + result.skippedCount : 0
}

function getPriceTransferProgressPercent(job: StorePriceTransferJobDto) {
  if (job.status === 'Succeeded' || job.status === 'Failed') return 100
  const totalCount = job.result?.totalCount ?? 0
  if (totalCount <= 0) return 0
  const percent = Math.floor((getPriceTransferHandledCount(job.result) / totalCount) * 100)
  return Math.max(0, Math.min(99, percent))
}

export default function StoreProductPricePage() {
  const { t } = useTranslation()
  const currentUser = useAuthStore((s) => s.currentUser)
  const access = useAuthStore((s) => s.access)
  const [searchForm] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<DataType[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [sortField, setSortField] = useState<string | undefined>()
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend' | undefined>()
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])

  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [supplierOptions, setSupplierOptions] = useState<{ label: string; value: string }[]>([])
  // 已生效的分店选择（驱动查询）；搜索栏里的多选框是草稿，下拉关闭后才提交到这里
  const [selectedStoreCodes, setSelectedStoreCodes] = useState<string[]>([])
  const storeSelectOpenRef = useRef(false)
  const draftStoreCodes: string[] | undefined = Form.useWatch('storeCodes', searchForm)

  const [batchModalOpen, setBatchModalOpen] = useState(false)
  const [batchForm] = Form.useForm()

  const [syncModalOpen, setSyncModalOpen] = useState(false)
  const [syncForm] = Form.useForm()

  const [copyModalOpen, setCopyModalOpen] = useState(false)
  const [copyForm] = Form.useForm()
  const [copyProgress, setCopyProgress] = useState<CopyProgressDto | null>(null)
  const [copying, setCopying] = useState(false)
  const eventSourceRef = useRef<EventSource | null>(null)

  const [priceTransferModalOpen, setPriceTransferModalOpen] = useState(false)
  const [priceTransferForm] = Form.useForm()
  const [priceTransferSubmitting, setPriceTransferSubmitting] = useState(false)
  const [priceTransferJob, setPriceTransferJob] = useState<StorePriceTransferJobDto | null>(null)
  const priceTransferPollerRef = useRef<{ stop: () => void } | null>(null)

  // 促销海报弹窗：打开时对「分店 + 选中商品」取快照，关闭动画结束后置空卸载
  const [promoPosterSession, setPromoPosterSession] = useState<{ storeCode: string; products: PromoPosterProduct[] } | null>(null)

  // 只采用最后一次查询的结果：多选分店、翻页等连续变化时，晚到的旧响应不会覆盖新结果
  const [loadGate] = useState(createLatestRequestGate)

  const stopPriceTransferPolling = useCallback(() => {
    priceTransferPollerRef.current?.stop()
    priceTransferPollerRef.current = null
  }, [])

  const loadData = useCallback(async () => {
    const seq = loadGate.begin()
    if (selectedStoreCodes.length === 0) {
      setData([])
      setTotal(0)
      setLoading(false)
      return
    }
    try {
      setLoading(true)
      const values = searchForm.getFieldsValue()
      const query: StoreProductPriceQueryDto = {
        // 单店时同时带 storeCode，兼容尚未支持 storeCodes 的后端
        storeCode: selectedStoreCodes.length === 1 ? selectedStoreCodes[0] : undefined,
        storeCodes: selectedStoreCodes,
        search: values.search || undefined,
        localSupplierCode: values.localSupplierCode || undefined,
        pageNumber: page,
        pageSize,
        sortBy: sortField,
        sortOrder: sortOrder === 'ascend' ? 'asc' : sortOrder === 'descend' ? 'desc' : undefined,
      }
      const result = await getStoreProductPriceGrid(query)
      if (!loadGate.isLatest(seq)) return
      setTotal(result?.total ?? 0)
      setData(buildStoreProductPriceRows(result?.items ?? [], selectedStoreCodes))
      setSelectedRowKeys([])
    } catch {
      if (!loadGate.isLatest(seq)) return
      message.error(t('posAdmin.productPrice.loadFailed', '加载商品价格列表失败'))
    } finally {
      if (loadGate.isLatest(seq)) setLoading(false)
    }
  }, [loadGate, selectedStoreCodes, page, pageSize, sortField, sortOrder, searchForm])

  useEffect(() => {
    ;(async () => {
      try {
        const managedCodes = access.managedStoreCodes()
        if (managedCodes === null) {
          const stores = await getActiveStores()
          setStoreOptions(stores)
        } else if (currentUser?.stores?.length) {
          const visible = currentUser.stores
            .filter((s) => managedCodes.includes(s.storeCode))
            .map((s) => ({ label: s.storeName || s.storeCode, value: s.storeCode }))
          setStoreOptions(visible)
        }
      } catch { /* ignore */ }
      try {
        const suppliers = await getActiveLocalSuppliers()
        setSupplierOptions(
          suppliers.map((s) => ({ label: s.name || s.localSupplierCode, value: s.localSupplierCode })),
        )
      } catch { /* ignore */ }
    })()
  }, [currentUser, access])

  // 提交分店选择：与当前生效的选择相同则不重新查询
  const commitStoreSelection = useCallback((codes: string[]) => {
    if (isSameStoreSelection(selectedStoreCodes, codes)) return
    setSelectedStoreCodes(codes)
    setPage(1)
    setSelectedRowKeys([])
    // 价格类排序只在单个分店下有意义，切到多分店时清掉，避免表头箭头与数据顺序不符
    if (codes.length > 1 && sortField && SINGLE_STORE_SORT_FIELDS.has(sortField)) {
      setSortField(undefined)
      setSortOrder(undefined)
    }
  }, [selectedStoreCodes, sortField])

  useEffect(() => {
    if (storeOptions.length === 1) {
      // 只有一个可见分店时自动选中
      const onlyStore = [storeOptions[0].value]
      if (!isSameStoreSelection(selectedStoreCodes, onlyStore)) {
        searchForm.setFieldsValue({ storeCodes: onlyStore })
        commitStoreSelection(onlyStore)
      }
      return
    }
    // 分店选项变化后剔除已不可见的分店
    const visibleCodes = selectedStoreCodes.filter((code) => storeOptions.some((s) => s.value === code))
    if (visibleCodes.length !== selectedStoreCodes.length) {
      searchForm.setFieldsValue({ storeCodes: visibleCodes })
      commitStoreSelection(visibleCodes)
    }
  }, [storeOptions, selectedStoreCodes, searchForm, commitStoreSelection])

  useEffect(() => {
    loadData()
  }, [loadData])

  useEffect(() => () => {
    stopPriceTransferPolling()
  }, [stopPriceTransferPolling])

  const onTableChange = (pagination: any, _filters: any, sorter: any) => {
    if (sorter?.field) {
      setSortField(String(sorter.field))
      setSortOrder(sorter.order)
    } else {
      setSortField(undefined)
      setSortOrder(undefined)
    }
    setPage(pagination.current)
    setPageSize(pagination.pageSize)
  }

  // 下拉展开时逐个勾选不触发查询，关闭下拉后一次性提交；下拉关闭时的删除标签、清空立即提交
  const handleStoreSelectChange = (codes: string[]) => {
    if (!storeSelectOpenRef.current) commitStoreSelection(codes)
  }

  const handleStoreSelectOpenChange = (open: boolean) => {
    storeSelectOpenRef.current = open
    if (!open) commitStoreSelection(searchForm.getFieldValue('storeCodes') ?? [])
  }

  const allStoreCodes = useMemo(() => storeOptions.map((option) => option.value), [storeOptions])
  const storeNameByCode = useMemo(() => new Map(storeOptions.map((option) => [option.value, option.label])), [storeOptions])
  const storeSelectAllState = resolveSelectAllState(draftStoreCodes ?? [], allStoreCodes)
  const isAllStoresDraft = storeSelectAllState.checked && allStoreCodes.length > 1
  const isMultiStore = selectedStoreCodes.length > 1
  const formatStoreName = useCallback((code: string) => storeNameByCode.get(code) ?? code, [storeNameByCode])
  // 选中行（列表每次加载都会清空选择，所以选中的都在当前页数据里）；多分店时可能跨多个分店
  const selectedRows = useMemo(() => {
    const keys = new Set(selectedRowKeys)
    return data.filter((row) => keys.has(row.key))
  }, [data, selectedRowKeys])
  const selectedRowsStoreCode = getSingleStoreCode(selectedRows)
  const selectedRowStoreNames = groupProductCodesByStore(selectedRows).map((group) => formatStoreName(group.storeCode))

  // 回到第 1 页再查询：不在第 1 页时由页码变化触发查询，避免先按旧页码多发一次请求
  const reloadFromFirstPage = () => {
    if (page !== 1) {
      setPage(1)
      return
    }
    loadData()
  }

  const handleSearch = () => {
    reloadFromFirstPage()
  }

  const handleReset = () => {
    searchForm.resetFields()
    // 重置只清查询条件，分店选择保留（与当前表格数据保持一致）
    searchForm.setFieldsValue({ storeCodes: selectedStoreCodes })
    reloadFromFirstPage()
  }

  const openBatchModal = () => {
    if (selectedRowKeys.length === 0) {
      message.warning(t('posAdmin.productPrice.selectProducts', '请先选择商品'))
      return
    }
    batchForm.resetFields()
    batchForm.setFieldsValue({
      updatePurchasePrice: false,
      updateRetailPrice: false,
      updateAutoPricing: false,
      updateSpecialProduct: false,
      updateDiscountRate: false,
    })
    setBatchModalOpen(true)
  }

  const handleBatchUpdate = async () => {
    // 选中行按分店分组，逐个分店调用批量更新（每个分店各自一次事务）
    const storeGroups = groupProductCodesByStore(selectedRows)
    let completedStores = 0
    try {
      const values = await batchForm.validateFields()
      if (!values.updatePurchasePrice && !values.updateRetailPrice && !values.updateAutoPricing && !values.updateSpecialProduct && !values.updateDiscountRate) {
        message.warning(t('posAdmin.productPrice.selectUpdateField', '请至少选择一项要更新的字段'))
        return
      }
      const dto: Omit<BatchUpdateStoreRetailPriceDto, 'productCodes' | 'storeCode'> = {}
      if (values.updatePurchasePrice && values.purchasePrice != null) {
        dto.purchasePrice = Number(values.purchasePrice)
      }
      if (values.updateRetailPrice && values.storeRetailPriceValue != null) {
        dto.storeRetailPriceValue = Number(values.storeRetailPriceValue)
      }
      if (values.updateAutoPricing) {
        dto.isAutoPricing = !!values.isAutoPricing
      }
      if (values.updateSpecialProduct) {
        dto.isSpecialProduct = !!values.isSpecialProduct
      }
      if (values.updateDiscountRate && values.discountRate != null) {
        dto.discountRate = discountRateToDecimal(Number(values.discountRate))
      }
      for (const group of storeGroups) {
        await batchUpdateStoreRetailPrices({ ...dto, productCodes: group.productCodes, storeCode: group.storeCode })
        completedStores += 1
      }
      message.success(t('posAdmin.productPrice.batchUpdateSuccess', '批量更新成功'))
      setBatchModalOpen(false)
      setSelectedRowKeys([])
      await loadData()
    } catch {
      if (completedStores > 0) {
        // 前面的分店已写入：说明进度并刷新表格，避免继续显示旧价格
        message.error(t('posAdmin.productPrice.multiStore.batchUpdatePartial', '已更新 {{done}}/{{total}} 个分店；分店 {{store}} 更新失败，其后的分店未更新', {
          done: completedStores,
          total: storeGroups.length,
          store: formatStoreName(storeGroups[completedStores].storeCode),
        }))
        setBatchModalOpen(false)
        await loadData()
        return
      }
      message.error(t('posAdmin.productPrice.batchUpdateFailed', '批量更新失败'))
    }
  }

  const openSyncModal = () => {
    if (selectedRowKeys.length === 0) {
      message.warning(t('posAdmin.productPrice.selectProducts', '请先选择商品'))
      return
    }
    if (!selectedRowsStoreCode) {
      message.warning(t('posAdmin.productPrice.multiStore.singleStoreOnly', '所选商品需来自同一个分店'))
      return
    }
    syncForm.resetFields()
    syncForm.setFieldsValue({
      syncPurchasePrice: true,
      syncRetailPrice: true,
      syncIsAutoPricing: false,
      syncIsSpecialProduct: false,
      syncDiscountRate: false,
      syncMode: 'Overwrite',
    })
    setSyncModalOpen(true)
  }

  const handleSyncToOtherStores = async () => {
    try {
      const values = await syncForm.validateFields()
      if (!values.targetStoreCodes || values.targetStoreCodes.length === 0) {
        message.warning(t('posAdmin.productPrice.selectTargetStore', '请选择目标分店'))
        return
      }
      const dto: SyncToOtherStoresDto = {
        productCodes: groupProductCodesByStore(selectedRows)[0]?.productCodes ?? [],
        sourceStoreCode: selectedRowsStoreCode!,
        targetStoreCodes: values.targetStoreCodes,
        syncPurchasePrice: !!values.syncPurchasePrice,
        syncRetailPrice: !!values.syncRetailPrice,
        syncIsAutoPricing: !!values.syncIsAutoPricing,
        syncIsSpecialProduct: !!values.syncIsSpecialProduct,
        syncDiscountRate: !!values.syncDiscountRate,
        syncMode: values.syncMode || 'Overwrite',
      }
      const count = await syncToOtherStores(dto)
      message.success(t('posAdmin.productPrice.syncComplete', '同步完成，影响 {{count}} 条记录', { count }))
      setSyncModalOpen(false)
      setSelectedRowKeys([])
      await loadData()
    } catch {
      message.error(t('posAdmin.productPrice.syncFailed', '同步失败'))
    }
  }

  const openCopyModal = () => {
    copyForm.resetFields()
    copyForm.setFieldsValue({
      mode: 'Overwrite',
      syncPurchasePrice: true,
      syncRetailPrice: true,
      syncIsAutoPricing: false,
      syncIsSpecialProduct: false,
      syncDiscountRate: false,
      syncMultiCode: false,
      syncMultiCodeRetailPrice: false,
    })
    setCopyProgress(null)
    setCopyModalOpen(true)
  }

  const openPromoPosterModal = () => {
    if (selectedRows.length === 0) {
      message.warning(t('posAdmin.productPrice.selectProducts', '请先选择商品'))
      return
    }
    // 海报按单个分店的价格生成，选中行必须来自同一个分店
    if (!selectedRowsStoreCode) {
      message.warning(t('posAdmin.productPrice.multiStore.singleStoreOnly', '所选商品需来自同一个分店'))
      return
    }
    // 列表每次加载都会清空选择，所以选中行都在当前页数据里，可直接取名称与货号
    setPromoPosterSession({
      storeCode: selectedRowsStoreCode,
      products: selectedRows
        .filter((row) => row.productCode)
        .map((row) => ({ productCode: row.productCode!, productName: row.productName, itemNumber: row.itemNumber })),
    })
  }

  const openPriceTransferModal = () => {
    stopPriceTransferPolling()
    priceTransferForm.resetFields()
    priceTransferForm.setFieldsValue({
      // HQ → 本地方向已于 2026-09-29 停用（后端返回 410），这里只保留本地 → HQ。
      direction: 'LocalToHq',
      sourceStoreCode: undefined,
      targetStoreCode: selectedStoreCodes.length === 1 ? selectedStoreCodes[0] : undefined,
      syncRetailPrices: true,
      syncMultiCodePrices: true,
      syncPurchasePrice: true,
      syncRetailPrice: true,
      syncDiscountRate: false,
      syncIsAutoPricing: false,
      syncIsSpecialProduct: false,
    })
    setPriceTransferJob(null)
    setPriceTransferSubmitting(false)
    setPriceTransferModalOpen(true)
  }

  const handlePriceTransferCancel = () => {
    stopPriceTransferPolling()
    setPriceTransferSubmitting(false)
    setPriceTransferModalOpen(false)
  }

  const handleStorePriceTransfer = async () => {
    if (priceTransferSubmitting) return
    let activePoller: { stop: () => void } | null = null

    try {
      const values = await priceTransferForm.validateFields()
      const hasSelectedTable = !!values.syncRetailPrices || !!values.syncMultiCodePrices
      const hasSelectedField = !!values.syncPurchasePrice || !!values.syncRetailPrice || !!values.syncDiscountRate || !!values.syncIsAutoPricing || !!values.syncIsSpecialProduct

      if (!hasSelectedTable) {
        message.warning(t('posAdmin.productPrice.selectSyncTable', '请至少选择一个同步表'))
        return
      }

      if (!hasSelectedField) {
        message.warning(t('posAdmin.productPrice.selectSyncField', '请至少选择一个同步字段'))
        return
      }

      const dto: StorePriceTransferRequest = {
        direction: values.direction,
        sourceStoreCode: values.sourceStoreCode,
        targetStoreCode: values.targetStoreCode,
        syncRetailPrices: !!values.syncRetailPrices,
        syncMultiCodePrices: !!values.syncMultiCodePrices,
        syncPurchasePrice: !!values.syncPurchasePrice,
        syncRetailPrice: !!values.syncRetailPrice,
        syncDiscountRate: !!values.syncDiscountRate,
        syncIsAutoPricing: !!values.syncIsAutoPricing,
        syncIsSpecialProduct: !!values.syncIsSpecialProduct,
      }

      setPriceTransferSubmitting(true)
      const job = await startStorePriceTransferJob(dto)
      setPriceTransferJob(job)
      if (job.isDuplicateRequest) {
        message.info(t('posAdmin.productPrice.priceTransferDuplicate', '目标分店同步任务正在执行，已切换到已有任务'))
      }

      const poller = createHqSyncJobPoller<StorePriceTransferJobDto>({
        jobId: job.jobId,
        getJob: async (jobId) => {
          const nextJob = await getStorePriceTransferJob(jobId)
          setPriceTransferJob(nextJob)
          return nextJob
        },
        timeoutMs: PRICE_TRANSFER_POLL_TIMEOUT_MS,
      })
      activePoller = poller
      priceTransferPollerRef.current = poller
      const completedJob = await poller.promise
      setPriceTransferJob(completedJob)

      if (completedJob.status === 'Failed') {
        const errorMessage = completedJob.message || completedJob.errors?.[0] || t('posAdmin.productPrice.priceTransferFailed', '分店价格同步失败')
        message.error(errorMessage)
        return
      }

      const totalProcessed = getPriceTransferHandledCount(completedJob.result)
      message.success(t('posAdmin.productPrice.priceTransferComplete', '分店价格同步完成，处理 {{count}} 条', { count: totalProcessed }))
      if (dto.targetStoreCode && selectedStoreCodes.includes(dto.targetStoreCode)) {
        await loadData()
      }
    } catch (error) {
      if (isFormValidationError(error)) return
      if (error instanceof HqProductSyncPollingCancelledError) return
      if (error instanceof HqProductSyncPollingTimeoutError) {
        message.error(t('posAdmin.productPrice.priceTransferTimeout', '分店价格同步任务轮询超时，任务可能仍在后台执行，请稍后刷新或重新查询'))
        return
      }
      message.error(error instanceof Error ? error.message : t('posAdmin.productPrice.priceTransferFailed', '分店价格同步失败'))
    } finally {
      if (priceTransferPollerRef.current === activePoller) {
        priceTransferPollerRef.current = null
      }
      setPriceTransferSubmitting(false)
    }
  }

  // 表头排序箭头受控于实际查询的排序字段（切到多分店清掉价格排序时箭头同步消失）
  const sortOrderFor = useCallback(
    (field: string) => (sortField === field ? sortOrder ?? null : null),
    [sortField, sortOrder],
  )

  const columns: ColumnsType<DataType> = useMemo(() => [
    {
      title: t('posAdmin.productPrice.rowIndex', '序号'),
      key: 'rowIndex',
      width: 42,
      align: 'right',
      render: (_: unknown, __: DataType, index: number) => index + 1,
    },
    {
      title: t('posAdmin.productPrice.productImage', '商品图片'),
      dataIndex: 'productImage',
      key: 'productImage',
      width: 50,
      align: 'center',
      render: (url: string) =>
        url ? (
          <Image className="store-product-price-image-cell" src={url} width={34} height={34} style={{ objectFit: 'cover', borderRadius: 4 }} />
        ) : (
          <div className="store-product-price-image-cell">
            {t('posAdmin.productPrice.noImage', '无图')}
          </div>
        ),
    },
    {
      title: t('posAdmin.productPrice.itemNumber', '货号'),
      dataIndex: 'itemNumber',
      key: 'itemNumber',
      width: 96,
      sorter: true,
      sortOrder: sortOrderFor('itemNumber'),
      render: (v: string) => <span className="store-product-price-code-cell">{v || '-'}</span>,
    },
    {
      title: t('posAdmin.productPrice.productCode', '商品代码'),
      dataIndex: 'productCode',
      key: 'productCode',
      width: 48,
      fixed: 'left',
      sorter: true,
      sortOrder: sortOrderFor('productCode'),
      render: (v: string) => (
        <Tooltip title={v}>
          <Button type="text" size="small" icon={<CopyOutlined />} onClick={() => void copyTextToClipboard(v)} />
        </Tooltip>
      ),
    },
    {
      title: t('posAdmin.productPrice.productName', '商品名称'),
      dataIndex: 'productName',
      key: 'productName',
      width: 180,
      sorter: true,
      sortOrder: sortOrderFor('productName'),
      render: (v: string) => (
        <div
          className="store-product-price-name-cell"
          title={v}
        >
          {v}
        </div>
      ),
    },
    
    {
      title: t('posAdmin.productPrice.barcode', '条码'),
      dataIndex: 'barcode',
      key: 'barcode',
      width: 132,
      render: (v: string) => (
        <div className="store-product-price-barcode-cell">
          <BarcodePreview
            value={v}
            compactCopy
            align="left"
            className="store-product-price-barcode-preview"
            gap={2}
            options={{ height: 22, width: 1, margin: 0 }}
            textMaxWidth={104}
            textNoWrap
          />
        </div>
      ),
    },
    {
      title: t('posAdmin.productPrice.supplier', '供应商'),
      dataIndex: 'localSupplierName',
      key: 'localSupplierName',
      width: 110,
      render: (v: string) => <div className="store-product-price-supplier-cell" title={v}>{v || '-'}</div>,
    },
    {
      title: t('posAdmin.productPrice.productType', '商品类型'),
      dataIndex: 'productType',
      key: 'productType',
      width: 76,
      align: 'center',
      render: (v: number) => {
        const info = productTypeMap[v] || { labelKey: String(v), color: 'default' }
        return <Tag color={info.color}>{t(info.labelKey)}</Tag>
      },
    },
   
    // 多分店时同一商品按分店展开成多行，需要「分店」列区分
    ...(isMultiStore ? [{
      title: t('posAdmin.productPrice.store', '分店'),
      dataIndex: 'storeCode',
      key: 'storeCode',
      width: 110,
      render: (code: string) => <div className="store-product-price-supplier-cell" title={formatStoreName(code)}>{formatStoreName(code)}</div>,
    }] : []),
    {
      title: t('posAdmin.productPrice.purchasePrice', '采购价'),
      dataIndex: 'storePurchasePrice',
      key: 'storePurchasePrice',
      width: 72,
      align: 'right',
      sorter: !isMultiStore,
      sortOrder: sortOrderFor('storePurchasePrice'),
      render: (v: number) => <span className="store-product-price-numeric-cell">{v != null ? v.toFixed(2) : '-'}</span>,
    },
    {
      title: t('posAdmin.productPrice.retailPrice', '零售价'),
      dataIndex: 'storeRetailPrice',
      key: 'storeRetailPrice',
      width: 72,
      align: 'right',
      sorter: !isMultiStore,
      sortOrder: sortOrderFor('storeRetailPrice'),
      render: (v: number) => <span className="store-product-price-numeric-cell">{v != null ? v.toFixed(2) : '-'}</span>,
    },
    {
      title: t('posAdmin.productPrice.autoPricing', '自动定价'),
      dataIndex: 'isStoreAutoPricing',
      key: 'isStoreAutoPricing',
      width: 70,
      align: 'center',
      render: (v: boolean) => <Tag color={v ? 'green' : 'default'}>{v ? t('posAdmin.invoiceDetail.yes', '是') : t('posAdmin.invoiceDetail.no', '否')}</Tag>,
    },
    {
      title: t('posAdmin.productPrice.specialProduct', '特殊商品'),
      dataIndex: 'isStoreSpecialProduct',
      key: 'isStoreSpecialProduct',
      width: 70,
      align: 'center',
      render: (v: boolean) => <Tag color={v ? 'orange' : 'default'}>{v ? t('posAdmin.invoiceDetail.yes', '是') : t('posAdmin.invoiceDetail.no', '否')}</Tag>,
    },
    {
      title: t('posAdmin.productPrice.discountRate', '折扣率'),
      dataIndex: 'discountRate',
      key: 'discountRate',
      width: 72,
      align: 'right',
      sorter: !isMultiStore,
      sortOrder: sortOrderFor('discountRate'),
      render: (v: number) => <span className="store-product-price-numeric-cell">{formatDiscountRate(v)}</span>,
    },
    {
      title: t('posAdmin.productPrice.enabled', '启用'),
      dataIndex: 'isActive',
      key: 'isActive',
      width: 56,
      align: 'center',
      render: (v: boolean) => <Tag color={v ? 'success' : 'error'}>{v ? t('posAdmin.invoiceDetail.yes', '是') : t('posAdmin.invoiceDetail.no', '否')}</Tag>,
    },
    {
      title: t('posAdmin.productPrice.updatedAt', '更新时间'),
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      width: 92,
      sorter: true,
      sortOrder: sortOrderFor('updatedAt'),
      render: (v: string) => v ? (
        <span className="store-product-price-date-cell">
          <span>{dayjs(v).format('YYYY-MM-DD')}</span>
          <span>{dayjs(v).format('HH:mm')}</span>
        </span>
      ) : '-',
    },
    {
      title: t('posAdmin.productPrice.updatedBy', '更新人'),
      dataIndex: 'updatedBy',
      key: 'updatedBy',
      width: 80,
      render: (v: string) => <span className="store-product-price-code-cell">{v || '-'}</span>,
    },
  ], [t, isMultiStore, formatStoreName, sortOrderFor])

  const selectedCount = selectedRowKeys.length
  const priceTransferErrors = priceTransferJob ? getPriceTransferErrors(priceTransferJob) : []

  return (
    <PageContainer
      title={t('posAdmin.productPrice.title', '分店商品价格管理')}
      subtitle={t('posAdmin.productPrice.subtitle', '管理各分店的商品采购价、零售价、自动定价及折扣率')}
    >
      <Card>
        <Form form={searchForm} layout="inline" style={{ marginBottom: 16 }} onFinish={handleSearch}>
          <Form.Item name="storeCodes" label={t('posAdmin.productPrice.store', '分店')}>
            <Select
              mode="multiple"
              showSearch
              optionFilterProp="label"
              options={storeOptions}
              placeholder={t('posAdmin.productPrice.multiStore.selectStores', '请选择分店（可多选）')}
              style={{ width: 300 }}
              allowClear
              // 全选时收成一个「全部分店」标签，避免挤满搜索栏
              maxTagCount={isAllStoresDraft ? 0 : 'responsive'}
              maxTagPlaceholder={(omitted) => isAllStoresDraft
                ? t('posAdmin.productPrice.multiStore.allStores', '全部分店（{{count}} 个）', { count: allStoreCodes.length })
                : `+${omitted.length}`}
              onChange={handleStoreSelectChange}
              onOpenChange={handleStoreSelectOpenChange}
              popupRender={(menu) => (
                <>
                  {/* 阻止 mousedown 夺走焦点，勾选全选时下拉保持展开 */}
                  <div style={{ padding: '4px 12px 6px', borderBottom: '1px solid #f0f0f0' }} onMouseDown={(event) => event.preventDefault()}>
                    <Checkbox
                      checked={storeSelectAllState.checked}
                      indeterminate={storeSelectAllState.indeterminate}
                      disabled={allStoreCodes.length === 0}
                      onChange={(event) => searchForm.setFieldValue('storeCodes', event.target.checked ? allStoreCodes : [])}
                    >
                      {t('posAdmin.productPrice.multiStore.allStores', '全部分店（{{count}} 个）', { count: allStoreCodes.length })}
                    </Checkbox>
                  </div>
                  {menu}
                </>
              )}
            />
          </Form.Item>
          <Form.Item name="localSupplierCode" label={t('posAdmin.productPrice.supplier', '供应商')}>
            <Select
              showSearch
              optionFilterProp="label"
              options={supplierOptions}
              placeholder={t('posAdmin.productPrice.allSuppliers', '全部供应商')}
              style={{ width: 220 }}
              allowClear
            />
          </Form.Item>
          <Form.Item name="search" label={t('common.query', '查询')}>
            <Input allowClear placeholder={t('posAdmin.productPrice.searchPlaceholder', '商品代码/名称/货号/条码')} style={{ width: 240 }} />
          </Form.Item>
          <Form.Item>
            <Space>
              <Button type="primary" htmlType="submit" disabled={selectedStoreCodes.length === 0}>
                {t('common.query', '查询')}
              </Button>
              <Button onClick={handleReset}>{t('common.reset', '重置')}</Button>
            </Space>
          </Form.Item>
        </Form>

        <div style={{ marginBottom: 12 }}>
          <Space wrap>
            {access.canEditStoreProducts && (
              <>
                <Button type="primary" disabled={selectedCount === 0} onClick={openBatchModal}>
                  {t('posAdmin.productPrice.batchUpdate', '批量更新')} {selectedCount > 0 ? `(${selectedCount})` : ''}
                </Button>
                <Button disabled={selectedCount === 0} onClick={openSyncModal}>
                  {t('posAdmin.productPrice.syncToStores', '同步到其他分店')} {selectedCount > 0 ? `(${selectedCount})` : ''}
                </Button>
              </>
            )}
            {/* 海报只读商品与价格，不要求编辑权限；门店范围由后端校验 */}
            <Button icon={<PrinterOutlined />} disabled={selectedCount === 0} onClick={openPromoPosterModal}>
              {t('posAdmin.productPrice.promoPoster.button', '打印促销海报')} {selectedCount > 0 ? `(${selectedCount})` : ''}
            </Button>
            {access.isAdmin && (
              <Button onClick={openCopyModal}>{t('posAdmin.productPrice.copyStoreData', '复制分店数据')}</Button>
            )}
            {access.isAdmin && (
              <Button icon={<SwapOutlined />} onClick={openPriceTransferModal}>
                {t('posAdmin.productPrice.priceTransfer', 'HQ/本地价格同步')}
              </Button>
            )}
          </Space>
        </div>

        <MeasuredTable metricId="pos-admin.store-product-price.table-1"
          className="store-product-price-compact-table"
          rowKey="key"
          loading={loading}
          dataSource={data}
          columns={columns}
          scroll={{ x: isMultiStore ? 1630 : 1520, y: 600 }}
          virtual
          rowSelection={{
            selectedRowKeys,
            onChange: (keys) => setSelectedRowKeys(keys),
            columnWidth: 40,
          }}
          pagination={{
            total,
            current: page,
            pageSize,
            showSizeChanger: true,
            pageSizeOptions: ['10', '20', '50', '100', '200'],
            showTotal: (value) => formatPaginationTotalText(value, pageSize, t),
          }}
          onChange={onTableChange}
          size="small"
        />
      </Card>

      <Modal
        open={batchModalOpen}
        title={t('posAdmin.productPrice.batchUpdateTitle', '批量更新价格')}
        onCancel={() => setBatchModalOpen(false)}
        onOk={handleBatchUpdate}
        width={600}
        forceRender
      >
        <p style={{ marginBottom: 16, color: '#666' }}>
          {selectedRowStoreNames.length > 1
            ? t('posAdmin.productPrice.multiStore.selectedRowsStores', '已选择 {{count}} 行，涉及 {{storeCount}} 个分店：{{stores}}', {
              count: selectedCount,
              storeCount: selectedRowStoreNames.length,
              stores: selectedRowStoreNames.join('、'),
            })
            : t('posAdmin.productPrice.selectedProductsStore', '已选择 {{count}} 个商品，分店：{{storeCode}}', { count: selectedCount, storeCode: selectedRowStoreNames[0] })}
        </p>
        <Form form={batchForm} layout="vertical">
          <Form.Item name="updatePurchasePrice" valuePropName="checked" label={t('posAdmin.productPrice.updatePurchasePriceLabel', '更新采购价')}>
            <Checkbox>{t('posAdmin.productPrice.enableUpdate', '启用')}</Checkbox>
          </Form.Item>
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.updatePurchasePrice !== cur.updatePurchasePrice}>
            {({ getFieldValue }) =>
              getFieldValue('updatePurchasePrice') ? (
                <Form.Item name="purchasePrice" label={t('posAdmin.productPrice.purchasePrice', '采购价')} rules={[{ required: true, type: 'number', min: 0 }]}>
                  <InputNumber min={0} precision={2} style={{ width: '100%' }} />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateRetailPrice" valuePropName="checked" label={t('posAdmin.productPrice.updateRetailPriceLabel', '更新零售价')}>
            <Checkbox>{t('posAdmin.productPrice.enableUpdate', '启用')}</Checkbox>
          </Form.Item>
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.updateRetailPrice !== cur.updateRetailPrice}>
            {({ getFieldValue }) =>
              getFieldValue('updateRetailPrice') ? (
                <Form.Item name="storeRetailPriceValue" label={t('posAdmin.productPrice.retailPrice', '零售价')} rules={[{ required: true, type: 'number', min: 0 }]}>
                  <InputNumber min={0} precision={2} style={{ width: '100%' }} />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateAutoPricing" valuePropName="checked" label={t('posAdmin.productPrice.updateAutoPricingLabel', '更新自动定价')}>
            <Checkbox>{t('posAdmin.productPrice.enableUpdate', '启用')}</Checkbox>
          </Form.Item>
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.updateAutoPricing !== cur.updateAutoPricing}>
            {({ getFieldValue }) =>
              getFieldValue('updateAutoPricing') ? (
                <Form.Item name="isAutoPricing" label={t('posAdmin.productPrice.autoPricing', '自动定价')} valuePropName="checked">
                  <Switch checkedChildren={t('posAdmin.invoiceDetail.yes', '是')} unCheckedChildren={t('posAdmin.invoiceDetail.no', '否')} />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateSpecialProduct" valuePropName="checked" label={t('posAdmin.productPrice.updateSpecialLabel', '更新特殊商品')}>
            <Checkbox>{t('posAdmin.productPrice.enableUpdate', '启用')}</Checkbox>
          </Form.Item>
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.updateSpecialProduct !== cur.updateSpecialProduct}>
            {({ getFieldValue }) =>
              getFieldValue('updateSpecialProduct') ? (
                <Form.Item name="isSpecialProduct" label={t('posAdmin.productPrice.specialProduct', '特殊商品')} valuePropName="checked">
                  <Switch checkedChildren={t('posAdmin.invoiceDetail.yes', '是')} unCheckedChildren={t('posAdmin.invoiceDetail.no', '否')} />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateDiscountRate" valuePropName="checked" label={t('posAdmin.productPrice.updateDiscountLabel', '更新折扣率')}>
            <Checkbox>{t('posAdmin.productPrice.enableUpdate', '启用')}</Checkbox>
          </Form.Item>
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.updateDiscountRate !== cur.updateDiscountRate}>
            {({ getFieldValue }) =>
              getFieldValue('updateDiscountRate') ? (
                <Form.Item name="discountRate" label={t('posAdmin.productPrice.discountRate', '折扣率')} rules={[{ required: true, type: 'number', min: 0, max: 100 }]}>
                  <InputNumber min={0} max={100} step={1} precision={1} addonAfter="%" style={{ width: '100%' }} />
                </Form.Item>
              ) : null
            }
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        open={syncModalOpen}
        title={t('posAdmin.productPrice.syncToStoresTitle', '同步到其他分店')}
        onCancel={() => setSyncModalOpen(false)}
        onOk={handleSyncToOtherStores}
        width={650}
        forceRender
      >
        <p style={{ marginBottom: 16, color: '#666' }}>
          {t('posAdmin.productPrice.selectedSourceStore', '已选择 {{count}} 个商品，源分店：{{storeCode}}', { count: selectedCount, storeCode: selectedRowsStoreCode ? formatStoreName(selectedRowsStoreCode) : '' })}
        </p>
        <Form form={syncForm} layout="vertical">
          <Form.Item name="targetStoreCodes" label={t('posAdmin.productPrice.targetStore', '目标分店')} rules={[{ required: true }]}>
            <Select
              mode="multiple"
              showSearch
              optionFilterProp="label"
              options={storeOptions.filter((s) => s.value !== selectedRowsStoreCode)}
              placeholder={t('posAdmin.productPrice.selectTargetStore', '请选择目标分店')}
            />
          </Form.Item>
          <Form.Item name="syncMode" label={t('posAdmin.productPrice.syncMode', '同步模式')} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'Overwrite', label: t('posAdmin.productPrice.overwrite', '覆盖') },
                { value: 'OnlyUpdateNull', label: t('posAdmin.productPrice.onlyUpdateNull', '仅更新空值') },
              ]}
            />
          </Form.Item>
          <Form.Item label={t('posAdmin.productPrice.syncFields', '同步字段')}>
            <Space wrap>
              <Form.Item name="syncPurchasePrice" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.purchasePrice', '采购价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncRetailPrice" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.retailPrice', '零售价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncIsAutoPricing" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.autoPricing', '自动定价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncIsSpecialProduct" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.specialProduct', '特殊商品')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncDiscountRate" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.discountRate', '折扣率')}</Checkbox>
              </Form.Item>
            </Space>
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        open={copyModalOpen}
        title={t('posAdmin.productPrice.copyTitle', '复制分店数据')}
        onCancel={() => {
          if (eventSourceRef.current) {
            eventSourceRef.current.close()
            eventSourceRef.current = null
          }
          setCopying(false)
          setCopyModalOpen(false)
        }}
        footer={copying ? null : undefined}
        width={650}
        forceRender
      >
        <Form form={copyForm} layout="vertical">
          <Form.Item name="sourceStoreCode" label={t('posAdmin.productPrice.sourceStore', '源分店')} rules={[{ required: true }]}>
            <Select showSearch optionFilterProp="label" options={storeOptions} placeholder={t('posAdmin.productPrice.selectSourceStore', '请选择源分店')} />
          </Form.Item>
          <Form.Item name="targetStoreCodes" label={t('posAdmin.productPrice.targetStore', '目标分店')} rules={[{ required: true }]}>
            <Select
              mode="multiple"
              showSearch
              optionFilterProp="label"
              options={storeOptions}
              placeholder={t('posAdmin.productPrice.selectTargetStore', '请选择目标分店')}
            />
          </Form.Item>
          <Form.Item name="mode" label={t('posAdmin.productPrice.copyMode', '复制模式')} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'Overwrite', label: t('posAdmin.productPrice.overwrite', '覆盖') },
                { value: 'OnlyUpdateNull', label: t('posAdmin.productPrice.onlyUpdateNull', '仅更新空值') },
              ]}
            />
          </Form.Item>
          <Form.Item label={t('posAdmin.productPrice.syncFields', '同步字段')}>
            <Space wrap>
              <Form.Item name="syncPurchasePrice" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.purchasePrice', '采购价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncRetailPrice" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.retailPrice', '零售价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncIsAutoPricing" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.autoPricing', '自动定价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncIsSpecialProduct" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.specialProduct', '特殊商品')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncDiscountRate" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.discountRate', '折扣率')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncMultiCode" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.multiCodeProduct', '多码商品')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncMultiCodeRetailPrice" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.multiCodeRetailPrice', '多码零售价')}</Checkbox>
              </Form.Item>
            </Space>
          </Form.Item>
        </Form>

        {copying && copyProgress && (
          <Card size="small" style={{ marginTop: 16 }}>
            <Space direction="vertical" style={{ width: '100%' }}>
              <div>
                <Spin size="small" /> {copyProgress.message}
              </div>
              {copyProgress.totalStores > 0 && (
                <Progress
                  percent={Math.round((copyProgress.storeIndex / copyProgress.totalStores) * 100)}
                  status="active"
                />
              )}
              <div style={{ color: '#666', fontSize: 12 }}>
                {t('posAdmin.productPrice.copyProgress', '零售价已复制：{{retailCount}} | 多码已复制：{{multiCodeCount}}', { retailCount: copyProgress.retailPriceCopied, multiCodeCount: copyProgress.multiCodeCopied })}
              </div>
            </Space>
          </Card>
        )}
      </Modal>

      <Modal
        open={priceTransferModalOpen}
        title={t('posAdmin.productPrice.priceTransferTitle', 'HQ/本地价格同步')}
        onCancel={handlePriceTransferCancel}
        onOk={handleStorePriceTransfer}
        width={650}
        confirmLoading={priceTransferSubmitting}
        forceRender
      >
        <Form form={priceTransferForm} layout="vertical" disabled={priceTransferSubmitting}>
          <Form.Item name="direction" label={t('posAdmin.productPrice.transferDirection', '同步方向')} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'LocalToHq', label: t('posAdmin.productPrice.localToHq', '本地 -> HQ') },
              ]}
            />
          </Form.Item>
          <Form.Item name="sourceStoreCode" label={t('posAdmin.productPrice.sourceStore', '源分店')} rules={[{ required: true, message: t('posAdmin.productPrice.selectSourceStore', '请选择源分店') }]}>
            <Select
              showSearch
              optionFilterProp="label"
              options={storeOptions}
              placeholder={t('posAdmin.productPrice.selectSourceStore', '请选择源分店')}
            />
          </Form.Item>
          <Form.Item name="targetStoreCode" label={t('posAdmin.productPrice.targetStore', '目标分店')} rules={[{ required: true, message: t('posAdmin.productPrice.selectTargetStore', '请选择目标分店') }]}>
            <Select
              showSearch
              optionFilterProp="label"
              options={storeOptions}
              placeholder={t('posAdmin.productPrice.selectTargetStore', '请选择目标分店')}
            />
          </Form.Item>
          <Form.Item label={t('posAdmin.productPrice.syncTables', '同步表')}>
            <Space wrap>
              <Form.Item name="syncRetailPrices" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.storeRetailPriceTable', '分店零售价表')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncMultiCodePrices" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.storeMultiCodePriceTable', '分店多码表')}</Checkbox>
              </Form.Item>
            </Space>
          </Form.Item>
          <Form.Item label={t('posAdmin.productPrice.syncFields', '同步字段')}>
            <Space wrap>
              <Form.Item name="syncPurchasePrice" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.purchasePrice', '采购价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncRetailPrice" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.retailPrice', '零售价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncDiscountRate" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.discountRate', '折扣率')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncIsAutoPricing" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.autoPricing', '自动定价')}</Checkbox>
              </Form.Item>
              <Form.Item name="syncIsSpecialProduct" valuePropName="checked" noStyle>
                <Checkbox>{t('posAdmin.productPrice.specialProduct', '特殊商品')}</Checkbox>
              </Form.Item>
            </Space>
          </Form.Item>
        </Form>

        {priceTransferJob && (
          <Card size="small" style={{ marginTop: 16 }}>
            <Space direction="vertical" style={{ width: '100%' }}>
              <Space>
                <Tag color={priceTransferJob.status === 'Succeeded' ? 'success' : priceTransferJob.status === 'Failed' ? 'error' : 'processing'}>
                  {priceTransferJob.status}
                </Tag>
                <span>{priceTransferJob.message || t('posAdmin.productPrice.priceTransferRunning', '分店价格同步任务处理中')}</span>
              </Space>
              <Progress
                percent={getPriceTransferProgressPercent(priceTransferJob)}
                status={priceTransferJob.status === 'Failed' ? 'exception' : priceTransferJob.status === 'Succeeded' ? 'success' : 'active'}
              />
              {priceTransferJob.result && (
                <div style={{ color: '#666', fontSize: 12 }}>
                  <div>
                    {t('posAdmin.productPrice.processedProgress', '已处理')}：
                    {getPriceTransferHandledCount(priceTransferJob.result)}
                    {priceTransferJob.result.totalCount > 0 ? ` / ${priceTransferJob.result.totalCount}` : ''}，
                    {t('posAdmin.productPrice.added', '新增')}：{priceTransferJob.result.insertedCount}，
                    {t('posAdmin.productPrice.updated', '更新')}：{priceTransferJob.result.updatedCount}，
                    {t('posAdmin.productPrice.skipped', '跳过')}：{priceTransferJob.result.skippedCount}
                  </div>
                  <div>
                    {t('posAdmin.productPrice.storeRetailPriceTable', '分店零售价表')}：
                    {priceTransferJob.result.retailPriceInserted}/{priceTransferJob.result.retailPriceUpdated}/{priceTransferJob.result.retailPriceSkipped}
                  </div>
                  <div>
                    {t('posAdmin.productPrice.storeMultiCodePriceTable', '分店多码表')}：
                    {priceTransferJob.result.multiCodeInserted}/{priceTransferJob.result.multiCodeUpdated}/{priceTransferJob.result.multiCodeSkipped}
                  </div>
                </div>
              )}
              {priceTransferErrors.length > 0 ? (
                <div style={{ color: '#cf1322', fontSize: 12 }}>
                  {priceTransferErrors.map((error, index) => (
                    <div key={`${error}-${index}`}>{error}</div>
                  ))}
                </div>
              ) : null}
            </Space>
          </Card>
        )}
      </Modal>

      {promoPosterSession && (
        <PromoPosterModal
          storeCode={promoPosterSession.storeCode}
          products={promoPosterSession.products}
          onClose={() => setPromoPosterSession(null)}
        />
      )}
    </PageContainer>
  )
}
