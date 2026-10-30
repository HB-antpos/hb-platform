import { CheckOutlined, CloseOutlined, CopyOutlined, SendOutlined } from '@ant-design/icons'
import { Alert, Button, Descriptions, Form, Input, Modal, Radio, Select, Space, Switch, Tag, Typography, message } from 'antd'
import { useMemo, useState } from 'react'

import { minorStaffRequestError, sendMinorGuardianLink } from '../../../services/minorEmploymentService'
import type { GuardianLinkSendResult, MinorStaffComplianceItem } from '../../../types/minorEmployment'
import {
  buildGuardianLinkPayload,
  canSubmitGuardianLink,
  formatSydneyMonthDayTime,
  getCodeChannel,
  getDefaultChannels,
  getFormTypeLabel,
  getRelationshipOptions,
  getSendResultWarning,
  getSmsBlockReason,
  isValidEmail,
  type GuardianLinkFormValues,
} from './minorGuardianLinkLogic'

const VERSION_CONFLICT_CODE = 'MINOR_COMPLIANCE_VERSION_CONFLICT'

/** 短信不可用（总开关没开 / 不是澳洲手机）时置灰并按关闭显示，勾选状态与实际提交一致；表单里的原值保留，换回手机号后恢复。 */
function SmsSwitch({ checked, onChange, blocked }: { checked?: boolean; onChange?: (checked: boolean) => void; blocked: boolean }) {
  return <Switch aria-label="发送短信" disabled={blocked} checked={checked === true && !blocked} onChange={onChange} />
}

interface SendModalProps {
  item: MinorStaffComplianceItem
  smsAvailable: boolean
  /** 重新发送：标题不同，并提示旧链接会作废。 */
  resend: boolean
  onClose: () => void
  onSent: (result: GuardianLinkSendResult) => void
  /** 版本冲突：关闭弹窗并重新加载列表。 */
  onConflict: () => void
}

