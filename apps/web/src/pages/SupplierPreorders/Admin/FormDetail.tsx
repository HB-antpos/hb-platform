import {
  ArrowLeftOutlined,
  DeleteOutlined,
  DownloadOutlined,
  DownOutlined,
  EditOutlined,
  ReloadOutlined,
  SyncOutlined,
  TeamOutlined,
} from '@ant-design/icons'
import { Alert, App, Button, Drawer, Dropdown, Form, Result, Spin, Tabs, Tag, Tooltip } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router-dom'

import {
  deleteSupplierPreorderForm,
  downloadSupplierPreorderExport,
  getSupplierPreorderForm,
  recomputeSupplierPreorderHistory,
  retrySupplierPreorderFailedImages,
  setSupplierPreorderFormStatus,
  updateSupplierPreorderForm,
} from '../../../services/supplierPreorderService'
import type { SupplierPreorderFormDetail, SupplierPreorderFormStatus } from '../../../types/supplierPreorder'

import AuthLogsTable from './AuthLogsTable'
import FormHeaderFields from './FormHeaderFields'
import { buildHeaderValuesFromDetail, toHeaderPayload, type FormHeaderValues } from './formHeaderValues'
import FormItemsTab from './FormItemsTab'
import FormMatrixPanel from './FormMatrixPanel'
import FormStoresPanel from './FormStoresPanel'
import { HISTORY_POLL_INTERVAL_MS, canRecomputeHistory, getHistoryWindow, shouldPollHistory } from './refPicker.logic'
import './registerSpAdminMessages'
import { DeadlineText, FormStatusPill } from './SpAdminParts'
import {
  canDeleteForm,
  formatCbm,
  formatInteger,
  formatMoney,
  formatSydneyDateTime,
  getErrorText,
  getNotStartedStoreCount,
  isAbortError,
  normalizeDateOnly,
} from './supplierPreorderAdmin.logic'
import './styles.css'

type DetailTab = 'stores' | 'items' | 'logs'

