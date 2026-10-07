import { TableOutlined } from '@ant-design/icons'
import { Button, Checkbox, Popover, Tooltip, Typography } from 'antd'
import type { TFunction } from 'i18next'
import type { ReactNode } from 'react'

export interface ColumnSettingsOption {
  key: string
  label: ReactNode
}

interface ColumnSettingsButtonProps {
  /** 可勾选显示的低频列；常驻列不出现在这里。 */
  options: ColumnSettingsOption[]
  visibleKeys: string[]
  onChange: (keys: string[]) => void
  /** 列顺序或显示列与默认不同时才可恢复默认。 */
  canReset: boolean
  onReset: () => void
  t: TFunction
}

/**
 * 仓库商品表格的列设置：勾选显示英文名称、装箱数、更新时间等低频列；
 * 列顺序仍通过拖动表头调整，「重置列」同时恢复默认显示列与默认顺序。
 */
export default function ColumnSettingsButton({ options, visibleKeys, onChange, canReset, onReset, t }: ColumnSettingsButtonProps) {
  const content = (
    <div className="warehouse-products-column-settings">
      <Typography.Text strong className="warehouse-products-column-settings-title">
        {t('warehouseUi.products.columnSettingsTitle')}
      </Typography.Text>
      <Checkbox.Group
        className="warehouse-products-column-settings-list"
        value={visibleKeys}
        options={options.map((option) => ({ label: option.label, value: option.key }))}
        onChange={(values) => onChange(values.map(String))}
      />
      <Typography.Text type="secondary" className="warehouse-products-column-settings-hint">
        {t('warehouseUi.products.columnSettingsDragHint')}
      </Typography.Text>
      <div className="warehouse-products-column-settings-footer">
        <Button size="small" disabled={!canReset} onClick={onReset}>
          {t('warehouse.resetColumns', '重置列')}
        </Button>
      </div>
    </div>
  )

  return (
    <Popover trigger="click" placement="bottomRight" content={content}>
      <Tooltip title={t('common.listToolbar.columnSettings', '列设置')}>
        <Button
          icon={<TableOutlined />}
          aria-label={t('common.listToolbar.columnSettings', '列设置')}
        />
      </Tooltip>
    </Popover>
  )
}
