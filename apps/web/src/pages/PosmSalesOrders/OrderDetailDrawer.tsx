import { CloseOutlined, CopyOutlined, DownloadOutlined, DownOutlined, FilePdfOutlined, PictureOutlined, PrinterOutlined, UpOutlined } from '@ant-design/icons'
import { Button, Drawer, Skeleton, Space, Tabs, Tooltip, message } from 'antd'
import { useState } from 'react'
import { createPortal } from 'react-dom'
import { useTranslation } from 'react-i18next'

import ProductListImage from '../../components/ProductListImage'
import { registerPageMessages } from '../../i18n/registerPageMessages'
import type { PosmSalesOrder, PosmSalesOrderDetailResponse } from '../../types/posmSalesOrder'

import OrderStatusTag, { PaymentMethodsText } from './OrderStatusTag'
import { formatMoney, orderActualAmount, paymentMethodKey } from './posmSalesOrdersLogic'
import ReceiptPaper from './receipt/ReceiptPaper'
import ReceiptQrPopover from './receipt/ReceiptQrPopover'
import { posmReceiptMessages } from './receipt/receiptMessages'
import { useOrderReceipt } from './receipt/useOrderReceipt'
import { formatPosmSalesOrderTime } from './time'

// 在线小票文案随订单页代码块懒注册，不进首屏 i18n 包。
registerPageMessages(posmReceiptMessages)

interface OrderDetailDrawerProps {
  open: boolean
  /** 列表行：抽屉打开即可显示订单头信息，不等详情接口。 */
  order: PosmSalesOrder | null
  detail: PosmSalesOrderDetailResponse | null
  loading: boolean
  hasPrev: boolean
  hasNext: boolean
  onPrev: () => void
  onNext: () => void
  onClose: () => void
  onPreviewInvoice: (orderGuid: string) => void
  onDownloadInvoice: (orderGuid: string) => void
}

const PAYMENT_FALLBACK = { cash: '现金', card: '刷卡', voucher: '代金券', other: '其他' }

/** 主档图片失效（如文件名带空格、已删除）时显示的灰底占位，不出现破图。 */
const IMAGE_FALLBACK =
  'data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0MCIgaGVpZ2h0PSI0MCI+PHJlY3Qgd2lkdGg9IjQwIiBoZWlnaHQ9IjQwIiBmaWxsPSIjZjVmNWY1Ii8+PC9zdmc+'

/**
 * 订单详情抽屉：替代原来的展开行，列表不再被撑开跳动；
 * 上一单/下一单（也可用 ↑ ↓）在当前页内切换，Esc 关闭。
 */
