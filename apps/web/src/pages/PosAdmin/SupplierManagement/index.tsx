import { CloudUploadOutlined, PlusOutlined } from '@ant-design/icons'
import {
  Button,
  Card,
  Form,
  Input,
  Modal,
  Pagination,
  Segmented,
  Select,
  Space,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useLayoutEffect, useRef, useState, type Key } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  createLocalSupplier,
  getLocalSuppliers,
  syncLocalSuppliersToHq,
  updateLocalSupplier,
} from '../../../services/localSupplierService'
import { useAuthStore } from '../../../store/auth'
import type { LocalSupplierDto, LocalSupplierImageTemplateFilter } from '../../../types/localSupplier'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'
import SupplierDrawer, { type SupplierFormValues } from './SupplierDrawer'
import {
  buildSupplierInvoicesLink,
  describeSupplierImageTemplate,
  formatPurchaseDate,
} from './supplierPageLogic'
import supplierMessagesEn from './supplierMessages.en.json'
import supplierMessagesZh from './supplierMessages.zh.json'
import './supplierManagement.css'

registerPageMessages({ zh: supplierMessagesZh, en: supplierMessagesEn })

const SORT_FIELD_MAP: Record<string, string> = {
  localSupplierCode: 'localsuppliercode',
  name: 'name',
  status: 'status',
  contactPerson: 'contactperson',
  imageBaseUrl: 'imagebaseurl',
  lastPurchaseDate: 'lastpurchasedate',
}

type StatusFilter = 'all' | '1' | '0'

function toPayload(values: SupplierFormValues) {
  return {
    name: values.name,
    status: values.status ? 1 : 0,
    contactPerson: values.contactPerson,
    phone: values.phone,
    email: values.email,
    remark: values.remark,
    imageBaseUrl: values.imageBaseUrl?.trim() || undefined,
  }
}

