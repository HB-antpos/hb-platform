import { Alert, Button, Modal, Tag } from 'antd'
import { useTranslation } from 'react-i18next'
import type { ColumnsType } from 'antd/es/table'
import type { DuplicateGroup } from './types'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface DuplicateDialogProps {
  open: boolean
  duplicateGroups: DuplicateGroup[]
  onClose: () => void
  onConfirm: () => void
}

export function DuplicateDialog({ open, duplicateGroups, onClose, onConfirm }: DuplicateDialogProps) {
  const { t } = useTranslation()
  const invalidGroupCount = duplicateGroups.filter((group) => !group.isMergeable).length
  const fieldLabels: Record<DuplicateGroup['invalidFields'][number], string> = {
    quantity: t('productImport.mergeQuantityField', '件数'),
    casePackQuantity: t('productImport.mergePackingQuantityField', '单件装箱数'),
    volume: t('productImport.mergeVolumeField', '单件体积'),
  }
  const columns: ColumnsType<DuplicateGroup> = [
    { title: t('productImport.itemNumberCol'), dataIndex: 'productCode', key: 'productCode', width: 150 },
    { title: t('productImport.duplicateCount', '重复数量'), dataIndex: 'count', key: 'count', width: 100, render: (count) => <Tag color="orange">{count}</Tag> },
    { title: t('productImport.mergedQuantity', '合并后件数'), key: 'mergedQuantity', width: 110, render: (_, record) => record.merged.quantity },
    {
      title: t('productImport.mergedPackingQuantity', '合并后装箱数'),
      key: 'mergedPackingQuantity',
      width: 140,
      render: (_, record) => record.invalidFields.includes('casePackQuantity') || record.invalidFields.includes('quantity') ? '--' : record.merged.casePackQuantity,
    },
    {
      title: t('productImport.mergedUnitVolume', '合并后体积'),
      key: 'mergedUnitVolume',
      width: 130,
      render: (_, record) => record.invalidFields.includes('volume') || record.invalidFields.includes('quantity') ? '--' : record.merged.volume.toFixed(3),
    },
    {
      title: t('productImport.mergeValidation', '校验'),
      key: 'mergeValidation',
      width: 190,
      render: (_, record) => record.isMergeable
        ? <Tag color="success">{t('productImport.mergeReady', '可合并')}</Tag>
        : <Tag color="error">{t('productImport.mergeInvalidFields', '无效字段：{{fields}}', { fields: record.invalidFields.map((field) => fieldLabels[field]).join('、') })}</Tag>,
    },
  ]

  return (
    // maskClosable={false}：重复合并会改写表格数据，误点遮罩关掉弹窗会让人以为合并已完成
    <Modal title={t('productImport.foundDuplicateGroups', '发现 {{count}} 组重复货号', { count: duplicateGroups.length })} open={open} onCancel={onClose} maskClosable={false} width={860} footer={<><Button onClick={onClose}>{t('common.cancel', '取消')}</Button><Button type="primary" onClick={onConfirm} disabled={invalidGroupCount > 0}>{t('productImport.mergeDuplicates', '合并重复')}</Button></>}>
      <div data-testid="product-import-duplicate-dialog">
        <p style={{ marginBottom: 12 }}>{t('productImport.duplicateWarning', '以下货号在导入数据中存在重复，建议合并后再执行检测匹配：')}</p>
        {invalidGroupCount > 0 && (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('productImport.mergeBlockedSummary', '{{count}} 组数据不完整，请返回表格修正后重新检测。', { count: invalidGroupCount })}
          />
        )}
        <MeasuredTable metricId="domestic-purchase.product-import.duplicate-dialog.table-1" columns={columns} dataSource={duplicateGroups} rowKey="productCode" size="small" pagination={false} scroll={{ x: 820 }} />
      </div>
    </Modal>
  )
}
