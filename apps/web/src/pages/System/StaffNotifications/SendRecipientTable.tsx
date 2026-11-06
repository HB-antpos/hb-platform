import { Tag, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { TFunction } from 'i18next'
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import type { StaffNotificationPreflightRecipient } from '../../../types/staffNotification'
import { describeLastLogin } from '../Users/usersPageLogic'

import {
  describeRecipientReason,
  getEmailDomain,
  getRecipientChannelPlan,
  getSmsSkipKey,
  isRecipientSelectable,
} from './staffNotificationLogic'

interface SendRecipientTableProps {
  /** 已按分段筛选过的收件人，顺序与预检一致。 */
  recipients: StaffNotificationPreflightRecipient[]
  selected: ReadonlySet<string>
  /** 勾选 / 取消勾选一批人（表头全选会一次传多个）。 */
  onToggle: (userGuids: string[], checked: boolean) => void
  /** 提供时，邮箱存疑 / 缺邮箱的人旁边出现「修改 / 补邮箱」入口（跳到用户编辑抽屉）。 */
  onEditEmail?: (userGuid: string) => void
  loading: boolean
}

/** 「3 天前」「昨天」这类相对登录时间，文案在本页消息文件里。 */
function formatLastLogin(t: TFunction, lastLoginAtUtc: string | null): string {
  const description = describeLastLogin(lastLoginAtUtc, new Date())
  switch (description.kind) {
    case 'justNow':
      return t('system.staffNotifications.send.lastLogin.justNow', '刚刚')
    case 'minutes':
      return t('system.staffNotifications.send.lastLogin.minutes', '{{count}} 分钟前', { count: description.count })
    case 'hours':
      return t('system.staffNotifications.send.lastLogin.hours', '{{count}} 小时前', { count: description.count })
    case 'yesterday':
      return t('system.staffNotifications.send.lastLogin.yesterday', '昨天')
    case 'days':
      return t('system.staffNotifications.send.lastLogin.days', '{{count}} 天前', { count: description.count })
    default:
      return ''
  }
}

const TAG_COLOR = { warning: 'warning', error: 'error', default: undefined } as const

/** 「发送判断」一列：状态标签 + 原因说明；原因后端以后可能新增，不认识的走通用文案。 */
function RecipientVerdict({
  recipient,
  onEditEmail,
}: {
  recipient: StaffNotificationPreflightRecipient
  onEditEmail?: (userGuid: string) => void
}) {
  const { t } = useTranslation()

  if (recipient.status === 'ready') {
    const plan = getRecipientChannelPlan(recipient)
    const skipKey = getSmsSkipKey(recipient.smsSkipReason)
    const tagLabel = plan === 'emailOnlySmsSkipped'
      ? t('system.staffNotifications.send.tag.emailOnly', '只发邮件')
      : t('system.staffNotifications.send.tag.ready', '可发送')
    const line = plan === 'both'
      ? t('system.staffNotifications.send.plan.both', '邮件 + 短信')
      : plan === 'smsOnly'
        ? t('system.staffNotifications.send.plan.smsOnly', '仅短信')
        : plan === 'emailOnlySmsSkipped' && skipKey
          ? t(`system.staffNotifications.send.smsSkip.${skipKey}`)
          : t('system.staffNotifications.send.plan.emailOnly', '仅邮件')
    return (
      <span className="staff-notify-send-verdict">
        <Tag color="success">{tagLabel}</Tag>
        <span className="staff-notify-send-hint">{line}</span>
      </span>
    )
  }

  const reasons = recipient.reasons.length ? recipient.reasons : ['']
  return (
    <span className="staff-notify-send-verdict">
      {reasons.map((reason) => {
        const described = describeRecipientReason(reason, recipient.status)
        const params = {
          when: formatLastLogin(t, recipient.lastLoginAtUtc),
          domain: getEmailDomain(recipient.email),
          reason: described.raw ?? reason,
        }
        const isAddEmail = described.key === 'noEmail'
        const isEditEmail = described.key === 'suspiciousEmail'
        return (
          <span key={reason || 'unknown'} style={{ display: 'block' }}>
            <Tag color={TAG_COLOR[described.tone]}>
              {t(`system.staffNotifications.send.reason.${described.key}`, params)}
            </Tag>
            <span className="staff-notify-send-hint">
              {isAddEmail && onEditEmail ? (
                <a onClick={() => onEditEmail(recipient.userGuid)}>{t('system.staffNotifications.send.addEmail', '补邮箱后可发送')}</a>
              ) : (
                t(`system.staffNotifications.send.reason.${described.key}Hint`, params)
              )}
              {isEditEmail && onEditEmail ? (
                <>
                  {' '}
                  <a onClick={() => onEditEmail(recipient.userGuid)}>{t('system.staffNotifications.send.editEmail', '修改')}</a>
                </>
              ) : null}
            </span>
          </span>
        )
      })}
    </span>
  )
}

/** 收件人核对表：ready 默认勾选，confirm 可手动勾，blocked 禁用勾选并显示原因。 */
export default function SendRecipientTable({ recipients, selected, onToggle, onEditEmail, loading }: SendRecipientTableProps) {
  const { t } = useTranslation()

  const columns = useMemo<ColumnsType<StaffNotificationPreflightRecipient>>(() => [
    {
      title: t('system.staffNotifications.send.colStaff', '员工'),
      dataIndex: 'displayName',
      width: 128,
      render: (_value: string, record) => (
        <span>
          <span className="staff-notify-send-name" title={record.displayName}>{record.displayName}</span>
          <span className="staff-notify-send-hint">{record.storeName || t('common.emptyValue', '—')}</span>
        </span>
      ),
    },
    {
      title: t('system.staffNotifications.send.colContact', '邮箱 / 手机'),
      dataIndex: 'email',
      render: (_value: string | null, record) => (
        <span>
          <span className="staff-notify-send-contact" title={record.email ?? undefined}>
            {record.email || <Typography.Text type="danger">{t('system.staffNotifications.send.emailMissing', '未设置')}</Typography.Text>}
          </span>
          <span className="staff-notify-send-contact">
            {record.phone
              ? (record.smsSkipReason === 'not_mobile' ? <Typography.Text type="secondary">{record.phone}</Typography.Text> : record.phone)
              : <Typography.Text type="secondary">{t('system.staffNotifications.send.phoneMissing', '无手机号')}</Typography.Text>}
          </span>
        </span>
      ),
    },
    {
      title: t('system.staffNotifications.send.colVerdict', '发送判断'),
      key: 'verdict',
      width: 220,
      render: (_value: unknown, record) => <RecipientVerdict recipient={record} onEditEmail={onEditEmail} />,
    },
  ], [onEditEmail, t])

  return (
    <MeasuredTable<StaffNotificationPreflightRecipient>
      metricId="system.staff-notifications.send-recipients"
      rowKey="userGuid"
      size="small"
      loading={loading}
      pagination={false}
      dataSource={recipients}
      columns={columns}
      rowSelection={{
        columnWidth: 36,
        selectedRowKeys: recipients.filter((recipient) => selected.has(recipient.userGuid)).map((recipient) => recipient.userGuid),
        getCheckboxProps: (record) => ({ disabled: !isRecipientSelectable(record) }),
        // 用 onSelect / onSelectAll 而不是 onChange：筛选后表格只有一部分人，onChange 会丢掉被筛掉的人的勾选。
        onSelect: (record, checked) => onToggle([record.userGuid], checked),
        onSelectAll: (checked, _rows, changeRows) => onToggle(changeRows.map((row) => row.userGuid), checked),
      }}
    />
  )
}
