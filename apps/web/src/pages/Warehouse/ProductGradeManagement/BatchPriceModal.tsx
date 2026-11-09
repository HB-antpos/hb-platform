import { DollarOutlined } from '@ant-design/icons'
import { Alert, Form, InputNumber, Modal, Radio, message } from 'antd'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { batchUpdateGradePrices } from '../../../services/productGradeService'
import { HQ_ACTIONS_ENABLED } from '../../../utils/legacyDatabases'

interface Props {
  open: boolean
  selectedCount: number
  productCodes: string[]
  onClose: () => void
  onSuccess: () => void
}

export default function BatchPriceModal({ open, selectedCount, productCodes, onClose, onSuccess }: Props) {
  const { t } = useTranslation()
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)

  const handleOk = async () => {
    try {
      const values = await form.validateFields()
      if (values.importPrice == null && values.oemPrice == null) {
        message.warning(t('productGrade.fillAtLeastOnePrice'))
        return
      }
      setLoading(true)
      const result = await batchUpdateGradePrices({
        productCodes,
        targetDatabase: values.targetDatabase,
        importPrice: values.importPrice ?? undefined,
        oemPrice: values.oemPrice ?? undefined,
      })
      if (result.success) {
        message.success(result.message || t('productGrade.batchPriceSuccess', { count: result.affectedCount }))
        onSuccess()
        onClose()
      } else {
        message.error(result.message || t('productGrade.batchPriceFailed'))
      }
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      message.error(t('productGrade.batchPriceFailed'))
    } finally {
      setLoading(false)
    }
  }

  const handleCancel = () => {
    form.resetFields()
    onClose()
  }

  return (
    <Modal
      title={t('productGrade.batchChangePrice')}
      open={open}
      onCancel={handleCancel}
      confirmLoading={loading}
      onOk={() => void handleOk()}
      okText={t('productGrade.confirmModify')}
      cancelText={t('common.cancel')}
      width={520}
    >
      <Alert
        type="info"
        showIcon
        message={t('productGrade.selectedProducts', { count: selectedCount })}
        style={{ marginBottom: 16 }}
      />

      <Form
        form={form}
        layout="vertical"
        initialValues={{ targetDatabase: 'HBweb' }}
      >
        {/* HQ 旧库已断开：只剩 HBweb 一个目标库，隐藏选择但保留字段，提交时仍带 targetDatabase=HBweb。 */}
        <Form.Item
          name="targetDatabase"
          hidden={!HQ_ACTIONS_ENABLED}
          label={t('productGrade.targetDatabase')}
          rules={[{ required: true, message: t('productGrade.selectTargetDatabase') }]}
        >
          <Radio.Group>
            <Radio value="HBweb">
              {t('productGrade.hbwebDbDesc')}
            </Radio>
            {HQ_ACTIONS_ENABLED && (
              <Radio value="HQ">
                {t('productGrade.hqDbDesc')}
              </Radio>
            )}
          </Radio.Group>
        </Form.Item>

        <Form.Item
          name="importPrice"
          label={t('productGrade.importPrice')}
          extra={t('productGrade.leaveEmptyUnchanged')}
        >
          <InputNumber
            min={0}
            precision={2}
            placeholder={t('productGrade.enterImportPrice')}
            prefix={<DollarOutlined />}
            style={{ width: '100%' }}
          />
        </Form.Item>

        <Form.Item
          name="oemPrice"
          label={t('productGrade.retailPrice')}
          extra={t('productGrade.leaveEmptyUnchanged')}
        >
          <InputNumber
            min={0}
            precision={2}
            placeholder={t('productGrade.enterRetailPrice')}
            prefix={<DollarOutlined />}
            style={{ width: '100%' }}
          />
        </Form.Item>
      </Form>

      <Alert
        type="warning"
        showIcon
        message={t('productGrade.confirmWarning')}
      />
    </Modal>
  )
}

