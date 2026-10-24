// 修订版相关的小组件（管理端）：商品行上的修订标记、已下架标签、改动明细（价格涨红跌绿）。
import { Tag, Tooltip } from 'antd'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderRevisionChange, SupplierPreorderRevisionMark } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { buildRevisionChips, getPriceDirection, getRevisionChipColor, type RevisionChip } from './revision.logic'
import { formatInteger, formatMoney } from './supplierPreorderAdmin.logic'

function useChangeNames() {
  const { t } = useTranslation()
  return (changes: SupplierPreorderRevisionChange[]) =>
    changes.map((change) => t(`spAdmin.revision.change.${change}`)).join(t('spAdmin.common.listSep'))
}

function chipLabel(t: ReturnType<typeof useTranslation>['t'], chip: RevisionChip): string {
  switch (chip.kind) {
    case 'added':
      return t('spAdmin.revision.chip.added')
    case 'restored':
      return t('spAdmin.revision.chip.restored')
    case 'price':
      return chip.from === null
        ? t('spAdmin.revision.chip.priceNoFrom')
        : t('spAdmin.revision.chip.price', { from: formatMoney(chip.from), to: formatMoney(chip.to) })
    case 'carton':
      return chip.from === null
        ? t('spAdmin.revision.chip.cartonNoFrom')
        : t('spAdmin.revision.chip.carton', { from: formatInteger(chip.from), to: formatInteger(chip.to) })
    case 'name':
      return t('spAdmin.revision.chip.name')
    default:
      return t('spAdmin.revision.chip.other')
  }
}

/** 商品行上的修订标记：紫色 = 这一版新增 / 重新上架；橙色 = 改价、改箱规、改名；灰色 = 其他改动（悬停看明细）。 */
export function RevisionChips({ mark, price, cartonQty }: { mark?: SupplierPreorderRevisionMark | null; price: number; cartonQty: number }) {
  const { t } = useTranslation()
  const changeNames = useChangeNames()
  const chips = buildRevisionChips(mark, { price, cartonQty })
  if (!chips.length || !mark) return null
  const versionTip = t('spAdmin.revision.chip.tip', { revision: mark.revision })
  return (
    <span className="sp-admin-revision-chips">
      {chips.map((chip) => (
        <Tooltip
          key={chip.kind}
          title={
            chip.kind === 'other' ? (
              <>
                <div>{versionTip}</div>
                <div>{t('spAdmin.revision.chip.otherTip', { fields: changeNames(chip.changes) })}</div>
              </>
            ) : (
              versionTip
            )
          }
        >
          <Tag color={getRevisionChipColor(chip)} className="sp-admin-tag sp-admin-number">
            {chipLabel(t, chip)}
          </Tag>
        </Tooltip>
      ))}
    </span>
  )
}

/** 已下架（修订版里被删掉）标签 */
export function RemovedTag() {
  const { t } = useTranslation()
  return (
    <Tooltip title={t('spAdmin.revision.removedTip')}>
      <Tag className="sp-admin-tag sp-admin-revision-removed-tag">{t('spAdmin.revision.removedTag')}</Tag>
    </Tooltip>
  )
}

/** 价格改动：$1.10 → $1.20，涨价红、降价绿 */
export function PriceChange({ from, to }: { from?: number | null; to?: number | null }) {
  const direction = getPriceDirection(from, to)
  const className = direction === 'up' ? 'sp-admin-price-up' : direction === 'down' ? 'sp-admin-price-down' : undefined
  return (
    <span className="sp-admin-number">
      <span className="sp-admin-muted">{formatMoney(from ?? null)}</span>
      {' → '}
      <span className={className}>{formatMoney(to ?? null)}</span>
    </span>
  )
}
