import { DeleteOutlined, EditOutlined, EllipsisOutlined, LoadingOutlined, PlusOutlined, ReloadOutlined, SendOutlined, TranslationOutlined, UploadOutlined, WarningOutlined } from '@ant-design/icons'
import { Button, Checkbox, Dropdown, Image, message, Modal, Select, Space, Tooltip, Typography } from 'antd'
import type { MenuProps } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import { assignProductsToContainer, checkContainerConflicts, getContainerList } from '../../../services/containerService'
import { batchDetectProducts, batchImportConfirm, batchUpdateDomesticProducts, fixProductImage, sendToHq, syncToHBSales, updateHbwebProductNames } from '../../../services/domesticProductImportService'
import { batchTranslate } from '../../../services/translationService'
import type { ContainerMain } from '../../../types/container'
import { isValidEAN13 } from '../../../utils/barcode'
import { createLatestRequestGuard } from '../../../utils/latestRequestGuard'
import type { ProductImportItem, DuplicateGroup } from './types'
import { buildAssignContainerItems, buildHbwebProductNameSyncNotificationDecision, buildHbwebProductNameUpdates, calculateStatistics, createEmptyProduct, detectDuplicates, findInvalidAssignContainerItems, generateImageUrl, getHbwebProductNameSyncConfirmationKeys, mergeDuplicateProducts, parseProductImportPasteText, stripAssignContainerItemsForRequest, summarizeAssignProductsResult, summarizeHbwebProductNameSyncResponse, updateCalculatedFields } from './utils'
import { buildConflictRows, buildDuplicateRowIndex, collectImportErrors, computeImportSteps, countImportRows, getChangedImportFields, getDuplicatePartnerRowNumbers, getImportCellDiff, getImportRowClassName, getImportRowErrors, isBlankImportRow, isImportDiffField, matchesImportFilter, mergeTranslationsById, resolveImportRowKind, snapshotTranslationTargets, splitProductsByResolution } from './importGridLogic'
import type { ConflictSelection, ContainerConflictRow, ImportDiffField, ImportStatusFilter } from './importGridLogic'
import { applyImportPaste, clearImportColumn, findRowIndexById, toEditableColumn } from './importPasteLogic'
import type { ImportEditableColumn } from './importPasteLogic'
import { ConflictResolutionDialog } from './ConflictResolutionDialog'
import { DuplicateDialog } from './DuplicateDialog'
import { ImportCell } from './ImportCell'
import { ImportStatusChips } from './ImportStatusChips'
import { ImportStepper } from './ImportStepper'
import './productImport.css'
import productImportMessagesEn from './productImportMessages.en.json'
import productImportMessagesZh from './productImportMessages.zh.json'
import { MeasuredTable } from '../../../components/MeasuredTable'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: productImportMessagesZh, en: productImportMessagesEn })

// 表格最小宽度：固定列宽合计 808（检测后：勾选 52 + 件数 48 + 图 36 + 货号 104 + 条码 126 + 国内价 78 + 零售价 64 + 中包 52 + 装箱 52 + 体积 68 + 状态 126）+ 商品名称 / 英文名称两个自适应列各至少约 80。
// 货号、条码、国内价、状态这几列必须完整显示（导入审核要逐个核对标识符与新旧值；宽度按实测溢出量加出来的），
// 名称类自由文本列在窄屏用省略号。1280 视口 + 展开侧栏（248）时内容区约 983px，Windows 经典滚动条再占约 15px → 968，
// 所以最小宽度取 968，不会出现横向滚动条；更宽的屏幕由两个自适应列吃掉余量。
const PRODUCT_IMPORT_TABLE_SCROLL_X = 968
const BLANK_IMAGE = 'data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs='

/** 页面里同一时刻只允许一个会改数据或发请求的动作，用来给按钮 loading 并挡住重复点击。 */
type BusyAction = 'detect' | 'translate' | 'create' | 'update' | 'names' | 'send' | 'hbsales' | 'hq'

interface ImportState {
  supplier: string | null
  products: ProductImportItem[]
  selectedIds: string[]
  /** 检测之后又改了数据：需要重新检测，「更新 / 新建」在此之前保持禁用。 */
  needsDetection: boolean
}

function createInitialState(supplier: string | null): ImportState {
  return { supplier, products: [createEmptyProduct()], selectedIds: [], needsDetection: false }
}

/** 把一次或多次发送货柜的结果合并成一个汇总（纯函数，放在组件外）。 */
function mergeAssignSummaries(summaries: Array<ReturnType<typeof summarizeAssignProductsResult>>) {
  const created = summaries.reduce((sum, item) => sum + item.created, 0)
  const updated = summaries.reduce((sum, item) => sum + item.updated, 0)
  const succeeded = summaries.reduce((sum, item) => sum + item.succeeded, 0)
  const failedCount = summaries.reduce((sum, item) => sum + item.failedCount, 0)
  const failed = summaries.flatMap((item) => item.failed)
  const messageText = summaries.map((item) => item.message).filter(Boolean).join('; ')

  if (summaries.some((item) => item.status === 'apiError')) {
    return { status: 'apiError' as const, created, updated, succeeded, failedCount, failed, message: messageText }
  }
  if (failedCount > 0 && succeeded === 0) {
    return { status: 'failed' as const, created, updated, succeeded, failedCount, failed, message: messageText }
  }
  if (failedCount > 0) {
    return { status: 'partial' as const, created, updated, succeeded, failedCount, failed, message: messageText }
  }
  return { status: 'success' as const, created, updated, succeeded, failedCount, failed, message: messageText }
}

