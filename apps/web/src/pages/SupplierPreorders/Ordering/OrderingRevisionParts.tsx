// 下单页的修订版小组件：商品行上的修订标记、本店订过但已下架商品的顶部提示。
// 标记推导复用管理端 revision.logic 的纯函数（不引用管理端的 React 组件）。
import { Alert, Button, Tag, Tooltip } from 'antd'
import type { TFunction } from 'i18next'
import { useState } from 'react'

import type { SupplierPreorderRemovedLine, SupplierPreorderRevisionMark } from '../../../types/supplierPreorder'
import { buildRevisionChips, getRevisionChipColor, type RevisionChip } from '../Admin/revision.logic'

import { formatAud, formatInteger } from './orderingPage.logic'
import { summarizeRemovedLines } from './orderingRevision.logic'

const NS = 'supplierPreorderOrdering'

function chipLabel(t: TFunction, chip: RevisionChip): string {
  switch (chip.kind) {
    case 'added':
      return t(`${NS}.revision.chip.added`)
    case 'restored':
      return t(`${NS}.revision.chip.restored`)
    case 'price':
      return chip.from === null
        ? t(`${NS}.revision.chip.priceNoFrom`)
        : t(`${NS}.revision.chip.price`, { from: formatAud(chip.from), to: formatAud(chip.to) })
    case 'carton':
      return chip.from === null
        ? t(`${NS}.revision.chip.cartonNoFrom`)
        : t(`${NS}.revision.chip.carton`, { from: formatInteger(chip.from), to: formatInteger(chip.to) })
    case 'name':
      return t(`${NS}.revision.chip.name`)
    default:
      return t(`${NS}.revision.chip.other`)
  }
}

/** 紫色 = 这一版新增 / 重新上架；橙色 = 改价、改箱规、改名；灰色 = 其他改动（悬停看明细） */
export function OrderingRevisionChips({ t, mark, price, cartonQty }: { t: TFunction; mark?: SupplierPreorderRevisionMark | null; price: number; cartonQty: number }) {
  const chips = buildRevisionChips(mark, { price, cartonQty })
  if (!chips.length || !mark) return null
  const versionTip = t(`${NS}.revision.chip.tip`, { revision: mark.revision })
  return (
    <>
      {chips.map((chip) => (
        <Tooltip
          key={chip.kind}
          title={
            chip.kind === 'other' ? (
              <>
                <div>{versionTip}</div>
                <div>
                  {t(`${NS}.revision.chip.otherTip`, {
                    fields: chip.changes.map((change) => t(`${NS}.revision.change.${change}`)).join(t(`${NS}.revision.listSep`)),
                  })}
                </div>
              </>
            ) : (
              versionTip
            )
          }
        >
          <Tag color={getRevisionChipColor(chip)} className="sp-order-revision-tag">
            {chipLabel(t, chip)}
          </Tag>
        </Tooltip>
      ))}
    </>
  )
}

/** 页面顶部：本店订过的 N 款已被供应商下架（箱数保留、不计入合计），太多时先列前几款，可展开。 */
export function RemovedLinesAlert({ t, lines }: { t: TFunction; lines?: SupplierPreorderRemovedLine[] | null }) {
  const [expanded, setExpanded] = useState(false)
  const summary = summarizeRemovedLines(lines, expanded)
  if (!summary) return null
  const canToggle = expanded || summary.hiddenCount > 0
  return (
    <Alert
      className="sp-order-alert"
      type="warning"
      showIcon
      message={t(`${NS}.revision.removedTitle`, { count: summary.count, cartons: formatInteger(summary.totalCartons) })}
      description={
        <div className="sp-order-removed-lines">
          {summary.shown.map((line) => (
            <span key={line.itemGuid} className="sp-order-removed-line sp-order-number">
              {t(`${NS}.revision.removedLine`, { code: line.supplierItemCode, name: line.name, cartons: formatInteger(line.cartons) })}
            </span>
          ))}
          {canToggle ? (
            <Button type="link" size="small" className="sp-order-removed-toggle" aria-expanded={expanded} onClick={() => setExpanded((value) => !value)}>
              {expanded ? t(`${NS}.revision.removedCollapse`) : t(`${NS}.revision.removedExpand`, { count: summary.count })}
            </Button>
          ) : null}
        </div>
      }
    />
  )
}
