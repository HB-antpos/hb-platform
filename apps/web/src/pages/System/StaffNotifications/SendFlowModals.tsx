import { ReloadOutlined, SendOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Descriptions, Modal, Progress, Space, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import type {
  StaffNotificationBatchDetail,
  StaffNotificationRecipientResult,
  StaffNotificationTemplate,
} from '../../../types/staffNotification'

import { ChannelStatusTag } from './StaffNotificationTags'
import {
  countRetryRecipients,
  formatAud,
  getBatchProgressPercent,
  getResultTitleCounts,
  isBatchFinished,
  isSmsQuotaExceeded,
  type BatchProgress,
  type SendSummary,
} from './staffNotificationLogic'

// ===== 第一步：点「发送」后的确认 =====

interface SendConfirmModalProps {
  open: boolean
  template: StaffNotificationTemplate
  summary: SendSummary
  emailEnabled: boolean
  smsEnabled: boolean
  /** 本次短信的发件名；只有用户真有得选时才传，确认前再核对一遍。 */
  smsSenderId?: string
  smsUsedLast24h: number
  smsDailyLimit: number
  smsUnitCostAud: number
  submitting: boolean
  onBack: () => void
  /** confirmReissue：勾选里有已登录过的人且用户已确认时为 true。 */
  onConfirm: (confirmReissue: boolean) => void
}

/** 发送前说清后果：人数、邮件封数、短信条数与费用；只有勾了已登录过的人才出现警告和必须勾选的确认框。 */
export function SendConfirmModal({
  open,
  template,
  summary,
  emailEnabled,
  smsEnabled,
  smsSenderId,
  smsUsedLast24h,
  smsDailyLimit,
  smsUnitCostAud,
  submitting,
  onBack,
  onConfirm,
}: SendConfirmModalProps) {
  const { t } = useTranslation()
  const [reissueConfirmed, setReissueConfirmed] = useState(false)
  const needsReissueConfirm = summary.reissueRecipients.length > 0
  const separator = t('system.staffNotifications.confirm.nameSeparator', '、')
  const quotaExceeded = smsEnabled && isSmsQuotaExceeded(smsUsedLast24h, smsDailyLimit, summary.smsSegments)

  const items = [
    {
      key: 'recipients',
      label: t('system.staffNotifications.confirm.recipients', '收件人'),
      children: summary.skippedCount > 0
        ? t('system.staffNotifications.confirm.recipientsValueSkipped', '{{count}} 人（跳过 {{skipped}} 人）', { count: summary.selectedCount, skipped: summary.skippedCount })
        : t('system.staffNotifications.confirm.recipientsValue', '{{count}} 人', { count: summary.selectedCount }),
    },
    ...(emailEnabled ? [{
      key: 'email',
      label: t('system.staffNotifications.confirm.email', '邮件'),
      children: template.issuesPassword
        ? t('system.staffNotifications.confirm.emailPassword', '{{count}} 封，每人一个新的随机密码', { count: summary.emailCount })
        : t('system.staffNotifications.confirm.emailPlain', '{{count}} 封', { count: summary.emailCount }),
    }] : []),
    {
      key: 'sms',
      label: t('system.staffNotifications.confirm.sms', '短信'),
      children: smsEnabled ? (
        <span>
          {t('system.staffNotifications.confirm.smsValue', '{{count}} 条，约 {{cost}}（近 24 小时已发 {{used}} / {{limit}}）', {
            count: summary.smsSegments,
            cost: formatAud(summary.smsSegments * smsUnitCostAud),
            used: smsUsedLast24h,
            limit: smsDailyLimit,
          })}
          {quotaExceeded ? (
            <Typography.Text type="danger" style={{ display: 'block', fontSize: 12 }}>
              {t('system.staffNotifications.confirm.smsQuotaExceeded', '加上这次会超过每日短信上限，超出的短信会发送失败。')}
            </Typography.Text>
          ) : null}
        </span>
      ) : t('system.staffNotifications.confirm.smsNone', '不发短信'),
    },
    ...(smsEnabled && smsSenderId ? [{
      key: 'smsSender',
      label: t('system.staffNotifications.confirm.smsSender', '短信发件名'),
      children: smsSenderId,
    }] : []),
  ]

  return (
    <Modal
      open={open}
      width={500}
      title={t('system.staffNotifications.confirm.title', '确认发送「{{name}}」', { name: template.name })}
      closable={false}
      maskClosable={false}
      keyboard={false}
      destroyOnHidden
      footer={(
        <Space>
          <Button onClick={onBack} disabled={submitting}>{t('system.staffNotifications.confirm.back', '返回修改')}</Button>
          <Button
            type="primary"
            icon={<SendOutlined />}
            loading={submitting}
            disabled={needsReissueConfirm && !reissueConfirmed}
            onClick={() => onConfirm(needsReissueConfirm && reissueConfirmed)}
          >
            {t('system.staffNotifications.confirm.confirm', '确认发送')}
          </Button>
        </Space>
      )}
    >
      <Descriptions bordered size="small" column={1} style={{ marginBottom: 12 }} items={items} />
      {needsReissueConfirm ? (
        <>
          <Alert
            type="warning"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('system.staffNotifications.confirm.reissueTitle', '{{count}} 人已登录过：{{names}}', {
              count: summary.reissueRecipients.length,
              names: summary.reissueRecipients.map((recipient) => recipient.displayName).join(separator),
            })}
            description={t('system.staffNotifications.confirm.reissueBody', '邮件发出后，这些员工的旧密码立即失效，手机和电脑上的登录全部退出，要用邮件里的新密码重新登录。')}
          />
          <Checkbox checked={reissueConfirmed} onChange={(event) => setReissueConfirmed(event.target.checked)}>
            {t('system.staffNotifications.confirm.reissueCheck', '我已确认要给已登录过的员工重新发放密码')}
          </Checkbox>
        </>
      ) : null}
      {template.issuesPassword ? (
        <Typography.Text type="secondary" style={{ display: 'block', marginTop: 10, fontSize: 12 }}>
          {t('system.staffNotifications.confirm.passwordHint', '随机密码 72 小时内有效，首次登录必须改密；过期后重新发送即可。')}
        </Typography.Text>
      ) : null}
    </Modal>
  )
}

