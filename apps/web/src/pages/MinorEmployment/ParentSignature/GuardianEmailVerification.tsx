import { MailOutlined, MessageOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Input, Layout, Space, Typography } from 'antd'
import { useEffect, useState } from 'react'

import { guardianErrorCode, mapGuardianSession, sendGuardianCode, verifyGuardianCode } from '../../../services/minorEmploymentService'
import type { GuardianSession } from '../../../types/minorEmployment'

import { guardianErrorText } from './guardianErrors'

// 部分错误（冷却中、发送失败）后端会在 data 里带回最新会话，用它刷新倒计时与剩余次数。
function sessionFromError(error: unknown): GuardianSession | undefined {
  const data = (error as { payload?: { data?: unknown; Data?: unknown } } | undefined)?.payload
  const raw = data?.data ?? data?.Data
  return raw && typeof raw === 'object' ? mapGuardianSession(raw as Record<string, unknown>) : undefined
}

/**
 * 监护人身份核验：向登记的邮箱或手机发送 6 位验证码，通过后拿到会话密钥再查看与签署。
 * 员工或其他人即使拿到链接，没有监护人收到的验证码也看不到孩子资料、无法代签。
 * 家长看到的文字一律英文。
 */
export default function GuardianEmailVerification({
  token,
  session,
  onVerified,
}: {
  token: string
  session: GuardianSession
  onVerified: (sessionKey: string) => void
}) {
  const [state, setState] = useState(session)
  const [code, setCode] = useState('')
  const [sending, setSending] = useState(false)
  const [verifying, setVerifying] = useState(false)
  const [notice, setNotice] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [now, setNow] = useState(() => Date.now())

  // 重发冷却倒计时；页面隐藏时计时器会被节流，所以每次都按服务端给的时间点重新计算。
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  const resendAt = state.resendAvailableAt ? Date.parse(state.resendAvailableAt) : 0
  const cooldown = Math.max(0, Math.ceil((resendAt - now) / 1000))
  const codeSent = Boolean(state.codeSentAt)
  // 短信渠道的邀请：验证码发到监护人手机，页面提示手机号；其余仍提示邮箱。
  const bySms = state.codeChannel === 'sms' && Boolean(state.maskedGuardianPhone)
  const target = bySms ? state.maskedGuardianPhone : state.maskedGuardianEmail

  const send = async () => {
    setSending(true)
    setNotice(null)
    try {
      const next = await sendGuardianCode(token)
      setState(next)
      setNotice({ type: 'success', text: next.codeChannel === 'sms' ? 'Code sent. Check your messages.' : 'Code sent. Check your email.' })
    } catch (error) {
      const latest = sessionFromError(error)
      if (latest) setState(latest)
      setNotice({ type: 'error', text: guardianErrorText(error, guardianErrorCode(error), "We couldn't send the code. Please try again in a few minutes.") })
    } finally {
      setSending(false)
    }
  }

  const verify = async () => {
    setVerifying(true)
    setNotice(null)
    try {
      onVerified(await verifyGuardianCode(token, code))
    } catch (error) {
      const errorCode = guardianErrorCode(error)
      // 验证码过期或被锁：清空输入，引导重新发送。
      if (errorCode === 'GUARDIAN_CODE_EXPIRED' || errorCode === 'GUARDIAN_CODE_LOCKED') setCode('')
      setNotice({ type: 'error', text: guardianErrorText(error, errorCode, "We couldn't verify the code. Please try again.") })
    } finally {
      setVerifying(false)
    }
  }

  return (
    <Layout style={{ minHeight: '100vh', background: '#f5f7fa', padding: 16 }}>
      <meta name="referrer" content="no-referrer" />
      <main style={{ maxWidth: 480, width: '100%', margin: '40px auto 0' }}>
        <Card>
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Typography.Text type="secondary">Hot Bargain · Parent / guardian consent</Typography.Text>
            <Typography.Title level={3} style={{ margin: 0 }}>
              <SafetyCertificateOutlined /> Verify it&apos;s you
            </Typography.Title>
            <Typography.Paragraph style={{ margin: 0 }}>
              To confirm you are the parent or guardian, {codeSent ? 'we sent' : "we'll send"} a 6-digit code {bySms ? 'by text' : 'by email'} to{' '}
              <Typography.Text strong>{target || (bySms ? 'your mobile' : 'your email')}</Typography.Text>.
            </Typography.Paragraph>
            <Button
              icon={bySms ? <MessageOutlined /> : <MailOutlined />}
              block
              type={codeSent ? 'default' : 'primary'}
              loading={sending}
              disabled={sending || cooldown > 0 || state.remainingSends <= 0}
              onClick={() => void send()}
            >
              {cooldown > 0
                ? `Resend code (${cooldown} s)`
                : codeSent
                  ? 'Resend code'
                  : 'Send code'}
            </Button>
            {codeSent ? (
              <Space.Compact style={{ width: '100%' }}>
                <Input
                  size="large"
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  maxLength={6}
                  placeholder="6-digit code"
                  aria-label="6-digit code"
                  value={code}
                  onChange={(event) => setCode(event.target.value.replace(/\D/g, '').slice(0, 6))}
                  onPressEnter={() => { if (code.length === 6 && !verifying) void verify() }}
                />
                <Button size="large" type="primary" loading={verifying} disabled={code.length !== 6 || verifying} onClick={() => void verify()}>
                  Verify
                </Button>
              </Space.Compact>
            ) : null}
            {notice ? <Alert type={notice.type} showIcon message={notice.text} /> : null}
            {state.remainingSends <= 0 ? (
              <Alert type="warning" showIcon message={`No more codes can be sent for this link. Please ask the store to send you a new signing ${bySms ? 'text message' : 'email'}.`} />
            ) : null}
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              The code expires in 10 minutes. Do not share it with anyone, including your child.{' '}
              {bySms ? "Didn't get it? Check your signal and resend." : "Didn't get it? Check your junk or spam folder and resend."}
            </Typography.Text>
          </Space>
        </Card>
      </main>
    </Layout>
  )
}
