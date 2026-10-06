import { DownloadOutlined, FileExcelOutlined, PrinterOutlined, RollbackOutlined } from '@ant-design/icons'
import { Button, Empty, Space, Spin, message } from 'antd'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { useStableRouteContext } from '../../../hooks/useStableRouteContext'
import { useAuthStore } from '../../../store/auth'
import { getStores } from '../../../services/storeService'
import { getStoreOrderDetail, startPickingStoreOrder } from '../../../services/storeOrderService'
import { StoreOrderFlowStatus } from '../../../types/storeOrder'
import type { StoreDto } from '../../../types/store'
import type { StoreOrderDetail } from '../../../types/storeOrder'
import { useDynamicTabTitle } from '../../../hooks/useDynamicTabTitle'
import { shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'
import { shouldShowStoreOrderDetailInitialLoading } from './detailLoadState'
import { buildDocumentFileName, downloadElementPagesAsPdf, formatCurrency, formatPrintDate, printElementPagesAsPdf } from './printUtils'
import { buildPickingListExcelData, buildPickingListPdfPages, buildPickingOrderBarcode, formatInnerPackCount, formatPickingOrderQuantity } from './pickingListLogic'
import { formatStoreOrderVolume } from './volumeFormat'
import { publishStoreOrderFlowStatusChanged } from './storeOrderFlowStatusSync'
import './print.css'

export default function PickingListPage() {
  const { t, i18n } = useTranslation()
  const route = useStableRouteContext()
  const id = route?.params.id || ''
  const navigate = useNavigate()
  const { active } = useKeepAliveContext()
  const { access } = useAuthStore()
  const printRootRef = useRef<HTMLDivElement | null>(null)
  const pdfRootRef = useRef<HTMLDivElement | null>(null)
  const activeRef = useRef(active)
  activeRef.current = active
  // 记录当前配货单已完成首次加载，保活 Tab 恢复时避免同订单自动刷新。
  const loadedOrderIdRef = useRef<string | null>(null)
  const visibleOrderIdRef = useRef<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [printing, setPrinting] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [exportingExcel, setExportingExcel] = useState(false)
  const [order, setOrder] = useState<StoreOrderDetail | null>(null)
  const [store, setStore] = useState<StoreDto | null>(null)
  const isWarehouseStaffOnly =
    access.isWarehouseStaff &&
    !access.isAdmin &&
    !access.isWarehouseManager &&
    (access.hasRole('WarehouseStaff') || access.hasRole('仓库员工'))
  // 配货单自动开始配货属于写动作，跟随仓库订货管理权限，纯 WarehouseStaff 只能打印/下载。
  const canUseWarehouseManagerActions = access.canManageWarehouseOrders && !isWarehouseStaffOnly
  // 打印页日期格式跟随当前语言，但只限定本次需求中的中英文区域设置。
  const printLocale = i18n.resolvedLanguage?.toLowerCase().startsWith('en') ? 'en-US' : 'zh-CN'

  useDynamicTabTitle(
    order?.orderNo
      ? t('warehouse.pickingList.titleWithStore', { storeName: store?.storeName || order.storeName || order.storeCode || t('warehouse.pickingList.unknownStore'), orderNo: order.orderNo })
      : t('warehouse.pickingList.title'),
  )

  useEffect(() => {
    if (!active) return

    if (!id) {
      return
    }

    let cancelled = false

    const load = async (showLoading = true) => {
      if (showLoading) {
        setLoading(true)
      }
      try {
        const detail = await getStoreOrderDetail(id)
        if (cancelled || !activeRef.current) return

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

        let nextStore: StoreDto | null = null
        // WarehouseStaff 无需加载完整分店下拉；配货单只读展示直接使用订单详情里的分店名称。
        if (detail.storeCode && canUseWarehouseManagerActions) {
          const storeResult = await getStores({
            search: detail.storeCode,
            page: 1,
            pageSize: 1,
          })
          if (cancelled || !activeRef.current) return
          nextStore = storeResult.items[0] ?? null
        }

        // 仅在当前激活请求完整结束后一次性提交，避免隐藏 Tab 回写半套打印数据。
        loadedOrderIdRef.current = detail.orderGUID || id
        visibleOrderIdRef.current = detail.orderGUID || id
        setOrder(detail)
        setStore(nextStore)
      } catch (error) {
        if (cancelled || !activeRef.current) return

        console.error(error)
        const errorMessage = error instanceof Error ? error.message : t('warehouse.pickingList.loadFailed')
        if (showLoading) {
          visibleOrderIdRef.current = null
          setOrder(null)
          setStore(null)
        }
        message.error(errorMessage)
      } finally {
        if (showLoading && !cancelled && activeRef.current) {
          setLoading(false)
        }
      }
    }

    // 保活 Tab 切回同订单且页面仍有可见内容时，直接复用现有打印数据。
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

    return () => {
      cancelled = true
    }
  }, [active, id])

  const sortedItems = useMemo(() => {
    if (!order?.items) {
      return []
    }

    return [...order.items].sort((left, right) => {
      const leftLocation = left.locationCode || ''
      const rightLocation = right.locationCode || ''
      const locationCompare = leftLocation.localeCompare(rightLocation, 'zh-CN', {
        numeric: true,
        sensitivity: 'base',
      })
      if (locationCompare !== 0) {
        return locationCompare
      }

      return (left.itemNumber || '').localeCompare(right.itemNumber || '', 'zh-CN', {
        numeric: true,
        sensitivity: 'base',
      })
    })
  }, [order?.items])

  const totalOrderVolume = useMemo(() => {
    if (!order) {
      return 0
    }

    if (typeof order.totalOrderVolume === 'number') {
      return order.totalOrderVolume
    }

    if (typeof order.totalVolume === 'number') {
      return order.totalVolume
    }

    return (order.items ?? []).reduce((sum, item) => {
      return sum + (item.orderVolume ?? item.totalVolume ?? ((item.volume ?? 0) * Number(item.quantity ?? 0)))
    }, 0)
  }, [order])

  const pdfPages = useMemo(() => {
    return buildPickingListPdfPages(sortedItems, Boolean(order))
  }, [order, sortedItems])

  // 订单条码只依赖订单号（HBSO:订单号，强制 CODE128）；必须放在下方提前 return 之前，保证 Hook 顺序稳定。
  const orderBarcode = useMemo(() => buildPickingOrderBarcode(order?.orderNo), [order?.orderNo])

  const handleBeforePrint = async () => {
    if (!order) {
      return true
    }

    if (!activeRef.current) {
      throw new Error(t('warehouse.pickingList.layoutNotReady'))
    }

    // WarehouseStaff 可打印/下载配货单，但不能借打印动作触发订单状态流转。
    if (canUseWarehouseManagerActions && order.flowStatus === StoreOrderFlowStatus.Submitted) {
      await startPickingStoreOrder(order.orderGUID)
      // 订单明细、订单列表是保活页面，切回时不会重新请求；这里通知它们把该订单改显示为「配货中」，
      // 否则打印后回到明细页仍会看到旧的「已提交」。无论当前打印页是否仍处于激活状态都要通知。
      publishStoreOrderFlowStatusChanged(order.orderGUID, StoreOrderFlowStatus.Picking)
      if (!activeRef.current) {
        // 状态写入已经成功，但当前打印 DOM 已隐藏；恢复页面后重新加载最新订单再允许打印。
        loadedOrderIdRef.current = null
        return true
      }
      setOrder((current) =>
        current
          ? {
              ...current,
              flowStatus: StoreOrderFlowStatus.Picking,
            }
          : current,
      )
    }

    return true
  }

  const getActivePdfRoot = () => {
    const root = pdfRootRef.current
    if (!activeRef.current || !root?.isConnected) {
      throw new Error(t('warehouse.pickingList.layoutNotReady'))
    }
    return root
  }

  const handlePrint = async () => {
    if (!order) {
      return
    }

    setPrinting(true)
    try {
      getActivePdfRoot()
      await handleBeforePrint()
      await printElementPagesAsPdf(getActivePdfRoot(), {
        createCanvasContextErrorMessage: t('warehouse.pickingList.createPdfCanvasFailed'),
        layoutNotReadyErrorMessage: t('warehouse.pickingList.layoutNotReady'),
      })
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.pickingList.printFailed'))
    } finally {
      setPrinting(false)
    }
  }

  const handleDownload = async () => {
    if (!order) {
      return
    }

    setDownloading(true)
    try {
      getActivePdfRoot()
      await handleBeforePrint()
      await downloadElementPagesAsPdf(
        getActivePdfRoot(),
        buildDocumentFileName(
          t('warehouse.pickingList.fileName'),
          store?.storeName || order.storeName || order.storeCode,
          order.orderNo || order.orderGUID,
          'pdf',
          {
            unknownStore: t('warehouse.pickingList.unknownStore'),
            unknownOrder: t('warehouse.pickingList.unknownOrder'),
          },
        ),
        {
          createCanvasContextErrorMessage: t('warehouse.pickingList.createPdfCanvasFailed'),
          layoutNotReadyErrorMessage: t('warehouse.pickingList.layoutNotReady'),
        },
      )
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.pickingList.downloadPdfFailed'))
    } finally {
      setDownloading(false)
    }
  }

  const handleExportExcel = async () => {
    if (!order) {
      return
    }

    setExportingExcel(true)
    try {
      const { default: ExcelJS } = await import('exceljs')
      const workbook = new ExcelJS.Workbook()
      const excelData = buildPickingListExcelData(
        order,
        sortedItems,
        {
          sheetName: t('warehouse.pickingList.excel.sheetName'),
          orderNoLabel: t('warehouse.pickingList.excel.orderNo'),
          storeLabel: t('warehouse.pickingList.excel.store'),
          orderDateLabel: t('warehouse.pickingList.excel.orderDate'),
          printTimeLabel: t('warehouse.pickingList.excel.printTime'),
          remarksLabel: t('warehouse.pickingList.excel.remarks'),
          totalSKULabel: t('warehouse.pickingList.excel.totalSKU'),
          totalOrderQtyLabel: t('warehouse.pickingList.excel.totalOrderQty'),
          totalShipQtyLabel: t('warehouse.pickingList.excel.totalShipQty'),
          totalOrderVolumeLabel: t('warehouse.pickingList.excel.totalOrderVolume'),
          detailHeaders: {
            index: '#',
            itemNumber: t('column.itemNumber'),
            location: t('column.location'),
            productName: t('column.productName'),
            importPrice: t('column.importPrice'),
            rrp: t('column.rrp'),
            innerPackCount: t('warehouse.pickingList.innerPackShort'),
            orderQuantity: t('column.orderQuantity'),
          },
        },
        {
          orderNoText: orderNoText,
          storeText: displayStoreText,
          orderDateText: formatPrintDate(order.orderDate, false, printLocale),
          printTimeText: formatPrintDate(undefined, true, printLocale),
          totalOrderVolumeText: formatStoreOrderVolume(totalOrderVolume),
        },
      )

      const worksheet = workbook.addWorksheet(excelData.sheetName)
      worksheet.columns = [
        { width: 8 },
        { width: 18 },
        { width: 14 },
        { width: 32 },
        { width: 14 },
        { width: 14 },
        { width: 16 },
        { width: 14 },
        { width: 14 },
      ]

      excelData.overviewRows.forEach(([label, value]) => {
        const row = worksheet.addRow([label, value])
        row.getCell(1).font = { bold: true }
      })

      worksheet.addRow([])

      const detailHeaderRow = worksheet.addRow(excelData.detailHeader)
      detailHeaderRow.font = { bold: true }

      excelData.detailRows.forEach((row) => {
        worksheet.addRow(row)
      })

      if (excelData.remarksRow || excelData.totalRows.length > 0) {
        worksheet.addRow([])
      }

      if (excelData.remarksRow) {
        const remarksRow = worksheet.addRow(excelData.remarksRow)
        remarksRow.getCell(1).font = { bold: true }
      }

      excelData.totalRows.forEach(([label, value]) => {
        const row = worksheet.addRow([label, value])
        row.getCell(1).font = { bold: true }
      })

      worksheet.getColumn(5).numFmt = '$#,##0.00'
      worksheet.getColumn(6).numFmt = '$#,##0.00'

      const buffer = await workbook.xlsx.writeBuffer()
      const blob = new Blob([buffer], {
        type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      })
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = buildDocumentFileName(
        t('warehouse.pickingList.fileName'),
        store?.storeName || order.storeName || order.storeCode,
        order.orderNo || order.orderGUID,
        'xlsx',
        {
          unknownStore: t('warehouse.pickingList.unknownStore'),
          unknownOrder: t('warehouse.pickingList.unknownOrder'),
        },
      )
      document.body.appendChild(link)
      link.click()
      document.body.removeChild(link)
      URL.revokeObjectURL(url)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.pickingList.exportExcelFailed'))
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

  const displayStoreName = store?.storeName || order.storeName || order.storeCode || t('warehouse.pickingList.unknownStore')
  const displayStoreText = store?.storeCode && store.storeCode !== displayStoreName
    ? `${displayStoreName} (${store.storeCode})`
    : displayStoreName
  const orderNoText = order.orderNo || order.orderGUID || t('warehouse.pickingList.unknownOrder')
  const renderPickingHeader = () => (
    <div className="store-order-picking-header">
      {/* 订货日期并到标题下方，右栏让给订单条码：表头只随条码块小幅增高，不额外占一行。 */}
      <div className="store-order-picking-heading">
        <div className="store-order-picking-title">{t('warehouse.pickingList.title')}</div>
        <div className="store-order-picking-order-date">
          <strong>{t('warehouse.pickingList.orderDate')}</strong>
          {formatPrintDate(order.orderDate, false, printLocale)}
        </div>
      </div>
      <div className="store-order-picking-primary">
        {/* 店名和单号放在同一视觉主线，方便仓库打印后快速识别单据归属。 */}
        <div className="store-order-picking-primary-line">
          <span className="store-order-picking-store">{displayStoreText}</span>
          <span className="store-order-picking-order-no">
            #{orderNoText}
          </span>
        </div>
      </div>
      <div className="store-order-picking-meta">
        {/* PDA 扫这个 Code 128 订单条码进入拣货；SVG 矢量绘制，html2canvas 按 2 倍截图时条边仍保持锐利。 */}
        {orderBarcode ? (
          <div className="store-order-picking-barcode">
            <svg
              className="store-order-picking-barcode-bars"
              width={orderBarcode.width}
              height={orderBarcode.height}
              viewBox={`0 0 ${orderBarcode.width} ${orderBarcode.height}`}
              shapeRendering="crispEdges"
              role="img"
              aria-label={orderBarcode.value}
            >
              <path d={orderBarcode.path} fill="#000" />
            </svg>
            <div className="store-order-picking-barcode-caption">
              {orderBarcode.value} · {t('warehouse.pickingList.scanToPick')}
            </div>
          </div>
        ) : null}
      </div>
    </div>
  )

  return (
    <div className="store-order-print-page">
      <div className="store-order-print-toolbar no-print">
        <Space wrap>
          <Button icon={<RollbackOutlined />} onClick={() => navigate(-1)}>
            {t('common.back')}
          </Button>
          <Button icon={<FileExcelOutlined />} loading={exportingExcel} onClick={() => void handleExportExcel()}>
            {t('warehouse.pickingList.exportExcel')}
          </Button>
          <Button icon={<DownloadOutlined />} loading={downloading} onClick={() => void handleDownload()}>
            {t('warehouse.pickingList.downloadPdf')}
          </Button>
          <Button type="primary" icon={<PrinterOutlined />} loading={printing} onClick={() => void handlePrint()}>
            {t('warehouse.pickingList.printPickingList')}
          </Button>
        </Space>
      </div>

      <div ref={printRootRef} className="store-order-print-root store-order-print-paper store-order-picking-paper">
        <table className="store-order-picking-table">
          <colgroup>
            <col className="col-index" />
            <col className="col-item" />
            <col className="col-location" />
            <col className="col-product" />
            <col className="col-price" />
            <col className="col-price" />
            <col className="col-inner-pack" />
            <col className="col-qty" />
          </colgroup>
          <thead>
            <tr>
              <td colSpan={8} className="store-order-picking-header-cell">
                {renderPickingHeader()}
              </td>
            </tr>
            <tr>
              <th className="col-index">#</th>
              <th className="col-item">{t('column.itemNumber')}</th>
              <th className="col-location">{t('column.location')}</th>
              <th className="col-product">{t('column.productName')}</th>
              <th className="col-price">{t('column.importPrice')}</th>
              <th className="col-price">{t('column.rrp')}</th>
              <th className="col-inner-pack">{t('warehouse.pickingList.innerPackShort')}</th>
              <th className="col-qty">{t('warehouse.pickingList.orderQtyShort')}</th>
            </tr>
          </thead>
          <tbody>
            {sortedItems.map((item, index) => (
              <tr key={item.detailGUID}>
                <td className="col-index">{index + 1}</td>
                <td className="col-item">{item.itemNumber || '--'}</td>
                <td className="col-location">{item.locationCode || '--'}</td>
                <td className="col-product">
                  <div className="store-order-picking-name">{item.productName || '--'}</div>
                </td>
                <td className="col-price">{formatCurrency(item.importPrice)}</td>
                <td className="col-price">{item.rrp !== undefined && item.rrp !== null ? formatCurrency(item.rrp) : ''}</td>
                <td className="col-inner-pack">
                  {/* 包数分子与订货数列一致：订货数为空时使用发货数兜底。 */}
                  {formatInnerPackCount(item.quantity, item.allocQuantity, item.minOrderQuantity)}
                </td>
                <td className="col-qty">{formatPickingOrderQuantity(item.quantity, item.allocQuantity)}</td>
              </tr>
            ))}
          </tbody>
        </table>

        <div className="store-order-picking-footer">
          {order.remarks ? (
            <div className="store-order-picking-remarks">
              {t('warehouse.pickingList.remarks', { remarks: order.remarks })}
            </div>
          ) : null}
          <div className="store-order-picking-totals">
            <div>{t('warehouse.pickingList.totalSKU', { count: order.totalSKU ?? order.items.length })}</div>
            <div>{t('warehouse.pickingList.totalOrderQty', { count: order.totalQuantity })}</div>
            <div>{t('warehouse.pickingList.totalShipQty', { count: order.totalAllocQuantity ?? 0 })}</div>
            <div>{t('warehouse.pickingList.totalOrderVolume', { volume: formatStoreOrderVolume(totalOrderVolume) })}</div>
          </div>
        </div>
      </div>

      <div ref={pdfRootRef} className="store-order-pdf-root" aria-hidden="true">
        {pdfPages.map((page, pageIndex) => (
          <div key={`${page.startIndex}-${pageIndex}`} className="store-order-pdf-page store-order-picking-paper">
            <div className="store-order-pdf-page-body">
              <table className="store-order-picking-table">
                <colgroup>
                  <col className="col-index" />
                  <col className="col-item" />
                  <col className="col-location" />
                  <col className="col-product" />
                  <col className="col-price" />
                  <col className="col-price" />
                  <col className="col-inner-pack" />
                  <col className="col-qty" />
                </colgroup>
                <thead>
                  <tr>
                    <td colSpan={8} className="store-order-picking-header-cell">
                      {renderPickingHeader()}
                    </td>
                  </tr>
                  <tr>
                    <th className="col-index">#</th>
                    <th className="col-item">{t('column.itemNumber')}</th>
                    <th className="col-location">{t('column.location')}</th>
                    <th className="col-product">{t('column.productName')}</th>
                    <th className="col-price">{t('column.importPrice')}</th>
                    <th className="col-price">{t('column.rrp')}</th>
                    <th className="col-inner-pack">{t('warehouse.pickingList.innerPackShort')}</th>
                    <th className="col-qty">{t('warehouse.pickingList.orderQtyShort')}</th>
                  </tr>
                </thead>
                <tbody>
                  {page.items.map((item, index) => (
                    <tr key={item.detailGUID}>
                      <td className="col-index">{page.startIndex + index + 1}</td>
                      <td className="col-item">{item.itemNumber || '--'}</td>
                      <td className="col-location">{item.locationCode || '--'}</td>
                      <td className="col-product">
                        <div className="store-order-picking-name">{item.productName || '--'}</div>
                      </td>
                      <td className="col-price">{formatCurrency(item.importPrice)}</td>
                      <td className="col-price">{item.rrp !== undefined && item.rrp !== null ? formatCurrency(item.rrp) : ''}</td>
                      <td className="col-inner-pack">
                        {/* PDF 分页复用同一套包数计算，避免屏幕和 PDF 内容不一致。 */}
                        {formatInnerPackCount(item.quantity, item.allocQuantity, item.minOrderQuantity)}
                      </td>
                      <td className="col-qty">{formatPickingOrderQuantity(item.quantity, item.allocQuantity)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>

              {page.showSummary ? (
                <div className="store-order-picking-footer">
                  {order.remarks ? (
                    <div className="store-order-picking-remarks">
                      {t('warehouse.pickingList.remarks', { remarks: order.remarks })}
                    </div>
                  ) : null}
                  <div className="store-order-picking-totals">
                    <div>{t('warehouse.pickingList.totalSKU', { count: order.totalSKU ?? order.items.length })}</div>
                    <div>{t('warehouse.pickingList.totalOrderQty', { count: order.totalQuantity })}</div>
                    <div>{t('warehouse.pickingList.totalShipQty', { count: order.totalAllocQuantity ?? 0 })}</div>
                    <div>{t('warehouse.pickingList.totalOrderVolume', { volume: formatStoreOrderVolume(totalOrderVolume) })}</div>
                  </div>
                </div>
              ) : null}
            </div>

            <div className="store-order-pdf-page-number">
              {t('warehouse.pickingList.pageNumber', { current: pageIndex + 1, total: pdfPages.length })}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
