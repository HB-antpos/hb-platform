import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, Card, Checkbox, Col, ConfigProvider, Descriptions, Form, Input, InputNumber, Radio, Row, Space, Tag, Typography } from 'antd'
import type { FormInstance } from 'antd'
import type { ReactNode } from 'react'

import { AMENDABLE_SECTIONS, WEEK_DAY_KEYS, WEEK_DAY_LABELS } from './guardianAmendments'
import type { AmendableSection, GuardianReviewValues } from './guardianAmendments'

// 两列在手机上自动折成一列。
const half = { xs: 24, sm: 12 }
const PHONE_RULE = { pattern: /^[0-9+() .-]{8,30}$/, message: '请输入有效电话' }
const TIME_RULE = { pattern: /^([01]\d|2[0-3]):[0-5]\d$/, message: '时间格式 HH:mm' }

function SectionCard({ section, changed, extra, children }: { section: AmendableSection; changed: AmendableSection[]; extra?: ReactNode; children: ReactNode }) {
  return (
    <Card
      size="small"
      title={<Space size={8}>{AMENDABLE_SECTIONS[section]}{changed.includes(section) ? <Tag color="orange">已修改</Tag> : null}</Space>}
      extra={extra}
    >
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
          {fields.length === 0 ? <Typography.Paragraph type="secondary" style={{ margin: '4px 0 8px' }}>未填写</Typography.Paragraph> : null}
          {fields.map((field) => (
            <Row key={field.key} gutter={8} align="bottom" wrap={false} style={{ marginTop: 8 }}>
              <Col flex="1"><Form.Item name={[field.name, 'start']} label="开始" style={{ marginBottom: 0 }}><Input type="date" /></Form.Item></Col>
              <Col flex="1">
                <Form.Item
                  name={[field.name, 'end']}
                  label="结束"
                  style={{ marginBottom: 0 }}
                  dependencies={[[...name, field.name, 'start']]}
                  rules={[({ getFieldValue }) => ({
                    validator: (_, value?: string) => {
                      const start = getFieldValue([...name, field.name, 'start']) as string | undefined
                      return !start || !value || start <= value ? Promise.resolve() : Promise.reject(new Error('结束不能早于开始'))
                    },
                  })]}
                >
                  <Input type="date" />
                </Form.Item>
              </Col>
              {editing ? <Col><Button aria-label="删除这一行" icon={<DeleteOutlined />} onClick={() => remove(field.name)} /></Col> : null}
            </Row>
          ))}
          {editing ? <Button type="dashed" icon={<PlusOutlined />} style={{ marginTop: 8 }} onClick={() => add({ start: '', end: '' })}>添加</Button> : null}
        </div>
      )}
    </Form.List>
  )
}

/**
 * 监护人核对 / 修改孩子填写的资料。默认整表只读（disabled）用于核对，点"修改资料"后可直接更正，签字时一并提交。
 * 监护人邮箱、出生日期、雇主信息不在此修改。
 */
