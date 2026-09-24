import {
  DeleteOutlined,
  DeleteTwoTone,
  ShoppingCartOutlined,
} from '@ant-design/icons'
import {
  Avatar,
  Button,
  Drawer,
  Empty,
  Input,
  InputNumber,
  List,
  Modal,
  Pagination,
  Popconfirm,
  Space,
  Spin,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  clearActiveStoreOrderCart,
  removeStoreOrderCartItem,
  submitActiveStoreOrder,
  updateStoreOrderCartItem,
} from '../services/storeOrderService'
import type { StoreOrderCart, SubmitStoreOrderResult } from '../types/storeOrder'
import { isPreorderRequiredError } from '../services/preorderService'
import { getSupplyPausedSubmitLabels } from '../services/supplyNoticeService'
import { registerPageMessages } from '../i18n/registerPageMessages'
import { supplyStatusCardMessages } from './SupplyNotice/supplyNoticeMessages'
import {
  splitLabelsForDisplay,
  summarizeCartForSubmit,
  summarizeKeptLines,
  type CartSubmitSummary,
} from './SupplyNotice/cartSubmitSummary'

registerPageMessages(supplyStatusCardMessages)

const { Text, Title } = Typography

type Translate = (key: string, options?: Record<string, unknown>) => string

/** 货号列表拼成一句话：最多列 10 个，其余折叠成「等 N 个」。 */
function formatSubmitLabels(labels: string[], t: Translate): string {
  const { shown, more } = splitLabelsForDisplay(labels)
  const joined = shown.join(t('supplyStatusCard.labelSeparator'))
  return more > 0 ? `${joined}${t('supplyStatusCard.labelMore', { count: more })}` : joined
}

/** 提交前确认与提交后结果共用的「保留 / 需删除」两行说明。 */
function renderKeptSummary(
  summary: CartSubmitSummary,
  keys: { paused: string; discontinued: string },
  t: Translate,
) {
  return (
    <>
      {summary.pausedLabels.length ? (
        <p>
          {t(keys.paused, {
            count: summary.pausedLabels.length,
            labels: formatSubmitLabels(summary.pausedLabels, t),
          })}
        </p>
      ) : null}
      {summary.discontinuedLabels.length ? (
        <p>
          <Text type="danger">
            {t(keys.discontinued, {
              count: summary.discontinuedLabels.length,
              labels: formatSubmitLabels(summary.discontinuedLabels, t),
            })}
          </Text>
        </p>
      ) : null}
    </>
  )
}

interface ShopCartDrawerProps {
  open: boolean
  onClose: () => void
  cart: StoreOrderCart | null
  loading?: boolean
  onCartChanged: () => Promise<void>
  preorderBlocked?: boolean
  onOpenPreorder?: () => void
  onPreorderRequired?: () => Promise<void>
}

