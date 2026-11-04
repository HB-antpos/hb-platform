import { Alert, Form, Input, InputNumber, Modal, Select, message } from 'antd'
import { useState, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { createPlan } from '../../../../services/planogramService'
import type { PlanogramDetailDto, ShelfModelDto } from '../../../../types/planogram'
import { applyPlanogramFormError } from '../formParts'
import { BAY_COUNT_RANGE, GAP_RANGE, NEW_PLAN_DEFAULTS } from './planListLogic'

interface NewPlanFormValues {
  name: string
  shelfModelGuid?: string
  bayCount: number
  gapXMm: number
  gapYMm: number
  notes?: string
}

const FIELD_NAMES = ['name', 'shelfModelGuid', 'bayCount', 'gapXMm', 'gapYMm', 'notes'] as const

interface NewPlanModalProps {
  open: boolean
  /** 只有启用的货架型号能用于新方案（停用的型号已有方案仍可看，不可新建）。 */
  shelfModels: readonly ShelfModelDto[]
  shelfModelsLoading: boolean
  onClose: () => void
  onCreated: (detail: PlanogramDetailDto) => void
}

/** 新建方案：名称、货架型号、节数 1–12、左右 / 上下间距（默认 10）、备注。创建成功后由调用方直接进入设计器。 */
export default function NewPlanModal({ open, shelfModels, shelfModelsLoading, onClose, onCreated }: NewPlanModalProps) {
  const { t, i18n } = useTranslation()
  const [form] = Form.useForm<NewPlanFormValues>()
  const [busy, setBusy] = useState(false)
  const noShelf = !shelfModelsLoading && shelfModels.length === 0

  // 货架型号目录可能在弹窗打开后才刷新到（别的页签刚新建的型号）：还没选时自动选中第一个，
  // initialValues 只在挂载时生效，不处理的话下拉会一直空着。
  useEffect(() => {
    if (!form.getFieldValue('shelfModelGuid') && shelfModels[0]) form.setFieldValue('shelfModelGuid', shelfModels[0].guid)
  }, [form, shelfModels])

  const submit = async () => {
    let values: NewPlanFormValues
    try {
      values = await form.validateFields()
    } catch {
      return
    }
    if (busy || !values.shelfModelGuid) return
    setBusy(true)
    try {
      const created = await createPlan({
        name: values.name.trim(),
        notes: values.notes?.trim() ? values.notes.trim() : null,
        shelfModelGuid: values.shelfModelGuid,
        bayCount: values.bayCount,
        gapXMm: values.gapXMm,
        gapYMm: values.gapYMm,
      })
      message.success(t('planogram.plans.newModal.created'))
      onCreated(created)
    } catch (error) {
      const text = applyPlanogramFormError(form, error, FIELD_NAMES, i18n.language, (key) => t(key))
      if (text) message.error(text)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={open}
      title={t('planogram.plans.newModal.title')}
      okText={t('planogram.plans.newModal.ok')}
      cancelText={t('planogram.common.cancel')}
      confirmLoading={busy}
      okButtonProps={{ disabled: noShelf }}
      onOk={() => void submit()}
      onCancel={onClose}
      destroyOnHidden
      maskClosable={false}
    >
      {noShelf ? <Alert className="planogram-modal-alert" type="warning" showIcon message={t('planogram.plans.newModal.noShelf')} /> : null}
      <Form<NewPlanFormValues>
        form={form}
        layout="vertical"
        className="planogram-form"
        initialValues={{ name: '', shelfModelGuid: shelfModels[0]?.guid, ...NEW_PLAN_DEFAULTS, notes: '' }}
        disabled={busy}
      >
        <Form.Item name="name" label={t('planogram.plans.newModal.name')} rules={[{ required: true, whitespace: true, message: t('planogram.validation.required') }]}>
          <Input maxLength={100} autoFocus placeholder={t('planogram.plans.newModal.namePlaceholder')} />
        </Form.Item>
        <Form.Item name="shelfModelGuid" label={t('planogram.plans.newModal.shelf')} rules={[{ required: true, message: t('planogram.validation.required') }]}>
          <Select
            loading={shelfModelsLoading}
            placeholder={t('planogram.plans.newModal.shelfPlaceholder')}
            options={shelfModels.map((model) => ({ value: model.guid, label: model.name }))}
          />
        </Form.Item>
        <div className="planogram-grid is-3">
          <Form.Item name="bayCount" label={t('planogram.plans.newModal.bays')} rules={[{ required: true, message: t('planogram.validation.required') }]}>
            <InputNumber className="planogram-number" min={BAY_COUNT_RANGE.min} max={BAY_COUNT_RANGE.max} precision={0} suffix={<span className="planogram-unit">{t('planogram.plans.newModal.baysUnit')}</span>} />
          </Form.Item>
          <Form.Item name="gapXMm" label={t('planogram.plans.newModal.gapX')} rules={[{ required: true, message: t('planogram.validation.required') }]}>
            <InputNumber className="planogram-number" min={GAP_RANGE.min} max={GAP_RANGE.max} precision={0} suffix={<span className="planogram-unit">mm</span>} />
          </Form.Item>
          <Form.Item name="gapYMm" label={t('planogram.plans.newModal.gapY')} rules={[{ required: true, message: t('planogram.validation.required') }]}>
            <InputNumber className="planogram-number" min={GAP_RANGE.min} max={GAP_RANGE.max} precision={0} suffix={<span className="planogram-unit">mm</span>} />
          </Form.Item>
        </div>
        <div className="planogram-field-note">{t('planogram.plans.newModal.gapNote')}</div>
        <Form.Item name="notes" label={t('planogram.plans.newModal.notes')} className="planogram-grid-gap-top">
          <Input.TextArea maxLength={500} autoSize={{ minRows: 2, maxRows: 4 }} />
        </Form.Item>
      </Form>
    </Modal>
  )
}
