import { ArrowLeftOutlined, DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, Drawer, Form, Input, InputNumber, Popconfirm, Switch, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  deactivateSetProductTemplate,
  getSetProductTemplate,
  getSetProductTemplates,
  updateSetProductTemplate,
} from '../../../services/domesticProductCreationService'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type {
  SetProductTemplateDetail,
  SetProductTemplatePayload,
  SetProductTemplateSummary,
} from '../../../types/domesticProductCreation'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { getBatchDetailErrorMessage } from './batchDetailErrorMessage'
import { buildSetProductTemplatePayload, validateSetTemplateProduct } from './setTemplateRules'
import { getSetTemplateValidationMessage } from './setTemplateUi'

interface TemplateFormValues {
  templateName: string
  setProductName: string
  isEnabled: boolean
  subItems: Array<{ productName?: string; privateLabelPrice?: number | null }>
}

interface SetTemplateDrawerProps {
  open: boolean
  supplierCode?: string
  supplierName?: string
  onClose: () => void
  /** 模板被修改或停用后通知工作台刷新「套用模板」下拉。 */
  onChanged: () => void
}

/**
 * 套装模板管理抽屉（取代原来 3 层嵌套的管理弹窗）：
 * 列表模式展示该供应商全部模板（含已停用），编辑模式在抽屉内切换成表单，不再叠加弹窗。
 */
