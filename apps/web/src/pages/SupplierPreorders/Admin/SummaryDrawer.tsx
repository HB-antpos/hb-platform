// 订货汇总右侧抽屉（约 700px，不遮挡左侧页签；窄屏改为全宽覆盖）：商品头部 + 上一款 / 下一款（J / K）+ 主体 + 保存状态底栏。
import { CheckOutlined, CloseOutlined, LeftOutlined, LoadingOutlined, RightOutlined } from '@ant-design/icons'
import { Button, Drawer } from 'antd'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrixRow, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'
import { toProductThumbnailUrl } from '../../../utils/productImageThumbnail'

import './registerSpAdminMessages'
import SummaryDrawerBody, { formatRowAmountVolume } from './SummaryDrawerBody'
import { formatMoney } from './supplierPreorderAdmin.logic'
import type { SummaryHistoryEntry } from './useSummaryEditing'

/** 窄屏（< 1100px）抽屉改为全宽覆盖 */
const NARROW_QUERY = '(max-width: 1099px)'

function useIsNarrow(): boolean {
  const [narrow, setNarrow] = useState(() => (typeof window !== 'undefined' && window.matchMedia ? window.matchMedia(NARROW_QUERY).matches : false))
  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return undefined
    const query = window.matchMedia(NARROW_QUERY)
    const onChange = () => setNarrow(query.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])
  return narrow
}

interface SummaryDrawerProps {
  /** null = 关闭 */
  row: SupplierPreorderMatrixRow | null
  stores: SupplierPreorderStoreRef[]
  /** 当前页内的位置（从 0 起）与当前页款数；总款数用于「第 N / M 款」 */
  index: number
  pageCount: number
  /** 当前页第一款在全部筛选结果里的序号（从 1 起） */
  pageStartNumber: number
  total: number
  entry?: SummaryHistoryEntry
  editable: boolean
  busyKeys: ReadonlySet<string>
  busyCount: number
  savedAtText: string | null
  onClose: () => void
  onStep: (delta: number) => void
  onLoadHistory: (itemGuid: string, force?: boolean) => void
  commit: (storeGuid: string, itemGuid: string, cartons: number) => void
}

