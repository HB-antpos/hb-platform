import {
  CheckCircleOutlined,
  CloudUploadOutlined,
  CopyOutlined,
  DeleteOutlined,
  EllipsisOutlined,
  InfoCircleOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
  StopOutlined,
} from '@ant-design/icons'
import { Button, Card, Dropdown, Empty, Input, Modal, Popconfirm, Segmented, Tooltip, message } from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { SorterResult, TableCurrentDataSource } from 'antd/es/table/interface'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import {
  createChinaSupplier,
  deleteChinaSupplier,
  getChinaSuppliers,
  syncChinaSuppliersToHbSales,
  toggleChinaSupplierStatus,
  updateChinaSupplier,
} from '../../../services/chinaSupplierService'
import type { ChinaSupplierItem, ChinaSupplierListParams } from '../../../types/chinaSupplier'
import { copyTextToClipboard } from '../../../utils/clipboard'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'
import SupplierFormModal from './SupplierFormModal'
import SyncResultModal from './SyncResultModal'
import {
  DEFAULT_SUPPLIER_SORT,
  buildSavePayload,
  formatCreatedDate,
  normalizeKeyword,
  normalizeSyncResult,
  resolveOverflowPage,
  resolveSupplierSort,
  sortOrderForColumn,
  statusFilterToParam,
  statusParamToFilter,
  type SupplierFormValues,
  type SupplierSortState,
  type SupplierStatusFilter,
  type SyncResultView,
} from './chinaSuppliersLogic'
import './chinaSuppliers.css'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import chinaSuppliersMessagesEn from './chinaSuppliersMessages.en.json'
import chinaSuppliersMessagesZh from './chinaSuppliersMessages.zh.json'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: chinaSuppliersMessagesZh, en: chinaSuppliersMessagesEn })

type DesiredChinaSupplierQuery = ChinaSupplierListParams & {
  page: number
  pageSize: number
  sortField: string
  sortDirection: 'asc' | 'desc'
}

/** 空值统一显示的占位符（弱化颜色，避免和有内容的单元格抢视觉）。 */
const EMPTY_CELL = <span className="china-sup-faint">—</span>

