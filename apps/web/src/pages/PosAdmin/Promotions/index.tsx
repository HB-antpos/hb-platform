import { getActiveLocalSuppliers } from '../../../services/localSupplierService'
import { getProductById as getPosProductById } from '../../../services/posProductService'
import type { PosProductDto } from '../../../types/posProduct'
import {
  createPromotion,
  deletePromotion,
  enablePromotion,
  getPromotionById,
  getPromotionGrid,
  updatePromotion,
} from '../../../services/promotionService'
import { getActiveStores } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type {
  CreatePromotionDto,
  PromotionDetailDto,
  PromotionListDto,
  PromotionProductItemDto,
  PromotionStoreItemDto,
  UpdatePromotionDto,
} from '../../../types/promotion'
import {
  Button,
  Card,
  DatePicker,
  Form,
  Input,
  InputNumber,
  message,
  Modal,
  Popconfirm,
  Select,
  Space,
  Switch,
  Tag,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import ProductPicker from './ProductPicker'
import { getPromotionEditorStoreCodes } from './promotionStoreScope'
import { MeasuredTable } from '../../../components/MeasuredTable'

type DataType = PromotionListDto & { key: string }

export default function PromotionsPage() {
  const { t } = useTranslation()
  // 与后端 Promotions.Edit 策略对齐；全局促销的写接口还要求管理员角色，由后端最终把关。
  const access = useAuthStore((state) => state.access)
  const canEdit = access.canEditPromotions
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<DataType[]>([])
  const [total, setTotal] = useState(0)
  const [pageSize, setPageSize] = useState(50)
  const [page, setPage] = useState(1)
  const [sortField, setSortField] = useState<string | undefined>()
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend' | undefined>()
  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [editorOpen, setEditorOpen] = useState(false)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editorForm] = Form.useForm()
  const selectedEditorStores = Form.useWatch('stores', editorForm) as string[] | undefined
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const [pickerOpen, setPickerOpen] = useState(false)
  const [productInfoMap, setProductInfoMap] = useState<Record<string, PosProductDto>>({})
  const [supplierNameMap, setSupplierNameMap] = useState<Record<string, string>>({})

  const loadData = async () => {
    if (!mountedRef.current) {
      return
    }

    await runLatestGuardedRequest(listRequestGuardRef.current, async () => {
      const values = form.getFieldsValue()
      const sortModel: Record<string, string>[] = []
      if (sortField && sortOrder) {
        sortModel.push({ ColId: sortField, Sort: sortOrder === 'ascend' ? 'asc' : 'desc' })
      }
      return getPromotionGrid({
        StartRow: (page - 1) * pageSize,
        EndRow: page * pageSize - 1,
        PageSize: pageSize,
        GlobalSearch: values.keyword || undefined,
        SortModel: sortModel.length ? sortModel : undefined,
      } as Record<string, unknown>)
    }, {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        const items = result?.items ?? []
        setTotal(result?.total ?? 0)
        setData(items.map((it) => ({ ...it, key: String(it.id) })))
      },
      onError: () => message.error(t('message.loadFailed')),
      onSettled: () => setLoading(false),
    })
  }

  const latestLoadDataRef = useRef(loadData)

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
  })

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      listRequestGuardRef.current.invalidate()
    }
  }, [])

  const loadSupplierNames = async () => {
    try {
      const suppliers = await getActiveLocalSuppliers()
      const map: Record<string, string> = {}
      suppliers.forEach((s) => {
        if (s.localSupplierCode) map[s.localSupplierCode] = s.name || s.localSupplierCode
      })
      setSupplierNameMap(map)
    } catch { /* ignore */ }
  }

  const openCreate = () => {
    setEditingId(null)
    editorForm.resetFields()
    editorForm.setFieldsValue({
      name: '', description: '', isEnabled: true, isExclusive: true, priority: 0,
      effectiveStart: null, effectiveEnd: null, applyQuantity: 2, fixedPrice: 0,
      maxApplicationsPerOrder: undefined, stores: [], products: [],
    })
    loadSupplierNames()
    setEditorOpen(true)
  }

  const openEdit = async (id: string) => {
    try {
      const dto: PromotionDetailDto = await getPromotionById(id)
      if (!dto) { message.error(t('posAdmin.promotions.notFound', '未找到促销')); return }
      setEditingId(id)
      editorForm.setFieldsValue({
        name: dto.name, description: dto.description, isEnabled: dto.isEnabled,
        isExclusive: dto.isExclusive, priority: dto.priority,
        effectiveStart: dto.effectiveStart ? dayjs(dto.effectiveStart) : null,
        effectiveEnd: dto.effectiveEnd ? dayjs(dto.effectiveEnd) : null,
        applyQuantity: dto.applyQuantity, fixedPrice: dto.fixedPrice,
        maxApplicationsPerOrder: dto.maxApplicationsPerOrder ?? undefined,
        stores: getPromotionEditorStoreCodes(dto),
        products: dto.products.map((p) => ({ id: p.id, productCode: p.productCode, unitWeight: p.unitWeight ?? 1 })),
      })
      loadSupplierNames()
      const codes = (dto.products || []).map((p) => p.productCode).filter(Boolean)
      await Promise.all(codes.map(async (code) => {
        try {
          const info = await getPosProductById(code)
          if (info?.productCode) setProductInfoMap((prev) => ({ ...prev, [info.productCode]: info }))
        } catch { /* ignore */ }
      }))
      setEditorOpen(true)
    } catch {
      message.error(t('message.loadFailed'))
    }
  }

  const saveEditor = async () => {
    try {
      const v = await editorForm.validateFields()
      const cleanedProducts: PromotionProductItemDto[] = (v.products || [])
        .filter((p: any) => !!p.productCode)
        .map((p: any) => ({ id: p.id, productCode: String(p.productCode), unitWeight: Number(p.unitWeight || 1) }))
      if (!cleanedProducts.length) { message.error(t('posAdmin.promotions.atLeastOneProduct')); return }
      const payload = {
        name: v.name, description: v.description || undefined, isEnabled: !!v.isEnabled,
        isExclusive: !!v.isExclusive, priority: Number(v.priority || 0),
        effectiveStart: v.effectiveStart?.toISOString(), effectiveEnd: v.effectiveEnd?.toISOString(),
        applyQuantity: Number(v.applyQuantity), fixedPrice: Number(v.fixedPrice),
        maxApplicationsPerOrder: v.maxApplicationsPerOrder != null ? Number(v.maxApplicationsPerOrder) : undefined,
        products: cleanedProducts,
        stores: (v.stores || []).map((code: string) => ({ storeCode: code })) as PromotionStoreItemDto[],
      }
      if (editingId === null) {
        await createPromotion(payload as CreatePromotionDto)
      } else {
        await updatePromotion(editingId, payload as UpdatePromotionDto)
      }
      message.success(t('message.saveSuccess'))
      setEditorOpen(false)
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch {
      message.error(t('message.saveFailed'))
    }
  }

  const handleDelete = async (id: string) => {
    try {
      await deletePromotion(id)
      message.success(t('message.deleteSuccess'))
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch { message.error(t('message.deleteFailed')) }
  }

  const toggleEnable = async (record: DataType, enable: boolean) => {
    try {
      await enablePromotion(String(record.id), enable)
      message.success(t('posAdmin.promotions.updateEnableSuccess', '已更新启用状态'))
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch { message.error(t('message.updateFailed')) }
  }

  const columns: ColumnsType<DataType> = [
    { title: t('posAdmin.promotions.name', '名称'), dataIndex: 'name', key: 'name', sorter: true },
    { title: t('posAdmin.promotions.isEnabled', '启用'), dataIndex: 'isEnabled', key: 'isEnabled', sorter: true, width: 100, render: (_, record) => <Switch checked={record.isEnabled} disabled={!canEdit} onChange={(val) => toggleEnable(record, val)} /> },
    { title: t('posAdmin.promotions.isExclusive', '排他'), dataIndex: 'isExclusive', key: 'isExclusive', width: 100, render: (v: boolean) => <Tag color={v ? 'red' : 'default'}>{v ? t('common.yes') : t('common.no')}</Tag> },
    { title: t('posAdmin.promotions.priority', '优先级'), dataIndex: 'priority', key: 'priority', sorter: true, width: 100 },
    { title: t('posAdmin.promotions.startTime', '开始'), dataIndex: 'effectiveStart', key: 'effectiveStart', sorter: true, render: (v: string) => (v ? new Date(v).toLocaleString() : '-') },
    { title: t('posAdmin.promotions.endTime', '结束'), dataIndex: 'effectiveEnd', key: 'effectiveEnd', sorter: true, render: (v: string) => (v ? new Date(v).toLocaleString() : '-') },
    { title: t('posAdmin.promotions.applyQuantity', '门槛件数'), dataIndex: 'applyQuantity', key: 'applyQuantity', width: 110 },
    { title: t('posAdmin.promotions.fixedPrice', '固定总价'), dataIndex: 'fixedPrice', key: 'fixedPrice', width: 110, render: (v: number) => (v ?? 0).toFixed(2) },
    { title: t('posAdmin.promotions.productsCount', '商品数'), dataIndex: 'productsCount', key: 'productsCount', width: 100 },
    {
      title: t('posAdmin.promotions.storesCount', '分店数'), dataIndex: 'storesCount', key: 'storesCount', width: 140,
      // 统一口径：未绑定分店的促销收银不计算，列表里明确标出来，提醒补选分店。
      render: (v: number) => (v > 0 ? v : <Tag color="red">{t('posAdmin.promotions.unboundStores', '未指定（收银不生效）')}</Tag>),
    },
    {
      title: t('column.action'), key: 'actions', width: 200,
      render: (_, record) => (
        <Space>
          <Button type="link" disabled={!canEdit} onClick={() => openEdit(String(record.id))}>{t('common.edit')}</Button>
          <Popconfirm title={t('posAdmin.promotions.confirmDeletePromotion')} description={t('posAdmin.promotions.deleteIrreversible')} okText={t('common.delete')} cancelText={t('common.cancel')} okButtonProps={{ danger: true }} disabled={!canEdit} onConfirm={() => handleDelete(String(record.id))}>
            <Button type="link" danger disabled={!canEdit}>{t('common.delete')}</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  const onTableChange = (pagination: any, _filters: any, sorter: any) => {
    if (sorter?.field) { setSortField(String(sorter.field)); setSortOrder(sorter.order) }
    else { setSortField(undefined); setSortOrder(undefined) }
    setPage(pagination.current)
    setPageSize(pagination.pageSize)
  }

  useEffect(() => {
    ;(async () => {
      try { setStoreOptions(await getActiveStores()) } catch { /* ignore */ }
    })()
  }, [])

  useEffect(() => {
    void loadData()
    return () => listRequestGuardRef.current.invalidate()
  }, [page, pageSize, sortField, sortOrder])

  const editorHasNoStores = !selectedEditorStores?.length

  return (
    <Card title={t('posAdmin.promotions.title', '促销管理（满减/固定组合价）')} extra={<Button type="primary" disabled={!canEdit} onClick={openCreate}>{t('posAdmin.promotions.createPromotion', '新建促销')}</Button>}>
      <Form form={form} layout="inline" onFinish={loadData} style={{ marginBottom: 16 }}>
        <Form.Item name="storeCode" label={t('common.store')}><Select allowClear showSearch optionFilterProp="label" options={storeOptions} style={{ width: 240 }} /></Form.Item>
        <Form.Item name="keyword" label={t('posAdmin.promotions.keyword', '关键词')}><Input allowClear placeholder={t('posAdmin.promotions.searchName', '搜索名称')} style={{ width: 240 }} /></Form.Item>
        <Form.Item><Space><Button type="primary" htmlType="submit">{t('common.query')}</Button><Button onClick={() => { form.resetFields(); loadData() }}>{t('common.reset')}</Button></Space></Form.Item>
      </Form>

      <MeasuredTable metricId="pos-admin.promotions.table-1" rowKey="key" loading={loading} dataSource={data} columns={columns} pagination={{ total, current: page, pageSize, showSizeChanger: true, pageSizeOptions: ['10', '20', '50', '100', '200'] }} onChange={onTableChange} />

      <Modal open={editorOpen} title={editingId === null ? t('posAdmin.promotions.createPromotion', '新建促销') : t('posAdmin.promotions.editPromotion', '编辑促销')} onCancel={() => setEditorOpen(false)} onOk={saveEditor} okButtonProps={{ disabled: !canEdit }} width={900} forceRender>
        <Form form={editorForm} layout="vertical" disabled={!canEdit}>
          <Space style={{ width: '100%' }} wrap>
            <Form.Item name="name" label={t('posAdmin.promotions.name', '名称')} rules={[{ required: true }]} style={{ width: 300 }}><Input /></Form.Item>
            <Form.Item name="priority" label={t('posAdmin.promotions.priority', '优先级')} style={{ width: 180 }}><InputNumber min={0} style={{ width: '100%' }} /></Form.Item>
            <Form.Item name="isExclusive" label={t('posAdmin.promotions.isExclusive', '排他')} valuePropName="checked" style={{ width: 120 }}><Switch /></Form.Item>
            <Form.Item name="isEnabled" label={t('posAdmin.promotions.isEnabled', '启用')} valuePropName="checked" style={{ width: 120 }}><Switch /></Form.Item>
          </Space>
          <Form.Item name="description" label={t('posAdmin.promotions.description', '说明')}><Input.TextArea rows={2} /></Form.Item>
          <Space style={{ width: '100%' }} wrap>
            <Form.Item name="effectiveStart" label={t('posAdmin.promotions.startTime', '开始时间')} rules={[{ required: true }]}><DatePicker showTime style={{ width: 240 }} /></Form.Item>
            <Form.Item name="effectiveEnd" label={t('posAdmin.promotions.endTime', '结束时间')} rules={[{ required: true }]}><DatePicker showTime style={{ width: 240 }} /></Form.Item>
          </Space>
          <Space style={{ width: '100%' }} wrap>
            <Form.Item name="applyQuantity" label={t('posAdmin.promotions.applyQuantity', '门槛件数')} rules={[{ required: true, type: 'number', min: 2 }]}><InputNumber min={2} style={{ width: 160 }} /></Form.Item>
            <Form.Item name="fixedPrice" label={t('posAdmin.promotions.fixedPrice', '固定总价')} rules={[{ required: true, type: 'number', min: 0 }]}><InputNumber min={0} precision={2} style={{ width: 160 }} /></Form.Item>
            <Form.Item name="maxApplicationsPerOrder" label={t('posAdmin.promotions.maxApplicationsPerOrder', '每单最多次数')}><InputNumber min={1} style={{ width: 160 }} /></Form.Item>
          </Space>
          <Form.Item label={t('posAdmin.promotions.applicableStores', '适用分店')} required>
            <Space wrap>
              {/* 统一口径：分店必选，未绑定分店的促销收银不计算；后端新建 / 编辑同样拒绝空分店。 */}
              <Form.Item
                name="stores"
                noStyle
                rules={[{ required: true, type: 'array', min: 1, message: t('posAdmin.promotions.storesRequired', '请至少选择一家适用分店') }]}
              >
                <Select mode="multiple" options={storeOptions} showSearch optionFilterProp="label" allowClear placeholder={t('posAdmin.promotions.storesPlaceholder', '选择适用分店')} style={{ minWidth: 360 }} />
              </Form.Item>
              <Button onClick={() => editorForm.setFieldsValue({ stores: storeOptions.map((x) => x.value) })}>{t('posAdmin.promotions.selectAll')}</Button>
            </Space>
            {editorHasNoStores ? (
              <div style={{ color: '#cf1322', fontSize: 12, marginTop: 4 }}>
                {t('posAdmin.promotions.storesRequiredHint', '必须选择分店；未绑定分店的促销收银不会计算。')}
              </div>
            ) : null}
          </Form.Item>
          <Form.List name="products">
            {(fields, { add: _add, remove }) => {
              const values = editorForm.getFieldValue('products') || []
              const dataSource = fields.map((f) => {
                const row = values?.[f.name] || {}
                const code = row?.productCode
                const info = code ? productInfoMap[code] : undefined
                return { key: code || String(f.key), field: f, code, supplierName: info?.localSupplierCode ? supplierNameMap[info.localSupplierCode] || info.localSupplierCode : '', itemNumber: info?.itemNumber || '', productName: info?.productName || '', retailPrice: info?.retailPrice }
              })
              const productColumns = [
                { title: t('posAdmin.promotions.unitWeight', '计数权重'), dataIndex: 'unitWeight', width: 160, render: (_: any, record: any) => <Form.Item name={[record.field.name, 'unitWeight']} rules={[{ required: true, type: 'number', min: 1 }]} style={{ marginBottom: 0 }}><InputNumber min={1} style={{ width: 120 }} /></Form.Item> },
                { title: t('column.supplier'), dataIndex: 'supplierName', width: 180, render: (v: any) => v || '—' },
                { title: t('column.itemNumber'), dataIndex: 'itemNumber', width: 180, render: (v: any) => v || '—' },
                { title: t('posAdmin.promotions.name', '名称'), dataIndex: 'productName', render: (v: any) => v || '—' },
                { title: t('column.retailPrice'), dataIndex: 'retailPrice', width: 140, render: (v: number) => v != null ? Number(v).toFixed(2) : '—' },
                { title: t('column.action'), key: 'actions', width: 120, render: (_: any, record: any) => <Button type="link" danger onClick={() => remove(record.field.name)}>{t('common.delete')}</Button> },
              ]
              return (
                <>
                  <MeasuredTable metricId="pos-admin.promotions.table-2" rowKey="key" dataSource={dataSource} columns={productColumns as any} pagination={false} />
                  <Form.Item style={{ marginTop: 8 }}>
                    <Button type="dashed" onClick={() => setPickerOpen(true)}>{t('posAdmin.promotions.addProduct')}</Button>
                  </Form.Item>
                </>
              )
            }}
          </Form.List>
        </Form>
      </Modal>

      <ProductPicker
        open={pickerOpen}
        onClose={() => setPickerOpen(false)}
        alreadySelectedCodes={(editorForm.getFieldValue('products') || []).map((x: any) => x?.productCode).filter((c: string) => !!c)}
        onPick={(code) => {
          const existing = (editorForm.getFieldValue('products') || []).map((x: any) => x?.productCode).filter((c: string) => !!c)
          if (existing.includes(code)) { message.warning(t('posAdmin.promotions.productAlreadyInStrategy')); return }
          const fields = editorForm.getFieldValue('products') || []
          editorForm.setFieldsValue({ products: [...fields, { productCode: code, unitWeight: 1 }] })
          ;(async () => {
            try {
              const info = await getPosProductById(code)
              if (info?.productCode) setProductInfoMap((prev) => ({ ...prev, [info.productCode]: info }))
            } catch { /* ignore */ }
          })()
        }}
        onPickMany={(codes) => {
          if (!codes?.length) return
          const existing = (editorForm.getFieldValue('products') || []).map((x: any) => x?.productCode).filter((c: string) => !!c)
          const addCodes = codes.filter((c) => !existing.includes(c))
          if (!addCodes.length) { message.warning(t('posAdmin.promotions.allProductsInStrategy')); return }
          const fields = editorForm.getFieldValue('products') || []
          editorForm.setFieldsValue({ products: [...fields, ...addCodes.map((code) => ({ productCode: code, unitWeight: 1 }))] })
          ;(async () => {
            await Promise.all(addCodes.map(async (code) => {
              try {
                const info = await getPosProductById(code)
                if (info?.productCode) setProductInfoMap((prev) => ({ ...prev, [info.productCode]: info }))
              } catch { /* ignore */ }
            }))
          })()
        }}
      />
    </Card>
  )
}