export default function SummaryDrawer(props: SummaryDrawerProps) {
  const { row: currentRow, stores, index, pageCount, pageStartNumber, total, entry, editable, busyKeys, busyCount, savedAtText, onClose, onStep, onLoadHistory, commit } = props
  const { t } = useTranslation()
  const narrow = useIsNarrow()
  const itemGuid = currentRow?.itemGuid ?? null
  // 关闭动画期间继续显示最后一款，避免抽屉滑出时内容瞬间消失
  const lastRowRef = useRef<SupplierPreorderMatrixRow | null>(null)
  if (currentRow) lastRowRef.current = currentRow
  const row = currentRow ?? lastRowRef.current

  // 打开 / 切换商品时懒加载历史；缓存被清空（entry 为空）时自动重拉
  useEffect(() => {
    if (itemGuid && !entry) onLoadHistory(itemGuid)
  }, [entry, itemGuid, onLoadHistory])

  // J = 上一款，K = 下一款（设计稿约定）；在输入框里打字时不响应
  useEffect(() => {
    if (!itemGuid) return undefined
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.metaKey || event.ctrlKey || event.altKey || event.isComposing) return
      const target = event.target as HTMLElement | null
      if (target && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))) return
      if (event.key === 'j' || event.key === 'J') onStep(-1)
      else if (event.key === 'k' || event.key === 'K') onStep(1)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [itemGuid, onStep])

  const thumb = toProductThumbnailUrl(row?.imageUrl, 112)
  const priceChanged = !!row && row.prevPrice !== null && row.prevPrice !== undefined && row.prevPrice !== row.price
  const volume = row ? formatRowAmountVolume(row) : null

  return (
    <Drawer
      open={!!currentRow}
      placement="right"
      width={narrow ? '100%' : 700}
      mask={narrow}
      closable={false}
      destroyOnHidden
      onClose={onClose}
      rootClassName="sp-admin-sum-drawer"
      styles={{ header: { display: 'none' }, body: { padding: 0, display: 'flex', flexDirection: 'column', background: '#f8f9fb' }, footer: { padding: 0 } }}
      footer={
        row ? (
          <div className="sp-admin-sum-dfoot">
            <span className="sp-admin-sum-save-state" role="status" aria-live="polite">
              {busyCount > 0 ? (
                <>
                  <LoadingOutlined /> {t('spAdmin.summary.saving', { count: busyCount })}
                </>
              ) : (
                <>
                  <CheckOutlined className="sp-admin-sum-ok" /> {t('spAdmin.summary.drawerAutoSave')}
                </>
              )}
              {busyCount === 0 && savedAtText ? <span className="sp-admin-muted sp-admin-number"> · {savedAtText}</span> : null}
            </span>
            {volume ? (
              <span className="sp-admin-muted sp-admin-number"> · {t('spAdmin.summary.rowAmountVolume', { amount: volume.amount, cbm: volume.cbm })}</span>
            ) : null}
            <span className="sp-admin-toolbar-spacer" />
            <span className="sp-admin-sum-keys">
              <kbd>J</kbd> / <kbd>K</kbd> {t('spAdmin.summary.prevNext')}
            </span>
          </div>
        ) : null
      }
    >
      {row ? (
        <>
          <header className="sp-admin-sum-dhead">
            {thumb ? <img className="sp-admin-sum-dthumb" src={thumb} alt="" width={56} height={56} /> : <span className="sp-admin-sum-dthumb is-empty" aria-hidden="true" />}
            <div className="sp-admin-sum-dtitle-block">
              <h2 className="sp-admin-sum-dtitle" title={row.name}>
                {row.name}
              </h2>
              <div className="sp-admin-sum-dmeta sp-admin-number">
                <span className="sp-admin-mono">{row.supplierItemCode}</span>
                {row.barcode ? <span className="sp-admin-mono">{row.barcode}</span> : null}
                {row.isNew ? <span className="sp-admin-sum-tag is-new">{t('spAdmin.summary.tagNew')}</span> : null}
                {priceChanged ? <span className="sp-admin-sum-tag is-price">{t('spAdmin.summary.tagPriceChanged')}</span> : null}
              </div>
              <div className="sp-admin-sum-dmeta sp-admin-number">
                <span>
                  {t('spAdmin.summary.price')} <b className={priceChanged ? 'sp-admin-sum-price is-changed' : undefined}>{formatMoney(row.price)}</b>
                  {priceChanged ? <span className="sp-admin-faint sp-admin-sum-strike"> {formatMoney(row.prevPrice)}</span> : null}
                </span>
                <span>{t('spAdmin.summary.cartonQty', { count: row.cartonQty })}</span>
                {row.cartonCbm ? <span>{t('spAdmin.summary.cartonCbm', { cbm: row.cartonCbm })}</span> : null}
                {row.category ? <span>{row.category}</span> : null}
              </div>
            </div>
            <div className="sp-admin-sum-dnav sp-admin-number">
              <span>{t('spAdmin.summary.position', { index: pageStartNumber + index, total })}</span>
              <Button size="small" icon={<LeftOutlined />} disabled={index <= 0} aria-label={t('spAdmin.summary.prevItem')} onClick={() => onStep(-1)} />
              <Button size="small" icon={<RightOutlined />} disabled={index >= pageCount - 1} aria-label={t('spAdmin.summary.nextItem')} onClick={() => onStep(1)} />
              <Button size="small" icon={<CloseOutlined />} aria-label={t('spAdmin.summary.closeDrawer')} onClick={onClose} />
            </div>
          </header>
          <div className="sp-admin-sum-dscroll">
            <SummaryDrawerBody row={row} stores={stores} entry={entry} editable={editable} busyKeys={busyKeys} commit={commit} onRetry={() => onLoadHistory(row.itemGuid, true)} />
          </div>
        </>
      ) : null}
    </Drawer>
  )
}
