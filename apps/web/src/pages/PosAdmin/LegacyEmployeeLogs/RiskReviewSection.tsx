import { AlertOutlined, CheckOutlined, FlagOutlined } from '@ant-design/icons'
import { Button, Input, Space, Tag, Typography, message, theme } from 'antd'
import dayjs from 'dayjs'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { LegacyEmployeeLogFlag, LegacyEmployeeLogReview } from '../../../types/legacyEmployeeLog'
import { RequestError } from '../../../utils/request'

import { describeFlagEvidence } from './legacyEmployeeLogsLogic'

const NOTE_MAX_LENGTH = 500

export type RiskReviewResult = 'normal' | 'followUp' | 'revoked'

interface RiskReviewSectionProps {
  /** 记录编号：换记录时清空未提交的备注，也用作输入框 id。 */
  recordKey: string
  flags: LegacyEmployeeLogFlag[]
  /** 当前结论；撤销后为 revoked（视同待核查，再次提交仍要带版本号）。 */
  review?: LegacyEmployeeLogReview | null
  canReview: boolean
  currentUserName: string
  /** 规则说明（老收银、新收银口径不同）。 */
  ruleDescription: (ruleCode: string) => string
  /** 调用各自的核查接口，返回最新结论。 */
  submit: (result: RiskReviewResult, note: string | undefined, expectedVersion: number | null) => Promise<LegacyEmployeeLogReview>
  /** 核查成功或撤销后回传最新结论；冲突时为 null，调用方应重新加载。 */
  onReviewChanged: (review: LegacyEmployeeLogReview | null) => void
}

/** 异常依据与核查：老收银与新收银详情共用，只对命中异常规则的记录显示。 */
export default function RiskReviewSection(props: RiskReviewSectionProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const [note, setNote] = useState('')
  const [submitting, setSubmitting] = useState<RiskReviewResult | null>(null)
  const review = props.review && props.review.result !== 'revoked' ? props.review : null

  useEffect(() => {
    setNote('')
  }, [props.recordKey])

  const submit = async (result: RiskReviewResult) => {
    setSubmitting(result)
    try {
      // 撤销后再核查也要带上次的版本号；从未核查过时不传。
      const saved = await props.submit(result, result === 'revoked' ? undefined : note.trim() || undefined, props.review?.version ?? null)
      message.success(t(result === 'revoked' ? 'legacyEmployeeLogs.review.revoked' : 'legacyEmployeeLogs.review.saved'))
      setNote('')
      props.onReviewChanged(saved)
    } catch (error) {
      if (error instanceof RequestError && error.status === 409) {
        message.warning(t('legacyEmployeeLogs.review.conflict'))
        props.onReviewChanged(null)
      } else {
        message.error(error instanceof Error && error.message ? error.message : t('legacyEmployeeLogs.review.failed'))
      }
    } finally {
      setSubmitting(null)
    }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {props.flags.map((flag) => (
        <div
          key={flag.ruleCode}
          style={{ border: `1px solid ${token.colorWarningBorder}`, background: token.colorWarningBg, borderRadius: token.borderRadiusLG, padding: '10px 14px' }}
        >
          <Typography.Text strong style={{ color: token.colorWarningText }}>
            <AlertOutlined style={{ marginInlineEnd: 6 }} />
            {t(`legacyEmployeeLogs.rules.${flag.ruleCode}.label`)}
          </Typography.Text>
          <div style={{ fontSize: 13, lineHeight: 1.6, marginTop: 4 }}>
            {describeFlagEvidence(flag).map((part) => t(`legacyEmployeeLogs.evidence.${part.key}`, part.params)).join('；')}
          </div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>{props.ruleDescription(flag.ruleCode)}</Typography.Text>
        </div>
      ))}

      <div style={{ border: `1px solid ${token.colorBorderSecondary}`, borderRadius: token.borderRadiusLG, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 10 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
          <Typography.Text strong>{t('legacyEmployeeLogs.review.title')}</Typography.Text>
          {review ? (
            <Space size={6}>
              <Tag color={review.result === 'followUp' ? 'purple' : 'default'} icon={review.result === 'followUp' ? <FlagOutlined /> : <CheckOutlined />} style={{ marginInlineEnd: 0 }}>
                {t(review.result === 'followUp' ? 'legacyEmployeeLogs.badges.followUp' : 'legacyEmployeeLogs.badges.reviewed')}
              </Tag>
              {props.canReview ? (
                <Button type="link" size="small" loading={submitting === 'revoked'} onClick={() => void submit('revoked')}>
                  {t('legacyEmployeeLogs.review.revoke')}
                </Button>
              ) : null}
            </Space>
          ) : (
            <Tag color="warning" style={{ marginInlineEnd: 0 }}>{t('legacyEmployeeLogs.review.pending')}</Tag>
          )}
        </div>
        {review ? (
          <>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {t('legacyEmployeeLogs.review.by', { name: review.reviewedByName, at: dayjs(review.reviewedAtUtc).format('YYYY-MM-DD HH:mm') })}
            </Typography.Text>
            {review.note ? <Typography.Text style={{ fontSize: 13 }}>{review.note}</Typography.Text> : null}
          </>
        ) : props.canReview ? (
          <>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t('legacyEmployeeLogs.review.reviewer', { name: props.currentUserName })}</Typography.Text>
            <label htmlFor={`risk-review-note-${props.recordKey}`} style={{ fontSize: 12, color: token.colorTextSecondary }}>
              {t('legacyEmployeeLogs.review.noteLabel')}
            </label>
            <Input.TextArea
              id={`risk-review-note-${props.recordKey}`}
              value={note}
              maxLength={NOTE_MAX_LENGTH}
              autoSize={{ minRows: 2, maxRows: 5 }}
              placeholder={t('legacyEmployeeLogs.review.notePlaceholder')}
              onChange={(event) => setNote(event.target.value)}
            />
            <div style={{ display: 'flex', gap: 8 }}>
              <Button block icon={<CheckOutlined />} loading={submitting === 'normal'} disabled={submitting !== null} onClick={() => void submit('normal')}>
                {t('legacyEmployeeLogs.review.normal')}
              </Button>
              <Button block danger icon={<FlagOutlined />} loading={submitting === 'followUp'} disabled={submitting !== null} onClick={() => void submit('followUp')}>
                {t('legacyEmployeeLogs.review.followUp')}
              </Button>
            </div>
          </>
        ) : (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t('legacyEmployeeLogs.review.readOnly')}</Typography.Text>
        )}
      </div>
    </div>
  )
}
