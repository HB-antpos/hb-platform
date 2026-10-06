import { Alert, Button, Form, Image, Input, InputNumber, Modal, Select, Switch, message } from 'antd'
import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { createDomesticProduct, updateDomesticProduct } from '../../../services/domesticProductService'
import type { DomesticProductItem, SupplierOption } from '../../../types/domesticProduct'
import { ProductType } from '../../../types/domesticProduct'
import { toProductThumbnailUrl } from '../../../utils/productImageThumbnail'
import {
  FIELD_MAX_LENGTH,
  buildCreatePayload,
  buildSupplierOptions,
  buildUpdatePayload,
  countChangedFields,
  emptyProductFormValues,
  filterSupplierOption,
  productToFormValues,
  type ProductFormValues,
} from './domesticProductsLogic'
import './domesticProducts.css'

export type ProductSavedResult =
  | { mode: 'create' }
  | { mode: 'edit'; product: DomesticProductItem; values: ProductFormValues }

interface ProductFormModalProps {
  open: boolean
  /** null 表示新建；否则编辑该商品。 */
  product: DomesticProductItem | null
  suppliers: SupplierOption[]
  onClose: () => void
  /** 保存成功后回调：由页面决定怎么刷新列表和详情抽屉。 */
  onSaved: (result: ProductSavedResult) => void
}

/** 标签右侧的字数计数 `14 / 200`：超出上限即时变红，不必等到提交后才收到 400。 */
function CountedLabel({ name, label, max }: { name: keyof ProductFormValues; label: ReactNode; max: number }) {
  return (
    <span className="dp-label">
      <span>{label}</span>
      <Form.Item noStyle shouldUpdate={(previous, next) => previous[name] !== next[name]}>
        {({ getFieldValue }) => {
          const length = String(getFieldValue(name) ?? '').length
          return (
            <span className={length > max ? 'dp-count dp-count-over' : 'dp-count'}>
              {length} / {max}
            </span>
          )
        }}
      </Form.Item>
    </span>
  )
}

/** 带说明的整行开关：Form.Item 通过 valuePropName="checked" 注入 checked / onChange。 */
function StatusSwitch({ checked, onChange }: { checked?: boolean; onChange?: (checked: boolean) => void }) {
  const { t } = useTranslation()
  return (
    <div className="dp-switch-row">
      <span>{t('domesticProducts.formStatusHint')}</span>
      <Switch
        checked={checked}
        onChange={onChange}
        checkedChildren={t('common.enable', '启用')}
        unCheckedChildren={t('common.disable', '停用')}
      />
    </div>
  )
}

/**
 * 图片链接预览：输入时防抖 400ms 再加载，避免每敲一个字符就向外站发一次请求；
 * 缩略图可点击放大到原图，链接无效时给出明确提示而不是一个破图。
 */
function ImageUrlPreview({ url }: { url?: string }) {
  const { t } = useTranslation()
  const trimmed = url?.trim() ?? ''
  const [debounced, setDebounced] = useState(trimmed)
  const [failedUrl, setFailedUrl] = useState<string>()

  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(trimmed), 400)
    return () => window.clearTimeout(timer)
  }, [trimmed])

  if (!debounced) {
    return <div className="dp-photo">{t('domesticProducts.formImagePlaceholder')}</div>
  }
  if (failedUrl === debounced) {
    return <div className="dp-photo dp-photo-error">{t('domesticProducts.formImageLoadFailed')}</div>
  }
  return (
    <div className="dp-photo">
      <Image
        key={debounced}
        src={toProductThumbnailUrl(debounced, 96) ?? debounced}
        width={24}
        height={24}
        alt=""
        preview={{ src: debounced, mask: '' }}
        onError={() => setFailedUrl(debounced)}
      />
      <span>{t('domesticProducts.formImageZoomHint')}</span>
    </div>
  )
}

/**
 * 表单主体（三个分区 + 编辑时的只读提示）。拆成独立组件，便于脱离弹窗单独渲染做冒烟测试：
 * antd Modal 在 Node 里不渲染 Portal，直接测弹窗什么都看不到。
 * 必须放在 <Form> 内部使用。
 */
