import { Card, Descriptions, Typography } from 'antd'
import { MeasuredTable } from '../../components/MeasuredTable'

type RecordValue = Record<string, unknown>
const record = (value: unknown): RecordValue => value && typeof value === 'object' && !Array.isArray(value) ? value as RecordValue : {}
const get = (value: RecordValue, key: string) => value[key] ?? value[key[0].toUpperCase() + key.slice(1)]
const text = (value: unknown) => value == null || value === '' ? '未填写' : typeof value === 'boolean' ? value ? '是' : '否' : String(value)
const array = (value: unknown): RecordValue[] => Array.isArray(value) ? value.map(record) : []
const days = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']
const dayKeys = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
function dayLabel(value: unknown) { const index = typeof value === 'number' ? value : dayKeys.indexOf(String(value)); return days[index] ?? text(value) }
function Fields({ source, fields }: { source: RecordValue; fields: Array<[string, string]> }) {
  return <Descriptions bordered size="small" column={{ xs: 1, sm: 2 }} items={fields.map(([key, label]) => ({ key, label, children: text(get(source, key)) }))} />
}
function Ranges({ title, value }: { title: string; value: unknown }) {
  const ranges = array(value)
  return <><Typography.Title level={5}>{title}</Typography.Title>{ranges.length ? ranges.map((range, index) => <Typography.Paragraph key={index}>{text(get(range, 'startDate')).slice(0, 10)} — {text(get(range, 'endDate')).slice(0, 10)} {get(range, 'label') ? ` · ${text(get(range, 'label'))}` : ''}</Typography.Paragraph>) : <Typography.Paragraph type="secondary">未填写日期</Typography.Paragraph>}</>
}
function Intervals({ title, value }: { title: string; value: unknown }) {
  return <><Typography.Title level={5}>{title}</Typography.Title><MeasuredTable metricId="minor-employment.record-rows" size="small" pagination={false} scroll={{ x: true }} dataSource={array(value).map((row, key) => ({ ...row, key }))} columns={[
    { title: '开始（UTC）', render: (_, row) => text(get(row, 'startUtc')) },
    { title: '结束（UTC）', render: (_, row) => text(get(row, 'endUtc')) },
    { title: '自报小时', render: (_, row) => text(get(row, 'hours')) },
    { title: '来源 / 雇主', render: (_, row) => text(get(row, 'source')) },
  ]} locale={{ emptyText: '未填写具体时段' }} /></>
}

