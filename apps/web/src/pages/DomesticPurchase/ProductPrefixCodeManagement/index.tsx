import { EllipsisOutlined, PlusOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Button, Card, Dropdown, Input, Modal, Segmented, Select, Switch, Tooltip, message } from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { SorterResult, TableCurrentDataSource } from 'antd/es/table/interface'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import {
  createPrefixCode,
  deletePrefixCode,
  getPrefixCodeList,
  updatePrefixCode,
} from '../../../services/productPrefixCodeService'
import type { ProductPrefixCodeItem, SavePrefixCodePayload } from '../../../types/productPrefixCode'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import PrefixCodeFormModal from './PrefixCodeFormModal'
import type { PrefixSupplierOption } from './PrefixCodeFormModal'
import PrefixProductsPanel from './PrefixProductsPanel'
import { buildStatusTogglePayload } from './prefixCodeRules'
import {
  formatPrefixTimestamp,
  hasActivePrefixFilters,
  mergePrefixListQuery,
  pageAfterRemoval,
  resolvePrefixSort,
  statusFilterToIsActive,
  toTableSortOrder,
} from './prefixListLogic'
import type { PrefixListQuery, PrefixStatusFilter } from './prefixListLogic'
import './prefixCode.css'

type DesiredPrefixListQuery = PrefixListQuery

/** 弹窗目标：新增，或编辑某一行。 */
type PrefixFormTarget = { mode: 'create' } | { mode: 'edit'; record: ProductPrefixCodeItem }

