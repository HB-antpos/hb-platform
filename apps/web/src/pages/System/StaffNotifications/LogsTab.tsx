import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Input, Modal, Select, Tooltip, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  getStaffNotificationBatch,
  getStaffNotificationBatches,
  getStaffNotificationTemplates,
  retryStaffNotificationBatch,
} from '../../../services/staffNotificationService'
import { STAFF_NOTIFICATION_ERROR_CODES } from '../../../types/staffNotification'
import type {
  StaffNotificationBatch,
  StaffNotificationBatchChannel,
  StaffNotificationBatchDetail,
  StaffNotificationBatchResult,
  StaffNotificationRecipientResult,
  StaffNotificationTemplate,
} from '../../../types/staffNotification'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'
import { createLatestRequestGuard, resolveSystemListPagination, runLatestGuardedRequest } from '../listPagination'

import { BatchResultTag, ChannelStatusTag } from './StaffNotificationTags'
import {
  formatChannelRatio,
  getStaffNotificationErrorCode,
  getStaffNotificationErrorMessage,
  isBatchFinished,
  startBatchPolling,
  type BatchPoller,
} from './staffNotificationLogic'
import {
  BATCH_LOG_DEFAULT_PAGE_SIZE,
  buildBatchQuery,
  formatBatchSmsCost,
  formatSydneyDateTime,
  getDefaultLogDateRange,
  replaceBatchInList,
  type BatchLogFilters,
} from './staffNotificationPageLogic'

type DetailState =
  | { status: 'loading' }
  | { status: 'ready'; detail: StaffNotificationBatchDetail }
  | { status: 'error' }

/** 同时自动跟踪进度的未结束批次上限（列表里正在发送的批次）。 */
const MAX_AUTO_POLLED_BATCHES = 10

const EMPTY_DASH = '—'

function createDefaultFilters(): BatchLogFilters {
  return getDefaultLogDateRange(formatSydneyIsoDate(new Date()))
}

