import { CloudUploadOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons'
import {
  Button,
  Card,
  Form,
  Input,
  Modal,
  Pagination,
  Select,
  Space,
  Switch,
  Tag,
  message,
} from 'antd'
import { useEffect, useLayoutEffect, useRef, useState, type Key } from 'react'
import { useTranslation } from 'react-i18next'
import {
  createLocalSupplier,
  getLocalSuppliers,
  syncLocalSuppliersToHq,
  updateLocalSupplier,
} from '../../../services/localSupplierService'
import { useAuthStore } from '../../../store/auth'
import type { LocalSupplierDto } from '../../../types/localSupplier'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'

const SORT_FIELD_MAP: Record<string, string> = {
  localSupplierCode: 'localsuppliercode',
  name: 'name',
  status: 'status',
  contactPerson: 'contactperson',
  phone: 'phone',
  email: 'email',
  remark: 'remark',
  imageBaseUrl: 'imagebaseurl',
}

export default function SupplierManagementPage() {
  const { t } = useTranslation()
  // 同步、新增、编辑与后端 AustralianSuppliers.Edit 策略对齐，仅有 View 的用户只能浏览列表。
  const access = useAuthStore((state) => state.access)
  const canEdit = access.canEditAustralianSuppliers
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<LocalSupplierDto[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string | undefined>(undefined)
  const [sortBy, setSortBy] = useState('name')
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend'>('ascend')
  const [selectedRowKeys, setSelectedRowKeys] = useState<Key[]>([])
  const [syncingToHq, setSyncingToHq] = useState(false)
  const [createVisible, setCreateVisible] = useState(false)
  const [createForm] = Form.useForm()
  const [editVisible, setEditVisible] = useState(false)
  const [editingSupplier, setEditingSupplier] = useState<LocalSupplierDto | null>(null)
  const [editForm] = Form.useForm()
  const wrapRef = useRef<HTMLDivElement>(null)
  const toolbarRef = useRef<HTMLDivElement>(null)
  const tableRegionRef = useRef<HTMLDivElement>(null)
  const pagerRef = useRef<HTMLDivElement>(null)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const [tableScrollY, setTableScrollY] = useState<number | undefined>(undefined)

  const loadData = async () => {
    if (!mountedRef.current) {
      return
    }

    await runLatestGuardedRequest(listRequestGuardRef.current, () => getLocalSuppliers({
        pageIndex: page,
        pageSize,
        keyword,
        status,
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
  }, [page, pageSize, keyword, status, sortBy, sortOrder])

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
  }, [data.length, pageSize, total])

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

  const handleCreate = async () => {
    const values = await createForm.validateFields()
    try {
      await createLocalSupplier({
        name: values.name,
        status: values.status ? 1 : 0,
        contactPerson: values.contactPerson,
        phone: values.phone,
        email: values.email,
        remark: values.remark,
        imageBaseUrl: values.imageBaseUrl?.trim() || undefined,
      })
      message.success(t('message.createSuccess'))
      setCreateVisible(false)
      createForm.resetFields()
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch {
      message.error(t('message.createFailed'))
    }
  }

  const openEdit = (record: LocalSupplierDto) => {
    setEditingSupplier(record)
    editForm.setFieldsValue({
      name: record.name,
      status: record.status === 1,
      contactPerson: record.contactPerson,
      phone: record.phone,
      email: record.email,
      remark: record.remark,
      imageBaseUrl: record.imageBaseUrl,
    })
    setEditVisible(true)
  }

  const handleEdit = async () => {
    if (!editingSupplier) return
    const values = await editForm.validateFields()
    try {
      await updateLocalSupplier(editingSupplier.localSupplierCode, {
        name: values.name,
        status: values.status ? 1 : 0,
        contactPerson: values.contactPerson,
        phone: values.phone,
        email: values.email,
        remark: values.remark,
        imageBaseUrl: values.imageBaseUrl?.trim() || undefined,
      })
      message.success(t('message.saveSuccess', '保存成功'))
      setEditVisible(false)
      setEditingSupplier(null)
      editForm.resetFields()
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch {
      message.error(t('message.saveFailed', '保存失败'))
    }
  }

  return (
    <Card
      title={t('posAdmin.suppliers.title')}
      extra={
        <Space wrap>
          <Input.Search
            allowClear
            placeholder={t('posAdmin.suppliers.codeNamePlaceholder', '代码/名称')}
            style={{ width: 200 }}
            onSearch={(v) => {
              setKeyword(v)
              setPage(1)
            }}
          />
          <Select
            allowClear
            placeholder={t('common.status')}
            style={{ width: 120 }}
            value={status}
            onChange={(v) => {
              setStatus(v)
              setPage(1)
            }}
            options={[
              { label: t('common.active'), value: '1' },
              { label: t('common.inactive'), value: '0' },
            ]}
          />
          <Button
            icon={<CloudUploadOutlined />}
            loading={syncingToHq}
            disabled={!canEdit || !selectedRowKeys.length}
            onClick={handleSyncToHq}
          >
            {t('posAdmin.suppliers.syncToHq', '同步所选到 HQ')}
          </Button>
          <Button type="primary" icon={<PlusOutlined />} disabled={!canEdit} onClick={() => setCreateVisible(true)}>
            {t('posAdmin.suppliers.createSupplier')}
          </Button>
        </Space>
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
        <div ref={toolbarRef} style={{ padding: 16 }} />
        <div ref={tableRegionRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <MeasuredTable metricId="pos-admin.supplier-management.table-1"
            rowKey="localSupplierCode"
            loading={loading}
            dataSource={data}
            pagination={false}
            scroll={tableScrollY ? { y: tableScrollY } : undefined}
            rowClassName={(_, index) => (index % 2 === 1 ? 'table-row-striped' : '')}
            rowSelection={{
              selectedRowKeys,
              onChange: setSelectedRowKeys,
              preserveSelectedRowKeys: true,
            }}
            columns={[
              {
                title: t('column.index'),
                width: 72,
                align: 'right',
                render: (_, __, index) => (page - 1) * pageSize + index + 1,
              },
              {
                title: t('posAdmin.suppliers.code', '代码'),
                dataIndex: 'localSupplierCode',
                sorter: true,
                sortOrder: sortBy === 'localSupplierCode' ? sortOrder : undefined,
              },
              {
                title: t('posAdmin.suppliers.name'),
                dataIndex: 'name',
                sorter: true,
                sortOrder: sortBy === 'name' ? sortOrder : undefined,
              },
              {
                title: t('column.status'),
                dataIndex: 'status',
                sorter: true,
                sortOrder: sortBy === 'status' ? sortOrder : undefined,
                render: (v: number) =>
                  v === 1 ? <Tag color="green">{t('common.active')}</Tag> : <Tag color="red">{t('common.inactive')}</Tag>,
              },
              {
                title: t('posAdmin.suppliers.contactPerson'),
                dataIndex: 'contactPerson',
                sorter: true,
                sortOrder: sortBy === 'contactPerson' ? sortOrder : undefined,
              },
              {
                title: t('posAdmin.suppliers.phone'),
                dataIndex: 'phone',
                sorter: true,
                sortOrder: sortBy === 'phone' ? sortOrder : undefined,
              },
              {
                title: 'Email',
                dataIndex: 'email',
                sorter: true,
                sortOrder: sortBy === 'email' ? sortOrder : undefined,
              },
              {
                title: t('column.remarks'),
                dataIndex: 'remark',
                sorter: true,
                sortOrder: sortBy === 'remark' ? sortOrder : undefined,
              },
              {
                title: t('posAdmin.suppliers.imageBaseUrl', '图片基础 URL'),
                dataIndex: 'imageBaseUrl',
                width: 260,
                ellipsis: true,
                sorter: true,
                sortOrder: sortBy === 'imageBaseUrl' ? sortOrder : undefined,
                render: (value: string | undefined) => value || '-',
              },
              {
                title: t('column.action', '操作'),
                width: 90,
                fixed: 'right',
                render: (_, record) => (
                  <Button type="link" size="small" icon={<EditOutlined />} disabled={!canEdit} onClick={() => openEdit(record)}>
                    {t('common.edit', '编辑')}
                  </Button>
                ),
              },
            ]}
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
          <div />
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

      <Modal
        open={createVisible}
        title={t('posAdmin.suppliers.createTitle')}
        onCancel={() => {
          setCreateVisible(false)
          createForm.resetFields()
        }}
        onOk={handleCreate}
      >
        <Form form={createForm} layout="vertical">
          <Form.Item name="name" label={t('posAdmin.suppliers.name')} rules={[{ required: true, message: t('posAdmin.suppliers.nameRequired', '请输入名称') }, { max: 128 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="status" label={t('common.active')} valuePropName="checked" initialValue={true}>
            <Switch />
          </Form.Item>
          <Form.Item name="contactPerson" label={t('posAdmin.suppliers.contactPerson')} rules={[{ max: 64 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="phone" label={t('posAdmin.suppliers.phone')} rules={[{ max: 32 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="email" label="Email" rules={[{ type: 'email' }, { max: 128 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="remark" label={t('column.remarks')} rules={[{ max: 256 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="imageBaseUrl" label={t('posAdmin.suppliers.imageBaseUrl', '图片基础 URL')} rules={[{ max: 512 }]}>
            <Input placeholder="https://www.dats.com.au/images/ProductImages/500/{itemNumber}.jpg" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        open={editVisible}
        title={t('posAdmin.suppliers.editTitle', '编辑供应商')}
        onCancel={() => {
          setEditVisible(false)
          setEditingSupplier(null)
          editForm.resetFields()
        }}
        onOk={handleEdit}
      >
        <Form form={editForm} layout="vertical">
          <Form.Item name="name" label={t('posAdmin.suppliers.name')} rules={[{ required: true, message: t('posAdmin.suppliers.nameRequired', '请输入名称') }, { max: 128 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="status" label={t('common.active')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="contactPerson" label={t('posAdmin.suppliers.contactPerson')} rules={[{ max: 64 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="phone" label={t('posAdmin.suppliers.phone')} rules={[{ max: 32 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="email" label="Email" rules={[{ type: 'email' }, { max: 128 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="remark" label={t('column.remarks')} rules={[{ max: 256 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="imageBaseUrl" label={t('posAdmin.suppliers.imageBaseUrl', '图片基础 URL')} rules={[{ max: 512 }]}>
            <Input placeholder="https://www.dats.com.au/images/ProductImages/500/{itemNumber}.jpg" />
          </Form.Item>
        </Form>
      </Modal>
    </Card>
  )
}
