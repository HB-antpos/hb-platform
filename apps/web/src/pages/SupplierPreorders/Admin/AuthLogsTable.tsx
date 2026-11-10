import { App } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getSupplierPreorderAuthLogs } from '../../../services/supplierPreorderService'
import type { SupplierPreorderAuthLog } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { AUTH_LOG_ACTIONS, formatSydneyDateTime, getErrorText, isAbortError } from './supplierPreorderAdmin.logic'

const KNOWN_ACTIONS: ReadonlySet<string> = new Set(AUTH_LOG_ACTIONS)

interface AuthLogsTableProps {
  /** 只看某张订单表的记录（详情页「操作记录」） */
  formGuid?: string
  /** 只看某个人的记录 */
  userGuid?: string
  /** 'Order' = 只看订货数量变更与提交订单；不传 = 管理类记录（与原来一致） */
  scope?: 'Order'
  /** 在内容和操作账号里模糊匹配（一般配合 scope = 'Order' 用） */
  keyword?: string
  /** 变化时重新拉第一页（如保存授权后） */
  refreshKey?: number
  pageSize?: number
}

/** 授权与订单表操作记录：服务端分页。 */
export default function AuthLogsTable({ formGuid, userGuid, scope, keyword, refreshKey = 0, pageSize: initialPageSize = 20 }: AuthLogsTableProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(initialPageSize)
  const [items, setItems] = useState<SupplierPreorderAuthLog[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const lastRefreshKeyRef = useRef(refreshKey)
  const lastFilterRef = useRef(`${scope ?? ''}|${keyword ?? ''}`)

  useEffect(() => {
    // 外部触发刷新、切换范围或关键字时回到第一页
    const filterKey = `${scope ?? ''}|${keyword ?? ''}`
    if (lastRefreshKeyRef.current !== refreshKey || lastFilterRef.current !== filterKey) {
      lastRefreshKeyRef.current = refreshKey
      lastFilterRef.current = filterKey
      if (page !== 1) {
        setPage(1)
        return undefined
      }
    }
    const controller = new AbortController()
    setLoading(true)
    getSupplierPreorderAuthLogs({ page, pageSize, formGuid, userGuid, scope, keyword: keyword?.trim() || undefined }, controller.signal)
      .then((result) => {
        setItems(result.items)
        setTotal(result.total)
      })
      .catch((error) => {
        if (controller.signal.aborted || isAbortError(error)) return
        message.error(getErrorText(error, t('spAdmin.common.loadFailed')))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [formGuid, keyword, message, page, pageSize, refreshKey, scope, t, userGuid])

  const columns = useMemo<ColumnsType<SupplierPreorderAuthLog>>(
    () => [
      {
        title: t('spAdmin.logs.colTime'),
        dataIndex: 'createdAtUtc',
        width: 150,
        className: 'sp-admin-number',
        render: (value: string) => formatSydneyDateTime(value),
      },
      { title: t('spAdmin.logs.colOperator'), dataIndex: 'operatorName', width: 120, render: (value?: string | null) => value || '--' },
      {
        title: t('spAdmin.logs.colAction'),
        dataIndex: 'action',
        width: 140,
        render: (value: string) => (KNOWN_ACTIONS.has(value) ? t(`spAdmin.logs.action.${value}`) : value),
      },
      ...(formGuid
        ? []
        : [
            {
              title: t('spAdmin.logs.colTarget'),
              dataIndex: 'targetUserName',
              width: 140,
              render: (value?: string | null) => value || '--',
            },
          ]),
      { title: t('spAdmin.logs.colDetail'), dataIndex: 'detail', render: (value?: string | null) => value || '--' },
    ],
    [formGuid, t],
  )

  return (
    <MeasuredTable<SupplierPreorderAuthLog>
      metricId="supplier-preorder-auth-logs"
      className="sp-admin-table"
      rowKey="logGuid"
      size="small"
      loading={loading}
      columns={columns}
      dataSource={items}
      scroll={{ x: 720 }}
      locale={{ emptyText: scope === 'Order' ? t('spAdmin.logs.emptyOrders') : t('spAdmin.logs.empty') }}
      pagination={{
        current: page,
        pageSize,
        total,
        showSizeChanger: true,
        pageSizeOptions: ['20', '50', '100'],
        showTotal: (count) => t('spAdmin.logs.total', { count }),
        onChange: (nextPage, nextPageSize) => {
          if (nextPageSize !== pageSize) {
            setPageSize(nextPageSize)
            setPage(1)
          } else {
            setPage(nextPage)
          }
        },
      }}
    />
  )
}
