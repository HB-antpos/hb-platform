// 供应商节日预订单 · 下单列表页（/supplier-preorder-ordering）
// 列出当前用户在「下单人员授权」里能下单的订单表，以及各自分店的下单进度；点击进入下单页。
import { RightOutlined } from '@ant-design/icons'
import { Button, Empty, Result, Spin } from 'antd'
import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'

import PageContainer from '../../../components/PageContainer'
import { getSupplierPreorderOrderingForms } from '../../../services/supplierPreorderService'
import type { SupplierPreorderOrderingForm } from '../../../types/supplierPreorder'

import './orderingMessages'
import { formatAud, formatInteger, getDeadlineState, getErrorMessage, isAbortError } from './orderingPage.logic'
import { DeadlineText, FormStatusTag, StoreStatusTag, formatHolidayYear } from './orderingShared'
import './styles.css'

const NS = 'supplierPreorderOrdering'

export default function SupplierPreorderOrderingList() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [forms, setForms] = useState<SupplierPreorderOrderingForm[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    getSupplierPreorderOrderingForms(controller.signal)
      .then((result) => setForms(result ?? []))
      .catch((loadError: unknown) => {
        if (isAbortError(loadError)) return
        setError(getErrorMessage(loadError, t(`${NS}.list.loadFailed`)))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [reloadKey, t])

  const retry = useCallback(() => setReloadKey((value) => value + 1), [])
  // 列表页只做展示，用本机时间即可；下单页会用服务器时间校正
  const nowMs = Date.now()

  let content
  if (loading) {
    content = (
      <div className="sp-order-state" style={{ textAlign: 'center' }}>
        <Spin />
      </div>
    )
  } else if (error) {
    content = (
      <div className="sp-order-state">
        <Result status="warning" title={t(`${NS}.list.loadFailed`)} subTitle={error} extra={<Button onClick={retry}>{t(`${NS}.list.retry`)}</Button>} />
      </div>
    )
  } else if (!forms.length) {
    content = (
      <div className="sp-order-state">
        <Empty
          description={
            <>
              <div style={{ color: '#1f2430', fontWeight: 500 }}>{t(`${NS}.list.emptyTitle`)}</div>
              <div className="sp-order-muted">{t(`${NS}.list.emptyHint`)}</div>
            </>
          }
        />
      </div>
    )
  } else {
    content = (
      <div className="sp-order-list-grid">
        {forms.map((form) => {
          const deadlineState = getDeadlineState(form.deadlineAtUtc, nowMs, form.isLocked)
          return (
            <button key={form.formGuid} type="button" className="sp-order-list-card" onClick={() => navigate(`/supplier-preorder-ordering/${form.formGuid}`)}>
              <div className="sp-order-list-head">
                <div className="sp-order-list-title" title={form.title}>
                  {form.title}
                </div>
                <FormStatusTag t={t} status={form.status} isLocked={form.isLocked} />
              </div>
              <div className="sp-order-list-meta">
                <span>{form.supplierName}</span>
                <span>{formatHolidayYear(t, form.holidayType, form.seasonYear)}</span>
                <span className="sp-order-number">{t(`${NS}.list.itemCount`, { value: formatInteger(form.itemCount) })}</span>
              </div>
              <div className="sp-order-list-meta">
                {form.deadlineAtUtc || form.isLocked ? (
                  <span>
                    {form.deadlineAtUtc ? `${t(`${NS}.page.deadlineLabel`)} ` : null}
                    <DeadlineText t={t} deadlineAtUtc={form.deadlineAtUtc} state={deadlineState} />
                  </span>
                ) : (
                  <span className="sp-order-muted">{t(`${NS}.list.noDeadline`)}</span>
                )}
              </div>
              <div className="sp-order-list-stores">
                <span className="sp-order-list-stores-label">{t(`${NS}.list.myStores`)}</span>
                {form.myStores.length ? (
                  form.myStores.map((store) => (
                    <div key={store.storeGuid} className="sp-order-list-store">
                      <span className="sp-order-list-store-name">{store.storeName}</span>
                      <StoreStatusTag t={t} status={store.orderStatus} />
                      <span className="sp-order-list-store-figures">
                        {t(`${NS}.list.storeFigures`, { cartons: formatInteger(store.cartons), amount: formatAud(store.amount) })}
                      </span>
                    </div>
                  ))
                ) : (
                  <span className="sp-order-muted sp-order-small">{t(`${NS}.list.noStores`)}</span>
                )}
              </div>
              <div className="sp-order-list-foot">
                {t(`${NS}.list.enter`)}
                <RightOutlined />
              </div>
            </button>
          )
        })}
      </div>
    )
  }

  return (
    <PageContainer title={t(`${NS}.list.title`)} subtitle={!loading && !error && forms.length ? t(`${NS}.list.subtitle`, { count: forms.length }) : undefined}>
      {content}
    </PageContainer>
  )
}