export function ProductFormFields({ isEdit, suppliers }: { isEdit: boolean; suppliers: SupplierOption[] }) {
  const { t } = useTranslation()
  const supplierOptions = useMemo(() => buildSupplierOptions(suppliers), [suppliers])

  const productTypeOptions = [
    { value: ProductType.NORMAL, label: t('common.normalProduct', '普通商品') },
    { value: ProductType.SET, label: t('common.setProduct', '套装商品') },
    { value: ProductType.MULTICODE, label: t('common.multiCodeProduct', '多码商品') },
  ]

  const maxLengthRule = (max: number) => ({ max, message: t('domesticProducts.formMaxLength', { max }) })
  const quantityRules = [{ type: 'number' as const, min: 1, message: t('domesticProducts.formQuantityMin') }]

  return (
    <>
      <section className="dp-form-section">
        <h4 className="dp-section-title">{t('domesticProducts.formSectionBasic')}</h4>
        <div className="dp-grid2">
          {!isEdit ? (
            <Form.Item
              className="dp-span-all"
              name="supplierCode"
              label={t('domesticProducts.supplier', '供应商')}
              rules={[{ required: true, message: t('domesticProducts.selectSupplier', '请选择供应商') }]}
            >
              <Select
                showSearch
                placeholder={t('domesticProducts.selectSupplier', '请选择供应商')}
                filterOption={filterSupplierOption}
                options={supplierOptions}
              />
            </Form.Item>
          ) : null}
          <Form.Item
            name="productName"
            label={
              <CountedLabel
                name="productName"
                label={t('domesticProducts.productName', '商品名称')}
                max={FIELD_MAX_LENGTH.productName}
              />
            }
            rules={[
              { required: true, whitespace: true, message: t('domesticProducts.enterProductName', '请输入商品名称') },
              maxLengthRule(FIELD_MAX_LENGTH.productName),
            ]}
          >
            <Input placeholder={t('domesticProducts.enterProductName', '请输入商品名称')} />
          </Form.Item>
          <Form.Item
            name="englishProductName"
            label={
              <CountedLabel
                name="englishProductName"
                label={t('domesticProducts.englishName', '英文名称')}
                max={FIELD_MAX_LENGTH.englishProductName}
              />
            }
            rules={[maxLengthRule(FIELD_MAX_LENGTH.englishProductName)]}
          >
            <Input placeholder={t('domesticProducts.enterEnglishName', '请输入英文名称')} />
          </Form.Item>
          <Form.Item
            name="productType"
            label={t('domesticProducts.productType', '商品类型')}
            rules={[{ required: true, message: t('domesticProducts.selectProductType', '请选择商品类型') }]}
          >
            <Select placeholder={t('domesticProducts.selectProductType', '请选择商品类型')} options={productTypeOptions} />
          </Form.Item>
          <Form.Item
            name="productSpecification"
            label={t('domesticProducts.specification', '规格')}
            rules={[maxLengthRule(FIELD_MAX_LENGTH.productSpecification)]}
          >
            <Input placeholder={t('domesticProducts.enterSpec', '请输入商品规格')} />
          </Form.Item>
          {!isEdit ? (
            <>
              <Form.Item
                name="hbProductNo"
                label={t('domesticProducts.hbProductNo', 'HB货号')}
                rules={[maxLengthRule(FIELD_MAX_LENGTH.hbProductNo)]}
              >
                <Input className="dp-mono" placeholder={t('domesticProducts.autoGenerate', '不填则后端自动生成')} />
              </Form.Item>
              <Form.Item
                name="barcode"
                label={t('domesticProducts.barcode', '条码')}
                rules={[maxLengthRule(FIELD_MAX_LENGTH.barcode)]}
              >
                <Input className="dp-mono" placeholder={t('domesticProducts.autoGenerate', '不填则后端自动生成')} />
              </Form.Item>
            </>
          ) : null}
          <Form.Item
            name="productImage"
            label={t('domesticProducts.formImageLink')}
            rules={[maxLengthRule(FIELD_MAX_LENGTH.productImage)]}
          >
            <Input className="dp-mono" placeholder={t('domesticProducts.enterImageUrl', '请输入图片 URL')} />
          </Form.Item>
          <Form.Item label={t('domesticProducts.formImagePreview')}>
            <Form.Item noStyle shouldUpdate={(previous, next) => previous.productImage !== next.productImage}>
              {({ getFieldValue }) => <ImageUrlPreview url={getFieldValue('productImage')} />}
            </Form.Item>
          </Form.Item>
          {/* 设计稿的编辑弹窗没有状态开关，但后端 Update 的 IsActive 默认 true：
              表单里去掉它，保存一个已停用的商品就会把它悄悄重新启用，所以必须保留。 */}
          <Form.Item
            className="dp-span-all"
            name="isActive"
            label={t('domesticProducts.status', '状态')}
            valuePropName="checked"
          >
            <StatusSwitch />
          </Form.Item>
        </div>
      </section>

      <section className="dp-form-section">
        <h4 className="dp-section-title">{t('domesticProducts.formSectionPrice')}</h4>
        <div className="dp-grid3-form">
          <Form.Item name="domesticPrice" label={t('domesticProducts.formDomesticPriceCny')}>
            <InputNumber className="dp-number-input" min={0} precision={2} />
          </Form.Item>
          <Form.Item name="oemPrice" label={t('domesticProducts.formOemPriceUsd')}>
            <InputNumber className="dp-number-input" min={0} precision={2} />
          </Form.Item>
          <Form.Item name="importPrice" label={t('domesticProducts.formImportPriceUsd')}>
            <InputNumber className="dp-number-input" min={0} precision={2} />
          </Form.Item>
        </div>
      </section>

      <section className="dp-form-section">
        <h4 className="dp-section-title">{t('domesticProducts.formSectionPackaging')}</h4>
        <div className="dp-grid3-form">
          <Form.Item
            name="packingQuantity"
            label={t('domesticProducts.packingQuantity', '装箱数')}
            extra={t('domesticProducts.formMinOne')}
            rules={quantityRules}
          >
            <InputNumber className="dp-number-input" min={1} max={2147483647} precision={0} />
          </Form.Item>
          <Form.Item name="unitVolume" label={t('domesticProducts.formUnitVolumeM3')}>
            <InputNumber className="dp-number-input" min={0} precision={4} />
          </Form.Item>
          <Form.Item
            name="middlePackQuantity"
            label={t('domesticProducts.middlePackQuantity', '中包数量')}
            extra={t('domesticProducts.formMinOne')}
            rules={quantityRules}
          >
            <InputNumber className="dp-number-input" min={1} max={2147483647} precision={0} />
          </Form.Item>
        </div>
      </section>

      {isEdit ? (
        <Alert type="info" showIcon message={t('domesticProducts.formIdentityReadonlyNotice')} style={{ marginBottom: 10 }} />
      ) : null}
    </>
  )
}

