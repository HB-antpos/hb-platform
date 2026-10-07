import { CheckCircleOutlined, ClearOutlined, FlagOutlined, ReloadOutlined, StopOutlined } from '@ant-design/icons'
import { Alert, Button, Descriptions, Drawer, Empty, Image, Skeleton, message } from 'antd'
import { useEffect, useState } from 'react'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { getCashExpense, reviewCashExpense, voidCashExpense } from '../../../services/storeCashService'
import type { CashExpenseDetail, CashReviewStatus } from '../../../types/storeCash'
import {
  categoryLabelKey,
  EMPTY_CELL,
  formatAud,
  formatStoreDateTime,
  isVoided,
  reviewNoteRequired,
  reviewTone,
} from './logic'
import { Amount, LoadErrorAlert, type Tr } from './parts'
import ReasonModal from './ReasonModal'
import { useCashRequest } from './useCashRequest'

interface ExpenseDrawerProps {
  target: { expenseGuid: string; seq: number } | null
  timeZone: string
  active: boolean
  tr: Tr
  tf: Tr
  errorText: (error: unknown) => string | null
  onClose: () => void
  onChanged: () => void
}

type PendingAction = { kind: 'void' } | { kind: 'review'; status: CashReviewStatus }

const THUMB_SIZE = 88

/**
 * 支出详情：收据照片、核对标记（已核 / 存疑 / 清除，canReview 才显示）与作废（canVoid 才显示）。
 * 核对标记是财务事后标记，不影响支出生效；存疑必须写说明。
 */