/** 发送 / 重新发送家长签字链接弹窗；由父组件按需挂载，每次打开都是新状态。 */
export function MinorGuardianSendModal({ item, smsAvailable, resend, onClose, onSent, onConflict }: SendModalProps) {
  const [form] = Form.useForm<GuardianLinkFormValues>()
  const [submitting, setSubmitting] = useState(false)
  const initialValues = useMemo<GuardianLinkFormValues>(() => ({
    guardianName: item.guardianName ?? '',
    guardianRelationship: item.guardianRelationship,
    guardianEmail: item.guardianEmail ?? '',
    guardianPhone: item.guardianPhone ?? '',
    ...getDefaultChannels(item, smsAvailable),
    expiryDays: 3,
  }), [item, smsAvailable])
  // preserve: true 让被置灰 / 未渲染的字段也能读到，开关与按钮状态实时跟随表单。
  const watched = Form.useWatch((all: GuardianLinkFormValues) => all, { form, preserve: true })
  const values = watched ?? initialValues
  const relationshipOptions = useMemo(() => getRelationshipOptions(item.guardianRelationship), [item.guardianRelationship])
  const smsBlock = getSmsBlockReason(values.guardianPhone, smsAvailable)
  const canSubmit = canSubmitGuardianLink(values, smsAvailable)
  const codeChannel = getCodeChannel(values, smsAvailable)

  const submit = async () => {
    let formValues: GuardianLinkFormValues
    try {
      formValues = await form.validateFields()
    } catch {
      return
    }
    // validateFields 只返回已注册字段，合并一次确保开关值完整。
    const merged = { ...values, ...formValues }
    if (!canSubmitGuardianLink(merged, smsAvailable)) return
    setSubmitting(true)
    try {
      const result = await sendMinorGuardianLink(item.userGUID, buildGuardianLinkPayload(item, merged, smsAvailable))
      onSent(result)
    } catch (error) {
      const { code, message: text } = minorStaffRequestError(error, '发送失败，请稍后重试')
      if (code === VERSION_CONFLICT_CODE) {
        message.error(text || '资料已被其他人更新，请刷新后重试')
        onConflict()
        return
      }
      message.error(text)
    } finally {
      setSubmitting(false)
    }
  }

  const userLine = `${item.employeeName}（${item.age} 岁）${item.username ? ` · @${item.username}` : ''}`
  const storeLine = [item.storeName || item.storeCode || '—', item.stateCode === 'UNKNOWN' ? '州未确认' : item.stateCode].join(' · ')

  return (
    <Modal
      open
      width={520}
      title={resend ? '重新发送家长签字链接' : '发送家长签字链接'}
      onCancel={onClose}
      maskClosable={false}
      footer={[
        <Button key="cancel" onClick={onClose}>取消</Button>,
        <Button key="send" type="primary" icon={<SendOutlined />} loading={submitting} disabled={!canSubmit} onClick={() => void submit()}>
          发送
        </Button>,
      ]}
    >
      {resend ? (
        <Alert type="warning" showIcon message="提交后会生成新链接，家长手里的旧链接立即作废。" style={{ marginBottom: 12 }} />
      ) : null}
      <Descriptions
        bordered
        size="small"
        column={1}
        style={{ marginBottom: 16 }}
        items={[
          { key: 'employee', label: '员工', children: userLine },
          { key: 'store', label: '门店 / 州', children: storeLine },
          {
            key: 'form',
            label: '同意书',
            children: (
              <span>
                {getFormTypeLabel(item.formType, true)}
                <Typography.Text type="secondary" style={{ marginLeft: 8, fontSize: 12 }}>（按门店所在州自动选）</Typography.Text>
              </span>
            ),
          },
        ]}
      />
      <Form form={form} layout="vertical" initialValues={initialValues} requiredMark>
        <div style={{ display: 'grid', gridTemplateColumns: '3fr 2fr', gap: 12 }}>
          <Form.Item name="guardianName" label="监护人姓名" rules={[{ required: true, whitespace: true, message: '请填写监护人姓名' }]}>
            <Input maxLength={200} placeholder="与证件一致的英文姓名" />
          </Form.Item>
          <Form.Item name="guardianRelationship" label="关系" rules={[{ required: true, message: '请选择关系' }]}>
            <Select options={relationshipOptions} placeholder="请选择" />
          </Form.Item>
        </div>

        <div style={{ marginBottom: 8 }}>
          发送方式 <Typography.Text type="secondary" style={{ fontSize: 12 }}>（至少开一个）</Typography.Text>
        </div>
        <div style={{ display: 'grid', gridTemplateColumns: 'auto 72px 1fr', columnGap: 10, alignItems: 'start' }}>
          <Form.Item name="sendEmail" valuePropName="checked" style={{ marginBottom: 12, paddingTop: 5 }}>
            <Switch aria-label="发送邮件" />
          </Form.Item>
          <span style={{ paddingTop: 5 }}>发送邮件</span>
          <Form.Item
            name="guardianEmail"
            dependencies={['sendEmail']}
            style={{ marginBottom: 12 }}
            rules={[
              { required: values.sendEmail === true, whitespace: true, message: '开了邮件就要填写邮箱' },
              { validator: (_, value?: string) => (!value?.trim() || isValidEmail(value) ? Promise.resolve() : Promise.reject(new Error('邮箱格式不正确'))) },
            ]}
          >
            <Input maxLength={254} placeholder="家长邮箱" />
          </Form.Item>

          <Form.Item name="sendSms" valuePropName="checked" style={{ marginBottom: 12, paddingTop: 5 }}>
            <SmsSwitch blocked={smsBlock !== null} />
          </Form.Item>
          <Typography.Text type={smsBlock ? 'secondary' : undefined} style={{ paddingTop: 5 }}>发送短信</Typography.Text>
          {/* 外层 Form.Item 不绑字段，只负责显示内层 noStyle 电话字段的校验错误与短信说明。 */}
          <Form.Item
            style={{ marginBottom: 12 }}
            extra={
              smsBlock === 'sms_unavailable'
                ? '服务器还没打开短信总开关，只能发邮件。'
                : smsBlock === 'not_mobile'
                  ? '固定电话收不到短信，请改用邮件，或填写 04 开头的手机号。'
                  : undefined
            }
          >
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Form.Item name="guardianPhone" noStyle rules={[{ required: true, whitespace: true, message: '请填写监护人电话' }]}>
                <Input maxLength={50} placeholder="04xx xxx xxx" style={{ flex: 1 }} />
              </Form.Item>
              {smsBlock === 'sms_unavailable' ? (
                <Tag style={{ marginInlineEnd: 0 }}>短信服务未开启</Tag>
              ) : smsBlock === null ? (
                <Tag color="green" style={{ marginInlineEnd: 0 }}>短信服务已开启</Tag>
              ) : null}
            </div>
          </Form.Item>
        </div>

        <Form.Item name="expiryDays" label="链接有效期" style={{ marginBottom: 16 }}>
          <Radio.Group optionType="button" options={[{ value: 1, label: '1 天' }, { value: 3, label: '3 天' }, { value: 7, label: '7 天' }]} />
        </Form.Item>
      </Form>
      <Alert
        type="info"
        showIcon
        message={
          <span>
            验证码发到<Typography.Text strong>{codeChannel === 'sms' ? '手机短信' : '邮箱'}</Typography.Text>
            {codeChannel === 'sms' ? '（开了短信就走短信）' : '（没开短信时走邮件）'}。家长看到的邮件、短信和签署页都是英文。改过的邮箱 / 手机号会写回监护人资料。
          </span>
        }
      />
    </Modal>
  )
}

