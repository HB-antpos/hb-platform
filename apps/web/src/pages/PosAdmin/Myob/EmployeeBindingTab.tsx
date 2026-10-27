import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Modal, Segmented, Skeleton, Tag, message } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import {
  confirmMyobBindings,
  getMyobBindingSnapshot,
  ignoreMyobEmployee,
  unbindMyobEmployee,
} from '../../../services/myobService'
import type {
  MyobBindingCounts,
  MyobBindingRow,
  MyobBindingSnapshot,
  MyobConfirmBindingItem,
  MyobConfirmBindingsResult,
  MyobCompanyOption,
  MyobOnlyInMyob,
  MyobOnlyInOurs,
  MyobTypeIssue,
} from '../../../types/myob'
import { copyTextToClipboard } from '../../../utils/clipboard'
import BindingDrawer from './BindingDrawer'
import BindingListView from './BindingListView'
import { MissingPanel, TypeIssuesPanel } from './MissingPanels'
import {
  buildBatchConfirmItems,
  buildSingleConfirmItem,
  describeBindFailures,
  getPreselectedCardUid,
  type BindingFilter,
  type BindingRowAction,
} from './myobEmployeeLogic'
import { extractMyobError, formatAbnDisplay, formatMyobTime } from './myobLogic'

type SubTab = 'list' | 'missing' | 'typeIssues'

interface EmployeeBindingTabProps {
  /** 可选公司文件（GET /myob/companies，只含 Active）；不依赖连接管理权限。 */
  companies: MyobCompanyOption[]
  companiesLoading: boolean
  companiesError: string | null
  /** 有 Myob.Connection.Manage 才显示「连接其他公司文件」。 */
  canConnect: boolean
  authorizing: boolean
  onAuthorize: () => void
  onReloadCompanies: () => void
  /** 由「发薪公司」抽屉的「去绑定」带来的预选公司；每次请求带递增序号，同一公司重复点击也会重新选中。 */
  focusRequest: { connectionGuid: string; seq: number } | null
  /** 把待处理计数回传给页签角标（null = 还没有数据）。 */
  onCountsChange: (counts: MyobBindingCounts | null) => void
  /** 有「用户管理」访问权限才显示「我方建档」。 */
  canCreateOurSide: boolean
}

interface DrawerState {
  row: MyobBindingRow
  preselectUid: string | null
}

