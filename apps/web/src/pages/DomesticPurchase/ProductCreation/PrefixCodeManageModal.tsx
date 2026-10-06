import { PlusOutlined, SearchOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { Button, Input, message, Modal, Popconfirm, Switch } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useRef, useState } from 'react'
// 创建页里的「管理前缀」沿用 /api/v1/productprefixcodes 这组接口：它允许 Admin 与 WarehouseManager 写入，
// 并带有独立的状态切换接口；而前缀管理页用的 React 接口写入只允许 Admin，所以这里不能直接换成同一套服务。
// 共用的是表单、规则与类型（见 ProductPrefixCodeManagement/）。
import {
  createPrefixCode,
  deletePrefixCode,
  getPrefixCodeList,
  togglePrefixCodeStatus,
  updatePrefixCode,
} from '../../../services/domesticProductCreationService'
import { MeasuredTable } from '../../../components/MeasuredTable'
import type { ProductPrefixCodeItem, SavePrefixCodePayload } from '../../../types/productPrefixCode'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import PrefixCodeFormModal from '../ProductPrefixCodeManagement/PrefixCodeFormModal'
import { formatPrefixTimestamp } from '../ProductPrefixCodeManagement/prefixListLogic'
import '../ProductPrefixCodeManagement/prefixCode.css'

interface PrefixCodeManageModalProps {
  visible: boolean
  supplierCode: string
  supplierName: string
  onClose: () => void
  onSuccess?: () => void
}

type PrefixFormTarget = { mode: 'create' } | { mode: 'edit'; record: ProductPrefixCodeItem }

interface ManageListQuery {
  page: number
  pageSize: number
  search?: string
}

