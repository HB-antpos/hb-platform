import { Col, DatePicker, Divider, Form, InputNumber, Row, Segmented, Switch, Typography } from 'antd'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'

// 考勤设置新增参数（规格第 1 节 AttendanceSettings 扩展）。放在排班考勤页的设置表单里渲染，
// 由父组件在后端返回这些字段时才挂载（旧后端不返回就整组隐藏，保存也不会带上）。

/** 表单里存 YYYY-MM-DD 字符串，DatePicker 显示时转 Dayjs。 */
const dateValueProps = (value?: string) => ({ value: value ? dayjs(value) : undefined })
const normalizeDate = (value?: Dayjs | null) => (value ? value.format('YYYY-MM-DD') : undefined)

export default function AttendancePayrollSettingsFields() {
  const { t } = useTranslation()
  const number = (name: string, label: string, min: number, max: number, suffix?: string) => (
    <Col xs={24} sm={12}>
      <Form.Item name={name} label={t(label)} rules={[{ required: true }]}>
        <InputNumber min={min} max={max} precision={0} style={{ width: '100%' }} addonAfter={suffix ? t(suffix) : undefined} />
      </Form.Item>
    </Col>
  )
  return (
    <>
      <Divider orientation="left" plain>{t('posAdmin.scheduleAttendance.payrollSettings.title')}</Divider>
      <Typography.Paragraph type="secondary">{t('posAdmin.scheduleAttendance.payrollSettings.hint')}</Typography.Paragraph>

      <Typography.Text strong>{t('posAdmin.scheduleAttendance.payrollSettings.period')}</Typography.Text>
      <Row gutter={16}>
        <Col xs={24} sm={12}>
          <Form.Item name="defaultPeriodWeeks" label={t('posAdmin.scheduleAttendance.payrollSettings.defaultPeriodWeeks')} rules={[{ required: true }]}>
            <Segmented options={[{ value: 1, label: t('posAdmin.scheduleAttendance.payrollSettings.oneWeek') }, { value: 2, label: t('posAdmin.scheduleAttendance.payrollSettings.twoWeeks') }]} />
          </Form.Item>
        </Col>
        <Col xs={24} sm={12}>
          <Form.Item
            name="fortnightAnchorDate"
            label={t('posAdmin.scheduleAttendance.payrollSettings.fortnightAnchorDate')}
            tooltip={t('posAdmin.scheduleAttendance.payrollSettings.fortnightAnchorDateHint')}
            getValueProps={dateValueProps}
            normalize={normalizeDate}
            rules={[{ required: true }]}
          >
            <DatePicker allowClear={false} style={{ width: '100%' }} disabledDate={(date) => date.day() !== 1} />
          </Form.Item>
        </Col>
      </Row>

      <Typography.Text strong>{t('posAdmin.scheduleAttendance.payrollSettings.publicHoliday')}</Typography.Text>
      <Row gutter={16}>
        {number('publicHolidayLookbackWeeks', 'posAdmin.scheduleAttendance.payrollSettings.publicHolidayLookbackWeeks', 1, 52, 'posAdmin.scheduleAttendance.payrollSettings.weeksUnit')}
        {number('publicHolidayMinCount', 'posAdmin.scheduleAttendance.payrollSettings.publicHolidayMinCount', 1, 52, 'posAdmin.scheduleAttendance.payrollSettings.timesUnit')}
      </Row>

      <Typography.Text strong>{t('posAdmin.scheduleAttendance.payrollSettings.paidLeave')}</Typography.Text>
      <Row gutter={16}>
        {number('sickLeaveSubmitDays', 'posAdmin.scheduleAttendance.payrollSettings.sickLeaveSubmitDays', 1, 30, 'posAdmin.scheduleAttendance.payrollSettings.daysUnit')}
        {number('annualLeaveLeadWeeks', 'posAdmin.scheduleAttendance.payrollSettings.annualLeaveLeadWeeks', 0, 26, 'posAdmin.scheduleAttendance.payrollSettings.weeksUnit')}
        {number('annualLeaveMaxWeeks', 'posAdmin.scheduleAttendance.payrollSettings.annualLeaveMaxWeeks', 1, 12, 'posAdmin.scheduleAttendance.payrollSettings.weeksUnit')}
        {number('annualCashOutLeadDays', 'posAdmin.scheduleAttendance.payrollSettings.annualCashOutLeadDays', 0, 60, 'posAdmin.scheduleAttendance.payrollSettings.daysUnit')}
        {number('annualCashOutMaxWeeks', 'posAdmin.scheduleAttendance.payrollSettings.annualCashOutMaxWeeks', 1, 12, 'posAdmin.scheduleAttendance.payrollSettings.weeksUnit')}
        {number('birthdayRemindDays', 'posAdmin.scheduleAttendance.payrollSettings.birthdayRemindDays', 0, 30, 'posAdmin.scheduleAttendance.payrollSettings.daysUnit')}
      </Row>

      <Typography.Text strong>{t('posAdmin.scheduleAttendance.payrollSettings.punch')}</Typography.Text>
      <Row gutter={16}>
        {number('employeeSegmentLimit', 'posAdmin.scheduleAttendance.payrollSettings.employeeSegmentLimit', 1, 10, 'posAdmin.scheduleAttendance.payrollSettings.segmentsUnit')}
        {number('managerSegmentLimit', 'posAdmin.scheduleAttendance.payrollSettings.managerSegmentLimit', 1, 10, 'posAdmin.scheduleAttendance.payrollSettings.segmentsUnit')}
        <Col xs={24}>
          <Form.Item name="autoClockOutAtMidnight" label={t('posAdmin.scheduleAttendance.payrollSettings.autoClockOutAtMidnight')} tooltip={t('posAdmin.scheduleAttendance.payrollSettings.autoClockOutAtMidnightHint')} valuePropName="checked">
            <Switch />
          </Form.Item>
        </Col>
      </Row>
    </>
  )
}
