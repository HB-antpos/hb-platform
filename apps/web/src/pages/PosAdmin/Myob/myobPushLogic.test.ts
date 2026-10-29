import assert from 'node:assert/strict'

import {
  normalizeMyobPreviewRow,
  normalizeMyobPushBatchDetail,
  normalizeMyobPushBatches,
  normalizeMyobPushResult,
  normalizeMyobPushStatus,
  normalizeMyobTimesheetPreview,
} from '../../../services/myobService'
import type { MyobPushRequest, MyobPushResult, MyobPushUserResult, MyobTimesheetPreview } from '../../../types/myob'
import {
  PUSH_CHUNK_SIZE,
  PUSH_CODE_MESSAGE_KEYS,
  PUSH_STATUS_LABEL_KEYS,
  buildPushPlan,
  canConfirmPush,
  canOpenPushConfirm,
  canUndoBatch,
  chunkUserGuids,
  classifyPushStatus,
  countPushResults,
  getBatchOutcomeChips,
  getOverwriteCandidateGuids,
  getPushCodeMessageKey,
  getPushStateView,
  getPushStatusLabelKey,
  getPushStatusTone,
  getPushableUserGuids,
  getRetryableUserGuids,
  isFatalPushCode,
  isPushableRow,
  mergePushResults,
  reconcileSelection,
  runPushChunks,
  sumPlanHours,
  summarizePushSelection,
  summarizeUndo,
} from './myobPushLogic'

// ───────────── 夹具 ─────────────

function row(userGuid: string, overrides: Record<string, unknown> = {}) {
  return {
    userGuid,
    userName: userGuid.toUpperCase(),
    status: 'Ready',
    pushedTotal: 10,
    myobState: 'Clear',
    hintCodes: [],
    blockers: [],
    ...overrides,
  }
}

function preview(rows: Array<Record<string, unknown>>, overrides: Record<string, unknown> = {}): MyobTimesheetPreview {
  return normalizeMyobTimesheetPreview({
    connectionGuid: 'c1',
    connectionName: 'Co',
    periodStart: '2026-10-26',
    periodEnd: '2026-11-01',
    pushEnabled: true,
    readiness: { myobChecked: true },
    rows,
    hints: [],
    ...overrides,
  })
}

// ───────────── 归一化：未知状态按失败处理 ─────────────

assert.equal(normalizeMyobPushStatus('Pushed'), 'Pushed')
assert.equal(normalizeMyobPushStatus('SomethingNew'), 'Failed', '不认识的状态按失败处理，绝不显示成已推送')
assert.equal(normalizeMyobPushStatus(undefined), 'Failed')

const rowWithPush = normalizeMyobPreviewRow({ userGuid: 'u1', status: 'Ready', push: { state: 'Pushed', needsRepush: true, lastStatus: 'Pushed', lastBatchGuid: 'b1', lastPushedAt: '2026-10-30T01:00:00Z' } })!
assert.equal(rowWithPush.push.state, 'NeedsRepush', 'needsRepush 为真时一定显示需重新推送')
assert.equal(normalizeMyobPreviewRow({ userGuid: 'u2', status: 'Ready' })!.push.state, 'NotPushed', '后端没给 push（旧版本）按未推送')
assert.equal(normalizeMyobPreviewRow({ userGuid: 'u3', status: 'Ready', push: { state: 'Weird' } })!.push.state, 'NotPushed')

const pushResult = normalizeMyobPushResult({
  batchGuid: 'b1',
  results: [{ userGuid: 'u1', userName: 'Mei', status: 'Pushed', pushedHours: 12.5 }, { status: 'Pushed' }, { userGuid: 'u2', status: '???' }],
})
assert.equal(pushResult.results.length, 2, '缺 userGuid 的结果丢弃')
assert.equal(pushResult.results[1].status, 'Failed')
assert.equal(pushResult.results[0].pushedHours, 12.5)
assert.deepEqual(normalizeMyobPushBatches(null), [])
assert.equal(normalizeMyobPushBatches([{ batchGuid: 'b1', undoable: 2, pushed: 2, total: 2 }, { total: 3 }]).length, 1)
const detail = normalizeMyobPushBatchDetail({ summary: { batchGuid: 'b1' }, items: [{ userGuid: 'u1', status: 'Undone', errorStatus: 400 }] })
assert.equal(detail.items[0].errorStatus, 400)
assert.equal(normalizeMyobPushBatchDetail({}).summary.batchGuid, 'unknown', '后端没给汇总时也不崩')

