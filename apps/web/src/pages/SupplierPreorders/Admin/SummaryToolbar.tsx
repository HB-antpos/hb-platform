// 订货汇总工具条：搜索、类别、筛选芯片（数字取 counts）、分店列按钮（打开分店下拉）、排序、「对照去年」开关、单位切换、保存状态。
import { CheckOutlined, LoadingOutlined } from '@ant-design/icons'
import { Input, Popover, Segmented, Select, Switch, Tooltip } from 'antd'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrixCounts, SupplierPreorderMatrixFilter, SupplierPreorderMatrixSort } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { SORT_OPTION_VALUES, isStoreSort, type SortState } from './summarySort.logic'
import { formatInteger, type MatrixUnit } from './supplierPreorderAdmin.logic'

/** 分店箱数排序（点列头触发）在下拉里的占位值：只用来显示当前排序，不可选 */
const STORE_SORT_OPTION = '__store'

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
  sort: SortState
  /** 按分店箱数排序时，该店的名字（显示在排序框里） */
  sortStoreName: string
  onSortChange: (value: SupplierPreorderMatrixSort) => void
  /** 分店列按钮：显示 M / 开启 N 列；点它打开分店下拉 */
  shownColumnCount: number
  enabledStoreCount: number
  pickerOpen: boolean
  onPickerOpenChange: (open: boolean) => void
  pickerContent: ReactNode
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
      <Popover
        open={props.pickerOpen}
        onOpenChange={props.onPickerOpenChange}
        trigger="click"
        placement="bottomRight"
        arrow={false}
        destroyOnHidden
        classNames={{ root: 'sp-admin-sum-picker-pop' }}
        // antd 在 content 为空时会吞掉「打开」事件，所以关闭状态也要给一个非空占位
        content={props.pickerContent ?? <span />}
      >
        <button type="button" className={props.pickerOpen ? 'sp-admin-sum-storebtn is-open' : 'sp-admin-sum-storebtn'} aria-haspopup="dialog" aria-expanded={props.pickerOpen}>
          <span>
            {t('spAdmin.summary.storeButton')} <b className="sp-admin-number">{props.shownColumnCount} / {props.enabledStoreCount}</b> {t('spAdmin.summary.storeButtonUnit')}
          </span>
          <i className="sp-admin-sum-storebtn-sep" aria-hidden="true">|</i>
          <span className="sp-admin-sum-storebtn-add">＋ {t('spAdmin.summary.addStore')}</span>
        </button>
      </Popover>
      <Select<string>
        className="sp-admin-sum-sort"
        value={isStoreSort(props.sort) ? STORE_SORT_OPTION : props.sort.sort}
        onChange={(value) => props.onSortChange(value as SupplierPreorderMatrixSort)}
        prefix={<span className="sp-admin-sum-sort-prefix">{t('spAdmin.summary.sortPrefix')}</span>}
        optionLabelProp="short"
        popupMatchSelectWidth={false}
        aria-label={t('spAdmin.summary.sortAria')}
        options={[
          ...(isStoreSort(props.sort)
            ? [
                {
                  value: STORE_SORT_OPTION,
                  disabled: true,
                  short: t('spAdmin.summary.sortStoreShort', { store: props.sortStoreName, dir: props.sort.sort === 'storeCartonsDesc' ? '▼' : '▲' }),
                  label: t('spAdmin.summary.sortStoreActive', { store: props.sortStoreName }),
                },
              ]
            : []),
          ...SORT_OPTION_VALUES.map((value) => ({
            value,
            short: t(`spAdmin.summary.sortShort.${value}`),
            label: (
              <span className="sp-admin-sum-sort-opt">
                <span>{t(`spAdmin.summary.sortLabel.${value}`)}</span>
                <span className="sp-admin-sum-sort-desc">{t(`spAdmin.summary.sortDesc.${value}`)}</span>
              </span>
            ),
          })),
        ]}
        popupRender={(menu) => (
          <>
            {menu}
            <div className="sp-admin-sum-sort-foot">{t('spAdmin.summary.sortFoot')}</div>
          </>
        )}
      />
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