interface ResultModalProps {
  result: GuardianLinkSendResult
  onClose: () => void
}

/** 有渠道失败或返回了链接时的「发送结果」：每个渠道是否送达、有效期、可复制的签字链接。 */
export function MinorGuardianSendResultModal({ result, onClose }: ResultModalProps) {
  const copy = async () => {
    if (!result.signingUrl) return
    try {
      await navigator.clipboard.writeText(result.signingUrl)
      message.success('链接已复制')
    } catch {
      message.warning('复制失败，请手动选中链接复制')
    }
  }
  const channelRow = (requested: boolean, sent: boolean, target: string | undefined, error: string | undefined) => {
    if (!requested) return <Typography.Text type="secondary">未发送</Typography.Text>
    return sent ? (
      <Space size={6}>
        <Tag color="success" icon={<CheckOutlined />} style={{ marginInlineEnd: 0 }}>已送达</Tag>
        <span>{target || ''}</span>
      </Space>
    ) : (
      <Space size={6} wrap>
        <Tag color="error" icon={<CloseOutlined />} style={{ marginInlineEnd: 0 }}>失败</Tag>
        <span>{error || '发送失败'}</span>
      </Space>
    )
  }

  return (
    <Modal open width={460} title="发送结果" onCancel={onClose} footer={<Button type="primary" onClick={onClose}>知道了</Button>}>
      <Descriptions
        bordered
        size="small"
        column={1}
        style={{ marginBottom: 16 }}
        items={[
          { key: 'email', label: '邮件', children: channelRow(result.emailRequested, result.emailSent, result.item.maskedGuardianEmail, result.emailError) },
          { key: 'sms', label: '短信', children: channelRow(result.smsRequested, result.smsSent, result.item.maskedGuardianPhone, result.smsError) },
          { key: 'expires', label: '有效至', children: `${formatSydneyMonthDayTime(result.expiresAtUtc)}（悉尼时间）` },
        ]}
      />
      <Alert type="warning" showIcon message={getSendResultWarning(result)} style={{ marginBottom: 12 }} />
      {result.signingUrl ? (
        <Space.Compact block>
          <Input readOnly value={result.signingUrl} onFocus={(event) => event.target.select()} aria-label="签字链接" />
          <Button icon={<CopyOutlined />} onClick={() => void copy()}>复制</Button>
        </Space.Compact>
      ) : null}
    </Modal>
  )
}
