import { Switch } from 'antd'
import { useTranslation } from 'react-i18next'

interface ExistingValuesToggleProps {
  checked: boolean
  onChange: (checked: boolean) => void
}

/**
 * 状态条右侧：「显示库内已有值」开关 + 图例。
 * 开关只决定表格里有没有灰字占位，不动任何数据；关掉就回到检测后原来的样子（没填的格子全空）。
 * 图例只在打开时出现：灰格样式关了就不存在，也就不需要解释。
 */
export function ExistingValuesToggle({ checked, onChange }: ExistingValuesToggleProps) {
  const { t } = useTranslation()
  const label = t('productImport.existingToggle', '显示库内已有值')
  return (
    <div className="pi-existing-toggle" data-testid="product-import-existing-toggle">
      {checked ? (
        <span className="pi-legend" aria-hidden="true">
          <span><i className="pi-legend-swatch is-existing" />{t('productImport.legendExisting', '库内已有（未填写，保持不变）')}</span>
          <span><i className="pi-legend-swatch is-changed" />{t('productImport.legendChanged', '本次改动')}</span>
        </span>
      ) : null}
      {/* label 包住 Switch：点文字也能切换 */}
      <label className="pi-existing-switch">
        <Switch size="small" checked={checked} onChange={onChange} aria-label={label} />
        <span>{label}</span>
      </label>
    </div>
  )
}