export default function OrderDetailDrawer({
  open,
  order,
  detail,
  loading,
  hasPrev,
  hasNext,
  onPrev,
  onNext,
  onClose,
  onPreviewInvoice,
  onDownloadInvoice,
}: OrderDetailDrawerProps) {
  const { t } = useTranslation()
  const orderGuid = order?.orderGuid ?? ''
  // 页签：默认停在「小票」，↑ ↓ 切换订单时保持当前页签。
  const [activeTab, setActiveTab] = useState<'receipt' | 'items'>('receipt')
  const receipt = useOrderReceipt(orderGuid, open)
  const lines = detail?.orderDetails ?? []
  const payments = detail?.paymentDetails ?? []
  const quantity = lines.reduce((sum, line) => sum + (line.quantity ?? 0), 0)
  const skuCount = new Set(lines.map((line) => (line.productCode ?? '').toUpperCase()).filter(Boolean)).size

  const copyOrderGuid = async () => {
    try {
      await navigator.clipboard.writeText(orderGuid)
      message.success(t('posmOrders.drawer.copied', '订单号已复制'))
    } catch {
      message.error(t('posmOrders.drawer.copyFailed', '复制失败，请手动选择订单号'))
    }
  }

  return (
    <Drawer
      className="posm-order-drawer"
      open={open}
      width={600}
      onClose={onClose}
      closable={false}
      title={
        <div>
          <Space size={10} align="center">
            <span>{t('posmOrders.drawer.title', '订单详情')}</span>
            <OrderStatusTag status={order?.status} />
          </Space>
          <div className="posm-order-drawer-guid">
            <span>{orderGuid}</span>
            <Tooltip title={t('posmOrders.drawer.copy', '复制订单号')}>
              <Button
                type="text"
                size="small"
                icon={<CopyOutlined />}
                aria-label={t('posmOrders.drawer.copy', '复制订单号')}
                onClick={copyOrderGuid}
              />
            </Tooltip>
          </div>
        </div>
      }
      extra={
        <Space size={4}>
          <Button size="small" icon={<UpOutlined />} disabled={!hasPrev} onClick={onPrev} aria-label={t('posmOrders.drawer.prev', '上一单')} />
          <Button size="small" icon={<DownOutlined />} disabled={!hasNext} onClick={onNext} aria-label={t('posmOrders.drawer.next', '下一单')} />
          <Button type="text" size="small" icon={<CloseOutlined />} onClick={onClose} aria-label={t('posmOrders.drawer.close', '关闭')} />
        </Space>
      }
      footer={
        <div className="posm-order-drawer-footer">
          <span className="posm-order-drawer-footer-hint">{t('posmOrders.drawer.keyboardHint', '↑ ↓ 切换订单 · Esc 关闭')}</span>
          <ReceiptQrPopover
            orderGuid={orderGuid}
            link={receipt.view?.link ?? null}
            disabled={!orderGuid || !receipt.view}
            onEnsure={receipt.ensureLink}
            onRegenerate={receipt.regenerate}
          />
          <Button icon={<PrinterOutlined />} disabled={!receipt.view} onClick={() => window.print()}>
            {t('posmReceipt.print', '打印小票')}
          </Button>
          <Button icon={<FilePdfOutlined />} disabled={!orderGuid} onClick={() => onPreviewInvoice(orderGuid)}>
            {t('posmOrders.previewInvoice', '预览发票')}
          </Button>
          <Button icon={<DownloadOutlined />} disabled={!orderGuid} onClick={() => onDownloadInvoice(orderGuid)}>
            {t('posmOrders.downloadPdf', '下载 PDF')}
          </Button>
        </div>
      }
    >
      <Tabs
        className="posm-order-drawer-tabs"
        activeKey={activeTab}
        onChange={(key) => setActiveTab(key as 'receipt' | 'items')}
        items={[
          {
            key: 'receipt',
            label: t('posmReceipt.tabReceipt', '小票'),
            children: (
              <div className="posm-order-drawer-receipt">
                {receipt.loading && !receipt.view ? (
                  <Skeleton active paragraph={{ rows: 8 }} title={false} style={{ maxWidth: 360 }} />
                ) : receipt.error ? (
                  <div className="posm-order-drawer-receipt-error">
                    <span>{receipt.error || t('posmReceipt.loadFailed', '小票加载失败')}</span>
                    <Button size="small" onClick={() => void receipt.reload()}>
                      {t('posmReceipt.retry', '重试')}
                    </Button>
                  </div>
                ) : receipt.view ? (
                  <ReceiptPaper document={receipt.view.document} />
                ) : null}
              </div>
            ),
          },
          {
            key: 'items',
            label: t('posmReceipt.tabItems', '商品明细'),
            children: (
              <>
      <dl className="posm-order-drawer-meta">
        <div>
          <dt>{t('posmOrders.drawer.time', '时间')}</dt>
          <dd>{formatPosmSalesOrderTime(order?.orderTime, 'YYYY-MM-DD HH:mm:ss')}</dd>
        </div>
        <div>
          <dt>{t('posmOrders.drawer.branch', '分店')}</dt>
          <dd>
            {order?.branchName || order?.branchCode || '-'}
            {order?.branchName && order?.branchCode ? <span className="posm-orders-muted"> · {order.branchCode}</span> : null}
          </dd>
        </div>
        <div>
          <dt>{t('posmOrders.drawer.device', '收银机')}</dt>
          <dd className="posm-orders-mono">{order?.deviceCode || '-'}</dd>
        </div>
        <div>
          <dt>{t('posmOrders.drawer.payment', '支付')}</dt>
          <dd>
            {payments.length ? (
              payments.map((payment, index) => {
                const key = paymentMethodKey(payment.paymentMethod ?? 0)
                return (
                  <div key={index}>
                    {t(`posmOrders.payment.${key}`, PAYMENT_FALLBACK[key])} {formatMoney(payment.amount)}
                    <span className="posm-orders-muted"> · {formatPosmSalesOrderTime(payment.paymentTime, 'HH:mm:ss')}</span>
                  </div>
                )
              })
            ) : (
              <PaymentMethodsText methods={order?.paymentMethods} />
            )}
          </dd>
        </div>
      </dl>

      <div className="posm-order-drawer-section">
        <h3>{t('posmOrders.drawer.goodsTitle', '商品明细')}</h3>
        {detail ? (
          <span className="posm-orders-muted">
            {t('posmOrders.drawer.goodsSummary', { sku: skuCount, quantity, defaultValue: '{{sku}} 种 · {{quantity}} 件' })}
          </span>
        ) : null}
      </div>

      {loading && !detail ? (
        <div style={{ padding: '0 20px' }}>
          <Skeleton active paragraph={{ rows: 4 }} title={false} />
        </div>
      ) : (
        <ul className="posm-order-drawer-lines">
          {lines.map((line, index) => {
            const discount = line.discountAmount ?? 0
            return (
              <li key={`${line.productCode ?? 'line'}-${index}`} className="posm-order-drawer-line">
                <span className="posm-order-drawer-thumb">
                  {line.productImage ? (
                    <ProductListImage src={line.productImage} size={40} radius={6} previewMask="" fallback={IMAGE_FALLBACK} />
                  ) : (
                    <PictureOutlined aria-hidden="true" />
                  )}
                </span>
                <span className="posm-order-drawer-line-main">
                  <span className="posm-order-drawer-line-name">{line.productName || '-'}</span>
                  <span className="posm-orders-mono">{line.itemNumber || line.productCode || '-'}</span>
                </span>
                <span className="posm-order-drawer-line-side">
                  <strong className={(line.actualAmount ?? 0) < 0 ? 'posm-orders-negative' : undefined}>
                    {formatMoney(line.actualAmount)}
                  </strong>
                  <span className="posm-orders-muted">
                    {line.quantity ?? 0} × {formatMoney(line.unitPrice)}
                    {discount ? ` · ${t('posmOrders.discount', '折扣')} ${formatMoney(-Math.abs(discount))}` : ''}
                  </span>
                </span>
              </li>
            )
          })}
        </ul>
      )}

      <div className="posm-order-drawer-totals">
        <div className="posm-order-drawer-total-row">
          <span>{t('posmOrders.drawer.itemsAmount', '商品金额')}</span>
          <span>{formatMoney(order?.totalAmount)}</span>
        </div>
        <div className="posm-order-drawer-total-row">
          <span>{t('posmOrders.discount', '折扣')}</span>
          <span>{order?.discountAmount ? formatMoney(-Math.abs(order.discountAmount)) : '—'}</span>
        </div>
        <div className="posm-order-drawer-total-row is-final">
          <span>{t('posmOrders.columns.actualPay', '实收')}</span>
          <strong>{formatMoney(orderActualAmount(order?.totalAmount, order?.discountAmount))}</strong>
        </div>
      </div>
              </>
            ),
          },
        ]}
      />

      {/* 打印用纸面：平时隐藏，window.print() 时 @media print 只输出它（80mm 纸宽）。 */}
      {open && receipt.view
        ? createPortal(
            <div id="posm-receipt-print-root">
              <ReceiptPaper document={receipt.view.document} variant="print" />
            </div>,
            document.body,
          )
        : null}
    </Drawer>
  )
}
