import { InfoCircleOutlined } from '@ant-design/icons'
import { Button, Modal } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { DraftProductItem, DraftPreviewItem } from './batchCreateRules'
import { buildPreviewRows } from './batchWorkspaceLogic'

interface PreviewCodesModalProps {
  open: boolean
  products: DraftProductItem[]
  prefixCode?: string
  supplierName?: string
  onClose: () => void
}

/**
 * 「预览货号」：只是 前缀 + 4 位序号 的示意。
 * 真实货号与条码由后端在提交时分配（序号可能不同），所以弹窗顶部与货号列都明确标注「示意」。
 */
export default function PreviewCodesModal({ open, products, prefixCode, supplierName, onClose }: PreviewCodesModalProps) {
  const { t } = useTranslation()
  // 弹窗关闭时不用推演，避免大批量草稿每次输入都重算 1 万行。
  const rows = useMemo(() => (open ? buildPreviewRows(products, prefixCode) : []), [open, prefixCode, products])

  const columns: ColumnsType<DraftPreviewItem> = [
    { title: '#', key: 'index', width: 56, render: (_, __, index) => index + 1 },
    {
      title: `${t('productCreation.itemNumberCol')} (${t('productCreation.illustrative')})`,
      dataIndex: 'itemNumber',
      key: 'itemNumber',
      width: 190,
      render: (value: string) => (value ? <span className="pc-mono">{value}</span> : <span className="pc-faint">{t('productCreation.previewUnassigned')}</span>),
    },
    {
      title: t('domesticProducts.productName'),
      dataIndex: 'productName',
      key: 'productName',
      ellipsis: true,
      render: (value: string | undefined, record) => (
        <span className={record.parentPreviewKey ? 'pc-preview-sub' : undefined}>
          {record.parentPreviewKey ? '└ ' : ''}{value || <span className="pc-faint">—</span>}
        </span>
      ),
    },
    {
      title: t('productCreation.type'),
      dataIndex: 'productType',
      key: 'productType',
      width: 86,
      render: (type: ProductCreationType) => (
        type === ProductCreationType.SET
          ? t('productCreation.set')
          : type === ProductCreationType.SET_SUB_ITEM ? t('productCreation.subItemTag') : t('productCreation.normal')
      ),
    },
    {
      title: `${t('productCreation.privateLabelPrice')} $`,
      dataIndex: 'privateLabelPrice',
      key: 'privateLabelPrice',
      width: 100,
      align: 'right',
      render: (value?: number | null) => (value != null ? value.toFixed(2) : <span className="pc-faint">—</span>),
    },
  ]

  return (
    <Modal
      title={t('productCreation.previewTitle')}
      open={open}
      onCancel={onClose}
      width={820}
      maskClosable={false}
      destroyOnHidden
      footer={<Button onClick={onClose}>{t('common.close')}</Button>}
    >
      <div className="pc-notice pc-notice-warn" data-testid="product-creation-preview-banner">
        <InfoCircleOutlined />
        <span>
          {t('productCreation.previewBanner')}
          {prefixCode ? null : <> {t('productCreation.previewNoPrefix')}</>}
        </span>
      </div>
      <div className="pc-preview-meta">
        <span>{t('domesticProducts.supplier')}：{supplierName || '-'}</span>
        <span>{t('productCreation.prefixColumn')}：{prefixCode || '-'}</span>
        <span>{t('productCreation.expectedItems')}：{rows.length}</span>
      </div>
      <MeasuredTable<DraftPreviewItem>
        metricId="domestic-purchase.product-creation.preview"
        columns={columns}
        dataSource={rows}
        rowKey="key"
        size="small"
        tableLayout="fixed"
        pagination={{ pageSize: 50, showSizeChanger: false, hideOnSinglePage: true, size: 'small' }}
      />
    </Modal>
  )
}
