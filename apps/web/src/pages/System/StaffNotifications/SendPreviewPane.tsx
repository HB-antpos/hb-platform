import { LockOutlined } from '@ant-design/icons'
import { Alert, Card, Select, Spin, Typography } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { previewStaffNotification } from '../../../services/staffNotificationService'
import type {
  StaffNotificationPreflightRecipient,
  StaffNotificationPreview,
  StaffNotificationTemplate,
} from '../../../types/staffNotification'

import { describeSmsLength, getStaffNotificationErrorMessage, isRecipientSelectable } from './staffNotificationLogic'

interface SendPreviewPaneProps {
  template: StaffNotificationTemplate | undefined
  recipients: StaffNotificationPreflightRecipient[]
  emailEnabled: boolean
  smsEnabled: boolean
}

type PreviewState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'ready'; preview: StaffNotificationPreview }
  | { status: 'error'; message: string }

/**
 * 邮件预览：服务端渲染好的 HTML 只放进 sandbox iframe 的 srcDoc，不往页面 DOM 里直接注入 HTML。
 * sandbox 不给 allow-scripts，邮件里的脚本与事件处理器都不会执行，链接也点不开；
 * 只加 allow-same-origin 是为了父页面能读到内容高度、让预览撑开而不是出现内部滚动条。
 */
function EmailPreviewFrame({ html, title }: { html: string; title: string }) {
  const frameRef = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState(240)

  const measure = () => {
    try {
      const doc = frameRef.current?.contentDocument
      if (doc?.documentElement) setHeight(Math.max(160, Math.ceil(doc.documentElement.scrollHeight)))
    } catch {
      // 读不到高度时保持默认高度，iframe 自己滚动。
    }
  }

  return (
    <iframe
      ref={frameRef}
      className="staff-notify-send-preview-frame"
      title={title}
      sandbox="allow-same-origin"
      referrerPolicy="no-referrer"
      srcDoc={html}
      style={{ height }}
      onLoad={measure}
    />
  )
}

/** 抽屉右侧的预览：可切换员工，邮件密码位置只显示圆点，短信气泡显示字符与条数。 */
export default function SendPreviewPane({ template, recipients, emailEnabled, smsEnabled }: SendPreviewPaneProps) {
  const { t } = useTranslation()
  const [chosenGuid, setChosenGuid] = useState<string | undefined>(undefined)
  const [state, setState] = useState<PreviewState>({ status: 'idle' })

  // 只能预览没被拦掉的人：被拦的人服务端可能拒绝渲染。
  const previewable = useMemo(() => recipients.filter(isRecipientSelectable), [recipients])
  const activeGuid = previewable.some((recipient) => recipient.userGuid === chosenGuid)
    ? chosenGuid
    : previewable[0]?.userGuid
  const activeRecipient = previewable.find((recipient) => recipient.userGuid === activeGuid)
  const templateKey = template?.key

  useEffect(() => {
    if (!templateKey || !activeGuid) {
      setState({ status: 'idle' })
      return undefined
    }
    const controller = new AbortController()
    setState({ status: 'loading' })
    previewStaffNotification({ templateKey, userGuid: activeGuid }, controller.signal)
      .then((preview) => {
        if (!controller.signal.aborted) setState({ status: 'ready', preview })
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setState({
          status: 'error',
          message: getStaffNotificationErrorMessage(error, t('system.staffNotifications.send.preview.loadFailed', '加载预览失败')),
        })
      })
    return () => controller.abort()
    // t 在语言切换时才变化，预览内容与语言无关；只在模板或预览对象变化时重新拉取。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [templateKey, activeGuid])

  const preview = state.status === 'ready' ? state.preview : null
  const smsLength = preview ? describeSmsLength(preview.smsLength, preview.smsSegments) : null

  return (
    <Card
      size="small"
      className="staff-notify-send-preview"
      title={t('system.staffNotifications.send.preview.title', '预览')}
      extra={(
        <Select
          size="small"
          showSearch
          optionFilterProp="label"
          style={{ width: 150 }}
          value={activeGuid}
          onChange={setChosenGuid}
          disabled={!previewable.length}
          aria-label={t('system.staffNotifications.send.preview.title', '预览')}
          options={previewable.map((recipient) => ({ value: recipient.userGuid, label: recipient.displayName }))}
        />
      )}
    >
      {!previewable.length ? (
        <div className="staff-notify-send-preview-note">
          {t('system.staffNotifications.send.preview.noRecipient', '没有可预览的收件人')}
        </div>
      ) : state.status === 'error' ? (
        <div style={{ padding: 12 }}>
          <Alert type="error" showIcon message={state.message} />
        </div>
      ) : (
        <Spin spinning={state.status === 'loading'}>
          <div className="staff-notify-send-preview-body">
            {preview ? (
              <>
                {emailEnabled ? (
                  <>
                    <div className="staff-notify-send-preview-meta">
                      <div>
                        {t('system.staffNotifications.send.preview.subject', '主题')} <b>{preview.emailSubject}</b>
                      </div>
                      <div>
                        {t('system.staffNotifications.send.preview.to', '收件')} {activeRecipient?.email || t('common.emptyValue', '—')}
                      </div>
                    </div>
                    <EmailPreviewFrame html={preview.emailHtml} title={t('system.staffNotifications.send.preview.frameTitle', '邮件预览')} />
                  </>
                ) : (
                  <div className="staff-notify-send-preview-note">{t('system.staffNotifications.send.preview.emailOff', '这次不发邮件')}</div>
                )}
                <div className="staff-notify-send-preview-sms">
                  <div className="staff-notify-send-label staff-notify-send-label-row" style={{ marginBottom: 6 }}>
                    <span>{t('system.staffNotifications.send.preview.sms', '短信')}</span>
                    {smsEnabled && smsLength ? (
                      <Typography.Text type={preview.smsValid ? 'secondary' : 'danger'} style={{ fontSize: 12, fontWeight: 400 }}>
                        {t('system.staffNotifications.send.preview.smsLength', '{{length}} / {{limit}} 字符 · {{segments}} 条', smsLength)}
                      </Typography.Text>
                    ) : null}
                  </div>
                  {smsEnabled ? (
                    <>
                      <div className="staff-notify-send-sms-bubble">{preview.smsText}</div>
                      {!preview.smsValid && preview.smsError ? (
                        <Typography.Text type="danger" style={{ display: 'block', marginTop: 6, fontSize: 12 }}>{preview.smsError}</Typography.Text>
                      ) : null}
                    </>
                  ) : (
                    <Typography.Text type="secondary" style={{ fontSize: 12.5 }}>
                      {t('system.staffNotifications.send.preview.smsOff', '这次不发短信')}
                    </Typography.Text>
                  )}
                </div>
              </>
            ) : null}
          </div>
        </Spin>
      )}
      {template?.issuesPassword ? (
        <div className="staff-notify-send-preview-note">
          <LockOutlined style={{ marginRight: 6 }} />
          {t('system.staffNotifications.send.preview.passwordNote', '密码发送时才随机生成，后台任何人都看不到，也不写入发送记录。')}
        </div>
      ) : null}
    </Card>
  )
}