/** 订单表详情（/supplier-preorders/forms/:formGuid）：分店开启与汇总矩阵、商品、操作记录。 */
export default function SupplierPreorderFormDetailPage() {
  const { t } = useTranslation()
  const { message, modal } = App.useApp()
  const navigate = useNavigate()
  const { formGuid = '' } = useParams<{ formGuid: string }>()
  const [editForm] = Form.useForm<FormHeaderValues>()

  const [detail, setDetail] = useState<SupplierPreorderFormDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [loadedAtMs, setLoadedAtMs] = useState(() => Date.now())
  const [activeTab, setActiveTab] = useState<DetailTab>('stores')
  const [matrixRefreshKey, setMatrixRefreshKey] = useState(0)
  const [logsRefreshKey, setLogsRefreshKey] = useState(0)
  const [statusSaving, setStatusSaving] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [editSaving, setEditSaving] = useState(false)
  const [recomputing, setRecomputing] = useState(false)
  const [retryingImages, setRetryingImages] = useState(false)
  const loadSeqRef = useRef(0)

  const loadDetail = useCallback(
    (signal?: AbortSignal) => {
      if (!formGuid) return
      const seq = ++loadSeqRef.current
      setLoading(true)
      getSupplierPreorderForm(formGuid, signal)
        .then((result) => {
          if (seq !== loadSeqRef.current) return
          setDetail(result)
          setLoadError(null)
          setLoadedAtMs(Date.now())
        })
        .catch((error) => {
          if (signal?.aborted || isAbortError(error) || seq !== loadSeqRef.current) return
          setLoadError(getErrorText(error, t('spAdmin.common.loadFailed')))
        })
        .finally(() => {
          if (seq === loadSeqRef.current) setLoading(false)
        })
    },
    [formGuid, t],
  )

  useEffect(() => {
    const controller = new AbortController()
    loadDetail(controller.signal)
    return () => controller.abort()
  }, [loadDetail])

  // 关键逻辑：去年历史排队中 / 计算中时每 15 秒刷新详情；页面隐藏时不发请求，回到前台立即补一次；卸载或状态结束即清理
  const historyStatus = detail?.history?.status
  useEffect(() => {
    if (!shouldPollHistory(historyStatus)) return undefined
    const tick = () => {
      if (document.visibilityState === 'visible') loadDetail()
    }
    const timer = window.setInterval(tick, HISTORY_POLL_INTERVAL_MS)
    document.addEventListener('visibilitychange', tick)
    return () => {
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', tick)
    }
  }, [historyStatus, loadDetail])

  // 指定 / 清除参考后只刷新页头计数（商品行已在商品页签局部替换）
  const handleRefChanged = useCallback(() => loadDetail(), [loadDetail])

  // 分店开关 / 复制后：页头统计、矩阵列、操作记录都要刷新
  const handleStoresChanged = useCallback(() => {
    loadDetail()
    setMatrixRefreshKey((value) => value + 1)
    setLogsRefreshKey((value) => value + 1)
  }, [loadDetail])

  if (!detail) {
    return (
      <div className="page-container">
        {loadError ? (
          <Result
            status="warning"
            title={loadError}
            extra={
              <>
                <Button onClick={() => navigate('/supplier-preorders')}>{t('spAdmin.detail.backToList')}</Button>
                <Button type="primary" onClick={() => loadDetail()}>
                  {t('spAdmin.common.retry')}
                </Button>
              </>
            }
          />
        ) : (
          <div className="sp-admin-loading">
            <Spin spinning={loading} />
          </div>
        )}
      </div>
    )
  }

  const { summary } = detail
  const notStarted = getNotStartedStoreCount(summary)
  const deletable = canDeleteForm(summary)

  const changeStatus = async (status: SupplierPreorderFormStatus, successKey: string) => {
    setStatusSaving(true)
    try {
      const next = await setSupplierPreorderFormStatus(formGuid, status)
      setDetail(next)
      setLoadedAtMs(Date.now())
      setLogsRefreshKey((value) => value + 1)
      message.success(t(successKey))
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.common.saveFailed')))
    } finally {
      setStatusSaving(false)
    }
  }

  const handlePublish = () => {
    if (summary.enabledStoreCount === 0) {
      modal.confirm({
        title: t('spAdmin.detail.publishNoStoreTitle'),
        content: t('spAdmin.detail.publishNoStoreContent'),
        okText: t('spAdmin.detail.publish'),
        cancelText: t('spAdmin.common.cancel'),
        onOk: () => changeStatus('Open', 'spAdmin.detail.published'),
      })
      return
    }
    void changeStatus('Open', 'spAdmin.detail.published')
  }

  const handleClose = () => {
    modal.confirm({
      title: t('spAdmin.detail.closeConfirmTitle'),
      content: t('spAdmin.detail.closeConfirmContent', { submitted: summary.submittedStoreCount, draft: summary.draftStoreCount, notStarted }),
      okText: t('spAdmin.detail.close'),
      cancelText: t('spAdmin.common.cancel'),
      onOk: () => changeStatus('Closed', 'spAdmin.detail.closed'),
    })
  }

  const handleReopen = () => {
    modal.confirm({
      title: t('spAdmin.detail.reopenConfirmTitle'),
      content: t('spAdmin.detail.reopenConfirmContent'),
      okText: t('spAdmin.detail.reopen'),
      cancelText: t('spAdmin.common.cancel'),
      onOk: () => changeStatus('Open', 'spAdmin.detail.reopened'),
    })
  }

  const handleExport = async (format: 'original' | 'generic') => {
    setExporting(true)
    try {
      await downloadSupplierPreorderExport(formGuid, format)
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.detail.exportFailed')))
    } finally {
      setExporting(false)
    }
  }

  const handleDelete = () => {
    modal.confirm({
      title: t('spAdmin.detail.deleteConfirmTitle', { title: summary.title }),
      content: t('spAdmin.detail.deleteConfirmContent'),
      okText: t('spAdmin.detail.delete'),
      okButtonProps: { danger: true },
      cancelText: t('spAdmin.common.cancel'),
      onOk: async () => {
        try {
          await deleteSupplierPreorderForm(formGuid)
          message.success(t('spAdmin.detail.deleted'))
          navigate('/supplier-preorders')
        } catch (error) {
          message.error(getErrorText(error, t('spAdmin.detail.deleteFailed')))
        }
      },
    })
  }

  const handleRetryImages = async () => {
    setRetryingImages(true)
    try {
      const next = await retrySupplierPreorderFailedImages(formGuid)
      setDetail(next)
      setLoadedAtMs(Date.now())
      message.success(t('spAdmin.images.retryQueued'))
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.images.retryFailed')))
    } finally {
      setRetryingImages(false)
    }
  }

  const handleRecompute = async () => {
    setRecomputing(true)
    try {
      const next = await recomputeSupplierPreorderHistory(formGuid)
      setDetail(next)
      setLoadedAtMs(Date.now())
      message.success(t('spAdmin.history.recomputeQueued'))
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.history.recomputeFailed')))
    } finally {
      setRecomputing(false)
    }
  }

  const openEdit = () => {
    editForm.setFieldsValue(buildHeaderValuesFromDetail(detail))
    setEditOpen(true)
  }

  const handleEditSave = async () => {
    try {
      await editForm.validateFields()
    } catch {
      return
    }
    // 取整个表单存储：选了 40GP 预设时柜型字段没渲染，也要带上
    const values = editForm.getFieldsValue(true) as FormHeaderValues
    setEditSaving(true)
    try {
      const next = await updateSupplierPreorderForm(formGuid, toHeaderPayload(values))
      setDetail(next)
      setLoadedAtMs(Date.now())
      setEditOpen(false)
      message.success(t('spAdmin.common.saved'))
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.common.saveFailed')))
    } finally {
      setEditSaving(false)
    }
  }

  const statusButton =
    summary.status === 'Draft' ? (
      <Button type="primary" loading={statusSaving} onClick={handlePublish}>
        {t('spAdmin.detail.publish')}
      </Button>
    ) : summary.status === 'Open' ? (
      <Button type="primary" loading={statusSaving} onClick={handleClose}>
        {t('spAdmin.detail.close')}
      </Button>
    ) : (
      <Button loading={statusSaving} onClick={handleReopen}>
        {t('spAdmin.detail.reopen')}
      </Button>
    )

  const metaParts = [
    `${summary.localSupplierCode} · ${summary.supplierName}`,
    summary.quoteNo ? t('spAdmin.detail.metaQuote', { quoteNo: summary.quoteNo }) : null,
    normalizeDateOnly(detail.offerDate) ? t('spAdmin.detail.metaOfferDate', { date: normalizeDateOnly(detail.offerDate) }) : null,
    detail.containerType
      ? t('spAdmin.detail.metaContainer', {
          container: detail.containerType,
          capacity: detail.containerCapacityCbm ? ` ≈ ${formatCbm(detail.containerCapacityCbm)} m³` : '',
        })
      : null,
    detail.priceTerms || null,
    summary.deadlineAtUtc ? t('spAdmin.detail.metaDeadline', { time: formatSydneyDateTime(summary.deadlineAtUtc) }) : null,
  ].filter(Boolean)

  return (
    <div className="page-container sp-admin-page">
      <div className="sp-admin-card sp-admin-card-pad sp-admin-detail-head">
        <div className="sp-admin-detail-title-block">
          <div className="sp-admin-detail-title-line">
            <Button type="text" size="small" icon={<ArrowLeftOutlined />} aria-label={t('spAdmin.detail.backToList')} onClick={() => navigate('/supplier-preorders')} />
            <h2 className="sp-admin-detail-title">{summary.title}</h2>
            <FormStatusPill status={summary.status} />
            {summary.status !== 'Closed' ? <DeadlineText deadlineAtUtc={summary.deadlineAtUtc} nowMs={loadedAtMs} showDate={false} /> : null}
          </div>
          <div className="sp-admin-muted sp-admin-small sp-admin-number">
            {t('spAdmin.detail.stats', {
              items: formatInteger(summary.itemCount),
              enabled: summary.enabledStoreCount,
              submitted: summary.submittedStoreCount,
              draft: summary.draftStoreCount,
              notStarted,
            })}
            {' · '}
            {t('spAdmin.detail.totals', {
              cartons: formatInteger(summary.totalCartons),
              amount: formatMoney(summary.totalAmount),
              cbm: formatCbm(summary.totalCbm),
            })}
          </div>
          <div className="sp-admin-muted sp-admin-small">{metaParts.join(' · ')}</div>
        </div>
        <div className="sp-admin-actions">
          <Button icon={<TeamOutlined />} onClick={() => navigate('/supplier-preorders/orderers')}>
            {t('spAdmin.list.orderers')}
          </Button>
          <Button icon={<EditOutlined />} onClick={openEdit}>
            {t('spAdmin.detail.edit')}
          </Button>
          <Dropdown
            trigger={['click']}
            menu={{
              items: [
                {
                  key: 'original',
                  disabled: !summary.hasSourceFile,
                  label: summary.hasSourceFile ? (
                    t('spAdmin.detail.exportOriginal')
                  ) : (
                    <Tooltip title={t('spAdmin.detail.exportOriginalNoFile')} placement="left">
                      <span>{t('spAdmin.detail.exportOriginal')}</span>
                    </Tooltip>
                  ),
                },
                { key: 'generic', label: t('spAdmin.detail.exportGeneric') },
              ],
              onClick: ({ key }) => void handleExport(key as 'original' | 'generic'),
            }}
          >
            <Button icon={<DownloadOutlined />} loading={exporting}>
              {t('spAdmin.detail.export')} <DownOutlined />
            </Button>
          </Dropdown>
          {statusButton}
          <Tooltip title={deletable ? undefined : t('spAdmin.detail.deleteDisabled')}>
            <Button danger type="text" icon={<DeleteOutlined />} disabled={!deletable} onClick={handleDelete} aria-label={t('spAdmin.detail.delete')} />
          </Tooltip>
        </div>
      </div>

      <HistoryStatusBar detail={detail} recomputing={recomputing} onRecompute={() => void handleRecompute()} />
      <ImageCopyBar detail={detail} retrying={retryingImages} onRetry={() => void handleRetryImages()} />

      {summary.status === 'Draft' ? <Alert className="sp-admin-page-alert" type="info" showIcon message={t('spAdmin.detail.draftHint')} /> : null}
      {summary.status === 'Closed' ? <Alert className="sp-admin-page-alert" type="warning" showIcon message={t('spAdmin.detail.closedHint')} /> : null}

      <Tabs
        className="sp-admin-tabs"
        activeKey={activeTab}
        onChange={(key) => setActiveTab(key as DetailTab)}
        items={[
          {
            key: 'stores',
            label: t('spAdmin.detail.tabStores'),
            children: (
              <div className="sp-admin-stack">
                <FormStoresPanel formGuid={formGuid} onChanged={handleStoresChanged} />
                <FormMatrixPanel formGuid={formGuid} refreshKey={matrixRefreshKey} />
              </div>
            ),
          },
          { key: 'items', label: t('spAdmin.detail.tabItems'), children: <FormItemsTab formGuid={formGuid} detail={detail} onRefChanged={handleRefChanged} /> },
          {
            key: 'logs',
            label: t('spAdmin.detail.tabLogs'),
            children: (
              <div className="sp-admin-card">
                <AuthLogsTable formGuid={formGuid} refreshKey={logsRefreshKey} />
              </div>
            ),
          },
        ]}
      />

      <Drawer
        title={t('spAdmin.detail.editTitle')}
        open={editOpen}
        width={720}
        forceRender
        onClose={() => setEditOpen(false)}
        extra={
          <div className="sp-admin-actions">
            <Button onClick={() => setEditOpen(false)}>{t('spAdmin.common.cancel')}</Button>
            <Button type="primary" loading={editSaving} onClick={() => void handleEditSave()}>
              {t('spAdmin.common.save')}
            </Button>
          </div>
        }
      >
        <Form<FormHeaderValues> form={editForm} layout="vertical" className="sp-admin-header-form">
          <FormHeaderFields form={editForm} supplierName={summary.supplierName} showExportQtyUnit compact />
        </Form>
      </Drawer>
    </div>
  )
}

