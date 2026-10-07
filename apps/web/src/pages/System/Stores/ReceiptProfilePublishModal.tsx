import { Alert, Button, Collapse, Modal, Spin, Tag, Typography } from 'antd'
import type { TFunction } from 'i18next'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getStoreReceiptProfileStatuses, publishStoreReceiptProfiles } from '../../../services/storeReceiptProfileService'
import type { StoreReceiptProfilePublishResult, StoreReceiptProfileStatusItem } from '../../../types/storeReceiptProfile'
import { createLatestRequestGuard } from '../listPagination'
import {
  ReceiptProfilePublishRequestError,
  buildPublishPreview,
  buildPublishRequest,
  isReceiptProfileEndpointMissing,
  parsePublishFailure,
  type PublishFailure,
  type PublishPreviewRow,
  type ReceiptFieldDiff,
  type ReceiptProfileFieldKey,
} from './receiptProfileLogic'

/**
 * 6 个字段的标签：字面量映射（页面消息契约测试靠字面量扫键）。
 * 品牌/店名/地址/电话/ABN/退货政策与分店表单共用既有文案，不重复定义。
 */
function fieldLabel(key: ReceiptProfileFieldKey, t: TFunction) {
  switch (key) {
    case 'brandName':
      return t('system.stores.brandName')
    case 'storeName':
      return t('system.stores.storeName')
    case 'address':
      return t('system.stores.address')
    case 'phone':
      return t('system.stores.contactPhone')
    case 'abn':
      return t('system.stores.abn')
    default:
      return t('system.stores.returnPolicy')
  }
}

interface ReceiptProfilePublishModalProps {
  /** 要下发的分店 guid；为 null 时弹窗关闭。 */
  storeGuids: string[] | null
  onCancel: () => void
  /** 下发成功：result 为服务端结果，guids 为本次实际提交的分店。 */
  onPublished: (result: StoreReceiptProfilePublishResult, guids: string[]) => void
}

function ValueText({ value }: { value: string | null | undefined }) {
  return value?.trim() ? <span className="sys-store-rp-value">{value}</span> : <span className="sys-store-faint">--</span>
}

/** 单店新旧对比：6 个字段逐行列出，只有「有差异」的行高亮；首次下发没有旧值，只列将下发的内容。 */
function DiffTable({ row }: { row: PublishPreviewRow }) {
  const { t } = useTranslation()
  const first = row.kind === 'first'
  const diffs: ReceiptFieldDiff[] = row.diffs
  return (
    <div className={first ? 'sys-store-rp-diff sys-store-rp-diff-first' : 'sys-store-rp-diff'} role="table">
      <div className="sys-store-rp-diff-head" role="row">
        <span role="columnheader">{t('system.stores.receiptProfile.diffField')}</span>
        {first ? null : (
          <span role="columnheader">{t('system.stores.receiptProfile.diffOld', { version: row.item.latestVersion })}</span>
        )}
        <span role="columnheader">
          {first ? t('system.stores.receiptProfile.diffFirst') : t('system.stores.receiptProfile.diffNew')}
        </span>
      </div>
      {diffs.map((diff) => (
        <div
          key={diff.key}
          role="row"
          className={!first && diff.changed ? 'sys-store-rp-diff-row sys-store-rp-diff-row-changed' : 'sys-store-rp-diff-row'}
        >
          <span role="rowheader" className="sys-store-rp-diff-label">{fieldLabel(diff.key, t)}</span>
          {first ? null : (
            <span role="cell" className="sys-store-rp-diff-old"><ValueText value={diff.oldValue} /></span>
          )}
          <span role="cell" className="sys-store-rp-diff-new"><ValueText value={diff.newValue} /></span>
        </div>
      ))}
    </div>
  )
}

