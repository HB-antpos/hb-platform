import { CopyOutlined, DownloadOutlined } from '@ant-design/icons'
import { Button, Drawer, InputNumber, Modal, Tabs, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { getBatchDetail, updatePrivateLabelPrice } from '../../../services/domesticProductCreationService'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { BatchDetail, BatchInfo, BatchProductItem } from '../../../types/domesticProductCreation'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import CopyValueButton from './CopyValueButton'
import { getBatchDetailErrorMessage } from './batchDetailErrorMessage'
import {
  buildDetailRows,
  computeChangedPrices,
  formatCopyText,
  getDetailTabCounts,
  getDisplayedPrice,
  isPriceChanged,
  setEditedPrice,
  settleEditedPrice,
} from './batchDetailLogic'
import type { BatchDetailTab, EditedPrices } from './batchDetailLogic'
import { formatBatchTime } from './batchListLogic'
import { exportProductCreationBatchToExcel, getExportableBatchItems } from './exportBatchDetail'

interface BatchDetailDrawerProps {
  open: boolean
  batch: BatchInfo | null
  onClose: () => void
}

/**
 * 批次明细抽屉：查看批次内全部货号/条码，改零售价并保存。
 * 只提交改动过的价格；清空输入框等于不改这一行（不会存成 0）。
 */
export default function BatchDetailDrawer({ open, batch, onClose }: BatchDetailDrawerProps) {
  const { t } = useTranslation()
  const guardRef = useRef(createLatestRequestGuard())
  const [loading, setLoading] = useState(false)
  const [detail, setDetail] = useState<BatchDetail | null>(null)
  const [activeTab, setActiveTab] = useState<BatchDetailTab>('all')
  const [editedPrices, setEditedPrices] = useState<EditedPrices>({})
  const [saving, setSaving] = useState(false)
  const [exporting, setExporting] = useState(false)
  const batchNumber = batch?.batchNumber

  const loadDetail = useCallback(async (targetBatchNumber: string) => {
    await runLatestGuardedRequest(guardRef.current, () => getBatchDetail(targetBatchNumber), {
      onStart: () => setLoading(true),
      onSuccess: (response) => {
        if (response.success && response.data) {
          setDetail(response.data)
          setEditedPrices({})
        } else {
          message.error(response.message || t('productCreation.loadDetailFailed'))
        }
      },
      onError: (error) => message.error(getBatchDetailErrorMessage(error, t('productCreation.loadDetailFailed'))),
      onSettled: () => setLoading(false),
    })
  }, [t])

  // 每次打开（或换了批次）都重新取明细；切批次时先清空，避免短暂显示上一个批次的数据。
  useEffect(() => {
    if (!open || !batchNumber) return undefined
    setDetail(null)
    setEditedPrices({})
    setActiveTab('all')
    void loadDetail(batchNumber)
    return () => guardRef.current.invalidate()
  }, [open, batchNumber, loadDetail])

  const items = useMemo(() => detail?.items ?? [], [detail])
  const rows = useMemo(() => buildDetailRows(items, activeTab), [items, activeTab])
  const counts = useMemo(() => getDetailTabCounts(items), [items])
  const changedPrices = useMemo(() => computeChangedPrices(items, editedPrices), [items, editedPrices])
  const changedCount = changedPrices.length

  const requestClose = () => {
    if (saving) return
    if (changedCount === 0) {
      onClose()
      return
    }
    // 有未保存的价格修改：关闭会丢失，先确认。
    Modal.confirm({
      title: t('productCreation.discardPriceEditsTitle'),
      content: t('productCreation.discardPriceEditsContent', { count: changedCount }),
      okText: t('productCreation.discardPriceEditsOk'),
      cancelText: t('productCreation.keepEditing'),
      okButtonProps: { danger: true },
      maskClosable: false,
      onOk: onClose,
    })
  }

  const handleSave = async () => {
    if (!batchNumber || changedCount === 0 || saving) return
    setSaving(true)
    try {
      const response = await updatePrivateLabelPrice(batchNumber, changedPrices)
      if (response.success) {
        message.success(t('productCreation.saveSuccess'))
        await loadDetail(batchNumber)
      } else {
        message.error(response.message || t('productCreation.saveFailed'))
      }
    } catch (error) {
      message.error(getBatchDetailErrorMessage(error, t('productCreation.saveFailed')))
    } finally {
      setSaving(false)
    }
  }

  // 导出入口只有这一个；导出的是服务端已保存的数据（未保存的价格草稿不进文件）。
  const handleExport = async () => {
    if (!detail || !batchNumber || exporting) return
    if (getExportableBatchItems(detail.items).length === 0) {
      message.warning(t('productCreation.noDataToExport'))
      return
    }
    const messageKey = `product-creation-export-${batchNumber}`
    setExporting(true)
    message.loading({ content: t('productCreation.exporting'), key: messageKey })
    try {
      await exportProductCreationBatchToExcel(detail, { batchNumber, t })
      message.success({ content: t('productCreation.exportSuccess'), key: messageKey })
    } catch (error) {
      console.error('导出失败:', error)
      message.error({ content: t('productCreation.exportFailed'), key: messageKey })
    } finally {
      setExporting(false)
    }
  }

  // 复制当前页签里看到的行：制表符分列，粘贴到 Excel 会自动分成「货号 / 条码」两列。
  const handleCopyAll = () => {
    const text = formatCopyText(rows, {
      itemNumber: t('productImport.hbProductNoCol'),
      barcode: t('domesticProducts.barcode'),
    })
    if (!text) {
      message.warning(t('productCreation.noCopyData'))
      return
    }
    void copyTextToClipboard(text, {
      successMessage: t('productCreation.copyAllItemBarcodesSuccess', { count: rows.length }),
      failureMessage: t('productCreation.copyFailed'),
    })
  }

  const columns: ColumnsType<BatchProductItem> = [
    {
      // 界面表头用页面自己的「货号」；全局 productImport.hbProductNoCol 带着字段名「(hbProductNo)」，仅保留给导出表头沿用。
      title: t('productCreation.itemNumberCol'),
      dataIndex: 'hbProductNo',
      key: 'hbProductNo',
      width: 138,
      render: (value: string) => (
        <span className="pc-code-cell">
          <span className="pc-mono">{value}</span>
          <CopyValueButton value={value} label={value} />
        </span>
      ),
    },
    {
      title: t('domesticProducts.barcode'),
      dataIndex: 'barcode',
      key: 'barcode',
      width: 162,
      render: (value: string) => (
        <span className="pc-code-cell">
          <span className="pc-mono">{value}</span>
          <CopyValueButton value={value} label={value} />
        </span>
      ),
    },
    {
      title: t('domesticProducts.productName'),
      dataIndex: 'productName',
      key: 'productName',
      ellipsis: true,
      render: (value: string, record) => (
        record.productType === ProductCreationType.SET_SUB_ITEM
          ? <span className="pc-detail-sub-name">└ {value}</span>
          : value
      ),
    },
    {
      title: t('productCreation.type'),
      dataIndex: 'productType',
      key: 'productType',
      width: 64,
      render: (type: ProductCreationType) => {
        if (type === ProductCreationType.SET) return <span className="pc-type-tag pc-type-tag-set">{t('productCreation.set')}</span>
        if (type === ProductCreationType.SET_SUB_ITEM) return <span className="pc-type-tag">{t('productCreation.subItemTag')}</span>
        return <span className="pc-type-tag">{t('productCreation.normal')}</span>
      },
    },
    {
      title: `${t('productCreation.privateLabelPrice')} $`,
      key: 'privateLabelPrice',
      width: 100,
      align: 'right',
      render: (_, record) => (
        <InputNumber
          size="small"
          controls={false}
          className={isPriceChanged(record, editedPrices) ? 'pc-cell pc-cell-num pc-cell-changed' : 'pc-cell pc-cell-num'}
          value={getDisplayedPrice(record, editedPrices)}
          min={0}
          precision={2}
          placeholder="—"
          aria-label={`${t('productCreation.privateLabelPrice')} ${record.hbProductNo}`}
          onChange={(value) => setEditedPrices((current) => setEditedPrice(current, record.itemNumber, value))}
          // 失焦时结算：清空或改回原价的草稿被移除，输入框恢复原价。
          onBlur={() => setEditedPrices((current) => settleEditedPrice(current, items, record.itemNumber))}
        />
      ),
    },
  ]

  const meta = batch ? (
    <div className="pc-drawer-meta">
      <span>{detail?.supplierName || batch.supplierName}</span>
      {(detail?.prefixCode || batch.prefixCode) ? <span className="pc-prefix-tag">{detail?.prefixCode || batch.prefixCode}</span> : null}
      <span>·</span>
      <span>
        {formatBatchTime(detail?.createdAt || batch.createdAt)}
        {(detail?.createdBy || batch.createdBy) ? ` · ${detail?.createdBy || batch.createdBy}` : ''}
      </span>
    </div>
  ) : null

  const drawerTitle = batch ? (
    <div className="pc-drawer-head">
      <span className="pc-drawer-title">
        <span className="pc-mono pc-drawer-batch-no">{batch.batchNumber}</span>
        <CopyValueButton value={batch.batchNumber} label={batch.batchNumber} />
      </span>
      {meta}
    </div>
  ) : null

  return (
    <Drawer
      rootClassName="pc-drawer pc-detail-drawer"
      title={drawerTitle}
      width={780}
      open={open}
      onClose={requestClose}
      maskClosable={false}
      destroyOnHidden
      closable={{ placement: 'end' }}
      footer={(
        <div className="pc-drawer-footer" data-testid="product-creation-detail-footer">
          {changedCount > 0 ? (
            <span className="pc-dirty">{t('productCreation.priceChangedCount', { count: changedCount })}</span>
          ) : null}
          <span className="pc-drawer-footer-spacer" />
          <Button icon={<CopyOutlined />} disabled={rows.length === 0} onClick={handleCopyAll}>
            {t('productCreation.copyCodeAndBarcode')}
          </Button>
          <Button icon={<DownloadOutlined />} loading={exporting} disabled={!detail} onClick={() => void handleExport()}>
            {t('productCreation.exportExcel')}
          </Button>
          <Button type="primary" loading={saving} disabled={changedCount === 0} onClick={() => void handleSave()}>
            {changedCount > 0 ? t('productCreation.saveChangedPrices', { count: changedCount }) : t('productCreation.saveChangedPricesIdle')}
          </Button>
        </div>
      )}
    >
      <div data-testid="product-creation-detail">
        <Tabs
          className="pc-detail-tabs"
          activeKey={activeTab}
          onChange={(key) => setActiveTab(key as BatchDetailTab)}
          items={[
            { key: 'all', label: <span>{t('common.all')} <span className="pc-sub">{counts.all}</span></span> },
            { key: 'normal', label: <span>{t('productCreation.normal')} <span className="pc-sub">{counts.normal}</span></span> },
            { key: 'set', label: <span>{t('productCreation.set')} <span className="pc-sub">{counts.set}</span></span> },
          ]}
        />
        <MeasuredTable<BatchProductItem>
          metricId="domestic-purchase.product-creation.detail"
          className="pc-detail-table"
          columns={columns}
          dataSource={rows}
          rowKey="itemNumber"
          loading={loading}
          size="small"
          tableLayout="fixed"
          rowClassName={(record) => (record.productType === ProductCreationType.SET_SUB_ITEM ? 'pc-detail-subrow' : '')}
          // 单个批次最多 1 万行，分页避免一次渲染过多输入框；表头固定、表体在抽屉高度内滚动。
          pagination={{ defaultPageSize: 100, pageSizeOptions: [50, 100, 200], showSizeChanger: true, hideOnSinglePage: true, size: 'small' }}
          scroll={{ y: 'calc(100vh - 290px)' }}
        />
      </div>
    </Drawer>
  )
}
