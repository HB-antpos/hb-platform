import { Alert, Button, Checkbox, Descriptions, Modal, Select, Space, Spin, Tag, Typography, message, notification } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  createWarehouseStorePriceSyncJob,
  createWarehouseStorePriceSyncJobPoller,
  getAllWarehouseProductCount,
  getWarehouseStorePriceSyncJob,
  getWarehouseStorePriceSyncTargetStores,
  HqProductSyncPollingCancelledError,
  HqProductSyncPollingTimeoutError,
  type WarehouseStorePriceSyncJob,
  type WarehouseStorePriceSyncTargetStore,
} from '../../../services/warehouseStorePriceSyncService'
import {
  buildWarehouseStorePriceSyncPayload,
  getWarehouseStorePriceSyncScopeSummary,
  isWarehouseStorePriceSyncTerminalStatus,
  summarizeWarehouseStorePriceSyncResult,
  validateWarehouseStorePriceSyncInput,
} from './warehouseStorePriceSync.logic'
import type {
  WarehouseStorePriceSyncError,
  WarehouseStorePriceSyncJobStatus,
} from './warehouseStorePriceSync.logic'
import { WAREHOUSE_STORE_PRICE_SYNC_FIELD_MAPPINGS } from './warehouseStorePriceSync.logic'
import { HQ_ACTIONS_ENABLED } from '../../../utils/legacyDatabases'

interface WarehouseProductStorePriceSyncModalProps {
  open: boolean
  selectedProductCodes: readonly string[]
  onCancel: () => void
  onSuccess: () => void
}

const STATUS_COLORS: Record<WarehouseStorePriceSyncJobStatus, string> = {
  Pending: 'default',
  Running: 'processing',
  Succeeded: 'success',
  PartiallySucceeded: 'warning',
  Failed: 'error',
}

function statusLabel(status: WarehouseStorePriceSyncJobStatus, t: ReturnType<typeof useTranslation>['t']) {
  return t(`warehouse.storePriceSync.status.${status}`, status)
}

function formatStorePriceSyncError(error: WarehouseStorePriceSyncError, t: ReturnType<typeof useTranslation>['t']) {
  const context = [
    error.productCode
      ? `${t('warehouse.storePriceSync.errorProductCode', '商品')}: ${error.productCode}`
      : null,
    error.storeCode
      ? `${t('warehouse.storePriceSync.errorStoreCode', '分店')}: ${error.storeCode}`
      : null,
    error.stage
      ? `${t('warehouse.storePriceSync.errorStage', '阶段')}: ${error.stage}`
      : null,
    error.code
      ? `${t('warehouse.storePriceSync.errorCode', '代码')}: ${error.code}`
      : null,
  ].filter((value): value is string => Boolean(value))
  return `${context.length ? `${context.join(' / ')}：` : ''}${error.message}`
}

function formatMappingValue(value: unknown, t: ReturnType<typeof useTranslation>['t']) {
  if (value === 0) return t('warehouse.storePriceSync.zero', '0')
  if (value === false) return t('warehouse.storePriceSync.off', '关闭')
  return ''
}

