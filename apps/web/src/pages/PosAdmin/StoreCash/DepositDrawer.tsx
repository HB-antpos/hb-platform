import { ReloadOutlined, StopOutlined } from '@ant-design/icons'
import { Alert, Button, Descriptions, Drawer, Empty, Image, Skeleton, Typography, message } from 'antd'
import { useEffect, useState } from 'react'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { getCashDeposit, voidCashDeposit } from '../../../services/storeCashService'
import type { CashDepositDetail } from '../../../types/storeCash'
import { EMPTY_CELL, formatAud, formatStoreDateTime, isVoided } from './logic'
import { Amount, LoadErrorAlert, type Tr } from './parts'
import ReasonModal from './ReasonModal'
import { useCashRequest } from './useCashRequest'

interface DepositDrawerProps {
  /** 当前打开的存款；null 表示关闭。seq 每次打开递增，保证每次打开都重新取详情（图片地址几分钟就过期）。 */
  target: { depositGuid: string; seq: number } | null
  timeZone: string
  active: boolean
  tr: Tr
  errorText: (error: unknown) => string | null
  onClose: () => void
  /** 作废成功后通知列表刷新。 */
  onChanged: () => void
}

const THUMB_SIZE = 88

/**
 * 存款详情：每张存单的金额、存单号与照片（缩略图懒加载，点开可放大并左右翻页），差异原因、作废信息；
 * canVoid 为 true 时可作废（二次确认 + 原因）。
 */
