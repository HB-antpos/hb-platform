import { PictureOutlined } from '@ant-design/icons'
import { Typography } from 'antd'
import type { CSSProperties } from 'react'

import ProductListImage from '../../../components/ProductListImage'
import type { OperationAuditListItem } from '../../../types/operationAudit'

import { primaryItemNumberOf, summarizeProductName } from './operationLogsLogic'

/** 主档图片失效（如文件名带空格、已删除）时显示的灰底占位，不出现破图。 */
const IMAGE_FALLBACK =
  'data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0MCIgaGVpZ2h0PSI0MCI+PHJlY3Qgd2lkdGg9IjQwIiBoZWlnaHQ9IjQwIiBmaWxsPSIjZjVmNWY1Ii8+PC9zdmc+'

interface PosProductSummaryProps {
  record: Pick<OperationAuditListItem, 'productCount' | 'primaryProduct' | 'primaryItemNumber' | 'primaryProductImage'>
  fallbackName: string
  imageSize: number
  nameStyle?: CSSProperties
}

/**
 * 新收银操作的商品摘要：缩略图 + 商品名（+N）+ 货号。
 * 没有主档图片时保留同尺寸占位，让整列文字对齐；点缩略图可看原图。
 */
export default function PosProductSummary({ record, fallbackName, imageSize, nameStyle }: PosProductSummaryProps) {
  const name = summarizeProductName(record, fallbackName)
  if (name === '-') return <span>-</span>
  const itemNumber = primaryItemNumberOf(record)
  const image = record.primaryProductImage?.trim()
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
      <span
        style={{
          flex: '0 0 auto',
          width: imageSize,
          height: imageSize,
          overflow: 'hidden',
          borderRadius: 6,
          background: '#f5f5f5',
          color: '#bfbfbf',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
        }}
      >
        {image ? (
          <ProductListImage src={image} size={imageSize} radius={6} previewMask="" fallback={IMAGE_FALLBACK} />
        ) : (
          <PictureOutlined aria-hidden="true" />
        )}
      </span>
      <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0, gap: 2 }}>
        <span style={{ whiteSpace: 'normal', overflowWrap: 'anywhere', ...nameStyle }}>{name}</span>
        {itemNumber ? (
          <Typography.Text type="secondary" style={{ fontSize: 12, fontVariantNumeric: 'tabular-nums' }}>
            {itemNumber}
          </Typography.Text>
        ) : null}
      </span>
    </div>
  )
}