export default function GuardianReviewForm({
  form,
  initialValues,
  editing,
  changed,
  readonlyFacts,
  onValuesChange,
}: {
  form: FormInstance<GuardianReviewValues>
  initialValues: GuardianReviewValues
  editing: boolean
  changed: AmendableSection[]
  readonlyFacts: { label: string; value?: string; hint: string }[]
  onValuesChange: () => void
}) {
  const hasOtherWork = Form.useWatch(['otherWork', 'hasOtherWork'], form)
  return (
    // 核对态用 disabled 锁住输入，但禁用态默认灰字不利于逐项核对：只在本区域把禁用文字恢复成正文颜色。
    <ConfigProvider theme={{ token: { colorTextDisabled: 'rgba(0, 0, 0, 0.88)', colorBgContainerDisabled: '#fafafa' } }}>
    <Form form={form} layout="vertical" initialValues={initialValues} disabled={!editing} onValuesChange={onValuesChange} requiredMark={editing}>
      <div style={{ display: 'grid', gap: 12 }}>
        <SectionCard section="childDetails" changed={changed}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['child', 'givenName']} label="名字" rules={[{ required: true, whitespace: true, message: '请填写名字' }]}><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['child', 'familyName']} label="姓氏" rules={[{ required: true, whitespace: true, message: '请填写姓氏' }]}><Input /></Form.Item></Col>
            <Col span={24}><Form.Item name={['child', 'address']} label="地址"><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['child', 'postcode']} label="邮编"><Input inputMode="numeric" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['child', 'phone']} label="电话"><Input inputMode="tel" /></Form.Item></Col>
            <Col span={24}><Form.Item name={['child', 'email']} label="邮箱" rules={[{ type: 'email', message: '邮箱格式不正确' }]}><Input inputMode="email" /></Form.Item></Col>
          </Row>
        </SectionCard>

        <SectionCard section="guardianDetails" changed={changed}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['guardian', 'name']} label="姓名（签名须与此一致）" rules={[{ required: true, whitespace: true, message: '请填写姓名' }]}><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['guardian', 'relationship']} label="与孩子关系"><Input placeholder="如 母亲 / 父亲 / 监护人" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['guardian', 'phone']} label="电话" rules={[{ required: true, message: '请填写电话' }, PHONE_RULE]}><Input inputMode="tel" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['guardian', 'postcode']} label="邮编"><Input inputMode="numeric" /></Form.Item></Col>
            <Col span={24}><Form.Item name={['guardian', 'address']} label="地址"><Input /></Form.Item></Col>
          </Row>
        </SectionCard>

        <SectionCard section="education" changed={changed}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['education', 'schoolName']} label="学校"><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['education', 'yearLevel']} label="年级"><Input placeholder="如 Year 9" /></Form.Item></Col>
            <Col span={24}>
              <Form.Item name={['education', 'completedYear10']} label="是否已完成 Year 10">
                <Radio.Group options={[{ label: '是', value: true }, { label: '否', value: false }]} />
              </Form.Item>
            </Col>
          </Row>
        </SectionCard>

        <SectionCard section="schoolCalendar" changed={changed}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['school', 'provider']} label="学校 / 教育机构"><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['school', 'contactName']} label="学校联系人"><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['school', 'contactPhone']} label="学校电话"><Input inputMode="tel" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['school', 'contactEmail']} label="学校邮箱" rules={[{ type: 'email', message: '邮箱格式不正确' }]}><Input inputMode="email" /></Form.Item></Col>
          </Row>
          <Typography.Text strong>每周需上学的时间</Typography.Text>
          <Form.List name={['school', 'weekly']}>
            {(fields) => fields.map((field) => {
              const dayOfWeek = initialValues.school.weekly[field.name]?.dayOfWeek ?? 1
              return (
                <Row key={field.key} gutter={8} align="middle" wrap={false} style={{ marginTop: 8 }}>
                  <Col flex="72px"><Form.Item name={[field.name, 'mustAttend']} valuePropName="checked" style={{ marginBottom: 0 }}><Checkbox>{WEEK_DAY_LABELS[dayOfWeek]}</Checkbox></Form.Item></Col>
                  <Form.Item noStyle dependencies={[['school', 'weekly', field.name, 'mustAttend']]}>
                    {({ getFieldValue }) => {
                      const must = getFieldValue(['school', 'weekly', field.name, 'mustAttend']) === true
                      if (!must) return <Col flex="1"><Typography.Text type="secondary">不用上学</Typography.Text></Col>
                      return (
                        <>
                          <Col flex="1"><Form.Item name={[field.name, 'start']} style={{ marginBottom: 0 }} rules={[{ required: true, message: '开始时间' }, TIME_RULE]}><Input type="time" aria-label={`${WEEK_DAY_LABELS[dayOfWeek]}开始`} /></Form.Item></Col>
                          <Col>—</Col>
                          <Col flex="1">
                            <Form.Item
                              name={[field.name, 'end']}
                              style={{ marginBottom: 0 }}
                              dependencies={[['school', 'weekly', field.name, 'start']]}
                              rules={[{ required: true, message: '结束时间' }, TIME_RULE, ({ getFieldValue: read }) => ({
                                validator: (_, value?: string) => {
                                  const start = read(['school', 'weekly', field.name, 'start']) as string | undefined
                                  return !start || !value || start < value ? Promise.resolve() : Promise.reject(new Error('须晚于开始'))
                                },
                              })]}
                            >
                              <Input type="time" aria-label={`${WEEK_DAY_LABELS[dayOfWeek]}结束`} />
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
            <RangeList name={['school', 'holidays']} label="学校假期" editing={editing} />
            <RangeList name={['school', 'pupilFreeDays']} label="学生免上学日（pupil-free days）" editing={editing} />
          </div>
        </SectionCard>

        <SectionCard section="otherWork" changed={changed}>
          <Form.Item name={['otherWork', 'hasOtherWork']} label="孩子是否还在其他地方工作">
            <Radio.Group options={[{ label: '有', value: true }, { label: '没有', value: false }]} />
          </Form.Item>
          {hasOtherWork ? (
            <Form.List name={['otherWork', 'employers']}>
              {(fields, { add, remove }) => (
                <div style={{ display: 'grid', gap: 12 }}>
                  {fields.map((field, index) => (
                    <Card key={field.key} size="small" type="inner" title={`其他雇主 ${index + 1}`} extra={editing ? <Button type="text" danger icon={<DeleteOutlined />} onClick={() => remove(field.name)}>删除</Button> : null}>
                      <Row gutter={12}>
                        <Col {...half}><Form.Item name={[field.name, 'companyName']} label="公司名称"><Input /></Form.Item></Col>
                        <Col {...half}><Form.Item name={[field.name, 'tradingName']} label="店名"><Input /></Form.Item></Col>
                        <Col {...half}><Form.Item name={[field.name, 'phone']} label="电话"><Input inputMode="tel" /></Form.Item></Col>
                        <Col {...half}><Form.Item name={[field.name, 'email']} label="邮箱" rules={[{ type: 'email', message: '邮箱格式不正确' }]}><Input inputMode="email" /></Form.Item></Col>
                      </Row>
                      <Typography.Text strong>每天大约工作小时</Typography.Text>
                      <Row gutter={[8, 8]} style={{ marginTop: 8 }}>
                        {WEEK_DAY_KEYS.map((key, dayIndex) => (
                          <Col key={key} xs={6} sm={3}>
                            <Form.Item name={[field.name, 'hours', key]} label={WEEK_DAY_LABELS[dayIndex]} style={{ marginBottom: 0 }}>
                              <InputNumber min={0} max={24} step={0.5} style={{ width: '100%' }} inputMode="decimal" />
                            </Form.Item>
                          </Col>
                        ))}
                      </Row>
                    </Card>
                  ))}
                  {editing && fields.length < 3 ? <Button type="dashed" icon={<PlusOutlined />} onClick={() => add({ hours: {} })}>添加其他雇主</Button> : null}
                </div>
              )}
            </Form.List>
          ) : null}
        </SectionCard>

        <SectionCard section="commute" changed={changed}>
          <Row gutter={12}>
            <Col {...half}><Form.Item name={['commute', 'afterSchoolToStoreMinutes']} label="放学到店（分钟）"><InputNumber min={0} max={300} style={{ width: '100%' }} inputMode="numeric" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'homewardMinutes']} label="下班回家（分钟）"><InputNumber min={0} max={300} style={{ width: '100%' }} inputMode="numeric" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'transportMode']} label="交通方式"><Input placeholder="如 家长接送 / 公交 / 步行" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'pickupPerson']} label="接送人"><Input /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'latestWorkEndLocalTime']} label="最晚可下班时间" rules={[TIME_RULE]}><Input type="time" /></Form.Item></Col>
            <Col {...half}><Form.Item name={['commute', 'latestTransportLocalTime']} label="最晚交通时间" rules={[TIME_RULE]}><Input type="time" /></Form.Item></Col>
          </Row>
        </SectionCard>

        <SectionCard section="backupContact" changed={changed}>
          <Typography.Paragraph type="secondary" style={{ marginTop: 0 }}>联系不上监护人时联系的另一位成年人，不能和监护人用同一个电话。</Typography.Paragraph>
          <Form.List name="contacts">
            {(fields, { add, remove }) => (
              <div style={{ display: 'grid', gap: 12 }}>
                {fields.map((field, index) => (
                  <Card key={field.key} size="small" type="inner" title={index === 0 ? '备用联系人' : `其他联系人 ${index}`} extra={editing && fields.length > 1 ? <Button type="text" danger icon={<DeleteOutlined />} onClick={() => remove(field.name)}>删除</Button> : null}>
                    <Row gutter={12}>
                      <Col {...half}><Form.Item name={[field.name, 'fullName']} label="姓名" rules={[{ required: true, whitespace: true, message: '请填写姓名' }]}><Input /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'relationship']} label="关系"><Input placeholder="如 姑姑 / 邻居" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'phone']} label="电话" rules={[{ required: true, message: '请填写电话' }, PHONE_RULE]}><Input inputMode="tel" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'mobile']} label="手机"><Input inputMode="tel" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'email']} label="邮箱" rules={[{ type: 'email', message: '邮箱格式不正确' }]}><Input inputMode="email" /></Form.Item></Col>
                      <Col {...half}><Form.Item name={[field.name, 'postcode']} label="邮编"><Input inputMode="numeric" /></Form.Item></Col>
                      <Col span={24}><Form.Item name={[field.name, 'address']} label="地址"><Input /></Form.Item></Col>
                    </Row>
                  </Card>
                ))}
                {editing && fields.length < 2 ? <Button type="dashed" icon={<PlusOutlined />} onClick={() => add({})}>{fields.length === 0 ? '添加备用联系人' : '再添加一位联系人'}</Button> : null}
              </div>
            )}
          </Form.List>
        </SectionCard>

        <Card size="small" title="以下资料不能在此修改">
          <Descriptions size="small" column={1} items={readonlyFacts.map((fact) => ({
            key: fact.label,
            label: fact.label,
            children: <span>{fact.value || '未填写'}<Typography.Text type="secondary" style={{ display: 'block', fontSize: 12 }}>{fact.hint}</Typography.Text></span>,
          }))} />
        </Card>
      </div>
    </Form>
    </ConfigProvider>
  )
}
