import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Col,
  Form,
  Input,
  InputNumber,
  message,
  Modal,
  Radio,
  Row,
  Select,
  Space,
  Spin,
  Switch,
  TreeSelect,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getCategoryTree, type WarehouseCategoryNode, batchAssignProducts } from '../../../services/warehouseCategoryService'
import {
  createSingleWarehouseProduct,
  type CreateSingleMultiCodeDetailInput,
  type CreateSingleSetDetailInput,
  type CreateSingleStorePriceInput,
} from '../../../services/warehouseProductService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import type { ProductType, SupplierOption } from '../../../types/domesticProduct'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface CreateProductModalProps {
  open: boolean
  suppliers: SupplierOption[]
  onCancel: () => void
  onSuccess: () => void
}

interface SetDetailFormRow {
  key: string
  productCode: string
  quantity: number
}

interface MultiCodeFormRow {
  key: string
  code: string
  name?: string
  barcode?: string
  quantity?: number
}

interface RetailPriceFormRow {
  key: string
  storeCode: string
  storeName?: string
  price: number
  cost: number
}

interface CreateProductFormValues {
  productType: ProductType
  itemNumber?: string
  barcode?: string
  chineseName: string
  englishName?: string
  productSpecification?: string
  domesticPrice?: number
  oemPrice: number
  importPrice: number
  volume?: number
  packingQuantity?: number
  middlePackQuantity?: number
  packingSize?: string
  material?: string
  remarks?: string
  categoryGuid?: string
  supplierCode: string
  isActive: boolean
  imageUrl?: string
  setProductType?: 'combination' | 'fixed' | 'variable'
}

interface CategoryTreeOption {
  title: string
  value: string
  key: string
  children?: CategoryTreeOption[]
}

const defaultSetRows = (): SetDetailFormRow[] => [{ key: '1', productCode: '', quantity: 1 }]
const defaultMultiCodeRows = (): MultiCodeFormRow[] => [{ key: '1', code: '' }]
const defaultRetailPriceRows = (): RetailPriceFormRow[] => [{ key: '1', storeCode: '', price: 0, cost: 0 }]

function createTreeData(nodes: WarehouseCategoryNode[]): CategoryTreeOption[] {
  return nodes.map((node) => ({
    title: node.categoryName || node.chineseName || node.categoryGUID,
    value: node.categoryGUID,
    key: node.categoryGUID,
    children: Array.isArray(node.children) ? createTreeData(node.children) : undefined,
  }))
}

function mapSetType(value?: 'combination' | 'fixed' | 'variable'): 1 | 2 | 3 | undefined {
  if (value === 'fixed') {
    return 2
  }

  if (value === 'variable') {
    return 3
  }

  if (value === 'combination') {
    return 1
  }

  return undefined
}

