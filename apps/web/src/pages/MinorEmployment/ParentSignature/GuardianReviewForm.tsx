import { CheckCircleFilled, DeleteOutlined, ExclamationCircleFilled, InfoCircleFilled, PlusOutlined } from '@ant-design/icons'
import { Button, Card, Checkbox, Col, ConfigProvider, Descriptions, Form, Input, InputNumber, Radio, Row, Space, Tag, Typography } from 'antd'
import type { FormInstance } from 'antd'
import type { ReactNode } from 'react'

import { WEEK_DAY_KEYS } from './guardianAmendments'
import type { AmendableSection, GuardianReviewValues } from './guardianAmendments'
import { GUARDIAN_SECTIONS, WEEK_DAY_SHORT_EN, isValidPhone } from './guardianSections'
import type { GuardianSectionKey, GuardianSectionState } from './guardianSections'

// 两列在手机上自动折成一列。
const half = { xs: 24, sm: 12 }
// 电话格式与后端一致（6~30 个允许字符、8~15 位数字）；空值交给 required 规则处理。
const PHONE_RULE = { validator: (_: unknown, value?: string) => (!value || isValidPhone(value) ? Promise.resolve() : Promise.reject(new Error('Please enter a valid phone number'))) }
const TIME_RULE = { pattern: /^([01]\d|2[0-3]):[0-5]\d$/, message: 'Use the format HH:mm' }
const EMAIL_RULE = { type: 'email' as const, message: 'Please enter a valid email address' }

const STATUS_COLORS = { complete: '#52c41a', required: '#d48806', suggested: '#1677ff' } as const

/** 分区头：状态图标 + 名称 +（已修改）标签；右侧状态文字；缺必填 / 建议补充时在卡片顶部列出具体项。 */
function SectionCard({ sectionKey, state, edited, children }: { sectionKey: GuardianSectionKey; state?: GuardianSectionState; edited: boolean; children: ReactNode }) {
  const status = state?.status ?? 'complete'
  const label = GUARDIAN_SECTIONS.find((x) => x.key === sectionKey)?.label ?? sectionKey
  const icon = status === 'complete'
    ? <CheckCircleFilled style={{ color: STATUS_COLORS.complete }} />
    : status === 'required'
      ? <ExclamationCircleFilled style={{ color: STATUS_COLORS.required }} />
      : <InfoCircleFilled style={{ color: STATUS_COLORS.suggested }} />
  // 孩子资料由门店预填：没改过且齐全时写 From store，其他齐全的分区提示家长核对。
  const statusText = status === 'required'
    ? 'Needs your input'
    : status === 'suggested'
      ? 'Please add'
      : edited ? 'Done' : sectionKey === 'child' ? 'From store' : 'Check'
  return (
    <Card
      id={`guardian-section-${sectionKey}`}
      size="small"
      title={<Space size={8}>{icon}<span>{label}</span>{edited ? <Tag color="orange">Edited</Tag> : null}</Space>}
      extra={<Typography.Text strong={status !== 'complete'} style={{ color: status === 'complete' ? undefined : STATUS_COLORS[status] }} type={status === 'complete' ? 'secondary' : undefined}>{statusText}</Typography.Text>}
    >
      {state?.missing.length ? (
        <Typography.Paragraph style={{ marginTop: 0, color: STATUS_COLORS.required }}>Required: {state.missing.join('; ')}</Typography.Paragraph>
      ) : null}
      {!state?.missing.length && state?.suggestions.length ? (
        <Typography.Paragraph type="secondary" style={{ marginTop: 0 }}>Not required, but please add if it applies: {state.suggestions.join('; ')}</Typography.Paragraph>
      ) : null}
      {children}
    </Card>
  )
}

function RangeList({ name, label, editing }: { name: (string | number)[]; label: string; editing: boolean }) {
  return (
    <Form.List name={name}>
      {(fields, { add, remove }) => (
        <div>
          <Typography.Text strong>{label}</Typography.Text>
          {fields.length === 0 ? <Typography.Paragraph type="secondary" style={{ margin: '4px 0 8px' }}>None added</Typography.Paragraph> : null}
          {fields.map((field) => (
            <Row key={field.key} gutter={8} align="bottom" wrap={false} style={{ marginTop: 8 }}>
              <Col flex="1"><Form.Item name={[field.name, 'start']} label="Start" style={{ marginBottom: 0 }}><Input type="date" /></Form.Item></Col>
              <Col flex="1">
                <Form.Item
                  name={[field.name, 'end']}
                  label="End"
                  style={{ marginBottom: 0 }}
                  dependencies={[[...name, field.name, 'start']]}
                  rules={[({ getFieldValue }) => ({
                    validator: (_, value?: string) => {
                      const start = getFieldValue([...name, field.name, 'start']) as string | undefined
                      return !start || !value || start <= value ? Promise.resolve() : Promise.reject(new Error('End date cannot be before the start date'))
                    },
                  })]}
                >
                  <Input type="date" />
                </Form.Item>
              </Col>
              {editing ? <Col><Button aria-label="Remove this row" icon={<DeleteOutlined />} onClick={() => remove(field.name)} /></Col> : null}
            </Row>
          ))}
          {editing ? <Button type="dashed" icon={<PlusOutlined />} style={{ marginTop: 8 }} onClick={() => add({ start: '', end: '' })}>Add dates</Button> : null}
        </div>
      )}
    </Form.List>
  )
}