export default function ProductPrefixCodeManagementPage() {
  const { t } = useTranslation()
  // 挂载后必定立刻发起首次加载，所以初值为 true：否则首帧会先渲染一次「还没有前缀，点击右上角新增」，
  // 在请求发出前误导用户以为没有数据。请求结束（含失败）时 onSettled 会置回 false。
  const [loading, setLoading] = useState(true)
  const [supplierLoading, setSupplierLoading] = useState(false)
  const [suppliers, setSuppliers] = useState<PrefixSupplierOption[]>([])
  const [data, setData] = useState<ProductPrefixCodeItem[]>([])
  const [expandedKeys, setExpandedKeys] = useState<string[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  // keyword 是输入框里的文本，search 是已提交（回车 / 清空）真正参与查询的关键词。
  const [keyword, setKeyword] = useState('')
  const [search, setSearch] = useState('')
  const [supplierCode, setSupplierCode] = useState<string | undefined>(undefined)
  const [statusFilter, setStatusFilter] = useState<PrefixStatusFilter>('all')
  const [sortField, setSortField] = useState<string | undefined>(undefined)
  const [sortDirection, setSortDirection] = useState<'asc' | 'desc' | undefined>(undefined)
  // 页头「共 N 个 · 启用 M」：与当前筛选无关的全局概览。
  const [overview, setOverview] = useState<{ total: number; enabled: number } | null>(null)
  const [formTarget, setFormTarget] = useState<PrefixFormTarget | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<ProductPrefixCodeItem | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [togglingKeys, setTogglingKeys] = useState<Set<string>>(() => new Set())
  const lastFormTargetRef = useRef<PrefixFormTarget>({ mode: 'create' })
  const lastDeleteTargetRef = useRef<ProductPrefixCodeItem | null>(null)
  const mainListRequestGuardRef = useRef(createLatestRequestGuard())
  const overviewRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const desiredListQueryRef = useRef<DesiredPrefixListQuery>({
    page,
    pageSize,
    search: search || undefined,
    supplierCode,
    isActive: statusFilterToIsActive(statusFilter),
    sortField,
    sortDirection,
  })

  const loadSuppliers = async () => {
    setSupplierLoading(true)
    try {
      const result = await getActiveChinaSuppliers()
      setSuppliers(
        result.map((item) => ({
          label: item.supplierName ? `${item.supplierName} · ${item.supplierCode}` : item.supplierCode,
          value: item.supplierCode,
        })),
      )
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error && error.message ? error.message : t('prefixCode.loadSuppliersFailed'))
    } finally {
      setSupplierLoading(false)
    }
  }

  const loadList = async (overrides: Partial<DesiredPrefixListQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    // 对象合并而非默认参数：overrides 里显式 undefined 表示「清除该筛选」。
    const query = mergePrefixListQuery(
      {
        page,
        pageSize,
        search: search || undefined,
        supplierCode,
        isActive: statusFilterToIsActive(statusFilter),
        sortField,
        sortDirection,
      },
      overrides,
    )
    // 主列表请求开始即发布目标查询，mutation 晚完成时继续刷新在途分页。
    desiredListQueryRef.current = query

    await runLatestGuardedRequest(mainListRequestGuardRef.current, () => getPrefixCodeList(query), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
        setSortField(query.sortField)
        setSortDirection(query.sortDirection)
      },
      onError: (error) => {
        console.error(error)
        message.error(error instanceof Error && error.message ? error.message : t('prefixCode.loadListFailed'))
      },
      // 旧请求结束时不能关闭较新请求的 loading。
      onSettled: () => setLoading(false),
    })
  }

  const latestLoadListRef = useRef(loadList)

  useLayoutEffect(() => {
    latestLoadListRef.current = loadList
  })

  const refreshDesiredList = (overrides: Partial<DesiredPrefixListQuery> = {}) =>
    latestLoadListRef.current({ ...desiredListQueryRef.current, ...overrides })

  // 概览只是辅助信息：用两个 pageSize=1 的轻量请求取 total，失败时保持页面可用，不打扰用户。
  const loadOverview = async () => {
    await runLatestGuardedRequest(
      overviewRequestGuardRef.current,
      async () => {
        const [all, enabled] = await Promise.all([
          getPrefixCodeList({ page: 1, pageSize: 1 }),
          getPrefixCodeList({ page: 1, pageSize: 1, isActive: true }),
        ])
        return { total: all.total, enabled: enabled.total }
      },
      {
        onSuccess: setOverview,
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
    void loadSuppliers()
    void loadList({ page: 1, pageSize })
    void loadOverview()
  }, [])

  // 搜索框：回车才查询；文本被清空（点清除图标或删光）等同于清除筛选，立即恢复完整列表。
  const commitSearch = (value: string) => {
    const nextSearch = value.trim()
    setKeyword(nextSearch)
    setSearch(nextSearch)
    void loadList({ page: 1, search: nextSearch || undefined })
  }

  const handleKeywordChange = (value: string) => {
    setKeyword(value)
    if (!value && search) {
      commitSearch('')
    }
  }

  // 供应商下拉与状态分段：即时生效，改变数据集所以回到第一页。
  const applySupplierFilter = (value?: string) => {
    setSupplierCode(value || undefined)
    void loadList({ page: 1, supplierCode: value || undefined })
  }

  const applyStatusFilter = (value: PrefixStatusFilter) => {
    setStatusFilter(value)
    void loadList({ page: 1, isActive: statusFilterToIsActive(value) })
  }

  const handleTableChange = (
    pagination: TablePaginationConfig,
    _filters: Record<string, unknown>,
    sorter: SorterResult<ProductPrefixCodeItem> | SorterResult<ProductPrefixCodeItem>[],
    extra: TableCurrentDataSource<ProductPrefixCodeItem>,
  ) => {
    const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const nextPage = extra.action === 'paginate' ? pagination.current ?? 1 : 1

    void loadList({
      page: nextPage,
      pageSize: pagination.pageSize ?? pageSize,
      ...resolvePrefixSort(nextSorter?.field, nextSorter?.order),
    })
  }

  const handleSubmitForm = async (payload: SavePrefixCodePayload) => {
    if (!formTarget) {
      return
    }

    // 请求失败直接向外抛，由弹窗就地提示并保持打开；成功后才关闭并刷新。
    if (formTarget.mode === 'create') {
      await createPrefixCode(payload)
      message.success(t('prefixCode.createSuccess'))
      setFormTarget(null)
      void refreshDesiredList({ page: 1 })
    } else {
      await updatePrefixCode(formTarget.record.prefixCode, payload)
      message.success(t('prefixCode.updateSuccess'))
      setFormTarget(null)
      void refreshDesiredList()
    }
    void loadOverview()
  }

  const handleConfirmDelete = async () => {
    if (!deleteTarget || deleting) {
      return
    }

    setDeleting(true)
    try {
      await deletePrefixCode(deleteTarget.prefixCode)
      message.success(t('prefixCode.deleteSuccess'))
      const removedKey = deleteTarget.prefixCode
      setDeleteTarget(null)
      setExpandedKeys((current) => current.filter((key) => key !== removedKey))
      // 删掉当前页最后一行时回退一页，避免停在空白页。
      void refreshDesiredList({ page: pageAfterRemoval(page, data.length) })
      void loadOverview()
    } catch (error) {
      // 后端会拒绝删除已被商品使用的前缀（PREFIX_IN_USE），其 message 已说明原因，直接展示。
      console.error(error)
      message.error(error instanceof Error && error.message ? error.message : t('prefixCode.deleteFailed'))
    } finally {
      setDeleting(false)
    }
  }

  // 列表内直接切换启用状态：先乐观更新让开关立刻跟手，请求失败再回滚到原值。
  const handleToggleActive = async (record: ProductPrefixCodeItem, checked: boolean) => {
    if (togglingKeys.has(record.prefixCode)) {
      return
    }

    const patchRow = (isActive: boolean) =>
      setData((current) => current.map((row) => (row.prefixCode === record.prefixCode ? { ...row, isActive } : row)))
    setTogglingKeys((current) => new Set(current).add(record.prefixCode))
    patchRow(checked)
    try {
      await updatePrefixCode(record.prefixCode, buildStatusTogglePayload(record, checked))
      message.success(t('prefixCode.statusUpdated'))
      // 筛选为「启用 / 停用」时，状态变更后这一行应当离开当前列表，所以要重新取数。
      void refreshDesiredList()
      void loadOverview()
    } catch (error) {
      console.error(error)
      patchRow(record.isActive)
      message.error(error instanceof Error && error.message ? error.message : t('prefixCode.statusUpdateFailed'))
    } finally {
      setTogglingKeys((current) => {
        const next = new Set(current)
        next.delete(record.prefixCode)
        return next
      })
    }
  }

  const sortState = { sortField, sortDirection }

  const columns: ColumnsType<ProductPrefixCodeItem> = [
    {
      title: t('prefixCode.prefixName'),
      dataIndex: 'prefixName',
      width: 100,
      sorter: true,
      sortOrder: toTableSortOrder(sortState, 'prefixName'),
      render: (value: string) => <span className="prefix-code-tag">{value}</span>,
    },
    {
      title: t('domesticProducts.supplier'),
      dataIndex: 'supplierName',
      width: 216,
      sorter: true,
      sortOrder: toTableSortOrder(sortState, 'supplierName'),
      // 供应商名称 + 编码两行：编码是排查「这条前缀属于哪家」最常用的线索。
      render: (_value: string | undefined, record) => (
        <div className="prefix-code-two">
          <span className="prefix-code-two-title" title={record.supplierName || record.supplierCode}>
            {record.supplierName || record.supplierCode}
          </span>
          {record.supplierName ? <span className="prefix-code-two-sub prefix-code-sub prefix-code-mono">{record.supplierCode}</span> : null}
        </div>
      ),
    },
    {
      title: t('prefixCode.prefixDescription'),
      dataIndex: 'prefixDescription',
      render: (value?: string) =>
        value ? <span className="prefix-code-ellipsis" title={value}>{value}</span> : <span className="prefix-code-faint">--</span>,
    },
    {
      title: t('domesticProducts.status'),
      dataIndex: 'isActive',
      width: 80,
      render: (value: boolean, record) => (
        <Switch
          checked={value}
          loading={togglingKeys.has(record.prefixCode)}
          onChange={(checked) => void handleToggleActive(record, checked)}
          aria-label={`${record.prefixName} ${t('domesticProducts.status')}`}
          data-testid="prefix-code-status-switch"
        />
      ),
    },
    {
      title: t('prefixCode.formSort'),
      dataIndex: 'sortOrder',
      width: 76,
      sorter: true,
      sortOrder: toTableSortOrder(sortState, 'sortOrder'),
      render: (value?: number) => (value === undefined ? <span className="prefix-code-faint">--</span> : <span className="prefix-code-mono">{value}</span>),
    },
    {
      // 更新时间后端不支持排序，故不给箭头。
      title: t('column.updateTime'),
      dataIndex: 'updatedAt',
      width: 116,
      render: (value?: string) => (
        <span className="prefix-code-sub" title={value?.replace('T', ' ')}>{formatPrefixTimestamp(value)}</span>
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 100,
      // 不能给操作列加 fixed：带固定列时 antd 会把展开行内容包进按表宽计算的 sticky 容器，
      // 与展开行左侧 48px 的缩进叠加后内容会超出单元格。本表最小宽度 920px，1280 屏上不会出现横向滚动。
      align: 'right',
      render: (_value, record) => (
        <div className="prefix-code-actions">
          <Button size="small" type="link" onClick={() => setFormTarget({ mode: 'edit', record })}>
            {t('common.edit')}
          </Button>
          {/* 删除是低频且不可恢复的操作，收进「更多」，并走二次确认弹窗。 */}
          <Dropdown
            trigger={['click']}
            menu={{
              items: [{ key: 'delete', label: t('common.delete'), danger: true }],
              onClick: ({ key }) => {
                if (key === 'delete') {
                  setDeleteTarget(record)
                }
              },
            }}
          >
            <Button size="small" type="link" icon={<EllipsisOutlined />} aria-label={t('prefixCode.moreActions')} />
          </Dropdown>
        </div>
      ),
    },
  ]

  const filtersActive = hasActivePrefixFilters({
    search,
    supplierCode,
    isActive: statusFilterToIsActive(statusFilter),
  })
  // 关闭动画期间 formTarget / deleteTarget 已是 null，内容若立刻切回默认值会闪一下，所以沿用最近一次的目标。
  if (formTarget) {
    lastFormTargetRef.current = formTarget
  }
  if (deleteTarget) {
    lastDeleteTargetRef.current = deleteTarget
  }
  const shownFormTarget = formTarget ?? lastFormTargetRef.current
  const editingRecord = shownFormTarget.mode === 'edit' ? shownFormTarget.record : undefined

  return (
    <PageContainer
      compact
      title={t('prefixCode.prefixManagement')}
      subtitle={overview ? t('prefixCode.totalSummary', { total: overview.total, enabled: overview.enabled }) : undefined}
      extra={(
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setFormTarget({ mode: 'create' })} data-testid="prefix-code-add">
          {t('prefixCode.addPrefix')}
        </Button>
      )}
    >
      <Card>
        <div className="prefix-code-toolbar" data-testid="prefix-code-toolbar">
          <Input
            placeholder={`${t('prefixCode.searchPlaceholder')} · ${t('common.listToolbar.searchEnterHint')}`}
            value={keyword}
            onChange={(event) => handleKeywordChange(event.target.value)}
            onPressEnter={() => commitSearch(keyword)}
            prefix={<SearchOutlined />}
            style={{ width: 360 }}
            allowClear
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            prefix={t('domesticProducts.supplier')}
            placeholder={t('common.all')}
            style={{ width: 260 }}
            value={supplierCode}
            options={suppliers}
            loading={supplierLoading}
            onChange={(value?: string) => applySupplierFilter(value)}
          />
          <Segmented<PrefixStatusFilter>
            value={statusFilter}
            options={[
              { label: t('common.all'), value: 'all' },
              { label: t('common.active'), value: 'active' },
              { label: t('common.inactive'), value: 'inactive' },
            ]}
            onChange={applyStatusFilter}
          />
          <Tooltip title={t('common.refresh')}>
            <Button
              icon={<ReloadOutlined />}
              aria-label={t('common.refresh')}
              onClick={() => {
                // 刷新沿用已开始请求的筛选 / 排序 / 分页，并顺带更新页头概览。
                void refreshDesiredList()
                void loadOverview()
              }}
            />
          </Tooltip>
        </div>

        <div data-testid="prefix-code-table">
          <MeasuredTable
            metricId="domestic-purchase.product-prefix-code-management.table-2"
            className="prefix-code-table"
            rowKey="prefixCode"
            loading={loading}
            columns={columns}
            dataSource={data}
            tableLayout="fixed"
            scroll={{ x: 920 }}
            locale={{
              emptyText: filtersActive ? t('prefixCode.emptyFiltered') : t('prefixCode.empty'),
            }}
            rowClassName={(record) =>
              expandedKeys.includes(record.prefixCode) ? 'prefix-code-row prefix-code-row-expanded' : 'prefix-code-row'
            }
            expandable={{
              columnWidth: 40,
              // 面板以前缀为 key：每个前缀独立分页与最新请求守卫，收起即卸载并作废在途请求。
              expandedRowRender: (record) => (
                <PrefixProductsPanel key={record.prefixCode} prefixCode={record.prefixCode} prefixName={record.prefixName} />
              ),
              expandedRowKeys: expandedKeys,
              onExpand: (expanded, record) =>
                setExpandedKeys((current) =>
                  expanded
                    ? current.includes(record.prefixCode) ? current : [...current, record.prefixCode]
                    : current.filter((key) => key !== record.prefixCode),
                ),
            }}
            pagination={{
              current: page,
              pageSize,
              total,
              showSizeChanger: true,
              showTotal: (count) => t('common.totalCount', { count }),
            }}
            onChange={handleTableChange}
          />
        </div>
      </Card>

      <PrefixCodeFormModal
        open={formTarget !== null}
        mode={shownFormTarget.mode}
        // 新增默认启用、排序 0（与旧页面一致）；编辑带入当前行，排序为空则保持为空。
        initialValues={
          editingRecord
            ? {
                prefixName: editingRecord.prefixName,
                prefixDescription: editingRecord.prefixDescription,
                sortOrder: editingRecord.sortOrder,
                isActive: editingRecord.isActive,
              }
            : { isActive: true, sortOrder: 0 }
        }
        supplierOptions={suppliers}
        supplierLoading={supplierLoading}
        // 后端不允许改已有前缀的供应商，编辑时只读展示。
        fixedSupplier={editingRecord ? { code: editingRecord.supplierCode, name: editingRecord.supplierName } : undefined}
        onSubmit={handleSubmitForm}
        onCancel={() => setFormTarget(null)}
      />

      <Modal
        title={t('prefixCode.deleteTitle', { name: lastDeleteTargetRef.current?.prefixName ?? '' })}
        open={deleteTarget !== null}
        okText={t('common.delete')}
        okButtonProps={{ danger: true }}
        cancelText={t('common.cancel')}
        cancelButtonProps={{ disabled: deleting }}
        confirmLoading={deleting}
        closable={!deleting}
        keyboard={!deleting}
        maskClosable={false}
        onOk={() => void handleConfirmDelete()}
        onCancel={() => setDeleteTarget(null)}
        width={440}
        destroyOnHidden
      >
        <p style={{ margin: 0 }}>{t('prefixCode.deleteDescription')}</p>
      </Modal>
    </PageContainer>
  )
}