export default function PrefixCodeManageModal({ visible, supplierCode, supplierName, onClose, onSuccess }: PrefixCodeManageModalProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [list, setList] = useState<ProductPrefixCodeItem[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  // keyword 是输入框文本，search 是已提交（回车 / 清空）真正参与查询的关键词。
  const [keyword, setKeyword] = useState('')
  const [search, setSearch] = useState('')
  const [formTarget, setFormTarget] = useState<PrefixFormTarget | null>(null)
  const [togglingKeys, setTogglingKeys] = useState<Set<string>>(() => new Set())
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const lastFormTargetRef = useRef<PrefixFormTarget>({ mode: 'create' })

  // 对象合并而非默认参数：overrides 里显式 undefined 表示「清除该筛选」。
  const loadList = useCallback(
    async (overrides: Partial<ManageListQuery> = {}) => {
      if (!supplierCode) return
      const query: ManageListQuery = { page, pageSize, search: search || undefined, ...overrides }

      // 快速翻页 / 切换供应商时，只认最后一次请求，旧响应不能覆盖新结果。
      await runLatestGuardedRequest(
        listRequestGuardRef.current,
        async () => {
          const res = await getPrefixCodeList({ page: query.page, pageSize: query.pageSize, search: query.search, supplierCode })
          if (!res.success) {
            throw new Error(res.message || t('prefixCode.loadListFailed'))
          }
          return res.data
        },
        {
          onStart: () => setLoading(true),
          onSuccess: (data) => {
            setList((data?.items ?? []) as ProductPrefixCodeItem[])
            setTotal(data?.total ?? 0)
            setPage(query.page)
            setPageSize(query.pageSize)
          },
          onError: (error) => {
            console.error(error)
            message.error(error instanceof Error && error.message ? error.message : t('prefixCode.loadListFailed'))
          },
          onSettled: () => setLoading(false),
        },
      )
    },
    [supplierCode, page, pageSize, search],
  )

  useEffect(() => {
    if (visible && supplierCode) {
      setPage(1)
      setKeyword('')
      setSearch('')
      setFormTarget(null)
      void loadList({ page: 1, search: undefined })
    }
    // 关闭 / 切换供应商时作废在途请求，晚到的响应不再写入。
    return () => listRequestGuardRef.current.invalidate()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible, supplierCode])

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

  const handleSubmitForm = async (payload: SavePrefixCodePayload) => {
    if (!formTarget) {
      return
    }

    // 请求失败向外抛，由共用弹窗就地提示并保持打开；成功后才关闭并刷新。
    if (formTarget.mode === 'create') {
      const res = await createPrefixCode({
        supplierCode,
        prefixName: payload.prefixName,
        prefixDescription: payload.prefixDescription,
        isActive: payload.isActive,
        sortOrder: payload.sortOrder,
      })
      if (!res.success) {
        throw new Error(res.message || t('prefixCode.saveFailed'))
      }
      message.success(t('prefixCode.createSuccess'))
    } else {
      const res = await updatePrefixCode(formTarget.record.prefixCode, {
        prefixName: payload.prefixName,
        prefixDescription: payload.prefixDescription,
        isActive: payload.isActive,
        sortOrder: payload.sortOrder,
      })
      if (!res.success) {
        throw new Error(res.message || t('prefixCode.saveFailed'))
      }
      message.success(t('prefixCode.updateSuccess'))
    }

    const reloadPage = formTarget.mode === 'create' ? 1 : page
    setFormTarget(null)
    void loadList({ page: reloadPage })
    onSuccess?.()
  }

  const handleDelete = async (record: ProductPrefixCodeItem) => {
    try {
      const res = await deletePrefixCode(record.prefixCode)
      if (!res.success) {
        message.error(res.message || t('prefixCode.deleteFailed'))
        return
      }
      message.success(t('prefixCode.deleteSuccess'))
      // 删掉当前页最后一行时回退一页，避免停在空白页。
      void loadList({ page: list.length <= 1 && page > 1 ? page - 1 : page })
      onSuccess?.()
    } catch (error) {
      // 后端会拒绝删除已被商品使用的前缀，其 message 已说明原因，直接展示。
      console.error(error)
      message.error(error instanceof Error && error.message ? error.message : t('prefixCode.deleteFailed'))
    }
  }

  // 行内直接切换启用状态：先乐观更新，请求失败再回滚。
  const handleToggleStatus = async (record: ProductPrefixCodeItem, checked: boolean) => {
    if (togglingKeys.has(record.prefixCode)) {
      return
    }

    const patchRow = (isActive: boolean) =>
      setList((current) => current.map((row) => (row.prefixCode === record.prefixCode ? { ...row, isActive } : row)))
    setTogglingKeys((current) => new Set(current).add(record.prefixCode))
    patchRow(checked)
    try {
      const res = await togglePrefixCodeStatus(record.prefixCode, checked)
      if (!res.success) {
        throw new Error(res.message || t('prefixCode.statusUpdateFailed'))
      }
      message.success(t('prefixCode.statusUpdated'))
      void loadList()
      onSuccess?.()
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

  const columns: ColumnsType<ProductPrefixCodeItem> = [
    {
      title: t('prefixCode.prefixName'),
      dataIndex: 'prefixName',
      width: 110,
      render: (value: string) => <span className="prefix-code-tag">{value}</span>,
    },
    {
      title: t('prefixCode.prefixDescription'),
      dataIndex: 'prefixDescription',
      render: (value?: string) =>
        value ? <span className="prefix-code-ellipsis" title={value}>{value}</span> : <span className="prefix-code-faint">--</span>,
    },
    {
      title: t('prefixCode.formSort'),
      dataIndex: 'sortOrder',
      width: 72,
      render: (value?: number) => (value === undefined || value === null ? <span className="prefix-code-faint">--</span> : <span className="prefix-code-mono">{value}</span>),
    },
    {
      title: t('domesticProducts.status'),
      dataIndex: 'isActive',
      width: 80,
      render: (value: boolean, record) => (
        <Switch
          checked={value}
          loading={togglingKeys.has(record.prefixCode)}
          onChange={(checked) => void handleToggleStatus(record, checked)}
          aria-label={`${record.prefixName} ${t('domesticProducts.status')}`}
        />
      ),
    },
    {
      title: t('column.updateTime'),
      dataIndex: 'updatedAt',
      width: 118,
      // 旧接口的列表项里没有 updatedAt 字段声明，缺失时退回创建时间，仍显示一个时间。
      render: (_value: string | undefined, record) => (
        <span className="prefix-code-sub">{formatPrefixTimestamp(record.updatedAt ?? record.createdAt)}</span>
      ),
    },
    {
      title: t('column.action'),
      key: 'actions',
      width: 104,
      fixed: 'right',
      align: 'right',
      render: (_value, record) => (
        <div className="prefix-code-actions">
          <Button size="small" type="link" onClick={() => setFormTarget({ mode: 'edit', record })}>
            {t('common.edit')}
          </Button>
          <Popconfirm
            title={t('prefixCode.confirmDeletePrefix')}
            description={t('prefixCode.deleteDescription')}
            okText={t('common.delete')}
            okButtonProps={{ danger: true }}
            cancelText={t('common.cancel')}
            onConfirm={() => handleDelete(record)}
          >
            <Button size="small" type="link" danger>
              {t('common.delete')}
            </Button>
          </Popconfirm>
        </div>
      ),
    },
  ]

  // 关闭动画期间 formTarget 已是 null，内容若立刻切回默认值会闪一下，所以沿用最近一次的目标。
  if (formTarget) {
    lastFormTargetRef.current = formTarget
  }
  const shownFormTarget = formTarget ?? lastFormTargetRef.current
  const editingRecord = shownFormTarget.mode === 'edit' ? shownFormTarget.record : undefined

  return (
    <Modal
      title={t('prefixCode.manageTitle', { name: supplierName || supplierCode })}
      open={visible}
      onCancel={onClose}
      width={880}
      footer={null}
      // 弹窗里有新增 / 编辑流程，误点遮罩会丢内容。
      maskClosable={false}
      destroyOnHidden
    >
      <div className="prefix-code-manage-toolbar" data-testid="prefix-code-manage-toolbar">
        <Input
          placeholder={`${t('prefixCode.searchPrefixOnly')} · ${t('common.listToolbar.searchEnterHint')}`}
          prefix={<SearchOutlined />}
          value={keyword}
          onChange={(event) => handleKeywordChange(event.target.value)}
          onPressEnter={() => commitSearch(keyword)}
          style={{ width: 300 }}
          allowClear
        />
        <span className="prefix-code-spacer" />
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setFormTarget({ mode: 'create' })} data-testid="prefix-code-manage-add">
          {t('prefixCode.addPrefix')}
        </Button>
      </div>
      <MeasuredTable
        metricId="domestic-purchase.product-creation.prefix-code-manage-modal.table-1"
        className="prefix-code-table"
        columns={columns}
        dataSource={list}
        rowKey="prefixCode"
        size="small"
        loading={loading}
        tableLayout="fixed"
        scroll={{ x: 760 }}
        pagination={{
          current: page,
          pageSize,
          total,
          showSizeChanger: true,
          showTotal: (count) => t('common.totalCount', { count }),
          onChange: (nextPage, nextPageSize) => void loadList({ page: nextPageSize === pageSize ? nextPage : 1, pageSize: nextPageSize }),
        }}
      />

      <PrefixCodeFormModal
        open={formTarget !== null}
        mode={shownFormTarget.mode}
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
        // 供应商固定为当前所管理的供应商，不再提供选择。
        fixedSupplier={{ code: supplierCode, name: supplierName }}
        onSubmit={handleSubmitForm}
        onCancel={() => setFormTarget(null)}
      />
    </Modal>
  )
}
