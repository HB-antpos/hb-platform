import {
  ClockCircleOutlined,
  DeleteOutlined,
  ShoppingCartOutlined,
} from '@ant-design/icons'
import { Button, Card, Image, InputNumber, Tooltip, Typography } from 'antd'
import { memo, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { StoreOrderDynamicData, StoreOrderProductItem } from '../../../types/storeOrder'
import { PRODUCT_GRADE_CONFIG } from '../../../types/productGrade'
import { formatOrderHistoryQuantity } from '../orderHistoryQuantity'

const { Paragraph, Text, Title } = Typography

// 来货日期是后端按分店时区截好的纯日期；只取 YYYY-MM-DD 按本地日期构造，避免 new Date 按 UTC 解析后跨时区差一天。
function formatArrivalDate(value?: string | null): string | null {
  const dateOnly = value?.trim().match(/^(\d{4})-(\d{2})-(\d{2})/)
  if (!dateOnly) {
    return null
  }

  return new Date(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3])).toLocaleDateString()
}

interface ProductCardProps {
  product: StoreOrderProductItem
  dynamicData?: StoreOrderDynamicData
  categoryPath?: string
  onCategoryPathClick?: (product: StoreOrderProductItem) => void
  onAddToCart: (product: StoreOrderProductItem, quantity: number) => Promise<void> | void
  onQuantityChange: (product: StoreOrderProductItem, quantity: number) => Promise<void> | void
  onRemoveFromCart?: (product: StoreOrderProductItem) => Promise<void> | void
  onActivityClick?: (product: StoreOrderProductItem) => void
  loading?: boolean
  removing?: boolean
}

