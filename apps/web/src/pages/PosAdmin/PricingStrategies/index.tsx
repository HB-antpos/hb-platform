import { PlusOutlined, QuestionCircleOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Form,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Select,
  Space,
  Switch,
  Tag,
  Tooltip,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getActiveLocalSuppliers } from '../../../services/localSupplierService'
import {
  createStrategy,
  deleteStrategy,
  evaluatePricing,
  getStrategyById,
  getStrategyGrid,
  updateStrategy,
} from '../../../services/pricingStrategyService'
import { getActiveStores } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type {
  CreatePricingStrategyDto,
  PricingStrategyListDto,
  PricingStrategyRuleDto,
  PricingStrategyTargetDto,
  UpdatePricingStrategyDto,
} from '../../../types/pricingStrategy'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'

import IntervalEditor from './IntervalEditor'
import { PricingCell, StrategyExpanded } from './StrategyViews'
import { boundedPrice, defaultCurve, nodesFromRules, rulesFromNodes } from './pricingCurve'
import { intervalsError, isEditable, priceDrops } from './pricingIntervals'

type DataType = PricingStrategyListDto & { key: string }

export default function PricingStrategiesPage() {
  const { t } = useTranslation()
  // 写操作与后端 PricingStrategy.Edit 策略对齐，仅有 View 的用户只能查看与试算。
  const access = useAuthStore((state) => state.access)
  const canEdit = access.canEditPricing
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<DataType[]>([])
  const [total, setTotal] = useState(0)
  const [pageSize, setPageSize] = useState(50)
  const [page, setPage] = useState(1)
  const [sortField, setSortField] = useState<string | undefined>()
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend' | undefined>()
  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [supplierOptions, setSupplierOptions] = useState<{ label: string; value: string }[]>([])
  const [editorOpen, setEditorOpen] = useState(false)
  // 区间编辑器模式（固定 / 曲线区间）；含旧版 Step、Exponential 等规则时为 false，走原有区间规则表单
  const [curveMode, setCurveMode] = useState(true)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editorForm] = Form.useForm()
  const watchedDetails = Form.useWatch('details', editorForm) as PricingStrategyRuleDto[] | undefined
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)

  const storeNameMap = useMemo(() => new Map(storeOptions.map((o) => [o.value, o.label])), [storeOptions])
  const supplierNameMap = useMemo(() => new Map(supplierOptions.map((o) => [o.value, o.label])), [supplierOptions])

  const [testForm] = Form.useForm()
  const [testResult, setTestResult] = useState<{ retailPrice: number; rate: number; strategyId?: string | null; strategyName?: string | null } | null>(null)
  const [togglingId, setTogglingId] = useState<string | null>(null)

  const loadData = async () => {
    if (!mountedRef.current) {
      return
    }

    await runLatestGuardedRequest(listRequestGuardRef.current, async () => {
      const values = form.getFieldsValue()
      const sortModel: Record<string, string> = {}
      if (sortField && sortOrder) {
        sortModel[sortField] = sortOrder === 'ascend' ? 'asc' : 'desc'
      }
      const filterModel: Record<string, unknown> = {}
      if (values.storeCode) filterModel.storeCode = { FilterType: 'text', Type: 'equals', Filter: values.storeCode }
      if (values.supplierCode) filterModel.supplierCode = { FilterType: 'text', Type: 'equals', Filter: values.supplierCode }
      return getStrategyGrid({
        StartRow: (page - 1) * pageSize,
        EndRow: page * pageSize - 1,
        PageSize: pageSize,
        GlobalSearch: values.keyword || undefined,
        FilterModel: Object.keys(filterModel).length ? filterModel : undefined,
        SortModel: Object.keys(sortModel).length && sortField ? [{ ColId: sortField, Sort: sortModel[sortField] }] : undefined,
      } as Record<string, unknown>)
    }, {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        const items = result?.items ?? []
        setTotal(result?.total ?? 0)
        setData(items.map((it) => ({ ...it, key: String(it.id) })))
      },
      onError: () => message.error(t('message.loadFailed', '加载失败')),
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

  const openCreate = () => {
    setEditingId(null)
    setCurveMode(true)
    editorForm.resetFields()
    editorForm.setFieldsValue({
      name: '',
      level: 'Global',
      priority: 0,
      isEnabled: true,
      targetsStores: [],
      targetsSuppliers: [],
      details: defaultCurve(),
    })
    setEditorOpen(true)
  }

  const openEdit = async (id: string) => {
    try {
      const dto = await getStrategyById(id)
      if (!dto) {
        message.error(t('posAdmin.pricing.notFound', '未找到策略'))
        return
      }
      setEditingId(id)
      // 全部是固定 / 带节点售价的曲线区间时用区间编辑器，否则保留原有的旧版区间规则表单
      setCurveMode(isEditable(dto.details))
      const stores = dto.targets.filter((t) => t.targetType === 'Store').map((t) => t.targetCode!).filter(Boolean)
      const suppliers = dto.targets.filter((t) => t.targetType === 'Supplier').map((t) => t.targetCode!).filter(Boolean)
      editorForm.setFieldsValue({
        name: dto.name,
        level: dto.level,
        priority: dto.priority,
        isEnabled: dto.isEnabled,
        targetsStores: stores,
        targetsSuppliers: suppliers,
        details: dto.details.map((r) => ({
          id: r.id,
          minPrice: r.minPrice,
          maxPrice: r.maxPrice,
          startRetailPrice: r.startRetailPrice,
          endRetailPrice: r.endRetailPrice,
          curveBend: r.curveBend,
          startRate: r.startRate,
          endRate: r.endRate,
          algorithm: r.algorithm,
        })),
      })
      setEditorOpen(true)
    } catch {
      message.error(t('message.loadFailed', '加载失败'))
    }
  }

  const saveEditor = async () => {
    try {
      const v = await editorForm.validateFields()
      if (v.level === 'Store' && (!v.targetsStores || v.targetsStores.length === 0)) {
        message.warning(t('posAdmin.pricing.storeLevelRequired', '分店级别策略必须选择至少一个分店'))
        return
      }
      if (v.level === 'Supplier' && (!v.targetsSuppliers || v.targetsSuppliers.length === 0)) {
        message.warning(t('posAdmin.pricing.supplierLevelRequired', '供应商级别策略必须选择至少一个供应商'))
        return
      }
      const rules: PricingStrategyRuleDto[] = (v.details || []).map((r: any) => ({
        id: r.id,
        minPrice: Number(r.minPrice),
        maxPrice: Number(r.maxPrice),
        startRetailPrice: r.startRetailPrice,
        endRetailPrice: r.endRetailPrice,
        curveBend: r.curveBend,
        startRate: Number(r.startRate),
        endRate: Number(r.endRate),
        algorithm: r.algorithm,
      }))
      if (curveMode) {
        const error = intervalsError(rules)
        if (error) { message.warning(error); return }
        // 交界处成本变大、售价反而降低（阶梯定价）：保存前必须二次确认
        const drops = priceDrops(rules)
        if (drops.length > 0) {
          const confirmed = await new Promise<boolean>((resolve) => Modal.confirm({
            title: t('posAdmin.pricing.confirmDropsTitle', '存在交界售价回落，确认保存？'),
            width: 480,
            content: <div>
              <div style={{ marginBottom: 8 }}>{t('posAdmin.pricing.confirmDropsHint', '以下交界处进货价变大时售价反而降低（阶梯定价），请确认这是有意设置：')}</div>
              {drops.map((d) => <div key={d.index} style={{ fontVariantNumeric: 'tabular-nums' }}>• 进货价 {d.at.toFixed(2)}：{d.from.toFixed(2)} → {d.to.toFixed(2)}（回落 {(d.from - d.to).toFixed(2)}）</div>)}
            </div>,
            okText: t('posAdmin.pricing.confirmSave', '确认保存'),
            cancelText: t('common.cancel', '取消'),
            onOk: () => resolve(true),
            onCancel: () => resolve(false),
          }))
          if (!confirmed) return
        }
      }
      const targets: PricingStrategyTargetDto[] = [
        ...(v.targetsStores || []).map((code: string) => ({ targetType: 'Store' as const, targetCode: code })),
        ...(v.targetsSuppliers || []).map((code: string) => ({ targetType: 'Supplier' as const, targetCode: code })),
      ]
      const payload = { name: v.name, level: v.level, priority: Number(v.priority || 0), isEnabled: !!v.isEnabled, details: rules, targets }
      if (editingId === null) {
        await createStrategy(payload as CreatePricingStrategyDto)
      } else {
        await updateStrategy(editingId, payload as UpdatePricingStrategyDto)
      }
      message.success(t('message.saveSuccess', '保存成功'))
      setEditorOpen(false)
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch {
      message.error(t('message.saveFailed', '保存失败'))
    }
  }

  const handleDelete = async (id: string) => {
    try {
      await deleteStrategy(id)
      message.success(t('message.deleteSuccess', '删除成功'))
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch {
      message.error(t('message.deleteFailed', '删除失败'))
    }
  }

  // 区间编辑器的底部摘要：区间数与组成、覆盖成本范围、校验结果（与保存时的校验同源）
  const curveSummary = (() => {
    if (!curveMode || !watchedDetails?.length) return null
    const error = intervalsError(watchedDetails), drops = priceDrops(watchedDetails).length
    const fixedCount = watchedDetails.filter(r => r.algorithm === 'Fixed').length
    const low = Math.min(...watchedDetails.map(r => r.minPrice)), high = Math.max(...watchedDetails.map(r => r.maxPrice))
    return <>
      <span>共 {watchedDetails.length} 个区间（固定 {fixedCount} · 曲线 {watchedDetails.length - fixedCount}），覆盖成本 {low.toFixed(2)} ～ {high >= 99999 ? '不限' : high.toFixed(2)}</span>
      <span style={{ color: error ? '#cf1322' : drops ? '#d4380d' : '#52c41a' }}>● {error ?? (drops ? `${drops} 处交界回落，保存需确认` : '校验通过')}</span>
    </>
  })()

  // 列表上直接启停：取回完整策略后只改 isEnabled 再整体保存（与编辑弹窗同一套后端校验）
  const toggleEnabled = async (record: DataType, next: boolean) => {
    setTogglingId(record.id)
    try {
      const dto = await getStrategyById(record.id)
      await updateStrategy(record.id, {
        name: dto.name,
        level: dto.level,
        priority: dto.priority,
        isEnabled: next,
        details: dto.details,
        targets: dto.targets.map(({ targetType, targetCode }) => ({ targetType, targetCode })),
      })
      message.success(t('message.saveSuccess', '保存成功'))
      if (mountedRef.current) await latestLoadDataRef.current()
    } catch (error) {
      message.error((error as Error)?.message || t('message.saveFailed', '保存失败'))
    } finally {
      setTogglingId(null)
    }
  }

  const levelTag = (v: string) => {
    const label = v === 'Supplier' ? t('posAdmin.pricing.supplierLevel', '供应商') : v === 'Store' ? t('posAdmin.pricing.storeLevel', '分店') : t('posAdmin.pricing.globalLevel', '全局')
    return <Tag color={v === 'Supplier' ? 'blue' : v === 'Store' ? 'green' : 'gold'} style={{ marginInlineEnd: 0 }}>{label}</Tag>
  }
  // 目标较多时只显示前 3 个，其余折叠成 +N 并在悬停里列出
  const scopeChips = (codes: string[], names: Map<string, string>, color: string) => {
    const shown = codes.slice(0, 3), rest = codes.slice(3)
    return <>
      {shown.map((c) => <Tag key={c} color={color} style={{ marginInlineEnd: 0 }}>{names.get(c) ?? c}</Tag>)}
      {rest.length > 0 && <Tooltip title={rest.map((c) => names.get(c) ?? c).join('、')}><Tag style={{ marginInlineEnd: 0 }}>+{rest.length}</Tag></Tooltip>}
    </>
  }

  const columns: ColumnsType<DataType> = [
    {
      title: t('posAdmin.pricing.strategy', '策略'), dataIndex: 'name', key: 'name', sorter: true, width: 260,
      render: (_, r) => <div><div style={{ fontWeight: 500 }}>{r.name}</div><div style={{ color: '#8c8c8c', fontSize: 12 }}>{t('posAdmin.pricing.priority', '优先级')} {r.priority}</div></div>,
    },
    {
      title: t('posAdmin.pricing.scope', '适用范围'), key: 'scope',
      render: (_, r) => {
        const stores = r.storeCodes ?? [], suppliers = r.supplierCodes ?? []
        const hint = !stores.length && !suppliers.length ? '所有分店 · 所有供应商' : !stores.length ? '所有分店' : !suppliers.length ? '所有供应商' : ''
        return <Space size={[4, 4]} wrap>
          {levelTag(r.level)}
          {scopeChips(stores, storeNameMap, 'cyan')}
          {scopeChips(suppliers, supplierNameMap, 'purple')}
          {hint && <span style={{ color: '#8c8c8c', fontSize: 12 }}>{hint}</span>}
        </Space>
      },
    },
    { title: t('posAdmin.pricing.pricingMethod', '定价方式'), key: 'pricing', width: 290, render: (_, r) => <PricingCell record={r} /> },
    {
      title: t('posAdmin.pricing.status', '状态'), dataIndex: 'isEnabled', key: 'isEnabled', sorter: true, width: 90,
      render: (v: boolean, r) => (
        <Popconfirm disabled={!canEdit} title={v ? t('posAdmin.pricing.confirmDisable', '确认停用该策略？') : t('posAdmin.pricing.confirmEnable', '确认启用该策略？')} description={t('posAdmin.pricing.toggleImpact', '会立即影响自动定价结果')} onConfirm={() => toggleEnabled(r, !v)}>
          <Switch checked={v} disabled={!canEdit} loading={togglingId === r.id} aria-label={r.name} />
        </Popconfirm>
      ),
    },
    {
      title: t('column.actions', '操作'), key: 'actions', width: 140,
      render: (_, record) => (
        <Space size={0}>
          <Button type="link" disabled={!canEdit} onClick={() => openEdit(String(record.id))}>{t('common.edit', '编辑')}</Button>
          <Popconfirm title={t('posAdmin.pricing.confirmDeleteStrategy', '确认删除该策略？')} description={t('posAdmin.pricing.deleteIrreversible', '删除后无法恢复')} okText={t('common.delete', '删除')} cancelText={t('common.cancel', '取消')} onConfirm={() => handleDelete(String(record.id))}>
            <Button type="link" danger disabled={!canEdit}>{t('common.delete', '删除')}</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  const onTableChange = (pagination: any, _filters: any, sorter: any) => {
    if (sorter?.field) {
      setSortField(String(sorter.field))
      setSortOrder(sorter.order)
    } else {
      setSortField(undefined)
      setSortOrder(undefined)
    }
    setPage(pagination.current)
    setPageSize(pagination.pageSize)
  }

  useEffect(() => {
    ;(async () => {
      try {
        const storeOpts = await getActiveStores()
        setStoreOptions(storeOpts)
      } catch { /* ignore */ }
      try {
        const suppliers = await getActiveLocalSuppliers()
        setSupplierOptions(suppliers.map((s) => ({ label: s.name || s.localSupplierCode, value: s.localSupplierCode })))
      } catch { /* ignore */ }
    })()
  }, [])

  useEffect(() => {
    void loadData()
    return () => listRequestGuardRef.current.invalidate()
  }, [page, pageSize, sortField, sortOrder])

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16, gap: 12, flexWrap: 'wrap' }}>
        <div>
          <h1 style={{ fontSize: 20, fontWeight: 600, margin: 0 }}>{t('posAdmin.pricing.title', '自动价格策略管理')}</h1>
          <div style={{ color: '#8c8c8c', fontSize: 13, marginTop: 2 }}>{t('posAdmin.pricing.subtitle', '按分店、供应商决定进货价到零售价的换算方式')}</div>
        </div>
        <Button type="primary" icon={<PlusOutlined />} disabled={!canEdit} onClick={openCreate}>{t('posAdmin.pricing.createStrategy', '新建策略')}</Button>
      </div>

      <Card style={{ marginBottom: 16 }} styles={{ body: { padding: '14px 18px' } }}>
        <Form form={testForm} onFinish={async (v) => {
          try {
            const purchasePrice = Number(v.purchasePrice)
            const result = await evaluatePricing({ purchasePrice, storeCode: v.storeCode, supplierCode: v.supplierCode })
            setTestResult({
              retailPrice: result.retailPrice,
              // 优先用后端返回的实际成率；后端较旧时用 零售价 / 进货价 兜底
              rate: result.effectiveRate ?? (purchasePrice > 0 ? result.retailPrice / purchasePrice : 0),
              strategyId: result.strategyId,
              strategyName: result.strategyName,
            })
          } catch (error) { message.error((error as Error)?.message || t('posAdmin.pricing.calcFailed', '计算失败')) }
        }} style={{ display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap' }}>
          <div style={{ display: 'flex', flexDirection: 'column', lineHeight: 1.3 }}>
            <b>{t('posAdmin.pricing.quickTry', '快速试算')}</b>
            <span style={{ color: '#8c8c8c', fontSize: 12 }}>{t('posAdmin.pricing.quickTryHint', '输入进货价，看命中哪条策略')}</span>
          </div>
          <Form.Item name="storeCode" style={{ marginBottom: 0 }}><Select allowClear showSearch optionFilterProp="label" placeholder={t('posAdmin.pricing.targetStores', '分店')} options={storeOptions} style={{ width: 170 }} /></Form.Item>
          <Form.Item name="supplierCode" style={{ marginBottom: 0 }}><Select allowClear showSearch optionFilterProp="label" placeholder={t('posAdmin.pricing.targetSuppliers', '供应商')} options={supplierOptions} style={{ width: 170 }} /></Form.Item>
          <Form.Item name="purchasePrice" rules={[{ required: true, message: t('posAdmin.pricing.purchasePriceRequired', '请输入进货价') }]} style={{ marginBottom: 0 }}><InputNumber min={0} precision={2} placeholder={t('posAdmin.pricing.purchasePrice', '进货价')} style={{ width: 150 }} /></Form.Item>
          <Form.Item style={{ marginBottom: 0 }}><Button type="primary" htmlType="submit">{t('posAdmin.pricing.calculate', '计算')}</Button></Form.Item>
          {testResult && <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 20, paddingLeft: 20, borderLeft: '1px solid #f0f0f0' }} aria-live="polite">
            <div style={{ fontSize: 28, fontWeight: 600, color: '#1677ff', lineHeight: 1, fontVariantNumeric: 'tabular-nums' }}>
              <span style={{ fontSize: 14, fontWeight: 400, color: '#8c8c8c', marginRight: 4 }}>{t('posAdmin.pricing.retailPrice', '零售价')}</span>{testResult.retailPrice.toFixed(2)}
            </div>
            <div style={{ fontSize: 12, color: '#595959', lineHeight: 1.7 }}>
              {t('posAdmin.pricing.rate', '成率')} <b style={{ fontVariantNumeric: 'tabular-nums' }}>{testResult.rate.toFixed(4)}</b><br />
              {t('posAdmin.pricing.hit', '命中')}：{testResult.strategyId
                ? <a onClick={() => openEdit(String(testResult.strategyId))}>{testResult.strategyName ?? testResult.strategyId} ›</a>
                : <span style={{ color: '#8c8c8c' }}>{t('posAdmin.pricing.noStrategyHit', '未命中策略（默认 2.5 倍）')}</span>}
            </div>
          </div>}
        </Form>
      </Card>

      <Card styles={{ body: { padding: 0 } }}>
        <Form form={form} layout="inline" onFinish={loadData} style={{ padding: '14px 16px', borderBottom: '1px solid #f0f0f0', rowGap: 8 }}>
          <Form.Item name="storeCode"><Select allowClear showSearch optionFilterProp="label" placeholder={t('posAdmin.pricing.targetStores', '分店')} options={storeOptions} style={{ width: 200 }} /></Form.Item>
          <Form.Item name="supplierCode"><Select allowClear showSearch optionFilterProp="label" placeholder={t('posAdmin.pricing.targetSuppliers', '供应商')} options={supplierOptions} style={{ width: 200 }} /></Form.Item>
          <Form.Item name="keyword"><Input allowClear placeholder={t('posAdmin.pricing.searchName', '搜索策略名称')} style={{ width: 240 }} /></Form.Item>
          <Form.Item><Space><Button type="primary" htmlType="submit">{t('common.query', '查询')}</Button><Button onClick={() => { form.resetFields(); loadData() }}>{t('common.reset', '重置')}</Button></Space></Form.Item>
          <span style={{ marginLeft: 'auto', color: '#8c8c8c', fontSize: 13, alignSelf: 'center' }}>
            {t('posAdmin.pricing.totalCount', '共 {{count}} 条', { count: total })}{' '}
            <Tooltip title={t('posAdmin.pricing.matchOrderHint', '匹配顺序：供应商 + 分店 › 仅供应商 › 仅分店 › 全局；同一类别内，优先级数字大的先匹配。')}><QuestionCircleOutlined aria-label={t('posAdmin.pricing.matchOrder', '匹配顺序')} /></Tooltip>
          </span>
        </Form>
        <MeasuredTable metricId="pos-admin.pricing-strategies.table-3"
          rowKey="key"
          loading={loading}
          dataSource={data}
          columns={columns}
          pagination={{ total, current: page, pageSize, showSizeChanger: true, pageSizeOptions: ['10', '20', '50', '100', '200'], style: { padding: '0 16px' } }}
          onChange={onTableChange}
          expandable={{
            expandedRowRender: (record: DataType) => <StrategyExpanded record={record} />,
            rowExpandable: (record: DataType) => record.pricingMode === 'Fixed' || (record.intervals?.length ?? 0) > 0 || (record.nodes?.length ?? 0) > 0,
          }}
        />
      </Card>

      <Modal open={editorOpen} title={editingId === null ? t('posAdmin.pricing.newStrategy', '新建策略') : t('posAdmin.pricing.editStrategy', '编辑策略')} onCancel={() => setEditorOpen(false)} width="min(1220px, calc(100vw - 32px))" forceRender footer={<div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <div style={{ marginRight: 'auto', display: 'flex', gap: 14, alignItems: 'center', fontSize: 13, color: '#595959', textAlign: 'left' }}>{curveSummary}</div>
        <Button onClick={() => setEditorOpen(false)}>{t('common.cancel', '取消')}</Button>
        <Button type="primary" disabled={!canEdit} onClick={saveEditor}>{t('common.save', '保存')}</Button>
      </div>}>
        <Form form={editorForm} layout="vertical" disabled={!canEdit}>
          <div style={{ display: 'flex', gap: 14, flexWrap: 'wrap' }}>
            <Form.Item name="name" label={t('posAdmin.pricing.strategyName', '名称')} rules={[{ required: true }]} style={{ width: 280, marginBottom: 12 }}><Input /></Form.Item>
            <Form.Item name="level" label={t('posAdmin.pricing.level', '级别')} rules={[{ required: true }]} style={{ width: 140, marginBottom: 12 }}>
              <Select options={[{ value: 'Supplier', label: t('posAdmin.pricing.supplierLevel', '供应商') }, { value: 'Store', label: t('posAdmin.pricing.storeLevel', '分店') }, { value: 'Global', label: t('posAdmin.pricing.globalLevel', '全局') }]} />
            </Form.Item>
            <Form.Item name="priority" label={t('posAdmin.pricing.priority', '优先级')} style={{ width: 110, marginBottom: 12 }}><InputNumber min={0} style={{ width: '100%' }} /></Form.Item>
            <Form.Item name="targetsStores" label={t('posAdmin.pricing.targetStores', '分店')} style={{ flex: '1 1 220px', minWidth: 0, marginBottom: 12 }}><Select mode="multiple" options={storeOptions} showSearch optionFilterProp="label" allowClear /></Form.Item>
            <Form.Item name="targetsSuppliers" label={t('posAdmin.pricing.targetSuppliers', '供应商')} style={{ flex: '1 1 220px', minWidth: 0, marginBottom: 12 }}><Select mode="multiple" options={supplierOptions} showSearch optionFilterProp="label" allowClear /></Form.Item>
            <Form.Item name="isEnabled" label={t('posAdmin.pricing.isEnabled', '启用')} valuePropName="checked" style={{ width: 60, marginBottom: 12 }}><Switch /></Form.Item>
          </div>
          {curveMode ? <Form.Item name="details" rules={[{ validator: (_, rules) => { const error = intervalsError(rules || []); return error ? Promise.reject(new Error(error)) : Promise.resolve() } }]}><IntervalEditor /></Form.Item> : <>
          <Alert type="info" showIcon style={{ marginBottom: 12 }} message="原有区间规则" description="已有 Linear 计算已升级为零售价插值。打开时保留现有配置；转换按钮切换为图形编辑，并将节点吸附到合法尾数、应用 1.5～5 成率约束。编辑结果保存后生效。" action={<Button onClick={() => {
            const old = editorForm.getFieldValue('details') as PricingStrategyRuleDto[]
            if (!old?.length || old.some((r, i) => i > 0 && (r.minPrice !== old[i - 1].maxPrice || Math.abs(r.minPrice * r.startRate - old[i - 1].maxPrice * old[i - 1].endRate) > .000001))) { message.warning('原有区间不连续，请先对齐相邻成本与售价端点'); return }
            const converted = rulesFromNodes(nodesFromRules(old).map(n => ({ cost: n.cost, price: boundedPrice(n.cost, n.price) })))
            const error = intervalsError(converted)
            if (error) { message.warning(error); return }
            editorForm.setFieldValue('details', converted); setCurveMode(true)
          }}>转换为曲线编辑</Button>} />
          <Form.List name="details">
            {(fields, { add, remove }) => (
              <>
                {fields.map(({ key, name, ...restField }) => (
                  <div key={key} style={{ marginBottom: 16, padding: 12, background: '#fafafa', borderRadius: 8, border: '1px solid #f0f0f0' }}>
                    <Space style={{ width: '100%' }} wrap align="start">
                      <Form.Item {...restField} name={[name, 'minPrice']} rules={[{ required: true }]} label={t('posAdmin.pricing.minPrice', '进货价下限')} style={{ marginBottom: 8 }}><InputNumber min={0} precision={2} style={{ width: 120 }} /></Form.Item>
                      <Form.Item {...restField} name={[name, 'maxPrice']} rules={[{ required: true }]} label={t('posAdmin.pricing.maxPrice', '进货价上限')} style={{ marginBottom: 8 }}><InputNumber min={0} precision={2} style={{ width: 120 }} /></Form.Item>
                      <Form.Item {...restField} name={[name, 'startRate']} rules={[{ required: true }]} label={t('posAdmin.pricing.startRate', '起始上浮率')} style={{ marginBottom: 8 }}><InputNumber min={0} precision={4} style={{ width: 100 }} /></Form.Item>
                      <Form.Item {...restField} name={[name, 'endRate']} rules={[{ required: true }]} label={t('posAdmin.pricing.endRate', '结束上浮率')} style={{ marginBottom: 8 }}><InputNumber min={0} precision={4} style={{ width: 100 }} /></Form.Item>
                      <Form.Item {...restField} name={[name, 'algorithm']} rules={[{ required: true }]} label={t('posAdmin.pricing.algorithm', '算法')} style={{ marginBottom: 8 }}>
                        <Select style={{ width: 120 }} options={[{ value: 'Linear', label: t('posAdmin.pricing.linear', '线性') }, { value: 'Exponential', label: t('posAdmin.pricing.exponential', '指数') }, { value: 'Step', label: t('posAdmin.pricing.step', '阶梯') }]} />
                      </Form.Item>
                      <Button type="link" danger onClick={() => remove(name)} style={{ marginTop: 30 }}>{t('posAdmin.pricing.deleteRule', '删除')}</Button>
                    </Space>
                  </div>
                ))}
                <Form.Item><Button type="dashed" onClick={() => add({ minPrice: 10, maxPrice: 100, startRate: 1.5, endRate: 2.5, algorithm: 'Linear' })}>{t('posAdmin.pricing.addRule', '添加规则')}</Button></Form.Item>
              </>
            )}
          </Form.List>
          </>}
        </Form>
      </Modal>
    </div>
  )
}
