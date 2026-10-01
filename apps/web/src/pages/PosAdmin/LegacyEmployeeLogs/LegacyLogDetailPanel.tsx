import { AlertOutlined, CheckOutlined, FlagOutlined, WarningOutlined } from '@ant-design/icons'
import { Alert, Button, Descriptions, Input, Space, Spin, Tag, Timeline, Typography, message, theme } from 'antd'
import dayjs from 'dayjs'
import { useEffect, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { reviewLegacyEmployeeLog } from '../../../services/legacyEmployeeLogService'
import type {
  LegacyEmployeeLogContext,
  LegacyEmployeeLogItem,
  LegacyEmployeeLogReview,
} from '../../../types/legacyEmployeeLog'
import { RequestError } from '../../../utils/request'

import { describeFlagEvidence, isHighRiskOperation, parseLegacyDetail, type ParsedLegacyDetail } from './legacyEmployeeLogsLogic'

const NOTE_MAX_LENGTH = 500

interface LegacyLogDetailPanelProps {
  record: LegacyEmployeeLogItem
  context: LegacyEmployeeLogContext | null
  contextLoading: boolean
  contextError: boolean
  storeLabel: string
  lag: { text: string; late: boolean }
  canReview: boolean
  currentUserName: string
  operationTag: (operation?: string | null) => ReactNode
  renderParsedDetail: (parsed: ParsedLegacyDetail, raw?: string | null) => ReactNode
  onShowEmployeeDay: (record: LegacyEmployeeLogItem) => void
  /** 核查成功或撤销后回传最新结论（撤销时为 result=revoked）；冲突时为 null，调用方应重新加载。 */
  onReviewChanged: (record: LegacyEmployeeLogItem, review: LegacyEmployeeLogReview | null) => void
}

/** 一条记录的详情：异常依据、核查、基本信息、解析字段、原始详情与前后操作。宽屏常驻面板与窄屏抽屉共用。 */
export default function LegacyLogDetailPanel(props: LegacyLogDetailPanelProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const { record, context } = props
  const [note, setNote] = useState('')
  const [submitting, setSubmitting] = useState<'normal' | 'followUp' | 'revoked' | null>(null)
  const parsed = parseLegacyDetail(record.operationDetail, record.operation)
  const flags = record.flags ?? []
  const review = record.review && record.review.result !== 'revoked' ? record.review : null
  const danger = record.isDanger ?? isHighRiskOperation(record.operation)

  // 换一条记录时清空未提交的备注。
  useEffect(() => {
    setNote('')
  }, [record.id])

  const submit = async (result: 'normal' | 'followUp' | 'revoked') => {
    setSubmitting(result)
    try {
      const saved = await reviewLegacyEmployeeLog({
        logId: record.id,
        result,
        note: result === 'revoked' ? undefined : note.trim() || undefined,
        // 撤销后再核查也要带上次的版本号；从未核查过时不传。
        expectedVersion: record.review?.version ?? null,
      })
      message.success(t(result === 'revoked' ? 'legacyEmployeeLogs.review.revoked' : 'legacyEmployeeLogs.review.saved'))
      setNote('')
      props.onReviewChanged(record, saved)
    } catch (error) {
      if (error instanceof RequestError && error.status === 409) {
        message.warning(t('legacyEmployeeLogs.review.conflict'))
        props.onReviewChanged(record, null)
      } else {
        message.error(error instanceof Error && error.message ? error.message : t('legacyEmployeeLogs.review.failed'))
      }
    } finally {
      setSubmitting(null)
    }
  }

  return (
    <Space direction="vertical" size={18} style={{ width: '100%' }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <Space size={6} wrap>
          {props.operationTag(record.operation)}
          {danger ? <Tag color="error" icon={<WarningOutlined />}>{t('legacyEmployeeLogs.badges.danger')}</Tag> : null}
          {flags.map((flag) => (
            <Tag key={flag.ruleCode} color="warning" icon={<AlertOutlined />}>{t(`legacyEmployeeLogs.rules.${flag.ruleCode}.label`)}</Tag>
          ))}
        </Space>
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 12 }}>
          <Typography.Text strong style={{ fontSize: 17 }}>{parsed.productName || record.operation || '-'}</Typography.Text>
          {record.amountImpact ? (
            <span style={{ color: token.colorError, fontWeight: 600, fontSize: 17, fontVariantNumeric: 'tabular-nums' }}>−{record.amountImpact.toFixed(2)}</span>
          ) : null}
        </div>
        <Typography.Text type="secondary" style={{ fontSize: 13 }}>
          {[record.employeeName || '-', props.storeLabel, record.deviceCode || '-', dayjs(record.operationTime).format('HH:mm:ss')].join(' · ')}
        </Typography.Text>
      </div>

      {flags.length > 0 ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {flags.map((flag) => (
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
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t(`legacyEmployeeLogs.rules.${flag.ruleCode}.desc`)}</Typography.Text>
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
                <label htmlFor={`legacy-review-note-${record.id}`} style={{ fontSize: 12, color: token.colorTextSecondary }}>
                  {t('legacyEmployeeLogs.review.noteLabel')}
                </label>
                <Input.TextArea
                  id={`legacy-review-note-${record.id}`}
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
      ) : danger ? (
        <Alert type="error" showIcon={false} message={t('legacyEmployeeLogs.dangerOnly')} />
      ) : null}

      <Descriptions bordered size="small" column={1} labelStyle={{ width: 100 }}>
        <Descriptions.Item label={t('legacyEmployeeLogs.detail.operationTime')}>
          {dayjs(record.operationTime).format('YYYY-MM-DD HH:mm:ss')}
          <Typography.Text type="secondary">{`（${t('legacyEmployeeLogs.detail.localTime')}）`}</Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('legacyEmployeeLogs.detail.uploadTime')}>
          {dayjs(record.lastUploadTime).format('YYYY-MM-DD HH:mm:ss')}
          <Typography.Text type={props.lag.late ? 'warning' : 'secondary'}>
            {` · ${t('legacyEmployeeLogs.detail.lagSuffix', { lag: props.lag.text })}`}
          </Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('legacyEmployeeLogs.detail.storeDevice')}>
          {props.storeLabel} · <Typography.Text code>{record.deviceCode || '-'}</Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('legacyEmployeeLogs.detail.employee')}>
          {record.employeeName || '-'}
          {record.employeeId ? (
            <Typography.Text type="secondary" copyable={{ text: record.employeeId }} style={{ fontSize: 11, marginInlineStart: 6 }}>
              {record.employeeId}
            </Typography.Text>
          ) : null}
        </Descriptions.Item>
      </Descriptions>

      {parsed.productName || parsed.fields.length || parsed.orderGuid ? (
        <div>
          <SectionTitle title={t('legacyEmployeeLogs.detail.parsedTitle')} hint={t('legacyEmployeeLogs.detail.parsedHint')} />
          <Descriptions bordered size="small" column={1} labelStyle={{ width: 100 }}>
            {parsed.productName ? <Descriptions.Item label={t('legacyEmployeeLogs.detail.product')}>{parsed.productName}</Descriptions.Item> : null}
            {parsed.orderGuid ? (
              <Descriptions.Item label={t('legacyEmployeeLogs.detail.order')}>
                <Typography.Text code copyable style={{ fontSize: 12 }}>{parsed.orderGuid}</Typography.Text>
              </Descriptions.Item>
            ) : null}
            {parsed.fields.map((field) => (
              <Descriptions.Item key={`${field.key}-${field.value}`} label={field.key}>
                <span
                  style={{
                    fontVariantNumeric: 'tabular-nums',
                    fontWeight: field.tone ? 600 : undefined,
                    color: field.tone === 'danger' ? token.colorError : field.tone === 'money' ? token.colorSuccess : undefined,
                  }}
                >
                  {field.value}
                </span>
              </Descriptions.Item>
            ))}
          </Descriptions>
        </div>
      ) : null}

      <div>
        <SectionTitle title={t('legacyEmployeeLogs.detail.rawTitle')} />
        <Typography.Paragraph
          copyable={record.operationDetail ? { text: record.operationDetail } : false}
          style={{
            background: token.colorFillQuaternary,
            border: `1px solid ${token.colorSplit}`,
            borderRadius: token.borderRadius,
            padding: '10px 12px',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-all',
            marginBottom: 0,
          }}
        >
          {record.operationDetail || '-'}
        </Typography.Paragraph>
      </div>

      <div>
        <SectionTitle
          title={t('legacyEmployeeLogs.detail.contextTitle')}
          hint={`${record.deviceCode || '-'} · ${t('legacyEmployeeLogs.contextSameDevice', { minutes: context?.windowMinutes ?? 5 })}`}
        />
        {props.contextLoading ? (
          <Spin />
        ) : props.contextError ? (
          <Alert type="warning" showIcon message={t('legacyEmployeeLogs.detail.contextFailed')} />
        ) : context && context.neighbors.length > 1 ? (
          <>
            {context.truncated ? (
              <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('legacyEmployeeLogs.detail.contextTruncated', { count: context.neighbors.length })} />
            ) : null}
            <Timeline
              items={context.neighbors.map((item) => {
                const isCurrent = item.id === record.id
                return {
                  key: item.id,
                  color: isCurrent ? 'orange' : 'gray',
                  children: (
                    <div style={{ fontWeight: isCurrent ? 500 : undefined, fontSize: 13 }}>
                      <Typography.Text type="secondary" style={{ fontSize: 12, marginInlineEnd: 8, fontVariantNumeric: 'tabular-nums' }}>
                        {dayjs(item.operationTime).format('HH:mm:ss')}
                      </Typography.Text>
                      {props.operationTag(item.operation)}
                      {item.employeeName && item.employeeName !== record.employeeName ? (
                        <Typography.Text type="secondary" style={{ marginInlineStart: 6 }}>{item.employeeName}</Typography.Text>
                      ) : null}
                      <div style={{ marginTop: 2 }}>{props.renderParsedDetail(parseLegacyDetail(item.operationDetail, item.operation), item.operationDetail)}</div>
                    </div>
                  ),
                }
              })}
            />
          </>
        ) : (
          <Typography.Text type="secondary">{t('legacyEmployeeLogs.detail.contextEmpty')}</Typography.Text>
        )}
        {record.employeeId ? (
          <Button size="small" onClick={() => props.onShowEmployeeDay(record)}>
            {t('legacyEmployeeLogs.detail.onlyEmployeeDay')} →
          </Button>
        ) : null}
      </div>
    </Space>
  )
}

function SectionTitle({ title, hint }: { title: string; hint?: string }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 12, marginBottom: 10 }}>
      <Typography.Text strong>{title}</Typography.Text>
      {hint ? <Typography.Text type="secondary" style={{ fontSize: 12 }}>{hint}</Typography.Text> : null}
    </div>
  )
}