export default function ShopCartDrawer({
  open,
  onClose,
  cart,
  loading = false,
  onCartChanged,
  preorderBlocked = false,
  onOpenPreorder,
  onPreorderRequired,
}: ShopCartDrawerProps) {
  const { t } = useTranslation()
  const cartItems = cart?.items ?? []
  const totalQuantity = cart?.totalQuantity ?? 0
  const totalImportAmount = cart?.totalImportAmount ?? 0
  const isSummaryOnly = Boolean(cart?.isSummaryOnly)
  const hasCartSummary = Boolean(totalQuantity || cart?.totalSKU || totalImportAmount)
  const isCartDetailLoading = loading || (isSummaryOnly && hasCartSummary)
  const canSubmitCart = !isCartDetailLoading && cartItems.length > 0
  // 提交前按当前购物车数据预估：哪些行进单、哪些暂停供货的行会保留、哪些已不再供应要删。
  const submitSummary = useMemo(() => summarizeCartForSubmit(cartItems), [cartItems])
  const [loadingMap, setLoadingMap] = useState<Record<string, boolean>>({})
  const [remarks, setRemarks] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)

  useEffect(() => {
    if (!cartItems.length) {
      setCurrentPage(1)
      return
    }

    const totalPages = Math.ceil(cartItems.length / pageSize)
    setCurrentPage(totalPages || 1)
  }, [cartItems, pageSize])

  const paginatedItems = useMemo(() => {
    if (!cartItems.length) {
      return []
    }

    const startIndex = (currentPage - 1) * pageSize
    return cartItems.slice(startIndex, startIndex + pageSize)
  }, [cartItems, currentPage, pageSize])

  const handleRemove = async (detailGUID: string) => {
    if (!cart?.storeCode) {
      return
    }

    setLoadingMap((prev) => ({ ...prev, [detailGUID]: true }))
    try {
      await removeStoreOrderCartItem({
        storeCode: cart.storeCode,
        detailGUID,
      })
      message.success(t('shop.cartItemRemoved', 'Item removed'))
      await onCartChanged()
    } catch (error) {
      message.error(t('shop.cartRemoveFailed', 'Failed to remove item'))
    } finally {
      setLoadingMap((prev) => ({ ...prev, [detailGUID]: false }))
    }
  }

  const handleUpdateQuantity = async (
    productCode: string,
    quantity: number,
    minOrderQuantity: number,
    detailGUID: string,
  ) => {
    if (!cart?.storeCode) {
      return
    }

    setLoadingMap((prev) => ({ ...prev, [detailGUID]: true }))
    try {
      await updateStoreOrderCartItem({
        storeCode: cart.storeCode,
        productCode,
        quantity: quantity || minOrderQuantity || 1,
      })
      await onCartChanged()
    } catch {
      message.error(t('shop.cartUpdateFailed', 'Failed to update quantity'))
    } finally {
      setLoadingMap((prev) => ({ ...prev, [detailGUID]: false }))
    }
  }

  const handleSubmitOrder = async () => {
    if (!cart?.storeCode || preorderBlocked) {
      return
    }

    setSubmitting(true)
    try {
      const result: SubmitStoreOrderResult = await submitActiveStoreOrder({
        storeCode: cart.storeCode,
        remarks: remarks.trim() || undefined,
      })
      if (result.keptLines.length === 0) {
        message.success(t('shop.orderSubmitted', 'Order submitted successfully'))
        setRemarks('')
        await onCartChanged()
        onClose()
        return
      }

      // 服务端只提交了在供货的行，暂停供货的行留在购物车里：刷新后让店员留在抽屉里处理剩余行，
      // 用弹窗列出货号（message 几秒就消失，多个货号看不清）。
      setRemarks('')
      await onCartChanged()
      const keptSummary = summarizeKeptLines(result.keptLines, result.submittedLineCount)
      Modal.warning({
        title: t('supplyStatusCard.keptResultTitle'),
        okText: t('supplyStatusCard.acknowledge'),
        content: (
          <div>
            <p>{t('supplyStatusCard.keptResultSubmitted', { count: result.submittedLineCount })}</p>
            {renderKeptSummary(
              keptSummary,
              { paused: 'supplyStatusCard.keptResultPaused', discontinued: 'supplyStatusCard.keptResultDiscontinued' },
              t,
            )}
          </div>
        ),
      })
    } catch (error) {
      const pausedLabels = getSupplyPausedSubmitLabels(error)
      if (isPreorderRequiredError(error)) {
        // 先给用户可见反馈，再刷新门禁；即使刷新响应畸形也不会静默循环。
        message.warning(t('shop.preorder.submitRequiredWarning'))
        await onPreorderRequired?.()
      } else if (pausedLabels) {
        // 购物车里没有一行能进单（全部暂停供货）：刷新购物车让标记显示出来；不自动删除。
        message.warning(t('supplyStatusCard.submitNothingOrderable'))
        await onCartChanged()
      } else message.error(t('shop.orderSubmitFailed', 'Failed to submit order'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleSubmitWithConfirm = () => {
    if (!cart?.storeCode) {
      return
    }
    if (preorderBlocked) {
      message.warning(t('shop.preorder.submitRequiredWarning'))
      return
    }
    if (submitSummary.submittableCount === 0) {
      // 全部行都暂停供货：不用请求服务端也知道提交不了。
      message.warning(t('supplyStatusCard.submitNothingOrderable'))
      return
    }

    const hasKeptLines = submitSummary.pausedLabels.length > 0 || submitSummary.discontinuedLabels.length > 0
    Modal.confirm({
      title: t('shop.confirmOrderSubmission', 'Confirm Order Submission'),
      content: (
        <div>
          <p>
            {t('common.store', 'Store')}: <strong>{cart.storeName || cart.storeCode}</strong>
          </p>
          <p>
            {t('shop.totalQuantity', 'Total Quantity')}:{' '}
            <strong>{hasKeptLines ? submitSummary.submittableQuantity : cart.totalQuantity}</strong>
          </p>
          <p>
            {t('shop.estimatedTotal', 'Estimated Total')}:{' '}
            <strong>${(hasKeptLines ? submitSummary.submittableImportAmount : cart.totalImportAmount).toFixed(2)}</strong>
          </p>
          {hasKeptLines ? (
            <>
              <p>{t('supplyStatusCard.confirmSubmitCount', { count: submitSummary.submittableCount })}</p>
              {renderKeptSummary(
                submitSummary,
                { paused: 'supplyStatusCard.confirmKeptPaused', discontinued: 'supplyStatusCard.confirmDiscontinued' },
                t,
              )}
            </>
          ) : null}
          {remarks.trim() ? (
            <p>
              {t('common.remarks', 'Remarks')}: <em>{remarks.trim()}</em>
            </p>
          ) : null}
        </div>
      ),
      okText: t('shop.submitOrder', 'Submit Order'),
      cancelText: t('common.cancel', 'Cancel'),
      onOk: () => {
        void handleSubmitOrder()
      },
    })
  }

  const handleClearCart = () => {
    if (!cart?.storeCode || !cartItems.length) {
      return
    }

    Modal.confirm({
      title: t('shop.clearCart', 'Clear Cart'),
      content: t('shop.clearCartConfirm', 'Remove all {{count}} items from the cart?', { count: cartItems.length }),
      okText: t('shop.clearCart', 'Clear Cart'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel', 'Cancel'),
      onOk: async () => {
        try {
          const storeCode = cart.storeCode
          if (!storeCode) {
            return
          }

          await clearActiveStoreOrderCart(storeCode)
          message.success(t('shop.cartCleared', 'Cart cleared'))
          await onCartChanged()
        } catch (error) {
          message.error(t('shop.cartClearFailed', 'Failed to clear cart'))
        }
      },
    })
  }

  return (
    <Drawer
      title={
        <Space>
          <ShoppingCartOutlined />
          <span>{t('shop.shoppingCart', 'Shopping Cart')}</span>
          {cart?.totalQuantity ? (
            <span className="shop-cart-drawer-title-note">({t('shop.itemsCount', '{{count}} items', { count: cart.totalQuantity })})</span>
          ) : null}
          {cartItems.length ? (
            <Button
              type="text"
              size="small"
              danger
              icon={<DeleteTwoTone twoToneColor="#cf1322" />}
              onClick={handleClearCart}
            >
              {t('common.clear', 'Clear')}
            </Button>
          ) : null}
        </Space>
      }
      placement="right"
      onClose={onClose}
      open={open}
      width={460}
      extra={<Button onClick={onClose}>{t('common.close', 'Close')}</Button>}
      footer={
        cartItems.length || hasCartSummary ? (
          <div className="shop-cart-drawer-footer">
            <div className="shop-cart-drawer-total-row">
              <Text>{t('shop.totalQuantity', 'Total Quantity')}</Text>
              <Text strong>{totalQuantity}</Text>
            </div>
            <div className="shop-cart-drawer-total-row">
              <Text>{t('shop.estimatedTotal', 'Estimated Total')}</Text>
              <Title level={4} className="shop-cart-drawer-amount">
                ${totalImportAmount.toFixed(2)}
              </Title>
            </div>
            <Input.TextArea
              placeholder={t('shop.remarksPlaceholder', 'Remarks (optional)')}
              value={remarks}
              onChange={(event) => setRemarks(event.target.value)}
              rows={2}
              maxLength={500}
              showCount
              disabled={!canSubmitCart || submitting}
            />
            {preorderBlocked ? (
              <Button block type="primary" ghost onClick={onOpenPreorder} disabled={!onOpenPreorder}>
                {t('shop.preorder.completeBeforeSubmit')}
              </Button>
            ) : null}
            <Button
              type="primary"
              size="large"
              block
              onClick={handleSubmitWithConfirm}
              loading={submitting || isCartDetailLoading}
              disabled={!canSubmitCart || preorderBlocked}
            >
              {t('shop.submitOrder', 'Submit Order')}
            </Button>
          </div>
        ) : null
      }
    >
      {cartItems.length ? (
        <>
          <List
            itemLayout="horizontal"
            dataSource={paginatedItems}
            renderItem={(item, index) => {
              const startIndex = (currentPage - 1) * pageSize
              const globalIndex = startIndex + index

              return (
                <List.Item
                  actions={[
                    <Popconfirm
                      key={item.detailGUID}
                      title={t('shop.removeItem', 'Remove Item')}
                      description={t('shop.removeItemConfirm', 'Remove this item from the cart?')}
                      onConfirm={() => {
                        void handleRemove(item.detailGUID)
                      }}
                      okText={t('common.remove', 'Remove')}
                      cancelText={t('common.cancel', 'Cancel')}
                    >
                      <Button
                        type="text"
                        danger
                        icon={<DeleteOutlined />}
                        loading={loadingMap[item.detailGUID]}
                      />
                    </Popconfirm>,
                  ]}
                >
                  <div className="shop-cart-drawer-index">{globalIndex + 1}.</div>
                  <List.Item.Meta
                    avatar={
                      <Avatar
                        src={item.productImage}
                        shape="square"
                        size={64}
                        style={{ border: '1px solid #f0f0f0' }}
                      />
                    }
                    title={
                      <Space size={4}>
                        <Text ellipsis style={{ width: item.isActive === false ? 120 : 180 }} strong>
                          {item.productName}
                        </Text>
                        {item.isActive === false ? (
                          item.supplyPlan === 'Discontinued' ? (
                            <Tooltip title={t('supplyStatusCard.cartDiscontinuedHint')}>
                              <Tag color="error" style={{ marginInlineEnd: 0 }}>{t('supplyStatusCard.cartDiscontinuedTag')}</Tag>
                            </Tooltip>
                          ) : (
                            <Tooltip title={t('supplyStatusCard.cartKeptHint')}>
                              <Tag color="warning" style={{ marginInlineEnd: 0 }}>{t('supplyStatusCard.cartPausedTag')}</Tag>
                            </Tooltip>
                          )
                        ) : null}
                      </Space>
                    }
                    description={
                      <div className="shop-cart-drawer-item-desc">
                        <Text type="secondary" style={{ fontSize: 12 }}>
                          {item.itemNumber}
                        </Text>
                        {item.isActive === false ? (
                          // 手机上没有 hover，Tooltip 看不到，把提示直接写在行里。
                          <Text type={item.supplyPlan === 'Discontinued' ? 'danger' : 'warning'} style={{ fontSize: 12 }}>
                            {t(
                              item.supplyPlan === 'Discontinued'
                                ? 'supplyStatusCard.cartDiscontinuedHint'
                                : 'supplyStatusCard.cartKeptHint',
                            )}
                          </Text>
                        ) : null}
                        <Text type="secondary" style={{ fontSize: 12 }}>
                          {t('shop.importPrice', 'Import')}: ${item.importPrice?.toFixed(2)}
                        </Text>
                        <div className="shop-cart-drawer-item-row">
                          <Space>
                            <Text style={{ fontSize: 12 }}>{t('shop.qty', 'Qty')}:</Text>
                            <InputNumber
                              size="small"
                              min={item.minOrderQuantity || 1}
                              step={item.minOrderQuantity || 1}
                              value={item.quantity}
                              onChange={(value) => {
                                void handleUpdateQuantity(
                                  item.productCode,
                                  Number(value ?? item.minOrderQuantity ?? 1),
                                  item.minOrderQuantity,
                                  item.detailGUID,
                                )
                              }}
                              disabled={loadingMap[item.detailGUID]}
                              style={{ width: 80 }}
                            />
                          </Space>
                          <Text strong>${item.importAmount?.toFixed(2)}</Text>
                        </div>
                      </div>
                    }
                  />
                </List.Item>
              )
            }}
          />
          <div className="shop-cart-drawer-pagination">
            <Pagination
              current={currentPage}
              pageSize={pageSize}
              total={cartItems.length}
              onChange={(page, size) => {
                setCurrentPage(page)
                if (size && size !== pageSize) {
                  setPageSize(size)
                }
              }}
              showSizeChanger
              showQuickJumper
              showTotal={(total, range) => t('shop.paginationRange', { start: range[0], end: range[1], total })}
              pageSizeOptions={['50', '100']}
            />
          </div>
        </>
      ) : isCartDetailLoading ? (
        <div className="shop-cart-drawer-loading">
          <Spin />
          <Text type="secondary">
            {t('shop.loadingCartItems', 'Loading cart items...')}
          </Text>
        </div>
      ) : (
        <Empty description={t('shop.emptyCart', 'Your cart is empty')} image={Empty.PRESENTED_IMAGE_SIMPLE} />
      )}
    </Drawer>
  )
}