// ───────────── 选择与确认 ─────────────

const base = preview([
  row('ready'),
  row('has-entries', { myobState: 'HasEntries', pushedTotal: 5 }),
  row('processed', { myobState: 'Processed' }),
  row('blocked', { status: 'Blocked', blockers: [{ code: 'NOT_BOUND', message: 'x' }] }),
  row('no-transfer', { status: 'NoTransfer', pushedTotal: 0 }),
  row('hinted', { hintCodes: ['BIRTHDAY_REVIEW'], pushedTotal: 2.5 }),
])
assert.equal(isPushableRow(base.rows[0]), true)
assert.equal(isPushableRow(base.rows[2]), false, 'MYOB 里已发薪的永远不可选')
assert.equal(isPushableRow(base.rows[3]), false)
assert.equal(isPushableRow(base.rows[4]), false)
assert.deepEqual(getPushableUserGuids(base), ['ready', 'has-entries', 'hinted'])
assert.deepEqual(getPushableUserGuids(null), [])
assert.deepEqual([...reconcileSelection(base, new Set(['ready', 'blocked', 'gone']))], ['ready'], '刷新后变成被拦截 / 不存在的人从选择里去掉')

const all = new Set(getPushableUserGuids(base))
const summary = summarizePushSelection(base, all)
assert.equal(summary.selectedCount, 3)
assert.deepEqual(summary.overwriteGuids, ['has-entries'])
assert.deepEqual(summary.hintCodes, ['BIRTHDAY_REVIEW'])
assert.equal(summary.hours, 17.5)
assert.equal(summary.myobChecked, true)
assert.equal(summarizePushSelection(preview([row('a')], { readiness: { myobChecked: false } }), new Set(['a'])).myobChecked, false)
// 服务端告诉我们有记录的人（预览没读过 MYOB 现状时）也要算作需要勾选覆盖
const unchecked = preview([row('a'), row('b')], { readiness: { myobChecked: false } })
assert.deepEqual(summarizePushSelection(unchecked, new Set(['a', 'b']), ['b']).overwriteGuids, ['b'])
assert.deepEqual(summarizePushSelection(unchecked, new Set(['a']), ['b']).overwriteGuids, [], '没被选中的人不算')

// 不勾覆盖：有记录的人本次不推（不是交给服务端去跳过），数字和实际一致
const noOverwrite = buildPushPlan(summary, all, false)
assert.deepEqual(noOverwrite.userGuids, ['ready', 'hinted'])
assert.deepEqual(noOverwrite.overwriteUserGuids, [])
assert.deepEqual(noOverwrite.excludedGuids, ['has-entries'])
// 勾了覆盖：全部推，覆盖名单只含有记录的人
const withOverwrite = buildPushPlan(summary, all, true)
assert.deepEqual(withOverwrite.userGuids.sort(), ['has-entries', 'hinted', 'ready'])
assert.deepEqual(withOverwrite.overwriteUserGuids, ['has-entries'])
assert.deepEqual(withOverwrite.excludedGuids, [])
// 只选了没有记录的人：覆盖勾选对结果没影响
const onlyReady = new Set(['ready'])
assert.deepEqual(buildPushPlan(summarizePushSelection(base, onlyReady), onlyReady, true).overwriteUserGuids, [])

// 确认页的小时数只算真正要推的人：没勾覆盖时，被排除的有记录者不计入
assert.equal(sumPlanHours(base, noOverwrite), 12.5, 'ready 10 + hinted 2.5，不含被排除的 has-entries（5）')
assert.equal(sumPlanHours(base, withOverwrite), 17.5)