/** 发送记录页签：按批次看，展开看到每个人；失败项可重试。 */
export default function LogsTab() {
  const { t } = useTranslation()

  const [draftFilters, setDraftFilters] = useState<BatchLogFilters>(createDefaultFilters)
  const [appliedFilters, setAppliedFilters] = useState<BatchLogFilters>(draftFilters)
  const [items, setItems] = useState<StaffNotificationBatch[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(BATCH_LOG_DEFAULT_PAGE_SIZE)
  const [loading, setLoading] = useState(false)
  const [loadFailed, setLoadFailed] = useState(false)
  const [templates, setTemplates] = useState<StaffNotificationTemplate[]>([])

  const [expandedKeys, setExpandedKeys] = useState<string[]>([])
  const [details, setDetails] = useState<Record<string, DetailState>>({})

  const listGuardRef = useRef(createLatestRequestGuard())
  const pollersRef = useRef(new Map<string, BatchPoller>())
  const expandedKeysRef = useRef<string[]>([])
  expandedKeysRef.current = expandedKeys

  // ===== 轮询：重试后（或列表里本来就在发送的批次）跟踪到结束，卸载 / 重新查询时全部停掉 =====
  const stopAllPollers = useCallback(() => {
    pollersRef.current.forEach((poller) => poller.stop())
    pollersRef.current.clear()
  }, [])

  const applyBatchUpdate = useCallback((detail: StaffNotificationBatchDetail) => {
    setItems((previous) => replaceBatchInList(previous, detail))
    // 已展开的明细跟着刷新，没展开的不缓存（下次展开再取最新的）。
    setDetails((previous) => (previous[detail.id] ? { ...previous, [detail.id]: { status: 'ready', detail } } : previous))
  }, [])

  const startPolling = useCallback((batchId: string) => {
    pollersRef.current.get(batchId)?.stop()
    const release = () => {
      pollersRef.current.delete(batchId)
    }
    const poller = startBatchPolling({
      batchId,
      fetchBatch: (id, signal) => getStaffNotificationBatch(id, signal),
      onUpdate: applyBatchUpdate,
      onFinished: (detail) => {
        release()
        applyBatchUpdate(detail)
      },
      onGiveUp: () => {
        release()
        message.warning(t('system.staffNotifications.logs.pollGiveUp', '暂时查不到重试进度，后台仍在发送，请稍后点「刷新」查看'))
      },
    })
    pollersRef.current.set(batchId, poller)
  }, [applyBatchUpdate, t])

  // ===== 列表 =====
  const loadBatches = useCallback(async (filters: BatchLogFilters, nextPage: number, nextPageSize: number) => {
    // 重新查询前先停掉旧轮询：旧列表里的批次可能已不在新结果中。
    stopAllPollers()
    await runLatestGuardedRequest(
      listGuardRef.current,
      () => getStaffNotificationBatches(buildBatchQuery(filters, nextPage, nextPageSize)),
      {
        onStart: () => {
          setLoading(true)
          setLoadFailed(false)
        },
        onSuccess: (result) => {
          setItems(result.items)
          setTotal(result.total)
          setPage(result.page)
          setPageSize(result.pageSize)
          setExpandedKeys([])
          setDetails({})
          // 列表里还在发送的批次（别人刚发的、或关掉抽屉后仍在跑的）自动跟踪到结束。
          result.items
            .filter((batch) => !isBatchFinished(batch.status))
            .slice(0, MAX_AUTO_POLLED_BATCHES)
            .forEach((batch) => startPolling(batch.id))
        },
        onError: (error) => {
          console.error(error)
          setLoadFailed(true)
          message.error(getStaffNotificationErrorMessage(error, t('system.staffNotifications.logs.loadFailed', '加载发送记录失败')))
        },
        onSettled: () => setLoading(false),
      },
    )
  }, [startPolling, stopAllPollers, t])

  useEffect(() => {
    void loadBatches(appliedFilters, 1, BATCH_LOG_DEFAULT_PAGE_SIZE)
    // 只在挂载时按默认筛选查一次；之后点「查询」才请求。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    // 模板筛选的选项：加载失败静默降级为空选项，不打断记录页。
    let cancelled = false
    getStaffNotificationTemplates()
      .then((list) => {
        if (!cancelled) setTemplates(list)
      })
      .catch(() => undefined)
    const guard = listGuardRef.current
    const pollers = pollersRef.current
    return () => {
      cancelled = true
      guard.invalidate()
      // 卸载：清掉所有轮询，不留悬挂的定时器。
      pollers.forEach((poller) => poller.stop())
      pollers.clear()
    }
  }, [])

  const handleQuery = () => {
    setAppliedFilters(draftFilters)
    void loadBatches(draftFilters, 1, pageSize)
  }

  // ===== 展开：逐人明细 =====
  const loadDetail = useCallback(async (batchId: string) => {
    setDetails((previous) => ({ ...previous, [batchId]: { status: 'loading' } }))
    try {
      const detail = await getStaffNotificationBatch(batchId)
      setDetails((previous) => ({ ...previous, [batchId]: { status: 'ready', detail } }))
    } catch (error) {
      console.error(error)
      setDetails((previous) => ({ ...previous, [batchId]: { status: 'error' } }))
    }
  }, [])

  const handleExpand = (expanded: boolean, record: StaffNotificationBatch) => {
    setExpandedKeys((previous) => (expanded ? [...previous, record.id] : previous.filter((key) => key !== record.id)))
    if (expanded && details[record.id]?.status !== 'ready') void loadDetail(record.id)
  }

  // ===== 重试失败项 =====
  const handleRetry = (batch: StaffNotificationBatch) => {
    Modal.confirm({
      title: t('system.staffNotifications.logs.retryConfirmTitle', '重试这一批的失败项？'),
      content: t('system.staffNotifications.logs.retryConfirmContent', '只重发失败的渠道：邮件失败的重发邮件；只有短信失败的只补短信，不会重新生成密码。'),
      okText: t('system.staffNotifications.logs.retryOk', '开始重试'),
      cancelText: t('common.cancel', '取消'),
      onOk: async () => {
        try {
          const detail = await retryStaffNotificationBatch(batch.id)
          message.success(t('system.staffNotifications.logs.retryStarted', '已开始重试，这一行会随结果更新'))
          applyBatchUpdate(detail)
          // 返回后批次变回 queued，继续轮询到结束。
          if (!isBatchFinished(detail.status)) startPolling(batch.id)
        } catch (error) {
          console.error(error)
          message.error(getStaffNotificationErrorMessage(error, t('system.staffNotifications.logs.retryFailed', '重试失败')))
          // 全部失败项都不能自动重试：服务端已把原因写进每个员工的「说明」，重新加载展开行才能看到。
          if (getStaffNotificationErrorCode(error) === STAFF_NOTIFICATION_ERROR_CODES.nothingToRetry) {
            void loadDetail(batch.id)
          }
        }
      },
    })
  }

  // ===== 选项 =====
  const templateOptions = useMemo(
    () => templates.map((template) => ({ value: template.key, label: template.name })),
    [templates],
  )
  const channelOptions = [
    { value: 'email', label: t('system.staffNotifications.logs.channelEmail', '邮件') },
    { value: 'sms', label: t('system.staffNotifications.logs.channelSms', '短信') },
  ]
  const resultOptions = [
    { value: 'all_sent', label: t('system.staffNotifications.batchResult.allSent', '全部发出') },
    { value: 'partial_failed', label: t('system.staffNotifications.batchResult.partialFailed', '部分失败') },
    { value: 'all_failed', label: t('system.staffNotifications.batchResult.allFailed', '全部失败') },
    { value: 'running', label: t('system.staffNotifications.batchResult.running', '发送中') },
  ]
  const rangeValue: [dayjs.Dayjs, dayjs.Dayjs] | null =
    draftFilters.from && draftFilters.to ? [dayjs(draftFilters.from), dayjs(draftFilters.to)] : null

  // ===== 列 =====
  const ratioCell = (sent: number, requested: number) => formatChannelRatio(sent, requested) ?? <Typography.Text type="secondary">{EMPTY_DASH}</Typography.Text>

  const columns: ColumnsType<StaffNotificationBatch> = [
    {
      title: t('system.staffNotifications.logs.columnTime', '发送时间（悉尼）'),
      dataIndex: 'createdAtUtc',
      width: 170,
      render: (value: string) => formatSydneyDateTime(value),
    },
    { title: t('system.staffNotifications.logs.columnTemplate', '模板'), dataIndex: 'templateName', width: 140, ellipsis: true },
    { title: t('system.staffNotifications.logs.columnOperator', '操作人'), dataIndex: 'createdBy', width: 120, ellipsis: true },
    {
      title: t('system.staffNotifications.logs.columnRecipients', '收件人'),
      dataIndex: 'totalCount',
      width: 90,
      render: (value: number) => t('system.staffNotifications.logs.recipientsCount', '{{count}} 人', { count: value }),
    },
    {
      title: t('system.staffNotifications.logs.columnEmail', '邮件'),
      key: 'email',
      width: 90,
      render: (_, record) => ratioCell(record.emailSent, record.emailRequested),
    },
    {
      title: t('system.staffNotifications.logs.columnSms', '短信'),
      key: 'sms',
      width: 90,
      render: (_, record) => ratioCell(record.smsSent, record.smsRequested),
    },
    {
      title: (
        <Tooltip title={t('system.staffNotifications.logs.smsCostHint', '只统计实际发出的条数')}>
          <span>{t('system.staffNotifications.logs.columnSmsCost', '短信费用')}</span>
        </Tooltip>
      ),
      key: 'cost',
      width: 100,
      render: (_, record) => formatBatchSmsCost(record) ?? <Typography.Text type="secondary">{EMPTY_DASH}</Typography.Text>,
    },
    {
      title: t('system.staffNotifications.logs.columnResult', '结果'),
      key: 'result',
      width: 150,
      render: (_, record) => (
        <span className="staff-notify-result-cell">
          <BatchResultTag result={record.result} />
          {record.status === 'interrupted' ? (
            <Typography.Text type="secondary" className="staff-notify-interrupted">
              {t('system.staffNotifications.logs.interrupted', '已中断')}
            </Typography.Text>
          ) : null}
        </span>
      ),
    },
    {
      title: t('system.staffNotifications.logs.columnAction', '操作'),
      key: 'action',
      width: 120,
      render: (_, record) => {
        return record.canRetry && isBatchFinished(record.status)
          ? <Button size="small" onClick={() => handleRetry(record)}>{t('system.staffNotifications.logs.retry', '重试失败项')}</Button>
          : <Typography.Text type="secondary">{EMPTY_DASH}</Typography.Text>
      },
    },
  ]

  const renderDetail = (batch: StaffNotificationBatch) => {
    const state = details[batch.id]
    if (!state || state.status === 'loading') {
      return <div className="staff-notify-detail-state"><Typography.Text type="secondary">…</Typography.Text></div>
    }
    if (state.status === 'error') {
      return (
        <Alert
          type="error"
          showIcon
          message={t('system.staffNotifications.logs.detailLoadFailed', '加载逐人明细失败')}
          action={<Button size="small" onClick={() => void loadDetail(batch.id)}>{t('system.staffNotifications.logs.detailRetry', '重试加载')}</Button>}
        />
      )
    }
    const finished = isBatchFinished(state.detail.status)
    const dash = <Typography.Text type="secondary">{EMPTY_DASH}</Typography.Text>
    const detailColumns: ColumnsType<StaffNotificationRecipientResult> = [
      {
        title: t('system.staffNotifications.logs.detailEmployee', '员工'),
        key: 'employee',
        width: 190,
        render: (_, record) => (
          <span className="staff-notify-detail-person">
            <span>{record.displayName}</span>
            {record.storeName ? <span className="staff-notify-detail-store">{record.storeName}</span> : null}
          </span>
        ),
      },
      { title: t('system.staffNotifications.logs.detailEmail', '邮箱'), dataIndex: 'emailMask', width: 200, render: (value: string | null) => value || dash },
      { title: t('system.staffNotifications.logs.detailPhone', '手机号'), dataIndex: 'phoneMask', width: 140, render: (value: string | null) => value || dash },
      {
        title: t('system.staffNotifications.logs.detailEmailStatus', '邮件'),
        dataIndex: 'emailStatus',
        width: 100,
        render: (value: StaffNotificationRecipientResult['emailStatus']) => <ChannelStatusTag status={value} batchFinished={finished} />,
      },
      {
        title: t('system.staffNotifications.logs.detailSmsStatus', '短信'),
        dataIndex: 'smsStatus',
        width: 100,
        render: (value: StaffNotificationRecipientResult['smsStatus']) => <ChannelStatusTag status={value} batchFinished={finished} />,
      },
      {
        title: t('system.staffNotifications.logs.detailNote', '说明'),
        dataIndex: 'note',
        render: (value: string) => (value ? <span className="staff-notify-detail-note">{value}</span> : dash),
      },
    ]
    return (
      <MeasuredTable<StaffNotificationRecipientResult>
        metricId="system.staff-notifications.batch-recipients"
        rowKey="id"
        size="small"
        pagination={false}
        columns={detailColumns}
        dataSource={state.detail.recipients}
        locale={{ emptyText: t('system.staffNotifications.logs.detailEmpty', '这一批没有逐人明细') }}
        scroll={{ x: 760 }}
      />
    )
  }

  return (
    <div className="staff-notify-logs">
      <div className="staff-notify-toolbar">
        <DatePicker.RangePicker
          style={{ width: 260 }}
          allowClear
          format="YYYY-MM-DD"
          placeholder={[t('system.staffNotifications.logs.dateStart', '开始日期'), t('system.staffNotifications.logs.dateEnd', '结束日期')]}
          value={rangeValue}
          onChange={(_, dateStrings) => {
            const [from, to] = dateStrings
            setDraftFilters((previous) => ({ ...previous, from: from || undefined, to: to || undefined }))
          }}
        />
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: 150 }}
          placeholder={t('system.staffNotifications.logs.templateFilter', '模板')}
          aria-label={t('system.staffNotifications.logs.templateFilter', '模板')}
          options={templateOptions}
          value={draftFilters.templateKey}
          onChange={(value?: string) => setDraftFilters((previous) => ({ ...previous, templateKey: value }))}
        />
        <Select
          allowClear
          style={{ width: 110 }}
          placeholder={t('system.staffNotifications.logs.channelFilter', '渠道')}
          aria-label={t('system.staffNotifications.logs.channelFilter', '渠道')}
          options={channelOptions}
          value={draftFilters.channel}
          onChange={(value?: StaffNotificationBatchChannel) => setDraftFilters((previous) => ({ ...previous, channel: value }))}
        />
        <Select
          allowClear
          style={{ width: 120 }}
          placeholder={t('system.staffNotifications.logs.resultFilter', '结果')}
          aria-label={t('system.staffNotifications.logs.resultFilter', '结果')}
          options={resultOptions}
          value={draftFilters.result}
          onChange={(value?: StaffNotificationBatchResult) => setDraftFilters((previous) => ({ ...previous, result: value }))}
        />
        <Input
          className="staff-notify-search"
          allowClear
          prefix={<SearchOutlined />}
          placeholder={t('system.staffNotifications.logs.keywordPlaceholder', '搜索员工 / 操作人')}
          aria-label={t('system.staffNotifications.logs.keywordPlaceholder', '搜索员工 / 操作人')}
          value={draftFilters.keyword ?? ''}
          onChange={(event) => setDraftFilters((previous) => ({ ...previous, keyword: event.target.value }))}
          onPressEnter={handleQuery}
        />
        <Button type="primary" loading={loading} onClick={handleQuery}>
          {t('system.staffNotifications.logs.query', '查询')}
        </Button>
        <Tooltip title={t('system.staffNotifications.logs.refresh', '刷新')}>
          <Button
            className="staff-notify-refresh"
            icon={<ReloadOutlined />}
            aria-label={t('system.staffNotifications.logs.refresh', '刷新')}
            // 刷新沿用上一次点「查询」时的筛选，而不是输入框里还没提交的改动。
            onClick={() => void loadBatches(appliedFilters, page, pageSize)}
          />
        </Tooltip>
      </div>

      <MeasuredTable<StaffNotificationBatch>
        metricId="system.staff-notifications.batches"
        rowKey="id"
        size="middle"
        loading={loading}
        columns={columns}
        dataSource={items}
        scroll={{ x: 1040 }}
        locale={{
          emptyText: loadFailed
            ? t('system.staffNotifications.logs.loadFailed', '加载发送记录失败')
            : t('system.staffNotifications.logs.empty', '没有符合条件的发送记录'),
        }}
        expandable={{
          expandedRowKeys: expandedKeys,
          onExpand: handleExpand,
          expandedRowRender: renderDetail,
        }}
        pagination={{
          current: page,
          pageSize,
          total,
          showSizeChanger: true,
          showTotal: (count) => t('system.staffNotifications.logs.total', '共 {{count}} 批', { count }),
        }}
        onChange={(pagination, _filters, _sorter, extra) => {
          const next = resolveSystemListPagination(extra.action, pagination, pageSize)
          void loadBatches(appliedFilters, next.page, next.pageSize)
        }}
      />
    </div>
  )
}
