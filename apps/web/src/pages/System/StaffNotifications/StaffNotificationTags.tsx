import { CheckOutlined, CloseOutlined, LoadingOutlined } from '@ant-design/icons'
import { Tag, Typography } from 'antd'
import { useTranslation } from 'react-i18next'

import { registerPageMessages } from '../../../i18n/registerPageMessages'
import type { StaffNotificationBatchResult, StaffNotificationChannelStatus } from '../../../types/staffNotification'

import sharedMessagesEn from './staffNotificationSharedMessages.en.json'
import sharedMessagesZh from './staffNotificationSharedMessages.zh.json'
import { getBatchResultMeta, getChannelStatusDisplay } from './staffNotificationLogic'

// 发送结果弹窗（用户管理页）与发送记录页共用这两个标签，文案随它们一起懒注册。
registerPageMessages({ zh: sharedMessagesZh, en: sharedMessagesEn })

const CHANNEL_DEFAULT_LABELS = {
  sent: '已发出',
  failed: '失败',
  pending: '发送中',
  notSent: '未发送',
  unfinished: '未完成',
} as const

const BATCH_DEFAULT_LABELS = {
  running: '发送中',
  allSent: '全部发出',
  partialFailed: '部分失败',
  allFailed: '全部失败',
} as const

/**
 * 逐人的渠道状态。sent 写「已发出」而不是「已送达」：邮件服务器接收就算已发出，退信目前收不到回执。
 * 没发（none / skipped）与批次结束后仍未完成（被中断）用灰色文字，不占 Tag 的视觉权重。
 */
export function ChannelStatusTag({ status, batchFinished }: { status: StaffNotificationChannelStatus; batchFinished: boolean }) {
  const { t } = useTranslation()
  const display = getChannelStatusDisplay(status, batchFinished)
  const label = t(`system.staffNotifications.channelStatus.${display.key}`, CHANNEL_DEFAULT_LABELS[display.key])
  if (display.color === 'default') {
    return <Typography.Text type="secondary">{label}</Typography.Text>
  }
  const icon = display.key === 'sent' ? <CheckOutlined /> : display.key === 'failed' ? <CloseOutlined /> : <LoadingOutlined />
  return (
    <Tag color={display.color} icon={icon} style={{ marginInlineEnd: 0 }}>
      {label}
    </Tag>
  )
}

/** 批次结果：发送中 / 全部发出 / 部分失败 / 全部失败。 */
export function BatchResultTag({ result }: { result: StaffNotificationBatchResult }) {
  const { t } = useTranslation()
  const meta = getBatchResultMeta(result)
  return (
    <Tag color={meta.color} style={{ marginInlineEnd: 0 }}>
      {t(`system.staffNotifications.batchResult.${meta.key}`, BATCH_DEFAULT_LABELS[meta.key])}
    </Tag>
  )
}
