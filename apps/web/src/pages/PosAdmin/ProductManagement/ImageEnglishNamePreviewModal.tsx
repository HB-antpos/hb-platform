import { Modal, Tag, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import ProductListImage from '../../../components/ProductListImage'
import type { ImageEnglishNamePreviewRow } from './imageEnglishNamePreview'

const I18N = 'posAdmin.products.imageNames'

export interface ImageEnglishNamePreviewModalProps {
  /** null 表示弹窗关闭 */
  rows: ImageEnglishNamePreviewRow[] | null
  saving: boolean
  onCancel: () => void
  /** 只回传仍勾选的行 */
  onConfirm: (rows: ImageEnglishNamePreviewRow[]) => void
}

/**
 * 收银商品管理「看图生成英文名」的保存前确认：这里的保存会直接改商品名称和英文名称，
 * 所以先让用户对照图片逐条确认，取消勾选的行不会修改。
 */
export default function ImageEnglishNamePreviewModal({ rows, saving, onCancel, onConfirm }: ImageEnglishNamePreviewModalProps) {
  const { t } = useTranslation()
  const [selectedKeys, setSelectedKeys] = useState<string[]>([])

  // 每次打开都默认全选本次结果。
  useEffect(() => {
    setSelectedKeys(rows?.map((row) => row.key) ?? [])
  }, [rows])

  const selectedRows = useMemo(
    () => (rows ?? []).filter((row) => selectedKeys.includes(row.key)),
    [rows, selectedKeys],
  )

  const columns: ColumnsType<ImageEnglishNamePreviewRow> = [
    {
      title: t(`${I18N}.columnImage`, '图片'),
      dataIndex: 'productImage',
      width: 64,
      render: (src?: string) => (src ? <ProductListImage src={src} size={44} fit="contain" previewMask="" /> : null),
    },
    {
      title: t(`${I18N}.columnCurrent`, '当前名称'),
      dataIndex: 'currentName',
      render: (value: string, row) => (
        <div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>{row.productCode}</Typography.Text>
          <div>{value || '—'}</div>
        </div>
      ),
    },
    {
      title: t(`${I18N}.columnNew`, '新英文名'),
      dataIndex: 'newName',
      render: (value: string, row) => (
        <div>
          <Typography.Text strong>{value}</Typography.Text>
          {row.source === 'text' ? (
            <div><Tag color="orange" style={{ marginTop: 4 }}>{t(`${I18N}.sourceText`, '无图，按名称翻译')}</Tag></div>
          ) : null}
        </div>
      ),
    },
  ]

  return (
    <Modal
      open={rows !== null}
      title={t(`${I18N}.previewTitle`, '确认看图生成的英文名')}
      width={720}
      okText={t(`${I18N}.previewConfirm`, '保存 {{count}} 个', { count: selectedRows.length })}
      okButtonProps={{ disabled: selectedRows.length === 0 }}
      confirmLoading={saving}
      onOk={() => onConfirm(selectedRows)}
      onCancel={onCancel}
      destroyOnHidden
    >
      <Typography.Paragraph type="secondary">
        {t(`${I18N}.previewHint`, '保存后会同时覆盖商品名称和英文名称；取消勾选的商品不会修改。')}
      </Typography.Paragraph>
      <MeasuredTable<ImageEnglishNamePreviewRow>
        metricId="pos-admin.product-management.image-english-name-preview"
        size="small"
        rowKey="key"
        columns={columns}
        dataSource={rows ?? []}
        pagination={false}
        scroll={{ y: 420 }}
        rowSelection={{
          selectedRowKeys: selectedKeys,
          onChange: (keys) => setSelectedKeys(keys.map(String)),
        }}
      />
    </Modal>
  )
}
