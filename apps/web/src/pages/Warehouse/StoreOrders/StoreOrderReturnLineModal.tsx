import { ScanOutlined } from '@ant-design/icons'
import { Alert, Button, Image, Input, InputNumber, Modal, Spin, message } from 'antd'
import type { InputRef } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  batchAddStoreOrderLines,
  getStoreOrderDetail,
  getStoreOrderDetailFull,
  getStoreOrderDetailProductCodes,
  getStoreOrderProducts,
} from '../../../services/storeOrderService'
import type {
  StoreOrderDetailLine,
  StoreOrderProductItem,
  StoreOrderReturnDisposition,
  StoreOrderReturnReason,
} from '../../../types/storeOrder'
import {
  StoreOrderReturnDispositionSegment,
  StoreOrderReturnNoteInput,
  StoreOrderReturnReasonChips,
} from './StoreOrderReturnFields'
import {
  buildReturnPayloadFields,
  computeReturnLinePreview,
  resolveReturnLineConflict,
  toReturnAllocQuantity,
  type StoreOrderReturnLineConflict,
} from './storeOrderReturnLogic'

const IMAGE_FALLBACK_SRC = 'data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs='
const PRODUCT_SEARCH_PAGE_SIZE = 20

function formatMoney(value: number) {
  const abs = Math.abs(value).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
  return value < 0 ? `−$${abs}` : `$${abs}`
}

interface StoreOrderReturnLineModalProps {
  open: boolean
  orderGUID: string
  orderNo?: string
  storeLabel?: string
  /** 订单当前净额 ex GST（含未保存修改），用于预览「加入后订单净额」。 */
  currentNetAmount: number
  /** 订单是否可编辑；已完成订单只读，不能再加退货行。 */
  editable: boolean
  onCancel: () => void
  /** 退货行写入后（含只写了一半的情况）通知页面刷新明细。 */
  onChanged: () => Promise<void> | void
}

/**
 * 「添加退货商品」弹窗（设计图 ② 入口 A）：
 * 找商品 → 填数量（正数，记为 发货 −N）/ 单价 / 原因 / 处置 / 备注 → 预览冲减金额 → 写入。
 * 与发货格直接输入负数（入口 B）写入同样的数据：一条订货 0、发货为负、带退货原因的明细行。
 *
 * 一步写入：「加入商品」接口（batch-add）收到负数 quantity + 退货字段时直接建出退货行。
 */