/** 家长签署和 HR 使用同一只读快照，完整呈现所有签入 PDF 的结构化字段。 */
export default function RecordDetails({ snapshot }: { snapshot: RecordValue }) {
  const form = record(get(snapshot, 'formData')), school = record(get(snapshot, 'schoolCalendar'))
  const work = record(get(snapshot, 'otherWork')), commute = record(get(snapshot, 'commute'))
  const contacts = array(get(snapshot, 'contacts'))
  return <div style={{ display: 'grid', gap: 16, marginTop: 16 }}>
    <Card size="small" title="员工与教育状态">
      <Fields source={form} fields={[['childGivenName', '员工名字'], ['childFamilyName', '员工姓氏'], ['childAddress', '员工地址'], ['childPostcode', '邮编'], ['childPhone', '员工电话'], ['childEmail', '员工邮箱']]} />
      <Fields source={snapshot} fields={[['dateOfBirth', '出生日期'], ['schoolName', '学校'], ['yearLevel', '年级'], ['completedYear10', '已完成 Year 10'], ['educationStatus', '当前教育 / 培训状态'], ['requiredToBeEnrolled', '仍须入学'], ['educationExemptionVerified', '教育豁免已核验'], ['participationEndDate', '教育参与结束日期']]} />
    </Card>
    <Card size="small" title="家长与紧急联系人">
      <Fields source={snapshot} fields={[['guardianName', '家长 / 监护人'], ['guardianRelationship', '与员工关系'], ['guardianPhone', '家长电话（必填）'], ['guardianEmail', '家长邮箱（必填）']]} />
      <Fields source={form} fields={[['guardianAddress', '家长地址'], ['guardianPostcode', '邮编']]} />
      {contacts.map((contact, index) => <div key={index}><Typography.Title level={5}>{index === 0 ? '指定备用联系人' : '额外联系人'}</Typography.Title><Fields source={contact} fields={[['fullName', '姓名'], ['relationship', '关系'], ['phone', '电话'], ['mobile', '手机'], ['email', '邮箱'], ['address', '地址'], ['postcode', '邮编']]} /></div>)}
      {!contacts.length ? <Typography.Text type="warning">尚未填写独立备用联系人</Typography.Text> : null}
    </Card>
    <Card size="small" title="学校时间与个人日历">
      <Fields source={school} fields={[['schoolProvider', '学校 / 教育机构'], ['timeZoneId', '时区'], ['schoolContactName', '学校联系人'], ['schoolContactPosition', '职务'], ['schoolContactPhone', '电话'], ['schoolContactMobile', '手机'], ['schoolContactEmail', '邮箱']]} />
      <Typography.Title level={5}>每周必须出席时间</Typography.Title>
      <MeasuredTable metricId="minor-employment.school-weekly" size="small" pagination={false} dataSource={array(get(school, 'weeklySchedule')).map((row, key) => ({ ...row, key }))} columns={[
        { title: '星期', render: (_, row) => dayLabel(get(row, 'dayOfWeek')) }, { title: '须出席', render: (_, row) => text(get(row, 'mustAttend')) },
        { title: '开始', render: (_, row) => text(get(row, 'startLocalTime')) }, { title: '结束', render: (_, row) => text(get(row, 'endLocalTime')) },
      ]} />
      <Ranges title="学期" value={get(school, 'termRanges')} /><Ranges title="学校假期" value={get(school, 'holidays')} /><Ranges title="学生免到校日（Pupil-free days）" value={get(school, 'pupilFreeDays')} />
      <Fields source={form} fields={[['flexibleSchoolingQualifiedTeacher', '弹性教育由合资格教师安排']]} />
    </Card>
    <Card size="small" title="本次同意的雇主">
      <Fields source={form} fields={[['employerCompanyName', '公司名称'], ['employerTradingName', '营业名称'], ['employerAddress', '地址'], ['employerPostcode', '邮编'], ['employerPhone', '电话'], ['employerMobile', '手机'], ['employerEmail', '邮箱']]} />
    </Card>
    <Card size="small" title="其他工作（员工自报）">
      <Fields source={work} fields={[['hasOtherWork', '是否有其他工作'], ['hoursUnknown', '其他工作时数未知']]} />
      {array(get(work, 'employers')).map((employer, index) => <div key={index}>
        <Typography.Title level={5}>其他雇主 {index + 1}</Typography.Title><Fields source={employer} fields={[['companyName', '公司名称'], ['tradingName', '营业名称'], ['address', '地址'], ['postcode', '邮编'], ['phone', '电话'], ['email', '邮箱']]} />
        <Typography.Paragraph style={{ marginTop: 12 }}>{dayKeys.map((key, i) => `${days[i]} ${text(get(record(get(employer, 'weeklyHours')), key) ?? get(record(get(employer, 'weeklyHours')), String(i)) ?? 0)}h`).join(' · ')}</Typography.Paragraph>
      </div>)}
      <Intervals title="计划工作时段" value={get(work, 'plannedIntervals')} /><Intervals title="实际工作时段" value={get(work, 'actualIntervals')} />
    </Card>
    <Card size="small" title="通勤与安全回家安排">
      <Fields source={commute} fields={[['afterSchoolToStoreMinutes', '放学到门店（分钟）'], ['homewardMinutes', '回家路程（分钟）'], ['transportMode', '交通方式'], ['pickupPerson', '接送人'], ['latestTransportLocalTime', '末班交通时间'], ['latestWorkEndLocalTime', '最晚工作结束时间']]} />
    </Card>
  </div>
}
