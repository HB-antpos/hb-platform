import { CopyOutlined, DownloadOutlined, QrcodeOutlined } from '@ant-design/icons'
import { QRCodeSVG } from '@rc-component/qrcode'
import { Button, Popconfirm, Popover, Spin, message } from 'antd'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { PosReceiptShareLink } from '../../../types/posmSalesOrder'

interface ReceiptQrPopoverProps {
  orderGuid: string
  /** 当前有效链接；没有则首次打开气泡时才生成。 */
  link: PosReceiptShareLink | null
  disabled?: boolean
  onEnsure: () => Promise<PosReceiptShareLink>
  onRegenerate: () => Promise<PosReceiptShareLink>
}

/** 把页面里的 SVG 二维码栅格化成 PNG 再下载：加白底和静区，手机相册 / 打印软件都能直接用。 */
async function downloadSvgAsPng(svg: SVGSVGElement, filename: string) {
  const size = 512
  const margin = 32
  const source = new XMLSerializer().serializeToString(svg)
  const image = new Image()
  const url = URL.createObjectURL(new Blob([source], { type: 'image/svg+xml;charset=utf-8' }))
  try {
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve()
      image.onerror = () => reject(new Error('QR image load failed'))
      image.src = url
    })
    const canvas = document.createElement('canvas')
    canvas.width = size + margin * 2
    canvas.height = size + margin * 2
    const context = canvas.getContext('2d')
    if (!context) throw new Error('Canvas unavailable')
    context.fillStyle = '#fff'
    context.fillRect(0, 0, canvas.width, canvas.height)
    context.imageSmoothingEnabled = false
    context.drawImage(image, margin, margin, size, size)
    const link = document.createElement('a')
    link.download = filename
    link.href = canvas.toDataURL('image/png')
    link.click()
  } finally {
    URL.revokeObjectURL(url)
  }
}

export default function ReceiptQrPopover({ orderGuid, link, disabled, onEnsure, onRegenerate }: ReceiptQrPopoverProps) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState(false)
  const qrRef = useRef<HTMLDivElement>(null)

  const ensure = async () => {
    setBusy(true)
    setFailed(false)
    try {
      await onEnsure()
    } catch {
      setFailed(true)
    } finally {
      setBusy(false)
    }
  }

  const handleOpenChange = (next: boolean) => {
    setOpen(next)
    // 首次打开才生成链接：没人点二维码的订单不会白白产生一条公开链接。
    if (next && !link) void ensure()
  }

  const copyLink = async () => {
    if (!link) return
    try {
      await navigator.clipboard.writeText(link.url)
      message.success(t('posmReceipt.copied', '链接已复制'))
    } catch {
      message.error(t('posmReceipt.copyFailed', '复制失败，请手动选择链接'))
    }
  }

  const regenerate = async () => {
    setBusy(true)
    try {
      await onRegenerate()
      message.success(t('posmReceipt.regenerated', '已重新生成，旧链接已失效'))
    } catch {
      message.error(t('posmReceipt.createFailed', '生成链接失败，请重试'))
    } finally {
      setBusy(false)
    }
  }

  const downloadQr = () => {
    const svg = qrRef.current?.querySelector('svg')
    if (svg) void downloadSvgAsPng(svg, `receipt-${orderGuid.slice(-8).toUpperCase()}.png`)
  }

  const content = (
    <div className="posm-receipt-popover">
      <div className="posm-receipt-popover-title">
        {t('posmReceipt.popoverTitle', '在线小票')}
        <small>{t('posmReceipt.popoverHint', '客人扫码即可查看，无需登录')}</small>
      </div>
      {link ? (
        <>
          <div className="posm-receipt-popover-qr" ref={qrRef}>
            <QRCodeSVG value={link.url} size={168} level="M" marginSize={0} title="Online receipt QR code" />
          </div>
          <div className="posm-receipt-popover-url">{link.url}</div>
          <div className="posm-receipt-popover-actions">
            <Button size="small" icon={<CopyOutlined />} onClick={() => void copyLink()}>
              {t('posmReceipt.copyLink', '复制链接')}
            </Button>
            <Button size="small" icon={<DownloadOutlined />} onClick={downloadQr}>
              {t('posmReceipt.downloadQr', '下载二维码')}
            </Button>
          </div>
          <div className="posm-receipt-popover-meta">
            <span>
              {t('posmReceipt.meta', {
                defaultValue: '有效至 {{date}} · 已打开 {{count}} 次',
                date: new Date(link.expiresAtUtc).toLocaleDateString('en-CA'),
                count: link.openCount,
              })}
            </span>
            <Popconfirm
              title={t('posmReceipt.regenerateConfirm', '重新生成后旧链接立即失效，确定吗？')}
              onConfirm={() => void regenerate()}
            >
              <Button type="link" size="small" danger loading={busy}>
                {t('posmReceipt.regenerate', '重新生成')}
              </Button>
            </Popconfirm>
          </div>
        </>
      ) : failed ? (
        <div className="posm-receipt-popover-state">
          {t('posmReceipt.createFailed', '生成链接失败，请重试')}
          <Button size="small" onClick={() => void ensure()}>
            {t('posmReceipt.retry', '重试')}
          </Button>
        </div>
      ) : (
        <div className="posm-receipt-popover-state">
          <Spin size="small" /> {t('posmReceipt.creating', '正在生成链接…')}
        </div>
      )}
    </div>
  )

  return (
    <Popover trigger="click" placement="topLeft" open={open} onOpenChange={handleOpenChange} content={content}>
      <Button icon={<QrcodeOutlined />} disabled={disabled}>
        {t('posmReceipt.openQr', '在线二维码')}
      </Button>
    </Popover>
  )
}