export default function DepositDrawer({ target, timeZone, active, tr, errorText, onClose, onChanged }: DepositDrawerProps) {
  const isMobile = useIsMobile()
  const requestKey = target ? `${target.depositGuid}:${target.seq}` : null
  const detailRequest = useCashRequest<CashDepositDetail>(
    requestKey,
    (signal) => getCashDeposit(target?.depositGuid as string, signal),
    active && target !== null,
  )
  // 作废成功后直接用接口返回的新详情，不必再请求一次。
  const [updated, setUpdated] = useState<CashDepositDetail | null>(null)
  const [voidOpen, setVoidOpen] = useState(false)

  useEffect(() => {
    setUpdated(null)
    setVoidOpen(false)
  }, [requestKey])

  const detail = updated ?? detailRequest.data
  const loadError = detailRequest.error ? errorText(detailRequest.error) : null
  const voided = detail ? isVoided(detail) : false

  const submitVoid = async (reason: string) => {
    if (!detail) return
    try {
      const result = await voidCashDeposit(detail.depositGuid, reason)
      setUpdated(result)
      setVoidOpen(false)
      message.success(tr('void.success'))
      onChanged()
    } catch (error) {
      throw new Error(errorText(error) ?? tr('errors.unknown'))
    }
  }

  const slipImageCount = detail?.slips.reduce((sum, slip) => sum + slip.attachments.length, 0) ?? 0

  return (
    <Drawer
      open={target !== null}
      width={isMobile ? '100%' : 600}
      title={detail ? tr('deposits.drawerTitle', { date: detail.depositDate }) : tr('deposits.drawerTitleLoading')}
      destroyOnHidden
      onClose={onClose}
      extra={(
        <Button
          icon={<ReloadOutlined />}
          aria-label={tr('drawer.reload')}
          loading={detailRequest.loading}
          onClick={() => {
            setUpdated(null)
            detailRequest.reload()
          }}
        >
          {tr('drawer.reload')}
        </Button>
      )}
      footer={detail && detail.canVoid && !voided ? (
        <div className="store-cash-drawer-actions">
          <Button danger icon={<StopOutlined />} onClick={() => setVoidOpen(true)}>{tr('void.depositButton')}</Button>
        </div>
      ) : null}
    >
      {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={detailRequest.reload} /> : null}
      {!detail && detailRequest.loading ? <Skeleton active paragraph={{ rows: 8 }} /> : null}
      {detail ? (
        <>
          {voided ? (
            <Alert
              type="warning"
              showIcon
              message={tr('void.voidedTitle')}
              description={tr('void.voidedDetail', {
                reason: detail.voidReason || EMPTY_CELL,
                name: detail.voidedByName || EMPTY_CELL,
                time: formatStoreDateTime(detail.voidedAtUtc, timeZone),
              })}
            />
          ) : null}
          <Descriptions className="store-cash-drawer-section" size="small" column={1} bordered>
            <Descriptions.Item label={tr('deposits.fields.status')}>
              {voided
                ? <StatusPill tone="gray">{tr('status.Voided')}</StatusPill>
                : <StatusPill tone="green">{tr('status.Active')}</StatusPill>}
            </Descriptions.Item>
            <Descriptions.Item label={tr('deposits.fields.depositDate')}>{detail.depositDate}</Descriptions.Item>
            <Descriptions.Item label={tr('deposits.fields.covered')}>
              {detail.coveredFromDate && detail.coveredToDate
                ? tr('deposits.coveredRange', { from: detail.coveredFromDate, to: detail.coveredToDate })
                : EMPTY_CELL}
            </Descriptions.Item>
            <Descriptions.Item label={tr('deposits.fields.total')}><Amount value={detail.totalAmount} strong /></Descriptions.Item>
            <Descriptions.Item label={tr('deposits.fields.slips')}>
              {tr('deposits.slipSummary', { slips: detail.slips.length, images: slipImageCount })}
            </Descriptions.Item>
            <Descriptions.Item label={tr('deposits.fields.createdBy')}>
              {tr('drawer.byAt', { name: detail.createdByName || EMPTY_CELL, time: formatStoreDateTime(detail.createdAtUtc, timeZone) })}
            </Descriptions.Item>
            <Descriptions.Item label={tr('deposits.fields.note')}>{detail.note || EMPTY_CELL}</Descriptions.Item>
            {detail.overrideReason ? (
              <Descriptions.Item label={tr('deposits.fields.overrideReason')}>{detail.overrideReason}</Descriptions.Item>
            ) : null}
          </Descriptions>

          <section className="store-cash-drawer-section" aria-label={tr('deposits.slipsTitle')}>
            <h3 className="store-cash-drawer-section-title">{tr('deposits.slipsTitle')}</h3>
            {detail.slips.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('deposits.noSlips')} /> : null}
            {/* 一个 PreviewGroup 包住全部存单的照片：点开任意一张都能左右翻看整笔存款的所有照片。 */}
            <Image.PreviewGroup>
              {detail.slips.map((slip, index) => (
                <div key={slip.slipGuid} className="store-cash-slip">
                  <div className="store-cash-slip-head">
                    <Typography.Text strong>{tr('deposits.slipLabel', { index: index + 1 })}</Typography.Text>
                    <Amount value={slip.amount} strong />
                  </div>
                  <Typography.Paragraph type="secondary" className="store-cash-device-meta">
                    {slip.slipNo ? tr('deposits.slipNo', { slipNo: slip.slipNo }) : tr('deposits.slipNoMissing')}
                  </Typography.Paragraph>
                  <div className="store-cash-thumbs">
                    {[...slip.attachments].sort((left, right) => left.sortOrder - right.sortOrder).map((attachment, imageIndex) => (
                      <Image
                        key={attachment.attachmentGuid}
                        src={attachment.url}
                        width={THUMB_SIZE}
                        height={THUMB_SIZE}
                        loading="lazy"
                        decoding="async"
                        alt={tr('deposits.slipImageAlt', { index: index + 1, image: imageIndex + 1 })}
                      />
                    ))}
                    {slip.attachments.length === 0 ? <span className="store-cash-muted">{tr('drawer.noImages')}</span> : null}
                  </div>
                </div>
              ))}
            </Image.PreviewGroup>
            {slipImageCount > 0 ? <p className="store-cash-note">{tr('drawer.imageExpiry')}</p> : null}
          </section>
        </>
      ) : null}

      <ReasonModal
        open={voidOpen}
        title={tr('void.depositTitle')}
        description={detail ? tr('void.depositDescription', { date: detail.depositDate, amount: formatAud(detail.totalAmount) }) : ''}
        fieldLabel={tr('void.reasonLabel')}
        placeholder={tr('void.reasonPlaceholder')}
        okText={tr('void.confirm')}
        required
        danger
        tr={tr}
        onSubmit={submitVoid}
        onCancel={() => setVoidOpen(false)}
      />
    </Drawer>
  )
}