/** 新品图片转存 COS：已转存 / 不需要 / 处理中 / 失败（可重试）。没有原表图片地址时不显示。 */
function ImageCopyBar({ detail, retrying, onRetry }: { detail: SupplierPreorderFormDetail; retrying: boolean; onRetry: () => void }) {
  const { t } = useTranslation()
  const copy = detail.imageCopy
  if (!copy || copy.total === 0) return null
  return (
    <div className="sp-admin-card sp-admin-history-bar">
      <span className="sp-admin-strong">{t('spAdmin.images.title')}</span>
      {copy.configured ? null : (
        <Tag color="warning" className="sp-admin-tag">
          {t('spAdmin.images.notConfigured')}
        </Tag>
      )}
      <span className="sp-admin-history-counts sp-admin-small sp-admin-number">
        <span>{t('spAdmin.images.done', { count: copy.done })}</span>
        <span>{t('spAdmin.images.skipped', { count: copy.skipped })}</span>
        <span>{t('spAdmin.images.pending', { count: copy.pending })}</span>
        <span className={copy.failed ? 'sp-admin-history-need' : undefined}>{t('spAdmin.images.failed', { count: copy.failed })}</span>
      </span>
      <span className="sp-admin-toolbar-spacer" />
      {copy.failed > 0 ? (
        <Tooltip title={t('spAdmin.images.retryHint')}>
          <Button size="small" icon={<ReloadOutlined />} loading={retrying} onClick={onRetry}>
            {t('spAdmin.images.retry')}
          </Button>
        </Tooltip>
      ) : null}
    </div>
  )
}

