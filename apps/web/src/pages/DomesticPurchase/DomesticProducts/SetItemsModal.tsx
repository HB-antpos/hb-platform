import { Button, Input, InputNumber, Modal, Space, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { ClipboardEvent } from 'react'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useSnapshotSort } from '../../../hooks/useSnapshotSort'
import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  getDomesticProductSetItems,
  updateDomesticProduct,
  updateDomesticProductSetItems,
} from '../../../services/domesticProductService'
import type { DomesticProductItem, DomesticProductSetItem } from '../../../types/domesticProduct'
import { formatPrice } from './domesticProductsLogic'
import {
  applySetItemColumnPaste,
  buildSetProductPriceSyncPayload,
  calculateSetItemPriceTotals,
  createEmptySetItem,
  type PasteableSetItemField,
} from './setItemsBulkPaste'
import './domesticProducts.css'

/**
 * 套装子项的「商品名称」后端只用于显示、不保存（UpdateSetItemsRequestDto.ProductName），
 * 所以名称列只读，也不再提供批量粘贴入口；可粘贴的只剩两个价格列。
 */
type PasteablePriceField = Exclude<PasteableSetItemField, 'productName'>

export interface SetItemsSavedResult {
  /** 套装子项有价格时，主码对应价格会被同步成合计；没有同步的字段为 undefined。 */
  syncedDomesticPrice?: number
  syncedLabelPrice?: number
}

interface SetItemsModalProps {
  open: boolean
  product: DomesticProductItem | null
  canEdit: boolean
  onClose: () => void
  onSaved: (result: SetItemsSavedResult) => void
}

function createTempId(suffix: string | number = '') {
  return `temp_${Date.now()}_${suffix}${Math.random()}`
}

