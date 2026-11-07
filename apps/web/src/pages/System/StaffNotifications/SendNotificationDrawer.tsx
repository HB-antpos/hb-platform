import { SendOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Segmented, Select, Space, Spin, Switch, Tag, Typography, message } from 'antd'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Trans, useTranslation } from 'react-i18next'

import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getStaffNotificationBatch,
  getStaffNotificationTemplates,
  preflightStaffNotification,
  retryStaffNotificationBatch,
  sendStaffNotification,
} from '../../../services/staffNotificationService'
import {
  STAFF_NOTIFICATION_ACCOUNT_ACTIVATED_KEY,
  STAFF_NOTIFICATION_ERROR_CODES,
  type StaffNotificationBatchDetail,
  type StaffNotificationPreflight,
  type StaffNotificationPreflightRecipient,
  type StaffNotificationRejected,
  type StaffNotificationTemplate,
} from '../../../types/staffNotification'

import { SendConfirmModal, SendProgressModal, SendResultModal, type RejectedLine } from './SendFlowModals'
import SendPreviewPane from './SendPreviewPane'
import SendRecipientTable from './SendRecipientTable'
import {
  countRecipientsByStatus,
  describeRejectedReason,
  filterRecipients,
  formatAud,
  getBatchProgress,
  getStaffNotificationErrorCode,
  getStaffNotificationErrorMessage,
  isRecipientSelectable,
  reconcileSelection,
  resolveSmsSenderId,
  startBatchPolling,
  summarizeSelection,
  type BatchPoller,
  type RecipientFilter,
} from './staffNotificationLogic'
import './staffNotificationSend.css'
import sendMessagesEn from './staffNotificationSendMessages.en.json'
import sendMessagesZh from './staffNotificationSendMessages.zh.json'

registerPageMessages({ zh: sendMessagesZh, en: sendMessagesEn })

type FlowState =
  | { step: 'idle' }
  | { step: 'confirm' }
  | { step: 'sending'; batchId: string; totalCount: number; detail: StaffNotificationBatchDetail | null; pollFailed: boolean }
  | { step: 'result'; detail: StaffNotificationBatchDetail | null }

export interface SendNotificationDrawerProps {
  open: boolean
  /** 目标员工：用户管理页勾选的人，或行尾菜单这一行的人（一次最多 100 个）。 */
  userGuids: string[]
  onClose: () => void
  /** 抽屉收起动画结束后调用，父组件在这里卸载抽屉。 */
  onAfterClose?: () => void
  /** 发送已提交并走完（点「完成」/「查看发送记录」/ 把进度窗口关到后台）：父组件清空勾选。 */
  onFinished: () => void
  /** 结果弹窗的「查看发送记录」：跳到员工通知页的发送记录页签。 */
  onViewLogs: () => void
  /** 提供时，邮箱存疑 / 缺邮箱的人旁边出现修改入口。 */
  onEditEmail?: (userGuid: string) => void
}

/** 渠道说明与服务状态一行：开关 + 名称 + 说明（+ 状态标签）。 */
function ChannelRow({ label, hint, control, extra }: { label: string; hint: ReactNode; control: ReactNode; extra?: ReactNode }) {
  return (
    <div className="staff-notify-send-channel">
      {control}
      <span>{label}</span>
      <span className="staff-notify-send-hint">
        {hint}
        {extra}
      </span>
    </div>
  )
}

/**
 * 发送员工通知抽屉：选模板 → 核对收件人 → 看预览 → 确认 → 后台逐人发送并显示进度 → 结果。
 * 关掉抽屉 / 弹窗不影响后台发送；卸载时必须停掉轮询，不留悬挂定时器。
 */