export default function ExpenseDrawer({ target, timeZone, active, tr, tf, errorText, onClose, onChanged }: ExpenseDrawerProps) {
  const isMobile = useIsMobile()
  const requestKey = target ? `${target.expenseGuid}:${target.seq}` : null
  const detailRequest = useCashRequest<CashExpenseDetail>(
    requestKey,
    (signal) => getCashExpense(target?.expenseGuid as string, signal),
    active && target !== null,
  )
  const [updated, setUpdated] = useState<CashExpenseDetail | null>(null)
  const [pending, setPending] = useState<PendingAction | null>(null)

  useEffect(() => {
    setUpdated(null)
    setPending(null)
  }, [requestKey])

  const detail = updated ?? detailRequest.data
  const loadError = detailRequest.error ? errorText(detailRequest.error) : null
  const voided = detail ? isVoided(detail) : false
  const canReview = Boolean(detail?.canReview) && !voided
  const canVoid = Boolean(detail?.canVoid) && !voided
  const categoryKey = detail ? categoryLabelKey(detail.category) : null

  const submit = async (text: string) => {
    if (!detail || !pending) return
    try {
      const result = pending.kind === 'void'
        ? await voidCashExpense(detail.expenseGuid, text)
        : await reviewCashExpense(detail.expenseGuid, { reviewStatus: pending.status, note: text || undefined })
      setUpdated(result)
      setPending(null)
      message.success(pending.kind === 'void' ? tr('void.success') : tr('reviewAction.success'))
      onChanged()
    } catch (error) {
      throw new Error(errorText(error) ?? tr('errors.unknown'))
    }
  }

  // 弹窗文案按动作取（键名写全，便于静态核对）。
  const modal = (() => {
    if (!pending || !detail) return null
    const params = { date: detail.expenseDate, amount: formatAud(detail.amount) }
    if (pending.kind === 'void') {
      return {
        title: tr('void.expenseTitle'),
        description: tr('void.expenseDescription', params),
        fieldLabel: tr('void.reasonLabel'),
        placeholder: tr('void.reasonPlaceholder'),
        okText: tr('void.confirm'),
        required: true,
        danger: true,
      }
    }
    if (pending.status === 'Flagged') {
      return {
        title: tr('reviewAction.flaggedTitle'),
        description: tr('reviewAction.flaggedDescription', params),
        fieldLabel: tr('reviewAction.noteLabel'),
        placeholder: tr('reviewAction.notePlaceholderRequired'),
        okText: tr('reviewAction.flaggedConfirm'),
        required: reviewNoteRequired('Flagged'),
        danger: true,
      }
    }
    if (pending.status === 'Reviewed') {
      return {
        title: tr('reviewAction.reviewedTitle'),
        description: tr('reviewAction.reviewedDescription', params),
        fieldLabel: tr('reviewAction.noteLabel'),
        placeholder: tr('reviewAction.notePlaceholderOptional'),
        okText: tr('reviewAction.reviewedConfirm'),
        required: reviewNoteRequired('Reviewed'),
        danger: false,
      }
    }
    return {
      title: tr('reviewAction.clearTitle'),
      description: tr('reviewAction.clearDescription', params),
      fieldLabel: tr('reviewAction.noteLabel'),
      placeholder: tr('reviewAction.notePlaceholderOptional'),
      okText: tr('reviewAction.clearConfirm'),
      required: reviewNoteRequired('None'),
      danger: false,
    }
  })()

  const images = detail ? [...detail.attachments].sort((left, right) => left.sortOrder - right.sortOrder) : []
  const hasActions = canReview || canVoid

  return (
    <Drawer
      open={target !== null}
      width={isMobile ? '100%' : 560}
      title={detail ? tr('expenses.drawerTitle', { date: detail.expenseDate }) : tr('expenses.drawerTitleLoading')}
      destroyOnHidden
      onClose={onClose}
      extra={(
        <Button
          icon={<ReloadOutlined />}
          loading={detailRequest.loading}
          onClick={() => {
            setUpdated(null)
            detailRequest.reload()
          }}
        >
          {tr('drawer.reload')}
        </Button>
      )}
      footer={detail && hasActions ? (
        <div className="store-cash-drawer-actions">
          {canReview && detail.reviewStatus !== 'Reviewed' ? (
            <Button icon={<CheckCircleOutlined />} onClick={() => setPending({ kind: 'review', status: 'Reviewed' })}>
              {tr('reviewAction.markReviewed')}
            </Button>
          ) : null}
          {canReview && detail.reviewStatus !== 'Flagged' ? (
            <Button icon={<FlagOutlined />} onClick={() => setPending({ kind: 'review', status: 'Flagged' })}>
              {tr('reviewAction.markFlagged')}
            </Button>
          ) : null}
          {canReview && detail.reviewStatus !== 'None' ? (
            <Button icon={<ClearOutlined />} onClick={() => setPending({ kind: 'review', status: 'None' })}>
              {tr('reviewAction.clear')}
            </Button>
          ) : null}
          {canVoid ? (
            <Button danger icon={<StopOutlined />} onClick={() => setPending({ kind: 'void' })}>
              {tr('void.expenseButton')}
            </Button>
          ) : null}
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
          {!voided && detail.reviewStatus === 'Flagged' ? (
            <Alert
              type="error"
              showIcon
              icon={<FlagOutlined />}
              message={tr('expenses.flaggedBanner')}
              description={detail.reviewNote || undefined}
            />
          ) : null}
          <Descriptions className="store-cash-drawer-section" size="small" column={1} bordered>
            <Descriptions.Item label={tr('expenses.fields.status')}>
              {voided
                ? <StatusPill tone="gray">{tr('status.Voided')}</StatusPill>
                : <StatusPill tone="green">{tr('status.Active')}</StatusPill>}
            </Descriptions.Item>
            <Descriptions.Item label={tr('expenses.fields.expenseDate')}>{detail.expenseDate}</Descriptions.Item>
            <Descriptions.Item label={tr('expenses.fields.category')}>{categoryKey ? tf(categoryKey) : detail.category}</Descriptions.Item>
            <Descriptions.Item label={tr('expenses.fields.amount')}><Amount value={detail.amount} strong /></Descriptions.Item>
            <Descriptions.Item label={tr('expenses.fields.payee')}>{detail.payeeName || EMPTY_CELL}</Descriptions.Item>
            <Descriptions.Item label={tr('expenses.fields.note')}>{detail.note || EMPTY_CELL}</Descriptions.Item>
            <Descriptions.Item label={tr('expenses.fields.createdBy')}>
              {tr('drawer.byAt', { name: detail.createdByName || EMPTY_CELL, time: formatStoreDateTime(detail.createdAtUtc, timeZone) })}
            </Descriptions.Item>
            <Descriptions.Item label={tr('expenses.fields.review')}>
              <div className="store-cash-cell-stack">
                <StatusPill tone={reviewTone(detail.reviewStatus)}>
                  {detail.reviewStatus === 'Flagged'
                    ? tr('review.Flagged')
                    : detail.reviewStatus === 'Reviewed' ? tr('review.Reviewed') : tr('review.None')}
                </StatusPill>
                {detail.reviewNote ? <span>{detail.reviewNote}</span> : null}
                {detail.reviewedByName || detail.reviewedAtUtc ? (
                  <span className="store-cash-sub">
                    {tr('drawer.byAt', {
                      name: detail.reviewedByName || EMPTY_CELL,
                      time: formatStoreDateTime(detail.reviewedAtUtc, timeZone),
                    })}
                  </span>
                ) : null}
              </div>
            </Descriptions.Item>
          </Descriptions>

          <section className="store-cash-drawer-section" aria-label={tr('expenses.receiptsTitle', { count: images.length })}>
            <h3 className="store-cash-drawer-section-title">{tr('expenses.receiptsTitle', { count: images.length })}</h3>
            {images.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('drawer.noImages')} />
            ) : (
              <Image.PreviewGroup>
                <div className="store-cash-thumbs">
                  {images.map((attachment, index) => (
                    <Image
                      key={attachment.attachmentGuid}
                      src={attachment.url}
                      width={THUMB_SIZE}
                      height={THUMB_SIZE}
                      loading="lazy"
                      decoding="async"
                      alt={tr('expenses.receiptAlt', { index: index + 1 })}
                    />
                  ))}
                </div>
              </Image.PreviewGroup>
            )}
            {images.length > 0 ? <p className="store-cash-note">{tr('drawer.imageExpiry')}</p> : null}
          </section>
        </>
      ) : null}

      {modal ? (
        <ReasonModal
          open
          title={modal.title}
          description={modal.description}
          fieldLabel={modal.fieldLabel}
          placeholder={modal.placeholder}
          okText={modal.okText}
          required={modal.required}
          danger={modal.danger}
          tr={tr}
          onSubmit={submit}
          onCancel={() => setPending(null)}
        />
      ) : null}
    </Drawer>
  )
}