export default function CreateProductModal({ open, suppliers, onCancel, onSuccess }: CreateProductModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<CreateProductFormValues>()
  const [loading, setLoading] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [categoryTree, setCategoryTree] = useState<WarehouseCategoryNode[]>([])
  const [stores, setStores] = useState<StoreOption[]>([])
  const [autoGenerateItemNumber, setAutoGenerateItemNumber] = useState(true)
  const [autoGenerateBarcode, setAutoGenerateBarcode] = useState(true)
  const [setDetails, setSetDetails] = useState<SetDetailFormRow[]>(defaultSetRows)
  const [multiCodeDetails, setMultiCodeDetails] = useState<MultiCodeFormRow[]>(defaultMultiCodeRows)
  const [retailPrices, setRetailPrices] = useState<RetailPriceFormRow[]>(defaultRetailPriceRows)

  const productType = Form.useWatch('productType', form) ?? 0
  const oemPrice = Form.useWatch('oemPrice', form) ?? 0
  const importPrice = Form.useWatch('importPrice', form) ?? 0
  const supplierCode = Form.useWatch('supplierCode', form)

  useEffect(() => {
    if (!open) {
      return
    }

    let cancelled = false

    const loadOptions = async () => {
      setLoading(true)
      try {
        const [categories, storeOptions] = await Promise.all([
          getCategoryTree(),
          getActiveStores(),
        ])

        if (!cancelled) {
          setCategoryTree(categories)
          setStores(storeOptions)
        }
      } catch (error) {
        console.error(error)
        message.error(error instanceof Error ? error.message : t('warehouse.loadCreateDataFailed'))
      } finally {
        if (!cancelled) {
          setLoading(false)
        }
      }
    }

    form.setFieldsValue({
      productType: 0,
      oemPrice: 0,
      importPrice: 0,
      isActive: true,
      setProductType: 'combination',
    })
    setAutoGenerateItemNumber(true)
    setAutoGenerateBarcode(true)
    setSetDetails(defaultSetRows())
    setMultiCodeDetails(defaultMultiCodeRows())
    setRetailPrices(defaultRetailPriceRows())
    void loadOptions()

    return () => {
      cancelled = true
    }
  }, [form, open])

  useEffect(() => {
    if (!open) {
      return
    }

    setRetailPrices((current) =>
      current.map((item) => ({
        ...item,
        price: oemPrice || 0,
        cost: importPrice || 0,
      })),
    )
  }, [importPrice, oemPrice, open])

  const supplierOptions = useMemo(
    () =>
      suppliers.map((item) => ({
        value: item.code,
        label: `${item.code} - ${item.name}`,
      })),
    [suppliers],
  )

  const setColumns = useMemo<ColumnsType<SetDetailFormRow>>(
    () => [
      {
        title: t('warehouse.productCode'),
        dataIndex: 'productCode',
        render: (_, record) => (
          <Input
            value={record.productCode}
            placeholder={t('warehouse.enterProductCode')}
            onChange={(event) => {
              const value = event.target.value
              setSetDetails((current) =>
                current.map((item) => (item.key === record.key ? { ...item, productCode: value } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('warehouse.quantity'),
        dataIndex: 'quantity',
        width: 120,
        render: (_, record) => (
          <InputNumber
            min={1}
            precision={0}
            value={record.quantity}
            style={{ width: '100%' }}
            onChange={(value) => {
              setSetDetails((current) =>
                current.map((item) => (item.key === record.key ? { ...item, quantity: value ?? 1 } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('common.action'),
        key: 'action',
        width: 80,
        render: (_, record) => (
          <Button
            type="link"
            danger
            icon={<DeleteOutlined />}
            onClick={() => {
              setSetDetails((current) => {
                if (current.length === 1) {
                  message.warning(t('warehouse.keepOneSetDetail'))
                  return current
                }
                return current.filter((item) => item.key !== record.key)
              })
            }}
          >
            {t('common.delete')}
          </Button>
        ),
      },
    ],
    [t],
  )

  const multiCodeColumns = useMemo<ColumnsType<MultiCodeFormRow>>(
    () => [
      {
        title: t('warehouse.code'),
        dataIndex: 'code',
        width: 140,
        render: (_, record) => (
          <Input
            value={record.code}
            placeholder={t('warehouse.enterCode')}
            onChange={(event) => {
              const value = event.target.value
              setMultiCodeDetails((current) =>
                current.map((item) => (item.key === record.key ? { ...item, code: value } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('warehouse.name'),
        dataIndex: 'name',
        width: 160,
        render: (_, record) => (
          <Input
            value={record.name}
            placeholder={t('productCreation.enterName')}
            onChange={(event) => {
              const value = event.target.value
              setMultiCodeDetails((current) =>
                current.map((item) => (item.key === record.key ? { ...item, name: value } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('domesticProducts.barcode'),
        dataIndex: 'barcode',
        width: 180,
        render: (_, record) => (
          <Input
            value={record.barcode}
            placeholder={t('warehouse.autoGenerateOptional')}
            onChange={(event) => {
              const value = event.target.value
              setMultiCodeDetails((current) =>
                current.map((item) => (item.key === record.key ? { ...item, barcode: value } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('warehouse.quantity'),
        dataIndex: 'quantity',
        width: 120,
        render: (_, record) => (
          <InputNumber
            min={1}
            precision={0}
            value={record.quantity}
            style={{ width: '100%' }}
            onChange={(value) => {
              setMultiCodeDetails((current) =>
                current.map((item) => (item.key === record.key ? { ...item, quantity: value ?? 1 } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('common.action'),
        key: 'action',
        width: 80,
        render: (_, record) => (
          <Button
            type="link"
            danger
            icon={<DeleteOutlined />}
            onClick={() => {
              setMultiCodeDetails((current) => {
                if (current.length === 1) {
                  message.warning(t('warehouse.keepOneMultiCodeDetail'))
                  return current
                }
                return current.filter((item) => item.key !== record.key)
              })
            }}
          >
            {t('common.delete')}
          </Button>
        ),
      },
    ],
    [t],
  )

  const retailPriceColumns = useMemo<ColumnsType<RetailPriceFormRow>>(
    () => [
      {
        title: t('warehouse.store'),
        dataIndex: 'storeCode',
        width: 220,
        render: (_, record) => (
          <Select
            value={record.storeCode || undefined}
            placeholder={t('warehouse.selectStore')}
            showSearch
            optionFilterProp="label"
            options={stores}
            onChange={(value, option) => {
              setRetailPrices((current) =>
                current.map((item) =>
                  item.key === record.key
                    ? {
                        ...item,
                        storeCode: value,
                        storeName: typeof option === 'object' && option && 'label' in option ? String(option.label) : undefined,
                      }
                    : item,
                ),
              )
            }}
          />
        ),
      },
      {
        title: t('productCreation.privateLabelPrice'),
        dataIndex: 'price',
        width: 140,
        render: (_, record) => (
          <InputNumber
            min={0}
            precision={2}
            value={record.price}
            style={{ width: '100%' }}
            onChange={(value) => {
              setRetailPrices((current) =>
                current.map((item) => (item.key === record.key ? { ...item, price: value ?? 0 } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('warehouse.costPrice'),
        dataIndex: 'cost',
        width: 140,
        render: (_, record) => (
          <InputNumber
            min={0}
            precision={2}
            value={record.cost}
            style={{ width: '100%' }}
            onChange={(value) => {
              setRetailPrices((current) =>
                current.map((item) => (item.key === record.key ? { ...item, cost: value ?? 0 } : item)),
              )
            }}
          />
        ),
      },
      {
        title: t('common.action'),
        key: 'action',
        width: 80,
        render: (_, record) => (
          <Button
            type="link"
            danger
            icon={<DeleteOutlined />}
            onClick={() => {
              setRetailPrices((current) => {
                if (current.length === 1) {
                  message.warning(t('warehouse.keepOneStorePrice'))
                  return current
                }
                return current.filter((item) => item.key !== record.key)
              })
            }}
          >
            {t('common.delete')}
          </Button>
        ),
      },
    ],
    [stores, t],
  )

  const handleClose = () => {
    if (submitting) {
      return
    }

    form.resetFields()
    setAutoGenerateItemNumber(true)
    setAutoGenerateBarcode(true)
    setSetDetails(defaultSetRows())
    setMultiCodeDetails(defaultMultiCodeRows())
    setRetailPrices(defaultRetailPriceRows())
    onCancel()
  }

  const handleSubmit = async () => {
    try {
      const values = await form.validateFields()

      const normalizedSetItems = setDetails
        .map((item) => ({
          ...item,
          productCode: item.productCode.trim(),
        }))
        .filter((item) => item.productCode)

      if (values.productType === 1 && !normalizedSetItems.length) {
        message.error(t('warehouse.addOneSetDetail'))
        return
      }

      const normalizedMultiCodeItems = multiCodeDetails
        .map((item) => ({
          ...item,
          code: item.code.trim(),
          barcode: item.barcode?.trim(),
        }))
        .filter((item) => item.code || item.barcode)

      if (values.productType === 2 && !normalizedMultiCodeItems.length) {
        message.error(t('warehouse.addOneMultiCodeDetail'))
        return
      }

      const normalizedStorePrices = retailPrices.filter((item) => item.storeCode)

      const setItems: CreateSingleSetDetailInput[] | undefined =
        values.productType === 1
          ? normalizedSetItems.map((item) => ({
              productCode: item.productCode,
              quantity: item.quantity || 1,
            }))
          : undefined

      const multiCodeItems: CreateSingleMultiCodeDetailInput[] | undefined =
        values.productType === 2
          ? normalizedMultiCodeItems.map((item) => ({
              barcode: item.barcode || item.code || undefined,
              purchasePrice: values.importPrice,
              retailPrice: values.oemPrice,
              isActive: true,
              autoPricing: false,
              isSpecialProduct: false,
            }))
          : undefined

      const storePrices: CreateSingleStorePriceInput[] | undefined = normalizedStorePrices.length
        ? normalizedStorePrices.map((item) => ({
            storeCode: item.storeCode,
            purchasePrice: item.cost,
            retailPrice: item.price,
            discountRate: 0,
            autoPricing: false,
            isSpecialProduct: false,
            isActive: true,
          }))
        : undefined

      setSubmitting(true)

      const result = await createSingleWarehouseProduct({
        productType: values.productType,
        itemNumber: autoGenerateItemNumber ? undefined : values.itemNumber?.trim() || undefined,
        barcode: autoGenerateBarcode ? undefined : values.barcode?.trim() || undefined,
        chineseName: values.chineseName.trim(),
        englishName: values.englishName?.trim() || undefined,
        productSpecification: values.productSpecification?.trim() || undefined,
        domesticPrice: values.domesticPrice,
        oemPrice: values.oemPrice,
        importPrice: values.importPrice,
        volume: values.volume,
        packingQuantity: values.packingQuantity,
        middlePackQuantity: values.middlePackQuantity,
        packingSize: values.packingSize?.trim() || undefined,
        material: values.material?.trim() || undefined,
        remarks: values.remarks?.trim() || undefined,
        categoryGuid: values.categoryGuid,
        supplierCode: values.supplierCode,
        isActive: values.isActive,
        imageUrl: values.imageUrl?.trim() || undefined,
        setType: mapSetType(values.setProductType),
        setItems,
        multiCodeItems,
        storePrices,
      })

      if (!result.success || !result.productCode) {
        message.error(result.message || t('warehouse.createProductFailed'))
        return
      }

      if (values.categoryGuid) {
        await batchAssignProducts(values.categoryGuid, [result.productCode])
      }

      if (result.warnings?.length) {
        message.warning(result.warnings.join('；'))
      }

      message.success(result.message || t('warehouse.createProductSuccess'))
      handleClose()
      onSuccess()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }
      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.createProductFailed'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal
      title={t('warehouse.createProduct')}
      open={open}
      width={980}
      destroyOnHidden
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={submitting}
      onCancel={handleClose}
      onOk={() => void handleSubmit()}
    >
      <Spin spinning={loading}>
        <Form form={form} layout="vertical" preserve={false}>
          <Card title={t('warehouse.productType')} size="small" style={{ marginBottom: 16 }}>
            <Form.Item name="productType" initialValue={0}>
              <Radio.Group>
                <Radio value={0}>{t('warehouse.normalProduct')}</Radio>
                <Radio value={1}>{t('warehouse.setProduct')}</Radio>
                <Radio value={2}>{t('warehouse.multiCodeProduct')}</Radio>
              </Radio.Group>
            </Form.Item>
          </Card>

          <Card title={t('warehouse.products.basicInfo')} size="small" style={{ marginBottom: 16 }}>
            <Row gutter={16}>
              <Col span={12}>
                <Form.Item label={t('warehouse.products.itemNumberMethod')}>
                  <Radio.Group
                    value={autoGenerateItemNumber ? 'auto' : 'manual'}
                    onChange={(event) => {
                      const auto = event.target.value === 'auto'
                      setAutoGenerateItemNumber(auto)
                      if (auto) {
                        form.setFieldValue('itemNumber', undefined)
                      }
                    }}
                  >
                    <Radio value="auto">{t('common.auto')}</Radio>
                    <Radio value="manual">{t('common.manual')}</Radio>
                  </Radio.Group>
                </Form.Item>
                {!autoGenerateItemNumber ? (
                  <Form.Item
                    name="itemNumber"
                    label={t('column.itemNumber')}
                    rules={[{ required: true, message: t('warehouse.enterItemNumber') }]}
                  >
                    <Input placeholder={t('warehouse.enterItemNumber')} />
                  </Form.Item>
                ) : null}
              </Col>
              <Col span={12}>
                <Form.Item label={t('warehouse.products.barcodeMethod')}>
                  <Radio.Group
                    value={autoGenerateBarcode ? 'auto' : 'manual'}
                    onChange={(event) => {
                      const auto = event.target.value === 'auto'
                      setAutoGenerateBarcode(auto)
                      if (auto) {
                        form.setFieldValue('barcode', undefined)
                      }
                    }}
                  >
                    <Radio value="auto">{t('common.auto')}</Radio>
                    <Radio value="manual">{t('common.manual')}</Radio>
                  </Radio.Group>
                </Form.Item>
                {!autoGenerateBarcode ? (
                  <Form.Item name="barcode" label={t('column.barcode')}>
                    <Input placeholder={t('warehouse.enterBarcode')} />
                  </Form.Item>
                ) : null}
              </Col>
              <Col span={12}>
                <Form.Item
                  name="chineseName"
                  label={t('warehouse.chineseName')}
                  rules={[{ required: true, message: t('warehouse.enterChineseName') }]}
                >
                  <Input placeholder={t('warehouse.enterChineseName')} />
                </Form.Item>
              </Col>
              <Col span={12}>
                <Form.Item name="englishName" label={t('warehouse.englishName')}>
                  <Input placeholder={t('warehouse.enterEnglishName')} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item name="domesticPrice" label={t('warehouse.domesticPriceLabel')}>
                  <InputNumber min={0} precision={2} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item
                  name="oemPrice"
                  label={t('warehouse.oemPriceLabel')}
                  rules={[{ required: true, message: t('warehouse.enterOemPrice') }]}
                >
                  <InputNumber min={0.01} precision={2} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item
                  name="importPrice"
                  label={t('warehouse.importPriceLabel')}
                  rules={[{ required: true, message: t('warehouse.enterImportPrice') }]}
                >
                  <InputNumber min={0.01} precision={2} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item name="volume" label={t('warehouse.volume')}>
                  <InputNumber min={0} precision={3} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item name="packingQuantity" label={t('warehouse.packingQuantity')}>
                  <InputNumber min={1} precision={0} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item name="middlePackQuantity" label={t('warehouse.middlePackQuantity')}>
                  <InputNumber min={1} precision={0} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item name="productSpecification" label={t('domesticProducts.specification')}>
                  <Input placeholder={t('warehouse.enterSpec')} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item name="packingSize" label={t('warehouse.packingSize')}>
                  <Input placeholder={t('warehouse.enterPackingSize')} />
                </Form.Item>
              </Col>
              <Col span={8}>
                <Form.Item name="material" label={t('warehouse.material')}>
                  <Input placeholder={t('warehouse.enterMaterial')} />
                </Form.Item>
              </Col>
              <Col span={12}>
                <Form.Item name="categoryGuid" label={t('warehouse.category')}>
                  <TreeSelect
                    allowClear
                    placeholder={t('warehouse.selectCategory')}
                    treeData={createTreeData(categoryTree)}
                    treeDefaultExpandAll
                  />
                </Form.Item>
              </Col>
              <Col span={12}>
                <Form.Item name="imageUrl" label={t('warehouse.imageUrl')}>
                  <Input placeholder={t('warehouse.enterImageUrl')} />
                </Form.Item>
              </Col>
              <Col span={24}>
                <Form.Item name="remarks" label={t('common.remarks')}>
                  <Input.TextArea rows={3} placeholder={t('common.enterRemarks')} />
                </Form.Item>
              </Col>
            </Row>
          </Card>

          <Card title={t('warehouse.supplierInfo')} size="small" style={{ marginBottom: 16 }}>
            <Row gutter={16}>
              <Col span={12}>
                <Form.Item
                  name="supplierCode"
                  label={t('warehouse.domesticSupplier')}
                  rules={[{ required: true, message: t('warehouse.selectDomesticSupplier') }]}
                >
                  <Select
                    placeholder={t('warehouse.selectDomesticSupplier')}
                    showSearch
                    optionFilterProp="label"
                    options={supplierOptions}
                  />
                </Form.Item>
              </Col>
              <Col span={12}>
                <Form.Item name="isActive" label={t('warehouse.isListed')} valuePropName="checked" initialValue>
                  <Switch />
                </Form.Item>
              </Col>
            </Row>
            {autoGenerateItemNumber && supplierCode ? (
              <Alert message={t('warehouse.autoGenerateHint', { code: supplierCode })} type="info" showIcon />
            ) : null}
          </Card>

          {productType === 1 ? (
            <Card title={t('warehouse.setConfig')} size="small" style={{ marginBottom: 16 }}>
              <Form.Item name="setProductType" label={t('warehouse.setType')} initialValue="combination">
                <Select
                  options={[
                    { value: 'combination', label: t('warehouse.combinationSet') },
                    { value: 'fixed', label: t('warehouse.fixedSet') },
                    { value: 'variable', label: t('warehouse.variableSet') },
                  ]}
                />
              </Form.Item>
              <MeasuredTable metricId="warehouse.products.create-product-modal.table-1" rowKey="key" dataSource={setDetails} columns={setColumns} pagination={false} size="small" />
              <Button
                block
                type="dashed"
                icon={<PlusOutlined />}
                style={{ marginTop: 8 }}
                onClick={() => {
                  setSetDetails((current) => [
                    ...current,
                    {
                      key: `set_${Date.now()}_${Math.random()}`,
                      productCode: '',
                      quantity: 1,
                    },
                  ])
                }}
              >
                {t('warehouse.products.addDetail')}
              </Button>
            </Card>
          ) : null}

          {productType === 2 ? (
            <Card title={t('warehouse.multiCodeConfig')} size="small" style={{ marginBottom: 16 }}>
              <MeasuredTable metricId="warehouse.products.create-product-modal.table-2" rowKey="key" dataSource={multiCodeDetails} columns={multiCodeColumns} pagination={false} size="small" />
              <Button
                block
                type="dashed"
                icon={<PlusOutlined />}
                style={{ marginTop: 8 }}
                onClick={() => {
                  setMultiCodeDetails((current) => [
                    ...current,
                    {
                      key: `multi_${Date.now()}_${Math.random()}`,
                      code: '',
                    },
                  ])
                }}
              >
                {t('warehouse.products.addDetail')}
              </Button>
            </Card>
          ) : null}

          <Card title={t('warehouse.storeRetailPrice')} size="small">
            <MeasuredTable metricId="warehouse.products.create-product-modal.table-3" rowKey="key" dataSource={retailPrices} columns={retailPriceColumns} pagination={false} size="small" />
            <Space direction="vertical" style={{ width: '100%', marginTop: 8 }}>
              <Button
                block
                type="dashed"
                icon={<PlusOutlined />}
                onClick={() => {
                  setRetailPrices((current) => [
                    ...current,
                    {
                      key: `store_${Date.now()}_${Math.random()}`,
                      storeCode: '',
                      price: oemPrice || 0,
                      cost: importPrice || 0,
                    },
                  ])
                }}
              >
                {t('warehouse.products.addPrice')}
              </Button>
              <Button
                block
                type="primary"
                icon={<PlusOutlined />}
                onClick={() => {
                  if (!stores.length) {
                    message.warning(t('warehouse.noStoresAvailable'))
                    return
                  }

                  setRetailPrices(
                    stores.map((store, index) => ({
                      key: `store_all_${store.value}_${index}`,
                      storeCode: store.value,
                      storeName: store.label,
                      price: oemPrice || 0,
                      cost: importPrice || 0,
                    })),
                  )
                }}
              >
                {t('warehouse.products.addAllStores')}
              </Button>
            </Space>
          </Card>
        </Form>
      </Spin>
    </Modal>
  )
}
