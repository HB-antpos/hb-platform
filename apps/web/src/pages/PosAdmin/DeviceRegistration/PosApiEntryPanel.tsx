import { InfoCircleFilled, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Button, Input, Modal, Select, Tooltip, message, type CheckboxProps } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { TFunction } from 'i18next'
import { Fragment, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { getPosApiEntryStores, updatePosApiEntryStores } from '../../../services/posApiEntryService'
import type { PosApiEntryMode, PosApiEntryStoreItem } from '../../../types/posApiEntry'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'

import { EMPTY_VALUE } from './deviceCells'
import {
  FallbackOrder,
  MODE_LABEL_KEY,
  handleRadioArrowKey,
} from './posApiEntryCells'
import {
  POS_API_ENTRY_HOSTS,
  POS_API_ENTRY_MODES,
  filterStores,
  getEffectiveMode,
  getEntrySegments,
  getFallbackOrder,
  getStoreAttention,
  parseEntryTimestamp,
  previewStoreNames,
  summarizeStores,
  summarizeTargets,
  type EntrySegment,
} from './posApiEntryLogic'

const POLL_INTERVAL_MS = 30_000
/** 后端 PosApiEntryService.MaxStoresPerRequest：一次最多改 200 家，整批原子（超出直接 400）。 */
const MAX_STORES_PER_UPDATE = 200
const PAGE_SIZE = 20

// 文案键写成字面量，页面文案契约测试才能扫描到并校验中英文都存在
const MODE_DESC_KEY: Record<PosApiEntryMode, string> = {
  edge: 'posAdmin.devices.apiEntry.modeDescriptions.edge',
  aws: 'posAdmin.devices.apiEntry.modeDescriptions.aws',
  direct: 'posAdmin.devices.apiEntry.modeDescriptions.direct',
}
const BATCH_LABEL_KEY: Record<PosApiEntryMode, string> = {
  edge: 'posAdmin.devices.apiEntry.batch.setEdge',
  aws: 'posAdmin.devices.apiEntry.batch.setAws',
  direct: 'posAdmin.devices.apiEntry.batch.setDirect',
}
const ACTUAL_ON_KEY = 'posAdmin.devices.apiEntry.summary.actualOn'

/** 把文案里的 <b>…</b> 渲染成加粗，不使用 dangerouslySetInnerHTML。 */
function renderRichText(text: string): ReactNode {
  return text.split(/<b>(.*?)<\/b>/g).map((part, index) =>
    index % 2 === 1 ? <strong key={index}>{part}</strong> : <Fragment key={index}>{part}</Fragment>,
  )
}

/** 「今天 10:42」/「10-23 16:05」：按浏览器本地时区显示。 */
function formatUpdatedAt(value: string | null, t: TFunction, now = Date.now()): string | null {
  const timestamp = parseEntryTimestamp(value)
  if (timestamp === null) {
    return null
  }
  const date = new Date(timestamp)
  const pad = (n: number) => String(n).padStart(2, '0')
  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`
  const today = new Date(now)
  if (date.toDateString() === today.toDateString()) {
    return t('posAdmin.devices.apiEntry.updatedToday', { time })
  }
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${time}`
}

interface RouteSwitchProps {
  store: PosApiEntryStoreItem
  disabled: boolean
  onSelect: (mode: PosApiEntryMode) => void
}

/** 行内首选线路三段切换：点击不直接保存，而是走与批量相同的确认流程。 */
function RouteSwitch({ store, disabled, onSelect }: RouteSwitchProps) {
  const { t } = useTranslation()
  const current = getEffectiveMode(store.effectiveMode)
  return (
    <span
      role="radiogroup"
      aria-label={t('posAdmin.devices.apiEntry.radioGroupAria', { store: store.storeName || store.storeCode })}
      className="dev-entry-switch"
      onKeyDown={handleRadioArrowKey}
    >
      {POS_API_ENTRY_MODES.map((mode) => (
        <button
          key={mode}
          type="button"
          role="radio"
          aria-checked={current === mode}
          aria-disabled={disabled || undefined}
          disabled={disabled}
          className={`dev-entry-switch-option dev-entry-switch-option-${mode}${current === mode ? ' dev-entry-switch-option-on' : ''}`}
          onClick={() => onSelect(mode)}
        >
          {t(MODE_LABEL_KEY[mode])}
        </button>
      ))}
    </span>
  )
}

/** 设备实际线路分布条 + 图例。 */
function DistributionCell({ store }: { store: PosApiEntryStoreItem }) {
  const { t } = useTranslation()
  const segments = getEntrySegments(store)
  if (segments.length === 0) {
    return <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
  }
  const total = segments.reduce((sum, segment) => sum + segment.count, 0)
  const describe = (segment: EntrySegment) => {
    const count = segment.count
    if (segment.kind === 'preferred') {
      return t('posAdmin.devices.apiEntry.legend.preferred', { route: t(MODE_LABEL_KEY[segment.entry!]), count })
    }
    if (segment.kind === 'fallback') {
      return t('posAdmin.devices.apiEntry.legend.fallback', { route: t(MODE_LABEL_KEY[segment.entry!]), count })
    }
    return t(
      segment.kind === 'unsupported'
        ? 'posAdmin.devices.apiEntry.legend.unsupported'
        : 'posAdmin.devices.apiEntry.legend.unknown',
      { count },
    )
  }
  const segmentClass = (segment: EntrySegment) =>
    segment.kind === 'preferred' ? segment.entry : segment.kind === 'fallback' ? 'fallback' : 'neutral'
  return (
    <div
      className="dev-entry-dist"
      role="img"
      aria-label={t('posAdmin.devices.apiEntry.distributionAria', {
        store: store.storeName || store.storeCode,
        summary: segments.map(describe).join('，'),
      })}
    >
      <div className="dev-entry-bar" aria-hidden="true">
        {segments.map((segment) => (
          <i
            key={`${segment.kind}-${segment.entry ?? ''}`}
            className={`dev-entry-bar-${segmentClass(segment)}`}
            style={{ width: `${(segment.count / total) * 100}%` }}
          />
        ))}
      </div>
      <div className="dev-entry-legend" aria-hidden="true">
        {segments.map((segment) => (
          <span key={`${segment.kind}-${segment.entry ?? ''}`} className="dev-entry-legend-item">
            <span className={`dev-entry-dot dev-entry-dot-${segmentClass(segment)}`} />
            {describe(segment)}
          </span>
        ))}
      </div>
    </div>
  )
}

interface ConfirmState {
  storeGuids: string[]
  mode: PosApiEntryMode
}

interface PosApiEntryPanelProps {
  /** 有设备管理的 Manage 权限才能修改；只读账号只能看。 */
  canManage: boolean
}

export default function PosApiEntryPanel({ canManage }: PosApiEntryPanelProps) {
  const { t } = useTranslation()
  const [items, setItems] = useState<PosApiEntryStoreItem[]>([])
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [modeFilter, setModeFilter] = useState<PosApiEntryMode>()
  const [onlyFallback, setOnlyFallback] = useState(false)
  const [selectedGuids, setSelectedGuids] = useState<string[]>([])
  const [confirm, setConfirm] = useState<ConfirmState | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [pagination, setPagination] = useState({ current: 1, pageSize: PAGE_SIZE })
  const requestGuardRef = useRef(createLatestRequestGuard())

  function load(showLoading = true) {
    return runLatestGuardedRequest(requestGuardRef.current, () => getPosApiEntryStores(), {
      onStart: () => {
        if (showLoading) {
          setLoading(true)
        }
      },
      onSuccess: setItems,
      onError: (error) => {
        console.error(t('posAdmin.devices.apiEntry.messages.loadFailed'), error)
        // 后台轮询失败不弹窗，避免网络抖动时每 30 秒刷一条错误
        if (showLoading) {
          message.error(t('posAdmin.devices.apiEntry.messages.loadFailed'))
        }
      },
      onSettled: () => setLoading(false),
    })
  }

  useEffect(() => {
    void load()
    const intervalId = window.setInterval(() => void load(false), POLL_INTERVAL_MS)
    return () => {
      window.clearInterval(intervalId)
      requestGuardRef.current.invalidate()
    }
  }, [])

  // 筛选变化回到第一页并清空勾选，避免批量操作作用到已被筛掉、当前看不见的分店
  useEffect(() => {
    setPagination((current) => (current.current === 1 ? current : { ...current, current: 1 }))
    setSelectedGuids((current) => (current.length ? [] : current))
  }, [keyword, modeFilter, onlyFallback])

  // 轮询刷新后剔除已不存在的勾选
  useEffect(() => {
    setSelectedGuids((current) => {
      if (!current.length) {
        return current
      }
      const known = new Set(items.map((item) => item.storeGuid))
      const next = current.filter((guid) => known.has(guid))
      return next.length === current.length ? current : next
    })
  }, [items])

  const summary = useMemo(() => summarizeStores(items), [items])
  const visibleItems = useMemo(
    () => filterStores(items, { keyword, mode: modeFilter, onlyFallback }),
    [items, keyword, modeFilter, onlyFallback],
  )
  const hasFilter = Boolean(keyword.trim() || modeFilter || onlyFallback)

  function openConfirm(storeGuids: string[], mode: PosApiEntryMode) {
    if (!canManage || storeGuids.length === 0 || submitting) {
      return
    }
    if (storeGuids.length > MAX_STORES_PER_UPDATE) {
      message.warning(t('posAdmin.devices.apiEntry.messages.tooMany', { max: MAX_STORES_PER_UPDATE }))
      return
    }
    setConfirm({ storeGuids, mode })
  }

  // 弹窗展示的是打开时选中的分店，但台数取最新一次拉取的数据（轮询期间设备数可能变化）
  const confirmTargets = useMemo(() => {
    if (!confirm) {
      return []
    }
    const guids = new Set(confirm.storeGuids)
    return items.filter((item) => guids.has(item.storeGuid))
  }, [confirm, items])

  async function submitConfirm() {
    if (!confirm || submitting) {
      return
    }
    setSubmitting(true)
    try {
      const result = await updatePosApiEntryStores(confirm.storeGuids, confirm.mode)
      message.success(
        t('posAdmin.devices.apiEntry.messages.updateSuccess', { updated: result.updated }) +
          (result.skipped > 0 ? t('posAdmin.devices.apiEntry.messages.updateSkipped', { skipped: result.skipped }) : ''),
      )
      setConfirm(null)
      setSelectedGuids([])
      await load(false)
    } catch (error) {
      console.error(t('posAdmin.devices.apiEntry.messages.updateFailed'), error)
      message.error(t('posAdmin.devices.apiEntry.messages.updateFailed'))
    } finally {
      setSubmitting(false)
    }
  }

  const columns = useMemo<ColumnsType<PosApiEntryStoreItem>>(
    () => [
      {
        title: t('posAdmin.devices.apiEntry.columns.store'),
        key: 'store',
        width: 190,
        render: (_value, store) => (
          <div className="dev-mgmt-two">
            <span className="dev-mgmt-strong dev-mgmt-ellipsis" title={store.storeName || store.storeCode}>
              {store.storeName || store.storeCode}
            </span>
            <span className="dev-mgmt-sub dev-mgmt-ellipsis">
              {store.storeCode ? <span className="dev-mgmt-mono">{store.storeCode}</span> : null}
              {store.storeCode ? ' · ' : ''}
              {t('posAdmin.devices.apiEntry.storeSub', { total: store.devices.total, online: store.devices.online })}
            </span>
          </div>
        ),
      },
      {
        title: (
          <span>
            {t('posAdmin.devices.apiEntry.columns.preferred')}
            <span className="dev-entry-th-hint"> · {t('posAdmin.devices.apiEntry.columns.preferredHint')}</span>
          </span>
        ),
        key: 'preferred',
        width: 300,
        render: (_value, store) => (
          <div className="dev-entry-preferred">
            <div className="dev-entry-preferred-line">
              <RouteSwitch store={store} disabled={!canManage} onSelect={(mode) => {
                // 点的就是当前首选：没有变化，不弹确认框
                if (mode !== getEffectiveMode(store.effectiveMode)) {
                  openConfirm([store.storeGuid], mode)
                }
              }} />
              {store.mode === null ? (
                <span className="dev-entry-default-tag">{t('posAdmin.devices.apiEntry.defaultTag')}</span>
              ) : null}
            </div>
            <FallbackOrder order={getFallbackOrder(store.effectiveMode)} t={t} />
          </div>
        ),
      },
      {
        title: t('posAdmin.devices.apiEntry.columns.actual'),
        key: 'actual',
        width: 270,
        render: (_value, store) => <DistributionCell store={store} />,
      },
      {
        title: t('posAdmin.devices.apiEntry.columns.attention'),
        key: 'attention',
        width: 190,
        render: (_value, store) => {
          const attention = getStoreAttention(store, Date.now())
          if (attention.length === 0) {
            return <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
          }
          return (
            <div className="dev-entry-attention">
              {attention.map((entry) => (
                <span key={entry.kind} className={`dev-entry-chip dev-entry-chip-${entry.kind}`}>
                  {entry.kind === 'fallback'
                    ? t('posAdmin.devices.apiEntry.attention.fallback', { count: entry.count })
                    : entry.kind === 'unsupported'
                      ? t('posAdmin.devices.apiEntry.attention.unsupported', { count: entry.count })
                      : entry.kind === 'offline'
                        ? t('posAdmin.devices.apiEntry.attention.offline', { count: entry.count })
                        : t('posAdmin.devices.apiEntry.attention.pending')}
                </span>
              ))}
            </div>
          )
        },
      },
      {
        title: t('posAdmin.devices.apiEntry.columns.updated'),
        key: 'updated',
        width: 160,
        render: (_value, store) => {
          const text = formatUpdatedAt(store.updatedAt, t)
          return text ? (
            <span className="dev-mgmt-sub">
              {text}
              {store.updatedBy ? ` · ${store.updatedBy}` : ''}
            </span>
          ) : (
            <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
          )
        },
      },
    ],
    [canManage, submitting, t],
  )

  const confirmSummary = summarizeTargets(confirmTargets)
  const namePreview = previewStoreNames(confirmTargets)

  return (
    <div className="dev-mgmt-card dev-entry-scope">
      <div className="dev-entry-note" role="note">
        <InfoCircleFilled className="dev-entry-note-icon" aria-hidden="true" />
        <div>{renderRichText(t('posAdmin.devices.apiEntry.intro'))}</div>
      </div>

      <div className="dev-entry-stats">
        <div className="dev-entry-stat dev-entry-stat-preferred">
          <div className="dev-entry-stat-label">{t('posAdmin.devices.apiEntry.summary.preferred')}</div>
          <div className="dev-entry-stat-split">
            {POS_API_ENTRY_MODES.map((mode) => (
              <div key={mode} className="dev-entry-stat-value">
                {summary.preferred[mode]}
                <small>{t(MODE_LABEL_KEY[mode])}</small>
              </div>
            ))}
          </div>
        </div>
        {POS_API_ENTRY_MODES.map((mode) => (
          <div key={mode} className="dev-entry-stat">
            <div className="dev-entry-stat-label">
              <span className={`dev-entry-dot dev-entry-dot-${mode}`} />
              {t(ACTUAL_ON_KEY, { route: t(MODE_LABEL_KEY[mode]) })}
            </div>
            <div className="dev-entry-stat-value">
              {summary.actual[mode]}
              <small>{t('posAdmin.devices.apiEntry.summary.deviceUnit')}</small>
            </div>
            {summary.fallbackOn[mode] > 0 ? (
              <div className="dev-entry-stat-hint">
                {t('posAdmin.devices.apiEntry.summary.fallbackHint', { count: summary.fallbackOn[mode] })}
              </div>
            ) : null}
          </div>
        ))}
        <div className="dev-entry-stat">
          <div className="dev-entry-stat-label">
            <span className="dev-entry-dot dev-entry-dot-neutral" />
            {t('posAdmin.devices.apiEntry.summary.unsupported')}
          </div>
          <div className="dev-entry-stat-value">
            {summary.unsupportedTotal}
            <small>{t('posAdmin.devices.apiEntry.summary.deviceUnit')}</small>
          </div>
          <div className="dev-entry-stat-hint">{t('posAdmin.devices.apiEntry.summary.unsupportedHint')}</div>
        </div>
      </div>

      <div className="dev-mgmt-toolbar">
        <Input
          allowClear
          prefix={<SearchOutlined className="dev-mgmt-faint" />}
          placeholder={t('posAdmin.devices.apiEntry.toolbar.searchPlaceholder')}
          aria-label={t('posAdmin.devices.apiEntry.toolbar.searchPlaceholder')}
          style={{ width: 260 }}
          value={keyword}
          onChange={(event) => setKeyword(event.target.value)}
        />
        <Select<PosApiEntryMode>
          allowClear
          placeholder={t('posAdmin.devices.apiEntry.toolbar.preferredPlaceholder')}
          aria-label={t('posAdmin.devices.apiEntry.toolbar.preferredPlaceholder')}
          style={{ width: 170 }}
          value={modeFilter}
          onChange={(value) => setModeFilter(value)}
          options={POS_API_ENTRY_MODES.map((mode) => ({ value: mode, label: t(MODE_LABEL_KEY[mode]) }))}
        />
        <Select<'fallback'>
          allowClear
          placeholder={t('posAdmin.devices.apiEntry.toolbar.scopePlaceholder')}
          aria-label={t('posAdmin.devices.apiEntry.toolbar.scopePlaceholder')}
          style={{ width: 190 }}
          value={onlyFallback ? 'fallback' : undefined}
          onChange={(value) => setOnlyFallback(value === 'fallback')}
          options={[{ value: 'fallback', label: t('posAdmin.devices.apiEntry.toolbar.onlyFallback') }]}
        />
        <span className="dev-mgmt-toolbar-spacer" />
        <Tooltip title={t('common.refresh')}>
          <Button
            icon={<ReloadOutlined spin={loading} />}
            aria-label={t('common.refresh')}
            onClick={() => void load()}
          />
        </Tooltip>
      </div>

      {!canManage ? <div className="dev-mgmt-note">{t('posAdmin.devices.apiEntry.readOnly')}</div> : null}

      {canManage ? (
        <div className="dev-mgmt-bars">
          {/* 勾选后才出现；三个按钮对应三条线路，点击后进入统一的确认弹窗 */}
          <SelectionActionBar selectedCount={selectedGuids.length} onClearSelection={() => setSelectedGuids([])}>
            {POS_API_ENTRY_MODES.map((mode) => (
              <Button key={mode} size="small" onClick={() => openConfirm(selectedGuids, mode)}>
                {t(BATCH_LABEL_KEY[mode])}
              </Button>
            ))}
          </SelectionActionBar>
        </div>
      ) : null}

      <MeasuredTable<PosApiEntryStoreItem>
        metricId="pos-admin.device-registration.api-entry"
        className="dev-mgmt-table dev-entry-table"
        rowKey="storeGuid"
        size="middle"
        loading={loading}
        columns={columns}
        dataSource={visibleItems}
        rowSelection={canManage ? {
          selectedRowKeys: selectedGuids,
          preserveSelectedRowKeys: true,
          columnWidth: 44,
          // CheckboxProps 类型里没声明 aria-label，但 antd 会把 aria-* 透传到 input，读屏用它报「选择 xxx」
          getCheckboxProps: (record) =>
            ({
              'aria-label': t('posAdmin.devices.apiEntry.selectRowAria', { store: record.storeName || record.storeCode }),
            }) as Partial<CheckboxProps>,
          onChange: (keys) => setSelectedGuids(keys.map(String)),
        } : undefined}
        // 窄屏：表格自身横向滚动，页面不出现横向滚动条
        scroll={{ x: canManage ? 1160 : 1110 }}
        locale={{
          emptyText: hasFilter ? (
            <div className="dev-mgmt-sub" style={{ padding: '24px 0' }}>
              {t('posAdmin.devices.apiEntry.emptyFiltered')}
            </div>
          ) : undefined,
        }}
        pagination={{
          current: pagination.current,
          pageSize: pagination.pageSize,
          hideOnSinglePage: true,
          showSizeChanger: false,
          onChange: (current, pageSize) => setPagination({ current, pageSize }),
        }}
      />
      <div className="dev-entry-footer">{t('posAdmin.devices.apiEntry.footer', { count: items.length })}</div>

      <Modal
        open={confirm !== null}
        rootClassName="dev-entry-modal-root"
        width={560}
        centered
        destroyOnHidden
        maskClosable={!submitting}
        keyboard={!submitting}
        title={t('posAdmin.devices.apiEntry.modal.title', { count: confirmTargets.length })}
        onCancel={() => (submitting ? undefined : setConfirm(null))}
        footer={
          <div className="dev-entry-modal-actions">
            <Button disabled={submitting} onClick={() => setConfirm(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="primary"
              loading={submitting}
              disabled={confirmTargets.length === 0}
              className={`dev-entry-ok dev-entry-ok-${confirm?.mode ?? 'edge'}`}
              onClick={() => void submitConfirm()}
            >
              {t('posAdmin.devices.apiEntry.modal.confirm')}
            </Button>
          </div>
        }
      >
        {confirm ? (
          <div className="dev-entry-modal-body">
            <div className="dev-mgmt-sub dev-entry-modal-subtitle">
              {t('posAdmin.devices.apiEntry.modal.subtitle', {
                names:
                  namePreview.names.join('、') +
                  (namePreview.rest > 0 ? t('posAdmin.devices.apiEntry.modal.namesMore', { count: namePreview.rest }) : ''),
                devices: confirmSummary.total,
              })}
            </div>
            <div
              role="radiogroup"
              aria-label={t('posAdmin.devices.apiEntry.modal.choiceAria')}
              onKeyDown={(event) => handleRadioArrowKey(event, {
                select: (mode) => setConfirm((current) => (current ? { ...current, mode } : current)),
              })}
            >
              {POS_API_ENTRY_MODES.map((mode) => (
                <button
                  key={mode}
                  type="button"
                  role="radio"
                  aria-checked={confirm.mode === mode}
                  tabIndex={confirm.mode === mode ? 0 : -1}
                  className={`dev-entry-choice dev-entry-choice-${mode}${confirm.mode === mode ? ' dev-entry-choice-on' : ''}`}
                  onClick={() => setConfirm({ ...confirm, mode })}
                >
                  <span className="dev-entry-choice-radio" aria-hidden="true" />
                  <span className="dev-entry-choice-main">
                    <span className="dev-entry-choice-title">
                      {t(MODE_LABEL_KEY[mode])}
                      {mode === 'edge' ? t('posAdmin.devices.apiEntry.modal.defaultSuffix') : ''}
                    </span>
                    <span className="dev-entry-choice-host">{POS_API_ENTRY_HOSTS[mode]}</span>
                    <FallbackOrder order={getFallbackOrder(mode)} t={t} />
                    <span className="dev-entry-choice-desc">{t(MODE_DESC_KEY[mode])}</span>
                  </span>
                </button>
              ))}
            </div>
            <div className="dev-entry-impact">
              <div className="dev-entry-impact-title">{t('posAdmin.devices.apiEntry.modal.effectTitle')}</div>
              <ul>
                <li>{t('posAdmin.devices.apiEntry.modal.effectSpeed')}</li>
                <li>{t('posAdmin.devices.apiEntry.modal.effectIdentity')}</li>
                {confirmSummary.unsupported > 0 ? (
                  <li className="dev-entry-impact-warn">
                    {t('posAdmin.devices.apiEntry.modal.effectPartlyUnsupported', {
                      supported: confirmSummary.supported,
                      count: confirmSummary.unsupported,
                    })}
                  </li>
                ) : (
                  <li>{t('posAdmin.devices.apiEntry.modal.effectAllSupported', { count: confirmSummary.total })}</li>
                )}
              </ul>
            </div>
          </div>
        ) : null}
      </Modal>
    </div>
  )
}
