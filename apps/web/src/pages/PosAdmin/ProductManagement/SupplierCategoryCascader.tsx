import { Button, Cascader, Input, theme } from 'antd'
import type { CSSProperties, ReactNode } from 'react'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { formatCascaderDisplayLabels } from './supplierCategoryFilter'
import { searchSupplierCategoryOptions, SUPPLIER_CATEGORY_RETRY_VALUE, type SupplierCategoryCascaderOption } from './supplierCategoryOptions'

const I18N = 'posAdmin.products.supplierCategory'

export interface SupplierCategoryCascaderProps {
  options: SupplierCategoryCascaderOption[]
  value?: string[]
  onChange: (value: string[] | undefined) => void
  /** 展开某个供应商且其分类树未加载时触发。 */
  onLoadSupplier: (supplierCode: string) => void
  /** 点击「加载失败，点击重试」伪节点时触发，不改变筛选条件。 */
  onRetrySupplier: (supplierCode: string) => void
  placeholder?: string
  style?: CSSProperties
  /**
   * 以下为可选的复用扩展（商品管理页都不传，行为不变）：
   * - prefix / size / className：输入框前缀标签、尺寸与样式类；
   * - onOpen：弹层打开时回调（懒加载供应商列表）；
   * - searchPlaceholder / searchEmptyText：覆盖弹层内搜索框与空结果的文案（默认读商品管理页的文案键）；
   * - notFoundContent：没有选项时的内容（加载中 / 加载失败）。
   */
  prefix?: ReactNode
  size?: 'small' | 'middle' | 'large'
  className?: string
  onOpen?: () => void
  searchPlaceholder?: string
  searchEmptyText?: string
  notFoundContent?: ReactNode
}

/**
 * 顶部「供应商分类」级联筛选：第一层供应商，下面是该供应商的分类树（200 为仓库分类）。
 * 分类树按供应商懒加载，内置 showSearch 与 loadData 互斥，因此在弹层内搜索已加载的节点。
 */
export default function SupplierCategoryCascader({
  options,
  value,
  onChange,
  onLoadSupplier,
  onRetrySupplier,
  placeholder,
  style,
  prefix,
  size,
  className,
  onOpen,
  searchPlaceholder,
  searchEmptyText,
  notFoundContent,
}: SupplierCategoryCascaderProps) {
  const { token } = theme.useToken()
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [searchText, setSearchText] = useState('')
  const popupRef = useRef<HTMLDivElement>(null)
  const searchResults = searchSupplierCategoryOptions(options, searchText)

  const handleChange = (nextValue: unknown) => {
    const path = Array.isArray(nextValue) ? nextValue.map(String) : []
    // 重试伪节点只触发重新加载，不能写进筛选条件。
    if (path[path.length - 1] === SUPPLIER_CATEGORY_RETRY_VALUE) {
      if (path[0]) onRetrySupplier(path[0])
      return
    }
    onChange(path.length ? path : undefined)
  }

  return (
    <Cascader
      allowClear
      changeOnSelect
      open={open}
      onOpenChange={(nextOpen) => {
        if (!nextOpen) {
          // 外部 mousedown 先于焦点转移；下一帧再判断，避免误把外部点击当成弹层内操作。
          requestAnimationFrame(() => {
            if (popupRef.current?.contains(document.activeElement)) return
            setOpen(false)
            setSearchText('')
          })
          return
        }
        setOpen(true)
        onOpen?.()
        const selectedSupplier = options.find((option) => option.value === value?.[0])
        if (nextOpen && selectedSupplier?.isLeaf === false) onLoadSupplier(selectedSupplier.value)
      }}
      placeholder={placeholder}
      style={style}
      prefix={prefix}
      size={size}
      className={className}
      notFoundContent={notFoundContent}
      options={options}
      value={value}
      onChange={handleChange}
      loadData={(selectedOptions) => {
        const supplierCode = selectedOptions[0]?.value
        if (supplierCode !== undefined && supplierCode !== null) onLoadSupplier(String(supplierCode))
      }}
      displayRender={(labels) => formatCascaderDisplayLabels(labels.map(String))}
      optionRender={(option) => {
        const { kind, label } = option as SupplierCategoryCascaderOption
        if (kind === 'unassigned') {
          return <span className="pos-products-supplier-category-unassigned-option">{label}</span>
        }
        if (kind === 'retry') {
          return <span style={{ color: token.colorError }}>{label}</span>
        }
        return label
      }}
      popupRender={(menus) => (
        <div ref={popupRef} style={{ minWidth: 280 }}>
          <div style={{ padding: 8 }}>
            <Input
              allowClear
              value={searchText}
              onChange={(event) => setSearchText(event.target.value)}
              onKeyDown={(event) => event.stopPropagation()}
              placeholder={searchPlaceholder ?? t(`${I18N}.filterSearchPlaceholder`, '搜索供应商或已加载的分类')}
            />
          </div>
          {searchText.trim() ? (
            <div style={{ maxHeight: 280, overflowY: 'auto', padding: '0 8px 8px' }}>
              {searchResults.length ? searchResults.map((result) => (
                <Button
                  key={result.valuePath.join('/')}
                  block
                  type="text"
                  onClick={() => {
                    handleChange(result.valuePath)
                    if (result.valuePath.length === 1) onLoadSupplier(result.valuePath[0])
                    setSearchText('')
                    setOpen(false)
                  }}
                  style={{ display: 'block', height: 'auto', textAlign: 'left', whiteSpace: 'normal' }}
                >
                  {result.labelPath.join(' / ')}
                </Button>
              )) : (
                <div style={{ padding: 8, color: token.colorTextSecondary }}>
                  {searchEmptyText ?? t(`${I18N}.filterSearchEmpty`, '没有匹配项；请先展开供应商以加载分类')}
                </div>
              )}
            </div>
          ) : menus}
        </div>
      )}
    />
  )
}
