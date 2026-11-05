import { Button, Form, Input, Modal, Select, Tag, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import { pasteImportGrades } from '../../../services/productGradeService'
import {
  PRODUCT_GRADE_CONFIG,
  type PasteImportPreviewItem,
  type PasteImportResult,
} from '../../../types/productGrade'
import { formatDomesticSupplierLabel } from '../../../utils/domesticSupplierLabel'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface Props {
  open: boolean
  onClose: () => void
  onSuccess: () => void
}

const GRADE_TAG_COLOR: Record<string, string> = {
  A: 'purple',
  B: 'blue',
  C: 'orange',
  D: 'red',
}

export default function PasteImportModal({ open, onClose, onSuccess }: Props) {
  const { t } = useTranslation()
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const [previewData, setPreviewData] = useState<PasteImportResult | null>(null)
  const [suppliers, setSuppliers] = useState<Array<{ label: string; value: string }>>([])
  const [supplierLoading, setSupplierLoading] = useState(false)

  const gradeOptions = Object.entries(PRODUCT_GRADE_CONFIG).map(([key, cfg]) => ({
    label: t(`productGrade.${cfg.i18nKey}`),
    value: key,
  }))

  const loadSuppliers = async () => {
    setSupplierLoading(true)
    try {
      const result = await getActiveChinaSuppliers()
      setSuppliers(
        result.map((item) => ({
          // 无「查看国内供应商名称」权限时名称为空，只显示代码。
          label: formatDomesticSupplierLabel(item.supplierCode, item.supplierName),
          value: item.supplierCode,
        })),
      )
    } catch {
      message.error(t('productGrade.loadSuppliersFailed'))
    } finally {
      setSupplierLoading(false)
    }
  }

  const handleOpen = () => {
    void loadSuppliers()
    form.resetFields()
    setPreviewData(null)
  }

  const handleMatch = async () => {
    try {
      const values = await form.validateFields()
      setLoading(true)
      const result = await pasteImportGrades({
        supplierCode: values.supplierCode,
        productNumbers: values.productNumbers,
        grade: values.grade,
      })
      setPreviewData(result)
      if (result.matchedCount === 0) {
        message.warning(t('productGrade.noMatchedProducts'))
      }
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      message.error(error instanceof Error ? error.message : t('productGrade.matchFailed'))
    } finally {
      setLoading(false)
    }
  }

  const handleImport = () => {
    if (!previewData || previewData.matchedCount === 0) return
    message.success(
      t('productGrade.importResult', {
        created: previewData.createdCount,
        updated: previewData.updatedCount,
      }),
    )
    onSuccess()
    onClose()
  }

  const previewColumns: ColumnsType<PasteImportPreviewItem> = [
    { title: t('column.itemNumber'), dataIndex: 'productNumber', width: 140 },
    {
      title: t('column.status'),
      dataIndex: 'matched',
      width: 80,
      render: (matched: boolean) => (
        <Tag color={matched ? 'success' : 'error'}>
          {matched ? t('productGrade.matched') : t('productGrade.unmatched')}
        </Tag>
      ),
    },
    { title: t('column.productName'), dataIndex: 'productName', width: 200, ellipsis: true },
    {
      title: t('productGrade.currentGrade'),
      dataIndex: 'existingGrade',
      width: 100,
      render: (grade?: string) =>
        grade ? <Tag color={GRADE_TAG_COLOR[grade] || 'default'}>{grade}</Tag> : '--',
    },
  ]

  return (
    <Modal
      title={t('productGrade.pasteImportTitle')}
      open={open}
      onCancel={onClose}
      afterOpenChange={(visible) => {
        if (visible) handleOpen()
      }}
      width={800}
      footer={[
        <Button key="cancel" onClick={onClose}>
          {t('common.close')}
        </Button>,
        <Button key="match" loading={loading} onClick={() => void handleMatch()}>
          {t('productGrade.match')}
        </Button>,
        <Button
          key="import"
          type="primary"
          disabled={!previewData || previewData.matchedCount === 0}
          onClick={handleImport}
        >
          {t('productGrade.confirmImport')}
        </Button>,
      ]}
    >
      <Form form={form} layout="vertical">
        <Form.Item
          name="supplierCode"
          label={t('column.supplier')}
          rules={[{ required: true, message: t('productGrade.selectSupplier') }]}
        >
          <Select
            showSearch
            placeholder={t('productGrade.selectSupplier')}
            options={suppliers}
            loading={supplierLoading}
            optionFilterProp="label"
          />
        </Form.Item>
        <Form.Item
          name="grade"
          label={t('productGrade.selectTargetGrade')}
          rules={[{ required: true, message: t('productGrade.selectTargetGrade') }]}
        >
          <Select placeholder={t('productGrade.selectTargetGrade')} options={gradeOptions} />
        </Form.Item>
        <Form.Item
          name="productNumbers"
          label={t('productGrade.gradeList')}
          rules={[{ required: true, message: t('productGrade.pasteItemNumbers') }]}
        >
          <Input.TextArea rows={6} placeholder={t('productGrade.pasteItemNumbers')} />
        </Form.Item>
      </Form>

      {previewData && (
        <>
          <div
            style={{
              marginBottom: 8,
              color: '#666',
              display: 'flex',
              gap: 12,
              alignItems: 'center',
              flexWrap: 'wrap',
            }}
          >
            <span>{t('productGrade.totalItemNumbers', { count: previewData.totalCount })}</span>
            <Tag color="success">{t('productGrade.matchCount', { count: previewData.matchedCount })}</Tag>
            <Tag color="processing">{t('productGrade.createCount', { count: previewData.createdCount })}</Tag>
            <Tag color="warning">{t('productGrade.updateCount', { count: previewData.updatedCount })}</Tag>
            {previewData.totalCount - previewData.matchedCount > 0 && (
              <Tag color="error">
                {t('productGrade.unmatchedCount', {
                  count: previewData.totalCount - previewData.matchedCount,
                })}
              </Tag>
            )}
          </div>
          <MeasuredTable<PasteImportPreviewItem> metricId="warehouse.product-grade-management.paste-import-modal.table-1"
            rowKey="productNumber"
            size="small"
            columns={previewColumns}
            dataSource={previewData.previewItems}
            pagination={{ pageSize: 50, showSizeChanger: false }}
            scroll={{ y: 300 }}
          />
        </>
      )}
    </Modal>
  )
}