function PanelHeader({ row }: { row: PublishPreviewRow }) {
  const { t } = useTranslation()
  const changedLabels = row.changedKeys.map((key) => fieldLabel(key, t)).join(t('system.stores.receiptProfile.listSeparator'))
  return (
    <span className="sys-store-rp-panel-head">
      <span className="sys-store-name">{row.item.storeName}</span>
      <span className="sys-store-mono sys-store-sub">{row.item.storeCode}</span>
      {row.kind === 'first' ? (
        <Tag bordered={false} color="blue" style={{ marginInlineEnd: 0 }}>{t('system.stores.receiptProfile.firstPublish')}</Tag>
      ) : (
        <>
          <Tag bordered={false} color="orange" style={{ marginInlineEnd: 0 }}>
            {t('system.stores.receiptProfile.changedCount', { count: row.changedKeys.length })}
          </Tag>
          {changedLabels ? <span className="sys-store-sub sys-store-rp-panel-fields">{changedLabels}</span> : null}
        </>
      )}
    </span>
  )
}

/**
 * 「下发小票资料」确认弹窗。
 * 打开时重新拉取所选分店的最新状态（不信任列表里可能已过期的快照），逐店展示新旧对比；
 * 确认后调用原子下发接口，整批失败时在弹窗内展示逐店原因并保留弹窗与勾选，便于修正后重试。
 */
