import { MailOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Input, Layout, Space, Typography } from 'antd'
import { useEffect, useState } from 'react'

import { guardianErrorCode, sendGuardianCode, verifyGuardianCode } from '../../../services/minorEmploymentService'
import type { GuardianSession } from '../../../types/minorEmployment'

// 后端错误信息形如 "GUARDIAN_CODE_INVALID: 验证码不正确…"，展示时去掉错误码前缀。
const readable = (error: unknown, fallback: string) =>
  error instanceof Error && error.message ? error.message.replace(/^[A-Z_]+:\s*/, '') : fallback

/**
 * 监护人身份核验：向登记邮箱发送 6 位验证码，通过后拿到会话密钥再查看与签署。
 * 员工或其他人即使拿到链接，没有监护人邮箱里的验证码也看不到孩子资料、无法代签。
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

  const send = async () => {
    setSending(true)
    setNotice(null)
    try {
      const next = await sendGuardianCode(token)
      setState(next)
      setNotice({ type: 'success', text: `验证码已发送到 ${next.maskedGuardianEmail} / Code sent` })
    } catch (error) {
      setNotice({ type: 'error', text: readable(error, '验证码发送失败，请稍后重试') })
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
      setNotice({ type: 'error', text: readable(error, '验证失败，请重试') })
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
            <Typography.Text type="secondary">Hot Bargain · 未成年员工家长签署</Typography.Text>
            <Typography.Title level={3} style={{ margin: 0 }}>
              <SafetyCertificateOutlined /> 验证监护人身份
            </Typography.Title>
            <Typography.Paragraph style={{ margin: 0 }}>
              为确认是家长 / 监护人本人签署，我们会向 <Typography.Text strong>{state.maskedGuardianEmail}</Typography.Text> 发送 6 位验证码。
            </Typography.Paragraph>
            <Typography.Paragraph type="secondary" style={{ margin: 0 }}>
              To confirm you are the parent or guardian, we will email a 6-digit code to {state.maskedGuardianEmail}.
            </Typography.Paragraph>
            <Button
              icon={<MailOutlined />}
              block
              type={codeSent ? 'default' : 'primary'}
              loading={sending}
              disabled={sending || cooldown > 0 || state.remainingSends <= 0}
              onClick={() => void send()}
            >
              {cooldown > 0
                ? `重新发送（${cooldown} 秒）`
                : codeSent
                  ? '重新发送验证码 / Resend code'
                  : '发送验证码 / Send code'}
            </Button>
            {codeSent ? (
              <Space.Compact style={{ width: '100%' }}>
                <Input
                  size="large"
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  maxLength={6}
                  placeholder="6 位验证码 / 6-digit code"
                  value={code}
                  onChange={(event) => setCode(event.target.value.replace(/\D/g, '').slice(0, 6))}
                  onPressEnter={() => { if (code.length === 6 && !verifying) void verify() }}
                />
                <Button size="large" type="primary" loading={verifying} disabled={code.length !== 6 || verifying} onClick={() => void verify()}>
                  验证
                </Button>
              </Space.Compact>
            ) : null}
            {notice ? <Alert type={notice.type} showIcon message={notice.text} /> : null}
            {state.remainingSends <= 0 ? (
              <Alert type="warning" showIcon message="验证码发送次数已用完，请联系员工重新发送签署邮件。" />
            ) : null}
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              验证码 10 分钟内有效，请勿告诉任何人（包括员工本人）。没收到请检查垃圾邮件。
            </Typography.Text>
          </Space>
        </Card>
      </main>
    </Layout>
  )
}
