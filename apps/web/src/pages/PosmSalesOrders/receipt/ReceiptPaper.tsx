import { QRCodeSVG } from '@rc-component/qrcode'
import { memo, useMemo } from 'react'

import type { PosReceiptDocument, PosReceiptLine } from '../../../types/posmSalesOrder'

import { encodeCode128 } from './code128'
import './receiptPaper.css'

/** 80mm 热敏纸可打印宽度 576 点 = 42 列；条码与二维码的物理尺寸都按这个比例换算成 ch，字号变化时与文字列宽同步缩放。 */
const PRINTABLE_DOTS = 576
/** 条码规格与收银机一致：3 点 / 模块、高 80 点（见收银端「单号一维条码」设计）。 */
const BARCODE_MODULE_DOTS = 3
const BARCODE_HEIGHT_DOTS = 80
const QR_DOTS = 200

const dotsToCh = (dots: number) => `${(dots / PRINTABLE_DOTS) * 42}ch`

function Barcode({ value }: { value: string }) {
  const encoding = useMemo(() => {
    try {
      return encodeCode128(value)
    } catch {
      return null
    }
  }, [value])
  if (!encoding) return null

  // 条的位置累加成一条 path，比逐个 rect 轻很多。
  let x = 0
  let path = ''
  encoding.runs.forEach((run, index) => {
    if (index % 2 === 0) path += `M${x} 0h${run}v1h-${run}z`
    x += run
  })
  return (
    <svg
      className="posm-receipt-barcode"
      role="img"
      aria-label={value}
      viewBox={`0 0 ${encoding.moduleCount} 1`}
      preserveAspectRatio="none"
      shapeRendering="crispEdges"
      style={{
        width: dotsToCh(encoding.moduleCount * BARCODE_MODULE_DOTS),
        height: dotsToCh(BARCODE_HEIGHT_DOTS),
      }}
    >
      <path d={path} fill="currentColor" />
    </svg>
  )
}

function Line({ line }: { line: PosReceiptLine }) {
  switch (line.kind) {
    case 'blank':
      return <div className="posm-receipt-row">{' '}</div>
    case 'barcode':
      return (
        <div className="posm-receipt-row posm-receipt-machine">
          <Barcode value={line.text} />
        </div>
      )
    case 'qr':
      return (
        <div className="posm-receipt-row posm-receipt-machine">
          <QRCodeSVG
            value={line.text}
            size={256}
            level="M"
            marginSize={0}
            title="QR code"
            style={{ width: dotsToCh(QR_DOTS), height: dotsToCh(QR_DOTS) }}
          />
        </div>
      )
    default:
      return (
        <div
          className={['posm-receipt-row', line.align === 'center' ? 'is-center' : '', line.bold ? 'is-bold' : '']
            .filter(Boolean)
            .join(' ')}
        >
          {line.text}
        </div>
      )
  }
}

interface ReceiptPaperProps {
  document: PosReceiptDocument
  /** print：去掉阴影与锯齿，按 80mm 纸宽输出（@media print 用）。 */
  variant?: 'screen' | 'print'
}

/**
 * 小票纸面：后端已把整张小票排成逐行内容，这里只按行画。
 * 纸宽固定 42 个等宽字符（42ch），窄屏时由外层等比缩放而不是折行，保证金额列不错位。
 */
function ReceiptPaper({ document, variant = 'screen' }: ReceiptPaperProps) {
  return (
    <div className={`posm-receipt-paper-wrap${variant === 'print' ? ' is-print' : ''}`}>
      <div className="posm-receipt-paper" data-status={document.statusKind}>
        {document.lines.map((line, index) => (
          <Line key={`${index}-${line.kind}`} line={line} />
        ))}
      </div>
    </div>
  )
}

export default memo(ReceiptPaper)