// ===== 发送过程：确认之后、结果出来之前 =====

interface SendProgressModalProps {
  open: boolean
  templateName: string
  progress: BatchProgress
  /** 连续取不到进度时为 true：后台仍在发送，提示去发送记录看。 */
  pollFailed: boolean
  onClose: () => void
  onViewLogs: () => void
}

/** 后台逐人发送，100 人可能要几分钟；关掉这个窗口不影响后台发送。 */
export function SendProgressModal({ open, templateName, progress, pollFailed, onClose, onViewLogs }: SendProgressModalProps) {
  const { t } = useTranslation()
  return (
    <Modal
      open={open}
      width={460}
      title={t('system.staffNotifications.progress.title', '正在发送「{{name}}」', { name: templateName })}
      closable={false}
      maskClosable={false}
      keyboard={false}
      destroyOnHidden
      footer={(
        <Space>
          {pollFailed ? (
            <Button onClick={onViewLogs}>{t('system.staffNotifications.progress.viewLogs', '查看发送记录')}</Button>
          ) : null}
          <Button type="primary" onClick={onClose}>{t('system.staffNotifications.progress.close', '关闭')}</Button>
        </Space>
      )}
    >
      <div className="staff-notify-send-progress">
        <Progress
          percent={getBatchProgressPercent(progress)}
          status={pollFailed ? 'exception' : 'active'}
          format={() => `${progress.done} / ${progress.total}`}
        />
        <Typography.Text style={{ display: 'block', marginTop: 8 }}>
          {t('system.staffNotifications.progress.status', '已处理 {{done}} / {{total}} 人', { done: progress.done, total: progress.total })}
        </Typography.Text>
        {pollFailed ? (
          <Alert
            type="warning"
            showIcon
            style={{ marginTop: 12 }}
            message={t('system.staffNotifications.progress.pollFailed', '暂时无法获取发送进度，后台仍在继续发送，请稍后到「发送记录」查看。')}
          />
        ) : (
          <Typography.Text type="secondary" style={{ display: 'block', marginTop: 8, fontSize: 12 }}>
            {t('system.staffNotifications.progress.note', '关掉这个窗口不影响后台发送，结果可在「发送记录」里查看。')}
          </Typography.Text>
        )}
      </div>
    </Modal>
  )
}

// ===== 第二步：发送结果 =====

export interface RejectedLine {
  key: string
  name: string
  reason: string
}

