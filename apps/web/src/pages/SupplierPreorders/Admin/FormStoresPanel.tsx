import { CopyOutlined } from '@ant-design/icons'
import { App, Button, Empty, Spin, Switch, Tag } from 'antd'
import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'

import {
  copySupplierPreorderFormStoresFromPrevious,
  getSupplierPreorderFormStores,
  updateSupplierPreorderFormStores,
} from '../../../services/supplierPreorderService'
import type { SupplierPreorderFormStoreStatus } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { OrderStatusTag } from './SpAdminParts'
import {
  buildEnabledStoreGuids,
  formatCbm,
  formatInteger,
  formatMoney,
  formatSydneyDateTime,
  getErrorText,
  isAbortError,
  storeHasOrderData,
} from './supplierPreorderAdmin.logic'

interface FormStoresPanelProps {
  formGuid: string
  /** 开关分店或复制后通知页面刷新页头统计与矩阵 */
  onChanged: () => void
}

/** 分店开启卡片网格：每家店一张，开关决定这张订单表有哪些分店参与。 */
export default function FormStoresPanel({ formGuid, onChanged }: FormStoresPanelProps) {
  const { t } = useTranslation()
  const { message, modal } = App.useApp()
  const [stores, setStores] = useState<SupplierPreorderFormStoreStatus[]>([])
  const [loading, setLoading] = useState(false)
  const [savingGuid, setSavingGuid] = useState<string | null>(null)
  const [copying, setCopying] = useState(false)

  const load = useCallback(
    (signal?: AbortSignal) => {
      setLoading(true)
      getSupplierPreorderFormStores(formGuid, signal)
        .then((result) => {
          if (!signal?.aborted) setStores(result)
        })
        .catch((error) => {
          if (signal?.aborted || isAbortError(error)) return
          message.error(getErrorText(error, t('spAdmin.common.loadFailed')))
        })
        .finally(() => {
          if (!signal?.aborted) setLoading(false)
        })
    },
    [formGuid, message, t],
  )

  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal)
    return () => controller.abort()
  }, [load])

  const applyToggle = async (store: SupplierPreorderFormStoreStatus, enabled: boolean) => {
    setSavingGuid(store.storeGuid)
    try {
      // 后端是整体覆盖开启列表；以返回结果为准刷新（关闭的店已下数量保留但不再计入）
      const next = await updateSupplierPreorderFormStores(formGuid, buildEnabledStoreGuids(stores, store.storeGuid, enabled))
      setStores(next)
      message.success(enabled ? t('spAdmin.stores.enabledMsg', { store: store.storeName }) : t('spAdmin.stores.disabledMsg', { store: store.storeName }))
      onChanged()
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.common.saveFailed')))
      load()
    } finally {
      setSavingGuid(null)
    }
  }

  const handleToggle = (store: SupplierPreorderFormStoreStatus, enabled: boolean) => {
    if (!enabled && storeHasOrderData(store)) {
      modal.confirm({
        title: t('spAdmin.stores.disableConfirmTitle', { store: store.storeName }),
        content: t('spAdmin.stores.disableConfirmContent', { items: store.itemCount, cartons: store.cartons }),
        okText: t('spAdmin.stores.disableConfirmOk'),
        okButtonProps: { danger: true },
        cancelText: t('spAdmin.common.cancel'),
        onOk: () => applyToggle(store, false),
      })
      return
    }
    void applyToggle(store, enabled)
  }

  const handleCopy = () => {
    modal.confirm({
      title: t('spAdmin.stores.copyConfirmTitle'),
      content: t('spAdmin.stores.copyConfirmContent'),
      okText: t('spAdmin.stores.copy'),
      cancelText: t('spAdmin.common.cancel'),
      onOk: async () => {
        setCopying(true)
        try {
          const result = await copySupplierPreorderFormStoresFromPrevious(formGuid)
          if (!result.sourceTitle) {
            message.info(t('spAdmin.stores.copyNothing'))
            return
          }
          setStores(result.stores)
          message.success(t('spAdmin.stores.copied', { title: result.sourceTitle }))
          onChanged()
        } catch (error) {
          message.error(getErrorText(error, t('spAdmin.stores.copyFailed')))
        } finally {
          setCopying(false)
        }
      },
    })
  }

  const renderStoreCard = (store: SupplierPreorderFormStoreStatus) => {
    const lastAt = store.orderStatus === 'Submitted' ? store.submittedAtUtc ?? store.lastEditedAtUtc : store.lastEditedAtUtc
    const lastBy = store.orderStatus === 'Submitted' ? store.submittedBy ?? store.lastEditedBy : store.lastEditedBy
    const noOrderer = store.enabled && store.orderers.length === 0
    return (
      <div key={store.storeGuid} className={`sp-admin-store-card${store.enabled ? '' : ' is-disabled'}`}>
        <div className="sp-admin-store-head">
          <span className="sp-admin-store-name" title={store.storeName}>
            {store.storeName}
          </span>
          <Switch
            size="small"
            checked={store.enabled}
            loading={savingGuid === store.storeGuid}
            disabled={savingGuid !== null && savingGuid !== store.storeGuid}
            aria-label={t('spAdmin.stores.toggleAria', { store: store.storeName })}
            onChange={(checked) => handleToggle(store, checked)}
          />
        </div>
        <div>
          {store.enabled ? (
            <OrderStatusTag status={store.orderStatus} />
          ) : (
            <Tag bordered={false} className="sp-admin-tag">
              {t('spAdmin.stores.notEnabled')}
            </Tag>
          )}
        </div>
        <div className="sp-admin-muted sp-admin-small sp-admin-number">
          {lastAt ? `${formatSydneyDateTime(lastAt)}${lastBy ? ` · ${lastBy}` : ''}` : '—'}
        </div>
        <div className="sp-admin-small">
          {noOrderer ? (
            <span className="sp-admin-danger">
              {t('spAdmin.stores.orderersLabel')}
              {t('spAdmin.stores.noOrderer')} · <Link to="/supplier-preorders/orderers">{t('spAdmin.stores.goAuthorize')}</Link>
            </span>
          ) : (
            <span className={store.enabled ? 'sp-admin-muted' : 'sp-admin-faint'}>
              {t('spAdmin.stores.orderersLabel')}
              {store.orderers.length ? store.orderers.join(t('spAdmin.common.listSep')) : '—'}
            </span>
          )}
        </div>
        <div className="sp-admin-store-figures sp-admin-number">
          <span>{t('spAdmin.stores.itemsCartons', { items: formatInteger(store.itemCount), cartons: formatInteger(store.cartons) })}</span>
        </div>
        <div className="sp-admin-store-figures sp-admin-number">
          <span className="sp-admin-strong">{formatMoney(store.amount)}</span>
          <span className="sp-admin-muted">{formatCbm(store.cbm)} m³</span>
        </div>
      </div>
    )
  }

  return (
    <div className="sp-admin-card sp-admin-card-pad">
      <div className="sp-admin-section-head">
        <h3 className="sp-admin-section-title">{t('spAdmin.stores.title')}</h3>
        <span className="sp-admin-muted sp-admin-small">
          {t('spAdmin.stores.hint')} <Link to="/supplier-preorders/orderers">{t('spAdmin.stores.hintLink')}</Link>
        </span>
      </div>
      <Spin spinning={loading && stores.length === 0}>
        {stores.length ? (
          <div className="sp-admin-store-grid">{stores.map(renderStoreCard)}</div>
        ) : loading ? (
          <div className="sp-admin-store-grid-placeholder" />
        ) : (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('spAdmin.stores.empty')} />
        )}
      </Spin>
      <div className="sp-admin-gap-top">
        <Button size="small" icon={<CopyOutlined />} loading={copying} onClick={handleCopy}>
          {t('spAdmin.stores.copy')}
        </Button>
      </div>
    </div>
  )
}
