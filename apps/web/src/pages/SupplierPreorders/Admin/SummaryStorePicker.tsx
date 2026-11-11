// 订货汇总「分店」下拉：显示 / 隐藏分店列、拖动调顺序、给已开启没显示的分店加列、开启未开启的分店。
// 三组：已显示（⋮⋮ + 勾选 + 今年箱数 + 状态）/ 已开启但没显示（＋ 加列）/ 未开启（候选，开启并下单）。
// 列的显示与顺序只影响本机；合计始终按全部开启分店计算。
import { SearchOutlined } from '@ant-design/icons'
import { Button, Checkbox, Input } from 'antd'
import { useEffect, useMemo, useRef, useState, type DragEvent } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderStoreCandidate, SupplierPreorderStoreOrderStatus, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { insertIndexAtRow, matchesStoreKeyword, previewDrop, type ResolvedColumns } from './summaryColumns.logic'
import { formatInteger } from './supplierPreorderAdmin.logic'
import type { ColumnDrag } from './useColumnDrag'
import type { StoreCandidatesState } from './useStoreCandidates'

interface SummaryStorePickerProps {
  resolved: ResolvedColumns
  storeStatuses: Record<string, SupplierPreorderStoreOrderStatus>
  storeCartons: Record<string, number>
  candidates: StoreCandidatesState
  drag: ColumnDrag
  onShow: (storeGuid: string) => void
  onHide: (storeGuid: string) => void
  onShowOnlyOrdered: () => void
  onReset: () => void
  /** 点「开启并下单」：由父组件关闭下拉并弹出确认框（不会直接开启） */
  onEnable: (candidate: SupplierPreorderStoreCandidate) => void
  onClose: () => void
}

