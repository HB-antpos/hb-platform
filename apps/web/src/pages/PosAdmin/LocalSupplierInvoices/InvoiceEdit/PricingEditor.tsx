/**
 * 定价弹窗：集中编辑一行的自动定价、定价浮率、新自动零售价、特殊商品和折扣率。
 * 这几个字段改得少，原来各占一列、手势还不统一（自动定价单击、特殊商品双击），
 * 收进一个弹窗后表格省出 4 列；修改仍只写入页面上的明细，由「保存修改」统一落库。
 */
import { Button, InputNumber, Switch, Typography } from 'antd'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'
import { discountRateToPercent } from '../../../../utils/discountRate'
import { buildPricingEditorChanges, type PricingEditorChange } from './pricingEditorChanges'

interface PricingEditorProps {
  detail: LocalSupplierInvoiceItemDto
  onApply: (changes: PricingEditorChange[]) => void
  onCancel: () => void
}

export default function PricingEditor({ detail, onApply, onCancel }: PricingEditorProps) {
  const { t } = useTranslation()
  const [autoPricing, setAutoPricing] = useState(Boolean(detail.autoPricing))
  const [pricingFloatRate, setPricingFloatRate] = useState<number | null>(detail.pricingFloatRate ?? null)
  const [newAutoRetailPrice, setNewAutoRetailPrice] = useState<number | null>(detail.newAutoRetailPrice ?? null)
  const [isSpecialProduct, setIsSpecialProduct] = useState(Boolean(detail.isSpecialProduct))
  // 折扣率空值保持为空，进入编辑时不能被改成 0。
  const [discountPercent, setDiscountPercent] = useState<number | null>(discountRateToPercent(detail.discountRate) ?? null)

  const handleApply = () => {
    onApply(buildPricingEditorChanges(detail, {
      autoPricing,
      pricingFloatRate,
      newAutoRetailPrice,
      isSpecialProduct,
      discountPercent,
    }))
  }

  return (
    <div style={{ width: 270 }} onKeyDown={(event) => event.stopPropagation()}>
      <div className="lsi-wb-pricing-editor">
        <label htmlFor={`pricing-auto-${detail.detailGUID}`}>{t('posAdmin.invoiceWorkbench.autoPricing')}</label>
        <Switch
          id={`pricing-auto-${detail.detailGUID}`}
          checked={autoPricing}
          onChange={setAutoPricing}
          checkedChildren={t('posAdmin.invoiceDetail.auto', '自动')}
          unCheckedChildren={t('posAdmin.invoiceDetail.manual', '手动')}
        />
        <label htmlFor={`pricing-rate-${detail.detailGUID}`}>{t('posAdmin.invoiceWorkbench.floatRate')}</label>
        <InputNumber
          id={`pricing-rate-${detail.detailGUID}`}
          size="small"
          min={0}
          precision={2}
          value={pricingFloatRate}
          onChange={(value) => setPricingFloatRate(value)}
          style={{ width: '100%' }}
        />
        <label htmlFor={`pricing-new-${detail.detailGUID}`}>{t('posAdmin.invoiceWorkbench.newAutoRetailPrice')}</label>
        <InputNumber
          id={`pricing-new-${detail.detailGUID}`}
          size="small"
          min={0}
          precision={2}
          value={newAutoRetailPrice}
          onChange={(value) => setNewAutoRetailPrice(value)}
          style={{ width: '100%' }}
        />
        <label htmlFor={`pricing-special-${detail.detailGUID}`}>{t('posAdmin.invoiceWorkbench.specialProduct')}</label>
        <Switch
          id={`pricing-special-${detail.detailGUID}`}
          checked={isSpecialProduct}
          onChange={setIsSpecialProduct}
          checkedChildren={t('posAdmin.invoiceDetail.yes', '是')}
          unCheckedChildren={t('posAdmin.invoiceDetail.no', '否')}
        />
        <label htmlFor={`pricing-discount-${detail.detailGUID}`}>{t('posAdmin.invoiceWorkbench.discountRate')}</label>
        <InputNumber
          id={`pricing-discount-${detail.detailGUID}`}
          size="small"
          min={0}
          max={100}
          precision={1}
          addonAfter="%"
          value={discountPercent}
          onChange={(value) => setDiscountPercent(value)}
          style={{ width: '100%' }}
        />
      </div>
      <Typography.Paragraph type="secondary" style={{ margin: '10px 0 0', fontSize: 12 }}>
        {t('posAdmin.invoiceWorkbench.pricingEditorHint')}
      </Typography.Paragraph>
      <div className="lsi-wb-pricing-editor-footer">
        <Button size="small" onClick={onCancel}>{t('common.cancel', '取消')}</Button>
        <Button size="small" type="primary" onClick={handleApply}>{t('posAdmin.invoiceWorkbench.apply')}</Button>
      </div>
    </div>
  )
}