export default function SendNotificationDrawer({
  open,
  userGuids,
  onClose,
  onAfterClose,
  onFinished,
  onViewLogs,
  onEditEmail,
}: SendNotificationDrawerProps) {
  const { t } = useTranslation()

  const [templates, setTemplates] = useState<StaffNotificationTemplate[] | null>(null)
  const [templatesError, setTemplatesError] = useState<string | null>(null)
  const [templatesAttempt, setTemplatesAttempt] = useState(0)
  const [templateKey, setTemplateKey] = useState<string | undefined>(undefined)
  const [sendEmailChoice, setSendEmailChoice] = useState(true)
  const [sendSmsChoice, setSendSmsChoice] = useState(true)
  // 用户选的短信发件名；undefined = 还没选，用预检给的默认项。
  const [smsSenderChoice, setSmsSenderChoice] = useState<string | undefined>(undefined)

  const [preflight, setPreflight] = useState<StaffNotificationPreflight | null>(null)
  const [preflightLoading, setPreflightLoading] = useState(false)
  const [preflightError, setPreflightError] = useState<string | null>(null)
  const [preflightAttempt, setPreflightAttempt] = useState(0)
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set())
  const [filter, setFilter] = useState<RecipientFilter>('all')

  const [flow, setFlow] = useState<FlowState>({ step: 'idle' })
  const [rejected, setRejected] = useState<StaffNotificationRejected[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [retrying, setRetrying] = useState(false)

  const pollerRef = useRef<BatchPoller | null>(null)
  const mountedRef = useRef(true)
  // 上一次预检的名单与模板：重新预检时据此保留用户的勾选，换模板则按默认规则重新勾选。
  const previousRecipientsRef = useRef<StaffNotificationPreflightRecipient[] | null>(null)
  const previousTemplateKeyRef = useRef<string | undefined>(undefined)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      // 抽屉卸载时停止轮询；后台发送不受影响，结果在发送记录里看。
      pollerRef.current?.stop()
      pollerRef.current = null
    }
  }, [])

  // ===== 模板：只列已启用的；默认「账号已开通」 =====
  useEffect(() => {
    const controller = new AbortController()
    setTemplatesError(null)
    getStaffNotificationTemplates()
      .then((list) => {
        if (controller.signal.aborted) return
        const enabled = list.filter((template) => template.isEnabled)
        setTemplates(enabled)
        setTemplateKey((current) => current
          ?? enabled.find((template) => template.key === STAFF_NOTIFICATION_ACCOUNT_ACTIVATED_KEY)?.key
          ?? enabled[0]?.key)
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setTemplates([])
        setTemplatesError(getStaffNotificationErrorMessage(error, t('system.staffNotifications.send.templatesLoadFailed', '加载模板失败')))
      })
    return () => controller.abort()
    // 只在首次打开与点「重试」时重新拉取。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [templatesAttempt])

  const template = useMemo(() => templates?.find((item) => item.key === templateKey), [templates, templateKey])
  // 必须发邮件的模板（发放随机密码）邮件开关强制打开且不能关。
  const emailEnabled = template?.requiresEmail === true ? true : sendEmailChoice
  const smsAvailable = preflight?.smsAvailable === true
  const smsEnabled = sendSmsChoice && smsAvailable
  // 本次短信实际用的发件名：选择已失效则回落默认项；没勾短信 / 没有可选项时为 undefined，发送时不传。
  const smsSenders = preflight?.smsSenders ?? []
  const smsSenderId = smsEnabled ? resolveSmsSenderId(smsSenders, smsSenderChoice) : undefined
  // 只有真有得选（多于 1 项）才露出下拉；只有默认一项时保持界面不变。
  const showSmsSenderPicker = smsEnabled && smsSenders.length > 1
  const userGuidsKey = userGuids.join(',')

  // ===== 预检：打开抽屉、换模板、切换渠道开关时重新调用 =====
  useEffect(() => {
    if (!templateKey) return undefined
    // 两个渠道都关着没有意义，也不发请求；保留上一次的名单，提示用户先打开渠道。
    if (!emailEnabled && !sendSmsChoice) return undefined

    if (previousTemplateKeyRef.current !== templateKey) {
      previousTemplateKeyRef.current = templateKey
      previousRecipientsRef.current = null
    }
    const controller = new AbortController()
    setPreflightLoading(true)
    setPreflightError(null)
    preflightStaffNotification(
      { templateKey, userGuids, sendEmail: emailEnabled, sendSms: sendSmsChoice },
      controller.signal,
    )
      .then((result) => {
        if (controller.signal.aborted) return
        setPreflight(result)
        // 重新预检后可选发件名可能变了：之前选的项已不在名单里就回落默认项，不把失效值留到发送请求里。
        // 返回空名单（比如这次预检没勾短信）时保留用户的选择，等名单回来再核对。
        if (result.smsSenders.length > 0) {
          setSmsSenderChoice((previous) => resolveSmsSenderId(result.smsSenders, previous))
        }
        // setSelected 的更新函数会在稍后渲染时才执行，所以必须先把「上一次的名单」取出来再更新 ref，
        // 否则更新函数读到的已经是新名单，会把所有人都当成「已知且没勾选」，默认勾选就丢了。
        const previousRecipients = previousRecipientsRef.current
        previousRecipientsRef.current = result.recipients
        setSelected((previous) => new Set(reconcileSelection(previous, previousRecipients, result.recipients)))
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setPreflightError(getStaffNotificationErrorMessage(error, t('system.staffNotifications.send.preflightFailed', '收件人预检失败')))
      })
      .finally(() => {
        if (!controller.signal.aborted) setPreflightLoading(false)
      })
    return () => controller.abort()
    // userGuids 用拼接后的 key 比较，避免父组件每次渲染传入新数组导致重复预检。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [templateKey, emailEnabled, sendSmsChoice, userGuidsKey, preflightAttempt])

  const recipients = useMemo(() => preflight?.recipients ?? [], [preflight])
  const counts = useMemo(() => countRecipientsByStatus(recipients), [recipients])
  const visibleRecipients = useMemo(() => filterRecipients(recipients, filter), [recipients, filter])
  const summary = useMemo(
    () => summarizeSelection(recipients, selected, preflight?.smsUnitCostAud ?? 0),
    [recipients, selected, preflight?.smsUnitCostAud],
  )

  const handleToggle = (guids: string[], checked: boolean) => {
    const selectable = new Set(recipients.filter(isRecipientSelectable).map((recipient) => recipient.userGuid))
    setSelected((previous) => {
      const next = new Set(previous)
      for (const guid of guids) {
        if (!selectable.has(guid)) continue
        if (checked) next.add(guid)
        else next.delete(guid)
      }
      return next
    })
  }

  // ===== 发送与轮询 =====
  const beginPolling = (batchId: string) => {
    pollerRef.current?.stop()
    pollerRef.current = startBatchPolling({
      batchId,
      fetchBatch: getStaffNotificationBatch,
      onUpdate: (detail) => setFlow((previous) => (
        previous.step === 'sending' && previous.batchId === batchId ? { ...previous, detail } : previous
      )),
      onFinished: (detail) => setFlow({ step: 'result', detail }),
      // 连续取不到进度：后台仍在发送，弹窗提示去发送记录查看。
      onGiveUp: () => setFlow((previous) => (
        previous.step === 'sending' && previous.batchId === batchId ? { ...previous, pollFailed: true } : previous
      )),
    })
  }

  const handleConfirm = async (confirmReissue: boolean) => {
    if (!template || !preflight) return
    // 按预检顺序取勾选且可发送的人，保证与界面一致。
    const guids = recipients
      .filter((recipient) => selected.has(recipient.userGuid) && isRecipientSelectable(recipient))
      .map((recipient) => recipient.userGuid)
    setSubmitting(true)
    try {
      const result = await sendStaffNotification({
        templateKey: template.key,
        userGuids: guids,
        sendEmail: emailEnabled,
        sendSms: smsEnabled,
        smsSenderId,
        confirmReissue,
      })
      if (!mountedRef.current) return
      setRejected(result.rejected)
      if (result.totalCount <= 0) {
        // 全部被服务端拒绝：没有批次可看，直接给结果。
        setFlow({ step: 'result', detail: null })
        return
      }
      setFlow({ step: 'sending', batchId: result.batchId, totalCount: result.totalCount, detail: null, pollFailed: false })
      beginPolling(result.batchId)
    } catch (error) {
      if (!mountedRef.current) return
      const code = getStaffNotificationErrorCode(error)
      // CONFIRM_REQUIRED：服务端重新预检后发现有已登录过的人（比如期间有人登录了），回到确认框让用户重新核对。
      // NO_RECIPIENTS：服务端重新预检后选中的人全被拦下（比如期间账号被停用），重新预检让表格显示最新的被拦原因。
      if (code === STAFF_NOTIFICATION_ERROR_CODES.confirmRequired || code === STAFF_NOTIFICATION_ERROR_CODES.noRecipients) {
        setPreflightAttempt((attempt) => attempt + 1)
        setFlow({ step: 'idle' })
      }
      message.error(getStaffNotificationErrorMessage(error, t('system.staffNotifications.confirm.sendFailed', '发送失败')))
    } finally {
      if (mountedRef.current) setSubmitting(false)
    }
  }

  const handleRetry = async () => {
    if (flow.step !== 'result' || !flow.detail) return
    setRetrying(true)
    try {
      const detail = await retryStaffNotificationBatch(flow.detail.id)
      if (!mountedRef.current) return
      setFlow({ step: 'sending', batchId: detail.id, totalCount: detail.totalCount, detail, pollFailed: false })
      beginPolling(detail.id)
    } catch (error) {
      if (mountedRef.current) {
        message.error(getStaffNotificationErrorMessage(error, t('system.staffNotifications.result.retryFailed', '重试失败')))
        // 全部失败项都不能自动重试：服务端已把原因写进每个员工的「说明」，重新拉取详情让弹窗里直接看到。
        if (getStaffNotificationErrorCode(error) === STAFF_NOTIFICATION_ERROR_CODES.nothingToRetry) {
          try {
            const detail = await getStaffNotificationBatch(flow.detail.id)
            if (mountedRef.current) setFlow({ step: 'result', detail })
          } catch {
            // 刷新失败不影响已经弹出的错误提示。
          }
        }
      }
    } finally {
      if (mountedRef.current) setRetrying(false)
    }
  }

  const finishAndClose = () => {
    pollerRef.current?.stop()
    onFinished()
    onClose()
  }

  // 发送过程中关掉进度窗口：转入后台继续发送。
  const handleDetach = () => {
    message.info(t('system.staffNotifications.progress.closedToast', '已转入后台发送，结果可在「发送记录」里查看'))
    finishAndClose()
  }

  const handleViewLogs = () => {
    pollerRef.current?.stop()
    onFinished()
    onClose()
    onViewLogs()
  }

  const handleDrawerClose = () => {
    if (flow.step !== 'idle') return
    onClose()
  }

  const rejectedLines = useMemo<RejectedLine[]>(() => rejected.map((item) => {
    const person = recipients.find((recipient) => recipient.userGuid === item.userGuid)
    const described = describeRejectedReason(item.reason)
    return {
      key: item.userGuid,
      name: person?.displayName ?? item.userGuid,
      reason: described.known ? t(`system.staffNotifications.send.reason.${described.key}`) : described.raw,
    }
  }), [recipients, rejected, t])

  const sendingFlow = flow.step === 'sending' ? flow : null
  const progress = sendingFlow
    ? (sendingFlow.detail ? getBatchProgress(sendingFlow.detail) : { done: 0, total: sendingFlow.totalCount })
    : { done: 0, total: 0 }

  const noChannel = !emailEnabled && !smsEnabled
  const canSend = !!template && !!preflight && !preflightLoading && summary.selectedCount > 0 && !noChannel && flow.step === 'idle'

  // 底部汇总：只统计当前勾选里能发的人。
  const summaryParts: ReactNode[] = []
  if (emailEnabled) {
    summaryParts.push(
      template?.issuesPassword
        ? <Trans key="email" i18nKey="system.staffNotifications.send.summary.emailPassword" defaults="邮件 <b>{{count}}</b> 封（含随机密码）" values={{ count: summary.emailCount }} components={{ b: <b /> }} />
        : <Trans key="email" i18nKey="system.staffNotifications.send.summary.email" defaults="邮件 <b>{{count}}</b> 封" values={{ count: summary.emailCount }} components={{ b: <b /> }} />,
    )
  }
  if (smsEnabled) {
    summaryParts.push(
      <Trans key="sms" i18nKey="system.staffNotifications.send.summary.sms" defaults="短信 <b>{{count}}</b> 条（约 {{cost}}）" values={{ count: summary.smsSegments, cost: formatAud(summary.smsCostAud) }} components={{ b: <b /> }} />,
    )
  }
  if (summary.skippedCount > 0) {
    summaryParts.push(
      <span key="skipped">{t('system.staffNotifications.send.summary.skipped', '跳过 {{count}} 人', { count: summary.skippedCount })}</span>,
    )
  }

  return (
    <>
      <Drawer
        open={open}
        width="min(1040px, 100vw)"
        title={t('system.staffNotifications.send.title', '发送员工通知')}
        extra={<Typography.Text type="secondary">{t('system.staffNotifications.send.selectedCount', '已选 {{count}} 人', { count: userGuids.length })}</Typography.Text>}
        styles={{ body: { padding: 20 } }}
        maskClosable={flow.step === 'idle'}
        keyboard={flow.step === 'idle'}
        onClose={handleDrawerClose}
        afterOpenChange={(visible) => {
          if (!visible) onAfterClose?.()
        }}
        footer={(
          <div className="staff-notify-send-footer">
            <span className="staff-notify-send-summary">
              {summary.selectedCount > 0 ? (
                <>
                  <Trans i18nKey="system.staffNotifications.send.summary.lead" defaults="将发送给 <b>{{count}}</b> 人：" values={{ count: summary.selectedCount }} components={{ b: <b /> }} />
                  {summaryParts.flatMap((part, index) => (index === 0 ? [part] : [' · ', part]))}
                </>
              ) : t('system.staffNotifications.send.summary.empty', '还没有勾选收件人')}
            </span>
            <Space>
              <Button onClick={handleDrawerClose}>{t('system.staffNotifications.send.cancel', '取消')}</Button>
              <Button type="primary" icon={<SendOutlined />} disabled={!canSend} onClick={() => setFlow({ step: 'confirm' })}>
                {t('system.staffNotifications.send.sendButton', '发送（{{count}} 人）', { count: summary.selectedCount })}
              </Button>
            </Space>
          </div>
        )}
      >
        {templatesError ? (
          <Alert
            type="error"
            showIcon
            message={templatesError}
            action={<Button size="small" onClick={() => setTemplatesAttempt((attempt) => attempt + 1)}>{t('system.staffNotifications.send.retry', '重试')}</Button>}
          />
        ) : templates === null ? (
          <div style={{ textAlign: 'center', padding: 48 }}><Spin /></div>
        ) : !template ? (
          <Alert type="warning" showIcon message={t('system.staffNotifications.send.noTemplates', '没有可用的模板。请管理员先到「员工通知」页启用模板。')} />
        ) : (
          <div className="staff-notify-send-grid">
            <div>
              <div className="staff-notify-send-section">
                <div className="staff-notify-send-label">{t('system.staffNotifications.send.template', '模板')}</div>
                <Select
                  style={{ width: '100%' }}
                  value={templateKey}
                  onChange={setTemplateKey}
                  aria-label={t('system.staffNotifications.send.template', '模板')}
                  options={(templates ?? []).map((item) => ({ value: item.key, label: item.name }))}
                />
                {template.description ? (
                  <span className="staff-notify-send-hint" style={{ marginTop: 6 }}>{template.description}</span>
                ) : null}
              </div>

              <div className="staff-notify-send-section">
                <div className="staff-notify-send-label">{t('system.staffNotifications.send.channels', '发送方式')}</div>
                <ChannelRow
                  label={t('system.staffNotifications.send.email', '邮件')}
                  hint={template.issuesPassword
                    ? t('system.staffNotifications.send.emailDescPassword', '登录邮箱、随机密码、下载按钮。这个模板必须发邮件')
                    : t('system.staffNotifications.send.emailDesc', '发到员工的登录邮箱')}
                  control={(
                    <Switch
                      aria-label={t('system.staffNotifications.send.email', '邮件')}
                      checked={emailEnabled}
                      disabled={template.requiresEmail}
                      onChange={setSendEmailChoice}
                    />
                  )}
                />
                <ChannelRow
                  label={t('system.staffNotifications.send.sms', '短信')}
                  hint={template.issuesPassword
                    ? t('system.staffNotifications.send.smsDescPassword', '提醒查收邮件 + 下载短链，不含密码。邮件发出后才发短信')
                    : t('system.staffNotifications.send.smsDesc', '发到员工资料里的澳洲手机号')}
                  control={(
                    // 短信服务不可用时置灰并按关闭显示（写法同家长签字链接弹窗的 SmsSwitch）。
                    <Switch
                      aria-label={t('system.staffNotifications.send.sms', '短信')}
                      checked={smsEnabled}
                      disabled={!smsAvailable}
                      loading={preflight === null && preflightLoading}
                      onChange={setSendSmsChoice}
                    />
                  )}
                  extra={preflight ? (
                    smsAvailable
                      ? <Tag color="green" style={{ marginInlineStart: 4 }}>{t('system.staffNotifications.send.smsServiceOn', '短信服务已开启')}</Tag>
                      : <Tag style={{ marginInlineStart: 4 }}>{t('system.staffNotifications.send.smsServiceOff', '短信服务未开启')}</Tag>
                  ) : null}
                />
                {preflight && !smsAvailable ? (
                  <span className="staff-notify-send-hint">{t('system.staffNotifications.send.smsServiceOffHint', '服务器还没打开短信总开关，只能发邮件。')}</span>
                ) : null}
                {showSmsSenderPicker ? (
                  <div className="staff-notify-send-sender">
                    <span>{t('system.staffNotifications.send.smsSender', '短信发件名')}</span>
                    <Select
                      size="small"
                      style={{ width: 160 }}
                      value={smsSenderId}
                      onChange={setSmsSenderChoice}
                      aria-label={t('system.staffNotifications.send.smsSender', '短信发件名')}
                      options={smsSenders.map((sender) => ({ value: sender.id, label: sender.id }))}
                    />
                    <span className="staff-notify-send-hint">{t('system.staffNotifications.send.smsSenderHint', '收件人手机上显示的发信人名称')}</span>
                  </div>
                ) : null}
                {noChannel ? (
                  <Alert style={{ marginTop: 8 }} type="warning" showIcon message={t('system.staffNotifications.send.noChannelSelected', '至少要打开一个发送渠道。')} />
                ) : null}
              </div>

              <div className="staff-notify-send-section">
                <div className="staff-notify-send-label staff-notify-send-label-row">
                  <span>{t('system.staffNotifications.send.recipients', '收件人核对')}</span>
                  <Segmented<RecipientFilter>
                    size="small"
                    value={filter}
                    onChange={setFilter}
                    options={[
                      { value: 'all', label: t('system.staffNotifications.send.filterAll', '全部 {{count}}', { count: counts.all }) },
                      { value: 'ready', label: t('system.staffNotifications.send.filterReady', '可发送 {{count}}', { count: counts.ready }) },
                      { value: 'confirm', label: t('system.staffNotifications.send.filterConfirm', '需确认 {{count}}', { count: counts.confirm }) },
                      { value: 'blocked', label: t('system.staffNotifications.send.filterBlocked', '不可发送 {{count}}', { count: counts.blocked }) },
                    ]}
                  />
                </div>
                {preflightError ? (
                  <Alert
                    type="error"
                    showIcon
                    message={preflightError}
                    action={<Button size="small" onClick={() => setPreflightAttempt((attempt) => attempt + 1)}>{t('system.staffNotifications.send.retry', '重试')}</Button>}
                  />
                ) : (
                  <SendRecipientTable
                    recipients={visibleRecipients}
                    selected={selected}
                    loading={preflightLoading}
                    onToggle={handleToggle}
                    onEditEmail={onEditEmail}
                  />
                )}
              </div>
            </div>

            <SendPreviewPane
              template={template}
              recipients={recipients}
              emailEnabled={emailEnabled}
              smsEnabled={smsEnabled}
            />
          </div>
        )}
      </Drawer>

      {template && preflight ? (
        <SendConfirmModal
          open={flow.step === 'confirm'}
          template={template}
          summary={summary}
          emailEnabled={emailEnabled}
          smsEnabled={smsEnabled}
          smsSenderId={showSmsSenderPicker ? smsSenderId : undefined}
          smsUsedLast24h={preflight.smsUsedLast24h}
          smsDailyLimit={preflight.smsDailyLimit}
          smsUnitCostAud={preflight.smsUnitCostAud}
          submitting={submitting}
          onBack={() => setFlow({ step: 'idle' })}
          onConfirm={(confirmReissue) => void handleConfirm(confirmReissue)}
        />
      ) : null}

      <SendProgressModal
        open={flow.step === 'sending'}
        templateName={template?.name ?? ''}
        progress={progress}
        pollFailed={sendingFlow?.pollFailed === true}
        onClose={handleDetach}
        onViewLogs={handleViewLogs}
      />

      <SendResultModal
        open={flow.step === 'result'}
        detail={flow.step === 'result' ? flow.detail : null}
        rejected={rejectedLines}
        retrying={retrying}
        onRetry={() => void handleRetry()}
        onViewLogs={handleViewLogs}
        onDone={finishAndClose}
      />
    </>
  )
}
