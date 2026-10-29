import { DownloadOutlined, FilePdfOutlined, SendOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Input, Modal, Skeleton, Tag, message } from 'antd'
import { useEffect, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'

import { getMyobPayslipEmailTarget, sendMyobPayslipEmail } from '../../../services/myobPayslipService'
import type { MyobPayslipEmailTarget, MyobPayslipKey } from '../../../types/myobPayslip'
import { extractMyobError } from '../Myob/myobLogic'

import { isSameEmailAddress, isValidEmailAddress } from './payslipLogic'

const NOTE_MAX_LENGTH = 500

/** 后端错误码 → 文案键（静态映射，契约测试能核对每个键都存在）。 */
const SEND_ERROR_KEYS: Record<string, string> = {
  MYOB_PAYSLIP_RECIPIENT_UNCONFIRMED: 'payslips.email.errors.unconfirmed',
  MYOB_PAYSLIP_EMAIL_INVALID: 'payslips.email.errors.invalid',
  MYOB_PAYSLIP_EMAIL_NOT_CONFIGURED: 'payslips.email.errors.notConfigured',
  MYOB_PAYSLIP_EMAIL_SEND_FAILED: 'payslips.email.errors.sendFailed',
  MYOB_PAYSLIP_FORBIDDEN: 'payslips.email.errors.forbidden',
  MYOB_PAYSLIP_NOT_FOUND: 'payslips.email.errors.notFound',
}

type TargetState =
  | { status: 'loading' }
  | { status: 'error' }
  | { status: 'ready'; data: MyobPayslipEmailTarget }

interface PayslipEmailModalProps {
  /** 要发送的工资单。 */
  target: MyobPayslipKey
  employeeName: string
  /** 副标题里的工资周期（已格式化）。 */
  periodLabel: string
  onClose: () => void
  /** 失败后「下载 PDF」：由抽屉统一实现（含下载提示与错误处理），弹窗只等它结束。 */
  onDownloadPdf: () => Promise<void>
}

/**
 * 发送工资单 PDF 邮件弹窗。每次只发一个员工、一个收件人；附件与正文由后端生成，正文不含金额。
 * - 收件人默认员工档案邮箱；改成别的地址须勾选「我已核对收件人」才能发送（防发错人）；
 * - 档案没有邮箱时可手填，只用于本次发送、不写回档案，此时不需要二次核对；
 * - 发送失败弹窗保持打开并保留已填内容；成功后关闭并在上方轻提示。
 * 由抽屉在需要时才挂载（关闭即卸载），所以每次打开都是全新状态。
 */
export default function PayslipEmailModal({ target, employeeName, periodLabel, onClose, onDownloadPdf }: PayslipEmailModalProps) {
  const { t } = useTranslation()
  const [state, setState] = useState<TargetState>({ status: 'loading' })
  const [reloadTick, setReloadTick] = useState(0)
  const [to, setTo] = useState('')
  const [copyToMe, setCopyToMe] = useState(false)
  const [note, setNote] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [sending, setSending] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [sendError, setSendError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setState({ status: 'loading' })
    getMyobPayslipEmailTarget(target)
      .then((data) => {
        if (cancelled) {
          return
        }
        setState({ status: 'ready', data })
        // 默认带入档案邮箱；有当前登录人邮箱时默认勾选「抄送给我」。
        setTo(data.source === 'profile' ? (data.email ?? '') : '')
        setCopyToMe(Boolean(data.myEmail))
      })
      .catch(() => {
        if (!cancelled) {
          setState({ status: 'error' })
        }
      })
    return () => {
      cancelled = true
    }
    // target 的各字段是原始字符串，按值依赖，避免父组件每次渲染产生新对象导致重复请求。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target.connectionGuid, target.employeeUid, target.paymentDate, target.periodStart, reloadTick])

  const data = state.status === 'ready' ? state.data : null
  const hasProfileEmail = data?.source === 'profile' && Boolean(data.email)
  const trimmedTo = to.trim()
  const valid = isValidEmailAddress(trimmedTo)
  // 档案有邮箱且收件人被改成了别的地址：必须二次核对；档案无邮箱时手填地址不需要。
  const changedRecipient = hasProfileEmail && trimmedTo !== '' && !isSameEmailAddress(trimmedTo, data?.email)
  const matchesProfile = hasProfileEmail && isSameEmailAddress(trimmedTo, data?.email)
  const canSend = state.status === 'ready' && valid && (!changedRecipient || confirmed) && !sending
  const recipientEmpty = trimmedTo === ''
  const recipientStatus = (!hasProfileEmail && recipientEmpty) || (!recipientEmpty && !valid) ? 'error' : undefined

  const handleToChange = (value: string) => {
    setTo(value)
    // 二次核对只对「当前这个地址」有效：改了地址就要重新勾选。
    setConfirmed(false)
    setSendError(null)
  }

  const handleSend = async () => {
    if (!canSend) {
      return
    }
    setSending(true)
    setSendError(null)
    try {
      const result = await sendMyobPayslipEmail({
        ...target,
        to: trimmedTo,
        copyToMe: copyToMe && Boolean(data?.myEmail),
        note,
        recipientChangeConfirmed: changedRecipient ? confirmed : false,
      })
      message.success(t('payslips.email.sent', { to: result.sentTo || trimmedTo }))
      onClose()
    } catch (err) {
      const { code } = extractMyobError(err, '')
      // 失败：弹窗保持打开、已填内容不丢，红色提示 + 「下载 PDF」「重试」。
      setSendError(t(SEND_ERROR_KEYS[code ?? ''] ?? 'payslips.email.errors.other'))
      setSending(false)
    }
  }

  const handleDownload = async () => {
    setDownloading(true)
    try {
      await onDownloadPdf()
    } finally {
      setDownloading(false)
    }
  }

  const footer = sendError ? (
    <>
      <Button icon={<DownloadOutlined />} loading={downloading} disabled={sending} onClick={() => void handleDownload()}>
        {t('payslips.email.download')}
      </Button>
      <Button type="primary" loading={sending} disabled={!canSend && !sending} onClick={() => void handleSend()}>
        {t('payslips.email.retry')}
      </Button>
    </>
  ) : (
    <>
      <Button disabled={sending} onClick={onClose}>
        {t('payslips.email.cancel')}
      </Button>
      <Button type="primary" icon={sending ? undefined : <SendOutlined />} loading={sending} disabled={!canSend && !sending} onClick={() => void handleSend()}>
        {sending ? t('payslips.email.sending') : t('payslips.email.send')}
      </Button>
    </>
  )

  return (
    <Modal
      open
      width={580}
      rootClassName="payslip-email-modal"
      // 发送中不允许关闭，避免丢失结果
      closable={!sending}
      maskClosable={false}
      keyboard={!sending}
      onCancel={onClose}
      title={
        <div>
          <div>{t('payslips.email.title')}</div>
          <div className="payslip-email-sub">{t('payslips.email.subtitle', { name: employeeName, range: periodLabel })}</div>
        </div>
      }
      footer={state.status === 'ready' ? footer : null}
    >
      {state.status === 'loading' ? (
        <div aria-busy="true" aria-label={t('payslips.email.loadingTarget')}>
          <Skeleton active paragraph={{ rows: 6 }} />
        </div>
      ) : null}

      {state.status === 'error' ? (
        <Alert
          type="error"
          showIcon
          message={t('payslips.email.targetFailed')}
          action={
            <Button size="small" onClick={() => setReloadTick((tick) => tick + 1)}>
              {t('payslips.email.retry')}
            </Button>
          }
        />
      ) : null}

      {data ? (
        <div className="payslip-email-body">
          <div className="payslip-email-field">
            <label htmlFor="payslip-email-to">
              <em>*</em>
              {t('payslips.email.recipient')}
            </label>
            <Input
              id="payslip-email-to"
              type="email"
              autoComplete="off"
              allowClear
              value={to}
              status={recipientStatus}
              placeholder={t('payslips.email.recipientPlaceholder')}
              disabled={sending}
              onChange={(event) => handleToChange(event.target.value)}
              onPressEnter={() => void handleSend()}
            />
            {!recipientEmpty && !valid ? <div className="payslip-email-error">{t('payslips.email.recipientInvalid')}</div> : null}
            {hasProfileEmail ? (
              <div className="payslip-email-hint">
                {t('payslips.email.recipientDefaultHint')}{' '}
                {matchesProfile ? <Tag color="success">{t('payslips.email.matchesProfile')}</Tag> : null}
              </div>
            ) : null}
            {!hasProfileEmail ? (
              <Alert className="payslip-email-alert" type="info" showIcon message={<Trans i18nKey="payslips.email.noProfileEmail" components={{ b: <b /> }} />} />
            ) : null}
            {changedRecipient ? (
              <>
                <Alert
                  className="payslip-email-alert"
                  type="warning"
                  showIcon
                  message={<Trans i18nKey="payslips.email.changedWarning" values={{ masked: data.maskedEmail ?? '' }} components={{ b: <b /> }} />}
                />
                <Checkbox className="payslip-email-confirm" checked={confirmed} disabled={sending} onChange={(event) => setConfirmed(event.target.checked)}>
                  {t('payslips.email.confirmRecipient')}
                </Checkbox>
              </>
            ) : null}
          </div>

          <div className="payslip-email-field">
            <label>{t('payslips.email.cc')}</label>
            <Checkbox checked={copyToMe && Boolean(data.myEmail)} disabled={!data.myEmail || sending} onChange={(event) => setCopyToMe(event.target.checked)}>
              {data.myEmail ? t('payslips.email.copyToMe', { email: data.myEmail }) : t('payslips.email.copyToMeNone')}
            </Checkbox>
          </div>

          <div className="payslip-email-field">
            <label htmlFor="payslip-email-subject">{t('payslips.email.subject')}</label>
            <Input id="payslip-email-subject" readOnly value={data.subject} />
          </div>

          <div className="payslip-email-field">
            <label htmlFor="payslip-email-note">{t('payslips.email.note')}</label>
            <Input.TextArea
              id="payslip-email-note"
              rows={3}
              maxLength={NOTE_MAX_LENGTH}
              showCount
              value={note}
              disabled={sending}
              placeholder={t('payslips.email.notePlaceholder')}
              onChange={(event) => setNote(event.target.value)}
            />
          </div>

          <div className="payslip-email-field">
            <label>{t('payslips.email.attachment')}</label>
            <div className="payslip-email-attach">
              <span className="payslip-email-attach-icon">
                <FilePdfOutlined />
              </span>
              <div>
                <b>{data.filename}</b>
                <small>{t('payslips.email.attachmentHint')}</small>
              </div>
            </div>
          </div>

          <Alert type="info" showIcon message={<Trans i18nKey="payslips.email.info" components={{ b: <b /> }} />} />

          {sendError ? <Alert type="error" showIcon message={sendError} /> : null}
        </div>
      ) : null}
    </Modal>
  )
}
