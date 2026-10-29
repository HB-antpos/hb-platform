import { ArrowLeftOutlined, ArrowRightOutlined, CloseOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Drawer, Modal, Skeleton, Steps, message } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import {
  applyMyobEmployeeSync,
  getMyobSyncFieldStats,
  previewMyobEmployeeSync,
  undoMyobSyncBatch,
} from '../../../services/myobService'
import { useAuthStore } from '../../../store/auth'
import type {
  MyobSyncApplyResult,
  MyobSyncFailure,
  MyobSyncField,
  MyobSyncFieldStats,
  MyobSyncMode,
  MyobSyncPreview,
  MyobSyncUndoResult,
} from '../../../types/myob'

import { extractMyobError, formatMyobTime } from './myobLogic'
import MyobSyncBatchesModal from './MyobSyncBatchesModal'
import MyobSyncFieldStep, { type SyncScopeView } from './MyobSyncFieldStep'
import {
  buildApplyRequest,
  buildInitialChecked,
  buildRescope,
  canSubmitSync,
  countSelectedFields,
  describeFetchAge,
  getDefaultScope,
  getDefaultSelectedFields,
  getScopeOptions,
  getSyncErrorMessageKey,
  listSelectedFields,
  resolveScopeGuids,
  summarizeChecked,
  type SyncCheckedMap,
  type SyncPreviewFilter,
  type SyncScopeKind,
} from './myobSyncLogic'
import MyobSyncResultStep from './MyobSyncResultStep'
import MyobSyncReviewStep from './MyobSyncReviewStep'

type Step = 'select' | 'review' | 'result'
const STEP_INDEX: Record<Step, number> = { select: 0, review: 1, result: 2 }
const EMPTY_SELECTION: ReadonlySet<MyobSyncField> = new Set()

interface MyobSyncDrawerProps {
  open: boolean
  connectionGuid: string
  connectionName: string
  /** 入口带来的员工：批量入口 = 列表勾选的已绑定员工（可为空）；单人入口 = 这 1 人。 */
  selectedGuids: string[]
  selectedNames: string[]
  /** 单人入口：范围固定为 selectedGuids 这 1 人。 */
  singleLocked: boolean
  /** 当前公司文件下已绑定员工数（「当前已绑定全部 N 人」）。 */
  boundCount: number
  onClose: () => void
  /** 同步权限（EmployeeProfiles.Edit）以 field-stats.canWrite 为准，告诉列表是否禁用入口。 */
  onCanWriteChange: (canWrite: boolean) => void
  /** 写入或撤销成功后员工资料变了，让列表重新加载。 */
  onChanged: () => void
}

