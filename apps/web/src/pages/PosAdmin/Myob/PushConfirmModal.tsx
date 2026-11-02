import { Alert, Button, Checkbox, Modal, Progress, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { pushMyobTimesheet } from '../../../services/myobService'
import type { MyobPushUserResult, MyobTimesheetPreview } from '../../../types/myob'
import { extractMyobError } from './myobLogic'
import {
  buildPushPlan,
  canConfirmPush,
  classifyPushStatus,
  countPushResults,
  getOverwriteCandidateGuids,
  getPushCodeMessageKey,
  getPushStatusLabelKey,
  getPushStatusTone,
  getRetryableUserGuids,
  isFatalPushCode,
  mergePushResults,
  PUSH_COUNT_LABEL_KEYS,
  PUSH_COUNT_ORDER,
  runPushChunks,
  sumPlanHours,
  summarizePushSelection,
  type PushRunProgress,
} from './myobPushLogic'
import { formatHours, formatPeriodRange } from './myobTimesheetLogic'

interface PushConfirmModalProps {
  open: boolean
  preview: MyobTimesheetPreview
  /** 打开时预览页勾选的员工。 */
  selectedUserGuids: string[]
  /** 预览页「我已知晓提示项」是否已勾选。 */
  hintsAcknowledged: boolean
  /** 关闭弹窗；changed = 这次发出过推送（上层要刷新预览）。 */
  onClose: (changed: boolean) => void
  /** 去看推送记录（含撤销）。 */
  onOpenBatches: () => void
}

type Phase = 'confirm' | 'running' | 'paused' | 'done'

/** 名单预览最多列几个名字，其余折叠成「等 N 人」。 */
const NAME_PREVIEW_LIMIT = 8

/**
 * 推送确认 → 分块进度 → 结果。会写 MYOB 生产数据，所以：
 * - 确认页写明「只写 Timesheet、不发薪」；MYOB 里已有未发薪工时的人必须在这里勾选覆盖，不勾选就本次不推；
 * - 推送中不能关闭弹窗；「停止」只在块与块之间生效，已处理的不会回滚（可在推送记录里撤销）；
 * - 某一块请求失败时可「继续」（沿用同一批次号），遇到总闸 / 授权 / 周期被占用这类整次都无法继续的错误则只能先解决原因。
 */
export default function PushConfirmModal({ open, preview, selectedUserGuids, hintsAcknowledged, onClose, onOpenBatches }: PushConfirmModalProps) {
  const { t } = useTranslation()
  const [phase, setPhase] = useState<Phase>('confirm')
  // 这次弹窗处理的人（首次 = 预览页勾选；「覆盖并重推」会改成服务端告诉我们有记录的那些人）
  const [candidates, setCandidates] = useState<string[]>(selectedUserGuids)
  const [forcedOverwrite, setForcedOverwrite] = useState<string[]>([])
  const [overwrite, setOverwrite] = useState(false)
  const [progress, setProgress] = useState<PushRunProgress>({ done: 0, total: 0, chunk: 0, chunks: 0 })
  const [results, setResults] = useState<MyobPushUserResult[]>([])
  const [batchGuid, setBatchGuid] = useState('')
  const [pending, setPending] = useState<string[]>([])
  const [runError, setRunError] = useState<{ message: string; code: string | null } | null>(null)
  const [changed, setChanged] = useState(false)
  const stopRef = useRef(false)
  const [stopping, setStopping] = useState(false)
  const alive = useRef(true)
  // 暂停后「继续」要沿用的覆盖名单
  const overwriteRef = useRef<string[]>([])
  const hintCodesRef = useRef<string[]>([])

  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  // 每次重新打开：回到确认页，清空上一次的运行状态
  useEffect(() => {
    if (open) {
      setPhase('confirm')
      setCandidates(selectedUserGuids)
      setForcedOverwrite([])
      setOverwrite(false)
      setResults([])
      setBatchGuid('')
      setPending([])
      setRunError(null)
      setChanged(false)
      stopRef.current = false
      setStopping(false)
    }
    // 只在打开瞬间取一次选择，运行中预览刷新不应改变它
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const selectedSet = useMemo(() => new Set(candidates), [candidates])
  const summary = useMemo(() => summarizePushSelection(preview, selectedSet, forcedOverwrite), [preview, selectedSet, forcedOverwrite])
  const plan = useMemo(() => buildPushPlan(summary, selectedSet, overwrite), [summary, selectedSet, overwrite])
  const nameOf = (guid: string) => preview.rows.find((row) => row.userGuid === guid)?.userName ?? guid
  const overwriteNames = summary.overwriteGuids.map(nameOf)

  const run = async (userGuids: string[], overwriteGuids: string[], hintCodes: string[], existingBatch: string) => {
    stopRef.current = false
    setStopping(false)
    overwriteRef.current = overwriteGuids
    hintCodesRef.current = hintCodes
    setPhase('running')
    setRunError(null)
    setChanged(true)
    const outcome = await runPushChunks(
      {
        connectionGuid: preview.connectionGuid,
        periodStart: preview.periodStart,
        userGuids,
        overwriteUserGuids: overwriteGuids,
        acknowledgedHintCodes: hintCodes,
        batchGuid: existingBatch || undefined,
      },
      pushMyobTimesheet,
      (item) => alive.current && setProgress(item),
      () => stopRef.current,
      (error) => extractMyobError(error, '').code,
    )
    if (!alive.current) {
      return
    }
    setBatchGuid(outcome.batchGuid)
    setResults((previous) => mergePushResults(previous, outcome.results))
    if (outcome.kind === 'failed') {
      setPending(outcome.pendingUserGuids)
      const parsed = extractMyobError(outcome.error, t('myob.push.run.requestFailed'))
      setRunError({ message: parsed.message, code: parsed.code })
      setPhase('paused')
    } else if (outcome.kind === 'stopped') {
      setPending(outcome.pendingUserGuids)
      setPhase('done')
    } else {
      setPending([])
      setPhase('done')
    }
  }

  const handleConfirm = () => {
    // 沿用原批次的「覆盖并重推」要保留前面的结果（结果页按员工合并显示最新结果）；全新一次运行才清空
    if (!batchGuid) {
      setResults([])
    }
    setProgress({ done: 0, total: plan.userGuids.length, chunk: 0, chunks: 0 })
    void run(plan.userGuids, plan.overwriteUserGuids, plan.acknowledgedHintCodes, batchGuid)
  }

  const handleContinue = () => void run(pending, overwriteRef.current, hintCodesRef.current, batchGuid)

  const handleStop = () => {
    stopRef.current = true
    setStopping(true)
  }

  const handleRetryFailed = () => {
    const failed = getRetryableUserGuids(results)
    setProgress({ done: 0, total: failed.length, chunk: 0, chunks: 0 })
    void run(failed, [], hintCodesRef.current, batchGuid)
  }

  // 「覆盖并重推」不直接写：回到确认页，由人再勾选一次覆盖
  const handleOverwriteRepush = () => {
    const skipped = getOverwriteCandidateGuids(results)
    setCandidates(skipped)
    setForcedOverwrite(skipped)
    setOverwrite(false)
    setPending([])
    setPhase('confirm')
  }

  const counts = countPushResults(results)
  const retryable = getRetryableUserGuids(results)
  const overwriteCandidates = getOverwriteCandidateGuids(results)
  const fatal = isFatalPushCode(runError?.code)
  const errorMessageKey = getPushCodeMessageKey(runError?.code)
  const running = phase === 'running'

  const resultColumns: ColumnsType<MyobPushUserResult> = [
    { title: t('myob.push.run.columns.employee'), key: 'employee', width: 180, render: (_, item) => <b>{item.userName}</b> },
    {
      title: t('myob.push.run.columns.status'),
      key: 'status',
      width: 150,
      render: (_, item) => (
        <Tag color={getPushStatusTone(item.status)} style={{ marginInlineEnd: 0 }}>
          {t(getPushStatusLabelKey(item.status))}
        </Tag>
      ),
    },
    {
      title: t('myob.push.run.columns.detail'),
      key: 'detail',
      render: (_, item) => {
        const key = getPushCodeMessageKey(item.code)
        return key ? t(key) : item.message || <span className="myob-muted">—</span>
      },
    },
  ]
  // 结果表只列「需要人看一眼」的；成功的只计数，免得几十个绿标签淹没问题行
  const attentionRows = results.filter((item) => classifyPushStatus(item.status) !== 'success')

  const range = formatPeriodRange(preview.periodStart, preview.periodEnd)

  const footer =
    phase === 'confirm' ? (
      <>
        <Button onClick={() => onClose(changed)}>{t('myob.push.run.cancel')}</Button>
        <Button type="primary" disabled={!canConfirmPush(plan, summary, hintsAcknowledged)} onClick={handleConfirm}>
          {t('myob.push.run.confirm', { count: plan.userGuids.length })}
        </Button>
      </>
    ) : phase === 'running' ? (
      <Button onClick={handleStop} disabled={stopping}>
        {stopping ? t('myob.push.run.stopping') : t('myob.push.run.stop')}
      </Button>
    ) : phase === 'paused' ? (
      <>
        <Button onClick={() => setPhase('done')}>{t('myob.push.run.giveUp')}</Button>
        {!fatal ? (
          <Button type="primary" onClick={handleContinue}>
            {t('myob.push.run.continue', { count: pending.length })}
          </Button>
        ) : null}
      </>
    ) : (
      <>
        <Button onClick={onOpenBatches}>{t('myob.push.run.openBatches')}</Button>
        {retryable.length > 0 ? <Button onClick={handleRetryFailed}>{t('myob.push.run.retryFailed', { count: retryable.length })}</Button> : null}
        {overwriteCandidates.length > 0 ? (
          <Button onClick={handleOverwriteRepush}>{t('myob.push.run.overwriteRepush', { count: overwriteCandidates.length })}</Button>
        ) : null}
        <Button type="primary" onClick={() => onClose(changed)}>
          {t('myob.push.run.close')}
        </Button>
      </>
    )

  return (
    <Modal
      open={open}
      title={t('myob.push.run.title')}
      width={680}
      footer={footer}
      // 推送中 / 暂停时不能随手关掉：关掉就看不到哪些人已经写进 MYOB 了
      closable={phase === 'confirm' || phase === 'done'}
      maskClosable={false}
      keyboard={phase === 'confirm' || phase === 'done'}
      onCancel={() => onClose(changed)}
      destroyOnHidden
    >
      {phase === 'confirm' ? (
        <div className="myob-run">
          <p className="myob-run-lead">
            {t('myob.push.run.lead', { company: preview.connectionName, range, count: plan.userGuids.length, hours: formatHours(sumPlanHours(preview, plan)) })}
          </p>
          <Alert type="info" showIcon className="myob-run-alert" message={t('myob.push.run.notice')} />
          {!summary.myobChecked ? <Alert type="warning" showIcon className="myob-run-alert" message={t('myob.push.run.notChecked')} /> : null}
          {summary.overwriteGuids.length > 0 ? (
            <div className="myob-run-overwrite" role="group" aria-label={t('myob.push.run.overwriteAria')}>
              <b>{t('myob.push.run.overwriteTitle', { count: summary.overwriteGuids.length })}</b>
              <div className="myob-sub">
                {overwriteNames.slice(0, NAME_PREVIEW_LIMIT).join('、')}
                {overwriteNames.length > NAME_PREVIEW_LIMIT ? t('myob.push.run.overwriteMore', { count: overwriteNames.length - NAME_PREVIEW_LIMIT }) : ''}
              </div>
              <Checkbox checked={overwrite} onChange={(event) => setOverwrite(event.target.checked)}>
                {t('myob.push.run.overwriteCheck', { count: summary.overwriteGuids.length })}
              </Checkbox>
              {!overwrite ? <div className="myob-run-excluded">{t('myob.push.run.overwriteExcluded', { count: plan.excludedGuids.length })}</div> : null}
            </div>
          ) : null}
          {summary.hintCodes.length > 0 ? (
            <Alert
              type={hintsAcknowledged ? 'success' : 'warning'}
              showIcon
              className="myob-run-alert"
              message={hintsAcknowledged ? t('myob.push.run.hintsAcknowledged') : t('myob.push.run.hintsNotAcknowledged')}
            />
          ) : null}
          {plan.userGuids.length === 0 ? <Alert type="error" showIcon className="myob-run-alert" message={t('myob.push.run.nobody')} /> : null}
        </div>
      ) : null}

      {running ? (
        <div className="myob-run" aria-live="polite">
          <Progress percent={progress.total > 0 ? Math.round((progress.done / progress.total) * 100) : 0} status="active" />
          <p className="myob-run-lead">{t('myob.push.run.progress', { done: progress.done, total: progress.total })}</p>
          <Alert type="warning" showIcon className="myob-run-alert" message={t('myob.push.run.keepOpen')} description={t('myob.push.run.stopHint')} />
        </div>
      ) : null}

      {phase === 'paused' ? (
        <div className="myob-run">
          <Alert
            type="error"
            showIcon
            className="myob-run-alert"
            message={t('myob.push.run.pausedTitle', { done: results.length, pending: pending.length })}
            description={
              <>
                <div>{errorMessageKey ? t(errorMessageKey) : runError?.message}</div>
                <div className="myob-sub">{fatal ? t('myob.push.run.fatalHint') : t('myob.push.run.pausedHint')}</div>
              </>
            }
          />
        </div>
      ) : null}

      {phase === 'done' ? (
        <div className="myob-run">
          <p className="myob-run-lead" role="status">
            {pending.length > 0 ? t('myob.push.run.doneStopped', { done: results.length, pending: pending.length }) : t('myob.push.run.doneAll', { count: results.length })}
          </p>
          <div className="myob-run-counts" role="list">
            {PUSH_COUNT_ORDER.filter(({ key }) => counts[key] > 0).map(({ key, tone }) => (
              <Tag key={key} color={tone} role="listitem">
                {t(PUSH_COUNT_LABEL_KEYS[key], { count: counts[key] })}
              </Tag>
            ))}
          </div>
          {counts.mismatch > 0 ? <Alert type="warning" showIcon className="myob-run-alert" message={t('myob.push.run.mismatchHint')} /> : null}
          {attentionRows.length > 0 ? (
            <MeasuredTable<MyobPushUserResult>
              metricId="pos-admin.myob.push-run-result"
              size="small"
              rowKey="userGuid"
              columns={resultColumns}
              dataSource={attentionRows}
              pagination={attentionRows.length > 10 ? { pageSize: 10, showSizeChanger: false } : false}
              scroll={{ x: 'max-content' }}
            />
          ) : null}
          <div className="myob-sub myob-run-range">{t('myob.push.run.reminder')}</div>
        </div>
      ) : null}
    </Modal>
  )
}
