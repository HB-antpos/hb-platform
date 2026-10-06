import { useTranslation } from 'react-i18next'
import type { ImportStatusCounts, ImportStatusFilter } from './importGridLogic'

interface ImportStatusChipsProps {
  counts: ImportStatusCounts
  active: ImportStatusFilter
  onChange: (filter: ImportStatusFilter) => void
}

/** 色调只决定数字颜色 / 选中态边框，与表格行色一一对应。 */
const CHIPS: Array<{ filter: ImportStatusFilter; tone: string; labelKey: string; defaultLabel: string }> = [
  { filter: 'all', tone: 'all', labelKey: 'productImport.filterAll', defaultLabel: '全部' },
  { filter: 'new', tone: 'new', labelKey: 'productImport.filterNew', defaultLabel: '新' },
  { filter: 'updated', tone: 'updated', labelKey: 'productImport.filterUpdated', defaultLabel: '更新' },
  { filter: 'unchanged', tone: 'unchanged', labelKey: 'productImport.filterUnchanged', defaultLabel: '无变化' },
  { filter: 'duplicate', tone: 'duplicate', labelKey: 'productImport.filterDuplicate', defaultLabel: '重复' },
  { filter: 'dbDuplicate', tone: 'dbDuplicate', labelKey: 'productImport.filterDbDuplicate', defaultLabel: '库内重复' },
  { filter: 'error', tone: 'error', labelKey: 'productImport.filterError', defaultLabel: '错误' },
  { filter: 'sent', tone: 'sent', labelKey: 'productImport.filterSent', defaultLabel: '已发送' },
]

/** 状态统计条：每个计数都是过滤按钮，再点一次已选中的回到「全部」。 */
export function ImportStatusChips({ counts, active, onChange }: ImportStatusChipsProps) {
  const { t } = useTranslation()
  return (
    <div className="pi-chips" role="group" aria-label={t('productImport.chipsAriaLabel', '按状态筛选')} data-testid="product-import-status-chips">
      {CHIPS.map((chip) => {
        const isActive = active === chip.filter
        const count = counts[chip.filter]
        return (
          <button
            key={chip.filter}
            type="button"
            className={`pi-chip pi-chip-${chip.tone}${isActive ? ' is-active' : ''}${count === 0 && chip.filter !== 'all' ? ' is-zero' : ''}`}
            aria-pressed={isActive}
            data-filter={chip.filter}
            onClick={() => onChange(isActive && chip.filter !== 'all' ? 'all' : chip.filter)}
          >
            {t(chip.labelKey, chip.defaultLabel)}
            <b>{count}</b>
          </button>
        )
      })}
    </div>
  )
}
