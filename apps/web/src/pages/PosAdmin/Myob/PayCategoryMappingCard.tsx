import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Modal, Select, Skeleton, Tag, message } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useBlocker, type BlockerFunction } from 'react-router-dom'

import { getMyobPayCategories, saveMyobPayCategoryMapping } from '../../../services/myobService'
import type { MyobConnection, MyobPayCategories, MyobWageCategory } from '../../../types/myob'
import { extractMyobError, formatMyobTime } from './myobLogic'
import {
  PAY_CATEGORY_ROWS,
  buildPayCategoryMappingRequest,
  getCompanyMappingBadge,
  getPayCategoryRowState,
  getPayCategorySaveState,
  hasPayCategoryEdits,
  reconcilePayCategoryEdits,
  resolvePayCategoryValues,
  setPayCategoryEdit,
  type PayCategoryEdits,
  type PayCategoryRowMeta,
  type PayCategoryRowState,
} from './myobTimesheetLogic'

interface PayCategoryMappingCardProps {
  /** 全部连接；只显示 Active 的（NeedsReauth / Disconnected 读不到 MYOB 工资类别）。 */
  connections: MyobConnection[]
  connectionsLoading: boolean
  /** 由推送预览里「去映射」带来的预选公司；每次请求带递增序号，同一公司重复点击也会重新选中。 */
  focusRequest: { connectionGuid: string; seq: number } | null
}

/** 每家公司的 MYOB 工资类别读取结果（首屏并行读取，公司卡片上要显示「已映射 N / 5」）。 */
interface CompanyEntry {
  data: MyobPayCategories | null
  loading: boolean
  error: string | null
}

const EMPTY_ENTRY: CompanyEntry = { data: null, loading: false, error: null }

