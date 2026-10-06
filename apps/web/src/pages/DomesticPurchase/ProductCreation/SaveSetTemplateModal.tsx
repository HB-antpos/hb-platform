import { Form, Input, Modal, message } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { createSetProductTemplate } from '../../../services/domesticProductCreationService'
import { getBatchDetailErrorMessage } from './batchDetailErrorMessage'
import type { DraftProductItem } from './batchCreateRules'
import { buildSetProductTemplatePayload } from './setTemplateRules'

interface SaveSetTemplateModalProps {
  /** 要保存为模板的套装草稿；null 表示弹窗关闭。调用方负责先做 validateSetTemplateProduct。 */
  product: DraftProductItem | null
  supplierCode?: string
  onClose: () => void
  onSaved: () => void
}

/** 把工作台里的一个套装草稿存为该供应商的套装模板。 */
export default function SaveSetTemplateModal({ product, supplierCode, onClose, onSaved }: SaveSetTemplateModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<{ templateName: string }>()
  const [saving, setSaving] = useState(false)

  // 每次打开都以套装名称作为默认模板名，用户可改。Modal 关闭时内容被销毁，重新打开才会再次赋值。
  useEffect(() => {
    if (product) form.setFieldsValue({ templateName: product.productName?.trim() || '' })
  }, [form, product])

  const handleOk = async () => {
    if (!product || !supplierCode || saving) return
    try {
      const values = await form.validateFields()
      setSaving(true)
      const response = await createSetProductTemplate(buildSetProductTemplatePayload(supplierCode, values.templateName, product))
      if (!response.success) {
        message.error(response.message || t('productCreation.saveSetTemplateFailed'))
        return
      }
      message.success(t('productCreation.saveSetTemplateSuccess'))
      onSaved()
    } catch (error) {
      // 表单校验失败（errorFields）由字段自身提示，不再弹 toast。
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      message.error(getBatchDetailErrorMessage(error, t('productCreation.saveSetTemplateFailed')))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      title={t('productCreation.saveSetTemplateTitle')}
      open={Boolean(product)}
      onOk={() => void handleOk()}
      onCancel={onClose}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={saving}
      maskClosable={false}
      destroyOnHidden
    >
      <Form form={form} layout="vertical" autoComplete="off">
        <Form.Item
          name="templateName"
          label={t('productCreation.setTemplateName')}
          rules={[{ required: true, whitespace: true, message: t('productCreation.setTemplateNameRequired') }]}
        >
          <Input maxLength={100} placeholder={t('productCreation.setTemplateName')} />
        </Form.Item>
      </Form>
    </Modal>
  )
}
