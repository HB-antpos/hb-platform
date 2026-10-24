// 「选择参考商品」弹窗：已建订单表的商品页签与导入校对页共用，靠 loader 区分取候选的接口。
// 不带关键词时列系统推荐前 3；输入关键词按货号 / 条码 / 名称搜索（不限供应商）。
import { SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Input, Modal, Spin, Tag } from 'antd'
import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderItemRef, SupplierPreorderRefCandidate, SupplierPreorderRefCandidates } from '../../../types/supplierPreorder'

import { formatRefScore, getRefDisplayCode, getRefPickerActions, getRefPriceDiffRate } from './refPicker.logic'
import './registerSpAdminMessages'
import { ItemThumb } from './SpAdminParts'
import { formatInteger, formatMoney, formatPriceChangeRate, getErrorText, isAbortError, normalizeDateOnly } from './supplierPreorderAdmin.logic'

/** 关键词停手 350ms 后再搜 */
const SEARCH_DEBOUNCE_MS = 350

export type RefPickerResult = { mode: 'Manual'; candidate: SupplierPreorderRefCandidate } | { mode: 'Clear' } | { mode: 'Auto' }

export type RefCandidatesLoader = (keyword: string | undefined, signal: AbortSignal) => Promise<SupplierPreorderRefCandidates>

export interface RefPickerTarget {
  /** 商品的唯一键（订单表商品 itemGuid / 导入行号），换商品时重置搜索与选择 */
  key: string
  name: string
  supplierItemCode?: string | null
  price: number | null
  currentRef?: SupplierPreorderItemRef | null
}

interface RefPickerModalProps {
  target: RefPickerTarget | null
  loader: RefCandidatesLoader
  /** 抛错表示没保存成功：弹窗保持打开（错误提示由调用方给） */
  onSubmit: (result: RefPickerResult) => Promise<void> | void
  onClose: () => void
}

export default function RefPickerModal({ target, loader, onSubmit, onClose }: RefPickerModalProps) {
  const { t } = useTranslation()
  return (
    <Modal
      open={Boolean(target)}
      title={t('spAdmin.ref.pickerTitle')}
      width={760}
      footer={null}
      destroyOnHidden
      onCancel={onClose}
    >
      {target ? <RefPickerBody key={target.key} target={target} loader={loader} onSubmit={onSubmit} onClose={onClose} /> : null}
    </Modal>
  )
}