/** 套装子项编辑弹窗：加载 / 编辑 / 保存都在内部完成，保存成功后通知页面刷新。 */
export default function SetItemsModal({ open, product, canEdit, onClose, onSaved }: SetItemsModalProps) {
  const { t, i18n } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [draft, setDraft] = useState<DomesticProductSetItem[]>([])
  const productId = product?.id
  // 排序只在点列头时排一次，编辑价格不会实时重排；粘贴以排序后的展示顺序为准。
  const { displayItems, sortOrderOf, onTableChange, resetSort } = useSnapshotSort({ items: draft, getId: (item) => item.id })

  useEffect(() => {
    if (!open || !productId) {
      setDraft([])
      resetSort()
      return undefined
    }

    // 弹窗快速关了再开别的商品时，晚回来的旧响应不能覆盖新草稿。
    let cancelled = false
    setLoading(true)
    setDraft([])
    resetSort()
    getDomesticProductSetItems(productId)
      .then((items) => {
        if (!cancelled) {
          setDraft(items)
        }
      })
      .catch((error) => {
        if (!cancelled) {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('domesticProducts.loadSetItemsFailed', '加载套装子项失败'))
          onClose()
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
    // onClose / t / resetSort 的引用不稳定，不应因它们重新请求。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, productId])

  const totals = useMemo(() => calculateSetItemPriceTotals(draft), [draft])

  const changeField = (rowId: string, field: keyof DomesticProductSetItem, value: string | number | undefined) => {
    setDraft((current) => current.map((item) => (item.id === rowId ? { ...item, [field]: value } : item)))
  }

  const pasteColumn = (rowId: string | undefined, field: PasteablePriceField, clipboardText: string) => {
    const result = applySetItemColumnPaste({
      items: displayItems,
      startRowId: rowId,
      field,
      clipboardText,
      createId: (rowIndex) => createTempId(`${rowIndex}_`),
    })
    setDraft(result.items)
    // 提示放在状态更新之外：放进 setState 回调里会在严格模式下重复弹两次。
    if (result.skippedCount) {
      message.warning(t('domesticProducts.setItemsPasteSkipped', '已跳过 {{count}} 个无效价格', { count: result.skippedCount }))
    }
  }

  const handlePaste = (event: ClipboardEvent<HTMLElement>, rowId: string | undefined, field: PasteablePriceField) => {
    if (!canEdit) {
      return
    }
    const clipboardText = event.clipboardData.getData('text')
    if (!clipboardText) {
      return
    }
    event.preventDefault()
    pasteColumn(rowId, field, clipboardText)
  }

  // 列头可聚焦：点击列头后 Ctrl+V 粘贴 Excel 单列，从第一行开始写入，行数不够自动补行。
  const createPasteTitle = (label: string, field: PasteablePriceField) => (
    <span
      className="dp-paste-head"
      tabIndex={canEdit ? 0 : -1}
      // 文字用于聚焦粘贴，不能冒泡成排序；排序由列头空白处/箭头触发。
      onClick={(event) => {
        event.stopPropagation()
        event.currentTarget.focus()
      }}
      onPaste={(event) => handlePaste(event, displayItems[0]?.id, field)}
    >
      {label}
    </span>
  )

  const columns: ColumnsType<DomesticProductSetItem> = [
    {
      title: t('domesticProducts.subItemName'),
      dataIndex: 'productName',
      sorter: true,
      sortOrder: sortOrderOf('productName'),
      ellipsis: true,
      render: (value?: string) => value || <span className="dp-faint">--</span>,
    },
    {
      title: t('domesticProducts.setProductNo', '套装货号'),
      dataIndex: 'setProductNo',
      sorter: true,
      sortOrder: sortOrderOf('setProductNo'),
      width: 170,
      render: (_, record) => (
        <Input
          className="dp-mono"
          value={record.setProductNo}
          disabled={!canEdit}
          onChange={(event) => changeField(record.id, 'setProductNo', event.target.value)}
        />
      ),
    },
    {
      title: t('domesticProducts.barcode', '条码'),
      dataIndex: 'setBarcode',
      sorter: true,
      sortOrder: sortOrderOf('setBarcode'),
      width: 170,
      render: (_, record) => (
        <Input
          className="dp-mono"
          value={record.setBarcode}
          disabled={!canEdit}
          onChange={(event) => changeField(record.id, 'setBarcode', event.target.value)}
        />
      ),
    },
    {
      title: createPasteTitle(t('domesticProducts.domesticPrice', '国内价'), 'domesticPrice'),
      dataIndex: 'domesticPrice',
      sorter: true,
      sortOrder: sortOrderOf('domesticPrice'),
      width: 120,
      render: (_, record) => (
        <div onPaste={(event) => handlePaste(event, record.id, 'domesticPrice')}>
          <InputNumber
            className="dp-number-input"
            min={0}
            precision={2}
            value={record.domesticPrice}
            disabled={!canEdit}
            onChange={(value) => changeField(record.id, 'domesticPrice', value ?? undefined)}
          />
        </div>
      ),
    },
    {
      title: t('domesticProducts.importPrice', '进口价'),
      dataIndex: 'importPrice',
      sorter: true,
      sortOrder: sortOrderOf('importPrice'),
      width: 120,
      render: (_, record) => (
        <InputNumber
          className="dp-number-input"
          min={0}
          precision={2}
          value={record.importPrice}
          disabled={!canEdit}
          onChange={(value) => changeField(record.id, 'importPrice', value ?? undefined)}
        />
      ),
    },
    {
      title: createPasteTitle(t('domesticProducts.oemPrice', '零售价'), 'oemPrice'),
      dataIndex: 'oemPrice',
      sorter: true,
      sortOrder: sortOrderOf('oemPrice'),
      width: 120,
      render: (_, record) => (
        <div onPaste={(event) => handlePaste(event, record.id, 'oemPrice')}>
          <InputNumber
            className="dp-number-input"
            min={0}
            precision={2}
            value={record.oemPrice}
            disabled={!canEdit}
            onChange={(value) => changeField(record.id, 'oemPrice', value ?? undefined)}
          />
        </div>
      ),
    },
    {
      title: t('common.action', '操作'),
      key: 'action',
      width: 76,
      render: (_, record) =>
        canEdit ? (
          <Button danger type="link" onClick={() => setDraft((current) => current.filter((item) => item.id !== record.id))}>
            {t('common.delete', '删除')}
          </Button>
        ) : null,
    },
  ]

  const handleSave = async () => {
    if (!product || saving) {
      return
    }

    try {
      setSaving(true)
      await updateDomesticProductSetItems(product.id, draft)
      const nextTotals = calculateSetItemPriceTotals(draft)

      // 主码价格同步仍复用现有更新接口；没有价格的字段保持主码当前值，不会被覆盖为空。
      const priceSyncPayload = buildSetProductPriceSyncPayload(product, nextTotals)
      if (priceSyncPayload) {
        await updateDomesticProduct(product.id, priceSyncPayload)
      }

      const syncLabels = [
        nextTotals.hasDomesticPrice ? t('domesticProducts.domesticPrice', '国内价') : '',
        nextTotals.hasOemPrice ? t('domesticProducts.oemPrice', '零售价') : '',
      ].filter(Boolean)
      const syncFields = (() => {
        if (syncLabels.length <= 1) {
          return syncLabels[0] || ''
        }

        if (i18n.language.startsWith('zh')) {
          return syncLabels.join('、')
        }

        return `${syncLabels[0]} and ${syncLabels[1]}`
      })()

      message.success(
        syncLabels.length
          ? t('domesticProducts.setItemsUpdatedWithPriceSync', '套装子项已更新，已同步主码{{fields}}合计', { fields: syncFields })
          : t('domesticProducts.setItemsUpdated', '套装子项已更新'),
      )
      onSaved({
        syncedDomesticPrice: nextTotals.hasDomesticPrice ? nextTotals.domesticPriceTotal : undefined,
        syncedLabelPrice: nextTotals.hasOemPrice ? nextTotals.oemPriceTotal : undefined,
      })
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('domesticProducts.saveSetItemsFailed', '保存套装子项失败'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      title={
        product
          ? t('domesticProducts.setItemsTitle', '套装子项 - {{name}}', { name: product.itemNumber || product.name })
          : t('domesticProducts.setItemsTitleShort', '套装子项')
      }
      open={open}
      width={1000}
      destroyOnHidden
      maskClosable={false}
      keyboard={!saving}
      closable={!saving}
      onCancel={onClose}
      onOk={() => void handleSave()}
      okText={t('common.save', '保存')}
      cancelText={t('common.close', '关闭')}
      confirmLoading={saving}
      cancelButtonProps={{ disabled: saving }}
      okButtonProps={{ disabled: !canEdit || loading }}
    >
      <div className="dp-set-hints" data-testid="domestic-product-set-items-modal">
        <Typography.Text type="secondary">{t('domesticProducts.setItemsHint', '仅对套装商品开放编辑，普通商品和多码商品不展示此入口。')}</Typography.Text>
        <Typography.Text type="secondary">{t('domesticProducts.setItemsNameReadonly')}</Typography.Text>
        {canEdit ? <Typography.Text type="secondary">{t('domesticProducts.setItemsPasteHintPrice')}</Typography.Text> : null}
      </div>
      {canEdit ? (
        <Button
          type="dashed"
          style={{ marginBottom: 12 }}
          onClick={() => setDraft((current) => [...current, createEmptySetItem(createTempId())])}
        >
          {t('domesticProducts.addSubItem', '新增子项')}
        </Button>
      ) : null}
      <MeasuredTable
        metricId="domestic-purchase.domestic-products.table-1"
        rowKey="id"
        loading={loading}
        columns={columns}
        dataSource={displayItems}
        onChange={onTableChange}
        showSorterTooltip={false}
        pagination={false}
        scroll={{ x: 980, y: 420 }}
      />
      <div className="dp-set-totals">
        <Space size={20} wrap>
          <Typography.Text strong>
            {t('domesticProducts.setItemsDomesticTotal', '国内价合计')}: {totals.hasDomesticPrice ? formatPrice(totals.domesticPriceTotal) : '--'}
          </Typography.Text>
          <Typography.Text strong>
            {t('domesticProducts.setItemsOemTotal', '零售价合计')}: {totals.hasOemPrice ? formatPrice(totals.oemPriceTotal) : '--'}
          </Typography.Text>
        </Space>
      </div>
      {canEdit ? (
        <Typography.Text type="secondary" style={{ display: 'block', marginTop: 8, textAlign: 'right', fontSize: 12 }}>
          {t('domesticProducts.setItemsSyncHint')}
        </Typography.Text>
      ) : null}
    </Modal>
  )
}