export default function SetTemplateDrawer({ open, supplierCode, supplierName, onClose, onChanged }: SetTemplateDrawerProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<TemplateFormValues>()
  const listGuardRef = useRef(createLatestRequestGuard())
  const [loading, setLoading] = useState(false)
  const [templates, setTemplates] = useState<SetProductTemplateSummary[]>([])
  const [editing, setEditing] = useState<SetProductTemplateDetail | null>(null)
  const [saving, setSaving] = useState(false)

  const loadTemplates = useCallback(async () => {
    if (!supplierCode) {
      setTemplates([])
      return
    }
    await runLatestGuardedRequest(listGuardRef.current, () => getSetProductTemplates(supplierCode, true), {
      onStart: () => setLoading(true),
      onSuccess: (response) => {
        if (!response.success) {
          message.error(response.message || t('productCreation.loadSetTemplatesFailed'))
          return
        }
        setTemplates(response.data || [])
      },
      onError: (error) => message.error(getBatchDetailErrorMessage(error, t('productCreation.loadSetTemplatesFailed'))),
      onSettled: () => setLoading(false),
    })
  }, [supplierCode, t])

  useEffect(() => {
    if (!open) return undefined
    setEditing(null)
    void loadTemplates()
    return () => listGuardRef.current.invalidate()
  }, [open, loadTemplates])

  const handleEdit = async (templateId: string) => {
    if (!supplierCode) return
    setLoading(true)
    try {
      const response = await getSetProductTemplate(templateId, supplierCode)
      if (!response.success || !response.data) {
        message.error(response.message || t('productCreation.loadSetTemplateFailed'))
        return
      }
      setEditing(response.data)
    } catch (error) {
      message.error(getBatchDetailErrorMessage(error, t('productCreation.loadSetTemplateFailed')))
    } finally {
      setLoading(false)
    }
  }

  const handleDeactivate = async (templateId: string) => {
    if (!supplierCode) return
    setLoading(true)
    try {
      const response = await deactivateSetProductTemplate(templateId, supplierCode)
      if (!response.success) {
        message.error(response.message || t('productCreation.deactivateSetTemplateFailed'))
        return
      }
      message.success(t('productCreation.deactivateSetTemplateSuccess'))
      onChanged()
      await loadTemplates()
    } catch (error) {
      message.error(getBatchDetailErrorMessage(error, t('productCreation.deactivateSetTemplateFailed')))
    } finally {
      setLoading(false)
    }
  }

  const handleSave = async () => {
    if (!supplierCode || !editing || saving) return
    try {
      const values = await form.validateFields()
      const editingProduct = {
        key: editing.templateId,
        productType: ProductCreationType.SET,
        productName: values.setProductName,
        subItems: (values.subItems || []).map((item, index) => ({
          key: `template-edit-${index}`,
          productName: item.productName,
          privateLabelPrice: item.privateLabelPrice,
        })),
      }
      const validationError = validateSetTemplateProduct(editingProduct)
      if (validationError) {
        message.error(getSetTemplateValidationMessage(validationError, t))
        return
      }
      const payload: SetProductTemplatePayload = buildSetProductTemplatePayload(
        supplierCode,
        values.templateName,
        editingProduct,
        values.isEnabled,
      )
      setSaving(true)
      const response = await updateSetProductTemplate(editing.templateId, supplierCode, payload)
      if (!response.success) {
        message.error(response.message || t('productCreation.saveSetTemplateFailed'))
        return
      }
      message.success(t('productCreation.saveSetTemplateSuccess'))
      setEditing(null)
      onChanged()
      await loadTemplates()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      message.error(getBatchDetailErrorMessage(error, t('productCreation.saveSetTemplateFailed')))
    } finally {
      setSaving(false)
    }
  }

  const columns: ColumnsType<SetProductTemplateSummary> = [
    {
      title: t('productCreation.setTemplateName'),
      dataIndex: 'templateName',
      key: 'templateName',
      ellipsis: true,
    },
    {
      title: t('productCreation.setProductName'),
      dataIndex: 'setProductName',
      key: 'setProductName',
      ellipsis: true,
    },
    {
      title: t('productCreation.subItemCountColumn'),
      dataIndex: 'setQuantity',
      key: 'setQuantity',
      width: 76,
      align: 'center',
    },
    {
      title: t('common.status'),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 76,
      render: (enabled: boolean) => (
        <span className={enabled ? 'pc-status pc-status-on' : 'pc-status'}>
          {enabled ? t('productCreation.templateEnabled') : t('productCreation.templateDisabled')}
        </span>
      ),
    },
    {
      title: t('common.action'),
      key: 'actions',
      width: 112,
      align: 'right',
      render: (_, record) => (
        <span className="pc-row-actions">
          <Button type="link" size="small" onClick={() => void handleEdit(record.templateId)}>{t('common.edit')}</Button>
          {record.isEnabled ? (
            <Popconfirm
              title={t('productCreation.confirmDeactivateSetTemplate')}
              okText={t('common.confirm')}
              cancelText={t('common.cancel')}
              onConfirm={() => void handleDeactivate(record.templateId)}
            >
              <Button type="link" size="small" danger>{t('common.disable')}</Button>
            </Popconfirm>
          ) : null}
        </span>
      ),
    },
  ]

  const drawerTitle = (
    <div className="pc-drawer-head">
      <span className="pc-drawer-title">
        {editing ? (editing.templateName || t('productCreation.editSetTemplate')) : t('productCreation.setTemplateDrawerTitle')}
      </span>
      {supplierName ? <span className="pc-sub">{supplierName}</span> : null}
    </div>
  )

  return (
    <Drawer
      rootClassName="pc-drawer"
      title={drawerTitle}
      width={620}
      open={open}
      onClose={onClose}
      maskClosable={false}
      destroyOnHidden
      closable={{ placement: 'end' }}
      footer={editing ? (
        <div className="pc-drawer-footer">
          <Button icon={<ArrowLeftOutlined />} disabled={saving} onClick={() => setEditing(null)}>
            {t('productCreation.backToTemplates')}
          </Button>
          <span className="pc-drawer-footer-spacer" />
          <Button type="primary" loading={saving} onClick={() => void handleSave()}>{t('common.save')}</Button>
        </div>
      ) : (
        <div className="pc-drawer-footer">
          <span className="pc-drawer-footer-spacer" />
          <Button onClick={onClose}>{t('common.close')}</Button>
        </div>
      )}
    >
      {editing ? (
        // key + initialValues：每次进入编辑都重新挂载表单，避免沿用上一个模板的残留值。
        <Form<TemplateFormValues>
          key={editing.templateId}
          form={form}
          layout="vertical"
          autoComplete="off"
          initialValues={{
            templateName: editing.templateName,
            setProductName: editing.setProductName,
            isEnabled: editing.isEnabled,
            subItems: [...editing.subItems]
              .sort((left, right) => left.sortOrder - right.sortOrder)
              .map((item) => ({ productName: item.productName, privateLabelPrice: item.privateLabelPrice })),
          }}
        >
          <div className="pc-form-grid">
            <Form.Item
              name="templateName"
              label={t('productCreation.setTemplateName')}
              rules={[{ required: true, whitespace: true, message: t('productCreation.setTemplateNameRequired') }]}
            >
              <Input maxLength={100} />
            </Form.Item>
            <Form.Item
              name="setProductName"
              label={t('productCreation.setProductName')}
              rules={[{ required: true, whitespace: true, message: t('productCreation.setTemplateSetNameRequired') }]}
            >
              <Input maxLength={200} />
            </Form.Item>
          </div>
          <Form.Item name="isEnabled" label={t('common.status')} valuePropName="checked">
            <Switch checkedChildren={t('common.enabled')} unCheckedChildren={t('productCreation.templateDisabled')} />
          </Form.Item>
          <Form.List name="subItems">
            {(fields, { add, remove }) => (
              <div className="pc-template-subitems">
                <div className="pc-template-subitems-head">
                  <span className="pc-section-title">{t('productCreation.setSubItem')}</span>
                  <Button type="link" size="small" icon={<PlusOutlined />} onClick={() => add({ productName: '', privateLabelPrice: undefined })}>
                    {t('common.add')}
                  </Button>
                </div>
                {fields.map((field, index) => (
                  <div className="pc-template-subitem-row" key={field.key}>
                    <Form.Item
                      name={[field.name, 'productName']}
                      rules={[{ required: true, whitespace: true, message: t('productCreation.setTemplateSubItemNameRequired') }]}
                    >
                      <Input placeholder={t('productCreation.subItemPlaceholder', { index: index + 1 })} />
                    </Form.Item>
                    <Form.Item
                      name={[field.name, 'privateLabelPrice']}
                      rules={[{ required: true, message: t('productCreation.setTemplateSubItemPriceRequired') }]}
                    >
                      <InputNumber min={0} precision={2} style={{ width: '100%' }} placeholder={t('productCreation.privateLabelPrice')} />
                    </Form.Item>
                    <Button
                      type="text"
                      danger
                      icon={<DeleteOutlined />}
                      aria-label={t('productCreation.deleteSetTemplateSubItem')}
                      title={t('productCreation.deleteSetTemplateSubItem')}
                      onClick={() => remove(field.name)}
                    />
                  </div>
                ))}
              </div>
            )}
          </Form.List>
        </Form>
      ) : (
        <MeasuredTable<SetProductTemplateSummary>
          metricId="domestic-purchase.product-creation.set-templates"
          rowKey="templateId"
          size="small"
          tableLayout="fixed"
          loading={loading}
          pagination={false}
          dataSource={templates}
          columns={columns}
          locale={{ emptyText: t('productCreation.noSetTemplates') }}
        />
      )}
    </Drawer>
  )
}
