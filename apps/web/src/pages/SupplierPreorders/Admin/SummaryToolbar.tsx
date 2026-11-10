// 订货汇总工具条：搜索、类别、筛选芯片（数字取 counts）、「对照去年」开关、单位切换、保存状态。
import { CheckOutlined, LoadingOutlined } from '@ant-design/icons'
import { Input, Segmented, Select, Switch, Tooltip } from 'antd'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrixCounts, SupplierPreorderMatrixFilter } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { formatInteger, type MatrixUnit } from './supplierPreorderAdmin.logic'

interface SummaryToolbarProps {
  keywordInput: string
  onKeywordInputChange: (value: string) => void
  onSearch: (value: string) => void
  category: string
  categories: string[]
  onCategoryChange: (value: string) => void
  filter: SupplierPreorderMatrixFilter
  counts: SupplierPreorderMatrixCounts | null
  onFilterChange: (value: SupplierPreorderMatrixFilter) => void
  compare: boolean
  onCompareChange: (value: boolean) => void
  unit: MatrixUnit
  onUnitChange: (value: MatrixUnit) => void
  editable: boolean
  busyCount: number
  savedAtText: string | null
}

export default function SummaryToolbar(props: SummaryToolbarProps) {
  const { t } = useTranslation()
  const { counts, filter, unit, editable, busyCount, savedAtText } = props
  const chips: { key: SupplierPreorderMatrixFilter; label: string; count?: number; warn?: boolean }[] = [
    { key: 'all', label: t('spAdmin.summary.filterAll'), count: counts?.total },
    { key: 'ordered', label: t('spAdmin.summary.filterOrdered'), count: counts?.ordered },
    { key: 'missed', label: t('spAdmin.summary.filterMissed'), count: counts?.missed, warn: true },
    { key: 'adjusted', label: t('spAdmin.summary.filterAdjusted'), count: counts?.adjusted },
  ]

  return (
    <div className="sp-admin-sum-toolbar">
      <Input.Search
        className="sp-admin-sum-search"
        allowClear
        value={props.keywordInput}
        placeholder={t('spAdmin.summary.searchPlaceholder')}
        onChange={(event) => props.onKeywordInputChange(event.target.value)}
        onSearch={props.onSearch}
      />
      <Select
        className="sp-admin-sum-category"
        value={props.category}
        onChange={props.onCategoryChange}
        options={[{ value: '', label: t('spAdmin.summary.allCategories') }, ...props.categories.map((item) => ({ value: item, label: item }))]}
        popupMatchSelectWidth={false}
        showSearch
        optionFilterProp="label"
        aria-label={t('spAdmin.summary.categoryAria')}
      />
      <div className="sp-admin-sum-chips" role="group" aria-label={t('spAdmin.summary.filterAria')}>
        {chips.map((chip) => (
          <button
            key={chip.key}
            type="button"
            className={['sp-admin-sum-chip', filter === chip.key ? 'is-on' : '', chip.warn ? 'is-warn' : ''].filter(Boolean).join(' ')}
            aria-pressed={filter === chip.key}
            onClick={() => props.onFilterChange(chip.key)}
          >
            {chip.label} <b className="sp-admin-number">{chip.count === undefined ? '--' : formatInteger(chip.count)}</b>
          </button>
        ))}
      </div>
      <span className="sp-admin-toolbar-spacer" />
      <label className="sp-admin-switch-label sp-admin-small">
        <Switch size="small" checked={props.compare} onChange={props.onCompareChange} />
        <span>{t('spAdmin.summary.compareLastYear')}</span>
      </label>
      <Tooltip title={unit !== 'cartons' ? t('spAdmin.summary.unitReadOnlyHint') : !editable ? t('spAdmin.summary.readOnlyHint') : undefined}>
        <Segmented<MatrixUnit>
          size="small"
          value={unit}
          onChange={props.onUnitChange}
          options={[
            { value: 'cartons', label: t('spAdmin.summary.unit.cartons') },
            { value: 'pieces', label: t('spAdmin.summary.unit.pieces') },
            { value: 'amount', label: t('spAdmin.summary.unit.amount') },
          ]}
        />
      </Tooltip>
      <span className="sp-admin-sum-save-state sp-admin-small" role="status" aria-live="polite">
        {busyCount > 0 ? (
          <>
            <LoadingOutlined /> {t('spAdmin.summary.saving', { count: busyCount })}
          </>
        ) : !editable ? (
          <span className="sp-admin-muted">{t('spAdmin.summary.readOnlyBadge')}</span>
        ) : savedAtText ? (
          <>
            <CheckOutlined className="sp-admin-sum-ok" /> {t('spAdmin.summary.allSaved', { time: savedAtText })}
          </>
        ) : (
          <span className="sp-admin-muted">{t('spAdmin.summary.autoSaveHint')}</span>
        )}
      </span>
    </div>
  )
}