function ResultSummary({
  job,
  t,
}: {
  job: WarehouseStorePriceSyncJob
  t: ReturnType<typeof useTranslation>['t']
}) {
  const summary = summarizeWarehouseStorePriceSyncResult(job)
  return (
    <Space direction="vertical" size={8} style={{ width: '100%' }}>
      <Space size={8}>
        <Typography.Text strong>{t('warehouse.storePriceSync.resultStatus', '结果状态')}</Typography.Text>
        <Tag color={STATUS_COLORS[summary.status]}>{statusLabel(summary.status, t)}</Tag>
      </Space>
      <Descriptions size="small" column={2} bordered>
        <Descriptions.Item label={t('warehouse.storePriceSync.requestedProducts', '请求商品数')}>
          {summary.requestedProductCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.eligibleProducts', '可处理商品数')}>
          {summary.eligibleProductCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.skippedProducts', '跳过商品数')}>
          {summary.skippedProductCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.failedItems', '失败数量')}>
          {summary.failedCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.targetStores', '目标分店数')}>
          {summary.targetStoreCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.localCreated', '本地新增')}>
          {summary.localCreatedCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.localUpdated', '本地更新')}>
          {summary.localUpdatedCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.hqCreated', 'HQ 新增')}>
          {summary.hqCreatedCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.hqUpdated', 'HQ 更新')}>
          {summary.hqUpdatedCount}
        </Descriptions.Item>
        <Descriptions.Item label={t('warehouse.storePriceSync.hqProvisionedProducts', 'HQ 补齐商品数')}>
          {summary.hqProvisionedProductCount}
        </Descriptions.Item>
      </Descriptions>
      {summary.errors.length ? (
        <Alert
          type="error"
          showIcon
          message={t('warehouse.storePriceSync.errors', '错误详情')}
          description={(
            <div style={{ whiteSpace: 'pre-wrap' }}>
              {summary.errors.map((error) => formatStorePriceSyncError(error, t)).join('\n')}
            </div>
          )}
        />
      ) : null}
    </Space>
  )
}

export default function WarehouseProductStorePriceSyncModal({
  open,
  selectedProductCodes,
  onCancel,
  onSuccess,
}: WarehouseProductStorePriceSyncModalProps) {
  const { t } = useTranslation()
  const [productCodes, setProductCodes] = useState<string[]>([])
  const [targetStores, setTargetStores] = useState<WarehouseStorePriceSyncTargetStore[]>([])
  const [targetStoreCodes, setTargetStoreCodes] = useState<string[]>([])
  const [targetStoresLoading, setTargetStoresLoading] = useState(false)
  const [targetStoresError, setTargetStoresError] = useState<string | null>(null)
  const [allProductCount, setAllProductCount] = useState<number>()
  const [allProductCountLoading, setAllProductCountLoading] = useState(false)
  const [allProductCountError, setAllProductCountError] = useState<string | null>(null)
  const [syncToHq, setSyncToHq] = useState(false)
  const [job, setJob] = useState<WarehouseStorePriceSyncJob | null>(null)
  const [jobError, setJobError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [pollingActive, setPollingActive] = useState(false)
  const stopPollingRef = useRef<(() => void) | null>(null)
  const requestSequenceRef = useRef(0)
  const mountedRef = useRef(true)
  const submittingRef = useRef(false)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      stopPollingRef.current?.()
      stopPollingRef.current = null
    }
  }, [])

  useEffect(() => {
    const requestId = requestSequenceRef.current + 1
    requestSequenceRef.current = requestId
    stopPollingRef.current?.()
    stopPollingRef.current = null
    setPollingActive(false)

    if (!open) return

    const nextProductCodes = Array.from(new Set(selectedProductCodes.map((code) => String(code).trim()).filter(Boolean)))
    setProductCodes(nextProductCodes)
    setTargetStores([])
    setTargetStoreCodes([])
    setTargetStoresLoading(true)
    setTargetStoresError(null)
    setAllProductCount(undefined)
    setAllProductCountError(null)
    setAllProductCountLoading(nextProductCodes.length === 0)
    // 每次打开都明确回到关闭，避免上一次 HQ 选择被意外复用。
    setSyncToHq(false)
    setJob(null)
    setJobError(null)
    setSubmitting(false)
    submittingRef.current = false

    void getWarehouseStorePriceSyncTargetStores()
      .then((stores) => {
        if (!mountedRef.current || requestSequenceRef.current !== requestId) return
        setTargetStores(stores)
      })
      .catch((error) => {
        if (!mountedRef.current || requestSequenceRef.current !== requestId) return
        setTargetStoresError(error instanceof Error ? error.message : t('warehouse.storePriceSync.loadStoresFailed', '加载目标分店失败'))
      })
      .finally(() => {
        if (mountedRef.current && requestSequenceRef.current === requestId) setTargetStoresLoading(false)
      })

    if (nextProductCodes.length === 0) {
      void getAllWarehouseProductCount()
        .then((count) => {
          if (!mountedRef.current || requestSequenceRef.current !== requestId) return
          setAllProductCount(count)
        })
        .catch((error) => {
          if (!mountedRef.current || requestSequenceRef.current !== requestId) return
          setAllProductCountError(error instanceof Error ? error.message : t('warehouse.storePriceSync.loadProductCountFailed', '加载无筛选商品总数失败'))
        })
        .finally(() => {
          if (mountedRef.current && requestSequenceRef.current === requestId) setAllProductCountLoading(false)
        })
    }
  }, [open])

  const scopeSummary = useMemo(() => getWarehouseStorePriceSyncScopeSummary({
    productCodes,
    allProductCount,
    targetStoreCount: targetStoreCodes.length,
  }), [allProductCount, productCodes, targetStoreCodes.length])
  const applyToAllProducts = scopeSummary.isFullScope
  const jobInProgress = job?.status === 'Pending' || job?.status === 'Running'
  const busy = submitting || pollingActive
  const countUnavailable = applyToAllProducts && (allProductCountLoading || allProductCount === undefined || Boolean(allProductCountError))

  const renderScopeDescription = () => (
    <Space direction="vertical" size={4} style={{ width: '100%' }}>
      {applyToAllProducts ? (
        <Alert
          type="warning"
          showIcon
          message={t('warehouse.storePriceSync.fullScopeWarning', '未勾选将处理全部仓库商品（包含已下架）')}
        />
      ) : null}
      <Typography.Text>
        {applyToAllProducts
          ? allProductCount === undefined
            ? t('warehouse.storePriceSync.loadingProductCount', '正在加载无筛选商品总数…')
            : t('warehouse.storePriceSync.fullScopeSummary', '无筛选商品总数：{{count}}，本地最大写入量：{{count}} × {{stores}} = {{writes}}', {
              count: scopeSummary.productCount,
              stores: targetStoreCodes.length,
              writes: scopeSummary.maxWriteCount,
            })
          : t('warehouse.storePriceSync.selectedScopeSummary', '已勾选 {{count}} 个商品，本地最大写入量：{{count}} × {{stores}} = {{writes}}', {
              count: scopeSummary.productCount,
              stores: targetStoreCodes.length,
              writes: scopeSummary.maxWriteCount,
            })}
      </Typography.Text>
      {allProductCountError ? <Typography.Text type="danger">{allProductCountError}</Typography.Text> : null}
    </Space>
  )

  const finishJob = async (completedJob: WarehouseStorePriceSyncJob) => {
    if (!mountedRef.current) return
    setJob(completedJob)
    setSubmitting(false)
    setPollingActive(false)
    submittingRef.current = false

    if (completedJob.status === 'Succeeded') {
      const summary = summarizeWarehouseStorePriceSyncResult(completedJob)
      notification.success({
        message: t('warehouse.storePriceSync.succeeded', '分店价格表更新完成'),
        description: (
          <ResultSummary job={completedJob} t={t} />
        ),
        duration: 8,
      })
      onSuccess()
      onCancel()
      // 引用摘要避免通知展示和弹窗状态脱钩时被误判为未使用。
      void summary
      return
    }

    if (completedJob.status === 'PartiallySucceeded') {
      message.warning(t('warehouse.storePriceSync.partiallySucceeded', '分店价格表更新部分成功，请检查跳过与失败明细；不要重试已跳过的商品'))
    }
  }

  const startPolling = (createdJob: WarehouseStorePriceSyncJob) => {
    setPollingActive(true)
    const poller = createWarehouseStorePriceSyncJobPoller({
      jobId: createdJob.jobId,
      getJob: async (jobId) => {
        const nextJob = await getWarehouseStorePriceSyncJob(jobId)
        if (mountedRef.current) setJob(nextJob)
        return nextJob
      },
    })
    stopPollingRef.current = poller.stop
    void poller.promise
      .then((completedJob) => {
        stopPollingRef.current = null
        return finishJob(completedJob)
      })
      .catch((error) => {
        stopPollingRef.current = null
        if (!mountedRef.current || error instanceof HqProductSyncPollingCancelledError) return
        setSubmitting(false)
        submittingRef.current = false
        const fallbackMessage = error instanceof HqProductSyncPollingTimeoutError
          ? t('warehouse.storePriceSync.pollingTimeout', '前端已停止轮询，任务可能仍在后台执行，请稍后重试或刷新。')
          : error instanceof Error
            ? error.message
            : t('warehouse.storePriceSync.pollingFailed', '查询分店价格更新任务失败')
        setJobError(fallbackMessage)
        setPollingActive(false)
        // 轮询失败只代表前端失去查询能力，保留最后一次服务端状态，避免误导用户重复提交。
      })
  }

  const retryPolling = () => {
    if (!job || pollingActive || submitting || !jobInProgress || stopPollingRef.current) return
    setJobError(null)
    startPolling(job)
  }

  const submitJob = async () => {
    if (submittingRef.current || jobInProgress) return
    const validationError = validateWarehouseStorePriceSyncInput({
      productCodes,
      targetStoreCodes,
    })
    if (validationError) {
      message.warning(t('warehouse.storePriceSync.targetStoresRequired', '请至少选择一个目标分店'))
      return
    }
    if (countUnavailable) {
      message.warning(t('warehouse.storePriceSync.productCountRequired', '无筛选商品总数尚未加载完成，请重试'))
      return
    }

    const payload = buildWarehouseStorePriceSyncPayload({
      productCodes,
      targetStoreCodes,
      syncToHq,
    })
    submittingRef.current = true
    setSubmitting(true)
    setJob(null)
    setJobError(null)
    try {
      const createdJob = await createWarehouseStorePriceSyncJob(payload)
      if (!mountedRef.current) return
      setJob(createdJob)
      if (isWarehouseStorePriceSyncTerminalStatus(createdJob.status)) {
        await finishJob(createdJob)
      } else {
        startPolling(createdJob)
      }
    } catch (error) {
      if (!mountedRef.current) return
      submittingRef.current = false
      setSubmitting(false)
      setJobError(error instanceof Error ? error.message : t('warehouse.storePriceSync.createFailed', '创建分店价格更新任务失败'))
    }
  }

  const handleOk = () => {
    if (applyToAllProducts) {
      Modal.confirm({
        title: t('warehouse.storePriceSync.fullScopeConfirmTitle', '确认处理全部仓库商品？'),
        content: t('warehouse.storePriceSync.fullScopeConfirmContent', '本次将忽略当前筛选，处理全部未删除仓库商品（包含已下架）。确认继续吗？'),
        okText: t('warehouse.storePriceSync.confirm', '确认更新'),
        cancelText: t('common.cancel', '取消'),
        okButtonProps: { danger: true },
        onOk: submitJob,
      })
      return
    }
    void submitJob()
  }

  const handleCancel = () => {
    if (busy) return
    onCancel()
  }

  const selectOptions = targetStores.map((store) => ({
    value: store.storeCode,
    label: store.storeName ? `${store.storeName}（${store.storeCode}）` : store.storeCode,
  }))

  return (
    <Modal
      open={open}
      title={t('warehouse.storePriceSync.title', '更新分店价格')}
      width="min(720px, calc(100vw - 32px))"
      okText={t('warehouse.storePriceSync.submit', '提交更新')}
      cancelText={t('common.cancel', '取消')}
      confirmLoading={submitting || pollingActive}
      okButtonProps={{
        disabled: targetStoresLoading || Boolean(targetStoresError) || !targetStoreCodes.length || countUnavailable || busy || jobInProgress,
      }}
      cancelButtonProps={{ disabled: busy }}
      closable={!busy}
      keyboard={!busy}
      maskClosable={!busy}
      onOk={handleOk}
      onCancel={handleCancel}
      destroyOnHidden
    >
      <Space direction="vertical" size={14} style={{ width: '100%' }}>
        {renderScopeDescription()}

        <div>
          <Typography.Text strong>{t('warehouse.storePriceSync.targetStoresLabel', '目标分店')}</Typography.Text>
          <Select
            mode="multiple"
            showSearch
            optionFilterProp="label"
            value={targetStoreCodes}
            options={selectOptions}
            loading={targetStoresLoading}
            disabled={busy || jobInProgress || targetStoresLoading}
            placeholder={t('warehouse.storePriceSync.targetStoresPlaceholder', '请选择目标分店（可多选）')}
            style={{ width: '100%', marginTop: 6 }}
            onChange={(values) => setTargetStoreCodes(values.map(String))}
          />
          {targetStoresError ? (
            <Space size={8} style={{ marginTop: 8 }}>
              <Typography.Text type="danger">{targetStoresError}</Typography.Text>
              <Typography.Link onClick={() => {
                setTargetStoresError(null)
                setTargetStoresLoading(true)
                void getWarehouseStorePriceSyncTargetStores()
                  .then(setTargetStores)
                  .catch((error) => setTargetStoresError(error instanceof Error ? error.message : t('warehouse.storePriceSync.loadStoresFailed', '加载目标分店失败')))
                  .finally(() => setTargetStoresLoading(false))
              }}>{t('common.retry', '重试')}</Typography.Link>
            </Space>
          ) : null}
        </div>

        <div>
          <Typography.Text strong>{t('warehouse.storePriceSync.fixedMapping', '固定价格字段映射')}</Typography.Text>
          <Space direction="vertical" size={4} style={{ width: '100%', marginTop: 6 }}>
            {WAREHOUSE_STORE_PRICE_SYNC_FIELD_MAPPINGS.map((mapping) => (
              <Typography.Text key={mapping.source} type="secondary">
                {t(mapping.sourceLabelKey, mapping.source === 'importPrice' ? '进口价' : mapping.source === 'retailPrice' ? '零售价' : mapping.source === 'discountRate' ? '折扣率' : '自动定价')}
                {' → '}
                {t(mapping.targetLabelKey, mapping.target === 'purchasePrice' ? '进货价' : mapping.target === 'storeRetailPrice' ? '分店零售价' : mapping.target === 'discountRate' ? '折扣率' : '自动定价')}
                {mapping.fixedValue !== undefined ? `：${formatMappingValue(mapping.fixedValue, t)}` : null}
              </Typography.Text>
            ))}
          </Space>
        </div>

        {HQ_ACTIONS_ENABLED && (
          <Checkbox checked={syncToHq} disabled={busy || jobInProgress} onChange={(event) => setSyncToHq(event.target.checked)}>
          {t('warehouse.storePriceSync.syncToHq', '同时更新 HQ 对应分店价格表')}
        </Checkbox>
        )}
        {syncToHq ? (
          <Alert
            type="warning"
            showIcon
            message={t(
              'warehouse.storePriceSync.hqWriteExpansionWarning',
              'HQ 缺少主商品时会向全部 HQ 分店建立必要关联，HQ 实际写入量可能高于上方本地最大写入量。',
            )}
          />
        ) : null}

        {job && (job.status === 'Pending' || job.status === 'Running') ? (
          <Alert
            type={pollingActive ? 'info' : 'warning'}
            showIcon
            message={t('warehouse.storePriceSync.jobRunning', '后台任务执行中')}
            description={<Space direction="vertical" size={4}>
              <div><Tag color={STATUS_COLORS[job.status]}>{statusLabel(job.status, t)}</Tag>{jobError || job.message || (pollingActive ? t('warehouse.storePriceSync.waiting', '正在等待后台完成…') : t('warehouse.storePriceSync.pollingTimeout', '前端已停止轮询，任务可能仍在后台执行，请稍后重试或刷新。'))}</div>
              <div>{t('warehouse.storePriceSync.jobId', '任务 ID')}: {job.jobId}</div>
              {!pollingActive ? <Button type="link" size="small" onClick={retryPolling}>{t('common.retry', '重试')}</Button> : null}
            </Space>}
          />
        ) : null}
        {targetStoresLoading || (applyToAllProducts && allProductCountLoading) ? <Spin size="small" /> : null}
        {jobError && !jobInProgress ? <Alert type="error" showIcon message={jobError} /> : null}
        {job && isWarehouseStorePriceSyncTerminalStatus(job.status) ? <ResultSummary job={job} t={t} /> : null}
      </Space>
    </Modal>
  )
}