const formatDate = (d?: string) => {
  if (!d) return '-'
  const date = new Date(d)
  if (isNaN(date.getTime())) return d
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

/** 生成 antd 单元格的 data-col-key：粘贴时靠它识别点击的是哪一列，比按列下标推算更不容易因列增减而错位。 */
const colCell = (key: string) => () => ({ 'data-col-key': key }) as React.TdHTMLAttributes<HTMLElement>

export default function ProductImportPage() {
  const { t } = useTranslation()
  // keepAlive：切到别的标签页后本页仍然挂载，只能靠 active 区分是否在前台
  const { active } = useKeepAliveContext()
  const [state, setState] = useState<ImportState>(() => createInitialState(null))
  // 异步流程（检测、翻译、发送）里要读「最新」的行数据，闭包里的 state 可能已经过期
  const stateRef = useRef(state)
  stateRef.current = state
  // 「已有检测结果」；沿用旧名，重复合并 / 清空等流程都靠它回到检测前的界面
  const [showStatistics, setShowStatistics] = useState(false)
  // 最近一次检测覆盖到的行：之后新增的行没有检测结果，不能被算成「无变化」
  const [detectedRowIds, setDetectedRowIds] = useState<ReadonlySet<string> | null>(null)
  const [detectedAt, setDetectedAt] = useState<number | null>(null)
  const [committedCount, setCommittedCount] = useState(0)
  const [statusFilter, setStatusFilter] = useState<ImportStatusFilter>('all')
  const [suppliers, setSuppliers] = useState<Array<{ supplierCode: string; supplierName: string; shopNumber?: string }>>([])
  const [loadingSuppliers, setLoadingSuppliers] = useState(false)
  const [duplicateDialogOpen, setDuplicateDialogOpen] = useState(false)
  const [containers, setContainers] = useState<ContainerMain[]>([])
  const [loadingContainers, setLoadingContainers] = useState(false)
  const [syncProductNamesToHq, setSyncProductNamesToHq] = useState(false)
  const [selectedContainerId, setSelectedContainerId] = useState('')
  const [conflictDialogOpen, setConflictDialogOpen] = useState(false)
  const [conflictRows, setConflictRows] = useState<ContainerConflictRow[]>([])
  const [pendingSend, setPendingSend] = useState<{ containerId: string; notes: string; products: ProductImportItem[] } | null>(null)
  const syncIncludeImageRef = useRef(false)
  const productImportTableRef = useRef<HTMLDivElement | null>(null)
  const headRef = useRef<HTMLDivElement | null>(null)
  const footerRef = useRef<HTMLDivElement | null>(null)
  const [duplicateGroups, setDuplicateGroups] = useState<DuplicateGroup[]>([])
  const [selectedColumnKey, setSelectedColumnKey] = useState<string | null>(null)
  const [tableScrollY, setTableScrollY] = useState(500)
  const [busyAction, setBusyAction] = useState<BusyAction | null>(null)
  // 同步标记：state 更新是异步的，连点两次时第二次点击读到的 busyAction 仍是 null，必须用 ref 才挡得住
  const busyRef = useRef(false)
  // 检测请求无法取消：只让最后一次请求的结果落地（切换供应商 / 清空后迟到的响应直接丢弃）
  const detectGuardRef = useRef(createLatestRequestGuard())

  const busy = busyAction !== null
  // 翻译期间允许继续编辑（结果按 id 合并，不会覆盖期间的修改）；其它动作期间整张表锁定
  const tableLocked = busyAction !== null && busyAction !== 'translate'

  /** 串行执行一个会改数据 / 发请求的动作：已有动作进行中时直接忽略，结束后无论成败都释放。 */
  const runExclusive = useCallback(async <T,>(action: BusyAction, task: () => Promise<T>): Promise<T | undefined> => {
    if (busyRef.current) return undefined
    busyRef.current = true
    setBusyAction(action)
    try {
      return await task()
    } finally {
      busyRef.current = false
      setBusyAction(null)
    }
  }, [])

  /** 回到「还没检测」的状态：清检测结果、重复提示、状态过滤与列选择。 */
  const resetDetectionView = useCallback(() => {
    detectGuardRef.current.invalidate()
    setShowStatistics(false)
    setDetectedRowIds(null)
    setDetectedAt(null)
    setCommittedCount(0)
    setDuplicateGroups([])
    setStatusFilter('all')
    setSelectedColumnKey(null)
  }, [])

  useEffect(() => {
    let cancelled = false
    const load = async () => {
      try {
        setLoadingSuppliers(true)
        const result = await getActiveChinaSuppliers()
        if (!cancelled) setSuppliers(result || [])
      } catch { if (!cancelled) message.error(t('productImport.loadSuppliersFailed', '加载供应商列表失败')) }
      finally { if (!cancelled) setLoadingSuppliers(false) }
    }
    load()
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const loadContainers = useCallback(async () => {
    try {
      setLoadingContainers(true)
      const response = await getContainerList({ sortBy: '装柜日期', sortDirection: 'desc', page: 1, pageSize: 50 })
      const filtered = (response.containers || []).filter((c) => c.状态 === 0)
      setContainers(filtered)
    } catch { message.error(t('productImport.loadContainersFailed', '加载货柜列表失败')) }
    finally { setLoadingContainers(false) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    void loadContainers()
  }, [loadContainers])

  useEffect(() => {
    // 页面不在前台时 DOM 已被 keepAlive 摘下，量出来的位置没有意义，回到前台再重算
    if (!active) return
    const updateTableScrollY = () => {
      const container = productImportTableRef.current
      const tableTop = container?.getBoundingClientRect().top ?? 0
      // 表头不在 scroll.y 里，需要单独扣掉；底部栏与内容区下边距同样要留出来
      const headerHeight = container?.querySelector<HTMLElement>('.ant-table-header')?.offsetHeight ?? 40
      const footerHeight = footerRef.current?.offsetHeight ?? 40
      const bottomPadding = 16 + footerHeight
      const minTableBodyHeight = 260
      const nextScrollY = Math.max(Math.floor(window.innerHeight - tableTop - headerHeight - bottomPadding), minTableBodyHeight)
      setTableScrollY(nextScrollY)
    }

    // 表格高度跟随视口剩余空间，避免导入多行时撑高整个页面，只让表格内部滚动。
    updateTableScrollY()
    window.addEventListener('resize', updateTableScrollY)
    // 工具栏换行、统计条出现 / 消失都会改变表格顶部位置，单靠 resize 事件感知不到
    let observer: ResizeObserver | undefined
    if (headRef.current && typeof ResizeObserver !== 'undefined') {
      observer = new ResizeObserver(updateTableScrollY)
      observer.observe(headRef.current)
      // 底部栏的错误汇总可能换行，高度变化同样要重算
      if (footerRef.current) observer.observe(footerRef.current)
    }
    return () => {
      window.removeEventListener('resize', updateTableScrollY)
      observer?.disconnect()
    }
  }, [active, showStatistics])

  /* ------------------------------ 行编辑 ------------------------------ */

  const addEmptyRows = useCallback((count = 10) => {
    setState((prev) => ({ ...prev, products: [...prev.products, ...Array.from({ length: count }, () => createEmptyProduct())], needsDetection: true }))
  }, [])

  // 删单行时同步从选中集合里去掉它：旧代码只删行不动 selectedIds，残留的 id 会让「已选 N 行」与发送数量对不上
  const removeRows = useCallback((rowIds: string[]) => {
    const idSet = new Set(rowIds)
    setState((prev) => ({
      ...prev,
      products: prev.products.filter((p) => !idSet.has(p.id)),
      selectedIds: prev.selectedIds.filter((id) => !idSet.has(id)),
    }))
  }, [])

  const deleteSelectedRows = useCallback(() => {
    setState((prev) => {
      const newProducts = prev.products.filter((p) => !prev.selectedIds.includes(p.id))
      return { ...prev, products: newProducts, selectedIds: [], needsDetection: true }
    })
  }, [])

  const deleteAllRows = useCallback(() => {
    if (state.products.length === 0) return
    Modal.confirm({
      title: t('productImport.deleteAllConfirmTitle', '删除所有表格行'),
      content: t('productImport.deleteAllConfirmContent', '确认删除当前表格中的所有行？此操作不可恢复。'),
      okText: t('common.confirm', '确定'),
      cancelText: t('common.cancel', '取消'),
      okButtonProps: { danger: true },
      onOk: () => {
        // 清空全部行时同步移除检测结果，避免旧统计继续显示在空表上（统计条与步骤都由行数据派生，行清空即归零）。
        setState((prev) => ({ ...prev, products: [], selectedIds: [], needsDetection: false }))
        resetDetectionView()
      },
    })
  }, [state.products.length, t, resetDetectionView])

  const updateProduct = useCallback((rowId: string, field: string, value: unknown) => {
    setState((prev) => {
      const newProducts = prev.products.map((p) => {
        if (p.id !== rowId) return p
        const updated = { ...p, newProduct: { ...p.newProduct, [field]: value } }
        if (field === 'productCode' && !p.customImage) {
          updated.imageUrl = generateImageUrl(value as string)
          updated.imageLoadStatus = 'loading'
        }
        return updateCalculatedFields(updated)
      })
      return { ...prev, products: newProducts, needsDetection: true }
    })
  }, [])

  /* ------------------------------ 检测匹配 ------------------------------ */

  /** 检测后顺手补全库里缺失的商品图片（尽力而为，失败不影响检测结果）。 */
  const fixMissingProductImages = useCallback(async (items: any[]) => {
    try {
      const itemsNeedingImage = items.filter((item: any) => !item.isNewProduct && item.existingData && (!item.existingData.productImage || String(item.existingData.productImage).trim() === '') && !!item.existingData.hbProductNo)
      if (itemsNeedingImage.length > 0) {
        await Promise.all(itemsNeedingImage.map(async (item: any) => {
          const defaultUrl = generateImageUrl(item.existingData.hbProductNo)
          try {
            await fixProductImage(item.existingData.productCode, defaultUrl)
            setState((prev) => ({ ...prev, products: prev.products.map((p) => p.matchedProduct?.productCode === item.existingData.productCode ? { ...p, matchedProduct: { ...p.matchedProduct, productImage: defaultUrl } } : p) }))
          } catch { /* ignore */ }
        }))
      }
    } catch { /* ignore */ }
  }, [])

  /** 检测核心流程；调用方负责串行（runExclusive）。返回是否检测成功。 */
  const detectProducts = useCallback(async (): Promise<boolean> => {
    const { supplier, products } = stateRef.current
    if (!supplier) { message.warning(t('productImport.selectSupplierFirst', '请先选择供应商')); return false }
    // 只有空行也算没有可检测的数据
    if (products.length === 0 || products.every(isBlankImportRow)) { message.warning(t('productImport.noDataToCheck', '没有可检测的数据')); return false }
    const duplicates = detectDuplicates(products)
    if (duplicates.length > 0) {
      setDuplicateGroups(duplicates)
      setDuplicateDialogOpen(true)
      setState((prev) => ({
        ...prev,
        products: prev.products.map((product) => {
          const isDup = duplicates.some((group) => group.items.some((item) => item.id === product.id))
          return { ...product, isDuplicate: isDup, status: isDup ? 'duplicate' : product.status } as ProductImportItem
        }),
      }))
      return false
    }
    setDuplicateGroups([])
    setDuplicateDialogOpen(false)
    setState((prev) => {
      const nextProducts = prev.products.map((product) => product.isDuplicate || product.status === 'duplicate'
        ? { ...product, isDuplicate: false, duplicateGroup: undefined, status: product.status === 'duplicate' ? 'unchanged' as const : product.status }
        : product)
      return { ...prev, products: nextProducts }
    })
    const requestId = detectGuardRef.current.begin()
    try {
      // 货号统一 trim：发送、匹配、重复检测、创建请求用同一口径，避免带空格的货号被误判为新商品
      const detectionData = products.filter((p) => p.newProduct.productCode?.trim()).map((p) => ({
        HBProductNo: p.newProduct.productCode.trim(),
        ProductName: p.newProduct.productName,
        EnglishProductName: p.newProduct.englishName,
        DomesticPrice: p.newProduct.domesticPrice,
        OEMPrice: p.newProduct.oemPrice,
        MiddlePackQuantity: p.newProduct.midPackQuantity,
        PackingQuantity: p.newProduct.casePackQuantity,
        UnitVolume: p.newProduct.volume,
        Barcode: p.newProduct.barcode,
      }))
      const response = await batchDetectProducts({ SupplierCode: supplier, Products: detectionData })
      if (!detectGuardRef.current.isLatest(requestId)) return false
      if (response.success && response.data) {
        const detectionMap = new Map(response.data.filter((item: any) => item?.inputData?.hbProductNo).map((item: any) => [String(item.inputData.hbProductNo).trim(), item]))
        const pascalToCamelMap: Record<string, string> = {
          ProductName: 'productName', EnglishProductName: 'englishProductName', Barcode: 'barcode',
          DomesticPrice: 'domesticPrice', OEMPrice: 'oemPrice', PackingQuantity: 'packingQuantity',
          UnitVolume: 'unitVolume', MiddlePackQuantity: 'middlePackQuantity',
        }
        setState((prev) => {
          const newProducts = prev.products.map((product) => {
            const code = product.newProduct.productCode?.trim()
            // 空行不是错误（getImportRowErrors 对空行返回空对象）；有内容却没货号才是错误，且是唯一的错误
            if (!code) return { ...product, matchedProduct: undefined, diffFields: [], status: isBlankImportRow(product) ? 'unchanged' : 'error', errors: getImportRowErrors(product) } as ProductImportItem
            const detection = detectionMap.get(code)
            if (!detection) return { ...product, matchedProduct: undefined, diffFields: [], status: 'new' } as ProductImportItem
            if (!detection.isNewProduct && detection.existingData) {
              const diffFields = (detection.changeList || []).map((f: string) => pascalToCamelMap[f] || f)
              const isDbDuplicate = detection.hasDuplicateInDatabase === true
              return { ...product, matchedProduct: detection.existingData, diffFields, status: isDbDuplicate ? 'dbDuplicate' : detection.hasChanges || diffFields.length > 0 ? 'updated' : 'unchanged' } as ProductImportItem
            }
            return { ...product, matchedProduct: undefined, diffFields: [], status: 'new' } as ProductImportItem
          })
          return { ...prev, products: newProducts, needsDetection: false }
        })
        // 检测期间表格是锁定的，行集合不会变，用请求时的 id 集合即可
        setDetectedRowIds(new Set(products.map((p) => p.id)))
        setDetectedAt(Date.now())
        setShowStatistics(true)
        message.success(t('productImport.checkComplete', '检测完成！'))
        // 补图是附带动作，不阻塞检测结果与表格解锁
        void fixMissingProductImages(response.data || [])
        return true
      }
      throw new Error(response.message || t('productImport.checkFailed', '检测失败'))
    } catch (error: any) {
      if (detectGuardRef.current.isLatest(requestId)) message.error(error.message || t('productImport.checkFailedRetry', '检测失败，请重试'))
      return false
    }
  }, [fixMissingProductImages, t])

  const handleDetect = useCallback(() => runExclusive('detect', detectProducts), [runExclusive, detectProducts])

  const handleMergeDuplicates = useCallback(() => {
    const result = mergeDuplicateProducts(state.products)
    if (result.invalidGroups.length > 0) {
      const details = result.invalidGroups.map((group) => group.productCode).join('、')
      message.error(t('productImport.mergeBlockedDetails', '无法合并以下货号，请修正件数、装箱数和体积：{{details}}', { details }))
      return
    }

    setState((prev) => ({
      ...prev,
      products: result.products,
      selectedIds: [],
      needsDetection: true,
    }))
    setDuplicateDialogOpen(false)
    setDuplicateGroups([])
    setShowStatistics(false)
    // 合并后行集合变了，之前的检测结果整体作废
    setDetectedRowIds(null)
    setDetectedAt(null)
    setStatusFilter('all')
    message.success(t('productImport.mergeSuccess', '成功合并重复数据，共 {{count}} 组', { count: result.mergedGroupCount }))
  }, [state.products, t])

  /* ------------------------------ 翻译 / 主表名称 ------------------------------ */

  const handleBatchTranslate = useCallback(() => runExclusive('translate', async () => {
    // 点击那一刻的快照：目标行与它们当时的名称，用来在结果回来时判断哪些行期间被改过
    const targets = snapshotTranslationTargets(stateRef.current.products, stateRef.current.selectedIds)
    const names = Array.from(new Set(targets.map((target) => target.productName)))

    if (names.length === 0) {
      message.warning(t('productImport.noNamesToTranslate', '没有可翻译的商品名称'))
      return
    }

    try {
      const translations = await batchTranslate(names)
      // 按 id 合并进「当前」的行：翻译期间用户的编辑、粘贴、删除都不会被旧数组覆盖
      const merged = mergeTranslationsById(stateRef.current.products, targets, translations)

      if (merged.appliedCount === 0) {
        message.warning(merged.editedCount > 0
          ? t('productImport.translateRowsEditedSkipped', '有 {{count}} 行在翻译期间被修改，未覆盖', { count: merged.editedCount })
          : t('productImport.noValidTranslatedNames', '没有可保存的英文翻译结果'))
        return
      }

      setState((prev) => ({
        ...prev,
        products: mergeTranslationsById(prev.products, targets, translations).products,
        needsDetection: true,
      }))
      message.success(t('productImport.batchTranslateSuccess', '成功翻译 {{count}} 个商品名称', { count: merged.appliedCount }))
      if (merged.skippedCount > 0) {
        message.warning(t('productImport.invalidTranslatedNamesSkipped', '有 {{count}} 条翻译结果仍包含中文或无变化，已跳过', { count: merged.skippedCount }))
      }
      if (merged.editedCount > 0) {
        message.warning(t('productImport.translateRowsEditedSkipped', '有 {{count}} 行在翻译期间被修改，未覆盖', { count: merged.editedCount }))
      }
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('productImport.batchTranslateFailed', '批量翻译失败'))
    }
  }), [runExclusive, t])

  const handleUpdateHbwebProductNames = useCallback(() => {
    if (busyRef.current) {
      return
    }

    if (!state.supplier?.trim()) { message.warning(t('productImport.selectSupplierFirst', '请先选择供应商')); return }

    if (state.selectedIds.length === 0) {
      message.warning(t('productImport.selectProductsFirst', '请先选择商品'))
      return
    }

    const updatePayload = buildHbwebProductNameUpdates(state.products, state.selectedIds, state.supplier)
    if (updatePayload.missingItemNumbers.length > 0) {
      message.error(t('productImport.missingHbwebNameItemNumbers', '已选商品存在空货号，请先补齐货号'))
      return
    }
    if (updatePayload.missingProductNames.length > 0) {
      message.warning(t('productImport.missingHbwebProductNames', '以下货号缺少英文名称：{{items}}', { items: updatePayload.missingProductNames.join(', ') }))
      return
    }
    if (updatePayload.conflictItemNumbers.length > 0) {
      message.error(t('productImport.conflictHbwebProductNames', '以下货号存在多个英文名称，请先修正：{{items}}', { items: updatePayload.conflictItemNumbers.join(', ') }))
      return
    }
    if (updatePayload.products.length === 0) {
      message.warning(t('productImport.noHbwebProductNamesToUpdate', '没有可更新的商品主表名称'))
      return
    }
    const confirmationKeys = getHbwebProductNameSyncConfirmationKeys(syncProductNamesToHq)

    Modal.confirm({
      title: t('productImport.updateHbwebProductNamesTitle', '更新商品主表名称'),
      content: (
        <div>
          <div>{t(
            confirmationKeys.confirmKey,
            syncProductNamesToHq
              ? '将把选中 {{count}} 个货号的英文名称同时写入 HBweb 商品主表 Product.ProductName 和 HQ DIC_商品信息字典表.H商品名称。'
              : '将把选中 {{count}} 个货号的英文名称写入 HBweb 商品主表 Product.ProductName。',
            { count: updatePayload.products.length },
          )}</div>
          <div style={{ marginTop: 8, color: '#8c8c8c' }}>{t(
            confirmationKeys.scopeKey,
            syncProductNamesToHq
              ? '仅更新商品名称和审计字段，不改价格、库存、条码、图片，也不会创建 HQ 缺失商品。'
              : '只更新商品名称字段，不改英文名、价格、条码、图片和分店价格。',
          )}</div>
        </div>
      ),
      okText: t('productImport.confirmUpdateHbwebProductNames', '确认更新'),
      cancelText: t('common.cancel', '取消'),
      // onOk 返回 Promise：antd 会让确认按钮保持 loading 直到请求结束；runExclusive 再挡住并发的其它动作
      onOk: () => runExclusive('names', async () => {
        try {
          const response = await updateHbwebProductNames({ Products: updatePayload.products, SyncToHq: syncProductNamesToHq })
          const feedback = summarizeHbwebProductNameSyncResponse(response)
          if (feedback.status === 'failure') {
            throw new Error(response.message || t('productImport.updateHbwebProductNamesFailed', '更新商品主表名称失败'))
          }

          const notification = buildHbwebProductNameSyncNotificationDecision(feedback)
          let notificationText: string
          if (notification.partial) {
            notificationText = t('productImport.hqProductNameSyncPartialFailed', 'HBweb 商品主表名称已更新：更新 {{updated}}，无变化 {{unchanged}}，未找到 {{missing}}，跳过 {{skipped}}；HQ 同步失败', {
              updated: notification.hbweb.updatedCount,
              unchanged: notification.hbweb.unchangedCount,
              missing: notification.hbweb.missingCount,
              skipped: notification.hbweb.warningCount,
            })
          } else {
            const parts = [t('productImport.updateHbwebProductNamesSuccess', '商品主表名称更新完成：更新 {{updated}}，无变化 {{unchanged}}，未找到 {{missing}}', {
              updated: notification.hbweb.updatedCount,
              unchanged: notification.hbweb.unchangedCount,
              missing: notification.hbweb.missingCount,
            })]
            if (notification.hbweb.warningCount > 0) {
              parts.push(t('productImport.updateHbwebProductNamesSkipped', '跳过商品：{{count}}', { count: notification.hbweb.warningCount }))
            }
            if (notification.hq) {
              parts.push(t('productImport.hqProductNameSyncSuccess', 'HQ 商品名称同步完成：更新 {{updated}}，无变化 {{unchanged}}，未找到 {{missing}}', {
                updated: notification.hq.updatedCount,
                unchanged: notification.hq.unchangedCount,
                missing: notification.hq.missingCount,
              }))
              if (notification.hq.warningCount > 0) {
                parts.push(t('productImport.hqProductNameSyncWarning', 'HQ 警告：{{count}}', { count: notification.hq.warningCount }))
              }
            }
            notificationText = parts.join(' | ')
          }

          if (notification.level === 'success') {
            message.success(notificationText)
          } else {
            message.warning(notificationText)
          }
        } catch (error: any) {
          message.error(error?.message || t('productImport.updateHbwebProductNamesFailed', '更新商品主表名称失败'))
        }
      }),
    })
  }, [runExclusive, state.products, state.selectedIds, state.supplier, syncProductNamesToHq, t])

  /* ------------------------------ 入库（新建 / 更新） ------------------------------ */

  const handleBatchCreate = useCallback(() => {
    const { supplier, products } = stateRef.current
    const newProducts = products.filter((p) => p.status === 'new')
    if (newProducts.length === 0) { message.warning(t('productImport.noNewProducts', '没有新商品需要创建')); return }
    if (!supplier) { message.warning(t('productImport.selectSupplierFirst', '请先选择供应商')); return }
    return runExclusive('create', async () => {
      try {
        const removeUndefined = (obj: any) => Object.fromEntries(Object.entries(obj).filter(([, v]) => v !== undefined && v !== null && v !== ''))
        const dto: any = {
          SupplierCode: supplier,
          NewProducts: newProducts.map((item) => removeUndefined({
            HBProductNo: item.newProduct.productCode?.trim(), ProductName: item.newProduct.productName?.trim(), EnglishProductName: item.newProduct.englishName?.trim(),
            Barcode: item.newProduct.barcode?.trim(), DomesticPrice: typeof item.newProduct.domesticPrice === 'number' ? item.newProduct.domesticPrice : undefined,
            OEMPrice: typeof item.newProduct.oemPrice === 'number' ? item.newProduct.oemPrice : undefined,
            MiddlePackQuantity: item.newProduct.midPackQuantity && item.newProduct.midPackQuantity > 0 ? item.newProduct.midPackQuantity : undefined,
            PackingQuantity: item.newProduct.casePackQuantity && item.newProduct.casePackQuantity > 0 ? item.newProduct.casePackQuantity : undefined,
            UnitVolume: item.newProduct.volume !== undefined && item.newProduct.volume >= 0 ? item.newProduct.volume : undefined,
          })),
          UpdateProducts: [],
        }
        const response = await batchImportConfirm(dto)
        const createdCount = response?.data?.createdProducts?.length ?? response?.data?.created ?? 0
        message.success(t('productImport.createSuccessCount', '成功新建 {{count}} 个商品', { count: createdCount }))
        setCommittedCount((count) => count + createdCount)
        setState((prev) => ({ ...prev, products: prev.products.map((p) => p.status === 'new' ? { ...p, status: 'unchanged' as const, diffFields: [] } : p) }))
        // 入库后立刻重新检测，让表格以库里的最新状态为准；仍在本次串行动作内，表格保持锁定
        const detected = await detectProducts()
        if (!detected) setState((prev) => ({ ...prev, needsDetection: true }))
      } catch (error: any) {
        message.error(error?.response?.data?.message || error?.message || t('productImport.batchCreateFailed', '批量创建失败'))
      }
    })
  }, [detectProducts, runExclusive, t])

  const handleBatchUpdate = useCallback(() => {
    const updatedProducts = stateRef.current.products.filter((p) => p.status === 'updated')
    if (updatedProducts.length === 0) { message.warning(t('productImport.noProductsToUpdate', '没有商品需要更新')); return }
    return runExclusive('update', async () => {
      try {
        const productsToUpdate = updatedProducts.map((product) => {
          const targetCode = product.matchedProduct?.productCode
          if (!targetCode) return null
          const updateItem: any = { ProductCode: targetCode }
          if (product.newProduct.productName) updateItem.ProductName = product.newProduct.productName
          if (product.newProduct.englishName) updateItem.EnglishProductName = product.newProduct.englishName
          if (product.newProduct.barcode?.trim()) updateItem.Barcode = product.newProduct.barcode.trim()
          if (product.newProduct.domesticPrice !== undefined) updateItem.DomesticPrice = product.newProduct.domesticPrice
          if (product.newProduct.oemPrice !== undefined) updateItem.OEMPrice = product.newProduct.oemPrice
          if (product.newProduct.midPackQuantity !== undefined) updateItem.MiddlePackQuantity = product.newProduct.midPackQuantity
          if (product.newProduct.casePackQuantity !== undefined) updateItem.PackingQuantity = product.newProduct.casePackQuantity
          if (product.newProduct.volume !== undefined) updateItem.UnitVolume = product.newProduct.volume
          return updateItem
        }).filter(Boolean) as any[]
        const response = await batchUpdateDomesticProducts({ Products: productsToUpdate })
        const successCount = response?.data?.updatedProducts?.length ?? 0
        message.success(t('productImport.updateSuccessCount', '成功更新 {{count}} 个商品', { count: successCount }))
        setCommittedCount((count) => count + successCount)
        setState((prev) => ({ ...prev, products: prev.products.map((p) => p.status === 'updated' ? { ...p, status: 'unchanged' as const, diffFields: [] } : p) }))
        const detected = await detectProducts()
        if (!detected) setState((prev) => ({ ...prev, needsDetection: true }))
      } catch { message.error(t('productImport.batchUpdateFailed', '批量更新失败，请重试')) }
    })
  }, [detectProducts, runExclusive, t])

  /* ------------------------------ 发送货柜 / 同步 ------------------------------ */

  const formatInvalidAssignItems = useCallback((products: ReturnType<typeof buildAssignContainerItems>) => (
    findInvalidAssignContainerItems(products)
      .map((item) => `${item.hbProductNo || item.productCode || '(无货号)'}(${item.fields.join('/')})`)
      .join(', ')
  ), [])

  const formatAssignFailedItems = useCallback((failed: Array<{ hbProductNo?: string; productCode?: string; reason: string }>) => (
    failed
      .map((item) => `${t('productImport.failedItemHbProductNo', '货号')}: ${item.hbProductNo || 'N/A'} / ${t('productImport.failedItemProductCode', '本地编码')}: ${item.productCode || 'N/A'} / ${item.reason}`)
      .join(', ')
  ), [t])

  const getMissingProductCodeError = useCallback((items: ReturnType<typeof buildAssignContainerItems>) => {
    const invalidItems = findInvalidAssignContainerItems(items)
    const missingProductCodeItems = invalidItems.filter((item) => item.reasons.includes('未匹配本地商品编码'))
    if (missingProductCodeItems.length === 0) return ''
    return missingProductCodeItems
      .map((item) => `${item.hbProductNo || 'N/A'}(${item.reasons.join('/')})`)
      .join(', ')
  }, [])

  const showAssignSummaryMessage = useCallback((summary: ReturnType<typeof mergeAssignSummaries>) => {
    const failedDetails = summary.failedCount > 0
      ? t('productImport.assignFailedDetails', '失败明细：{{items}}', { items: formatAssignFailedItems(summary.failed) })
      : ''

    if (summary.status === 'success') {
      message.success(t('productImport.sendResultDetail', '发送完成：新建 {{created}}，更新 {{updated}}', { created: summary.created, updated: summary.updated }))
      return
    }

    if (summary.status === 'partial') {
      message.warning(t('productImport.sendPartialSuccess', '部分发送成功：新建 {{created}}，更新 {{updated}}，失败 {{failedCount}}。{{details}}', {
        created: summary.created,
        updated: summary.updated,
        failedCount: summary.failedCount,
        details: failedDetails,
      }))
      return
    }

    const fallbackMessage = t('productImport.sendFailedWithDetails', '发送失败：新建 {{created}}，更新 {{updated}}，失败 {{failedCount}}。{{details}}', {
      created: summary.created,
      updated: summary.updated,
      failedCount: summary.failedCount,
      details: failedDetails,
    })
    message.error([summary.message, failedDetails].filter(Boolean).join(' ') || fallbackMessage)
  }, [formatAssignFailedItems, t])

  const markProductsSentToContainer = useCallback((productIds: string[]) => {
    if (productIds.length === 0) return
    const idSet = new Set(productIds)
    setState((prev) => ({
      ...prev,
      // 只有整批成功时才标记为已发送，避免局部失败时出现假成功状态。
      products: prev.products.map((product) => idSet.has(product.id) ? { ...product, sentToContainer: true } : product),
    }))
  }, [])

  const handleSendToContainer = useCallback((containerId: string, notes: string) => runExclusive('send', async () => {
    const { products, selectedIds } = stateRef.current
    const selectedProducts = products.filter((p) => selectedIds.includes(p.id))
    const invalid = selectedProducts.filter((p) => !p.newProduct.quantity || p.newProduct.quantity <= 0)
    if (invalid.length > 0) { message.error(t('productImport.invalidQuantity', '以下商品件数不能为空且必须>0：{{items}}', { items: invalid.map((p) => p.newProduct.productCode || '(无货号)').join(', ') })); return }
    const assignItems = buildAssignContainerItems(selectedProducts, notes)
    const missingProductCodeError = getMissingProductCodeError(assignItems)
    if (missingProductCodeError) {
      message.error(t('productImport.missingContainerProductCodes', '以下商品未匹配本地商品编码，无法发送到货柜：{{items}}', { items: missingProductCodeError }))
      return
    }
    const invalidAssignItems = formatInvalidAssignItems(assignItems)
    if (invalidAssignItems) { message.error(t('productImport.invalidContainerBusinessFields', '发送货柜商品业务字段不能为空或为 0：{{items}}', { items: invalidAssignItems })); return }
    try {
      const checkResp = await checkContainerConflicts(containerId, selectedProducts.filter((p) => p.newProduct.productCode || p.matchedProduct?.productCode).map((p) => ({ hbProductNo: p.newProduct.productCode || undefined, productCode: p.matchedProduct?.productCode || undefined })))
      if (checkResp?.data && checkResp.data.length > 0) {
        // 后端 check-conflicts 按本地商品编码返回冲突，这里对回到本次要发送的行，弹窗里才有名称与本次件数
        setConflictRows(buildConflictRows(selectedProducts, checkResp.data.map((item) => ({ productCode: item.productCode, existingPieces: item.existingPieces ?? item.existingQuantity }))))
        setPendingSend({ containerId, notes, products: selectedProducts })
        setConflictDialogOpen(true)
        return
      }
      const assignResp = await assignProductsToContainer(containerId, stripAssignContainerItemsForRequest(assignItems), 'increase', notes)
      const summary = mergeAssignSummaries([summarizeAssignProductsResult(assignResp, assignItems)])
      showAssignSummaryMessage(summary)
      if (summary.status === 'success') {
        markProductsSentToContainer(selectedProducts.map((product) => product.id))
      }
    } catch (error: any) { message.error(error?.response?.data?.message || error?.message || t('productImport.sendFailed', '发送失败')) }
  }), [formatInvalidAssignItems, getMissingProductCodeError, markProductsSentToContainer, runExclusive, showAssignSummaryMessage, t])

  const handleSendSelectedClick = () => {
    if (state.selectedIds.length === 0) { message.error(t('productImport.selectProductsFirst', '请先选择商品')); return }
    if (!selectedContainerId) { message.error(t('productImport.selectContainerFirst', '请先选择货柜')); return }
    if (state.products.some((p) => state.selectedIds.includes(p.id) && (!p.newProduct.quantity || p.newProduct.quantity <= 0))) { message.error(t('productImport.fixSelectedQuantityFirst', '请先修正已选商品的件数')); return }
    void handleSendToContainer(selectedContainerId, '')
  }

  const handleSyncToHBSales = () => {
    if (busyRef.current) return
    if (state.selectedIds.length === 0) { message.warning(t('productImport.selectSyncProductsFirst', '请先选择要同步的商品')); return }
    Modal.confirm({
      title: t('productImport.syncToHBSales', '同步到HBSales'),
      content: (
        <div>
          <p>{t('productImport.syncConfirmText', '确定要将选中的 {{count}} 件商品同步到HBSales数据库吗？', { count: state.selectedIds.length })}</p>
          <div style={{ marginTop: 12 }}>
            <Checkbox defaultChecked={syncIncludeImageRef.current} onChange={(e) => { syncIncludeImageRef.current = e.target.checked }}>{t('productImport.syncIncludeImage', '同时更新商品图片')}</Checkbox>
          </div>
        </div>
      ),
      okText: t('common.confirm', '确定'),
      cancelText: t('common.cancel', '取消'),
      onOk: () => runExclusive('hbsales', async () => {
        try {
          const selectedProducts = stateRef.current.products.filter((p) => stateRef.current.selectedIds.includes(p.id))
          const productCodes = selectedProducts.map((p) => p.matchedProduct?.productCode).filter((code): code is string => code !== undefined)
          if (productCodes.length === 0) { message.warning(t('productImport.noMatchedCodes', '选中的商品中没有已匹配的本地商品编码')); return }
          const response = await syncToHBSales(productCodes, syncIncludeImageRef.current)
          if (response.success) { message.success(response.data?.message || t('productImport.syncResult', '同步完成：成功 {{count}} 条', { count: response.data?.addedCount || 0 })) }
          else { message.error(t('productImport.syncFailedMsg', '同步失败: ') + response.message) }
        } catch { message.error(t('productImport.syncFailedRetry', '同步失败，请稍后重试')) }
      }),
    })
  }

  const handleSendToHq = () => {
    if (busyRef.current) return
    if (state.selectedIds.length === 0) { message.warning(t('productImport.selectSendProductsFirst', '请先选择要发送的商品')); return }
    const selectedProducts = state.products.filter((p) => state.selectedIds.includes(p.id))
    const productCodes = selectedProducts.map((p) => p.matchedProduct?.productCode).filter((code): code is string => !!code)
    if (productCodes.length === 0) { message.warning(t('productImport.noMatchedCodesCheck', '选中的商品中没有已匹配的本地商品编码，请先检测匹配')); return }
    const missingPrice = selectedProducts.filter((p) => !p.matchedProduct?.productCode)
    if (missingPrice.length > 0) { message.warning(`${t('productImport.unmatchedProducts', '{{count}} 个商品未匹配，请先检测', { count: missingPrice.length })}`); return }
    Modal.confirm({
      title: t('productImport.sendToHQ', '发送商品到HQ'),
      content: (
        <div>
          <p>{t('productImport.sendConfirmText', '确定要将选中的 {{count}} 个商品发送到HQ数据库吗？', { count: productCodes.length })}</p>
          <p style={{ fontSize: 12, color: '#888' }}>{t('productImport.sendHint', '将先同步到 HBSales（含商品图片），再写入 HQ 的 DIC_商品信息字典表、DIC_商品零售价表 和 CBP_DIC_商品库存表')}</p>
          <p style={{ fontSize: 12, color: '#f97316' }}>{t('productImport.sendPriceWarning', '⚠️ 要求商品必须有进口价格和零售价')}</p>
        </div>
      ),
      okText: t('productImport.confirmSend', '确定发送'),
      cancelText: t('common.cancel', '取消'),
      onOk: () => runExclusive('hq', async () => {
        try {
          const response = await sendToHq(productCodes)
          if (response.success) { message.success(response.data?.message || t('productImport.sendComplete', '发送完成')) }
          else { message.error(t('productImport.sendFailedMsg', '发送失败: ') + response.message) }
        } catch { message.error(t('productImport.sendFailedRetry', '发送失败，请稍后重试')) }
      }),
    })
  }

  const handleResolveConflicts = (selection: ConflictSelection) => runExclusive('send', async () => {
    if (!pendingSend) return
    const { containerId, notes, products } = pendingSend
    // 逐项选择以本地商品编码为键：旧代码用货号去查，「覆盖」的选择从未生效
    const { override: overrideItems, increase: increaseItems } = splitProductsByResolution(products, selection)
    try {
      const summaries: Array<ReturnType<typeof summarizeAssignProductsResult>> = []
      const overrideAssignItems = buildAssignContainerItems(overrideItems, notes)
      const increaseAssignItems = buildAssignContainerItems(increaseItems, notes)
      const allAssignItems = [...overrideAssignItems, ...increaseAssignItems]
      const missingProductCodeError = getMissingProductCodeError(allAssignItems)
      if (missingProductCodeError) { message.error(t('productImport.missingContainerProductCodes', '以下商品未匹配本地商品编码，无法发送到货柜：{{items}}', { items: missingProductCodeError })); return }
      const invalidAssignItems = formatInvalidAssignItems(allAssignItems)
      if (invalidAssignItems) { message.error(t('productImport.invalidContainerBusinessFields', '发送货柜商品业务字段不能为空或为 0：{{items}}', { items: invalidAssignItems })); return }
      if (overrideItems.length > 0) {
        const resp = await assignProductsToContainer(containerId, stripAssignContainerItemsForRequest(overrideAssignItems), 'override', notes)
        summaries.push(summarizeAssignProductsResult(resp, overrideAssignItems))
      }
      if (increaseItems.length > 0) {
        const resp = await assignProductsToContainer(containerId, stripAssignContainerItemsForRequest(increaseAssignItems), 'increase', notes)
        summaries.push(summarizeAssignProductsResult(resp, increaseAssignItems))
      }
      if (summaries.length === 0) {
        message.warning(t('productImport.selectProductsFirst', '请先选择商品'))
        return
      }
      const summary = mergeAssignSummaries(summaries)
      showAssignSummaryMessage(summary)
      if (summary.status === 'success') {
        markProductsSentToContainer(products.map((product) => product.id))
      }
      setPendingSend(null)
      setConflictDialogOpen(false)
    } catch (error: any) { message.error(error?.response?.data?.message || error?.message || t('productImport.sendFailed', '发送失败')) }
  })

  /* ------------------------------ 派生数据 ------------------------------ */

  const counts = useMemo(() => countImportRows(state.products, detectedRowIds), [state.products, detectedRowIds])
  const visibleProducts = useMemo(
    () => (statusFilter === 'all' ? state.products : state.products.filter((row) => matchesImportFilter(row, statusFilter, detectedRowIds))),
    [state.products, statusFilter, detectedRowIds],
  )
  // 行号按「整张表」计，不随状态过滤变化，与「第 N 行」文案一致
  const rowNumberById = useMemo(() => new Map(state.products.map((row, index) => [row.id, index + 1])), [state.products])
  const duplicateIndex = useMemo(() => buildDuplicateRowIndex(state.products), [state.products])
  const importErrors = useMemo(() => collectImportErrors(state.products, detectedRowIds), [state.products, detectedRowIds])
  const selectionStats = useMemo(() => calculateStatistics(state.products, state.selectedIds), [state.products, state.selectedIds])
  const filledRowCount = useMemo(() => state.products.filter((row) => !isBlankImportRow(row)).length, [state.products])
  // 重复被拦下时统计条也要出现，方便用户过滤出重复行去改
  const showChips = showStatistics || duplicateGroups.length > 0

  const steps = useMemo(() => computeImportSteps({
    hasSupplier: Boolean(state.supplier),
    filledRowCount,
    detected: showStatistics,
    needsDetection: state.needsDetection,
    duplicateGroupCount: duplicateGroups.length,
    pendingCommitCount: counts.new + counts.updated,
    committedCount,
    sentCount: counts.sent,
  }), [state.supplier, filledRowCount, showStatistics, state.needsDetection, duplicateGroups.length, counts.new, counts.updated, counts.sent, committedCount])

  // 「更新 / 新建」只在检测结果新鲜、没有重复阻断时可用
  const commitBlockedReason = !showStatistics || state.needsDetection || duplicateGroups.length > 0
    ? t('productImport.commitNeedDetect', '数据有改动或尚未检测，请先重新检测')
    : ''

  const containerOptions = useMemo(() => containers
    .filter((c) => c.货柜编号)
    .map((c) => ({
      label: t('productImport.containerOptionLabel', '{{code}} · 装柜 {{date}}', { code: c.货柜编号, date: formatDate(c.装柜日期) }),
      value: c.货柜编号 as string,
    })), [containers, t])

  const supplierOptions = useMemo(() => [...suppliers].sort((a, b) => (a.supplierCode || '').localeCompare(b.supplierCode || '')).map((s) => ({
    label: `${s.supplierCode} - ${s.supplierName}${s.shopNumber ? ` - ${s.shopNumber}` : ''}`,
    value: s.supplierCode,
    // 供应商下拉搜索覆盖完整展示信息，店号/附加编码也能直接命中。
    searchText: [s.supplierCode, s.supplierName, s.shopNumber].filter(Boolean).join(' '),
  })), [suppliers])

  const handleSupplierChange = (value: string) => {
    if (value === state.supplier) return
    const change = () => {
      setState(createInitialState(value))
      resetDetectionView()
    }
    // 第一次选供应商时表里的数据本来就不属于任何供应商，直接保留；
    // 之后才是真正的「切换」，且只有表里确实有内容才需要确认清空
    if (state.supplier === null) {
      setState((prev) => ({ ...prev, supplier: value }))
      return
    }
    if (state.products.some((row) => !isBlankImportRow(row))) {
      Modal.confirm({ title: t('productImport.switchSupplier', '切换供应商'), content: t('productImport.switchSupplierWarning', '切换供应商将清空当前表格数据，是否继续？'), onOk: change })
    } else change()
  }

  /* ------------------------------ 列选择 / 粘贴 ------------------------------ */

  const resolveColumnKeyFromTd = useCallback((td: HTMLTableCellElement): string | null => {
    // 每个单元格都带 data-col-key（见 colCell），直接读属性，不再按列下标推算（列数随检测前后变化，下标容易错位）
    return td.getAttribute('data-col-key')
  }, [])

  const handleClearColumn = useCallback(() => {
    if (!selectedColumnKey) return
    const editableKey = toEditableColumn(selectedColumnKey)
    if (!editableKey) { message.warning(t('productImport.columnCannotClear', '该列不支持清空')); return }
    // 过滤状态下表里只显示一部分行，清列却会清掉全部行，必须先回到「全部」
    if (statusFilter !== 'all') { message.warning(t('productImport.filterBlocksBulkEdit', '筛选状态下不能整列粘贴或清空，请先切换到「全部」')); return }
    setState((prev) => ({ ...prev, products: clearImportColumn(prev.products, editableKey), needsDetection: true }))
    message.success(t('productImport.columnCleared', '已清空列数据'))
  }, [selectedColumnKey, statusFilter, t])

  const handlePaste = useCallback((e: ClipboardEvent) => {
    const target = e.target
    if (!(target instanceof HTMLElement)) return
    const tableContainer = target.closest('.ant-table-wrapper')
    // 只处理本页表格里的粘贴：document 级监听会收到页面上任何位置（含别的标签页、弹窗）的粘贴事件
    if (!tableContainer || !productImportTableRef.current?.contains(tableContainer)) return
    if (tableLocked) return
    const text = e.clipboardData?.getData('text/plain')
    if (!text) return
    const data = parseProductImportPasteText(text)
    if (data.length === 0) return
    const isSingleCell = data.length === 1 && data[0].length === 1
    const isInInput = target.tagName === 'INPUT' || target.tagName === 'TEXTAREA'
    if (isInInput && isSingleCell) return
    e.preventDefault()
    e.stopPropagation()
    // 过滤状态下可见行只是整表的一部分，多行粘贴会写进看不见的行
    if (statusFilter !== 'all') { message.warning(t('productImport.filterBlocksBulkEdit', '筛选状态下不能整列粘贴或清空，请先切换到「全部」')); return }

    let startColumn: ImportEditableColumn | null
    let rowIndex: number
    const td = target.closest('td')

    if (selectedColumnKey) {
      startColumn = toEditableColumn(selectedColumnKey)
      if (!startColumn) { message.warning(t('productImport.cannotPaste', '该列不支持粘贴')); return }
      const tr = td?.closest('tr')
      if (td && tr) {
        // 行 key 是 row_时间戳_随机串，必须按 id 全等查下标，不能 parseInt
        rowIndex = findRowIndexById(state.products, tr.getAttribute('data-row-key'))
        if (rowIndex === -1) { message.warning(t('productImport.cannotDetermineRow', '无法确定行位置')); return }
      } else {
        rowIndex = 0
      }
    } else {
      if (!td) {
        message.warning(t('productImport.selectPasteStart', '请先选中一个单元格或列头作为粘贴起点'))
        return
      }
      startColumn = toEditableColumn(resolveColumnKeyFromTd(td))
      if (!startColumn) { message.warning(t('productImport.selectEditableCol', '请先选中一个可编辑列')); return }
      const tr = td.closest('tr')
      if (!tr) return
      rowIndex = findRowIndexById(state.products, tr.getAttribute('data-row-key'))
      if (rowIndex === -1) { message.warning(t('productImport.cannotDetermineRow', '无法确定行位置')); return }
    }

    const newProducts = applyImportPaste(state.products, rowIndex, startColumn, data)
    setState((prev) => ({ ...prev, products: newProducts, needsDetection: true }))
    message.success(t('productImport.pasteSuccess', '成功粘贴 {{count}} 行数据', { count: data.length }))
  }, [state.products, selectedColumnKey, statusFilter, tableLocked, resolveColumnKeyFromTd, t])

  useEffect(() => {
    // keepAlive 下页面切到后台时 React 树仍然挂载，document 级监听如果一直挂着会截获别的页面里的粘贴；
    // active 取自 keepalive-for-react 的上下文（后台页恒为 false），所以只在页面处于前台时才注册。
    if (!active) return
    document.addEventListener('paste', handlePaste)
    return () => document.removeEventListener('paste', handlePaste)
  }, [active, handlePaste])

  const handleHeaderClick = useCallback((columnKey: string, e: React.MouseEvent) => {
    e.stopPropagation()
    if (tableLocked) return
    setSelectedColumnKey((prev) => prev === columnKey ? null : columnKey)
  }, [tableLocked])

  /** 点击底部错误汇总：必要时先回到「全部」，再把目标行滚到中间并聚焦货号输入框。 */
  const jumpToRow = useCallback((rowId: string) => {
    setStatusFilter('all')
    window.requestAnimationFrame(() => {
      const rowElement = Array.from(productImportTableRef.current?.querySelectorAll<HTMLElement>('tr[data-row-key]') ?? [])
        .find((element) => element.getAttribute('data-row-key') === rowId)
      if (!rowElement) return
      rowElement.scrollIntoView({ block: 'center' })
      rowElement.querySelector<HTMLInputElement>('td[data-col-key="productCode"] input')?.focus()
      // 短暂高亮，帮用户在密集的表格里找到目标行
      rowElement.classList.add('pi-row-flash')
      window.setTimeout(() => rowElement.classList.remove('pi-row-flash'), 1600)
    })
  }, [])

  /* ------------------------------ 表格列 ------------------------------ */

  const columns: ColumnsType<ProductImportItem> = useMemo(() => {
    const editableHeader = (field: ImportEditableColumn) => ({
      onHeaderCell: () => ({
        onClick: (e: React.MouseEvent) => handleHeaderClick(field, e),
        className: 'pi-th-pastable',
        title: t('productImport.headerPasteHint', '点击选中整列，再按 Ctrl+V 粘贴该列'),
      }) as React.ThHTMLAttributes<HTMLElement>,
      onCell: colCell(field),
      className: selectedColumnKey === field ? 'pi-col-selected' : undefined,
    })

    const renderEditable = (
      field: ImportEditableColumn,
      kind: 'text' | 'number',
      record: ProductImportItem,
      extra?: { precision?: number; step?: string; mono?: boolean; warning?: string },
    ) => {
      const diff = isImportDiffField(field) ? getImportCellDiff(record, field) : null
      const isCodeError = field === 'productCode' && resolveImportRowKind(record, detectedRowIds) === 'error'
      return (
        <ImportCell
          rowId={record.id}
          field={field}
          kind={kind}
          value={(record.newProduct as unknown as Record<string, string | number | undefined>)[field]}
          disabled={tableLocked}
          changed={diff !== null}
          oldLabel={diff ? (diff.oldDisplay === null ? t('productImport.oldValueEmpty', '原：空') : t('productImport.oldValue', '原 {{value}}', { value: diff.oldDisplay })) : undefined}
          invalidMessage={isCodeError ? t('productImport.cellItemNoRequired', '货号不能为空') : undefined}
          warning={extra?.warning}
          precision={extra?.precision}
          step={extra?.step}
          mono={extra?.mono}
          onCommit={updateProduct}
        />
      )
    }

    const renderDeleteButton = (record: ProductImportItem) => (
      <Tooltip title={t('productImport.deleteRow', '删除此行')}>
        <button
          type="button"
          className="pi-row-delete"
          aria-label={t('productImport.deleteRow', '删除此行')}
          disabled={tableLocked}
          onClick={() => removeRows([record.id])}
        >
          <DeleteOutlined />
        </button>
      </Tooltip>
    )

    const base: ColumnsType<ProductImportItem> = [
      {
        title: t('productImport.quantity', '件数'),
        dataIndex: ['newProduct', 'quantity'],
        key: 'quantity',
        width: 48,
        ...editableHeader('quantity'),
        render: (_, record) => renderEditable('quantity', 'number', record),
      },
      {
        title: t('productImport.colImage', '图'),
        key: 'newImage',
        width: 36,
        onCell: colCell('newImage'),
        render: (_, record) => {
          // 新图片是按货号推算的地址，可能 404；库里已有商品的图片作为兜底
          const matchedImage = record.matchedProduct?.productImage ? String(record.matchedProduct.productImage).trim() : ''
          const src = record.imageUrl || matchedImage
          if (!src) return <span className="pi-faint">-</span>
          return <Image src={src} alt={record.newProduct.productCode} width={28} height={28} style={{ objectFit: 'contain' }} fallback={matchedImage || BLANK_IMAGE} preview />
        },
      },
      {
        title: t('productImport.colItemNo', '货号'),
        dataIndex: ['newProduct', 'productCode'],
        key: 'productCode',
        width: 104,
        ...editableHeader('productCode'),
        render: (_, record) => renderEditable('productCode', 'text', record, { mono: true }),
      },
      {
        title: t('domesticProducts.barcode', '条码'),
        dataIndex: ['newProduct', 'barcode'],
        key: 'barcode',
        width: 126,
        ...editableHeader('barcode'),
        render: (_, record) => {
          const barcode = record.newProduct.barcode?.trim()
          // 非 EAN13 只做醒目标识，检测和保存仍按原流程放行。
          const isNonEan13Barcode = Boolean(barcode && !isValidEAN13(barcode))
          return renderEditable('barcode', 'text', record, { mono: true, warning: isNonEan13Barcode ? t('productImport.notEan13Barcode', '不是 EAN13 条码') : undefined })
        },
      },
      {
        title: t('domesticProducts.productName', '商品名称'),
        dataIndex: ['newProduct', 'productName'],
        key: 'productName',
        ...editableHeader('productName'),
        render: (_, record) => renderEditable('productName', 'text', record),
      },
      {
        title: t('domesticProducts.englishName', '英文名称'),
        dataIndex: ['newProduct', 'englishName'],
        key: 'englishName',
        ...editableHeader('englishName'),
        render: (_, record) => renderEditable('englishName', 'text', record),
      },
      {
        title: t('productImport.colDomesticPrice', '国内价'),
        dataIndex: ['newProduct', 'domesticPrice'],
        key: 'domesticPrice',
        width: 78,
        ...editableHeader('domesticPrice'),
        render: (_, record) => renderEditable('domesticPrice', 'number', record, { precision: 2 }),
      },
      {
        title: t('productImport.oemPriceCol', '零售价'),
        dataIndex: ['newProduct', 'oemPrice'],
        key: 'oemPrice',
        width: 64,
        ...editableHeader('oemPrice'),
        render: (_, record) => renderEditable('oemPrice', 'number', record, { precision: 2 }),
      },
      {
        title: <span title={t('productImport.middlePackCol', '中包数')}>{t('productImport.colMidPack', '中包')}</span>,
        dataIndex: ['newProduct', 'midPackQuantity'],
        key: 'midPackQuantity',
        width: 52,
        ...editableHeader('midPackQuantity'),
        render: (_, record) => renderEditable('midPackQuantity', 'number', record),
      },
      {
        title: <span title={t('productImport.packingQtyCol', '单件装箱数')}>{t('productImport.colCasePack', '装箱')}</span>,
        dataIndex: ['newProduct', 'casePackQuantity'],
        key: 'casePackQuantity',
        width: 52,
        ...editableHeader('casePackQuantity'),
        render: (_, record) => renderEditable('casePackQuantity', 'number', record),
      },
      {
        title: <span title={t('productImport.unitVolumeCol', '单件体积')}>{t('productImport.colVolume', '体积')}</span>,
        dataIndex: ['newProduct', 'volume'],
        key: 'volume',
        width: 68,
        ...editableHeader('volume'),
        render: (_, record) => renderEditable('volume', 'number', record, { step: '0.001', precision: 3 }),
      },
    ]

    if (showStatistics) {
      // 检测后：旧值已并入各单元格，这里只剩一列「状态」（含变化字段 / 重复行号 / 错误原因）。
      const fieldLabels: Record<ImportDiffField, string> = {
        productName: t('productImport.fieldProductName', '名称'),
        englishName: t('productImport.fieldEnglishName', '英文名'),
        barcode: t('productImport.fieldBarcode', '条码'),
        domesticPrice: t('productImport.colDomesticPrice', '国内价'),
        oemPrice: t('productImport.oemPriceCol', '零售价'),
        midPackQuantity: t('productImport.colMidPack', '中包'),
        casePackQuantity: t('productImport.colCasePack', '装箱'),
        volume: t('productImport.colVolume', '体积'),
      }
      base.push({
        title: t('productImport.colStatus', '状态'),
        key: 'status',
        width: 126,
        onCell: colCell('status'),
        render: (_, record) => {
          const kind = resolveImportRowKind(record, detectedRowIds)
          let tag: React.ReactNode = null
          let note: string | null = null
          switch (kind) {
            case 'new':
              tag = <span className="pi-tag pi-tag-new">{t('productImport.rowNew', '新')}</span>
              break
            case 'updated': {
              const changed = getChangedImportFields(record)
              tag = <span className="pi-tag pi-tag-updated">{t('productImport.rowUpdated', '更新')}</span>
              if (changed.length > 0) {
                note = changed.length > 2
                  ? t('productImport.rowChangedMore', '{{first}} 等 {{count}} 项', { first: fieldLabels[changed[0]], count: changed.length })
                  : changed.map((field) => fieldLabels[field]).join('、')
              }
              break
            }
            case 'unchanged':
              tag = <span className="pi-tag pi-tag-unchanged">{t('productImport.rowUnchanged', '无变化')}</span>
              break
            case 'duplicate': {
              tag = <span className="pi-tag pi-tag-duplicate">{t('productImport.rowDuplicate', '重复')}</span>
              const partners = getDuplicatePartnerRowNumbers(duplicateIndex, record, rowNumberById.get(record.id) ?? 0)
              if (partners.length > 0) note = t('productImport.rowDuplicateWith', '与第 {{rows}} 行', { rows: partners.join('、') })
              break
            }
            case 'dbDuplicate':
              tag = <span className="pi-tag pi-tag-dbDuplicate">{t('productImport.rowDbDuplicate', '库内重复')}</span>
              break
            case 'error':
              tag = <span className="pi-tag pi-tag-error">{t('productImport.rowError', '错误')}</span>
              note = t('productImport.rowErrorItemNoEmpty', '货号为空')
              break
            case 'pending':
              note = t('productImport.rowPending', '未检测')
              break
            default:
              break
          }
          return (
            <div className="pi-status">
              {tag}
              {note ? <span className="pi-status-note" title={note}>{note}</span> : null}
              {record.sentToContainer ? <span className="pi-tag pi-tag-sent">{t('productImport.rowSent', '已发货柜')}</span> : null}
              {renderDeleteButton(record)}
            </div>
          )
        },
      })
    } else {
      // 检测前没有状态列，只保留一个窄的删除列
      base.push({
        title: <span className="pi-visually-hidden">{t('common.action', '操作')}</span>,
        key: 'actions',
        width: 44,
        onCell: colCell('actions'),
        render: (_, record) => <div className="pi-status pi-status-actions">{renderDeleteButton(record)}</div>,
      })
    }
    return base
  }, [t, showStatistics, detectedRowIds, duplicateIndex, rowNumberById, selectedColumnKey, tableLocked, handleHeaderClick, updateProduct, removeRows])

  const getRowClassName = (row: ProductImportItem) => getImportRowClassName(resolveImportRowKind(row, detectedRowIds), row.sentToContainer === true)

  /* ------------------------------ 工具栏 ------------------------------ */

  const supplierControl = (
    <Select
      className="pi-supplier-select"
      size="small"
      style={{ width: 240 }}
      placeholder={loadingSuppliers ? t('common.loading', '加载中...') : t('productImport.selectSupplierPlaceholder', '选择供应商')}
      value={state.supplier || undefined}
      onChange={handleSupplierChange}
      showSearch
      disabled={busy}
      loading={loadingSuppliers}
      filterOption={(input, option) => {
        const keyword = input.trim().toLowerCase()
        if (!keyword) return true
        const searchText = String(option?.searchText || option?.label || option?.value || '').toLowerCase()
        return searchText.includes(keyword)
      }}
      options={supplierOptions}
    />
  )

  const selectedColumnEditable = selectedColumnKey ? toEditableColumn(selectedColumnKey) : null
  const columnLabels: Record<ImportEditableColumn, string> = {
    quantity: t('productImport.quantity', '件数'),
    productCode: t('productImport.colItemNo', '货号'),
    barcode: t('domesticProducts.barcode', '条码'),
    productName: t('domesticProducts.productName', '商品名称'),
    englishName: t('domesticProducts.englishName', '英文名称'),
    domesticPrice: t('productImport.colDomesticPrice', '国内价'),
    oemPrice: t('productImport.oemPriceCol', '零售价'),
    midPackQuantity: t('productImport.colMidPack', '中包'),
    casePackQuantity: t('productImport.colCasePack', '装箱'),
    volume: t('productImport.colVolume', '体积'),
  }
  const moreMenuItems: MenuProps['items'] = [
    {
      key: 'updateNames',
      icon: <EditOutlined />,
      label: syncProductNamesToHq
        ? t('productImport.menuUpdateNamesWithHq', '更新主表名称并同步 HQ（选中 {{count}} 行）', { count: state.selectedIds.length })
        : t('productImport.menuUpdateNames', '更新主表名称（选中 {{count}} 行）', { count: state.selectedIds.length }),
      title: t('productImport.updateHbwebProductNamesTooltip', '把已选行英文名称写入 HBweb 商品主表 Product.ProductName'),
      disabled: state.selectedIds.length === 0 || busy,
    },
    { key: 'syncHbSales', label: t('productImport.syncToHBSales', '同步到HBSales'), disabled: state.selectedIds.length === 0 || busy },
    { key: 'sendHq', label: t('productImport.menuSendToHq', '发送到 HQ'), disabled: state.selectedIds.length === 0 || busy },
    { type: 'divider' },
    { key: 'deleteSelected', danger: true, icon: <DeleteOutlined />, label: t('productImport.menuDeleteSelected', '删除选中行（{{count}}）', { count: state.selectedIds.length }), disabled: state.selectedIds.length === 0 || busy },
    {
      key: 'clearColumn',
      danger: true,
      label: selectedColumnEditable
        ? t('productImport.menuClearColumn', '清空所选列「{{column}}」', { column: columnLabels[selectedColumnEditable] })
        : t('productImport.menuClearColumnHint', '清空整列（先点击列头选中）'),
      disabled: !selectedColumnEditable || busy,
    },
  ]

  const handleMoreMenuClick: MenuProps['onClick'] = ({ key }) => {
    if (key === 'updateNames') handleUpdateHbwebProductNames()
    else if (key === 'syncHbSales') handleSyncToHBSales()
    else if (key === 'sendHq') handleSendToHq()
    else if (key === 'deleteSelected') deleteSelectedRows()
    else if (key === 'clearColumn') handleClearColumn()
  }

  const detectEmphasis = !showStatistics || state.needsDetection || duplicateGroups.length > 0
  const moreBusy = busyAction === 'names' || busyAction === 'hbsales' || busyAction === 'hq'

  return (
    <PageContainer
      compact
      // 标题与侧栏菜单 / 标签页同一份文案，避免两处写法漂移
      title={t('menu.productImport', '商品导入')}
      subtitle={t('productImport.pageHeadingSubtitle', '国内商品批量建档 / 更新')}
      extra={<Typography.Text type="secondary" className="pi-data-hint">{t('productImport.dataLossHint', '数据仅保存在当前页面，刷新会丢失')}</Typography.Text>}
    >
      <div className="pi-card" data-testid="product-import-card">
        <div className="pi-head" ref={headRef}>
          <ImportStepper steps={steps} supplierControl={supplierControl} detectedAt={detectedAt} />

          <div className="pi-toolbar" data-testid="product-import-toolbar">
            <div className="pi-group">
              <Button size="small" icon={<PlusOutlined />} onClick={() => addEmptyRows(10)} disabled={busy}>{t('productImport.addRowsButton', '{{count}} 行', { count: 10 })}</Button>
              <Button size="small" icon={<TranslationOutlined />} onClick={() => void handleBatchTranslate()} loading={busyAction === 'translate'} disabled={busy || state.products.length === 0}>
                {state.selectedIds.length > 0 ? t('productImport.translateSelected', '翻译选中') : t('productImport.translateAll', '翻译全部')}
              </Button>
              <Button size="small" danger onClick={deleteAllRows} disabled={state.products.length === 0 || busy}>{t('productImport.deleteAll', '删除全部')}</Button>
            </div>
            <span className="pi-sep" aria-hidden="true" />
            <Tooltip title={state.supplier ? undefined : t('productImport.selectSupplierFirst', '请先选择供应商')}>
              <Button
                size="small"
                type={detectEmphasis ? 'primary' : 'default'}
                icon={<ReloadOutlined />}
                onClick={() => void handleDetect()}
                loading={busyAction === 'detect'}
                disabled={!state.supplier || (busy && busyAction !== 'detect')}
              >
                {showStatistics ? t('productImport.redetect', '重新检测') : t('productImport.detectMatch', '检测匹配')}
              </Button>
            </Tooltip>

            <span className="pi-spacer" />

            <Tooltip title={t('productImport.syncHqNamesTooltip', '勾选后，「更多操作」里的「更新主表名称」会把英文名同时写入总部（HQ）商品字典；不影响「更新 / 新建」。')}>
              <span className="pi-hq-check">
                <Space size="small" wrap={false}>
                  <Checkbox
                    checked={syncProductNamesToHq}
                    onChange={(event) => setSyncProductNamesToHq(event.target.checked)}
                    disabled={busy}
                  >
                    {t('productImport.syncHqNamesLabel', '同步更新总部英文名')}
                  </Checkbox>
                </Space>
              </span>
            </Tooltip>
            <Tooltip title={commitBlockedReason || undefined}>
              <div className="pi-group">
                <Button size="small" icon={<UploadOutlined />} onClick={() => void handleBatchUpdate()} loading={busyAction === 'update'} disabled={Boolean(commitBlockedReason) || counts.updated === 0 || (busy && busyAction !== 'update')}>
                  {t('productImport.updateCountButton', '更新 {{count}} 条', { count: counts.updated })}
                </Button>
                <Button size="small" type="primary" icon={<UploadOutlined />} onClick={() => void handleBatchCreate()} loading={busyAction === 'create'} disabled={Boolean(commitBlockedReason) || counts.new === 0 || (busy && busyAction !== 'create')}>
                  {t('productImport.createCountButton', '新建 {{count}} 条', { count: counts.new })}
                </Button>
              </div>
            </Tooltip>
            <span className="pi-sep" aria-hidden="true" />
            <div className="pi-group">
              <Select
                size="small"
                className="pi-container-select"
                style={{ width: 190 }}
                prefix={<span className="pi-sub">{t('productImport.containerPrefix', '货柜')}</span>}
                placeholder={loadingContainers ? t('common.loading', '加载中...') : t('productImport.selectContainer', '请选择货柜')}
                value={selectedContainerId || undefined}
                onChange={(v) => setSelectedContainerId(v)}
                loading={loadingContainers}
                disabled={busy}
                showSearch
                optionFilterProp="label"
                onOpenChange={(open) => {
                  if (open) void loadContainers()
                }}
                options={containerOptions}
              />
              <Button size="small" icon={<SendOutlined />} onClick={handleSendSelectedClick} loading={busyAction === 'send'} disabled={state.selectedIds.length === 0 || (busy && busyAction !== 'send')}>
                {state.selectedIds.length > 0
                  ? t('productImport.sendSelectedCount', '发送选中（{{count}}）', { count: state.selectedIds.length })
                  : t('productImport.sendSelected', '发送选中')}
              </Button>
              <Dropdown menu={{ items: moreMenuItems, onClick: handleMoreMenuClick }} trigger={['click']}>
                <Button size="small" icon={moreBusy ? <LoadingOutlined /> : <EllipsisOutlined />} aria-label={t('productImport.moreActions', '更多操作')} aria-busy={moreBusy || undefined} />
              </Dropdown>
            </div>
          </div>

          {showChips ? <ImportStatusChips counts={counts} active={statusFilter} onChange={setStatusFilter} /> : null}
        </div>

        <div className="pi-table-wrap" ref={productImportTableRef} data-testid="product-import-table">
          <MeasuredTable metricId="domestic-purchase.product-import.table-1"
            className="product-import-table"
            style={{ '--product-import-table-body-height': `${tableScrollY}px` } as React.CSSProperties}
            columns={columns}
            dataSource={visibleProducts}
            rowKey="id"
            size="small"
            scroll={{ x: PRODUCT_IMPORT_TABLE_SCROLL_X, y: tableScrollY }}
            rowClassName={getRowClassName}
            // 检测期间表格显示遮罩；其它动作（入库、发送…）只锁定编辑，不盖遮罩
            loading={busyAction === 'detect'}
            locale={{ emptyText: statusFilter === 'all' ? t('productImport.emptyTable', '没有数据，点击「+ 10 行」或从 Excel 粘贴') : t('productImport.filterEmpty', '没有符合当前筛选的行') }}
            rowSelection={{ selectedRowKeys: state.selectedIds,
              columnWidth: 52,
              // 过滤后勾选的行不能因为切换过滤而丢失
              preserveSelectedRowKeys: true,
              getCheckboxProps: () => ({ disabled: tableLocked }),
              renderCell: (_checked, record, _index, originNode) => (
                <div className="pi-sel-cell">
                  <span className="pi-rowno">{rowNumberById.get(record.id)}</span>
                  {originNode}
                </div>
              ),
              onChange: (keys) => setState((prev) => ({ ...prev, selectedIds: keys as string[] })),
            }}
            pagination={false}
          />
        </div>

        <div className="pi-footer" ref={footerRef} data-testid="product-import-footer">
          <div className="pi-footer-main">
            {importErrors.length > 0 ? (
              <span className="pi-footer-errors" role="status">
                <WarningOutlined />
                <b>{t('productImport.footerErrorCount', '{{count}} 个错误', { count: importErrors.length })}</b>
                <span>：</span>
                {importErrors.slice(0, 5).map((error, index) => (
                  <span key={error.rowId}>
                    {index > 0 ? '、' : null}
                    <button type="button" className="pi-link" onClick={() => jumpToRow(error.rowId)} title={t('productImport.footerJumpHint', '定位到该行')}>
                      {t('productImport.footerRowLabel', '第 {{row}} 行', { row: error.rowNumber })}
                    </button>
                  </span>
                ))}
                {importErrors.length > 5 ? <span>{t('productImport.footerMoreRows', ' 等 {{count}} 行', { count: importErrors.length })}</span> : null}
                <span>{t('productImport.footerErrorReason', '货号为空')}</span>
                <span className="pi-sub">{t('productImport.footerErrorNote', '（已在表格里标红）；新建 / 更新会跳过错误行；零售价可留空。')}</span>
              </span>
            ) : (
              <span className="pi-sub">{t('productImport.footerPasteHint', '点击单元格或列头后按 Ctrl+V，可粘贴 Excel 的多行多列数据')}</span>
            )}
          </div>
          <div className="pi-footer-side">
            {state.selectedIds.length > 0 ? (
              <span>{t('productImport.footerSelected', '已选 {{count}} 行 · 件数 {{quantity}} · 体积 {{volume}} m³', { count: selectionStats.selectedCount, quantity: selectionStats.totalQuantity, volume: selectionStats.totalVolume.toFixed(3) })}</span>
            ) : null}
            {showStatistics ? <span>{t('productImport.footerDiffHint', '变化的单元格用浅黄底，并在下方显示原值')}</span> : null}
          </div>
        </div>
      </div>

      <DuplicateDialog open={duplicateDialogOpen} duplicateGroups={duplicateGroups} onClose={() => setDuplicateDialogOpen(false)} onConfirm={handleMergeDuplicates} />
      <ConflictResolutionDialog
        open={conflictDialogOpen}
        containerCode={pendingSend?.containerId}
        conflicts={conflictRows}
        totalSendCount={pendingSend?.products.length ?? 0}
        confirming={busyAction === 'send'}
        onClose={() => { setConflictDialogOpen(false); setPendingSend(null) }}
        onConfirm={(selection) => void handleResolveConflicts(selection)}
      />
    </PageContainer>
  )
}