// 确认条件：有人可推 + 命中提示时必须已勾选「已知晓」
assert.equal(canConfirmPush(noOverwrite, summary, false), false, '命中提示项但没勾已知晓')
assert.equal(canConfirmPush(noOverwrite, summary, true), true)
const onlyOverwriteUser = new Set(['has-entries'])
const onlyOverwriteSummary = summarizePushSelection(base, onlyOverwriteUser)
assert.equal(canConfirmPush(buildPushPlan(onlyOverwriteSummary, onlyOverwriteUser, false), onlyOverwriteSummary, true), false, '选的全是有记录的人又没勾覆盖 → 没有要推的人')

// 打开确认弹窗的条件
assert.equal(canOpenPushConfirm(base, true, 3, true), true)
assert.equal(canOpenPushConfirm(base, false, 3, true), false, '没有推送权限')
assert.equal(canOpenPushConfirm(preview([row('a')], { pushEnabled: false }), true, 1, true), false, '总闸关 / 不在白名单')
assert.equal(canOpenPushConfirm(base, true, 0, true), false)
assert.equal(canOpenPushConfirm(preview([row('a')], { hints: [{ code: 'BIRTHDAY_REVIEW', userGuid: 'a', userName: 'A', message: 'm' }] }), true, 1, false), false, '有提示项但没勾已知晓')
assert.equal(canOpenPushConfirm(null, true, 1, true), false)

// ───────────── 分块 ─────────────

assert.equal(PUSH_CHUNK_SIZE, 10)
assert.deepEqual(chunkUserGuids([]), [])
assert.deepEqual(chunkUserGuids(['a', 'b', 'c'], 2), [['a', 'b'], ['c']])
const twentyFive = Array.from({ length: 25 }, (_, index) => `u${index}`)
assert.deepEqual(chunkUserGuids(twentyFive).map((chunk) => chunk.length), [10, 10, 5])

// ───────────── 分块执行器 ─────────────

function okResult(request: MyobPushRequest, batchGuid = 'batch-1', status: MyobPushUserResult['status'] = 'Pushed'): MyobPushResult {
  return {
    batchGuid,
    connectionGuid: request.connectionGuid,
    periodStart: request.periodStart,
    results: request.userGuids.map((userGuid) => ({ userGuid, userName: userGuid, status, code: null, message: null, pushedHours: 1 })),
  }
}

const runBase = { connectionGuid: 'c1', periodStart: '2026-10-26', userGuids: twentyFive, overwriteUserGuids: ['u3', 'u12', 'u24'], acknowledgedHintCodes: ['H'] }

{
  const calls: MyobPushRequest[] = []
  const progress: number[] = []
  const outcome = await runPushChunks(
    runBase,
    async (request) => {
      calls.push(request)
      return okResult(request)
    },
    (item) => progress.push(item.done),
    () => false,
  )
  assert.equal(outcome.kind, 'completed')
  assert.equal(outcome.results.length, 25)
  assert.equal(outcome.batchGuid, 'batch-1')
  assert.deepEqual(calls.map((call) => call.userGuids.length), [10, 10, 5])
  assert.equal(calls[0].batchGuid, undefined, '第一块不带批次号')
  assert.deepEqual(calls.slice(1).map((call) => call.batchGuid), ['batch-1', 'batch-1'], '第二块起沿用第一块返回的批次号')
  assert.deepEqual(calls.map((call) => call.overwriteUserGuids), [['u3'], ['u12'], ['u24']], '每块只带这一块里需要覆盖的人')
  assert.ok(calls.every((call) => call.acknowledgedHintCodes[0] === 'H'))
  assert.deepEqual(progress, [0, 10, 20, 25])
}