export default function StoreOrderReturnLineModal({
  open,
  orderGUID,
  orderNo,
  storeLabel,
  currentNetAmount,
  editable,
  onCancel,
  onChanged,
}: StoreOrderReturnLineModalProps) {
  const { t } = useTranslation()
  const [keyword, setKeyword] = useState('')
  const [searching, setSearching] = useState(false)
  const [candidates, setCandidates] = useState<StoreOrderProductItem[]>([])
  const [searched, setSearched] = useState(false)
  const [product, setProduct] = useState<StoreOrderProductItem | null>(null)
  const [existingLine, setExistingLine] = useState<StoreOrderDetailLine | null>(null)
  const [checkingExisting, setCheckingExisting] = useState(false)
  const [checkFailed, setCheckFailed] = useState(false)
  const [quantity, setQuantity] = useState<number | null>(null)
  const [unitPrice, setUnitPrice] = useState<number | null>(null)
  const [reason, setReason] = useState<StoreOrderReturnReason | null>(null)
  const [disposition, setDisposition] = useState<StoreOrderReturnDisposition>(0)
  const [note, setNote] = useState('')
  const [submitting, setSubmitting] = useState(false)
  // 只采纳最后一次「选商品 → 核对本单已有行」的结果，快速换商品时旧响应不能覆盖新状态。
  const selectRequestRef = useRef(0)
  const keywordInputRef = useRef<InputRef | null>(null)

  const resetProduct = () => {
    selectRequestRef.current += 1
    setProduct(null)
    setExistingLine(null)
    setCheckingExisting(false)
    setCheckFailed(false)
    setQuantity(null)
    setUnitPrice(null)
    setReason(null)
    setDisposition(0)
    setNote('')
  }

  // 每次打开都从空白开始，避免带着上一次的商品和原因。
  useEffect(() => {
    if (!open) {
      return
    }
    resetProduct()
    setKeyword('')
    setCandidates([])
    setSearched(false)
    window.setTimeout(() => keywordInputRef.current?.focus(), 0)
  }, [open])

  const conflict: StoreOrderReturnLineConflict = useMemo(() => resolveReturnLineConflict(existingLine), [existingLine])
  const returnAllocQuantity = toReturnAllocQuantity(quantity)
  const existingReturnAmount =
    conflict === 'existingReturn' && existingLine
      ? Number(existingLine.allocQuantity ?? 0) * Number(existingLine.importPrice ?? 0)
      : 0
  const preview = computeReturnLinePreview({
    quantity,
    unitPrice,
    currentNetAmount,
    existingReturnAmount,
  })
  const hasBlockingConflict = conflict === 'ordered' || conflict === 'shipped'
  const canSubmit =
    editable &&
    Boolean(product) &&
    !checkingExisting &&
    !checkFailed &&
    !hasBlockingConflict &&
    returnAllocQuantity !== null &&
    Number(unitPrice ?? 0) > 0 &&
    reason !== null &&
    !submitting

  const handleSearch = async () => {
    const trimmed = keyword.trim()
    if (!trimmed) {
      return
    }
    setSearching(true)
    try {
      const result = await getStoreOrderProducts({
        // 与「加入商品」弹窗一致：一个搜索框同时覆盖货号 / 条码和商品名称；退货允许下架商品。
        itemNumber: trimmed,
        productName: trimmed,
        includeInactiveWarehouseProducts: true,
        pageNumber: 1,
        pageSize: PRODUCT_SEARCH_PAGE_SIZE,
        sortBy: 'Default',
      })
      setSearched(true)
      const lowered = trimmed.toLowerCase()
      const exact = result.items.filter(
        (item) => item.itemNumber?.trim().toLowerCase() === lowered || item.barcode?.trim().toLowerCase() === lowered,
      )
      // 扫码 / 货号精确命中唯一商品时直接选中，省一步点选。
      if (exact.length === 1) {
        setCandidates([])
        void handleSelectProduct(exact[0])
        return
      }
      setCandidates(result.items)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouseUi.storeOrderDetail.returnModal.addFailed'))
    } finally {
      setSearching(false)
    }
  }

  const handleSelectProduct = async (item: StoreOrderProductItem) => {
    const requestId = selectRequestRef.current + 1
    resetProduct()
    selectRequestRef.current = requestId
    setProduct(item)
    setCandidates([])
    // 单价默认值：现有商品接口取不到「上次发货价」，先回落到进口价（可手改）。
    setUnitPrice(item.importPrice ?? null)
    setCheckingExisting(true)
    try {
      // 一个订单一个商品一行：先确认本单有没有这个商品，再取到那一行的订货 / 发货数量做冲突判定。
      const productCodes = await getStoreOrderDetailProductCodes(orderGUID)
      let line: StoreOrderDetailLine | null = null
      if (productCodes.includes(item.productCode)) {
        const keywordForLookup = item.itemNumber?.trim() || item.barcode?.trim() || item.productName?.trim() || ''
        const page = keywordForLookup
          ? await getStoreOrderDetail(orderGUID, { pageNumber: 1, pageSize: 50, keyword: keywordForLookup })
          : null
        line = page?.items.find((row) => row.productCode === item.productCode) ?? null
        if (!line) {
          // 关键字没命中（极少见）时退回整单明细，宁可慢一点也不能漏判冲突。
          const full = await getStoreOrderDetailFull(orderGUID)
          line = full?.items.find((row) => row.productCode === item.productCode) ?? null
        }
      }
      if (selectRequestRef.current !== requestId) {
        return
      }
      setExistingLine(line)
      // 本单已有退货行：把数量 / 单价 / 原因预填成现状，方便修改。
      if (line && Number(line.allocQuantity ?? 0) < 0) {
        setQuantity(-Number(line.allocQuantity))
        setUnitPrice(line.importPrice ?? item.importPrice ?? null)
        setReason((line.returnReason as StoreOrderReturnReason | null | undefined) ?? null)
        setDisposition(line.returnDisposition === 1 ? 1 : 0)
        setNote(line.returnNote ?? '')
      }
    } catch (error) {
      console.error(error)
      if (selectRequestRef.current === requestId) {
        setCheckFailed(true)
      }
    } finally {
      if (selectRequestRef.current === requestId) {
        setCheckingExisting(false)
      }
    }
  }

  const handleSubmit = async (continueAdding: boolean) => {
    if (!product || returnAllocQuantity === null || reason === null || !canSubmit) {
      return
    }
    setSubmitting(true)
    try {
      // 一步写入：加入商品接口收到负数 quantity + 退货字段时直接建出（或覆盖）退货行，
      // 按设置而不是累加，重复提交不会叠加；有订货 / 已发货的冲突已在上面禁用提交。
      await batchAddStoreOrderLines({
        orderGUID,
        items: [
          {
            productCode: product.productCode,
            quantity: returnAllocQuantity,
            importPrice: Number(unitPrice),
            ...buildReturnPayloadFields(returnAllocQuantity, {
              returnReason: reason,
              returnDisposition: disposition,
              returnNote: note,
            }),
          },
        ],
      })
      message.success(t('warehouseUi.storeOrderDetail.returnModal.added'))
      await onChanged()
      if (continueAdding) {
        resetProduct()
        setKeyword('')
        setCandidates([])
        setSearched(false)
        window.setTimeout(() => keywordInputRef.current?.focus(), 0)
      } else {
        onCancel()
      }
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error && error.message ? error.message : t('warehouseUi.storeOrderDetail.returnModal.addFailed'))
    } finally {
      setSubmitting(false)
    }
  }

  const conflictAlert =
    conflict === 'ordered' ? (
      <Alert
        type="error"
        showIcon
        message={t('warehouseUi.storeOrderDetail.returnModal.conflictOrdered', { qty: Number(existingLine?.quantity ?? 0) })}
      />
    ) : conflict === 'shipped' ? (
      <Alert
        type="error"
        showIcon
        message={t('warehouseUi.storeOrderDetail.returnModal.conflictShipped', { qty: Number(existingLine?.allocQuantity ?? 0) })}
      />
    ) : conflict === 'existingReturn' ? (
      <Alert
        type="warning"
        showIcon
        message={t('warehouseUi.storeOrderDetail.returnModal.existingReturn', { qty: -Number(existingLine?.allocQuantity ?? 0) })}
      />
    ) : checkFailed ? (
      <Alert type="error" showIcon message={t('warehouseUi.storeOrderDetail.returnModal.checkFailed')} />
    ) : null

  return (
    <Modal
      open={open}
      width={640}
      destroyOnHidden
      maskClosable={false}
      className="wh-order-return-modal"
      onCancel={submitting ? undefined : onCancel}
      title={
        <span className="wh-order-return-modal-title">
          {t('warehouseUi.storeOrderDetail.returnModal.title')}
          <span className="wh-order-return-modal-subtitle">
            {t('warehouseUi.storeOrderDetail.returnModal.subtitle', { store: storeLabel || '--', orderNo: orderNo || '--' })}
          </span>
        </span>
      }
      footer={[
        <Button key="cancel" disabled={submitting} onClick={onCancel}>
          {t('warehouseUi.storeOrderDetail.returnModal.cancel')}
        </Button>,
        <Button key="continue" disabled={!canSubmit} loading={submitting} onClick={() => void handleSubmit(true)}>
          {t('warehouseUi.storeOrderDetail.returnModal.addAndContinue')}
        </Button>,
        <Button key="add" type="primary" danger disabled={!canSubmit} loading={submitting} onClick={() => void handleSubmit(false)}>
          {t('warehouseUi.storeOrderDetail.returnModal.add')}
        </Button>,
      ]}
    >
      <div className="wh-order-return-form">
        {!editable ? (
          <Alert type="info" showIcon message={t('warehouseUi.storeOrderDetail.returnModal.orderReadonly')} />
        ) : null}

        <div className="wh-order-return-field">
          <div className="wh-order-return-label">{t('warehouseUi.storeOrderDetail.returnModal.product')}</div>
          {product ? (
            <div className="wh-order-return-product">
              <Image
                src={product.productImage}
                alt={product.productName}
                width={44}
                height={44}
                preview={false}
                style={{ borderRadius: 6, objectFit: 'cover' }}
                fallback={IMAGE_FALLBACK_SRC}
              />
              <div className="wh-order-return-product-text">
                <div className="wh-order-return-product-name">
                  {product.itemNumber ? <span className="wh-order-return-product-code">{product.itemNumber}</span> : null}
                  {product.productName || '--'}
                </div>
                <div className="wh-order-return-product-sub">{product.barcode || product.productCode}</div>
              </div>
              <Button size="small" disabled={submitting} onClick={() => { resetProduct(); setSearched(false) }}>
                {t('warehouseUi.storeOrderDetail.returnModal.change')}
              </Button>
            </div>
          ) : (
            <>
              <Input.Search
                ref={keywordInputRef}
                allowClear
                prefix={<ScanOutlined />}
                value={keyword}
                loading={searching}
                disabled={!editable}
                placeholder={t('warehouseUi.storeOrderDetail.returnModal.searchPlaceholder')}
                enterButton={t('warehouseUi.storeOrderDetail.returnModal.search')}
                onChange={(event) => setKeyword(event.target.value)}
                onSearch={() => void handleSearch()}
              />
              {searched && candidates.length === 0 && !searching ? (
                <div className="wh-order-return-help">{t('warehouseUi.storeOrderDetail.returnModal.noResult')}</div>
              ) : null}
              {candidates.length > 0 ? (
                <>
                  <div className="wh-order-return-help">
                    {t('warehouseUi.storeOrderDetail.returnModal.pickOne', { count: candidates.length })}
                  </div>
                  <ul className="wh-order-return-candidates">
                    {candidates.map((item) => (
                      <li key={item.productCode}>
                        <button type="button" onClick={() => void handleSelectProduct(item)}>
                          <span className="wh-order-return-product-code">{item.itemNumber || '--'}</span>
                          <span className="wh-order-return-candidate-name">{item.productName || '--'}</span>
                          <span className="wh-order-return-candidate-sub">{item.barcode || item.productCode}</span>
                        </button>
                      </li>
                    ))}
                  </ul>
                </>
              ) : null}
            </>
          )}
        </div>

        {product ? (
          <>
            {checkingExisting ? (
              <div className="wh-order-return-help">
                <Spin size="small" /> {t('warehouseUi.storeOrderDetail.returnModal.checkingExisting')}
              </div>
            ) : null}
            {conflictAlert}

            <div className="wh-order-return-row2">
              <div className="wh-order-return-field">
                <div className="wh-order-return-label">
                  {t('warehouseUi.storeOrderDetail.returnModal.quantity')} <span className="wh-order-return-req">*</span>
                </div>
                <div className="wh-order-return-qty-line">
                  <InputNumber
                    min={1}
                    precision={0}
                    value={quantity}
                    disabled={!editable || hasBlockingConflict}
                    aria-label={t('warehouseUi.storeOrderDetail.returnModal.quantity')}
                    style={{ width: 120 }}
                    onChange={(value) => setQuantity(value === null ? null : Number(value))}
                  />
                  {returnAllocQuantity !== null ? (
                    <span className="wh-order-return-as">
                      {t('warehouseUi.storeOrderDetail.returnModal.asShip', { qty: `−${Math.abs(returnAllocQuantity)}` })}
                    </span>
                  ) : null}
                </div>
              </div>
              <div className="wh-order-return-field">
                <div className="wh-order-return-label">
                  {t('warehouseUi.storeOrderDetail.returnModal.unitPrice')}
                  <span className="wh-order-return-hint">{t('warehouseUi.storeOrderDetail.returnModal.unitPriceHint')}</span>
                </div>
                <InputNumber
                  min={0}
                  precision={2}
                  prefix="$"
                  value={unitPrice}
                  disabled={!editable || hasBlockingConflict}
                  aria-label={t('warehouseUi.storeOrderDetail.returnModal.unitPrice')}
                  style={{ width: 150 }}
                  onChange={(value) => setUnitPrice(value === null ? null : Number(value))}
                />
              </div>
            </div>

            <div className="wh-order-return-field">
              <div className="wh-order-return-label">
                {t('warehouseUi.storeOrderDetail.returnModal.reason')} <span className="wh-order-return-req">*</span>
              </div>
              <StoreOrderReturnReasonChips
                t={t}
                value={reason}
                disabled={!editable || hasBlockingConflict}
                ariaLabel={t('warehouseUi.storeOrderDetail.returnModal.reason')}
                onChange={setReason}
              />
            </div>

            <div className="wh-order-return-field">
              <div className="wh-order-return-label">
                {t('warehouseUi.storeOrderDetail.returnModal.disposition')}
                <span className="wh-order-return-hint">{t('warehouseUi.storeOrderDetail.returnModal.dispositionHint')}</span>
              </div>
              <StoreOrderReturnDispositionSegment
                t={t}
                value={disposition}
                disabled={!editable || hasBlockingConflict}
                ariaLabel={t('warehouseUi.storeOrderDetail.returnModal.disposition')}
                onChange={setDisposition}
              />
              <div className="wh-order-return-help">{t('warehouseUi.storeOrderDetail.returnModal.dispositionHelp')}</div>
            </div>

            <div className="wh-order-return-field">
              <div className="wh-order-return-label">
                {t('warehouseUi.storeOrderDetail.returnModal.note')}
                <span className="wh-order-return-hint">{t('warehouseUi.storeOrderDetail.returnModal.optional')}</span>
              </div>
              <StoreOrderReturnNoteInput
                value={note}
                disabled={!editable || hasBlockingConflict}
                placeholder={t('warehouseUi.storeOrderDetail.returnModal.notePlaceholder')}
                onChange={setNote}
              />
            </div>

            <div className="wh-order-return-preview" aria-live="polite">
              <div>
                <small>{t('warehouseUi.storeOrderDetail.returnModal.previewExGst')}</small>
                <b>{formatMoney(preview.amount)}</b>
              </div>
              <div>
                <small>{t('warehouseUi.storeOrderDetail.returnModal.previewGst')}</small>
                <b>{formatMoney(preview.gst)}</b>
              </div>
              <div>
                <small>{t('warehouseUi.storeOrderDetail.returnModal.previewTotal')}</small>
                <b>{formatMoney(preview.total)}</b>
              </div>
              <span className="wh-order-detail-spacer" />
              <small className="wh-order-return-preview-net">
                {t('warehouseUi.storeOrderDetail.returnModal.previewNetAfter', { net: formatMoney(preview.netAfter) })}
              </small>
            </div>
          </>
        ) : null}
      </div>
    </Modal>
  )
}