export default function ReceiptProfilePublishModal({ storeGuids, onCancel, onPublished }: ReceiptProfilePublishModalProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<unknown>(null)
  const [items, setItems] = useState<StoreReceiptProfileStatusItem[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [failure, setFailure] = useState<PublishFailure | null>(null)
  const guardRef = useRef(createLatestRequestGuard())
  const submittingRef = useRef(false)
  const open = storeGuids !== null
  // 用 join 作为依赖键：父组件每次渲染传入的数组引用可能不同，但内容不变时不应重复拉取。
  const guidsKey = storeGuids?.join('|') ?? ''

  const load = useCallback(async (guids: string[]) => {
    const requestId = guardRef.current.begin()
    setLoading(true)
    setLoadError(null)
    try {
      const result = await getStoreReceiptProfileStatuses(guids)
      if (guardRef.current.isLatest(requestId)) {
        // 按请求顺序排列，保证对比列表与勾选顺序一致。
        const order = new Map(guids.map((guid, index) => [guid, index]))
        setItems([...result].sort((a, b) => (order.get(a.storeGuid) ?? 0) - (order.get(b.storeGuid) ?? 0)))
      }
    } catch (error) {
      if (guardRef.current.isLatest(requestId)) {
        console.error(error)
        setLoadError(error)
      }
    } finally {
      if (guardRef.current.isLatest(requestId)) {
        setLoading(false)
      }
    }
  }, [])

  useEffect(() => {
    const guard = guardRef.current
    if (!storeGuids) {
      return undefined
    }
    setItems([])
    setFailure(null)
    submittingRef.current = false
    setSubmitting(false)
    void load(storeGuids)
    return () => {
      guard.invalidate()
    }
    // storeGuids 的内容由 guidsKey 表示
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [guidsKey, load])

  const preview = useMemo(() => buildPublishPreview(items), [items])
  const requestedCount = storeGuids?.length ?? 0
  const missingCount = Math.max(0, requestedCount - items.length)
  const storeNameByGuid = useMemo(() => new Map(items.map((item) => [item.storeGuid, item.storeName])), [items])
  const ready = !loading && !loadError
  const canConfirm = ready && !submitting && preview.publishCount > 0

  const handleConfirm = async () => {
    // 用 ref 同步挡住连点：state 要等重渲染后按钮才会进入 loading，期间的第二次点击会重复提交并重复提示。
    if (!canConfirm || submittingRef.current) {
      return
    }
    // 只提交「仍存在」的分店：已被删除的分店状态接口不返回，带上它会让整批被后端拒绝。
    const guids = items.map((item) => item.storeGuid)
    submittingRef.current = true
    setSubmitting(true)
    setFailure(null)
    try {
      const result = await publishStoreReceiptProfiles(buildPublishRequest(guids).storeGuids)
      onPublished(result, guids)
    } catch (error) {
      if (error instanceof ReceiptProfilePublishRequestError) {
        setFailure({ kind: 'other', message: t('system.stores.batchSelectionLimit'), items: [] })
      } else {
        console.error(error)
        setFailure(parsePublishFailure(error))
      }
    } finally {
      submittingRef.current = false
      setSubmitting(false)
    }
  }

  // 展开策略：改动的分店不多时直接展开方便逐一核对；很多时折叠，靠标题里的「修改了哪些字段」概览。
  const defaultActiveKeys = preview.publishRows.length <= 3 ? preview.publishRows.map((row) => row.item.storeGuid) : []

  return (
    <Modal
      title={t('system.stores.receiptProfile.publishTitle')}
      open={open}
      onCancel={() => {
        if (!submitting) {
          onCancel()
        }
      }}
      width={760}
      destroyOnHidden
      maskClosable={!submitting}
      keyboard={!submitting}
      closable={!submitting}
      footer={(
        <>
          <Button disabled={submitting} onClick={onCancel}>{t('common.cancel')}</Button>
          <Button type="primary" loading={submitting} disabled={!canConfirm} onClick={() => void handleConfirm()}>
            {t('system.stores.receiptProfile.publishConfirm', { count: preview.publishCount })}
          </Button>
        </>
      )}
    >
      <div className="sys-store-rp-modal">
        <Typography.Paragraph type="secondary" style={{ marginBottom: 12 }}>
          {t('system.stores.receiptProfile.publishIntro')}
        </Typography.Paragraph>

        {loading ? (
          <div className="sys-store-rp-loading"><Spin /></div>
        ) : loadError ? (
          <Alert
            type="error"
            showIcon
            message={isReceiptProfileEndpointMissing(loadError)
              ? t('system.stores.receiptProfile.statusUnavailable')
              : t('system.stores.receiptProfile.publishLoadFailed')}
            action={<Button size="small" onClick={() => storeGuids && void load(storeGuids)}>{t('common.retry')}</Button>}
          />
        ) : (
          <>
            {failure ? (
              <Alert
                type={failure.kind === 'conflict' ? 'warning' : 'error'}
                showIcon
                style={{ marginBottom: 12 }}
                message={
                  failure.kind === 'conflict'
                    ? t('system.stores.receiptProfile.publishConflict')
                    : failure.kind === 'rejected'
                      ? t('system.stores.receiptProfile.publishRejected')
                      : failure.message || t('system.stores.receiptProfile.publishFailed')
                }
                description={failure.kind === 'rejected' && failure.items.length > 0 ? (
                  <ul className="sys-store-rp-errors">
                    {failure.items.map((item, index) => {
                      const name = (item.storeGuid && storeNameByGuid.get(item.storeGuid)) || item.storeCode || item.storeGuid
                      return (
                        <li key={`${item.storeGuid ?? item.storeCode ?? 'item'}-${index}`}>
                          {name ? <strong>{name}</strong> : null}
                          {name && item.message ? t('system.stores.receiptProfile.colon') : null}
                          {item.message}
                        </li>
                      )
                    })}
                  </ul>
                ) : failure.kind === 'rejected' && failure.message ? failure.message : undefined}
              />
            ) : null}

            {preview.publishCount === 0 ? (
              <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('system.stores.receiptProfile.publishNothing')} />
            ) : (
              <div className="sys-store-rp-summary-line">
                {preview.unchangedCount === 0
                  ? t('system.stores.receiptProfile.publishSummaryAll', { total: items.length })
                  : t('system.stores.receiptProfile.publishSummary', {
                    total: items.length,
                    publish: preview.publishCount,
                    unchanged: preview.unchangedCount,
                  })}
              </div>
            )}

            {missingCount > 0 ? (
              <Alert
                type="warning"
                showIcon
                style={{ marginBottom: 12 }}
                message={t('system.stores.receiptProfile.missingStores', { count: missingCount })}
              />
            ) : null}

            {preview.publishRows.length > 0 ? (
              <Collapse
                size="small"
                className="sys-store-rp-collapse"
                defaultActiveKey={defaultActiveKeys}
                items={preview.publishRows.map((row) => ({
                  key: row.item.storeGuid,
                  label: <PanelHeader row={row} />,
                  children: <DiffTable row={row} />,
                }))}
              />
            ) : null}

            {preview.unchangedRows.length > 0 && preview.publishCount > 0 ? (
              <div className="sys-store-rp-unchanged sys-store-sub">
                {t('system.stores.receiptProfile.unchangedList', {
                  names: preview.unchangedRows.map((row) => row.item.storeName).join(t('system.stores.receiptProfile.listSeparator')),
                })}
              </div>
            ) : null}
          </>
        )}
      </div>
    </Modal>
  )
}