/** 去年历史：窗口、计算状态、参考商品计数与「重新计算」。 */
function HistoryStatusBar({
  detail,
  recomputing,
  onRecompute,
}: {
  detail: SupplierPreorderFormDetail
  recomputing: boolean
  onRecompute: () => void
}) {
  const { t } = useTranslation()
  const history = detail.history
  const status = history?.status ?? 'None'
  const range = getHistoryWindow(history, detail.historyFrom, detail.historyTo)

  const statusTag = (() => {
    switch (status) {
      case 'Queued':
        return <Tag className="sp-admin-tag">{t('spAdmin.history.status.Queued')}</Tag>
      case 'Running':
        return (
          <Tag color="processing" icon={<SyncOutlined spin />} className="sp-admin-tag">
            {t('spAdmin.history.status.Running')}
          </Tag>
        )
      case 'Ready':
        return (
          <Tag color="success" className="sp-admin-tag sp-admin-number">
            {t('spAdmin.history.status.Ready', { time: formatSydneyDateTime(history?.computedAtUtc) })}
          </Tag>
        )
      case 'Failed':
        return (
          <Tooltip title={history?.error || undefined}>
            <Tag color="error" className="sp-admin-tag sp-admin-history-error">
              {t('spAdmin.history.status.Failed', { error: history?.error || t('spAdmin.history.unknownError') })}
            </Tag>
          </Tooltip>
        )
      case 'NoWindow':
        return (
          <Tag color="warning" className="sp-admin-tag">
            {t('spAdmin.history.status.NoWindow')}
          </Tag>
        )
      default:
        return <Tag className="sp-admin-tag">{t('spAdmin.history.status.None')}</Tag>
    }
  })()

  const busy = !canRecomputeHistory(status)

  return (
    <div className="sp-admin-card sp-admin-history-bar">
      <span className="sp-admin-strong">{t('spAdmin.history.title')}</span>
      <span className="sp-admin-number sp-admin-small">
        {range ? t('spAdmin.history.window', { from: range.from, to: range.to }) : <span className="sp-admin-muted">{t('spAdmin.history.noWindow')}</span>}
      </span>
      {statusTag}
      <span className="sp-admin-history-counts sp-admin-small sp-admin-number">
        <span>{t('spAdmin.history.ownCount', { count: detail.ownHistoryCount ?? 0 })}</span>
        <span>{t('spAdmin.history.refAutoCount', { count: detail.refAutoCount ?? 0 })}</span>
        <span>{t('spAdmin.history.refManualCount', { count: detail.refManualCount ?? 0 })}</span>
        <span className={detail.needRefCount ? 'sp-admin-history-need' : undefined}>
          {t('spAdmin.history.needRefCount', { count: detail.needRefCount ?? 0 })}
        </span>
      </span>
      <span className="sp-admin-toolbar-spacer" />
      <Tooltip title={busy ? t('spAdmin.history.recomputeBusy') : t('spAdmin.history.recomputeHint')}>
        <Button size="small" icon={<ReloadOutlined />} loading={recomputing} disabled={busy} onClick={onRecompute}>
          {t('spAdmin.history.recompute')}
        </Button>
      </Tooltip>
    </div>
  )
}