export default function EmployeeBindingTab({
  companies,
  companiesLoading,
  companiesError,
  canConnect,
  authorizing,
  onAuthorize,
  onReloadCompanies,
  focusRequest,
  onCountsChange,
  canCreateOurSide,
}: EmployeeBindingTabProps) {
  const { t } = useTranslation()
  // companies 只含已连接（Active）的公司文件；NeedsReauth / Disconnected 拉不到 MYOB 员工，后端不会返回
  const [pickedGuid, setPickedGuid] = useState<string | null>(null)
  const selectedGuid = useMemo(() => {
    if (pickedGuid && companies.some((item) => item.connectionGuid === pickedGuid)) {
      return pickedGuid
    }
    return companies[0]?.connectionGuid ?? null
  }, [companies, pickedGuid])
  const selected = companies.find((item) => item.connectionGuid === selectedGuid) ?? null

  const [snapshot, setSnapshot] = useState<MyobBindingSnapshot | null>(null)
  const [loading, setLoading] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [subTab, setSubTab] = useState<SubTab>('list')
  const [filter, setFilter] = useState<BindingFilter>('all')
  const [keyword, setKeyword] = useState('')
  const [busyUserGuids, setBusyUserGuids] = useState<ReadonlySet<string>>(new Set())
  const [batchBusy, setBatchBusy] = useState(false)
  const [ignoringUids, setIgnoringUids] = useState<ReadonlySet<string>>(new Set())
  const [drawer, setDrawer] = useState<DrawerState | null>(null)
  const requestSeq = useRef(0)

  // 去绑定：外部指定公司时切换选中
  useEffect(() => {
    if (focusRequest) {
      setPickedGuid(focusRequest.connectionGuid)
    }
  }, [focusRequest])

  const load = useCallback(
    async (refresh: boolean) => {
      if (!selectedGuid) {
        return
      }
      const seq = ++requestSeq.current
      setLoading(true)
      setRefreshing(refresh)
      try {
        const data = await getMyobBindingSnapshot(selectedGuid, refresh)
        if (seq === requestSeq.current) {
          setSnapshot(data)
          setError(null)
          if (refresh) {
            message.success(t('myob.employees.company.refreshed'))
          }
        }
      } catch (err) {
        if (seq === requestSeq.current) {
          const fallback = refresh ? t('myob.employees.company.refreshFailed') : t('myob.employees.loadFailed')
          const text = extractMyobError(err, fallback).message
          setError(text)
          if (refresh) {
            message.error(text)
          }
        }
      } finally {
        if (seq === requestSeq.current) {
          setLoading(false)
          setRefreshing(false)
        }
      }
    },
    [selectedGuid, t],
  )

  // 切换公司：清空旧数据与筛选，重新请求（不强制刷新 MYOB）
  useEffect(() => {
    setSnapshot(null)
    setError(null)
    setFilter('all')
    setKeyword('')
    setSubTab('list')
    setDrawer(null)
    if (selectedGuid) {
      void load(false)
    } else {
      requestSeq.current += 1
      setLoading(false)
    }
  }, [selectedGuid, load])

  useEffect(() => {
    onCountsChange(snapshot?.counts ?? null)
  }, [snapshot, onCountsChange])

  const rows = snapshot?.rows ?? []
  const connectionName = snapshot?.connectionName || selected?.name || ''

  const setBusy = (guids: string[], on: boolean) =>
    setBusyUserGuids((current) => {
      const next = new Set(current)
      for (const guid of guids) {
        if (on) next.add(guid)
        else next.delete(guid)
      }
      return next
    })

  const showFailures = (result: MyobConfirmBindingsResult) => {
    const lines = describeBindFailures(result.failed, rows)
    Modal.warning({
      title:
        result.confirmed > 0
          ? t('myob.employees.failures.title', { ok: result.confirmed, count: lines.length })
          : t('myob.employees.failures.titleAllFailed', { count: lines.length }),
      okText: t('myob.employees.failures.ok'),
      content: (
        <ul style={{ margin: 0, paddingLeft: 18 }}>
          {lines.map((line) => (
            <li key={line.userGuid}>
              {t('myob.employees.failures.line', {
                name: line.userName,
                reason: line.messageKey ? t(line.messageKey) : line.fallbackMessage,
              })}
            </li>
          ))}
        </ul>
      ),
    })
  }

  /** 提交确认；部分失败时逐条展示原因。返回服务端结果，请求本身失败返回 null。 */
  const runConfirm = async (items: MyobConfirmBindingItem[], successText: string): Promise<MyobConfirmBindingsResult | null> => {
    if (!selectedGuid || items.length === 0) {
      return null
    }
    const guids = items.map((item) => item.userGuid)
    setBusy(guids, true)
    try {
      const result = await confirmMyobBindings(selectedGuid, items)
      if (result.confirmed > 0) {
        message.success(successText)
      }
      if (result.failed.length > 0) {
        showFailures(result)
      }
      await load(false)
      return result
    } catch (err) {
      message.error(extractMyobError(err, t('myob.employees.actions.confirmFailed')).message)
      return null
    } finally {
      setBusy(guids, false)
    }
  }

  const handleUnbind = (row: MyobBindingRow) => {
    const linkGuid = row.link?.linkGuid
    if (!linkGuid) {
      return
    }
    Modal.confirm({
      title: t('myob.employees.actions.unbindTitle', { name: row.userName }),
      content: t('myob.employees.actions.unbindContent'),
      okText: t('myob.employees.actions.unbindOk'),
      cancelText: t('myob.employees.actions.cancel'),
      okButtonProps: { danger: true },
      onOk: async () => {
        setBusy([row.userGuid], true)
        try {
          await unbindMyobEmployee(linkGuid)
          message.success(t('myob.employees.actions.unbound', { name: row.userName }))
          await load(false)
        } catch (err) {
          message.error(extractMyobError(err, t('myob.employees.actions.unbindFailed')).message)
          throw err
        } finally {
          setBusy([row.userGuid], false)
        }
      },
    })
  }

  const handleRowAction = (action: BindingRowAction, row: MyobBindingRow) => {
    if (action === 'confirm') {
      const item = buildSingleConfirmItem(row)
      if (item) {
        void runConfirm([item], t('myob.employees.actions.confirmed', { name: row.userName }))
      }
      return
    }
    if (action === 'unbind') {
      handleUnbind(row)
      return
    }
    // 核对并绑定 / 换一个 / 搜索其他卡 / 搜索 MYOB 员工卡 → 手动选卡抽屉
    setDrawer({ row, preselectUid: getPreselectedCardUid(action, row) })
  }

  const handleBatchConfirm = () => {
    const items = buildBatchConfirmItems(rows)
    if (items.length === 0) {
      return
    }
    Modal.confirm({
      title: t('myob.employees.batch.confirmTitle', { count: items.length }),
      content: t('myob.employees.batch.confirmContent', { count: items.length }),
      okText: t('myob.employees.batch.confirmOk'),
      cancelText: t('myob.employees.actions.cancel'),
      onOk: async () => {
        setBatchBusy(true)
        try {
          await runConfirm(items, t('myob.employees.actions.batchConfirmed', { count: items.length }))
        } finally {
          setBatchBusy(false)
        }
      },
    })
  }

  const handleDrawerConfirm = async (_userGuid: string, items: MyobConfirmBindingItem[]) => {
    const name = drawer?.row.userName ?? ''
    const result = await runConfirm(items, t('myob.employees.actions.confirmed', { name }))
    if (result && result.failed.length === 0 && result.confirmed > 0) {
      setDrawer(null)
    }
    return result
  }

  const handleCopyOurs = (item: MyobOnlyInOurs) =>
    void copyTextToClipboard(item.copyText || [item.userName, item.email].filter(Boolean).join('\n'), {
      successMessage: t('myob.employees.missing.ours.copied', { name: item.userName }),
      failureMessage: t('myob.connections.copyFailed'),
    })

  const handleIgnore = async (item: MyobOnlyInMyob) => {
    if (!selectedGuid) {
      return
    }
    setIgnoringUids((current) => new Set(current).add(item.card.uid))
    try {
      await ignoreMyobEmployee({ connectionGuid: selectedGuid, myobEmployeeUid: item.card.uid, ignored: true })
      message.success(t('myob.employees.missing.theirs.ignored', { name: item.card.name }))
      await load(false)
    } catch (err) {
      message.error(extractMyobError(err, t('myob.employees.missing.theirs.ignoreFailed')).message)
    } finally {
      setIgnoringUids((current) => {
        const next = new Set(current)
        next.delete(item.card.uid)
        return next
      })
    }
  }

  const handleCopyIssue = (_issue: MyobTypeIssue, note: string) =>
    void copyTextToClipboard(note, {
      successMessage: t('myob.employees.typeIssues.noteCopied'),
      failureMessage: t('myob.connections.copyFailed'),
    })

  // 「我方建档」：在新标签打开用户管理页（现有路由 /system/users），不带任何个人资料到 URL
  const handleCreateOurSide = () => window.open('/system/users', '_blank', 'noopener,noreferrer')

  // ───────────────────────── 渲染 ─────────────────────────

  const fetchedTime = snapshot?.fetchedAt ? formatMyobTime(snapshot.fetchedAt).slice(11) : ''

  const companyBar = (
    <section className="myob-card" aria-labelledby="myob-company-title">
      <div className="myob-card-head">
        <div>
          <h2 className="myob-card-title" id="myob-company-title">
            {t('myob.employees.company.title')}
          </h2>
          <div className="myob-card-hint">{t('myob.employees.company.hint')}</div>
        </div>
        <Button
          icon={<ReloadOutlined />}
          loading={refreshing}
          disabled={!selectedGuid || loading}
          onClick={() => void load(true)}
        >
          {t('myob.employees.company.refresh')}
          {fetchedTime ? t('myob.employees.company.refreshedAt', { time: fetchedTime }) : ''}
        </Button>
      </div>

      {companiesError && companies.length === 0 ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 12 }}
          message={t('myob.employees.company.loadFailed')}
          description={companiesError}
          action={
            <Button size="small" onClick={onReloadCompanies}>
              {t('myob.employees.company.retry')}
            </Button>
          }
        />
      ) : null}

      {companiesLoading && companies.length === 0 ? (
        <Skeleton active paragraph={{ rows: 1 }} />
      ) : (
        <div className="myob-companies" role="group" aria-label={t('myob.employees.company.title')}>
          {companies.map((item) => {
            const name = item.name
            const on = item.connectionGuid === selectedGuid
            return (
              <button
                key={item.connectionGuid}
                type="button"
                aria-pressed={on}
                aria-label={t('myob.employees.company.select', { name })}
                className={`myob-company${on ? ' myob-company-on' : ''}`}
                onClick={() => setPickedGuid(item.connectionGuid)}
              >
                <span className="myob-company-icon" aria-hidden="true">
                  {name.trim().charAt(0).toUpperCase() || '?'}
                </span>
                <span className="myob-company-body">
                  <span className="myob-company-name" title={name}>
                    {name}
                  </span>
                  <span className="myob-sub">
                    {item.abn ? (
                      <span className="myob-mono">{t('myob.employees.company.abn', { abn: formatAbnDisplay(item.abn) })}</span>
                    ) : (
                      t('myob.employees.company.noAbn')
                    )}
                    {on && snapshot ? ` · ${t('myob.employees.company.employeeCount', { count: snapshot.counts.all })}` : ''}
                  </span>
                </span>
                <Tag color="success" style={{ marginInlineEnd: 0 }}>
                  {t('myob.connections.status.Active')}
                </Tag>
              </button>
            )
          })}
          {canConnect ? (
            <button type="button" className="myob-company myob-company-add" disabled={authorizing} onClick={onAuthorize}>
              <PlusOutlined aria-hidden="true" />
              {t('myob.employees.company.connectOther')}
            </button>
          ) : null}
        </div>
      )}
      {!companiesLoading && !companiesError && companies.length === 0 ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={canConnect ? t('myob.employees.company.emptyCanConnect') : t('myob.employees.company.empty')}
        />
      ) : null}
    </section>
  )

  const counts = snapshot?.counts
  const missingCount = (counts?.onlyInOurs ?? 0) + (counts?.onlyInMyob ?? 0)

  const subTabOptions = [
    { value: 'list', label: t('myob.employees.subtabs.list') },
    {
      value: 'missing',
      label: (
        <span>
          {t('myob.employees.subtabs.missing')}
          {counts ? <Tag color="warning" style={{ marginInlineStart: 6, marginInlineEnd: 0 }}>{missingCount}</Tag> : null}
        </span>
      ),
    },
    {
      value: 'typeIssues',
      label: (
        <span>
          {t('myob.employees.subtabs.typeIssues')}
          {counts ? <Tag color="warning" style={{ marginInlineStart: 6, marginInlineEnd: 0 }}>{counts.typeIssues}</Tag> : null}
        </span>
      ),
    },
  ]

  return (
    <div className="myob-tabpane">
      {companyBar}

      {selectedGuid ? (
        <section className="myob-card" aria-labelledby="myob-binding-title">
          <div className="myob-card-head">
            <div>
              <h2 className="myob-card-title" id="myob-binding-title">
                {t('myob.employees.title', { name: connectionName })}
              </h2>
              <div className="myob-card-hint">
                {subTab === 'list' ? t('myob.employees.hint') : t('myob.employees.missing.hint')}
              </div>
            </div>
            <Segmented
              className="myob-subtabs"
              aria-label={t('myob.employees.subtabs.aria')}
              value={subTab}
              options={subTabOptions}
              onChange={(value) => setSubTab(value as SubTab)}
            />
          </div>

          {error ? (
            <Alert
              type="error"
              showIcon
              style={{ marginBottom: 12 }}
              message={t('myob.employees.loadFailed')}
              description={error}
              action={
                <Button size="small" onClick={() => void load(false)}>
                  {t('myob.employees.company.retry')}
                </Button>
              }
            />
          ) : null}

          {!snapshot ? (
            loading ? (
              <Skeleton active paragraph={{ rows: 6 }} />
            ) : null
          ) : subTab === 'list' ? (
            <BindingListView
              connectionName={connectionName}
              counts={snapshot.counts}
              rows={snapshot.rows}
              filter={filter}
              keyword={keyword}
              busyUserGuids={busyUserGuids}
              batchBusy={batchBusy}
              onFilterChange={setFilter}
              onKeywordChange={setKeyword}
              onRowAction={handleRowAction}
              onBatchConfirm={handleBatchConfirm}
            />
          ) : subTab === 'missing' ? (
            <MissingPanel
              onlyInOurs={snapshot.onlyInOurs}
              onlyInMyob={snapshot.onlyInMyob}
              canCreateOurSide={canCreateOurSide}
              ignoringUids={ignoringUids}
              onCopy={handleCopyOurs}
              onCreateOurSide={handleCreateOurSide}
              onIgnore={(item) => void handleIgnore(item)}
            />
          ) : (
            <TypeIssuesPanel issues={snapshot.typeIssues} onCopyNote={handleCopyIssue} />
          )}
        </section>
      ) : null}

      {selectedGuid ? (
        <BindingDrawer
          open={drawer !== null}
          connectionGuid={selectedGuid}
          connectionName={connectionName}
          row={drawer?.row ?? null}
          preselectUid={drawer?.preselectUid ?? null}
          onClose={() => setDrawer(null)}
          onConfirm={handleDrawerConfirm}
        />
      ) : null}
    </div>
  )
}