function ProductCard({
  product,
  dynamicData,
  categoryPath,
  onCategoryPathClick,
  onAddToCart,
  onQuantityChange,
  onRemoveFromCart,
  onActivityClick,
  loading,
  removing = false,
}: ProductCardProps) {
  const { t } = useTranslation()
  const stepQuantity = product.minOrderQuantity > 0 ? product.minOrderQuantity : 1
  const cartQuantity = dynamicData?.cartQuantity ?? 0
  const [quantity, setQuantity] = useState<number>(0)

  const imageSrc = useMemo(() => {
    return product.productImage || 'https://via.placeholder.com/200x200?text=No+Image'
  }, [product.productImage])

  const gradeColor = product.grade
    ? (PRODUCT_GRADE_CONFIG[product.grade as keyof typeof PRODUCT_GRADE_CONFIG]?.color || '#999')
    : undefined
  const canClickCategoryPath = Boolean(categoryPath && onCategoryPathClick)
  // Sales 只在后端返回数字时显示，0 与负数也显示；null/undefined 隐藏入口。
  const salesQuantity = dynamicData?.salesQuantitySinceLastArrival
  const hasSalesQuantity = typeof salesQuantity === 'number'
  // 最近来货日即「来货后销量」的起算日，与销量同批回填。
  const formattedLastArrivalDate = formatArrivalDate(dynamicData?.lastArrivalDate)
  const lastOrderDate = dynamicData?.lastOrderDate
  const hasLastOrder = Boolean(lastOrderDate)
    || dynamicData?.lastQuantity != null
    || dynamicData?.lastAllocQuantity != null
  const formattedLastOrderDate = lastOrderDate ? new Date(lastOrderDate).toLocaleDateString() : '-'
  // 上次订货与上次来货落在同一天时合并成一个日期，避免同一日期在卡片上重复出现。
  const isSameOrderAndArrivalDate = hasLastOrder && formattedLastArrivalDate === formattedLastOrderDate
  const lastQuantity = dynamicData?.lastQuantity ?? 0
  const lastAllocQuantity = dynamicData?.lastAllocQuantity ?? 0
  const formattedLastQuantity = formatOrderHistoryQuantity(lastQuantity)
  const formattedLastAllocQuantity = formatOrderHistoryQuantity(lastAllocQuantity)

  useEffect(() => {
    setQuantity(cartQuantity)
  }, [cartQuantity, product.productCode])

  const normalizeQuantity = (value: number | null | undefined) => {
    const numericValue = Number(value ?? 0)
    if (!Number.isFinite(numericValue) || numericValue <= 0) {
      return 0
    }

    return Math.floor(numericValue)
  }

  const applyQuantityChange = (nextQuantity: number) => {
    if (removing) {
      return
    }
    const normalizedQuantity = normalizeQuantity(nextQuantity)
    setQuantity(normalizedQuantity)

    if (normalizedQuantity === cartQuantity) {
      return
    }

    // 商品卡数量直接代表购物车数量；0 只在已有购物车数量时提交，用于触发后端删除明细。
    if (normalizedQuantity > 0 || cartQuantity > 0) {
      void onQuantityChange(product, normalizedQuantity)
    }
  }

  const handleAddToCart = () => {
    if (removing) {
      return
    }
    const addQuantity = quantity > 0 ? quantity : stepQuantity
    setQuantity(addQuantity)
    void onAddToCart(product, addQuantity)
  }

  const handleQuickPackQuantity = (packCount: number) => {
    if (removing) {
      return
    }

    // 快捷按钮表示设置总份数，不是在当前数量上累加。
    const quickQuantity = packCount * stepQuantity
    applyQuantityChange(quickQuantity)
  }

  const handleCategoryPathActivate = () => {
    if (!canClickCategoryPath || !onCategoryPathClick) {
      return
    }

    onCategoryPathClick(product)
  }

  const handleOpenActivity = () => {
    onActivityClick?.(product)
  }

  return (
    <article className={`shop-product-card-shell${cartQuantity > 0 ? ' in-cart' : ''}`}>
      {product.grade ? (
        <span className="shop-product-card-grade" style={{ borderColor: gradeColor, color: gradeColor }}>
          Grade {product.grade}
        </span>
      ) : null}

      <Card className="shop-product-card" variant="outlined">
        <div className="shop-product-card-layout">
          <div className="shop-product-card-media">
            <Image
              alt={product.productName}
              src={imageSrc}
              loading="lazy"
              height="100%"
              width="100%"
              style={{ objectFit: 'contain' }}
              preview={{ mask: t('common.preview', 'Preview') }}
              fallback="https://via.placeholder.com/200x200?text=No+Image"
            />
          </div>

          <div className="shop-product-card-content">
            <Paragraph className="shop-product-card-title" ellipsis={{ rows: 2 }}>
              {product.productName}
            </Paragraph>
            <div className="shop-product-card-identifiers">
              <Text type="secondary">Item No: </Text>
              <Text strong copyable>{product.itemNumber}</Text>
            </div>

            {categoryPath ? (
              <Tooltip title={categoryPath}>
                {/* 搜索结果卡片空间有限，分类路径保留两行并通过悬浮显示完整内容。 */}
                <Paragraph
                  className={[
                    'shop-product-category-path',
                    canClickCategoryPath ? 'shop-product-category-path--clickable' : '',
                  ].filter(Boolean).join(' ')}
                  type="secondary"
                  ellipsis={{ rows: 2 }}
                  role={canClickCategoryPath ? 'button' : undefined}
                  tabIndex={canClickCategoryPath ? 0 : undefined}
                  onClick={handleCategoryPathActivate}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault()
                      handleCategoryPathActivate()
                    }
                  }}
                >
                  {categoryPath}
                </Paragraph>
              </Tooltip>
            ) : null}

            {(hasLastOrder || hasSalesQuantity) ? (
              <div
                className="shop-product-last-order shop-product-activity-entry"
                role="button"
                tabIndex={0}
                onClick={handleOpenActivity}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    handleOpenActivity()
                  }
                }}
                aria-label={t('shop.productActivityHistory.entryAria', {
                  order: formattedLastQuantity,
                  send: formattedLastAllocQuantity,
                  sales: salesQuantity ?? 0,
                })}
                title={t('shop.productActivityHistory.entryTitle')}
              >
                {(hasLastOrder || formattedLastArrivalDate) ? (
                  <div className="shop-product-activity-dates">
                    {hasLastOrder ? (
                      <Text type="secondary" className="shop-product-activity-date">
                        <ClockCircleOutlined />{' '}
                        {t(isSameOrderAndArrivalDate
                          ? 'shop.productActivityHistory.cardLastOrderAndArrival'
                          : 'shop.productActivityHistory.lastOrder')}:{' '}
                        {formattedLastOrderDate}
                      </Text>
                    ) : null}
                    {formattedLastArrivalDate && !isSameOrderAndArrivalDate ? (
                      <Text type="secondary" className="shop-product-activity-date">
                        {t('shop.productActivityHistory.cardLastArrival')}: {formattedLastArrivalDate}
                      </Text>
                    ) : null}
                  </div>
                ) : null}
                <div className="shop-product-sales-row">
                  {hasLastOrder ? (
                    <span className="shop-product-activity-order-send">
                      {t('shop.productActivityHistory.orderLabel')} <strong>{formattedLastQuantity}</strong>
                      {' / '}{t('shop.productActivityHistory.sendLabel')} <strong>{formattedLastAllocQuantity}</strong>
                    </span>
                  ) : null}
                  {hasSalesQuantity ? (
                    <span className="shop-product-activity-sales">
                      {t('shop.productActivityHistory.salesSinceArrival')} <strong>{salesQuantity}</strong>
                    </span>
                  ) : null}
                </div>
              </div>
            ) : null}
          </div>

          <div className="shop-product-card-purchase">
            <div className="shop-product-price-row">
              <div className="shop-product-price">
                <Text type="secondary">{t('shop.orderPrice', 'Order price')}</Text>
                <Title level={4}>${product.oemPrice?.toFixed(2)}</Title>
              </div>
              <Text className="shop-product-moq">{t('shop.moq', 'MOQ')} {stepQuantity}</Text>
            </div>

            <div className="shop-product-card-actions">
              <div className="shop-product-card-action-slot shop-product-card-action-slot--left">
                {onRemoveFromCart && cartQuantity > 0 ? (
                  <Button
                    type="text"
                    danger
                    icon={<DeleteOutlined />}
                    onClick={() => void onRemoveFromCart(product)}
                    disabled={loading}
                    size="small"
                    title="Remove from cart"
                    aria-label="Remove product from cart"
                  />
                ) : null}
              </div>
              <div className="shop-product-quantity-stepper">
                <Button
                  size="small"
                  onClick={() => applyQuantityChange(quantity - stepQuantity)}
                  disabled={removing || quantity <= 0}
                  aria-label="Decrease quantity"
                  title="Decrease quantity"
                  className="shop-product-quantity-button"
                >
                  -
                </Button>
                <InputNumber
                  size="small"
                  min={0}
                  precision={0}
                  step={stepQuantity}
                  controls={false}
                  value={quantity}
                  disabled={removing}
                  onChange={(value) => setQuantity(normalizeQuantity(value))}
                  onBlur={() => applyQuantityChange(quantity)}
                  onPressEnter={() => applyQuantityChange(quantity)}
                  className="shop-product-quantity-input"
                />
                <Button
                  size="small"
                  type="default"
                  onClick={() => applyQuantityChange(quantity + stepQuantity)}
                  disabled={removing}
                  aria-label="Increase quantity"
                  title="Increase quantity"
                  className="shop-product-quantity-button"
                >
                  +
                </Button>
              </div>
              {[2, 3, 4].map((packCount) => {
                const quickQuantity = packCount * stepQuantity
                return (
                  <Button
                    key={packCount}
                    size="small"
                    onClick={() => handleQuickPackQuantity(packCount)}
                    disabled={removing}
                    aria-label={`Set total quantity to ${packCount} packs (${quickQuantity})`}
                    title={`Set total quantity to ${packCount} packs (${quickQuantity})`}
                    className="shop-product-quick-pack-button"
                  >
                    {packCount}
                  </Button>
                )
              })}
              <div className="shop-product-card-action-slot shop-product-card-action-slot--right">
                {cartQuantity <= 0 ? (
                  <Button
                    type="primary"
                    size="small"
                    icon={<ShoppingCartOutlined />}
                    onClick={handleAddToCart}
                    loading={loading}
                    disabled={removing}
                    aria-label="Add product to cart"
                    title="Add product to cart"
                    className="shop-product-cart-button"
                  >
                    {t('common.add', 'Add')}
                  </Button>
                ) : (
                  <span className="shop-product-in-cart">{t('shop.inCart', 'In cart')}: {cartQuantity}</span>
                )}
              </div>
            </div>
          </div>
        </div>
      </Card>
    </article>
  )
}

export default memo(ProductCard)