/**
 * 家长核对 / 补全孩子的资料。只读核对时整表 disabled；可编辑时直接填写或更正，签字时一并提交。
 * 出生日期、雇主、州、监护人邮箱不在此修改（由公司 / 门店决定）。界面文字全部英文。
 */
export default function GuardianReviewForm({
  form,
  initialValues,
  editing,
  changed,
  sectionStates,
  schoolRequired,
  readonlyFacts,
}: {
  form: FormInstance<GuardianReviewValues>
  initialValues: GuardianReviewValues
  editing: boolean
  changed: AmendableSection[]
  sectionStates: GuardianSectionState[]
  /** 后端 RequiredToBeEnrolled !== false 时学校名必填。 */
  schoolRequired: boolean
  readonlyFacts: { label: string; value?: string; hint?: string }[]
}) {
  const hasOtherWork = Form.useWatch(['otherWork', 'hasOtherWork'], form)
  const stateOf = (key: GuardianSectionKey) => sectionStates.find((x) => x.key === key)
  const editedOf = (key: GuardianSectionKey) => (GUARDIAN_SECTIONS.find((x) => x.key === key)?.amendable ?? []).some((section) => changed.includes(section))
  return (
    // 核对态用 disabled 锁住输入，但禁用态默认灰字不利于逐项核对：只在本区域把禁用文字恢复成正文颜色。
    <ConfigProvider theme={{ token: { colorTextDisabled: 'rgba(0, 0, 0, 0.88)', colorBgContainerDisabled: '#fafafa' } }}>
    <Form
      form={form}
      layout="vertical"
      initialValues={initialValues}
      disabled={!editing}
      requiredMark={editing}
      onValuesChange={(changedValues: Partial<GuardianReviewValues>) => {
        // 家长页只显示一个 School name（后端必填的学校安排 SchoolProvider），同步写到就读情况的学校名，HR 两处看到的一致。
        const provider = changedValues.school?.provider
        if (provider !== undefined) form.setFieldValue(['education', 'schoolName'], provider)
      }}
    >
      <div style={{ display: 'grid', gap: 12 }}>
        <SectionCard sectionKey="child" state={stateOf('child')} edited={editedOf('child')}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['child', 'givenName']} label="First name" rules={[{ required: true, whitespace: true, message: "Please enter the child's first name" }]}><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['child', 'familyName']} label="Last name" rules={[{ required: true, whitespace: true, message: "Please enter the child's last name" }]}><Input /></Form.Item></Col>
            <Col span={24}><Form.Item name={['child', 'address']} label="Address"><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['child', 'postcode']} label="Postcode"><Input inputMode="numeric" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['child', 'phone']} label="Phone"><Input inputMode="tel" /></Form.Item></Col>
            <Col span={24}><Form.Item name={['child', 'email']} label="Email" rules={[EMAIL_RULE]}><Input inputMode="email" /></Form.Item></Col>
          </Row>
        </SectionCard>

        <SectionCard sectionKey="guardian" state={stateOf('guardian')} edited={editedOf('guardian')}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['guardian', 'name']} label="Full name (must match your signature)" rules={[{ required: true, whitespace: true, message: 'Please enter your full name' }]}><Input autoComplete="name" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['guardian', 'relationship']} label="Relationship to the child"><Input placeholder="e.g. Mother / Father / Guardian" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['guardian', 'phone']} label="Phone" rules={[{ required: true, whitespace: true, message: 'Please enter your phone number' }, PHONE_RULE]}><Input inputMode="tel" autoComplete="tel" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['guardian', 'postcode']} label="Postcode"><Input inputMode="numeric" /></Form.Item></Col>
            <Col span={24}><Form.Item name={['guardian', 'address']} label="Address"><Input /></Form.Item></Col>
          </Row>
        </SectionCard>

        {/* 「就读情况」和「学校安排」两个可修改分区合成一项显示。 */}
        <SectionCard sectionKey="school" state={stateOf('school')} edited={editedOf('school')}>
          <Row gutter={12}>
            <Col span={24}>
              <Form.Item name={['school', 'provider']} label="School name" rules={schoolRequired ? [{ required: true, whitespace: true, message: 'Please enter the school name' }] : []}>
                <Input placeholder="e.g. Example High School" />
              </Form.Item>
            </Col>
            <Col {...half}><Form.Item name={['education', 'yearLevel']} label="Year level"><Input placeholder="e.g. Year 10" /></Form.Item></Col>
            <Col {...half}>
              <Form.Item name={['education', 'completedYear10']} label="Has your child completed Year 10?">
                <Radio.Group options={[{ label: 'Yes', value: true }, { label: 'No', value: false }]} />
              </Form.Item>
            </Col>
            <Col {...half}><Form.Item name={['school', 'contactName']} label="School contact (optional)"><Input placeholder="Name" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['school', 'contactPhone']} label="School phone (optional)"><Input inputMode="tel" /></Form.Item></Col>
            <Col span={24}><Form.Item name={['school', 'contactEmail']} label="School email (optional)" rules={[EMAIL_RULE]}><Input inputMode="email" /></Form.Item></Col>
          </Row>
          <Typography.Text strong>School hours (tick the days your child must attend school)</Typography.Text>
          <Form.List name={['school', 'weekly']}>
            {(fields) => fields.map((field) => {
              const dayOfWeek = initialValues.school.weekly[field.name]?.dayOfWeek ?? 1
              const dayLabel = WEEK_DAY_SHORT_EN[dayOfWeek] ?? ''
              return (
                <Row key={field.key} gutter={8} align="middle" wrap={false} style={{ marginTop: 8 }}>
                  <Col flex="72px"><Form.Item name={[field.name, 'mustAttend']} valuePropName="checked" style={{ marginBottom: 0 }}><Checkbox>{dayLabel}</Checkbox></Form.Item></Col>
                  <Form.Item noStyle dependencies={[['school', 'weekly', field.name, 'mustAttend']]}>
                    {({ getFieldValue }) => {
                      const must = getFieldValue(['school', 'weekly', field.name, 'mustAttend']) === true
                      if (!must) return <Col flex="1"><Typography.Text type="secondary">No school</Typography.Text></Col>
                      return (
                        <>
                          <Col flex="1"><Form.Item name={[field.name, 'start']} style={{ marginBottom: 0 }} rules={[{ required: true, message: 'Start time' }, TIME_RULE]}><Input type="time" aria-label={`${dayLabel} start time`} /></Form.Item></Col>
                          <Col>–</Col>
                          <Col flex="1">
                            <Form.Item
                              name={[field.name, 'end']}
                              style={{ marginBottom: 0 }}
                              dependencies={[['school', 'weekly', field.name, 'start']]}
                              rules={[{ required: true, message: 'End time' }, TIME_RULE, ({ getFieldValue: read }) => ({
                                validator: (_, value?: string) => {
                                  const start = read(['school', 'weekly', field.name, 'start']) as string | undefined
                                  return !start || !value || start < value ? Promise.resolve() : Promise.reject(new Error('Must be after the start time'))
                                },
                              })]}
                            >
                              <Input type="time" aria-label={`${dayLabel} end time`} />
                            </Form.Item>
                          </Col>
                        </>
                      )
                    }}
                  </Form.Item>
                </Row>
              )
            })}
          </Form.List>
          <div style={{ display: 'grid', gap: 16, marginTop: 16 }}>
            <RangeList name={['school', 'holidays']} label="School holidays" editing={editing} />
            <RangeList name={['school', 'pupilFreeDays']} label="Pupil-free days" editing={editing} />
          </div>
        </SectionCard>

        <SectionCard sectionKey="otherWork" state={stateOf('otherWork')} edited={editedOf('otherWork')}>
          <Form.Item name={['otherWork', 'hasOtherWork']} label="Does your child work anywhere else?">
            <Radio.Group options={[{ label: 'Yes', value: true }, { label: 'No', value: false }]} />
          </Form.Item>
          {hasOtherWork ? (
            <Form.List name={['otherWork', 'employers']}>
              {(fields, { add, remove }) => (
                <div style={{ display: 'grid', gap: 12 }}>
                  {fields.map((field, index) => (
                    <Card key={field.key} size="small" type="inner" title={`Other employer ${index + 1}`} extra={editing ? <Button type="text" danger icon={<DeleteOutlined />} onClick={() => remove(field.name)}>Remove</Button> : null}>
                      <Row gutter={12}>
                        <Col {...half}><Form.Item name={[field.name, 'companyName']} label="Company name"><Input /></Form.Item></Col>
                        <Col {...half}><Form.Item name={[field.name, 'tradingName']} label="Trading name"><Input /></Form.Item></Col>
                        <Col {...half}><Form.Item name={[field.name, 'phone']} label="Phone"><Input inputMode="tel" /></Form.Item></Col>
                        <Col {...half}><Form.Item name={[field.name, 'email']} label="Email" rules={[EMAIL_RULE]}><Input inputMode="email" /></Form.Item></Col>
                      </Row>
                      <Typography.Text strong>Approximate hours worked each day</Typography.Text>
                      <Row gutter={[8, 8]} style={{ marginTop: 8 }}>
                        {WEEK_DAY_KEYS.map((key, dayIndex) => (
                          <Col key={key} xs={6} sm={3}>
                            <Form.Item name={[field.name, 'hours', key]} label={WEEK_DAY_SHORT_EN[dayIndex]} style={{ marginBottom: 0 }}>
                              <InputNumber min={0} max={24} step={0.5} style={{ width: '100%' }} inputMode="decimal" />
                            </Form.Item>
                          </Col>
                        ))}
                      </Row>
                    </Card>
                  ))}
                  {editing && fields.length < 3 ? <Button type="dashed" icon={<PlusOutlined />} onClick={() => add({ hours: {} })}>Add another employer</Button> : null}
                </div>
              )}
            </Form.List>
          ) : null}
        </SectionCard>

        <SectionCard sectionKey="commute" state={stateOf('commute')} edited={editedOf('commute')}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['commute', 'afterSchoolToStoreMinutes']} label="School to store (minutes)"><InputNumber min={0} max={300} style={{ width: '100%' }} inputMode="numeric" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'homewardMinutes']} label="Store to home (minutes)"><InputNumber min={0} max={300} style={{ width: '100%' }} inputMode="numeric" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'transportMode']} label="How they travel"><Input placeholder="e.g. Parent pick-up / Bus / Walk" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'pickupPerson']} label="Who picks them up"><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'latestWorkEndLocalTime']} label="Latest finish time at work" rules={[TIME_RULE]}><Input type="time" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'latestTransportLocalTime']} label="Latest time transport is available" rules={[TIME_RULE]}><Input type="time" /></Form.Item></Col>
          </Row>
        </SectionCard>

        <SectionCard sectionKey="backupContact" state={stateOf('backupContact')} edited={editedOf('backupContact')}>
          <Typography.Paragraph type="secondary" style={{ marginTop: 0 }}>Another adult we can call if we can&apos;t reach you. Their phone, mobile and email must be different from yours.</Typography.Paragraph>
          <Form.List name="contacts">
            {(fields, { add, remove }) => (
              <div style={{ display: 'grid', gap: 12 }}>
                {fields.map((field, index) => (
                  <Card key={field.key} size="small" type="inner" title={index === 0 ? 'Backup contact' : `Additional contact ${index}`} extra={editing && fields.length > 1 ? <Button type="text" danger icon={<DeleteOutlined />} onClick={() => remove(field.name)}>Remove</Button> : null}>
                    <Row gutter={12}>
                      <Col {...half}><Form.Item name={[field.name, 'fullName']} label="Full name" rules={[{ required: true, whitespace: true, message: 'Please enter a name' }]}><Input /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'relationship']} label="Relationship to the child"><Input placeholder="e.g. Aunt / Neighbour" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'phone']} label="Phone" rules={[{ required: true, whitespace: true, message: 'Please enter a phone number' }, PHONE_RULE]}><Input inputMode="tel" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'mobile']} label="Mobile (optional)"><Input inputMode="tel" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'email']} label="Email (optional)" rules={[EMAIL_RULE]}><Input inputMode="email" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'postcode']} label="Postcode"><Input inputMode="numeric" /></Form.Item></Col>
                      <Col span={24}><Form.Item name={[field.name, 'address']} label="Address"><Input /></Form.Item></Col>
                    </Row>
                  </Card>
                ))}
                {editing && fields.length < 2 ? <Button type="dashed" icon={<PlusOutlined />} onClick={() => add({})}>{fields.length === 0 ? 'Add a backup contact' : 'Add another contact'}</Button> : null}
              </div>
            )}
          </Form.List>
        </SectionCard>

        <Card size="small" title="Set by Hot Bargain">
          <Descriptions size="small" column={1} items={readonlyFacts.map((fact) => ({
            key: fact.label,
            label: fact.label,
            children: <span>{fact.value || 'Not provided'}{fact.hint ? <Typography.Text type="secondary" style={{ display: 'block', fontSize: 12 }}>{fact.hint}</Typography.Text> : null}</span>,
          }))} />
          <Typography.Paragraph type="secondary" style={{ margin: '8px 0 0', fontSize: 12 }}>
            Date of birth and employer are set by Hot Bargain and can&apos;t be changed here. If they&apos;re wrong, contact the store manager.
          </Typography.Paragraph>
        </Card>
      </div>
    </Form>
    </ConfigProvider>
  )
}