export default function PayCategoryMappingCard({ connections, connectionsLoading, focusRequest }: PayCategoryMappingCardProps) {
  const { t } = useTranslation()
  // 与其他页签的公司选择条保持同一顺序（后端返回顺序），不再自己排序
  const companies = useMemo(() => connections.filter((item) => item.status === 'Active'), [connections])
  const [pickedGuid, setPickedGuid] = useState<string | null>(null)
  const selectedGuid = useMemo(
    () => (pickedGuid && companies.some((item) => item.connectionGuid === pickedGuid) ? pickedGuid : (companies[0]?.connectionGuid ?? null)),
    [companies, pickedGuid],
  )
  const selected = companies.find((item) => item.connectionGuid === selectedGuid) ?? null

  const [entries, setEntries] = useState<Record<string, CompanyEntry>>({})
  const [edits, setEdits] = useState<PayCategoryEdits>({})
  const [refreshing, setRefreshing] = useState(false)
  const [saving, setSaving] = useState(false)
  // 每家公司各自的请求序号：只认最后一次发起的请求，避免快速点「重新读取」时旧响应覆盖新数据
  const seqs = useRef<Record<string, number>>({})
  const requested = useRef<Set<string>>(new Set())
  const editsRef = useRef(edits)
  editsRef.current = edits
  const selectedGuidRef = useRef<string | null>(null)

  selectedGuidRef.current = selectedGuid
  const entry = (selectedGuid ? entries[selectedGuid] : null) ?? EMPTY_ENTRY
  const data = entry.data
  const dirty = hasPayCategoryEdits(edits)

  const patchEntry = useCallback((guid: string, patch: Partial<CompanyEntry>) => {
    setEntries((current) => ({ ...current, [guid]: { ...(current[guid] ?? EMPTY_ENTRY), ...patch } }))
  }, [])

  const load = useCallback(
    async (guid: string, refresh: boolean) => {
      const seq = (seqs.current[guid] = (seqs.current[guid] ?? 0) + 1)
      patchEntry(guid, { loading: true })
      try {
        const result = await getMyobPayCategories(guid, refresh)
        if (seq !== seqs.current[guid]) {
          return
        }
        patchEntry(guid, { data: result, error: null, loading: false })
        // 重新读取后，草稿里已不在新类别列表的项要丢掉，避免保存时提交一个 MYOB 里已不存在的类别
        if (refresh) {
          // 草稿只属于当前选中的公司；读的是别家（刷新途中切了公司）就不要拿它去对账
          if (guid === selectedGuidRef.current) {
            setEdits((current) => reconcilePayCategoryEdits(result, current))
          }
          message.success(t('myob.payCategories.refreshed'))
        }
      } catch (err) {
        if (seq !== seqs.current[guid]) {
          return
        }
        const text = extractMyobError(err, refresh ? t('myob.payCategories.refreshFailed') : t('myob.payCategories.loadFailed')).message
        patchEntry(guid, { error: text, loading: false })
        if (refresh) {
          message.error(text)
        }
      }
    },
    [patchEntry, t],
  )

  // 公司列表就绪（或新增公司）后，把还没读过的公司并行读一遍
  useEffect(() => {
    for (const company of companies) {
      if (!requested.current.has(company.connectionGuid)) {
        requested.current.add(company.connectionGuid)
        void load(company.connectionGuid, false)
      }
    }
  }, [companies, load])

  // 草稿属于某一家公司：公司换了就清空（切换前已经过确认）
  useEffect(() => {
    setEdits({})
  }, [selectedGuid])

  // ───────────────────────── 未保存改动保护 ─────────────────────────

  // 站内跳到别的页面（路径变化）前确认；同一页面内换查询参数（如授权回调清参数）不拦
  const blocker = useBlocker(
    useCallback<BlockerFunction>(
      ({ currentLocation, nextLocation }) => hasPayCategoryEdits(editsRef.current) && currentLocation.pathname !== nextLocation.pathname,
      [],
    ),
  )
  const blockerPrompted = useRef(false)
  useEffect(() => {
    if (blocker.state !== 'blocked') {
      blockerPrompted.current = false
      return
    }
    if (blockerPrompted.current) {
      return
    }
    blockerPrompted.current = true
    Modal.confirm({
      title: t('myob.payCategories.leave.title'),
      content: t('myob.payCategories.leave.content'),
      okText: t('myob.payCategories.leave.ok'),
      cancelText: t('myob.payCategories.leave.cancel'),
      okButtonProps: { danger: true },
      onOk: () => blocker.proceed?.(),
      onCancel: () => blocker.reset?.(),
    })
  }, [blocker, t])

  // 关闭 / 刷新浏览器标签：交给浏览器原生确认
  useEffect(() => {
    if (!dirty) {
      return
    }
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handleBeforeUnload)
    return () => window.removeEventListener('beforeunload', handleBeforeUnload)
  }, [dirty])

  // ───────────────────────── 选公司 ─────────────────────────

  const handleSelect = useCallback(
    (guid: string) => {
      if (guid === selectedGuid) {
        return
      }
      if (!hasPayCategoryEdits(editsRef.current)) {
        setPickedGuid(guid)
        return
      }
      Modal.confirm({
        title: t('myob.payCategories.switch.title'),
        content: t('myob.payCategories.switch.content'),
        okText: t('myob.payCategories.switch.ok'),
        cancelText: t('myob.payCategories.switch.cancel'),
        okButtonProps: { danger: true },
        onOk: () => setPickedGuid(guid),
      })
    },
    [selectedGuid, t],
  )

  // 去映射：外部指定公司时切换选中
  const handledFocusSeq = useRef(0)
  useEffect(() => {
    if (focusRequest && focusRequest.seq !== handledFocusSeq.current) {
      handledFocusSeq.current = focusRequest.seq
      handleSelect(focusRequest.connectionGuid)
    }
  }, [focusRequest, handleSelect])

  // ───────────────────────── 保存 ─────────────────────────

  const saveState = data ? getPayCategorySaveState(data, edits) : null

  const handleSave = async () => {
    if (!data || !selectedGuid || saving) {
      return
    }
    const body = buildPayCategoryMappingRequest(data, edits)
    if (!body) {
      return
    }
    setSaving(true)
    try {
      const saved = await saveMyobPayCategoryMapping(selectedGuid, body)
      patchEntry(selectedGuid, { data: saved, error: null })
      setEdits({})
      message.success(t('myob.payCategories.saved', { name: selected?.businessName || saved.connectionName }))
    } catch (err) {
      const failure = extractMyobError(err, t('myob.payCategories.saveFailed'))
      message.error(failure.message)
      // 选的类别在 MYOB 里已被删掉：重新读一遍，让下拉与失效提示跟上现状
      if (failure.code === 'MYOB_PAY_CATEGORY_NOT_FOUND') {
        void load(selectedGuid, true)
      }
    } finally {
      setSaving(false)
    }
  }

  const handleRefresh = async () => {
    if (!selectedGuid) {
      return
    }
    setRefreshing(true)
    try {
      await load(selectedGuid, true)
    } finally {
      setRefreshing(false)
    }
  }

  // ───────────────────────── 渲染 ─────────────────────────

  const fetchedTime = data?.fetchedAt ? formatMyobTime(data.fetchedAt).slice(11) : ''
  const selectedName = data?.connectionName || selected?.businessName || selected?.businessId || ''
  const categoryByUid = useMemo(
    () => new Map<string, MyobWageCategory>((data?.categories ?? []).map((item) => [item.uid, item])),
    [data],
  )
  const options = useMemo(
    () => (data?.categories ?? []).map((item) => ({ value: item.uid, label: item.name, wageType: item.wageType })),
    [data],
  )
  const values = data ? resolvePayCategoryValues(data, edits) : null

  const renderNote = (meta: PayCategoryRowMeta, state: PayCategoryRowState) => {
    const staticNote = meta.noteKey ? <span className="myob-pc-note">{t(meta.noteKey)}</span> : null
    switch (state) {
      case 'suggested':
        return (
          <>
            <Tag color="success">{t('myob.payCategories.state.suggested')}</Tag>
            <span className="myob-pc-note">{t('myob.payCategories.state.suggestedHint')}</span>
          </>
        )
      case 'unset':
        return (
          <>
            <Tag color="warning">{t('myob.payCategories.state.unset')}</Tag>
            <span className="myob-pc-note myob-pc-note-warn">{t('myob.payCategories.state.unsetHint')}</span>
          </>
        )
      case 'missing':
        return (
          <>
            <Tag color="error">{t('myob.payCategories.state.missing')}</Tag>
            <span className="myob-pc-note myob-pc-note-bad">{t('myob.payCategories.state.missingHint')}</span>
          </>
        )
      case 'edited':
        return (
          <>
            <Tag>{t('myob.payCategories.state.edited')}</Tag>
            {staticNote}
          </>
        )
      default:
        return staticNote
    }
  }

  let body: ReactNode = null
  if (selected && entry.error && !data) {
    body = (
      <Alert
        type="error"
        showIcon
        message={t('myob.payCategories.loadFailed')}
        description={entry.error}
        action={
          <Button size="small" onClick={() => void load(selected.connectionGuid, false)}>
            {t('myob.payCategories.retry')}
          </Button>
        }
      />
    )
  } else if (selected && (!data || !values || !saveState)) {
    body = <Skeleton active paragraph={{ rows: 5 }} />
  } else if (selected && data && values && saveState) {
    body = (
      <>
        {entry.error ? <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={entry.error} /> : null}
        {data.categories.length === 0 ? (
          <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('myob.payCategories.noCategories', { name: selectedName })} />
        ) : null}
        <div className="myob-pc-scroll">
          <table className="myob-pc-table" aria-label={t('myob.payCategories.title')}>
            <thead>
              <tr>
                <th scope="col" className="myob-pc-col-ours">
                  {t('myob.payCategories.columns.ours')}
                </th>
                <th scope="col" className="myob-pc-col-arrow">
                  <span className="myob-sr-only">{t('myob.payCategories.columns.arrow')}</span>
                </th>
                <th scope="col">{t('myob.payCategories.columns.myob', { name: selectedName })}</th>
                <th scope="col" className="myob-pc-col-note">
                  {t('myob.payCategories.columns.note')}
                </th>
              </tr>
            </thead>
            <tbody>
              {PAY_CATEGORY_ROWS.map((meta) => {
                const state = getPayCategoryRowState(data, edits, meta.key)
                const value = values[meta.key]
                return (
                  <tr key={meta.key} className={`myob-pc-row myob-pc-row-${state}`} data-state={state}>
                    <th scope="row" className="myob-pc-ours">
                      <span className="myob-person-name">{t(meta.labelKey)}</span>
                      {meta.subKey ? <span className="myob-sub">{t(meta.subKey)}</span> : null}
                    </th>
                    <td className="myob-pc-arrow" aria-hidden="true">
                      →
                    </td>
                    <td>
                      <Select
                        showSearch
                        className="myob-pc-select"
                        value={value ?? undefined}
                        options={options}
                        optionFilterProp="label"
                        placeholder={t('myob.payCategories.placeholder')}
                        aria-label={t('myob.payCategories.selectAria', { label: t(meta.labelKey) })}
                        status={state === 'missing' ? 'error' : state === 'unset' ? 'warning' : undefined}
                        disabled={saving}
                        notFoundContent={t('myob.payCategories.noMatch')}
                        optionRender={(option) => <CategoryLabel name={String(option.data.label ?? '')} wageType={option.data.wageType} />}
                        labelRender={(props) => {
                          const category = categoryByUid.get(String(props.value))
                          return category ? <CategoryLabel name={category.name} wageType={category.wageType} /> : props.label
                        }}
                        onChange={(uid: string) => setEdits((current) => setPayCategoryEdit(data, current, meta.key, uid))}
                      />
                    </td>
                    <td className="myob-pc-desc">{renderNote(meta, state)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>

        <Alert className="myob-note" type="info" showIcon={false} message={<PayCategoryNote />} />

        <div className="myob-pc-footer">
          <span className="myob-pc-footer-hint" role="status">
            {saveState.remaining > 0
              ? t('myob.payCategories.remaining', { count: saveState.remaining })
              : dirty
                ? t('myob.payCategories.unsaved')
                : saveState.changed
                  ? t('myob.payCategories.suggestionsUnsaved')
                  : ''}
          </span>
          <Button disabled={!dirty || saving} onClick={() => setEdits({})}>
            {t('myob.payCategories.cancel')}
          </Button>
          <Button type="primary" disabled={!saveState.canSave} loading={saving} onClick={() => void handleSave()}>
            {t('myob.payCategories.save')}
          </Button>
        </div>
      </>
    )
  }

  return (
    <section className="myob-card" aria-labelledby="myob-paycat-title">
      <div className="myob-card-head">
        <div>
          <h2 className="myob-card-title" id="myob-paycat-title">
            {t('myob.payCategories.title')}
          </h2>
          <div className="myob-card-hint">{t('myob.payCategories.hint')}</div>
        </div>
        <Button
          icon={<ReloadOutlined />}
          loading={refreshing}
          disabled={!selectedGuid || saving || (entry.loading && !refreshing)}
          onClick={() => void handleRefresh()}
        >
          {t('myob.payCategories.refresh')}
          {fetchedTime ? t('myob.payCategories.refreshedAt', { time: fetchedTime }) : ''}
        </Button>
      </div>

      {connectionsLoading && companies.length === 0 ? (
        <Skeleton active paragraph={{ rows: 1 }} />
      ) : companies.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.payCategories.noCompanies')} />
      ) : (
        <div className="myob-companies myob-pc-companies" role="group" aria-label={t('myob.payCategories.companyAria')}>
          {companies.map((company) => {
            const name = company.businessName || company.businessId
            const on = company.connectionGuid === selectedGuid
            const state = entries[company.connectionGuid] ?? EMPTY_ENTRY
            const badge = getCompanyMappingBadge(state.data)
            return (
              <button
                key={company.connectionGuid}
                type="button"
                aria-pressed={on}
                aria-label={t('myob.employees.company.select', { name })}
                disabled={saving}
                className={`myob-company${on ? ' myob-company-on' : ''}`}
                onClick={() => handleSelect(company.connectionGuid)}
              >
                <span className="myob-company-icon" aria-hidden="true">
                  {name.trim().charAt(0).toUpperCase() || '?'}
                </span>
                <span className="myob-company-body">
                  <span className="myob-company-name" title={name}>
                    {name}
                  </span>
                  <span className="myob-sub">
                    {state.data
                      ? t('myob.payCategories.mappedCount', { mapped: badge.mapped, total: badge.total })
                      : state.error
                        ? t('myob.payCategories.loadFailedShort')
                        : t('myob.payCategories.loadingShort')}
                  </span>
                </span>
                {state.data ? (
                  <Tag color={badge.complete ? 'success' : 'warning'} style={{ marginInlineEnd: 0 }}>
                    {badge.complete ? t('myob.payCategories.complete') : t('myob.payCategories.incomplete')}
                  </Tag>
                ) : state.error ? (
                  <Tag color="error" style={{ marginInlineEnd: 0 }}>
                    {t('myob.payCategories.loadFailedShort')}
                  </Tag>
                ) : null}
              </button>
            )
          })}
        </div>
      )}

      {body ? <div className="myob-pc-body">{body}</div> : null}
    </section>
  )
}

/** 下拉里的类别：名称 + MYOB 的 WageType 小标签（Hourly / Salary）。 */
function CategoryLabel({ name, wageType }: { name: string; wageType: string | null | undefined }) {
  return (
    <span className="myob-pc-category">
      <span className="myob-pc-category-name">{name}</span>
      {wageType ? <Tag className="myob-pc-wage">{wageType}</Tag> : null}
    </span>
  )
}

/** 底部说明条：不推送年假折现；MYOB 端前置配置由财务确认。 */
function PayCategoryNote() {
  const { t } = useTranslation()
  return (
    <span>
      <b>{t('myob.payCategories.note.notPushedLabel')}</b>
      {t('myob.payCategories.note.notPushed')}
      <b>{t('myob.payCategories.note.prereqLabel')}</b>
      {t('myob.payCategories.note.prereq')}
    </span>
  )
}