function RefPickerBody({ target, loader, onSubmit, onClose }: RefPickerModalProps & { target: RefPickerTarget }) {
  const { t } = useTranslation()
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [data, setData] = useState<SupplierPreorderRefCandidates | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [retry, setRetry] = useState(0)
  const [selected, setSelected] = useState<SupplierPreorderRefCandidate | null>(null)
  const [saving, setSaving] = useState<RefPickerResult['mode'] | null>(null)
  // loader 每次渲染可能是新函数：放进 ref，避免它变化触发重复请求
  const loaderRef = useRef(loader)
  loaderRef.current = loader

  const currentRef = target.currentRef ?? null
  const currentCode = currentRef && currentRef.source !== 'Cleared' ? currentRef.productCode ?? null : null
  const actions = getRefPickerActions(currentRef?.source ?? null)

  // 关键词防抖
  useEffect(() => {
    const timer = window.setTimeout(() => setKeyword(keywordInput.trim()), SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [keywordInput])

  // 关键逻辑：每次关键词变化取消上一次请求，旧响应不会覆盖新结果
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    loaderRef
      .current(keyword || undefined, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return
        setData(result)
      })
      .catch((reason) => {
        if (controller.signal.aborted || isAbortError(reason)) return
        setError(getErrorText(reason, t('spAdmin.ref.loadFailed')))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [keyword, retry, t])

  const list = (keyword ? data?.searchResults : data?.recommended) ?? []
  const windowFrom = normalizeDateOnly(data?.windowFrom)
  const windowTo = normalizeDateOnly(data?.windowTo)

  const submit = async (result: RefPickerResult) => {
    setSaving(result.mode)
    try {
      await onSubmit(result)
    } catch {
      // 调用方已提示错误，弹窗留着让用户重试
    } finally {
      setSaving(null)
    }
  }

  const renderCurrent = () => {
    if (!currentRef) return <span className="sp-admin-muted">{t('spAdmin.ref.currentNone')}</span>
    if (currentRef.source === 'Cleared') return <span className="sp-admin-muted">{t('spAdmin.ref.cleared')}</span>
    return (
      <span className="sp-admin-ref-current">
        <Tag color="purple" className="sp-admin-tag sp-admin-mono">
          {t('spAdmin.ref.tag', { code: getRefDisplayCode(currentRef) })}
        </Tag>
        <span className="sp-admin-ellipsis">{currentRef.name || '--'}</span>
        <span className="sp-admin-muted sp-admin-small">
          {currentRef.source === 'Auto'
            ? t('spAdmin.ref.sourceAuto', { score: formatRefScore(currentRef.score) })
            : t('spAdmin.ref.sourceManual', { by: currentRef.setBy || '--' })}
        </span>
      </span>
    )
  }

  const renderCandidate = (candidate: SupplierPreorderRefCandidate) => {
    const isSelected = selected?.productCode === candidate.productCode
    const diff = getRefPriceDiffRate(candidate.purchasePrice, target.price)
    const choose = () => setSelected(candidate)
    const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault()
        choose()
      }
    }
    return (
      <div
        key={candidate.productCode}
        role="radio"
        aria-checked={isSelected}
        tabIndex={0}
        className={`sp-admin-ref-cand${isSelected ? ' is-selected' : ''}${!candidate.eligible || !candidate.hasHistory ? ' is-weak' : ''}`}
        onClick={choose}
        onKeyDown={onKeyDown}
      >
        <ItemThumb url={candidate.imageUrl} size={44} />
        <div className="sp-admin-ref-cand-main">
          <div className="sp-admin-ref-cand-title">
            <span className="sp-admin-mono sp-admin-strong">{candidate.itemNumber || candidate.productCode}</span>
            <span className="sp-admin-ellipsis" title={candidate.name}>
              {candidate.name}
            </span>
          </div>
          <div className="sp-admin-muted sp-admin-small sp-admin-number">
            {t('spAdmin.ref.candPrice', { price: formatMoney(candidate.purchasePrice ?? null) })}
            {' · '}
            {t('spAdmin.ref.candSales', { qty: formatInteger(candidate.salesQty) })}
            {candidate.purchaseQty ? ` · ${t('spAdmin.ref.candPurchase', { qty: formatInteger(candidate.purchaseQty) })}` : ''}
            {candidate.barcode ? ` · ${candidate.barcode}` : ''}
          </div>
          <div className="sp-admin-ref-cand-tags sp-admin-small sp-admin-number">
            <span>
              {t('spAdmin.ref.candScore', {
                score: formatRefScore(candidate.score),
                name: formatRefScore(candidate.nameScore),
                price: formatRefScore(candidate.priceScore),
              })}
            </span>
            {candidate.productCode === currentCode ? (
              <Tag color="purple" className="sp-admin-tag">
                {t('spAdmin.ref.candCurrent')}
              </Tag>
            ) : null}
            {!candidate.eligible ? (
              <Tag color="orange" className="sp-admin-tag">
                {t('spAdmin.ref.candNotEligible')}
              </Tag>
            ) : null}
            {!candidate.hasHistory ? (
              <Tag color="red" className="sp-admin-tag">
                {t('spAdmin.ref.candNoHistory')}
              </Tag>
            ) : null}
          </div>
        </div>
        <div className="sp-admin-ref-cand-compare sp-admin-small sp-admin-number">
          <span>{t('spAdmin.ref.priceCompare', { ref: formatMoney(candidate.purchasePrice ?? null), self: formatMoney(target.price) })}</span>
          {diff !== null ? <span className="sp-admin-muted">{formatPriceChangeRate(diff)}</span> : null}
        </div>
      </div>
    )
  }

  const selectedIsCurrent = Boolean(selected && selected.productCode === currentCode && currentRef?.source === 'Manual')

  return (
    <div className="sp-admin-ref-picker">
      <div className="sp-admin-ref-self">
        <div className="sp-admin-ref-self-name">
          {target.supplierItemCode ? <span className="sp-admin-mono sp-admin-muted">{target.supplierItemCode}</span> : null}
          <span className="sp-admin-strong">{target.name}</span>
        </div>
        <div className="sp-admin-small sp-admin-number">
          <span className="sp-admin-muted">{t('spAdmin.ref.selfPrice')}</span> {formatMoney(target.price)}
          {windowFrom && windowTo ? (
            <span className="sp-admin-muted"> · {t('spAdmin.ref.window', { from: windowFrom, to: windowTo })}</span>
          ) : null}
        </div>
        <div className="sp-admin-ref-self-current sp-admin-small">
          <span className="sp-admin-muted">{t('spAdmin.ref.currentLabel')}</span>
          {renderCurrent()}
        </div>
      </div>

      <Input
        allowClear
        autoFocus
        prefix={<SearchOutlined />}
        value={keywordInput}
        placeholder={t('spAdmin.ref.searchPlaceholder')}
        onChange={(event) => setKeywordInput(event.target.value)}
      />

      <div className="sp-admin-ref-list-head sp-admin-small">
        <span className="sp-admin-strong">{keyword ? t('spAdmin.ref.searchResults') : t('spAdmin.ref.recommended')}</span>
        <span className="sp-admin-muted">{keyword ? t('spAdmin.ref.searchHint') : t('spAdmin.ref.recommendedHint')}</span>
      </div>

      {error ? (
        <Alert
          type="error"
          showIcon
          message={error}
          action={
            <Button size="small" onClick={() => setRetry((value) => value + 1)}>
              {t('spAdmin.common.retry')}
            </Button>
          }
        />
      ) : (
        <Spin spinning={loading}>
          <div className="sp-admin-ref-list" role="radiogroup" aria-label={t('spAdmin.ref.pickerTitle')}>
            {list.length ? (
              list.map(renderCandidate)
            ) : loading ? (
              <div className="sp-admin-ref-list-placeholder" />
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={keyword ? t('spAdmin.ref.searchEmpty') : t('spAdmin.ref.recommendedEmpty')}
              />
            )}
          </div>
        </Spin>
      )}

      <div className="sp-admin-ref-footer">
        <div className="sp-admin-actions">
          {actions.canClear ? (
            <Button danger type="text" loading={saving === 'Clear'} disabled={Boolean(saving)} onClick={() => void submit({ mode: 'Clear' })}>
              {t('spAdmin.ref.actionClear')}
            </Button>
          ) : null}
          {actions.canAuto ? (
            <Button type="text" loading={saving === 'Auto'} disabled={Boolean(saving)} onClick={() => void submit({ mode: 'Auto' })}>
              {t('spAdmin.ref.actionAuto')}
            </Button>
          ) : null}
        </div>
        <span className="sp-admin-toolbar-spacer" />
        <div className="sp-admin-actions">
          <Button onClick={onClose} disabled={Boolean(saving)}>
            {t('spAdmin.common.cancel')}
          </Button>
          <Button
            type="primary"
            loading={saving === 'Manual'}
            disabled={!selected || selectedIsCurrent || Boolean(saving)}
            onClick={() => selected && void submit({ mode: 'Manual', candidate: selected })}
          >
            {t('spAdmin.ref.actionManual')}
          </Button>
        </div>
      </div>
    </div>
  )
}