{
  // 第二块请求失败：已处理的结果保留，剩余未处理，错误码带出
  let count = 0
  const outcome = await runPushChunks(
    runBase,
    async (request) => {
      count += 1
      if (count === 2) {
        throw Object.assign(new Error('boom'), { code: 'MYOB_PUSH_IN_PROGRESS' })
      }
      return okResult(request)
    },
    () => undefined,
    () => false,
    (error) => (error as { code?: string }).code ?? null,
  )
  assert.equal(outcome.kind, 'failed')
  if (outcome.kind === 'failed') {
    assert.equal(outcome.results.length, 10)
    assert.equal(outcome.pendingUserGuids.length, 15)
    assert.equal(outcome.errorCode, 'MYOB_PUSH_IN_PROGRESS')
    assert.equal(outcome.batchGuid, 'batch-1')
    // 「继续」：用返回的批次号重跑剩余的人，结果合并后是完整的 25 人
    const resumed = await runPushChunks({ ...runBase, userGuids: outcome.pendingUserGuids, batchGuid: outcome.batchGuid }, async (request) => okResult(request), () => undefined, () => false)
    assert.equal(resumed.kind, 'completed')
    assert.equal(mergePushResults(outcome.results, resumed.results).length, 25)
    assert.equal(resumed.batchGuid, 'batch-1')
  }
}

{
  // 用户点「停止」：只在块与块之间生效，已处理的保留
  let calls = 0
  const outcome = await runPushChunks(runBase, async (request) => { calls += 1; return okResult(request) }, () => undefined, () => calls >= 1)
  assert.equal(outcome.kind, 'stopped')
  if (outcome.kind === 'stopped') {
    assert.equal(outcome.results.length, 10)
    assert.equal(outcome.pendingUserGuids.length, 15)
  }
  assert.equal(calls, 1)
}

{
  // 一开始就停：没发任何请求
  let calls = 0
  const outcome = await runPushChunks(runBase, async (request) => { calls += 1; return okResult(request) }, () => undefined, () => true)
  assert.equal(outcome.kind, 'stopped')
  assert.equal(calls, 0)
}

assert.equal(isFatalPushCode('MYOB_PUSH_DISABLED'), true)
assert.equal(isFatalPushCode('MYOB_PUSH_IN_PROGRESS'), true)
assert.equal(isFatalPushCode('MYOB_PUSH_AUTH_FAILED'), true)
assert.equal(isFatalPushCode('MYOB_TIMESHEET_FORBIDDEN'), true)
assert.equal(isFatalPushCode(null), false)
assert.equal(isFatalPushCode('NETWORK'), false, '网络错误等可以重试 / 继续')

// ───────────── 结果分类 ─────────────

assert.equal(classifyPushStatus('Pushed'), 'success')
assert.equal(classifyPushStatus('UpToDate'), 'success')
assert.equal(classifyPushStatus('SkippedProcessed'), 'skipped')
assert.equal(classifyPushStatus('SkippedHasEntries'), 'skipped')
assert.equal(classifyPushStatus('Blocked'), 'blocked')
assert.equal(classifyPushStatus('Mismatch'), 'attention', '回读不一致要人去 MYOB 核对，不能算成功')
assert.equal(classifyPushStatus('Failed'), 'failed')
assert.equal(classifyPushStatus('Running'), 'failed', '没有结局的状态不当作成功')

const mixed: MyobPushUserResult[] = (['Pushed', 'Pushed', 'UpToDate', 'SkippedProcessed', 'SkippedHasEntries', 'Blocked', 'Failed', 'Mismatch'] as const).map((status, index) => ({
  userGuid: `u${index}`, userName: `U${index}`, status, code: null, message: null, pushedHours: 1,
}))
assert.deepEqual(countPushResults(mixed), { pushed: 2, upToDate: 1, skippedProcessed: 1, skippedHasEntries: 1, blocked: 1, failed: 1, mismatch: 1 })
assert.deepEqual(getRetryableUserGuids(mixed), ['u6'], '只有失败可以重试；拦截 / 已发薪 / 待核对不能靠重试解决')
assert.deepEqual(getOverwriteCandidateGuids(mixed), ['u4'])
const merged = mergePushResults(mixed, [{ ...mixed[6], status: 'Pushed' }])
assert.equal(merged.length, mixed.length)
assert.equal(merged.find((item) => item.userGuid === 'u6')!.status, 'Pushed', '重试后列表只显示最新结果')

// ───────────── 文案键与状态视图 ─────────────