export default function DomesticChinaSuppliersPage() {
  const { t } = useTranslation()
  const [modal, modalContextHolder] = Modal.useModal()
  // 挂载后必定立刻发起首次加载，所以初值为 true：否则首帧会先渲染一次「暂无供应商」空状态，
  // 在请求发出前误导用户以为没有数据。请求结束（含失败）时 onSettled 会置回 false。
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [syncing, setSyncing] = useState(false)
  const [formOpen, setFormOpen] = useState(false)
  const [editingItem, setEditingItem] = useState<ChinaSupplierItem | null>(null)
  const [syncResult, setSyncResult] = useState<SyncResultView | null>(null)
  const [data, setData] = useState<ChinaSupplierItem[]>([])
  // keyword 是输入框里正在敲的文本；真正生效的搜索词在 appliedSearch / desiredListQueryRef 里（回车才生效）。
  const [keyword, setKeyword] = useState('')
  const [appliedSearch, setAppliedSearch] = useState<string | undefined>(undefined)
  const [status, setStatus] = useState<SupplierStatusFilter>('all')
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [sort, setSort] = useState<SupplierSortState>({ ...DEFAULT_SUPPLIER_SORT })
  // 页头「共 N 家 · 启用 M」：与当前筛选无关的全局概览。
  const [overview, setOverview] = useState<{ total: number; enabled: number } | null>(null)
  const mainListRequestGuardRef = useRef(createLatestRequestGuard())
  const overviewRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const desiredListQueryRef = useRef<DesiredChinaSupplierQuery>({
    page,
    pageSize,
    search: undefined,
    status: undefined,
    sortField: sort.sortField,
    sortDirection: sort.sortDirection,
  })

  /**
   * 列表查询。查询条件一律用「对象合并」：以已开始请求的 desired query 为底，再叠加 overrides。
   * 不用默认参数——`loadData(x = state)` 会吞掉显式写下的 `undefined`，导致「清除筛选」失效；
   * 也不读渲染闭包里的 state，避免用户连续点击（筛选后马上排序）时拿到上一次成功请求的旧条件。
   */
  const loadData = async (overrides: Partial<DesiredChinaSupplierQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    const query: DesiredChinaSupplierQuery = {
      ...desiredListQueryRef.current,
      ...overrides,
    }
    // 请求 begin 前同步目标分页和排序，避免 mutation 用最后成功页覆盖在途查询。
    desiredListQueryRef.current = query

    await runLatestGuardedRequest(mainListRequestGuardRef.current, () => getChinaSuppliers(query), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
        setStatus(statusParamToFilter(query.status))
        setAppliedSearch(query.search)
        setSort({ sortField: query.sortField, sortDirection: query.sortDirection })

        // 删除 / 停用后当前页可能整页消失（例如删掉最后一页唯一的一条）：自动回退到最后一页，
        // 否则用户会看到一个空表却显示「共 N 条」。
        const overflowPage = resolveOverflowPage({
          page: result.page,
          pageSize: result.pageSize,
          total: result.total,
          itemCount: result.items.length,
        })
        if (overflowPage !== null) {
          void refreshDesiredList({ page: overflowPage })
        }
      },
      onError: (error) => {
        console.error(error)
        message.error(error instanceof Error ? error.message : t('chinaSuppliers.loadFailed'))
      },
      // 旧请求结束时不能关闭较新请求的 loading。
      onSettled: () => setLoading(false),
    })
  }

  const latestLoadDataRef = useRef(loadData)

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
  })

  const refreshDesiredList = (overrides: Partial<DesiredChinaSupplierQuery> = {}) =>
    latestLoadDataRef.current({ ...desiredListQueryRef.current, ...overrides })

  // 页头概览：用两个 pageSize=1 的轻量请求取 total（全部 / 启用），列表接口不单独返回启用数。
  // 同样加「最新请求」守卫：新建、停用、删除会连续触发刷新，旧响应不能覆盖新数字。
  const loadOverview = async () => {
    if (!mountedRef.current) {
      return
    }

    await runLatestGuardedRequest(
      overviewRequestGuardRef.current,
      () =>
        Promise.all([
          getChinaSuppliers({ page: 1, pageSize: 1 }),
          getChinaSuppliers({ page: 1, pageSize: 1, status: 1 }),
        ]),
      {
        onSuccess: ([all, enabled]) => setOverview({ total: all.total, enabled: enabled.total }),
        // 概览只是辅助信息，失败时保持页面可用，不打扰用户。
        onError: (error) => console.error(error),
      },
    )
  }

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      mainListRequestGuardRef.current.invalidate()
      overviewRequestGuardRef.current.invalidate()
    }
  }, [])

  useEffect(() => {
    void loadData({ page: 1, pageSize })
    void loadOverview()
  }, [])

  const clearSelection = () => setSelectedRowKeys([])

  // 搜索：回车才生效。改变数据集，回到第一页并清空可能已被筛掉的隐藏选择。
  const submitSearch = (text: string) => {
    clearSelection()
    void loadData({ page: 1, search: normalizeKeyword(text) })
  }

  // 状态分段即时生效，同样回到第一页并清空隐藏选择。
  // 分段控件先乐观切换，不等接口返回；列表数据仍以最新一次请求的结果为准。
  const applyStatus = (nextStatus: SupplierStatusFilter) => {
    clearSelection()
    setStatus(nextStatus)
    void loadData({ page: 1, status: statusFilterToParam(nextStatus) })
  }

  const handleRefresh = () => {
    void refreshDesiredList()
    void loadOverview()
  }

  const handleCreate = () => {
    setEditingItem(null)
    setFormOpen(true)
  }

  const handleEdit = (record: ChinaSupplierItem) => {
    setEditingItem(record)
    setFormOpen(true)
  }

  const handleCloseForm = () => {
    if (saving) {
      return
    }
    setFormOpen(false)
    setEditingItem(null)
  }

  const handleSave = async (values: SupplierFormValues) => {
    if (saving) {
      return
    }

    setSaving(true)
    try {
      // 载荷整理（去空白、空串邮箱不发送）见 buildSavePayload，否则清空邮箱再保存会被后端 400。
      const payload = buildSavePayload(values)
      if (editingItem) {
        await updateChinaSupplier(editingItem.guid, payload)
        message.success(t('chinaSuppliers.updateSuccess'))
      } else {
        await createChinaSupplier(payload)
        message.success(t('chinaSuppliers.createSuccess'))
      }

      const wasEditing = Boolean(editingItem)
      setFormOpen(false)
      setEditingItem(null)
      if (wasEditing) {
        void refreshDesiredList()
      } else {
        // 新建后回到第一页，并清空勾选（页码变了，旧勾选不再对应可见行）。
        clearSelection()
        void refreshDesiredList({ page: 1 })
      }
      void loadOverview()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('chinaSuppliers.saveFailed'))
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async (record: ChinaSupplierItem) => {
    try {
      await deleteChinaSupplier(record.guid)
      message.success(t('chinaSuppliers.deleteSuccess'))
      setSelectedRowKeys((current) => current.filter((item) => item !== record.guid))
      void refreshDesiredList()
      void loadOverview()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('chinaSuppliers.deleteFailed'))
    }
  }

  const handleToggleStatus = async (record: ChinaSupplierItem) => {
    const nextStatus = record.status === 1 ? 0 : 1
    try {
      await toggleChinaSupplierStatus(record.guid, nextStatus)
      message.success(nextStatus === 1 ? t('chinaSuppliers.enabled') : t('chinaSuppliers.disabled'))
      void refreshDesiredList()
      void loadOverview()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('chinaSuppliers.toggleStatusFailed'))
    }
  }

  // 停用 / 删除要二次确认；onOk 返回 Promise，确认按钮自带 loading，期间不会重复提交。
  const confirmToggleStatus = (record: ChinaSupplierItem) => {
    const disabling = record.status === 1
    modal.confirm({
      title: t('chinaSuppliers.confirmToggle', {
        action: disabling ? t('chinaSuppliers.actionDisable') : t('chinaSuppliers.actionEnable'),
      }),
      content: `${record.supplierCode} ${record.supplierName}`,
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      okButtonProps: { danger: disabling },
      onOk: () => handleToggleStatus(record),
    })
  }

  const confirmDelete = (record: ChinaSupplierItem) => {
    modal.confirm({
      title: t('chinaSuppliers.confirmDelete'),
      content: (
        <>
          <div>{`${record.supplierCode} ${record.supplierName}`}</div>
          <div>{t('chinaSuppliers.deleteWarning')}</div>
        </>
      ),
      okText: t('common.delete'),
      cancelText: t('common.cancel'),
      okButtonProps: { danger: true },
      onOk: () => handleDelete(record),
    })
  }

  const handleSync = async () => {
    if (syncing || selectedRowKeys.length === 0) {
      return
    }

    setSyncing(true)
    try {
      const result = normalizeSyncResult(await syncChinaSuppliersToHbSales(selectedRowKeys.map(String)))
      setSyncResult(result)
      // 只有全部成功才清空勾选：有失败 / 未找到时保留，便于用户处理后直接重试。
      if (result.outcome === 'success') {
        clearSelection()
      }
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('chinaSuppliers.syncFailed'))
    } finally {
      setSyncing(false)
    }
  }

  const handleTableChange = (
    pagination: TablePaginationConfig,
    _filters: Record<string, unknown>,
    sorter: SorterResult<ChinaSupplierItem> | SorterResult<ChinaSupplierItem>[],
    extra: TableCurrentDataSource<ChinaSupplierItem>,
  ) => {
    const nextPage = extra.action === 'paginate' ? pagination.current ?? 1 : 1
    const nextPageSize = pagination.pageSize ?? pageSize
    const currentSorter = Array.isArray(sorter) ? sorter[0] : sorter

    // 排序列只映射到后端认识的键；取消排序回到默认的创建时间倒序，不再向后端发它不认识的键。
    void loadData({
      page: nextPage,
      pageSize: nextPageSize,
      ...resolveSupplierSort(currentSorter?.columnKey, currentSorter?.order),
    })
  }

  const columns: ColumnsType<ChinaSupplierItem> = [
    {
      title: t('chinaSuppliers.supplierColumn'),
      key: 'supplierName',
      dataIndex: 'supplierName',
      // 不设固定宽度：吃掉其它列之外的剩余空间，名称过长时省略并在 title 里看全称。
      sorter: true,
      sortOrder: sortOrderForColumn('supplierName', sort),
      render: (_value: string, record) => (
        <div className="china-sup-two">
          <div className="china-sup-name-row">
            <span className="china-sup-name" title={record.supplierName}>{record.supplierName}</span>
            {record.remarks ? (
              <Tooltip title={record.remarks}>
                <InfoCircleOutlined className="china-sup-note" tabIndex={0} aria-label={t('common.remarks')} />
              </Tooltip>
            ) : null}
          </div>
          <div className="china-sup-code-row">
            <span className="china-sup-mono china-sup-sub">{record.supplierCode}</span>
            <Tooltip title={t('common.copy')}>
              <Button
                size="small"
                type="text"
                className="china-sup-copy"
                icon={<CopyOutlined />}
                aria-label={t('common.copyValue', { value: record.supplierCode })}
                onClick={() => void copyTextToClipboard(record.supplierCode, { successMessage: t('common.copySuccess'), failureMessage: t('common.copyFailed') })}
              />
            </Tooltip>
          </div>
        </div>
      ),
    },
    {
      title: t('chinaSuppliers.shopNumber'),
      key: 'shopNumber',
      dataIndex: 'shopNumber',
      width: 96,
      sorter: true,
      sortOrder: sortOrderForColumn('shopNumber', sort),
      render: (value?: string) => (value ? <span className="china-sup-ellipsis" title={value}>{value}</span> : EMPTY_CELL),
    },
    {
      // 联系人与电话同属「联系方式」，合并成两行；电话不再占独立列。
      title: t('chinaSuppliers.contactPerson'),
      key: 'contactPerson',
      dataIndex: 'contactPerson',
      width: 140,
      sorter: true,
      sortOrder: sortOrderForColumn('contactPerson', sort),
      render: (_value: string | undefined, record) =>
        !record.contactPerson && !record.phone ? (
          EMPTY_CELL
        ) : (
          <div className="china-sup-two">
            <span className="china-sup-contact-name china-sup-ellipsis" title={record.contactPerson}>{record.contactPerson ?? EMPTY_CELL}</span>
            <span className="china-sup-sub china-sup-mono china-sup-ellipsis" title={record.phone}>{record.phone ?? '—'}</span>
          </div>
        ),
    },
    {
      title: t('chinaSuppliers.email'),
      key: 'email',
      dataIndex: 'email',
      width: 168,
      render: (value?: string) => (value ? <span className="china-sup-email china-sup-ellipsis" title={value}>{value}</span> : EMPTY_CELL),
    },
    {
      title: t('common.status'),
      key: 'status',
      dataIndex: 'status',
      width: 84,
      sorter: true,
      sortOrder: sortOrderForColumn('status', sort),
      render: (value: number) => (
        <span className={value === 1 ? 'china-sup-status china-sup-status-on' : 'china-sup-status'}>
          {value === 1 ? t('common.enabled') : t('common.disabled')}
        </span>
      ),
    },
    {
      title: t('chinaSuppliers.createdAt'),
      key: 'createdAt',
      dataIndex: 'createdAt',
      width: 104,
      // 创建时间首次点击更自然的方向是「最新在前」。
      sortDirections: ['descend', 'ascend'],
      sorter: true,
      sortOrder: sortOrderForColumn('createdAt', sort),
      render: (value?: string) => {
        const date = formatCreatedDate(value)
        return date ? <span className="china-sup-date" title={date.full}>{date.text}</span> : EMPTY_CELL
      },
    },
    {
      title: t('common.action'),
      key: 'action',
      width: 104,
      align: 'right',
      render: (_value, record) => (
        <div className="china-sup-actions">
          <Button size="small" type="link" onClick={() => handleEdit(record)}>
            {t('common.edit')}
          </Button>
          {/* 低频且有破坏性的操作收进「更多」：启用 / 禁用、删除都要二次确认。 */}
          <Dropdown
            trigger={['click']}
            menu={{
              items: [
                {
                  key: 'toggle',
                  label: record.status === 1 ? t('chinaSuppliers.actionDisable') : t('chinaSuppliers.actionEnable'),
                  icon: record.status === 1 ? <StopOutlined /> : <CheckCircleOutlined />,
                  danger: record.status === 1,
                },
                { type: 'divider' },
                { key: 'delete', label: t('common.delete'), icon: <DeleteOutlined />, danger: true },
              ],
              onClick: ({ key }) => {
                if (key === 'toggle') {
                  confirmToggleStatus(record)
                } else if (key === 'delete') {
                  confirmDelete(record)
                }
              },
            }}
          >
            <Button size="small" type="link" icon={<EllipsisOutlined />} aria-label={t('common.more')} />
          </Dropdown>
        </div>
      ),
    },
  ]

  const hasActiveFilter = Boolean(appliedSearch) || status !== 'all'

  return (
    <PageContainer
      compact
      title={t('chinaSuppliers.pageTitle')}
      subtitle={overview ? t('chinaSuppliers.totalSummary', { total: overview.total, enabled: overview.enabled }) : undefined}
      extra={(
        <Button type="primary" icon={<PlusOutlined />} onClick={handleCreate}>
          {t('chinaSuppliers.newSupplier')}
        </Button>
      )}
    >
      <Card>
        <div className="china-sup-toolbar" data-testid="china-suppliers-toolbar">
          <Input
            className="china-sup-search"
            placeholder={t('chinaSuppliers.searchPlaceholderAll')}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
            onPressEnter={() => submitSearch(keyword)}
            onClear={() => submitSearch('')}
            prefix={<SearchOutlined />}
            suffix={<span className="china-sup-kbd">{t('chinaSuppliers.searchEnterKey')}</span>}
            allowClear
          />
          <Segmented<SupplierStatusFilter>
            value={status}
            options={[
              { label: t('common.all'), value: 'all' },
              { label: t('common.enabled'), value: 'enabled' },
              { label: t('common.disabled'), value: 'disabled' },
            ]}
            onChange={applyStatus}
          />
          <Tooltip title={t('common.refresh')}>
            <Button icon={<ReloadOutlined />} aria-label={t('common.refresh')} loading={loading} onClick={handleRefresh} />
          </Tooltip>
        </div>

        {/* 勾选后才出现的批量操作条：原先常驻的灰色「同步到销售库」不再占位。 */}
        {selectedRowKeys.length > 0 ? (
          <div className="china-sup-bars" data-testid="china-suppliers-selection-bar">
            <SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={clearSelection}>
              <Popconfirm
                title={t('chinaSuppliers.syncConfirm', { count: selectedRowKeys.length })}
                okText={t('chinaSuppliers.confirmSync')}
                cancelText={t('common.cancel')}
                disabled={syncing}
                onConfirm={handleSync}
              >
                <Button size="small" icon={<CloudUploadOutlined />} loading={syncing}>
                  {t('chinaSuppliers.syncToSales')}
                </Button>
              </Popconfirm>
            </SelectionActionBar>
          </div>
        ) : null}

        <MeasuredTable metricId="domestic-purchase.china-suppliers.table-1"
          rowKey="guid"
          className="china-sup-table"
          data-testid="china-suppliers-table"
          loading={loading}
          columns={columns}
          dataSource={data}
          tableLayout="fixed"
          // 侧栏 248 + 页面内边距 32 + 卡片内边距 48 后，1280 宽屏幕的表格区约 952px：
          // 勾选列 40 + 6 列固定宽度 696 = 736，供应商列吃剩余约 216px（最小 920 - 736 = 184px），
          // 所以 ≥1280 不出现横向滚动；1440 宽时供应商列约 376px。
          scroll={{ x: 920 }}
          showSorterTooltip={false}
          locale={{
            emptyText: (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={hasActiveFilter ? t('chinaSuppliers.emptyFiltered') : t('chinaSuppliers.emptyAll')}
              />
            ),
          }}
          rowSelection={{
            selectedRowKeys,
            onChange: setSelectedRowKeys,
            preserveSelectedRowKeys: true,
            columnWidth: 40,
          }}
          onChange={handleTableChange}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            showTotal: (count) => t('common.totalCount', { count }),
          }}
        />
      </Card>

      <SupplierFormModal
        open={formOpen}
        editing={editingItem}
        saving={saving}
        onCancel={handleCloseForm}
        onSubmit={handleSave}
      />

      <SyncResultModal result={syncResult} onClose={() => setSyncResult(null)} />

      {modalContextHolder}
    </PageContainer>
  )
}