/**
 * 国内商品新建 / 编辑弹窗（三分区：基本信息 / 价格 / 包装）。
 * - 编辑只提交后端真正保存的字段；货号与条码创建后不可改，只读展示在详情抽屉头部。
 * - 底栏「已修改 N 项」与初始值对比，不是简单的「碰过」。
 * - 保存中禁止重复提交、禁止关闭；点遮罩不关闭，避免误点丢数据。
 */
export default function ProductFormModal({ open, product, suppliers, onClose, onSaved }: ProductFormModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<ProductFormValues>()
  const [saving, setSaving] = useState(false)
  const [changedCount, setChangedCount] = useState(0)
  const isEdit = Boolean(product)

  // 弹窗每次打开都会重新挂载 Form（destroyOnHidden），initialValues 以打开那一刻的商品为准。
  const initialValues = useMemo<ProductFormValues>(
    () => (product ? productToFormValues(product) : emptyProductFormValues()),
    [product],
  )

  useEffect(() => {
    if (open) {
      setChangedCount(0)
    }
  }, [open, product])

  const handleSubmit = async () => {
    if (saving) {
      return
    }

    try {
      const values = await form.validateFields()
      setSaving(true)

      if (product) {
        await updateDomesticProduct(product.id, buildUpdatePayload(values))
        message.success(t('domesticProducts.updateSuccess', '更新国内商品成功'))
        onSaved({ mode: 'edit', product, values })
      } else {
        await createDomesticProduct(buildCreatePayload(values))
        message.success(t('domesticProducts.createSuccess', '新建国内商品成功'))
        onSaved({ mode: 'create' })
      }
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }
      console.error(error)
      message.error(error instanceof Error ? error.message : t('domesticProducts.saveFailed', '保存国内商品失败'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      title={
        product ? (
          <span className="dp-modal-title">
            <span>{t('domesticProducts.formEditHeading')}</span>
            <span className="dp-title-chip dp-mono">{product.itemNumber || product.id}</span>
          </span>
        ) : (
          t('domesticProducts.createTitle', '新建国内商品')
        )
      }
      open={open}
      width={820}
      destroyOnHidden
      maskClosable={false}
      keyboard={!saving}
      closable={!saving}
      onCancel={onClose}
      styles={{ body: { maxHeight: '70vh', overflowY: 'auto', paddingInline: 4 } }}
      footer={
        <div className="dp-modal-footer" data-testid="domestic-product-form-footer">
          {isEdit ? (
            <span className={changedCount > 0 ? 'dp-dirty dp-dirty-on' : 'dp-dirty'} aria-live="polite">
              <span className="dp-dirty-dot" />
              {changedCount > 0
                ? t('domesticProducts.formChangedCount', { count: changedCount })
                : t('domesticProducts.formUnchanged')}
            </span>
          ) : null}
          <span className="dp-modal-footer-spacer" />
          <Button onClick={onClose} disabled={saving}>
            {t('common.cancel', '取消')}
          </Button>
          <Button type="primary" loading={saving} onClick={() => void handleSubmit()}>
            {t('common.save', '保存')}
          </Button>
        </div>
      }
    >
      <Form
        form={form}
        layout="vertical"
        autoComplete="off"
        // preserve=false：字段卸载时清掉 store 里的值。否则同一个 form 实例会把上一个商品改到一半的值
        // 带进下一次打开（rc-field-form 重新挂载时 store 里的旧值优先于 initialValues）。
        preserve={false}
        initialValues={initialValues}
        onValuesChange={(_, allValues) => setChangedCount(countChangedFields(initialValues, allValues))}
        data-testid="domestic-product-form"
      >
        <ProductFormFields isEdit={isEdit} suppliers={suppliers} />
      </Form>
    </Modal>
  )
}