/** 「从 MYOB 同步资料」三步抽屉：选字段 → 核对差异 → 完成。纯逻辑在 myobSyncLogic.ts，这里只管状态机和请求。 */
export default function MyobSyncDrawer({
  open,
  connectionGuid,
  connectionName,
  selectedGuids,
  selectedNames,
  singleLocked,
  boundCount,
  onClose,
  onCanWriteChange,
  onChanged,
}: MyobSyncDrawerProps) {
  const { t } = useTranslation()
  const currentUser = useAuthStore((state) => state.currentUser)
  const [step, setStep] = useState<Step>('select')
  const [scopeKind, setScopeKind] = useState<SyncScopeKind>(() => (singleLocked ? 'selected' : getDefaultScope(selectedGuids.length)))
  // 「重新预览这 1 人」：范围临时限定为该员工，字段沿用失败字段
  const [rescope, setRescope] = useState<{ guid: string; name: string } | null>(null)
  const [stats, setStats] = useState<MyobSyncFieldStats | null>(null)
  const [statsLoading, setStatsLoading] = useState(false)
  const [statsError, setStatsError] = useState<string | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<MyobSyncField> | null>(null)
  const [mode, setMode] = useState<MyobSyncMode>('FillEmpty')
  const [preview, setPreview] = useState<MyobSyncPreview | null>(null)
  // 预览加载中 = preview 为空且无错误（显示骨架屏），不需要单独读这个状态
  const [, setPreviewLoading] = useState(false)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [checked, setChecked] = useState<SyncCheckedMap>({})
  const [filter, setFilter] = useState<SyncPreviewFilter>('all')
  const [acknowledged, setAcknowledged] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<MyobSyncApplyResult | null>(null)
  const [appliedAt, setAppliedAt] = useState('')
  const [undone, setUndone] = useState<MyobSyncUndoResult | null>(null)
  const [undoing, setUndoing] = useState(false)
  const [historyOpen, setHistoryOpen] = useState(false)

  // 只认最后一次请求；卸载 / 提交中用 ref 保护，避免过期响应覆盖新状态或重复提交
  const statsSeq = useRef(0)
  const previewSeq = useRef(0)
  const alive = useRef(true)
  const submittingRef = useRef(false)
  // 回调放进 ref：父组件每次渲染传新函数也不会触发 loadStats 重新请求
  const onCanWriteRef = useRef(onCanWriteChange)
  const onChangedRef = useRef(onChanged)
  onCanWriteRef.current = onCanWriteChange
  onChangedRef.current = onChanged
  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  // 实际请求用的员工范围：重新预览 > 单人入口 > 批量里选的范围（全部 = 省略）
  const userGuids = useMemo<string[] | undefined>(() => {
    if (rescope) return [rescope.guid]
    if (singleLocked) return selectedGuids
    return resolveScopeGuids(scopeKind, selectedGuids)
  }, [rescope, singleLocked, scopeKind, selectedGuids])

  const showError = useCallback(
    (err: unknown, fallback: string) => {
      const parsed = extractMyobError(err, fallback)
      const key = getSyncErrorMessageKey(parsed.code)
      return { text: key ? t(key) : parsed.message, code: parsed.code }
    },
    [t],
  )

  const loadStats = useCallback(
    async (refresh: boolean) => {
      const mine = ++statsSeq.current
      setStatsLoading(true)
      try {
        const data = await getMyobSyncFieldStats({ connectionGuid, userGuids, refresh: refresh || undefined })
        if (!alive.current || mine !== statsSeq.current) return
        setStats(data)
        setStatsError(null)
        onCanWriteRef.current(data.canWrite)
        // 首次加载：默认勾选 = 后端 defaultSelected；之后换范围 / 刷新保留用户的选择
        setSelected((current) => current ?? new Set(getDefaultSelectedFields(data.fields, data.unavailableFields)))
      } catch (err) {
        if (!alive.current || mine !== statsSeq.current) return
        const { text, code } = showError(err, t('myob.sync.step1.loadFailed'))
        setStatsError(text)
        if (code === 'MYOB_SYNC_NO_PERMISSION') onCanWriteRef.current(false)
      } finally {
        if (alive.current && mine === statsSeq.current) setStatsLoading(false)
      }
    },
    [connectionGuid, userGuids, showError, t],
  )

  useEffect(() => {
    if (open) void loadStats(false)
  }, [open, loadStats])

  const runPreview = async (guids: string[] | undefined, fields: MyobSyncField[]) => {
    const mine = ++previewSeq.current
    setPreviewLoading(true)
    setPreviewError(null)
    setPreview(null)
    try {
      const data = await previewMyobEmployeeSync({ connectionGuid, userGuids: guids, fields, mode })
      if (!alive.current || mine !== previewSeq.current) return
      setPreview(data)
      setChecked(buildInitialChecked(data.employees))
      setFilter('all')
      setAcknowledged(false)
    } catch (err) {
      if (alive.current && mine === previewSeq.current) setPreviewError(showError(err, t('myob.sync.step2.loadFailed')).text)
    } finally {
      if (alive.current && mine === previewSeq.current) setPreviewLoading(false)
    }
  }

  const selectedSet = selected ?? EMPTY_SELECTION
  const unavailable = stats?.unavailableFields ?? []
  const fieldCounts = stats ? countSelectedFields(stats.fields, selectedSet, unavailable) : { fieldCount: 0, sensitiveCount: 0 }
  const checkedSummary = useMemo(
    () => summarizeChecked(preview?.employees ?? [], checked),
    [preview, checked],
  )

  const goReview = () => {
    if (!stats || fieldCounts.fieldCount === 0) return
    setStep('review')
    void runPreview(userGuids, listSelectedFields(stats.fields, selectedSet, unavailable))
  }

  const goBack = () => {
    previewSeq.current += 1 // 作废还在路上的预览请求
    setPreviewLoading(false)
    setStep('select')
  }

  const handleRescope = (failure: MyobSyncFailure) => {
    const { userGuids: guids, fields } = buildRescope(failure)
    setRescope({ guid: failure.userGuid, name: failure.userName })
    setSelected(new Set(fields))
    setStep('review')
    void runPreview(guids, fields)
  }

  const handleConfirm = async () => {
    if (submittingRef.current || !preview) return
    const request = buildApplyRequest(connectionGuid, preview.employees, checked)
    if (!request || !canSubmitSync(checkedSummary, acknowledged)) return
    submittingRef.current = true
    setSubmitting(true)
    try {
      const data = await applyMyobEmployeeSync(request)
      if (!alive.current) return
      setResult(data)
      setAppliedAt(new Date().toISOString())
      setUndone(null)
      setStep('result')
      if (data.writtenChangeCount > 0) onChangedRef.current()
    } catch (err) {
      if (alive.current) message.error(showError(err, t('myob.sync.step2.submitFailed')).text)
    } finally {
      submittingRef.current = false
      if (alive.current) setSubmitting(false)
    }
  }

  const handleUndo = () => {
    const batchGuid = result?.batchGuid
    if (!batchGuid || !result) return
    Modal.confirm({
      title: t('myob.sync.step3.undoConfirmTitle'),
      content: t('myob.sync.step3.undoConfirmContent', { people: result.writtenEmployeeCount, changes: result.writtenChangeCount }),
      okText: t('myob.sync.step3.undoConfirmOk'),
      cancelText: t('myob.sync.step3.undoCancel'),
      okButtonProps: { danger: true },
      onOk: async () => {
        setUndoing(true)
        try {
          const data = await undoMyobSyncBatch(batchGuid)
          if (alive.current) setUndone(data)
          onChangedRef.current()
        } catch (err) {
          message.error(showError(err, t('myob.sync.step3.undoFailed')).text)
        } finally {
          if (alive.current) setUndoing(false)
        }
      },
    })
  }

  // ───────────────────────── 渲染 ─────────────────────────

  const scopeView: SyncScopeView = {
    kind: scopeKind,
    options: getScopeOptions(selectedGuids.length, singleLocked),
    singleName: rescope ? rescope.name : singleLocked ? (selectedNames[0] ?? '') : null,
    rescoped: rescope !== null,
    selectedNames,
    selectedCount: selectedGuids.length,
    boundCount,
  }
  const peopleInScope = rescope ? 1 : scopeKind === 'all' && !singleLocked ? (stats?.boundCount ?? boundCount) : selectedGuids.length
  const fetchAge = describeFetchAge(stats?.fetchedAt, Date.now())

  const subtitle =
    step === 'select' ? (
      <span className="myob-sub" style={{ fontWeight: 400 }}>
        {connectionName} · <span className="myob-sync-readonly">{t('myob.sync.readOnly')}</span>
        {fetchAge ? ` · ${t('myob.sync.fetchedAt', { age: t(fetchAge.key, { count: fetchAge.count }) })}` : ''}{' '}
        <Button type="link" size="small" className="myob-sync-refresh" disabled={statsLoading} onClick={() => void loadStats(true)}>
          {t('myob.sync.refresh')}
        </Button>
      </span>
    ) : step === 'review' ? (
      <span className="myob-sub" style={{ fontWeight: 400 }}>
        {t('myob.sync.step2.subtitle', { mode: t(`myob.sync.step2.modeName.${mode}`) })}
      </span>
    ) : (
      <span className="myob-sub" style={{ fontWeight: 400 }}>
        {t('myob.sync.step3.subtitle', { time: formatMyobTime(appliedAt), actor: currentUser?.fullName || currentUser?.username || '--' })}
      </span>
    )

  const closeDisabled = submitting
  let footer: React.ReactNode
  if (step === 'select') {
    footer = (
      <div className="myob-sync-footer">
        <span className="myob-sync-footer-note">
          {stats?.canWrite
            ? t('myob.sync.step1.summary', { fields: fieldCounts.fieldCount, sensitive: fieldCounts.sensitiveCount, people: peopleInScope })
            : null}
        </span>
        <Button onClick={onClose}>{t('myob.sync.step1.cancel')}</Button>
        <Button type="primary" disabled={!stats?.canWrite || fieldCounts.fieldCount === 0} onClick={goReview}>
          {t('myob.sync.step1.next')} <ArrowRightOutlined />
        </Button>
      </div>
    )
  } else if (step === 'review') {
    footer = (
      <div className="myob-sync-footer">
        {checkedSummary.sensitiveCount > 0 ? (
          <Checkbox checked={acknowledged} onChange={(event) => setAcknowledged(event.target.checked)}>
            {t('myob.sync.step2.ack')}
          </Checkbox>
        ) : null}
        <span className="myob-sync-footer-note">
          {t('myob.sync.step2.willWrite', { count: checkedSummary.changeCount, people: checkedSummary.employeeCount })}
        </span>
        <Button icon={<ArrowLeftOutlined />} disabled={submitting} onClick={goBack}>
          {t('myob.sync.step2.back')}
        </Button>
        <Button
          type="primary"
          loading={submitting}
          disabled={!preview || !canSubmitSync(checkedSummary, acknowledged)}
          onClick={() => void handleConfirm()}
        >
          {t('myob.sync.step2.confirm', { count: checkedSummary.changeCount })}
        </Button>
      </div>
    )
  } else {
    footer = (
      <div className="myob-sync-footer">
        <span className="myob-sync-footer-note">{t('myob.sync.step3.footerNote')}</span>
        <Button onClick={() => setHistoryOpen(true)}>{t('myob.sync.step3.viewHistory')}</Button>
        <Button type="primary" onClick={onClose}>
          {t('myob.sync.step3.done')}
        </Button>
      </div>
    )
  }

  return (
    <>
      <Drawer
        open={open}
        width={960}
        rootClassName="myob-drawer myob-sync-drawer"
        destroyOnClose
        onClose={onClose}
        maskClosable={!closeDisabled}
        keyboard={!closeDisabled}
        closable={false}
        extra={<Button type="text" icon={<CloseOutlined />} disabled={closeDisabled} aria-label={t('myob.sync.close')} onClick={onClose} />}
        title={
          <div className="myob-sync-head">
            <div>{t('myob.sync.title')}</div>
            {subtitle}
            <Steps
              size="small"
              responsive={false}
              current={STEP_INDEX[step]}
              items={[{ title: t('myob.sync.steps.select') }, { title: t('myob.sync.steps.review') }, { title: t('myob.sync.steps.done') }]}
            />
          </div>
        }
        footer={footer}
      >
        {step === 'select' ? (
          <div className="myob-sync-scroll">
            <MyobSyncFieldStep
              stats={stats}
              loading={statsLoading}
              error={statsError}
              onRetry={() => void loadStats(false)}
              selected={selectedSet}
              onSelectedChange={setSelected}
              mode={mode}
              onModeChange={setMode}
              scope={scopeView}
              onScopeChange={setScopeKind}
            />
          </div>
        ) : step === 'review' ? (
          preview ? (
            <MyobSyncReviewStep
              preview={preview}
              checked={checked}
              summary={checkedSummary}
              filter={filter}
              onFilterChange={setFilter}
              onCheckedChange={setChecked}
            />
          ) : (
            <div className="myob-sync-scroll">
              {previewError ? (
                <Alert
                  type="error"
                  showIcon
                  message={t('myob.sync.step2.loadFailed')}
                  description={previewError}
                  action={
                    <Button size="small" onClick={() => stats && void runPreview(userGuids, listSelectedFields(stats.fields, selectedSet, unavailable))}>
                      {t('myob.sync.step2.retry')}
                    </Button>
                  }
                />
              ) : (
                <Skeleton active paragraph={{ rows: 8 }} />
              )}
            </div>
          )
        ) : result ? (
          <div className="myob-sync-scroll">
            <MyobSyncResultStep
              result={result}
              // 「N 人无需同步」= 无差异 + MYOB 卡已不存在（与设计稿 18 = 10 写入 + 1 失败 + 7 跳过一致）
              skippedNoDiff={(preview?.summary.skippedNoDiff ?? 0) + (preview?.summary.skippedMissingCard ?? 0)}
              undone={undone}
              undoing={undoing}
              now={Date.now()}
              onUndo={handleUndo}
              onRescope={handleRescope}
            />
          </div>
        ) : null}
      </Drawer>
      <MyobSyncBatchesModal open={historyOpen} connectionGuid={connectionGuid} onClose={() => setHistoryOpen(false)} onUndone={() => onChangedRef.current()} />
    </>
  )
}
