import { EditOutlined } from '@ant-design/icons'
import { Button, Drawer, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import BarcodePreview from '../../../components/BarcodePreview'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { getDomesticProductSetItems } from '../../../services/domesticProductService'
import type { DomesticProductItem, DomesticProductSetItem } from '../../../types/domesticProduct'
import { ProductType } from '../../../types/domesticProduct'
import { CopyButton, CopyableText, ProductStatusText, ProductTypeTag } from './ProductCells'
import ProductThumb from './ProductThumb'
import { formatMoney, formatVolume } from './domesticProductsLogic'
import { calculateSetItemPriceTotals } from './setItemsBulkPaste'
import './domesticProducts.css'

interface ProductDetailDrawerProps {
  open: boolean
  product: DomesticProductItem | null
  /** 有新增 / 编辑权限才显示「编辑商品」「套装子项」。 */
  canWrite: boolean
  /** 递增即重新拉取套装子项：套装子项在弹窗里保存后由页面触发。 */
  setItemsVersion: number
  onClose: () => void
  onEdit: (product: DomesticProductItem) => void
  onEditSetItems: (product: DomesticProductItem) => void
}

/** 抽屉头部：缩略图 + 名称 + 类型标签 + 英文名 + 状态。 */
export function ProductDetailHeader({ product }: { product: DomesticProductItem }) {
  return (
    <div className="dp-drawer-head">
      <ProductThumb src={product.productImage} name={product.name} seed={product.itemNumber} size={44} />
      <div className="dp-drawer-head-text">
        <div className="dp-drawer-title">
          <span className="dp-drawer-title-text" title={product.name}>
            {product.name || '--'}
          </span>
          <ProductTypeTag type={product.productType} />
        </div>
        <div className="dp-drawer-meta">
          {product.nameEn ? <span className="dp-sub">{product.nameEn}</span> : null}
          {product.nameEn ? <span className="dp-faint">·</span> : null}
          <ProductStatusText active={product.isActive} />
        </div>
      </div>
    </div>
  )
}

/**
 * 抽屉主体：价格指标 + 标识 + 包装 + 套装子项。
 * 拆成独立组件，便于脱离抽屉单独渲染做冒烟测试（antd Drawer 在 Node 里不渲染 Portal）。
 */
export function ProductDetailBody({
  product,
  setItems,
  setItemsLoading,
  setItemsFailed,
}: {
  product: DomesticProductItem
  setItems: DomesticProductSetItem[]
  setItemsLoading: boolean
  setItemsFailed: boolean
}) {
  const { t } = useTranslation()
  const isSet = product.productType === ProductType.SET
  const totals = useMemo(() => calculateSetItemPriceTotals(setItems), [setItems])

  const subItemColumns: ColumnsType<DomesticProductSetItem> = [
    {
      title: t('domesticProducts.subItemNo'),
      dataIndex: 'setProductNo',
      width: 124,
      render: (value?: string) => (value ? <span className="dp-mono">{value}</span> : <span className="dp-faint">--</span>),
    },
    {
      title: t('domesticProducts.subItemName'),
      dataIndex: 'productName',
      ellipsis: true,
      render: (value?: string) => value || <span className="dp-faint">--</span>,
    },
    {
      title: t('domesticProducts.domesticPrice', '国内价'),
      dataIndex: 'domesticPrice',
      width: 88,
      align: 'right',
      render: (value?: number) => <span className="dp-mini-num">{formatMoney('¥', value)}</span>,
    },
    {
      title: t('domesticProducts.oemPrice', '零售价'),
      dataIndex: 'oemPrice',
      width: 88,
      align: 'right',
      render: (value?: number) => <span className="dp-mini-num">{formatMoney('$', value)}</span>,
    },
  ]

  return (
    <div data-testid="domestic-product-drawer-body">
      <div className="dp-stats">
        <div>
          <div className="dp-stat-key">{t('domesticProducts.domesticPrice', '国内价')}</div>
          <div className="dp-stat-value">{formatMoney('¥', product.domesticPrice)}</div>
        </div>
        <div>
          <div className="dp-stat-key">{t('domesticProducts.oemPrice', '零售价')}</div>
          <div className="dp-stat-value">{formatMoney('$', product.labelPrice)}</div>
        </div>
        <div>
          <div className="dp-stat-key">{t('domesticProducts.importPrice', '进口价')}</div>
          <div className="dp-stat-value">{formatMoney('$', product.importPrice)}</div>
        </div>
      </div>

      <section className="dp-section">
        <h4 className="dp-section-title">{t('domesticProducts.detailSectionIdentity')}</h4>
        <dl className="dp-dl">
          <dt>{t('domesticProducts.hbProductNo', 'HB货号')}</dt>
          <dd>
            <CopyableText value={product.itemNumber} label={t('domesticProducts.hbProductNo', 'HB货号')} />
          </dd>
          <dt>{t('domesticProducts.barcode', '条码')}</dt>
          <dd>
            {product.barcode ? (
              <div className="dp-barcode-row">
                <span className="dp-barcode-box">
                  <BarcodePreview value={product.barcode} showCopy={false} gap={2} options={{ width: 2, height: 44 }} />
                </span>
                <CopyButton value={product.barcode} label={t('domesticProducts.barcode', '条码')} />
              </div>
            ) : (
              <span className="dp-faint">--</span>
            )}
          </dd>
          <dt>{t('domesticProducts.supplier', '供应商')}</dt>
          <dd>
            {product.supplierName || product.supplierCode ? (
              <>
                {product.supplierName}{' '}
                {product.supplierCode ? <span className="dp-mono">{product.supplierCode}</span> : null}
              </>
            ) : (
              <span className="dp-faint">--</span>
            )}
          </dd>
        </dl>
      </section>

      <section className="dp-section">
        <h4 className="dp-section-title">
          {t('domesticProducts.detailSectionPackaging')}
          {product.specs ? (
            <span className="dp-section-sub">{t('domesticProducts.detailSpec', { spec: product.specs })}</span>
          ) : null}
        </h4>
        <div className="dp-grid3">
          <div className="dp-kv">
            <span className="dp-sub">{t('domesticProducts.packingQuantity', '装箱数')}</span>
            <span className="dp-kv-value">{product.packingQty ?? '--'}</span>
          </div>
          <div className="dp-kv">
            <span className="dp-sub">{t('domesticProducts.unitVolumeLabel')}</span>
            <span className="dp-kv-value">
              {product.volume === undefined ? '--' : `${formatVolume(product.volume)} m³`}
            </span>
          </div>
          <div className="dp-kv">
            <span className="dp-sub">{t('domesticProducts.middlePackQuantity', '中包数量')}</span>
            <span className="dp-kv-value">{product.middlePackQty ?? '--'}</span>
          </div>
        </div>
      </section>

      {isSet ? (
        <section className="dp-section" data-testid="domestic-product-drawer-set-items">
          <h4 className="dp-section-title">
            {t('domesticProducts.setItems', '套装子项')}
            {!setItemsLoading && !setItemsFailed ? (
              <span className="dp-section-sub">{t('domesticProducts.detailSetItemsCount', { count: setItems.length })}</span>
            ) : null}
          </h4>
          {setItemsFailed ? (
            <Typography.Text type="danger">{t('domesticProducts.loadSetItemsFailed', '加载套装子项失败')}</Typography.Text>
          ) : (
            <div className="dp-mini">
              <MeasuredTable
                metricId="domestic-purchase.domestic-products.drawer-set-items"
                rowKey="id"
                size="small"
                tableLayout="fixed"
                loading={setItemsLoading}
                columns={subItemColumns}
                dataSource={setItems}
                pagination={false}
                locale={{ emptyText: t('domesticProducts.detailSetItemsEmpty') }}
                summary={() =>
                  setItems.length ? (
                    <MeasuredTable.Summary.Row>
                      <MeasuredTable.Summary.Cell index={0} colSpan={2}>
                        {t('domesticProducts.subItemTotal')}
                      </MeasuredTable.Summary.Cell>
                      <MeasuredTable.Summary.Cell index={2} align="right">
                        <span className="dp-mini-num">{formatMoney('¥', totals.domesticPriceTotal)}</span>
                      </MeasuredTable.Summary.Cell>
                      <MeasuredTable.Summary.Cell index={3} align="right">
                        <span className="dp-mini-num">{formatMoney('$', totals.oemPriceTotal)}</span>
                      </MeasuredTable.Summary.Cell>
                    </MeasuredTable.Summary.Row>
                  ) : null
                }
              />
            </div>
          )}
        </section>
      ) : null}
    </div>
  )
}

/**
 * 商品详情抽屉（宽 700）：头部身份 + 三个价格指标 + 标识（货号 / 条码画布 / 供应商）+ 包装 + 套装子项。
 * 列表里不再放条码画布（每行一个 canvas 又慢又占地方），统一收进这里。
 * 套装子项在列表接口里只有数量没有内容，所以套装商品打开时单独请求一次。
 */
export default function ProductDetailDrawer({
  open,
  product,
  canWrite,
  setItemsVersion,
  onClose,
  onEdit,
  onEditSetItems,
}: ProductDetailDrawerProps) {
  const { t } = useTranslation()
  const [setItems, setSetItems] = useState<DomesticProductSetItem[]>([])
  const [setItemsLoading, setSetItemsLoading] = useState(false)
  const [setItemsFailed, setSetItemsFailed] = useState(false)

  const productId = product?.id
  const isSet = product?.productType === ProductType.SET

  useEffect(() => {
    if (!open || !productId || !isSet) {
      setSetItems([])
      setSetItemsFailed(false)
      return undefined
    }

    // 快速切换商品 / 关闭抽屉时，晚回来的旧响应不能写进新商品的子项表。
    let cancelled = false
    setSetItemsLoading(true)
    setSetItemsFailed(false)
    getDomesticProductSetItems(productId)
      .then((items) => {
        if (!cancelled) {
          setSetItems(items)
        }
      })
      .catch((error) => {
        if (!cancelled) {
          console.error(error)
          setSetItemsFailed(true)
          message.error(error instanceof Error ? error.message : t('domesticProducts.loadSetItemsFailed', '加载套装子项失败'))
        }
      })
      .finally(() => {
        if (!cancelled) {
          setSetItemsLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
    // t 的引用随语言变化，不应因此重新请求。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, productId, isSet, setItemsVersion])

  return (
    <Drawer
      rootClassName="dp-drawer"
      width={700}
      open={open}
      destroyOnHidden
      maskClosable={false}
      onClose={onClose}
      closable={{ placement: 'end' }}
      title={product ? <ProductDetailHeader product={product} /> : null}
      footer={
        product ? (
          <div className="dp-drawer-footer">
            {isSet && canWrite ? (
              <Button icon={<EditOutlined />} onClick={() => onEditSetItems(product)}>
                {t('domesticProducts.setItems', '套装子项')}
              </Button>
            ) : null}
            <span className="dp-drawer-footer-spacer" />
            <Button onClick={onClose}>{t('common.close', '关闭')}</Button>
            {canWrite ? (
              <Button type="primary" icon={<EditOutlined />} onClick={() => onEdit(product)}>
                {t('domesticProducts.detailEdit')}
              </Button>
            ) : null}
          </div>
        ) : null
      }
    >
      {product ? (
        <ProductDetailBody
          product={product}
          setItems={setItems}
          setItemsLoading={setItemsLoading}
          setItemsFailed={setItemsFailed}
        />
      ) : null}
    </Drawer>
  )
}
