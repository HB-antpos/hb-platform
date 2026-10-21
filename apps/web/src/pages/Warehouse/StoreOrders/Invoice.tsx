import { DownloadOutlined, FileExcelOutlined, MailOutlined, PrinterOutlined, RollbackOutlined } from '@ant-design/icons'
import { Button, Empty, Image, Input, Modal, Segmented, Select, Space, Spin, Switch, message } from 'antd'
import type { TFunction } from 'i18next'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import BarcodePreview from '../../../components/BarcodePreview'
import { useDynamicTabTitle } from '../../../hooks/useDynamicTabTitle'
import { useStableRouteContext } from '../../../hooks/useStableRouteContext'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getInvoiceEmailSenderAccounts } from '../../../services/invoiceEmailSettingsService'
import { getStores } from '../../../services/storeService'
import {
  getStoreOrderDetail,
  getStoreOrderInvoiceEmailJob,
  sendStoreOrderInvoiceEmail,
  translateStoreOrderInvoiceEmailText,
  updateStoreOrderStoreContact,
} from '../../../services/storeOrderService'
import type { InvoiceEmailSenderAccountDto } from '../../../types/invoiceEmailSettings'
import type { StoreDto } from '../../../types/store'
import type { StoreOrderDetail, StoreOrderDetailLine } from '../../../types/storeOrder'
import { shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'
import { shouldShowStoreOrderDetailInitialLoading } from './detailLoadState'
import {
  StoreOrderInvoiceEmailPollingTimeoutError,
  createStoreOrderInvoiceEmailJobPoller,
} from './invoiceEmailJobPolling'
import { formatInvoiceEmailSenderAccountLabel, resolveInvoiceEmailSenderAccountId } from './invoiceEmailSender'
import { InvoiceEmailSentStatusText } from './invoiceEmailSentInfo'
import {
  buildDocumentFileName,
  collectElementBreakOffsets,
  downloadElementAsPdf,
  formatDocumentFileDate,
  formatCurrency,
  formatPrintDate,
} from './printUtils'
import {
  computeStoreOrderDocumentTotals,
  isStoreOrderReturnLine,
  resolveStoreOrderDocumentTypeOf,
  splitStoreOrderInvoiceLines,
} from './storeOrderReturnLogic'
import storeOrderInvoiceMessagesEn from './storeOrderInvoiceMessages.en.json'
import storeOrderInvoiceMessagesZh from './storeOrderInvoiceMessages.zh.json'
import './print.css'

// 退货 / 退款单新增的文案随页面代码块懒注册，不进首屏 i18n 包（首屏 gzip 预算很紧）。
registerPageMessages({ zh: storeOrderInvoiceMessagesZh, en: storeOrderInvoiceMessagesEn })

const TRANSPARENT_IMAGE_FALLBACK = 'data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs='
const EMAIL_REGEXP = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
type InvoiceEmailModalLanguage = 'zh' | 'en'

interface InvoiceExcelHeaderInfo {
  storeName: string
  storeContact: string
  storeAddress: string
  invoiceDateText: string
  invoiceFileDate: string
}

function normalizeInvoiceEmailModalLanguage(language?: string): InvoiceEmailModalLanguage {
  return language === 'en' || language?.startsWith('en-') ? 'en' : 'zh'
}

function sortInvoiceItems(items: StoreOrderDetailLine[]) {
  return [...items].sort((left, right) => {
    const leftAllocQuantity = Number(left.allocQuantity ?? 0)
    const rightAllocQuantity = Number(right.allocQuantity ?? 0)
    const leftIsZero = leftAllocQuantity === 0
    const rightIsZero = rightAllocQuantity === 0

    if (leftIsZero !== rightIsZero) {
      return leftIsZero ? 1 : -1
    }

    const itemNumberCompare = (left.itemNumber || '').localeCompare(right.itemNumber || '', 'zh-CN', {
      numeric: true,
      sensitivity: 'base',
    })
    if (itemNumberCompare !== 0) {
      return itemNumberCompare
    }

    return (left.productCode || '').localeCompare(right.productCode || '', 'zh-CN', {
      numeric: true,
      sensitivity: 'base',
    })
  })
}

function formatOptionalCurrency(value?: number | null) {
  // RRP 为空代表主数据缺失，不能误显示成真实的 $0.00。
  return value === undefined || value === null ? '--' : formatCurrency(value)
}

/** 退货原因的单据文案（英文，客户可见）；写成字面量映射，页面消息契约测试才能静态核对键。 */
function resolveInvoiceReturnReasonLabel(t: TFunction, reason?: string | null) {
  switch (reason) {
    case 'damaged':
      return t('warehouseUi.storeOrderInvoice.reason.damaged')
    case 'wrongItem':
      return t('warehouseUi.storeOrderInvoice.reason.wrongItem')
    case 'overShipped':
      return t('warehouseUi.storeOrderInvoice.reason.overShipped')
    case 'quality':
      return t('warehouseUi.storeOrderInvoice.reason.quality')
    case 'nearExpiry':
      return t('warehouseUi.storeOrderInvoice.reason.nearExpiry')
    case 'other':
      return t('warehouseUi.storeOrderInvoice.reason.other')
    default:
      return ''
  }
}

function formatInvoiceReasonLine(t: TFunction, item: StoreOrderDetailLine) {
  const reason = resolveInvoiceReturnReasonLabel(t, item.returnReason)
  return reason ? t('warehouseUi.storeOrderInvoice.reasonLine', { reason }) : ''
}

async function downloadInvoiceExcel(
  order: StoreOrderDetail,
  items: StoreOrderDetailLine[],
  headerInfo: InvoiceExcelHeaderInfo,
  t: TFunction,
) {
  const { default: ExcelJS } = await import('exceljs')
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet(t('warehouse.invoice.excel.sheetName'))

  worksheet.columns = [
    { key: 'index', width: 8 },
    { key: 'itemNumber', width: 18 },
    { key: 'productName', width: 28 },
    { key: 'barcode', width: 20 },
    { key: 'importPrice', width: 14 },
    { key: 'rrp', width: 12 },
    { key: 'orderQuantity', width: 12 },
    { key: 'allocQuantity', width: 12 },
    { key: 'subtotal', width: 16 },
  ]

  // 净额 < 0 整张单据改为退款单（Adjustment Note）；有退货行时发货与退货分段列出。
  const documentType = resolveStoreOrderDocumentTypeOf(order)
  const isCreditNote = documentType === 'creditNote'
  const { supplied: suppliedItems, returned: returnedItems } = splitStoreOrderInvoiceLines(items)
  const hasReturns = returnedItems.length > 0
  const titleRow = worksheet.addRow([
    isCreditNote
      ? t('warehouseUi.storeOrderInvoice.docTitleCredit')
      : hasReturns
        ? t('warehouseUi.storeOrderInvoice.docTitleInvoice')
        : 'INVOICE',
  ])
  titleRow.font = { bold: true, size: 14 }
  worksheet.mergeCells(titleRow.number, 1, titleRow.number, 9)

  worksheet.addRow([
    isCreditNote
      ? t('warehouseUi.storeOrderInvoice.docNoCredit', { orderNo: order.orderNo || order.orderGUID })
      : t('warehouse.invoice.invoiceNo', { orderNo: order.orderNo || order.orderGUID }),
    '',
    '',
    '',
    isCreditNote
      ? t('warehouseUi.storeOrderInvoice.docDateCredit', { date: headerInfo.invoiceDateText })
      : t('warehouse.invoice.invoiceDate', { date: headerInfo.invoiceDateText }),
  ])
  worksheet.mergeCells(2, 1, 2, 4)
  worksheet.mergeCells(2, 5, 2, 9)
  worksheet.addRow([t('warehouse.invoice.customer'), headerInfo.storeName])
  worksheet.addRow([t('warehouse.invoice.customerContact'), headerInfo.storeContact])
  worksheet.addRow([t('warehouse.invoice.address'), headerInfo.storeAddress])
  worksheet.addRow([])

  // Excel 页头固定写在明细前，方便邮件附件和手动导出的核对口径一致。
  const tableHeaderRow = worksheet.addRow([
    '#',
    t('warehouse.invoice.excel.itemNo'),
    t('warehouse.invoice.excel.name'),
    t('warehouse.invoice.excel.barcode'),
    t('warehouse.invoice.excel.cost'),
    t('warehouse.invoice.excel.rrp'),
    t('warehouse.invoice.excel.orderQty'),
    t('warehouse.invoice.excel.shipQty'),
    t('warehouse.invoice.excel.subtotal'),
  ])
  tableHeaderRow.font = { bold: true }

  let rowIndex = 0
  const addItemRow = (item: StoreOrderDetailLine) => {
    const orderQuantity = Number(item.quantity || 0)
    const allocQuantity = Number(item.allocQuantity ?? 0)
    const allocatedImportAmount = Number(item.allocatedImportAmount ?? allocQuantity * Number(item.importPrice || 0))
    const reasonLine = isStoreOrderReturnLine(item) ? formatInvoiceReasonLine(t, item) : ''
    rowIndex += 1
    worksheet.addRow({
      index: rowIndex,
      itemNumber: item.itemNumber || '',
      productName: reasonLine ? `${item.productName || ''} (${reasonLine})` : item.productName || '',
      barcode: item.barcode || item.productCode,
      importPrice: Number(item.importPrice || 0),
      rrp: item.rrp ?? null,
      orderQuantity,
      allocQuantity,
      subtotal: Number(allocatedImportAmount.toFixed(2)),
    })
  }
  const addSectionRow = (title: string) => {
    const sectionRow = worksheet.addRow([title])
    sectionRow.font = { bold: true }
    worksheet.mergeCells(sectionRow.number, 1, sectionRow.number, 9)
  }
  if (hasReturns) {
    // 发货在前、退货（调整项）在后；净额为负的退款单没有发货行时只剩「Returned goods」一段。
    if (suppliedItems.length > 0) {
      addSectionRow(t('warehouseUi.storeOrderInvoice.sectionSupplied'))
      suppliedItems.forEach(addItemRow)
    }
    addSectionRow(
      suppliedItems.length > 0 && !isCreditNote
        ? t('warehouseUi.storeOrderInvoice.sectionAdjustments')
        : t('warehouseUi.storeOrderInvoice.sectionReturned'),
    )
    returnedItems.forEach(addItemRow)
  } else {
    items.forEach(addItemRow)
  }

  const totals = computeStoreOrderDocumentTotals(order.totalAllocatedImportAmount ?? order.totalImportAmount ?? 0, order.shippingFee)
  const subTotal = totals.subTotal
  const gst = totals.gst
  const freight = totals.freight
  const total = totals.total

  worksheet.addRow({})
  worksheet.addRow({ productName: t('warehouse.invoice.subTotal'), subtotal: subTotal })
  worksheet.addRow({ productName: t('warehouse.invoice.gst'), subtotal: gst })
  worksheet.addRow({ productName: t('warehouse.invoice.freight'), subtotal: freight })
  worksheet.addRow({
    productName: isCreditNote ? t('warehouseUi.storeOrderInvoice.totalCredit') : t('warehouse.invoice.total'),
    subtotal: total,
  })
  worksheet.addRow({})
  worksheet.addRow({ productName: t('warehouse.invoice.remarks'), barcode: t('warehouse.invoice.imageRefNote') })

  worksheet.getColumn('importPrice').numFmt = '$#,##0.00'
  worksheet.getColumn('rrp').numFmt = '$#,##0.00'
  worksheet.getColumn('subtotal').numFmt = '$#,##0.00'

  const buffer = await workbook.xlsx.writeBuffer()
  const blob = new Blob([buffer], {
    type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = buildDocumentFileName(
    isCreditNote ? t('warehouseUi.storeOrderInvoice.excelFileNameCredit') : 'INVOICE',
    headerInfo.storeName || order.storeCode,
    order.orderNo || order.orderGUID,
    'xlsx',
    {
      unknownStore: t('warehouse.invoice.unknownStore'),
      unknownOrder: t('warehouse.invoice.unknownOrder'),
    },
    headerInfo.invoiceFileDate,
  )
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)
  URL.revokeObjectURL(url)
}

export default function StoreOrderInvoicePage() {
  const { t, i18n } = useTranslation()
  const route = useStableRouteContext()
  const id = route?.params.id || ''
  const navigate = useNavigate()
  const printRootRef = useRef<HTMLDivElement | null>(null)
  // 记录当前发票已完成首次加载，保活 Tab 恢复时避免同订单自动刷新。
  const loadedOrderIdRef = useRef<string | null>(null)
  const visibleOrderIdRef = useRef<string | null>(null)
  const stopInvoiceEmailPollingRef = useRef<(() => void) | null>(null)
  const [loading, setLoading] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [exportingExcel, setExportingExcel] = useState(false)
  const [emailModalOpen, setEmailModalOpen] = useState(false)
  const [sendingEmail, setSendingEmail] = useState(false)
  const [order, setOrder] = useState<StoreOrderDetail | null>(null)
  const [store, setStore] = useState<StoreDto | null>(null)
  const [recipientEmail, setRecipientEmail] = useState('')
  const [emailSubject, setEmailSubject] = useState('')
  const [emailBody, setEmailBody] = useState('')
  const [saveAsStoreDefault, setSaveAsStoreDefault] = useState(true)
  const [emailModalLanguage, setEmailModalLanguage] = useState<InvoiceEmailModalLanguage>('zh')
  const [emailSubjectTouched, setEmailSubjectTouched] = useState(false)
  const [emailBodyTouched, setEmailBodyTouched] = useState(false)
  const [translatingEmailText, setTranslatingEmailText] = useState(false)
  const [senderAccounts, setSenderAccounts] = useState<InvoiceEmailSenderAccountDto[]>([])
  const [senderAccountId, setSenderAccountId] = useState<string | undefined>()
  const [loadingSenderAccounts, setLoadingSenderAccounts] = useState(false)
  // 每次打开弹窗都重新拉取发件账号，只采纳最后一次请求的结果，避免快速开关弹窗时旧响应覆盖新列表。
  const senderAccountsRequestRef = useRef(0)

  // 单据类型按净额：净额 < 0 为退款单（Adjustment Note），否则仍是发票；优先用后端给的 documentType。
  const documentType = resolveStoreOrderDocumentTypeOf(order ?? {})
  const isCreditNote = documentType === 'creditNote'

  useDynamicTabTitle(
    order?.orderNo
      ? isCreditNote
        ? t('warehouseUi.storeOrderInvoice.creditTitleWithStore', { storeName: store?.storeName || order.storeCode || t('warehouse.invoice.unknownStore'), orderNo: order.orderNo })
        : t('warehouse.invoice.titleWithStore', { storeName: store?.storeName || order.storeCode || t('warehouse.invoice.unknownStore'), orderNo: order.orderNo })
      : isCreditNote
        ? t('warehouseUi.storeOrderInvoice.creditTitle')
        : t('warehouse.invoice.title'),
  )

  useEffect(() => {
    if (!id) {
      return
    }

    const load = async (showLoading = true) => {
      if (showLoading) {
        setLoading(true)
      }
      try {
        const detail = await getStoreOrderDetail(id)
        if (!detail) {
          if (showLoading) {
            loadedOrderIdRef.current = null
            visibleOrderIdRef.current = null
            setOrder(null)
            setStore(null)
          }
          message.error(t('storeOrders.detail.notFound'))
          return
        }

        loadedOrderIdRef.current = detail.orderGUID || id
        visibleOrderIdRef.current = detail.orderGUID || id
        setOrder(detail)

        if (detail.storeCode) {
          const storeResult = await getStores({
            search: detail.storeCode,
            page: 1,
            pageSize: 1,
          })
          setStore(storeResult.items[0] ?? null)
        } else {
          setStore(null)
        }
      } catch (error) {
        console.error(error)
        const errorMessage = error instanceof Error ? error.message : t('warehouse.invoice.loadFailed')
        if (showLoading) {
          visibleOrderIdRef.current = null
          setOrder(null)
          setStore(null)
        }
        message.error(errorMessage)
      } finally {
        if (showLoading) {
          setLoading(false)
        }
      }
    }

    // 保活 Tab 切回同订单且页面仍有可见内容时，直接复用现有发票数据。
    if (shouldSkipDetailAutoReload({
      requestedDetailId: id,
      loadedDetailId: loadedOrderIdRef.current,
      visibleDetailId: visibleOrderIdRef.current,
    })) {
      return
    }

    const shouldShowInitialLoading = shouldShowStoreOrderDetailInitialLoading({
      requestedOrderId: id,
      loadedOrderId: loadedOrderIdRef.current,
      visibleDetailId: visibleOrderIdRef.current,
    })
    void load(shouldShowInitialLoading)
  }, [id])

  useEffect(() => {
    return () => {
      stopInvoiceEmailPollingRef.current?.()
      stopInvoiceEmailPollingRef.current = null
    }
  }, [])

  // 小计 = 净额（发货 + 退货冲减）；GST 按净额算（净额为负时同向冲减），与后端同一口径。
  const totals = useMemo(
    () => computeStoreOrderDocumentTotals(order?.totalAllocatedImportAmount ?? order?.totalImportAmount ?? 0, order?.shippingFee),
    [order?.shippingFee, order?.totalAllocatedImportAmount, order?.totalImportAmount],
  )

  const sortedItems = useMemo(() => sortInvoiceItems(order?.items || []), [order?.items])
  // 退货行（发货数为负）单独成段；没有退货行时 returnedItems 为空，页面与改动前一致。
  const { supplied: suppliedItems, returned: returnedItems } = useMemo(
    () => splitStoreOrderInvoiceLines(sortedItems),
    [sortedItems],
  )
  const hasReturnLines = returnedItems.length > 0
  const defaultRecipientEmail = order ? order.storeContactEmail || store?.contactEmail || '' : ''
  const getEmailStoreName = (language: InvoiceEmailModalLanguage) =>
    store?.storeName || order?.storeCode || t('warehouse.invoice.unknownStore', { lng: language })
  const buildDefaultEmailSubject = (language: InvoiceEmailModalLanguage) =>
    order && isCreditNote
      ? t('warehouseUi.storeOrderInvoice.creditEmailSubject', {
          lng: language,
          storeName: getEmailStoreName(language),
          orderNo: order.orderNo || order.orderGUID,
        })
      : order
      ? t('warehouse.invoice.defaultEmailSubject', {
          lng: language,
          storeName: getEmailStoreName(language),
          orderNo: order.orderNo || order.orderGUID,
        })
      : ''
  const buildDefaultEmailBody = (language: InvoiceEmailModalLanguage) =>
    order && isCreditNote
      ? t('warehouseUi.storeOrderInvoice.creditEmailBody', {
          lng: language,
          storeName: getEmailStoreName(language),
          orderNo: order.orderNo || order.orderGUID,
        })
      : order
      ? t('warehouse.invoice.defaultEmailBody', {
          lng: language,
          storeName: getEmailStoreName(language),
          orderNo: order.orderNo || order.orderGUID,
        })
      : ''
  const invoiceDateSource = order?.outboundDate || order?.orderDate
  const invoiceDateText = formatPrintDate(invoiceDateSource, false, i18n.language)
  const invoiceFileDate = formatDocumentFileDate(invoiceDateSource)

  const resolveInvoicePdfFileName = () =>
    buildDocumentFileName(
      isCreditNote ? t('warehouseUi.storeOrderInvoice.creditFileName') : t('warehouse.invoice.fileName'),
      store?.storeName || order?.storeCode,
      order?.orderNo || order?.orderGUID,
      'pdf',
      {
        unknownStore: t('warehouse.invoice.unknownStore'),
        unknownOrder: t('warehouse.invoice.unknownOrder'),
      },
      invoiceFileDate,
    )

  const getInvoicePdfBreakOffsets = () => {
    if (!printRootRef.current) {
      return []
    }

    return collectElementBreakOffsets(
      printRootRef.current,
      '.store-order-invoice-table tbody tr',
      '.store-order-invoice-footer',
    )
  }

  const handlePrint = () => {
    window.print()
  }

  const handleDownloadPdf = async () => {
    if (!printRootRef.current || !order) {
      return
    }

    setDownloading(true)
    try {
      await downloadElementAsPdf(
        printRootRef.current,
        resolveInvoicePdfFileName(),
        {
          createCanvasContextErrorMessage: t('warehouse.invoice.createPdfCanvasFailed'),
          avoidBreakOffsets: getInvoicePdfBreakOffsets(),
        },
      )
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.invoice.downloadPdfFailed'))
    } finally {
      setDownloading(false)
    }
  }

  const handleOpenEmailModal = () => {
    const initialLanguage = normalizeInvoiceEmailModalLanguage(i18n.language)
    setEmailModalLanguage(initialLanguage)
    setRecipientEmail(defaultRecipientEmail)
    setEmailSubject(buildDefaultEmailSubject(initialLanguage))
    setEmailBody(buildDefaultEmailBody(initialLanguage))
    setEmailSubjectTouched(false)
    setEmailBodyTouched(false)
    setSaveAsStoreDefault(true)
    setEmailModalOpen(true)
    void loadSenderAccounts()
  }

  const loadSenderAccounts = async () => {
    const requestId = senderAccountsRequestRef.current + 1
    senderAccountsRequestRef.current = requestId
    setLoadingSenderAccounts(true)
    try {
      const accounts = await getInvoiceEmailSenderAccounts()
      if (senderAccountsRequestRef.current !== requestId) {
        return
      }
      setSenderAccounts(accounts)
      setSenderAccountId((current) => resolveInvoiceEmailSenderAccountId(accounts, current))
    } catch (error) {
      if (senderAccountsRequestRef.current !== requestId) {
        return
      }
      // 列表拉取失败不阻断发送：清空选择后不传 fromAccountId，由后端使用默认发件账号。
      console.error(error)
      setSenderAccounts([])
      setSenderAccountId(undefined)
      message.warning(t('warehouse.invoice.senderAccountsLoadFailed'))
    } finally {
      if (senderAccountsRequestRef.current === requestId) {
        setLoadingSenderAccounts(false)
      }
    }
  }

  const handleEmailModalLanguageChange = async (nextLanguage: InvoiceEmailModalLanguage) => {
    if (!order || nextLanguage === emailModalLanguage || translatingEmailText) {
      return
    }

    const nextSubjectTemplate = buildDefaultEmailSubject(nextLanguage)
    const nextBodyTemplate = buildDefaultEmailBody(nextLanguage)
    if (!emailSubjectTouched && !emailBodyTouched) {
      setEmailModalLanguage(nextLanguage)
      setEmailSubject(nextSubjectTemplate)
      setEmailBody(nextBodyTemplate)
      return
    }

    setTranslatingEmailText(true)
    try {
      const translated = await translateStoreOrderInvoiceEmailText({
        orderGUID: order.orderGUID,
        targetLanguage: nextLanguage,
        subject: emailSubjectTouched ? emailSubject : undefined,
        body: emailBodyTouched ? emailBody : undefined,
      })

      setEmailModalLanguage(nextLanguage)
      setEmailSubject(emailSubjectTouched ? translated.subject ?? emailSubject : nextSubjectTemplate)
      setEmailBody(emailBodyTouched ? translated.body ?? emailBody : nextBodyTemplate)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.invoice.emailTranslateFailed', { lng: emailModalLanguage }))
    } finally {
      setTranslatingEmailText(false)
    }
  }

  const pollInvoiceEmailJob = async (jobId: string, fallbackRecipientEmail?: string) => {
    stopInvoiceEmailPollingRef.current?.()

    const poller = createStoreOrderInvoiceEmailJobPoller({
      jobId,
      getJob: getStoreOrderInvoiceEmailJob,
    })
    stopInvoiceEmailPollingRef.current = poller.stop

    try {
      const result = await poller.promise
      if (result.status === 'Succeeded') {
        // 邮件发送成功后立即刷新前端提示，避免用户重新打开弹窗时仍看到旧状态。
        setOrder((current) =>
          current
            ? {
                ...current,
                invoiceEmailSentInfo: {
                  hasSent: true,
                  sentAt: result.completedAt || new Date().toISOString(),
                  toEmail: result.toEmail || fallbackRecipientEmail || current.invoiceEmailSentInfo?.toEmail,
                  jobId: result.jobId || jobId,
                },
              }
            : current,
        )
        message.success(result.message || t('warehouse.invoice.emailSendSuccess'))
        return
      }

      if (result.status === 'Failed') {
        message.error(result.message || t('warehouse.invoice.emailSendFailed'))
      }
    } catch (error) {
      if (error instanceof StoreOrderInvoiceEmailPollingTimeoutError) {
        message.warning(t('warehouse.invoice.emailJobPollingTimeout'))
        return
      }

      console.error(error)
      message.error(t('warehouse.invoice.emailJobPollingFailed'))
    } finally {
      if (stopInvoiceEmailPollingRef.current === poller.stop) {
        stopInvoiceEmailPollingRef.current = null
      }
    }
  }

  const handleSendInvoiceEmail = async () => {
    if (!order) {
      return
    }

    const normalizedRecipientEmail = recipientEmail.trim()
    if (!normalizedRecipientEmail) {
      message.warning(t('warehouse.invoice.emailRequired'))
      return
    }
    if (!EMAIL_REGEXP.test(normalizedRecipientEmail)) {
      message.warning(t('warehouse.invoice.invalidEmail'))
      return
    }

    setSendingEmail(true)
    try {
      const job = await sendStoreOrderInvoiceEmail({
        orderGUID: order.orderGUID,
        toEmail: normalizedRecipientEmail,
        subject: emailSubject.trim() || undefined,
        body: emailBody.trim() || undefined,
        fromAccountId: senderAccountId,
      })

      message.success(
        isCreditNote ? t('warehouseUi.storeOrderInvoice.creditEmailSubmitted') : t('warehouse.invoice.emailJobSubmitted'),
      )
      setEmailModalOpen(false)
      if (job.jobId) {
        void pollInvoiceEmailJob(job.jobId, normalizedRecipientEmail)
      }

      if (saveAsStoreDefault && order.storeCode) {
        await updateStoreOrderStoreContact({
          orderGUID: order.orderGUID,
          storeCode: order.storeCode,
          contactEmail: normalizedRecipientEmail,
        })

        setStore((current) => (current ? { ...current, contactEmail: normalizedRecipientEmail } : current))
        setOrder((current) => (current ? { ...current, storeContactEmail: normalizedRecipientEmail } : current))
      }
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.invoice.emailSendFailed'))
    } finally {
      setSendingEmail(false)
    }
  }

  const handleExportExcel = async () => {
    if (!order) {
      return
    }

    setExportingExcel(true)
    try {
      await downloadInvoiceExcel(
        order,
        sortedItems,
        {
          storeName: store?.storeName || order.storeCode || t('warehouse.invoice.unknownStore'),
          storeContact: order.storeContactEmail || store?.contactEmail || '-',
          storeAddress: order.storeAddress || store?.address || '--',
          invoiceDateText,
          invoiceFileDate,
        },
        t,
      )
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.invoice.exportExcelFailed'))
    } finally {
      setExportingExcel(false)
    }
  }

  if (loading) {
    return <Spin size="large" style={{ display: 'block', margin: '120px auto' }} />
  }

  if (!order) {
    return <Empty description={t('storeOrders.detail.orderDataNotFound')} style={{ marginTop: 120 }} />
  }

  const displayStoreName = store?.storeName || order.storeCode || '--'
  const storeAddress = order.storeAddress || store?.address || '--'

  // 一行明细：退货行（发货数为负）数字标红并在名称下写英文原因；其余与改动前一致。
  const renderInvoiceRow = (item: StoreOrderDetailLine, index: number) => {
    const orderQuantity = Number(item.quantity || 0)
    const allocQuantity = Number(item.allocQuantity ?? 0)
    const isReturnRow = isStoreOrderReturnLine(item)
    const reasonLine = isReturnRow ? formatInvoiceReasonLine(t, item) : ''
    return (
      <tr key={item.detailGUID}>
        <td className="col-index">{index + 1}</td>
        <td className="col-image">
          {item.productImage ? (
            <Image
              src={item.productImage}
              alt=""
              width={36}
              height={36}
              preview={false}
              fallback={TRANSPARENT_IMAGE_FALLBACK}
              style={{ objectFit: 'contain' }}
            />
          ) : (
            null
          )}
        </td>
        <td className="col-item">{item.itemNumber || '--'}</td>
        <td className="col-barcode">
          {item.barcode ? (
            <BarcodePreview
              value={item.barcode}
              showCopy={false}
              textMaxWidth={78}
              options={{ width: 0.9, height: 20, margin: 0 }}
            />
          ) : (
            item.productCode
          )}
        </td>
        <td className="col-name">
          {item.productName || '--'}
          {reasonLine ? <div className="store-order-invoice-reason">{reasonLine}</div> : null}
        </td>
        <td className="col-cost">{formatCurrency(item.importPrice)}</td>
        <td className="col-rrp">{formatOptionalCurrency(item.rrp)}</td>
        <td className="col-qty">{orderQuantity}</td>
        <td className={`col-qty${isReturnRow ? ' is-negative' : ''}`}>{allocQuantity}</td>
        <td className={`col-subtotal${isReturnRow ? ' is-negative' : ''}`}>
          {formatCurrency(item.allocatedImportAmount ?? allocQuantity * Number(item.importPrice || 0))}
        </td>
      </tr>
    )
  }

  return (
    <div className="store-order-print-page">
      <div className="store-order-print-toolbar no-print">
        <Space wrap>
          <Button icon={<RollbackOutlined />} onClick={() => navigate(-1)}>
            {t('common.back')}
          </Button>
          <Button icon={<MailOutlined />} loading={sendingEmail} onClick={handleOpenEmailModal}>
            {t('warehouse.invoice.sendEmail')}
          </Button>
          <Button icon={<FileExcelOutlined />} loading={exportingExcel} onClick={() => void handleExportExcel()}>
            {t('warehouse.invoice.exportExcel')}
          </Button>
          <Button icon={<DownloadOutlined />} loading={downloading} onClick={() => void handleDownloadPdf()}>
            {t('warehouse.invoice.downloadPdf')}
          </Button>
          <Button type="primary" icon={<PrinterOutlined />} onClick={handlePrint}>
            {isCreditNote ? t('warehouseUi.storeOrderInvoice.creditPrintButton') : t('warehouse.invoice.printInvoice')}
          </Button>
        </Space>
      </div>

      <div ref={printRootRef} className="store-order-print-root store-order-print-paper store-order-invoice-paper">
        <div className="store-order-invoice-header">
          <div className="store-order-invoice-logo">
            <Image src="/invoice-logo.png" alt="HOT BARGAIN" preview={false} style={{ maxHeight: 120, objectFit: 'contain' }} />
          </div>
          <div className="store-order-invoice-company">
            {/* 退款单：红色 ADJUSTMENT NOTE + CREDIT 印章，避免被当成普通发票付款；有退货的发票写 TAX INVOICE。 */}
            {isCreditNote || hasReturnLines ? (
              <div className="store-order-invoice-doc-heading">
                <h3 className={`store-order-invoice-doc-title${isCreditNote ? ' is-credit' : ''}`}>
                  {isCreditNote
                    ? t('warehouseUi.storeOrderInvoice.docTitleCredit')
                    : t('warehouseUi.storeOrderInvoice.docTitleInvoice')}
                </h3>
                {isCreditNote ? (
                  <span className="store-order-invoice-stamp">{t('warehouseUi.storeOrderInvoice.creditStamp')}</span>
                ) : null}
              </div>
            ) : null}
            <h4>{t('warehouse.invoice.warehouseAddress')}</h4>
            <p>3 Rogilla close Maryland, NSW, 2287, Australia</p>
            <p>
              <strong>A.B.N.</strong> 35 160 589 793
            </p>
            <p>
              <strong>{t('warehouse.invoice.warehouseEmail')}</strong> dong@hotbargain.com.au
            </p>
          </div>
        </div>

        <div className={`store-order-invoice-bar${isCreditNote ? ' is-credit' : ''}`}>
          <div>
            {isCreditNote
              ? t('warehouseUi.storeOrderInvoice.docNoCredit', { orderNo: order.orderNo || order.orderGUID })
              : t('warehouse.invoice.invoiceNo', { orderNo: order.orderNo || order.orderGUID })}
          </div>
          <div>
            {isCreditNote
              ? t('warehouseUi.storeOrderInvoice.docDateCredit', { date: invoiceDateText })
              : t('warehouse.invoice.invoiceDate', { date: invoiceDateText })}
          </div>
        </div>

        <div className="store-order-invoice-customer">
          <div className="store-order-invoice-customer-row">
            <span className="store-order-invoice-label">{t('warehouse.invoice.customer')}</span>
            <span>{displayStoreName}</span>
          </div>
          <div className="store-order-invoice-customer-row">
            <span className="store-order-invoice-label">{t('warehouse.invoice.customerContact')}</span>
            <span>{store?.contactPhone || '-'}</span>
          </div>
          <div className="store-order-invoice-customer-row">
            <span className="store-order-invoice-label">{t('warehouse.invoice.address')}</span>
            <span>{storeAddress}</span>
          </div>
          {isCreditNote ? (
            <div className="store-order-invoice-customer-row">
              <span className="store-order-invoice-label">{t('warehouseUi.storeOrderInvoice.creditReference')}</span>
              <span>{t('warehouseUi.storeOrderInvoice.creditReferenceValue')}</span>
            </div>
          ) : null}
        </div>

        <table className="store-order-invoice-table">
          <thead>
            <tr>
              <th className="col-index">#</th>
              <th className="col-image">{t('column.image')}</th>
              <th className="col-item">{t('column.itemNumber')}</th>
              <th className="col-barcode">{t('column.barcode')}</th>
              <th className="col-name">{t('column.name')}</th>
              <th className="col-cost">{t('column.cost')}</th>
              <th className="col-rrp">{t('column.rrp')}</th>
              <th className="col-qty">{t('column.orderQuantity')}</th>
              <th className="col-qty">{t('column.shipQuantity')}</th>
              <th className="col-subtotal">{t('column.subtotal')}</th>
            </tr>
          </thead>
          <tbody>
            {hasReturnLines ? (
              <>
                {suppliedItems.length > 0 ? (
                  <>
                    <tr className="store-order-invoice-section-row">
                      <td colSpan={10}>{t('warehouseUi.storeOrderInvoice.sectionSupplied')}</td>
                    </tr>
                    {suppliedItems.map((item, index) => renderInvoiceRow(item, index))}
                  </>
                ) : null}
                <tr className="store-order-invoice-section-row is-return">
                  <td colSpan={10}>
                    {suppliedItems.length > 0 && !isCreditNote
                      ? t('warehouseUi.storeOrderInvoice.sectionAdjustments')
                      : t('warehouseUi.storeOrderInvoice.sectionReturned')}
                  </td>
                </tr>
                {returnedItems.map((item, index) => renderInvoiceRow(item, suppliedItems.length + index))}
              </>
            ) : (
              sortedItems.map((item, index) => renderInvoiceRow(item, index))
            )}
          </tbody>
        </table>

        <div className="store-order-invoice-footer">
          {/* 退款单是我们退钱给分店，不是让对方付款，所以不放收款账户与 30 天付款条款。 */}
          {isCreditNote ? (
            <div className="store-order-invoice-payment">
              <div className="store-order-invoice-note">{t('warehouseUi.storeOrderInvoice.creditFootnote')}</div>
            </div>
          ) : (
            <div className="store-order-invoice-payment">
              <h4>{t('warehouse.invoice.paymentDetail')}</h4>
              <div className="store-order-invoice-payment-row">
                <span className="store-order-invoice-label">{t('warehouse.invoice.paymentName')}</span>
                <span>HOT BARGAIN INTERNATIONAL</span>
              </div>
              <div className="store-order-invoice-payment-row">
                <span className="store-order-invoice-label">{t('warehouse.invoice.bsb')}</span>
                {/* 公司收款 BSB 需保留银行分隔符，避免显示成错误的 5 位数字。 */}
                <span>012-532</span>
              </div>
              <div className="store-order-invoice-payment-row">
                <span className="store-order-invoice-label">{t('warehouse.invoice.account')}</span>
                <span>208034605</span>
              </div>
              <div className="store-order-invoice-disclaimer">
                {t('warehouse.invoice.paymentDisclaimer')}
              </div>
            </div>
          )}

          <div className="store-order-invoice-totals">
            <div className={`store-order-invoice-total-row${totals.subTotal < 0 ? ' is-negative' : ''}`}>
              <span>{t('warehouse.invoice.subTotal')}</span>
              <span>{formatCurrency(totals.subTotal)}</span>
            </div>
            <div className={`store-order-invoice-total-row${totals.gst < 0 ? ' is-negative' : ''}`}>
              <span>{t('warehouse.invoice.gst')}</span>
              <span>{formatCurrency(totals.gst)}</span>
            </div>
            <div className="store-order-invoice-total-row">
              <span>{t('warehouse.invoice.freight')}</span>
              <span>{formatCurrency(totals.freight)}</span>
            </div>
            <div className={`store-order-invoice-total-row is-grand${isCreditNote ? ' is-credit' : ''}`}>
              <span>
                {isCreditNote ? t('warehouseUi.storeOrderInvoice.totalCredit') : t('warehouse.invoice.totalBeforeDiscount')}
              </span>
              <span>{formatCurrency(totals.total)}</span>
            </div>
          </div>
        </div>
        {hasReturnLines && !isCreditNote ? (
          <div className="store-order-invoice-note">{t('warehouseUi.storeOrderInvoice.invoiceFootnote')}</div>
        ) : null}

        <div className="store-order-print-footer">
          <div>{t('warehouse.invoice.pageDocument')}</div>
          <div>{t('warehouse.invoice.imageRefNote')}</div>
          <div>{t('warehouse.invoice.dateLabel')} {formatPrintDate(undefined)}</div>
        </div>
      </div>

      <Modal
        title={
          isCreditNote
            ? t('warehouseUi.storeOrderInvoice.creditEmailModalTitle', { lng: emailModalLanguage })
            : t('warehouse.invoice.emailModalTitle', { lng: emailModalLanguage })
        }
        open={emailModalOpen}
        destroyOnHidden
        okText={t('warehouse.invoice.sendEmail', { lng: emailModalLanguage })}
        cancelText={t('common.cancel', { lng: emailModalLanguage })}
        confirmLoading={sendingEmail || translatingEmailText}
        // 发件账号列表加载完成前禁止发送，避免还没选上账号就按默认账号发出。
        okButtonProps={{ disabled: translatingEmailText || loadingSenderAccounts }}
        onCancel={() => setEmailModalOpen(false)}
        onOk={() => void handleSendInvoiceEmail()}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Segmented<InvoiceEmailModalLanguage>
            aria-label={t('warehouse.invoice.emailLanguage', { lng: emailModalLanguage })}
            size="small"
            value={emailModalLanguage}
            disabled={translatingEmailText}
            options={[
              { label: t('warehouse.invoice.emailLanguageChinese', { lng: emailModalLanguage }), value: 'zh' },
              { label: t('warehouse.invoice.emailLanguageEnglish', { lng: emailModalLanguage }), value: 'en' },
            ]}
            onChange={(value) => void handleEmailModalLanguageChange(value)}
          />
          <InvoiceEmailSentStatusText info={order?.invoiceEmailSentInfo} t={t} lng={emailModalLanguage} />
          {(loadingSenderAccounts || senderAccounts.length > 0) && (
            <Select
              aria-label={t('warehouse.invoice.senderAccount', { lng: emailModalLanguage })}
              style={{ width: '100%' }}
              value={senderAccountId}
              loading={loadingSenderAccounts}
              disabled={translatingEmailText || loadingSenderAccounts}
              placeholder={t('warehouse.invoice.senderAccount', { lng: emailModalLanguage })}
              options={senderAccounts.map((account) => ({
                value: account.id,
                label: account.isDefault
                  ? `${formatInvoiceEmailSenderAccountLabel(account)} · ${t('warehouse.invoice.senderAccountDefault', { lng: emailModalLanguage })}`
                  : formatInvoiceEmailSenderAccountLabel(account),
              }))}
              onChange={setSenderAccountId}
            />
          )}
          <Input
            type="email"
            value={recipientEmail}
            onChange={(event) => setRecipientEmail(event.target.value)}
            disabled={translatingEmailText}
            placeholder={t('warehouse.invoice.recipientEmail', { lng: emailModalLanguage })}
          />
          <Input
            value={emailSubject}
            onChange={(event) => {
              setEmailSubjectTouched(true)
              setEmailSubject(event.target.value)
            }}
            disabled={translatingEmailText}
            placeholder={t('warehouse.invoice.emailSubject', { lng: emailModalLanguage })}
          />
          <Input.TextArea
            rows={5}
            value={emailBody}
            onChange={(event) => {
              setEmailBodyTouched(true)
              setEmailBody(event.target.value)
            }}
            disabled={translatingEmailText}
            placeholder={t('warehouse.invoice.emailBody', { lng: emailModalLanguage })}
          />
          <Switch
            checked={saveAsStoreDefault}
            disabled={translatingEmailText}
            checkedChildren={t('warehouse.invoice.saveAsStoreDefault', { lng: emailModalLanguage })}
            unCheckedChildren={t('warehouse.invoice.saveAsStoreDefault', { lng: emailModalLanguage })}
            onChange={setSaveAsStoreDefault}
          />
        </Space>
      </Modal>
    </div>
  )
}
