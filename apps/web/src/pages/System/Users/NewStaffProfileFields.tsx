import { SafetyCertificateOutlined } from '@ant-design/icons'
import { DatePicker, Form, Input, Radio, Space, Tag, Typography, theme } from 'antd'
import type { Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'
import {
  EARLIEST_BIRTHDAY,
  GUARDIAN_RELATIONSHIP_OPTIONS,
  calculateAge,
  isAustralianMobile,
  isBirthdayInRange,
  isMinorAge,
  isSameContactPhone,
  isSameEmail,
  isStaffPhoneValid,
} from './newStaffProfileRules'

/** 「创建用户」表单里新建员工相关的输入字段（生日在表单里是 Dayjs，提交前再格式化）。 */
export interface NewStaffFormInputs {
  phone?: string
  birthday?: Dayjs | null
  guardianName?: string
  guardianRelationship?: string
  guardianPhone?: string
  guardianEmail?: string
}

const BIRTHDAY_FORMAT = 'YYYY-MM-DD'

export function formatBirthdayValue(value: Dayjs | null | undefined) {
  return value && value.isValid() ? value.format(BIRTHDAY_FORMAT) : ''
}

/** 员工电话（新建必填；后端只要求 8~15 位数字）。 */
export function NewStaffPhoneItem() {
  const { t } = useTranslation()
  return (
    <Form.Item
      label={t('system.usersNewStaff.phone', '电话')}
      name="phone"
      rules={[
        { required: true, whitespace: true, message: t('system.usersNewStaff.phoneRequired', '请输入电话') },
        {
          validator: (_, value?: string) =>
            !value?.trim() || isStaffPhoneValid(value)
              ? Promise.resolve()
              : Promise.reject(new Error(t('system.usersNewStaff.phoneInvalid', '电话格式无效（8–15 位数字）'))),
        },
      ]}
    >
      <Input inputMode="tel" autoComplete="off" />
    </Form.Item>
  )
}

/**
 * 生日 + 年龄标签；未满 18 岁时在下方展开黄色「监护人」区块（四项必填）。
 * 区块收起时监护人字段会从表单注销，不参与校验；提交时也不会带 guardian（见 buildNewStaffPayloadFields）。
 */
export function NewStaffBirthdayAndGuardian() {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const form = Form.useFormInstance<NewStaffFormInputs>()
  const birthday = Form.useWatch('birthday', form)
  // 年龄与可选范围都按悉尼当天算，避免浏览器在其他时区时差一天。
  const todayIso = formatSydneyIsoDate()
  const birthdayIso = formatBirthdayValue(birthday)
  const age = isBirthdayInRange(birthdayIso, todayIso) ? calculateAge(birthdayIso, todayIso) : null
  const isMinor = isMinorAge(age)

  const ageTag = age === null ? null : isMinor ? (
    <Tag color="gold" style={{ marginInlineEnd: 0 }}>
      {t('system.usersNewStaff.ageMinor', '{{age}} 岁 · 未成年', { age })}
    </Tag>
  ) : (
    <Tag style={{ marginInlineEnd: 0 }}>{t('system.usersNewStaff.age', '{{age}} 岁', { age })}</Tag>
  )

  return (
    <>
      <Form.Item label={t('system.usersNewStaff.birthday', '生日')} required>
        <Space align="center" size={8} wrap>
          <Form.Item
            name="birthday"
            noStyle
            rules={[
              { required: true, message: t('system.usersNewStaff.birthdayRequired', '请选择生日') },
              {
                validator: (_, value?: Dayjs | null) =>
                  !value || isBirthdayInRange(formatBirthdayValue(value), formatSydneyIsoDate())
                    ? Promise.resolve()
                    : Promise.reject(new Error(t('system.usersNewStaff.birthdayOutOfRange', '生日不能晚于今天，也不能早于 1940-01-01'))),
              },
            ]}
          >
            <DatePicker
              format={BIRTHDAY_FORMAT}
              style={{ width: 200 }}
              placeholder={t('system.usersNewStaff.birthdayPlaceholder', '选择生日')}
              disabledDate={(current) => {
                const value = current.format(BIRTHDAY_FORMAT)
                return value > todayIso || value < EARLIEST_BIRTHDAY
              }}
            />
          </Form.Item>
          {ageTag}
        </Space>
      </Form.Item>

      {isMinor ? (
        <div
          style={{
            background: token.colorWarningBg,
            border: `1px solid ${token.colorWarningBorder}`,
            borderRadius: token.borderRadiusLG,
            padding: '12px 16px 0',
            marginBottom: token.marginLG,
          }}
        >
          <Typography.Text strong>
            <SafetyCertificateOutlined style={{ color: token.colorWarning, marginInlineEnd: 6 }} />
            {t('system.usersNewStaff.guardianTitle', '监护人（未满 18 岁必填）')}
          </Typography.Text>
          <Typography.Paragraph type="secondary" style={{ fontSize: token.fontSizeSM, margin: '4px 0 12px' }}>
            {t(
              'system.usersNewStaff.guardianHint',
              '创建后可在「员工个人信息维护 → 未成年用工合规」或手机「人员管理」里给家长发送英文签字链接。',
            )}
          </Typography.Paragraph>
          <Form.Item
            label={t('system.usersNewStaff.guardianName', '监护人姓名')}
            name="guardianName"
            rules={[{ required: true, whitespace: true, message: t('system.usersNewStaff.guardianNameRequired', '请输入监护人姓名') }]}
          >
            <Input maxLength={200} />
          </Form.Item>
          <Form.Item
            label={t('system.usersNewStaff.guardianRelationship', '关系')}
            name="guardianRelationship"
            rules={[{ required: true, message: t('system.usersNewStaff.guardianRelationshipRequired', '请选择与员工的关系') }]}
          >
            <Radio.Group optionType="button">
              {GUARDIAN_RELATIONSHIP_OPTIONS.map((option) => (
                <Radio.Button key={option.value} value={option.value}>
                  {t(option.labelKey, option.defaultLabel)}
                </Radio.Button>
              ))}
            </Radio.Group>
          </Form.Item>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', columnGap: 16 }}>
            <Form.Item
              label={t('system.usersNewStaff.guardianPhone', '监护人手机')}
              name="guardianPhone"
              dependencies={['phone']}
              extra={t('system.usersNewStaff.guardianPhoneHint', '04 开头，用于接收签字短信和验证码')}
              rules={[
                { required: true, whitespace: true, message: t('system.usersNewStaff.guardianPhoneRequired', '请输入监护人手机') },
                ({ getFieldValue }) => ({
                  validator(_, value?: string) {
                    if (!value?.trim()) return Promise.resolve()
                    if (!isAustralianMobile(value)) {
                      return Promise.reject(new Error(t('system.usersNewStaff.guardianPhoneInvalid', '请输入 04 开头的澳洲手机号')))
                    }
                    if (isSameContactPhone(getFieldValue('phone'), value)) {
                      return Promise.reject(new Error(t('system.usersNewStaff.guardianPhoneSameAsStaff', '监护人手机不能和员工本人的电话相同')))
                    }
                    return Promise.resolve()
                  },
                }),
              ]}
            >
              <Input inputMode="tel" autoComplete="off" />
            </Form.Item>
            <Form.Item
              label={t('system.usersNewStaff.guardianEmail', '监护人邮箱')}
              name="guardianEmail"
              dependencies={['email']}
              rules={[
                { required: true, whitespace: true, message: t('system.usersNewStaff.guardianEmailRequired', '请输入监护人邮箱') },
                { type: 'email', message: t('system.usersNewStaff.guardianEmailInvalid', '监护人邮箱格式不正确') },
                ({ getFieldValue }) => ({
                  validator(_, value?: string) {
                    return isSameEmail(getFieldValue('email'), value)
                      ? Promise.reject(new Error(t('system.usersNewStaff.guardianEmailSameAsStaff', '监护人邮箱不能和员工本人的邮箱相同')))
                      : Promise.resolve()
                  },
                }),
              ]}
            >
              <Input autoComplete="off" />
            </Form.Item>
          </div>
        </div>
      ) : null}
    </>
  )
}