export default function SummaryStorePicker(props: SummaryStorePickerProps) {
  const { t } = useTranslation()
  const { resolved, storeStatuses, storeCartons, candidates, drag } = props
  const [keyword, setKeyword] = useState('')
  const { load } = candidates

  // 打开下拉时才请求未开启分店（有缓存就不再请求）
  useEffect(() => {
    load()
  }, [load])

  // Esc 关闭（不论焦点在搜索框还是别处）；回调放 ref，避免每次渲染重新绑定监听
  const closeRef = useRef(props.onClose)
  closeRef.current = props.onClose
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') closeRef.current()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  const visibleGuids = useMemo(() => resolved.visible.map((store) => store.storeGuid), [resolved.visible])
  const shown = resolved.visible.filter((store) => matchesStoreKeyword(store, keyword))
  const hidden = resolved.hiddenStores.filter((store) => matchesStoreKeyword(store, keyword))
  const off = (candidates.candidates ?? []).filter((store) => matchesStoreKeyword(store, keyword))
  const nothingMatched = !!keyword.trim() && shown.length + hidden.length + off.length === 0 && !candidates.loading

  const preview = drag.guid !== null && drag.insertIndex !== null ? previewDrop(visibleGuids, drag.guid, drag.insertIndex) : null
  const dropClass = (guid: string): string => {
    if (!preview?.changed || drag.insertIndex === null) return ''
    const index = visibleGuids.indexOf(guid)
    if (drag.insertIndex === index) return ' is-drop-before'
    if (drag.insertIndex === visibleGuids.length && index === visibleGuids.length - 1) return ' is-drop-after'
    return ''
  }

  const handleGripDragStart = (event: DragEvent<HTMLElement>, store: SupplierPreorderStoreRef) => {
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/plain', store.storeName)
    const row = event.currentTarget.closest('.sp-admin-sum-pr')
    if (row) event.dataTransfer.setDragImage(row, 24, 14)
    drag.begin(store.storeGuid)
  }
  const handleRowDragOver = (event: DragEvent<HTMLElement>, storeGuid: string) => {
    if (drag.guid === null) return
    event.preventDefault()
    event.dataTransfer.dropEffect = 'move'
    drag.hover(insertIndexAtRow(visibleGuids.indexOf(storeGuid), event.clientY, event.currentTarget.getBoundingClientRect()))
  }
  const handleDrop = (event: DragEvent<HTMLElement>) => {
    if (drag.guid === null) return
    event.preventDefault()
    drag.drop()
  }

  const statusOf = (guid: string) => storeStatuses[guid] ?? 'None'

  return (
    <div className="sp-admin-sum-picker" role="dialog" aria-label={t('spAdmin.summary.pickerAria')}>
      <Input
        autoFocus
        allowClear
        className="sp-admin-sum-picker-search"
        prefix={<SearchOutlined />}
        placeholder={t('spAdmin.summary.pickerSearch')}
        value={keyword}
        onChange={(event) => setKeyword(event.target.value)}
      />

      <div className="sp-admin-sum-pg">
        <span>{t('spAdmin.summary.pickerShown')}</span>
        <span className="sp-admin-sum-pg-cnt">{t('spAdmin.summary.pickerShownCount', { count: resolved.visible.length })}</span>
        <span className="sp-admin-toolbar-spacer" />
        <Button type="link" size="small" onClick={props.onShowOnlyOrdered}>
          {t('spAdmin.summary.pickerOnlyOrdered')}
        </Button>
        <Button type="link" size="small" onClick={props.onReset}>
          {t('spAdmin.summary.pickerReset')}
        </Button>
      </div>
      <div className="sp-admin-sum-pl" onDragOver={(event) => drag.guid !== null && event.preventDefault()} onDrop={handleDrop}>
        {shown.map((store) => (
          <div key={store.storeGuid} className={`sp-admin-sum-pr is-shown${dropClass(store.storeGuid)}${drag.guid === store.storeGuid ? ' is-dragging' : ''}`} onDragOver={(event) => handleRowDragOver(event, store.storeGuid)}>
            <span
              className="sp-admin-sum-grip is-list"
              draggable
              role="button"
              tabIndex={-1}
              aria-label={t('spAdmin.summary.gripAria', { store: store.storeName })}
              title={t('spAdmin.summary.gripTitle')}
              onDragStart={(event) => handleGripDragStart(event, store)}
              onDragEnd={drag.end}
            >
              ⋮⋮
            </span>
            <Checkbox checked onChange={() => props.onHide(store.storeGuid)} className="sp-admin-sum-pr-name">
              <span title={store.storeName}>{store.storeName}</span>
            </Checkbox>
            <span className="sp-admin-sum-pr-right">
              <span className="sp-admin-number">{t('spAdmin.summary.cartonsValue', { count: formatInteger(storeCartons[store.storeGuid] ?? 0) })}</span>
              <i className={`sp-admin-sum-dot is-${statusOf(store.storeGuid)}`} aria-hidden="true" />
              {t(`spAdmin.common.orderStatus.${statusOf(store.storeGuid)}`)}
            </span>
          </div>
        ))}
      </div>

      {hidden.length > 0 ? (
        <>
          <div className="sp-admin-sum-pg is-sep">
            <span>{t('spAdmin.summary.pickerHidden')}</span>
            <span className="sp-admin-sum-pg-cnt">{t('spAdmin.summary.pickerHiddenCount', { count: resolved.hiddenStores.length })}</span>
          </div>
          {hidden.map((store) => (
            <div key={store.storeGuid} className="sp-admin-sum-pr">
              <Checkbox checked={false} onChange={() => props.onShow(store.storeGuid)} className="sp-admin-sum-pr-name">
                <span title={store.storeName}>{store.storeName}</span>
              </Checkbox>
              <span className="sp-admin-sum-pr-right">
                {statusOf(store.storeGuid) === 'None' ? (
                  <span className="sp-admin-sum-pill is-gray">{t('spAdmin.common.orderStatus.None')}</span>
                ) : (
                  <>
                    <span className="sp-admin-number">{t('spAdmin.summary.cartonsValue', { count: formatInteger(storeCartons[store.storeGuid] ?? 0) })}</span>
                    <i className={`sp-admin-sum-dot is-${statusOf(store.storeGuid)}`} aria-hidden="true" />
                    {t(`spAdmin.common.orderStatus.${statusOf(store.storeGuid)}`)}
                  </>
                )}
                <Button size="small" className="sp-admin-sum-pr-btn" onClick={() => props.onShow(store.storeGuid)}>
                  ＋ {t('spAdmin.summary.pickerAddColumn')}
                </Button>
              </span>
            </div>
          ))}
        </>
      ) : null}

      <div className="sp-admin-sum-pg is-sep">
        <span>{t('spAdmin.summary.pickerOff')}</span>
        {candidates.candidates ? <span className="sp-admin-sum-pg-cnt">{t('spAdmin.summary.pickerOffCount', { count: candidates.candidates.length })}</span> : null}
      </div>
      {candidates.loading && !candidates.candidates ? <div className="sp-admin-sum-picker-note">{t('spAdmin.summary.pickerLoading')}</div> : null}
      {candidates.failed ? (
        <div className="sp-admin-sum-picker-note is-error">
          {t('spAdmin.summary.pickerOffFailed')}
          <Button type="link" size="small" onClick={() => candidates.load(true)}>
            {t('spAdmin.common.retry')}
          </Button>
        </div>
      ) : null}
      {candidates.candidates && candidates.candidates.length === 0 ? <div className="sp-admin-sum-picker-note">{t('spAdmin.summary.pickerOffNone')}</div> : null}
      {off.map((store) => (
        <div key={store.storeGuid} className="sp-admin-sum-pr is-off">
          <Checkbox checked={false} onChange={() => props.onEnable(store)} className="sp-admin-sum-pr-name" aria-label={t('spAdmin.summary.pickerEnableAria', { store: store.storeName })}>
            <span className="sp-admin-sum-pr-text">
              <span title={store.storeName}>{store.storeName}</span>
              {store.orderers.length > 0 ? (
                <small>{t('spAdmin.summary.pickerOrderers', { names: store.orderers.join(t('spAdmin.summary.listSeparator')) })}</small>
              ) : (
                <small className="is-warn">{t('spAdmin.summary.pickerNoOrderer')}</small>
              )}
            </span>
          </Checkbox>
          <span className="sp-admin-sum-pr-right">
            {store.lastYearSalesQty > 0 ? (
              <span className="sp-admin-number">{t('spAdmin.summary.pickerLastYearQty', { count: formatInteger(store.lastYearSalesQty) })}</span>
            ) : (
              <span className="sp-admin-muted">{t('spAdmin.summary.pickerLastYearNone')}</span>
            )}
            <Button size="small" className="sp-admin-sum-pr-btn" onClick={() => props.onEnable(store)}>
              {t('spAdmin.summary.pickerEnable')}
            </Button>
          </span>
        </div>
      ))}
      {nothingMatched ? <div className="sp-admin-sum-picker-note">{t('spAdmin.summary.pickerNoMatch')}</div> : null}

      <div className="sp-admin-sum-picker-foot">{t('spAdmin.summary.pickerFoot')}</div>
    </div>
  )
}