interface SendResultModalProps {
  open: boolean
  /** 没有批次（全部被服务端拒绝）时为 null，只显示被拒的人。 */
  detail: StaffNotificationBatchDetail | null
  rejected: RejectedLine[]
  retrying: boolean
  onRetry: () => void
  onViewLogs: () => void
  onDone: () => void
}

/** 逐人给结果：「已发出」不写「已送达」；失败要说清原因；服务端拒绝的人单独列出。 */
export function SendResultModal({ open, detail, rejected, retrying, onRetry, onViewLogs, onDone }: SendResultModalProps) {
  const { t } = useTranslation()
  const batchFinished = detail ? isBatchFinished(detail.status) : true

  const columns = useMemo<ColumnsType<StaffNotificationRecipientResult>>(() => [
    {
      title: t('system.staffNotifications.result.colStaff', '员工'),
      dataIndex: 'displayName',
      width: 130,
    },
    {
      title: t('system.staffNotifications.result.colEmail', '邮件'),
      dataIndex: 'emailStatus',
      width: 96,
      render: (_value, record) => <ChannelStatusTag status={record.emailStatus} batchFinished={batchFinished} />,
    },
    {
      title: t('system.staffNotifications.result.colSms', '短信'),
      dataIndex: 'smsStatus',
      width: 96,
      render: (_value, record) => <ChannelStatusTag status={record.smsStatus} batchFinished={batchFinished} />,
    },
    {
      title: t('system.staffNotifications.result.colNote', '说明'),
      dataIndex: 'note',
      render: (value: string) => (
        <span style={{ fontSize: 12.5, color: '#667085' }}>{value || t('system.staffNotifications.result.noNote', '—')}</span>
      ),
    },
  ], [batchFinished, t])

  const counts = detail ? getResultTitleCounts(detail) : { email: null, sms: null }
  const parts = [
    counts.email ? t('system.staffNotifications.result.partEmail', '邮件 {{ratio}}', { ratio: counts.email }) : null,
    counts.sms ? t('system.staffNotifications.result.partSms', '短信 {{ratio}}', { ratio: counts.sms }) : null,
  ].filter((part): part is string => part !== null)
  const title = parts.length
    ? t('system.staffNotifications.result.titleWithParts', '发送结果：{{parts}}', {
      parts: parts.join(t('system.staffNotifications.result.partJoiner', '，')),
    })
    : t('system.staffNotifications.result.title', '发送结果')

  const retryCount = detail ? countRetryRecipients(detail) : 0

  return (
    <Modal
      open={open}
      width={640}
      title={title}
      closable={false}
      maskClosable={false}
      keyboard={false}
      destroyOnHidden
      footer={(
        <Space>
          {detail ? <Button onClick={onViewLogs}>{t('system.staffNotifications.result.viewLogs', '查看发送记录')}</Button> : null}
          {detail?.canRetry ? (
            <Button icon={<ReloadOutlined />} loading={retrying} onClick={onRetry}>
              {t('system.staffNotifications.result.retry', '重试失败项（{{count}}）', { count: retryCount })}
            </Button>
          ) : null}
          <Button type="primary" onClick={onDone}>{t('system.staffNotifications.result.done', '完成')}</Button>
        </Space>
      )}
    >
      {detail?.status === 'interrupted' ? (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('system.staffNotifications.result.interrupted', '发送过程被中断，下表是已记录的结果；还没发出的可以用「重试失败项」补发。')} />
      ) : null}
      {detail ? (
        <MeasuredTable<StaffNotificationRecipientResult>
          metricId="system.staff-notifications.send-result"
          rowKey="id"
          size="small"
          pagination={false}
          dataSource={detail.recipients}
          columns={columns}
          // 人少时不出内部滚动条；人多（最多 100 个）时限高，表头固定。
          scroll={detail.recipients.length > 8 ? { y: 360 } : undefined}
        />
      ) : (
        <Typography.Text type="secondary">{t('system.staffNotifications.result.noBatch', '没有人被发送。')}</Typography.Text>
      )}
      {rejected.length ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginTop: 12 }}
          message={t('system.staffNotifications.result.rejectedTitle', '有 {{count}} 人没有发送', { count: rejected.length })}
          description={(
            <ul style={{ margin: 0, paddingLeft: 18 }}>
              {rejected.map((line) => (
                <li key={line.key}>{t('system.staffNotifications.result.rejectedLine', '{{name}}：{{reason}}', { name: line.name, reason: line.reason })}</li>
              ))}
            </ul>
          )}
        />
      ) : null}
    </Modal>
  )
}
