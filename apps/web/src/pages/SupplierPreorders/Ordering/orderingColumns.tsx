// 下单页表格列定义集中在这里。
// 「箱规」与「其他分店已下单」之间是去年本店四列：进货 / 销售 / 日销曲线 / 折扣（PR 2）；
// 没有数据时四格合并成一个占位，参考商品的数据整格紫色底。展开大图见 OrderingHistoryExpand。
import { LoadingOutlined, MinusOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, Image, Input, Space, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { TFunction } from 'i18next'
import { memo, useEffect, useState } from 'react'

import type {
  SupplierPreorderHistoryState,
  SupplierPreorderKeyDate,
  SupplierPreorderOrderingItem,
} from '../../../types/supplierPreorder'
import { toProductThumbnailUrl } from '../../../utils/productImageThumbnail'

import { historyCellState } from './orderingHistory.logic'
import { DiscountCell, HistoryPlaceholder, HistorySparkline, PurchaseCell, RefBadge, SalesCell } from './orderingHistoryCells'
import {
  computeLineTotals,
  formatAud,
  formatInteger,
  parseCartonsInput,
  stepCartons,
} from './orderingPage.logic'

const NS = 'supplierPreorderOrdering'

/** 列表缩略图显示 52px，按 2 倍像素密度取图 */
const THUMB_SIZE = 104

export interface OrderingColumnContext {
  t: TFunction
  canViewOtherStores: boolean
  canEdit: boolean
  /** 当前显示的本店箱数（含尚未保存完成的本地值） */
  getDisplayCartons: (row: SupplierPreorderOrderingItem) => number
  isSaving: (itemGuid: string) => boolean
  onCommit: (row: SupplierPreorderOrderingItem, cartons: number) => void
  onInvalidInput: () => void
  /** 去年历史快照状态：没有数据的行据此显示「计算中」还是「无去年记录」 */
  historyStatus?: SupplierPreorderHistoryState | null
  /** 历史窗口起点（日期字符串，按日期处理）；曲线下标 0 = 这一天 */
  historyWindowFrom?: string | null
  /** 历史窗口天数（含首尾）；迷你曲线的横轴长度 */
  historyWindowDays: number | null
  /** 节日关键日期（迷你曲线紫色虚线） */
  historyKeyDates: readonly SupplierPreorderKeyDate[]
}

/** 列 key 常量：以后插列、隐藏列都按 key 处理 */
export const ORDERING_COLUMN_KEYS = {
  seq: 'seq',
  image: 'image',
  product: 'product',
  price: 'price',
  carton: 'carton',
  lastYearPurchase: 'lastYearPurchase',
  lastYearSales: 'lastYearSales',
  lastYearCurve: 'lastYearCurve',
  discount: 'discount',
  otherStores: 'otherStores',
  mine: 'mine',
} as const

export function buildOrderingColumns(ctx: OrderingColumnContext): ColumnsType<SupplierPreorderOrderingItem> {
  const { t } = ctx
  const columns: ColumnsType<SupplierPreorderOrderingItem> = [
    {
      key: ORDERING_COLUMN_KEYS.seq,
      title: t(`${NS}.table.colSeq`),
      width: 56,
      render: (_, row) => <span className="sp-order-seq">#{row.seq}</span>,
    },
    {
      key: ORDERING_COLUMN_KEYS.image,
      title: t(`${NS}.table.colImage`),
      width: 76,
      render: (_, row) => <ProductThumb row={row} />,
    },
    {
      key: ORDERING_COLUMN_KEYS.product,
      title: t(`${NS}.table.colProduct`),
      render: (_, row) => (
        <div className="sp-order-product">
          <div className="sp-order-product-name" title={row.name}>
            {row.name}
          </div>
          <div className="sp-order-product-codes sp-order-mono">
            {row.supplierItemCode}
            {row.barcode ? ` · ${row.barcode}` : null}
          </div>
          {row.category || row.isNew || row.history?.source === 'Ref' ? (
            <div className="sp-order-product-meta">
              {row.category ? <span>{row.category}</span> : null}
              {row.isNew ? <Tag color="orange">{t(`${NS}.table.newTag`)}</Tag> : null}
              {/* 新品借用参考商品的去年数据：标「参考 XXX」，悬停看参考商品 */}
              {row.history?.source === 'Ref' ? <RefBadge t={t} history={row.history} ownPrice={row.price} /> : null}
            </div>
          ) : null}
        </div>
      ),
    },
    {
      key: ORDERING_COLUMN_KEYS.price,
      title: t(`${NS}.table.colPrice`),
      width: 110,
      align: 'right',
      render: (_, row) => (
        <div>
          <div className="sp-order-price">{formatAud(row.price)}</div>
          <div className="sp-order-muted sp-order-small">
            {row.unit ? t(`${NS}.table.priceUnit`, { unit: row.unit }) : t(`${NS}.table.priceUnitDefault`)}
          </div>
        </div>
      ),
    },
    {
      key: ORDERING_COLUMN_KEYS.carton,
      title: t(`${NS}.table.colCarton`),
      width: 110,
      render: (_, row) => (
        <div>
          <div className="sp-order-number" style={{ fontWeight: 500 }}>
            {t(`${NS}.table.cartonQty`, { count: row.cartonQty })}
          </div>
          <div className="sp-order-muted sp-order-small">{t(`${NS}.table.moq`)}</div>
        </div>
      ),
    },
    ...buildHistoryColumns(ctx),
  ]

  if (ctx.canViewOtherStores) {
    columns.push({
      key: ORDERING_COLUMN_KEYS.otherStores,
      title: (
        <span>
          {t(`${NS}.table.colOthers`)}
          <span className="sp-order-th-sub">{t(`${NS}.table.colOthersUnit`)}</span>
        </span>
      ),
      width: 210,
      render: (_, row) => {
        const others = (row.otherStores ?? []).filter((entry) => entry.cartons > 0)
        if (!others.length) return <span className="sp-order-muted sp-order-small">{t(`${NS}.table.othersNone`)}</span>
        return (
          <div className="sp-order-others">
            {others.map((entry) => (
              <span key={entry.storeName} className="sp-order-chip">
                {entry.storeName}
                <b>{formatInteger(entry.cartons)}</b>
              </span>
            ))}
          </div>
        )
      },
    })
  }

  columns.push({
    key: ORDERING_COLUMN_KEYS.mine,
    title: (
      <span>
        {t(`${NS}.table.colMine`)}
        <span className="sp-order-th-sub">{t(`${NS}.table.colMineUnit`)}</span>
      </span>
    ),
    width: 170,
    align: 'right',
    // 固定在右侧：加了去年四列后表格很宽，横向滚动时仍能直接下单；有固定列时展开行也会按可视宽度排版
    fixed: 'right',
    render: (_, row) => (
      <CartonStepper
        t={t}
        row={row}
        value={ctx.getDisplayCartons(row)}
        disabled={!ctx.canEdit}
        saving={ctx.isSaving(row.itemGuid)}
        onCommit={ctx.onCommit}
        onInvalidInput={ctx.onInvalidInput}
      />
    ),
  })

  return columns
}

/** 去年本店四列共用的格子属性：没有数据时第一格横跨四格、其余隐藏；参考商品整格紫色底 */
const HISTORY_SPAN = 4

function buildHistoryColumns(ctx: OrderingColumnContext): ColumnsType<SupplierPreorderOrderingItem> {
  const { t } = ctx
  const stateOf = (row: SupplierPreorderOrderingItem) => historyCellState(row.history, ctx.historyStatus)
  const cellProps = (row: SupplierPreorderOrderingItem, isFirst: boolean) => {
    if (stateOf(row) !== 'data') return isFirst ? { colSpan: HISTORY_SPAN } : { colSpan: 0 }
    return row.history?.source === 'Ref' ? { className: 'sp-hist-cell-ref' } : {}
  }
  const header = (title: string, sub: string) => (
    <span>
      {title}
      <span className="sp-order-th-sub">{sub}</span>
    </span>
  )

  return [
    {
      key: ORDERING_COLUMN_KEYS.lastYearPurchase,
      title: header(t(`${NS}.table.colLastYearPurchase`), t(`${NS}.table.colLastYearPurchaseSub`)),
      width: 124,
      onCell: (row) => cellProps(row, true),
      render: (_, row) => {
        const state = stateOf(row)
        if (state !== 'data' || !row.history) return <HistoryPlaceholder t={t} state={state} canViewOtherStores={ctx.canViewOtherStores} />
        return <PurchaseCell t={t} history={row.history} />
      },
    },
    {
      key: ORDERING_COLUMN_KEYS.lastYearSales,
      title: header(t(`${NS}.table.colLastYearSales`), t(`${NS}.table.colLastYearSalesSub`)),
      width: 136,
      onCell: (row) => cellProps(row, false),
      render: (_, row) => (row.history && stateOf(row) === 'data' ? <SalesCell t={t} history={row.history} /> : null),
    },
    {
      key: ORDERING_COLUMN_KEYS.lastYearCurve,
      title: header(t(`${NS}.table.colLastYearCurve`), t(`${NS}.table.colLastYearCurveSub`)),
      width: 144,
      onCell: (row) => cellProps(row, false),
      render: (_, row) => {
        if (!row.history || stateOf(row) !== 'data') return null
        return (
          <div className="sp-hist-curve">
            <HistorySparkline
              t={t}
              history={row.history}
              windowFrom={ctx.historyWindowFrom}
              windowDays={ctx.historyWindowDays}
              keyDates={ctx.historyKeyDates}
            />
            {row.history.source === 'Ref' ? <RefBadge t={t} history={row.history} ownPrice={row.price} /> : null}
          </div>
        )
      },
    },
    {
      key: ORDERING_COLUMN_KEYS.discount,
      title: t(`${NS}.table.colDiscount`),
      width: 108,
      onCell: (row) => cellProps(row, false),
      render: (_, row) => (row.history && stateOf(row) === 'data' ? <DiscountCell t={t} history={row.history} /> : null),
    },
  ]
}

function ProductThumb({ row }: { row: SupplierPreorderOrderingItem }) {
  const thumb = toProductThumbnailUrl(row.imageUrl, THUMB_SIZE)
  if (!thumb) return <span className="sp-order-thumb-empty">—</span>
  // 点击预览用原图：优先供应商原表图片地址，没有时用我方图片原图
  const previewSrc = row.imageSourceUrl?.trim() || row.imageUrl?.trim() || thumb
  return (
    <span className="sp-order-thumb">
      <Image src={thumb} width={52} height={52} loading="lazy" alt={row.name} preview={{ src: previewSrc }} />
    </span>
  )
}

interface CartonStepperProps {
  t: TFunction
  row: SupplierPreorderOrderingItem
  value: number
  disabled: boolean
  saving: boolean
  onCommit: (row: SupplierPreorderOrderingItem, cartons: number) => void
  onInvalidInput: () => void
}

/**
 * 本店下单步进器：− [箱数] +。
 * 加减按钮立即提交新值；输入框只在失焦或回车时提交。提交后的防抖合并、串行与回滚由页面的保存队列负责。
 */
const CartonStepper = memo(function CartonStepper({ t, row, value, disabled, saving, onCommit, onInvalidInput }: CartonStepperProps) {
  const [text, setText] = useState(String(value))
  const [editing, setEditing] = useState(false)

  // 不在编辑时跟随外部值（保存成功、回滚、切换分店）
  useEffect(() => {
    if (!editing) setText(String(value))
  }, [value, editing])

  const commitText = () => {
    setEditing(false)
    const parsed = parseCartonsInput(text)
    if (parsed === null) {
      onInvalidInput()
      setText(String(value))
      return
    }
    setText(String(parsed))
    if (parsed !== value) onCommit(row, parsed)
  }

  const step = (delta: number) => {
    const next = stepCartons(value, delta)
    if (next !== value) onCommit(row, next)
  }

  const { pieces, amount } = computeLineTotals(value, row.cartonQty, row.price)

  return (
    <div className={value > 0 ? 'sp-order-stepper has-value' : 'sp-order-stepper'}>
      <div className="sp-order-stepper-row">
        <span className="sp-order-stepper-spin" aria-hidden={!saving}>
          {saving ? <LoadingOutlined /> : null}
        </span>
        <Space.Compact size="small">
          <Button icon={<MinusOutlined />} disabled={disabled || value <= 0} onClick={() => step(-1)} aria-label={t(`${NS}.table.decrease`)} />
          <Input
            value={text}
            disabled={disabled}
            inputMode="numeric"
            aria-label={t(`${NS}.table.inputLabel`, { name: row.name })}
            onFocus={(event) => {
              setEditing(true)
              event.target.select()
            }}
            // 只保留数字，避免输入小数点、负号
            onChange={(event) => setText(event.target.value.replace(/[^\d]/g, ''))}
            onBlur={commitText}
            // 回车只触发失焦，由 onBlur 统一提交，避免同一次输入提交两遍
            onPressEnter={(event) => event.currentTarget.blur()}
          />
          <Button icon={<PlusOutlined />} disabled={disabled} onClick={() => step(1)} aria-label={t(`${NS}.table.increase`)} />
        </Space.Compact>
      </div>
      <span className="sp-order-stepper-figures">
        {value > 0 ? t(`${NS}.table.lineFigures`, { pieces: formatInteger(pieces), amount: formatAud(amount) }) : t(`${NS}.table.cartonsUnit`)}
      </span>
    </div>
  )
})