for (const status of ['Pushed', 'UpToDate', 'SkippedProcessed', 'SkippedHasEntries', 'Blocked', 'Failed', 'Mismatch', 'Undone', 'UndoConflict', 'UndoBlockedProcessed', 'UndoBlockedNewer'] as const) {
  assert.ok(getPushStatusLabelKey(status).startsWith('myob.push.run.status.'), status)
  assert.ok(PUSH_STATUS_LABEL_KEYS[status])
}
assert.equal(getPushStatusTone('Pushed'), 'success')
assert.equal(getPushStatusTone('Mismatch'), 'warning')
assert.equal(getPushStatusTone('Failed'), 'error')
assert.equal(getPushStatusTone('SkippedProcessed'), 'default')
assert.equal(getPushCodeMessageKey('MYOB_PUSH_DISABLED'), 'myob.push.run.code.disabled')
assert.equal(getPushCodeMessageKey('DateOutOfRange'), null, 'MYOB 的错误类型名不当文案展示')
assert.equal(getPushCodeMessageKey(undefined), null)
assert.ok(Object.values(PUSH_CODE_MESSAGE_KEYS).every((key) => key.startsWith('myob.push.run.code.')))

const states = ['NotPushed', 'Pushed', 'NeedsRepush', 'ProcessedSkipped', 'Failed', 'Mismatch'] as const
for (const state of states) {
  const view = getPushStateView({ state, needsRepush: state === 'NeedsRepush', lastStatus: null, lastBatchGuid: null, lastPushedAt: '2026-10-30T01:00:00Z' })
  assert.equal(view.labelKey, `myob.push.pushState.${state}`)
}
assert.equal(getPushStateView({ state: 'Pushed', needsRepush: false, lastStatus: 'Pushed', lastBatchGuid: 'b', lastPushedAt: 't' }).tone, 'success')
assert.equal(getPushStateView({ state: 'NeedsRepush', needsRepush: true, lastStatus: 'Pushed', lastBatchGuid: 'b', lastPushedAt: 't' }).tone, 'warning')
assert.ok(getPushStateView({ state: 'NeedsRepush', needsRepush: true, lastStatus: null, lastBatchGuid: null, lastPushedAt: null }).tipKey, '需重新推送要解释原因（MYOB 里还留着旧工时）')
assert.equal(getPushStateView({ state: 'NotPushed', needsRepush: false, lastStatus: 'Undone', lastBatchGuid: 'b', lastPushedAt: 't' }).lastAt, null, '未推送不显示上次时间')

// ───────────── 推送记录 ─────────────

const batch = { batchGuid: 'b', startedAt: 't', actorUserGuid: null, actorName: null, total: 6, pushed: 2, upToDate: 1, skipped: 1, failed: 1, mismatch: 0, undone: 1, undoable: 2 }
assert.equal(canUndoBatch(batch), true)
assert.equal(canUndoBatch({ ...batch, undoable: 0 }), false, '没有真正写过 MYOB 的批次没有撤销按钮')
assert.deepEqual(getBatchOutcomeChips(batch).map((chip) => chip.key), ['failed', 'pushed', 'upToDate', 'skipped', 'undone'], '失败 / 待核对排前，数量为 0 的不显示')
assert.deepEqual(getBatchOutcomeChips({ ...batch, pushed: 0, upToDate: 0, skipped: 0, failed: 0, undone: 0 }), [])

const undoResults = (statuses: MyobPushUserResult['status'][]): MyobPushUserResult[] =>
  statuses.map((status, index) => ({ userGuid: `u${index}`, userName: 'x', status, code: null, message: null, pushedHours: 0 }))
assert.deepEqual(summarizeUndo(undoResults(['Undone', 'Undone'])), { kind: 'all', undone: 2, total: 2 })
assert.deepEqual(summarizeUndo(undoResults(['Undone', 'UndoBlockedProcessed'])), { kind: 'partial', undone: 1, total: 2 })
assert.deepEqual(summarizeUndo(undoResults(['UndoConflict'])), { kind: 'none', undone: 0, total: 1 })
assert.deepEqual(summarizeUndo([]), { kind: 'none', undone: 0, total: 0 })

console.log('myobPushLogic.test: ok')
