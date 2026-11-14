import { Select } from 'antd'
import { useTranslation } from 'react-i18next'

import { usePackingPage } from './packingShared'

interface LocationSelectProps {
  /** 位置名称；为空表示没放到具体位置。 */
  value?: string | null
  onChange: (value: string | null) => void
  disabled?: boolean
  placeholder?: string
  className?: string
  loading?: boolean
}

/** 存放位置选择：当前地点启用中的位置（分店是本店自己维护的位置，总仓是现有货位）；可清除。 */
export default function LocationSelect({ value, onChange, disabled, placeholder, className, loading }: LocationSelectProps) {
  const { t } = useTranslation()
  const { locations } = usePackingPage()
  const options = locations.map((location) => ({ value: location.name, label: location.name }))
  // 当前值可能是已停用的位置：补一个选项保证能显示。
  if (value && !options.some((option) => option.value === value)) {
    options.unshift({ value, label: value })
  }
  return (
    <Select
      showSearch
      allowClear
      className={className}
      disabled={disabled}
      loading={loading}
      optionFilterProp="label"
      placeholder={placeholder ?? t('packingUi.location.placeholder')}
      value={value ?? undefined}
      options={options}
      notFoundContent={t('packingUi.location.none')}
      onChange={(next) => onChange(next ?? null)}
    />
  )
}
