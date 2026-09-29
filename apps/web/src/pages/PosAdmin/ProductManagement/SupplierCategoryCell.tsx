import { EditOutlined } from '@ant-design/icons'
import { Tooltip } from 'antd'
import type { SupplierCategorySource } from '../../../types/localSupplierCategory'
import { splitSupplierCategoryPath } from './supplierCategoryOptions'

export interface SupplierCategoryCellLabels {
  unassigned: string
  manual: string
}

export interface SupplierCategoryCellProps {
  hasSupplier: boolean
  name?: string
  /** 服务端完整路径 "A > B > C"。 */
  path?: string
  source?: SupplierCategorySource
  labels: SupplierCategoryCellLabels
}

/**
 * 商品表格「供应商分类」单元格：只显示叶子名，悬停看完整路径；
 * 人工指定的分类带一个小的编辑标记；有供应商但未归类时用禁用色文字提示。
 * 文案由调用方传入，便于静态渲染测试。
 */
export default function SupplierCategoryCell({ hasSupplier, name, path, source, labels }: SupplierCategoryCellProps) {
  if (!name) {
    if (!hasSupplier) return <span>-</span>
    return <span className="pos-products-supplier-category-empty">{labels.unassigned}</span>
  }

  const segments = splitSupplierCategoryPath(path)
  const fullPath = segments.length > 1 ? segments.join(' / ') : name
  const isManual = source === 'manual'
  return (
    <Tooltip title={isManual ? `${fullPath} · ${labels.manual}` : fullPath}>
      <span className="pos-products-supplier-cell">
        {name}
        {isManual ? (
          <EditOutlined className="pos-products-source-mark" aria-label={labels.manual} />
        ) : null}
      </span>
    </Tooltip>
  )
}