export default function SupplierManagementPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  // 同步、新增、编辑与后端 AustralianSuppliers.Edit 策略对齐，仅有 View 的用户只能浏览列表和详情。
  const access = useAuthStore((state) => state.access)
  const canEdit = access.canEditAustralianSuppliers
  // 近期进货单 / 跳转分店进货单需要进货单查看权限，没有就不显示入口。
  const canViewInvoices = access.canManageLocalPurchase
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<LocalSupplierDto[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [keyword, setKeyword] = useState('')
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all')
  const [imageTemplate, setImageTemplate] = useState<LocalSupplierImageTemplateFilter | undefined>(undefined)
  const [sortBy, setSortBy] = useState('name')
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend'>('ascend')
  const [selectedRowKeys, setSelectedRowKeys] = useState<Key[]>([])
  const [syncingToHq, setSyncingToHq] = useState(false)
  // 抽屉：open 且 drawerSupplier 为空 = 新建；有值 = 编辑 / 查看。
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [drawerSupplier, setDrawerSupplier] = useState<LocalSupplierDto | null>(null)
  const [saving, setSaving] = useState(false)
  const [supplierForm] = Form.useForm<SupplierFormValues>()
  const wrapRef = useRef<HTMLDivElement>(null)
  const toolbarRef = useRef<HTMLDivElement>(null)
  const tableRegionRef = useRef<HTMLDivElement>(null)
  const pagerRef = useRef<HTMLDivElement>(null)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const [tableScrollY, setTableScrollY] = useState<number | undefined>(undefined)
  // 操作条出现 / 消失会改变表格可用高度，作为重新测量的依据。
  const hasSelection = selectedRowKeys.length > 0

  const loadData = async () => {
    if (!mountedRef.current) {
      return
    }

    await runLatestGuardedRequest(listRequestGuardRef.current, () => getLocalSuppliers({
        pageIndex: page,
        pageSize,
        keyword,
        status: statusFilter === 'all' ? undefined : statusFilter,
        imageTemplate,
        sortBy: SORT_FIELD_MAP[sortBy] || sortBy,
        sortOrder,
      }), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result?.items ?? [])
        setTotal(result?.total ?? 0)
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

  useEffect(() => {
    void loadData()
    return () => listRequestGuardRef.current.invalidate()
  }, [page, pageSize, keyword, statusFilter, imageTemplate, sortBy, sortOrder])

  useLayoutEffect(() => {
    let frameId: number | null = null

    const readOuterHeight = (element: HTMLElement | null) => {
      if (!element) {
        return 0
      }

      const style = window.getComputedStyle(element)
      const marginTop = Number.parseFloat(style.marginTop) || 0
      const marginBottom = Number.parseFloat(style.marginBottom) || 0
      return Math.ceil(element.getBoundingClientRect().height + marginTop + marginBottom)
    }

    const calc = () => {
      const region = tableRegionRef.current
      if (!region) {
        return
      }

      const tableHeader = region.querySelector('.ant-table-thead') as HTMLElement | null
      const tableBody = region.querySelector('.ant-table-body') as HTMLElement | null
      const tableHeaderHeight = readOuterHeight(tableHeader)
      const horizontalScrollbarHeight = tableBody ? Math.max(0, tableBody.offsetHeight - tableBody.clientHeight) : 0
      // scroll.y 只代表表体高度，不包含表头；扣除表头与横向滚动条高度，
      // 避免固定“操作”列继续画到底部分页区域。
      const available = Math.floor(region.clientHeight - tableHeaderHeight - horizontalScrollbarHeight - 8)
      const nextScrollY = available > 200 ? available : 200
      // 仅在高度变化超过小阈值时更新，避免无意义重渲染。
      setTableScrollY((current) => (
        current === undefined || Math.abs(current - nextScrollY) > 4 ? nextScrollY : current
      ))
    }

    const scheduleCalc = () => {
      if (frameId != null) {
        window.cancelAnimationFrame(frameId)
      }
      frameId = window.requestAnimationFrame(() => {
        frameId = null
        calc()
      })
    }

    scheduleCalc()
    window.addEventListener('resize', scheduleCalc)

    if (typeof ResizeObserver === 'undefined') {
      return () => {
        if (frameId != null) window.cancelAnimationFrame(frameId)
        window.removeEventListener('resize', scheduleCalc)
      }
    }

    const observer = new ResizeObserver(scheduleCalc)
    for (const element of [wrapRef.current, toolbarRef.current, tableRegionRef.current, pagerRef.current]) {
      if (element) {
        observer.observe(element)
      }
    }

    return () => {
      if (frameId != null) window.cancelAnimationFrame(frameId)
      window.removeEventListener('resize', scheduleCalc)
      observer.disconnect()
    }
  }, [data.length, pageSize, total, hasSelection])

  // 「从 HQ 同步」（HQ → HBweb）已于 2026-09-29 停用；「同步所选到 HQ」方向相反，继续保留。
  const handleSyncToHq = () => {
    if (!selectedRowKeys.length) {
      message.warning(t('posAdmin.suppliers.selectSyncFirst', '请先选择要同步的澳洲供应商'))
      return
    }

    Modal.confirm({
      title: t('posAdmin.suppliers.syncToHqTitle', '同步澳洲供应商到 HQ'),
      content: t(
        'posAdmin.suppliers.syncToHqConfirm',
        '确认将选中的 {{count}} 个澳洲供应商同步到 HQ？将写入代码、名称、联系人和 Email。',
        { count: selectedRowKeys.length },
      ),
      okText: t('posAdmin.suppliers.confirmSync', '确认同步'),
      cancelText: t('common.cancel', '取消'),
      onOk: async () => {
        try {
          setSyncingToHq(true)
          const result = await syncLocalSuppliersToHq(selectedRowKeys.map(String))
          message.success(
            t('posAdmin.suppliers.syncToHqComplete', {
              created: result.createdCount ?? 0,
              updated: result.updatedCount ?? 0,
              skipped: result.skippedCount ?? 0,
            }),
          )
          setSelectedRowKeys([])
        } catch {
          message.error(t('posAdmin.suppliers.syncToHqFailed', '同步到 HQ 失败'))
        } finally {
          setSyncingToHq(false)
        }
      },
    })
  }

  const openCreate = () => {
    setDrawerSupplier(null)
    supplierForm.resetFields()
    supplierForm.setFieldsValue({ status: true })
    setDrawerOpen(true)
  }

  const closeDrawer = () => {
    setDrawerOpen(false)
    setDrawerSupplier(null)
    supplierForm.resetFields()
  }

  const handleCreate = async () => {
    const values = await supplierForm.validateFields()
    try {
      setSaving(true)
      await createLocalSupplier(toPayload(values))
      message.success(t('message.createSuccess'))
      closeDrawer()
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch {
      message.error(t('message.createFailed'))
    } finally {
      setSaving(false)
    }
  }

  const openEdit = (record: LocalSupplierDto) => {
    setDrawerSupplier(record)
    supplierForm.setFieldsValue({
      name: record.name,
      status: record.status === 1,
      contactPerson: record.contactPerson,
      phone: record.phone,
      email: record.email,
      remark: record.remark,
      imageBaseUrl: record.imageBaseUrl,
    })
    setDrawerOpen(true)
  }

  const handleEdit = async () => {
    if (!drawerSupplier) return
    const values = await supplierForm.validateFields()
    try {
      setSaving(true)
      await updateLocalSupplier(drawerSupplier.localSupplierCode, toPayload(values))
      message.success(t('message.saveSuccess', '保存成功'))
      closeDrawer()
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch {
      message.error(t('message.saveFailed', '保存失败'))
    } finally {
      setSaving(false)
    }
  }

  const sortOrderOf = (field: string) => (sortBy === field ? sortOrder : undefined)

  const columns: ColumnsType<LocalSupplierDto> = [
    {
      title: t('posAdmin.supplierPage.columns.supplier'),
      dataIndex: 'name',
      sorter: true,
      sortOrder: sortOrderOf('name'),
      render: (_value: string, record) => (
        <div className="supplier-cell-name">
          <span className="supplier-name">{record.name}</span>
          <span className="supplier-code-tag">{record.localSupplierCode}</span>
        </div>
      ),
    },
    {
      title: t('column.status'),
      dataIndex: 'status',
      width: 96,
      sorter: true,
      sortOrder: sortOrderOf('status'),
      render: (value: number) => (
        <span className={value === 1 ? 'supplier-status is-active' : 'supplier-status'}>
          {value === 1 ? t('common.active') : t('common.inactive')}
        </span>
      ),
    },
    {
      title: t('posAdmin.supplierPage.columns.contact'),
      dataIndex: 'contactPerson',
      sorter: true,
      sortOrder: sortOrderOf('contactPerson'),
      render: (_value: string | undefined, record) => {
        const secondLine = [record.phone, record.email].filter(Boolean).join(' · ')
        return (
          <div className="supplier-cell-contact">
            <span>{record.contactPerson || '-'}</span>
            {secondLine ? <span className="supplier-sub">{secondLine}</span> : null}
          </div>
        )
      },
    },
    {
      title: t('posAdmin.supplierPage.columns.image'),
      dataIndex: 'imageBaseUrl',
      width: 170,
      sorter: true,
      sortOrder: sortOrderOf('imageBaseUrl'),
      render: (_value: string | undefined, record) => {
        const state = describeSupplierImageTemplate(record)
        if (state.kind === 'configured') {
          return (
            <Tooltip title={record.imageBaseUrl}>
              <span className="supplier-image is-configured">
                {state.host ?? t('posAdmin.supplierPage.image.configured')}
              </span>
            </Tooltip>
          )
        }
        if (state.kind === 'cos') {
          return (
            <Tooltip title={record.imageBaseUrl}>
              <span className="supplier-image is-configured">{t('posAdmin.supplierPage.drawer.cosPreset')}</span>
            </Tooltip>
          )
        }
        if (state.kind === 'preset') {
          return (
            <span className="supplier-image is-preset">
              {t('posAdmin.supplierPage.image.preset', { name: state.name })}
            </span>
          )
        }
        return <span className="supplier-image is-missing">{t('posAdmin.supplierPage.image.missing')}</span>
      },
    },
    {
      title: (
        <Tooltip title={t('posAdmin.supplierPage.columns.lastPurchaseHint')}>
          <span>{t('posAdmin.supplierPage.columns.lastPurchase')}</span>
        </Tooltip>
      ),
      dataIndex: 'lastPurchaseDate',
      width: 190,
      sorter: true,
      sortOrder: sortOrderOf('lastPurchaseDate'),
      render: (value: string | null | undefined, record) => {
        const date = formatPurchaseDate(value)
        if (!date) return <span className="supplier-sub">-</span>
        return (
          <span className="supplier-nowrap">
            {date}
            <span className="supplier-sub"> · {record.lastPurchaseStoreName || record.lastPurchaseStoreCode || '-'}</span>
          </span>
        )
      },
    },
    {
      title: t('column.remarks'),
      dataIndex: 'remark',
      width: 200,
      ellipsis: true,
      render: (value: string | undefined) => value ? <span title={value}>{value}</span> : <span className="supplier-sub">-</span>,
    },
    {
      title: t('column.action', '操作'),
      key: 'action',
      width: canViewInvoices ? 128 : 72,
      fixed: 'right',
      render: (_value, record) => (
        // 操作区点击不能冒泡到整行点击（整行点击会打开详情抽屉）。
        <span className="supplier-actions" onClick={(event) => event.stopPropagation()}>
          <Button type="link" size="small" onClick={() => openEdit(record)}>
            {canEdit ? t('common.edit') : t('common.view')}
          </Button>
          {canViewInvoices ? (
            <Button type="link" size="small" onClick={() => navigate(buildSupplierInvoicesLink(record.localSupplierCode))}>
              {t('posAdmin.supplierPage.invoices')}
            </Button>
          ) : null}
        </span>
      ),
    },
  ]

  return (
    <Card
      title={
        <div className="supplier-page-title">
          <span>{t('posAdmin.suppliers.title')}</span>
          <Typography.Text type="secondary" className="supplier-page-subtitle">
            {t('posAdmin.supplierPage.subtitle')}
          </Typography.Text>
        </div>
      }
      extra={
        <Button type="primary" icon={<PlusOutlined />} disabled={!canEdit} onClick={openCreate}>
          {t('posAdmin.suppliers.createSupplier')}
        </Button>
      }
      styles={{ body: { padding: 0 } }}
    >
      <div
        ref={wrapRef}
        style={{
          height: 'calc(100vh - 160px)',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        <div ref={toolbarRef}>
          <div className="supplier-toolbar">
            <Input.Search
              allowClear
              placeholder={t('posAdmin.supplierPage.searchPlaceholder')}
              style={{ width: 240 }}
              onSearch={(value) => {
                setKeyword(value.trim())
                setPage(1)
              }}
            />
            <Segmented<StatusFilter>
              value={statusFilter}
              onChange={(value) => {
                setStatusFilter(value)
                setPage(1)
              }}
              options={[
                { label: t('common.all'), value: 'all' },
                { label: t('common.active'), value: '1' },
                { label: t('common.inactive'), value: '0' },
              ]}
            />
            <Select<LocalSupplierImageTemplateFilter | 'all'>
              value={imageTemplate ?? 'all'}
              style={{ width: 170 }}
              onChange={(value) => {
                setImageTemplate(value === 'all' ? undefined : value)
                setPage(1)
              }}
              options={[
                { label: t('posAdmin.supplierPage.imageFilter.all'), value: 'all' },
                { label: t('posAdmin.supplierPage.imageFilter.configured'), value: 'configured' },
                { label: t('posAdmin.supplierPage.imageFilter.missing'), value: 'missing' },
              ]}
            />
          </div>
          {/* 勾选后才出现同步操作条，避免一个常年灰着的按钮。 */}
          {hasSelection ? (
            <div className="supplier-selection-bar">
              <Space size={12} wrap>
                <strong>{t('posAdmin.supplierPage.selection.count', { count: selectedRowKeys.length })}</strong>
                <span className="supplier-selection-hint">{t('posAdmin.supplierPage.selection.hint')}</span>
              </Space>
              <Space size={8}>
                <Button
                  type="primary"
                  icon={<CloudUploadOutlined />}
                  loading={syncingToHq}
                  disabled={!canEdit}
                  onClick={handleSyncToHq}
                >
                  {t('posAdmin.supplierPage.selection.sync')}
                </Button>
                <Button type="link" onClick={() => setSelectedRowKeys([])}>
                  {t('posAdmin.supplierPage.selection.clear')}
                </Button>
              </Space>
            </div>
          ) : null}
        </div>
        <div ref={tableRegionRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <MeasuredTable metricId="pos-admin.supplier-management.table-1"
            rowKey="localSupplierCode"
            className="supplier-table"
            loading={loading}
            dataSource={data}
            pagination={false}
            scroll={{ x: 1100, ...(tableScrollY ? { y: tableScrollY } : {}) }}
            rowSelection={{
              selectedRowKeys,
              onChange: setSelectedRowKeys,
              preserveSelectedRowKeys: true,
            }}
            onRow={(record) => ({
              // 点整行打开详情；勾选框所在列的点击留给勾选，不打开抽屉。
              onClick: (event) => {
                if ((event.target as HTMLElement).closest('.ant-table-selection-column')) return
                openEdit(record)
              },
            })}
            columns={columns}
            onChange={(_pagination, _filters, sorter) => {
              const s = Array.isArray(sorter) ? sorter[0] : sorter
              const field = s?.field || s?.column?.dataIndex
              const order = s?.order as 'ascend' | 'descend' | undefined
              if (field && order) {
                setSortBy(String(field))
                setSortOrder(order)
              } else {
                setSortBy('name')
                setSortOrder('ascend')
              }
            }}
          />
        </div>
        <div
          ref={pagerRef}
          style={{
            padding: '8px 16px',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            width: '100%',
            background: '#fff',
            position: 'relative',
            zIndex: 3,
            flexShrink: 0,
          }}
        >
          <Typography.Text type="secondary">{t('common.totalCount', { count: total })}</Typography.Text>
          <Pagination
            current={page}
            pageSize={pageSize}
            total={total}
            onChange={(p, ps) => {
              setPage(p)
              setPageSize(ps)
            }}
            showSizeChanger
            responsive={false}
            pageSizeOptions={[10, 20, 50, 100, 200]}
          />
        </div>
      </div>

      <SupplierDrawer
        open={drawerOpen}
        supplier={drawerSupplier}
        form={supplierForm}
        canEdit={canEdit}
        canViewInvoices={canViewInvoices}
        saving={saving}
        onClose={closeDrawer}
        onSubmit={() => void (drawerSupplier ? handleEdit() : handleCreate())}
      />
    </Card>
  )
}
