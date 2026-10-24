import { DatePicker, Form, Input, InputNumber, Radio, Select, type FormInstance } from 'antd'
import dayjs, { type Dayjs } from 'dayjs'
import { useEffect, useRef, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import type { FormHeaderValues } from './formHeaderValues'
import {
  CONTAINER_PRESET_CBM,
  HOLIDAY_TYPES,
  buildDefaultFormTitle,
  getDefaultHistoryRange,
  type ContainerPreset,
} from './supplierPreorderAdmin.logic'

interface FormHeaderFieldsProps {
  form: FormInstance<FormHeaderValues>
  /** 用于默认标题「供应商名 · 节日 年份」 */
  supplierName?: string | null
  /** 编辑抽屉里显示「填回原表时数量单位」；导入向导在第 2 步单独设置 */
  showExportQtyUnit?: boolean
  /** 排在最前面的字段（导入时的供应商下拉） */
  leading?: ReactNode
  /** 抽屉里用单列/双列布局 */
  compact?: boolean
}

function formatRangeKey(range?: [Dayjs, Dayjs] | null) {
  return range?.[0] && range?.[1] ? `${range[0].format('YYYY-MM-DD')}~${range[1].format('YYYY-MM-DD')}` : ''
}

/** 订单表信息字段：导入第 3 步与详情页「编辑订单表信息」抽屉共用。 */
export default function FormHeaderFields({ form, supplierName, showExportQtyUnit = false, leading, compact = false }: FormHeaderFieldsProps) {
  const { t } = useTranslation()
  const holidayType = Form.useWatch('holidayType', form)
  const seasonYear = Form.useWatch('seasonYear', form)
  const containerPreset = Form.useWatch('containerPreset', form)
  const previousDefaultTitleRef = useRef<string | null>(null)
  const previousDefaultHistoryRef = useRef<string | null>(null)

  // 关键逻辑：标题和历史对比季在「用户没手改过」时跟着供应商 / 节日 / 年份自动更新。
  // 判断依据是当前值仍等于上一次自动生成的默认值（或为空）；用户改过就不再覆盖。
  useEffect(() => {
    if (!holidayType) return
    const nextTitle = buildDefaultFormTitle(supplierName, holidayType, seasonYear)
    const currentTitle: string = form.getFieldValue('title') ?? ''
    const previousTitle = previousDefaultTitleRef.current
    if (!currentTitle.trim() || (previousTitle !== null && currentTitle === previousTitle)) {
      form.setFieldValue('title', nextTitle)
    }
    previousDefaultTitleRef.current = nextTitle

    const nextHistory = getDefaultHistoryRange(holidayType, seasonYear)
    const nextHistoryRange: [Dayjs, Dayjs] | undefined = nextHistory ? [dayjs(nextHistory.from), dayjs(nextHistory.to)] : undefined
    const currentHistoryKey = formatRangeKey(form.getFieldValue('historyRange'))
    const previousHistoryKey = previousDefaultHistoryRef.current
    if (!currentHistoryKey || (previousHistoryKey !== null && currentHistoryKey === previousHistoryKey)) {
      form.setFieldValue('historyRange', nextHistoryRange)
    }
    previousDefaultHistoryRef.current = formatRangeKey(nextHistoryRange)
  }, [form, holidayType, seasonYear, supplierName])

  const handleContainerPresetChange = (preset: ContainerPreset) => {
    if (preset === '40GP' || preset === '20GP') {
      form.setFieldsValue({ containerType: preset, containerCapacityCbm: CONTAINER_PRESET_CBM[preset] })
    } else if (preset === 'none') {
      form.setFieldsValue({ containerType: undefined, containerCapacityCbm: null })
    } else if (form.getFieldValue('containerType') === '40GP' || form.getFieldValue('containerType') === '20GP') {
      // 从预设切到自定义：清掉预设柜型名，容量保留方便微调
      form.setFieldValue('containerType', undefined)
    }
  }

  const containerOptions = [
    { value: 'none', label: t('spAdmin.fields.containerNone') },
    { value: '40GP', label: t('spAdmin.fields.container40') },
    { value: '20GP', label: t('spAdmin.fields.container20') },
    { value: 'custom', label: t('spAdmin.fields.containerCustom') },
  ]

  return (
    <div className={`sp-admin-form-grid${compact ? ' is-compact' : ''}`}>
      {leading}
      <Form.Item name="holidayType" label={t('spAdmin.fields.holiday')} rules={[{ required: true, message: t('spAdmin.fields.holidayRequired') }]}>
        <Select options={HOLIDAY_TYPES.map((value) => ({ value, label: t(`spAdmin.common.holiday.${value}`) }))} />
      </Form.Item>
      <Form.Item name="seasonYear" label={t('spAdmin.fields.year')} rules={[{ required: true, message: t('spAdmin.fields.yearRequired') }]}>
        <InputNumber min={2000} max={2100} precision={0} style={{ width: '100%' }} />
      </Form.Item>
      <Form.Item
        name="title"
        label={t('spAdmin.fields.title')}
        className="sp-admin-form-span2"
        rules={[{ required: true, whitespace: true, message: t('spAdmin.fields.titleRequired') }]}
      >
        <Input maxLength={200} />
      </Form.Item>
      <Form.Item name="quoteNo" label={t('spAdmin.fields.quoteNo')}>
        <Input maxLength={100} />
      </Form.Item>
      <Form.Item name="offerDate" label={t('spAdmin.fields.offerDate')}>
        <DatePicker style={{ width: '100%' }} />
      </Form.Item>
      <Form.Item name="deadlineDate" label={t('spAdmin.fields.deadline')} extra={t('spAdmin.fields.deadlineHint')}>
        <DatePicker style={{ width: '100%' }} />
      </Form.Item>
      <Form.Item name="containerPreset" label={t('spAdmin.fields.container')}>
        <Select options={containerOptions} onChange={handleContainerPresetChange} />
      </Form.Item>
      {containerPreset === 'custom' ? (
        <Form.Item name="containerType" label={t('spAdmin.fields.containerType')}>
          <Input maxLength={50} placeholder={t('spAdmin.fields.containerTypePlaceholder')} />
        </Form.Item>
      ) : null}
      {containerPreset && containerPreset !== 'none' ? (
        <Form.Item name="containerCapacityCbm" label={t('spAdmin.fields.capacity')}>
          <InputNumber min={0} max={1000} step={1} style={{ width: '100%' }} addonAfter="m³" />
        </Form.Item>
      ) : null}
      <Form.Item name="priceTerms" label={t('spAdmin.fields.priceTerms')}>
        <Input maxLength={200} placeholder={t('spAdmin.fields.priceTermsPlaceholder')} />
      </Form.Item>
      <Form.Item name="historyRange" label={t('spAdmin.fields.history')} className="sp-admin-form-span2" extra={t('spAdmin.fields.historyHint')}>
        <DatePicker.RangePicker style={{ width: '100%' }} />
      </Form.Item>
      {showExportQtyUnit ? (
        <Form.Item name="exportQtyUnit" label={t('spAdmin.fields.exportQtyUnit')} className="sp-admin-form-span2" extra={t('spAdmin.import.exportQtyUnitHint')}>
          <Radio.Group
            optionType="button"
            options={[
              { value: 'Cartons', label: t('spAdmin.import.exportQtyCartons') },
              { value: 'Pieces', label: t('spAdmin.import.exportQtyPieces') },
            ]}
          />
        </Form.Item>
      ) : null}
      <Form.Item name="notes" label={t('spAdmin.fields.notes')} className="sp-admin-form-span-all">
        <Input.TextArea rows={2} maxLength={1000} />
      </Form.Item>
    </div>
  )
}
