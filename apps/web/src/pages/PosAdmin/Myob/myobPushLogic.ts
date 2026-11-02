// MYOB Timesheet 推送与撤销（方案 docs/plans/2026-10-29-myob-timesheet-push.md 第 5 节，批 2）的纯逻辑：
// 选择汇总与确认条件、分块、分块执行器（可注入 push 函数便于测试）、结果分类、状态视图、错误码到文案键。
// 不依赖 React / antd；文案键一律写成字面量，供页面文案契约测试扫描。
//
// 安全约束（和后端一致，界面只是第一道门，服务端每次都会重新校验）：
// - 客户端只传「推谁」，工时由服务端重新规划；
// - MYOB 里已有未发薪工时的员工，必须由人在确认弹窗里勾选「覆盖」才会写；不勾选的这些人本次直接不推；
// - 已发薪（Processed）的员工在预览里就是被拦截的，永远不在可选范围内。

import type {
  MyobPreviewPushState,
  MyobPreviewRow,
  MyobPushBatchSummary,
  MyobPushRequest,
  MyobPushResult,
  MyobPushStatus,
  MyobPushUserResult,
  MyobTimesheetPreview,
} from '../../../types/myob'

/** 一块最多处理的员工数（后端 MaxUsersPerChunk）。 */
export const PUSH_CHUNK_SIZE = 10

// ───────────────────────── 选择与确认 ─────────────────────────

/** 可以被选中推送的行：状态就绪（没有拦截项）且 MYOB 里没有已发薪记录。 */
export function isPushableRow(row: MyobPreviewRow): boolean {
  return row.status === 'Ready' && row.myobState !== 'Processed'
}

/** 默认全选：所有可推送的员工。 */
export function getPushableUserGuids(preview: MyobTimesheetPreview | null): string[] {
  return (preview?.rows ?? []).filter(isPushableRow).map((row) => row.userGuid)
}

/** 把已选集合收敛到现在仍然可推送的人（预览刷新后，原来选中的人可能变成被拦截）。 */
export function reconcileSelection(preview: MyobTimesheetPreview | null, selected: ReadonlySet<string>): Set<string> {
  const pushable = new Set(getPushableUserGuids(preview))
  return new Set([...selected].filter((guid) => pushable.has(guid)))
}

export interface PushSelectionSummary {
  /** 已选且可推送的人数。 */
  selectedCount: number
  /** 已选的人里，预览已知 MYOB 里有未发薪工时的人（需要勾选覆盖才会写）。 */
  overwriteGuids: string[]
  /** 已选的人命中的提示项码（去重）。 */
  hintCodes: string[]
  /** 已选的人合计的推送小时。 */
  hours: number
  /** 预览是否读过 MYOB 现状（没读过就不知道谁有未发薪工时）。 */
  myobChecked: boolean
}

/**
 * @param extraOverwriteGuids 服务端刚告诉我们「MYOB 里已有未发薪工时」的人（SkippedHasEntries）：
 *   「覆盖并重推」时预览可能没读过 MYOB 现状，这些人也要算作需要勾选覆盖。
 */
export function summarizePushSelection(
  preview: MyobTimesheetPreview,
  selected: ReadonlySet<string>,
  extraOverwriteGuids: readonly string[] = [],
): PushSelectionSummary {
  const rows = preview.rows.filter((row) => selected.has(row.userGuid) && isPushableRow(row))
  const extra = new Set(extraOverwriteGuids)
  return {
    selectedCount: rows.length,
    overwriteGuids: rows.filter((row) => row.myobState === 'HasEntries' || extra.has(row.userGuid)).map((row) => row.userGuid),
    hintCodes: [...new Set(rows.flatMap((row) => row.hintCodes))],
    hours: rows.reduce((sum, row) => sum + row.pushedTotal, 0),
    myobChecked: preview.readiness.myobChecked,
  }
}

export interface PushPlan {
  /** 本次真正要推送的人（已选 − 没勾选覆盖的有记录者）。 */
  userGuids: string[]
  /** 勾选覆盖的人。 */
  overwriteUserGuids: string[]
  /** 因为没勾选覆盖而本次不推的人。 */
  excludedGuids: string[]
  acknowledgedHintCodes: string[]
}

/**
 * 由选择 + 「覆盖」勾选算出这次的推送计划。
 * 没勾选覆盖时，MYOB 里已有未发薪工时的人直接排除（而不是交给服务端去跳过），界面上数字和实际一致。
 */
export function buildPushPlan(summary: PushSelectionSummary, selected: ReadonlySet<string>, overwrite: boolean): PushPlan {
  const overwriteSet = new Set(summary.overwriteGuids)
  const userGuids = [...selected].filter((guid) => overwrite || !overwriteSet.has(guid))
  return {
    userGuids,
    overwriteUserGuids: overwrite ? summary.overwriteGuids.filter((guid) => selected.has(guid)) : [],
    excludedGuids: overwrite ? [] : summary.overwriteGuids.filter((guid) => selected.has(guid)),
    acknowledgedHintCodes: summary.hintCodes,
  }
}

/** 这次真正要推送的人合计多少小时（确认页的数字必须和实际一致，不含因没勾覆盖而排除的人）。 */
export function sumPlanHours(preview: MyobTimesheetPreview, plan: PushPlan): number {
  const guids = new Set(plan.userGuids)
  return preview.rows.filter((row) => guids.has(row.userGuid)).reduce((sum, row) => sum + row.pushedTotal, 0)
}

/** 确认按钮是否可点：有要推的人，且（命中提示项时）预览页已勾选「已知晓」。 */
export function canConfirmPush(plan: PushPlan, summary: PushSelectionSummary, hintsAcknowledged: boolean): boolean {
  if (plan.userGuids.length === 0) {
    return false
  }
  return summary.hintCodes.length === 0 || hintsAcknowledged
}

/** 「推送到 MYOB」按钮是否可点：后端已开放 + 有推送权限 + 有选中的人 + 有提示时已勾选「已知晓」。 */
export function canOpenPushConfirm(
  preview: MyobTimesheetPreview | null,
  canPush: boolean,
  selectedCount: number,
  hintsAcknowledged: boolean,
): boolean {
  if (!preview || !canPush || !preview.pushEnabled || selectedCount <= 0) {
    return false
  }
  return preview.hints.length === 0 || hintsAcknowledged
}

// ───────────────────────── 分块与执行 ─────────────────────────

export function chunkUserGuids(userGuids: readonly string[], size = PUSH_CHUNK_SIZE): string[][] {
  const chunks: string[][] = []
  for (let index = 0; index < userGuids.length; index += size) {
    chunks.push(userGuids.slice(index, index + size))
  }
  return chunks
}

export interface PushRunBase {
  connectionGuid: string
  periodStart: string
  userGuids: string[]
  overwriteUserGuids: string[]
  acknowledgedHintCodes: string[]
  /** 重试 / 覆盖重推时沿用原批次号；省略 = 新批次。 */
  batchGuid?: string
}

export type PushFn = (request: MyobPushRequest) => Promise<MyobPushResult>

export interface PushRunProgress {
  /** 已处理（拿到结果）的人数。 */
  done: number
  total: number
  /** 第几块（从 1 开始）。 */
  chunk: number
  chunks: number
}

export type PushRunOutcome =
  | { kind: 'completed'; batchGuid: string; results: MyobPushUserResult[] }
  /** 某一块请求本身失败（网络 / 总闸 / 周期被占用 / 授权…）：已处理的结果保留，剩余未处理。 */
  | { kind: 'failed'; batchGuid: string; results: MyobPushUserResult[]; pendingUserGuids: string[]; errorCode: string | null; error: unknown }
  | { kind: 'stopped'; batchGuid: string; results: MyobPushUserResult[]; pendingUserGuids: string[] }

/** 整次运行都没法继续的错误码：重试也没用，只能先解决原因（总闸 / 授权 / 周期被占用 / 权限）。 */
const FATAL_CODES: ReadonlySet<string> = new Set([
  'MYOB_PUSH_DISABLED',
  'MYOB_PUSH_IN_PROGRESS',
  'MYOB_PUSH_AUTH_FAILED',
  'MYOB_PUSH_PERIOD_NOT_ENDED',
  'MYOB_TIMESHEET_FORBIDDEN',
])

export function isFatalPushCode(code: string | null | undefined): boolean {
  return FATAL_CODES.has((code ?? '').trim())
}

/**
 * 按块顺序推送。同一次运行的第二块起带上第一块返回的批次号；每块结束回调进度；
 * `shouldStop()` 在块与块之间检查（不打断正在进行的一块）。请求失败不抛出，而是返回 failed 并带上未处理的人，调用方可「继续」。
 */
export async function runPushChunks(
  base: PushRunBase,
  push: PushFn,
  onProgress: (progress: PushRunProgress) => void,
  shouldStop: () => boolean,
  extractCode: (error: unknown) => string | null = () => null,
): Promise<PushRunOutcome> {
  const chunks = chunkUserGuids(base.userGuids)
  const results: MyobPushUserResult[] = []
  let batchGuid = base.batchGuid ?? ''
  onProgress({ done: 0, total: base.userGuids.length, chunk: 0, chunks: chunks.length })
  for (let index = 0; index < chunks.length; index += 1) {
    if (shouldStop()) {
      return { kind: 'stopped', batchGuid, results, pendingUserGuids: chunks.slice(index).flat() }
    }
    const chunk = chunks[index]
    try {
      const response = await push({
        connectionGuid: base.connectionGuid,
        periodStart: base.periodStart,
        userGuids: chunk,
        batchGuid: batchGuid || undefined,
        // 只带这一块里需要覆盖的人
        overwriteUserGuids: base.overwriteUserGuids.filter((guid) => chunk.includes(guid)),
        acknowledgedHintCodes: base.acknowledgedHintCodes,
      })
      batchGuid = response.batchGuid || batchGuid
      results.push(...response.results)
    } catch (error) {
      return { kind: 'failed', batchGuid, results, pendingUserGuids: chunks.slice(index).flat(), errorCode: extractCode(error), error }
    }
    onProgress({ done: results.length, total: base.userGuids.length, chunk: index + 1, chunks: chunks.length })
  }
  return { kind: 'completed', batchGuid, results }
}

// ───────────────────────── 结果分类 ─────────────────────────

export type PushResultGroup = 'success' | 'skipped' | 'blocked' | 'failed' | 'attention'

/** 成功 = 已推送 / 已是最新；跳过 = 已发薪或有记录未覆盖；Mismatch 要人去 MYOB 核对（attention）。 */
export function classifyPushStatus(status: MyobPushStatus): PushResultGroup {
  switch (status) {
    case 'Pushed':
    case 'UpToDate':
    case 'Undone':
      return 'success'
    case 'SkippedProcessed':
    case 'SkippedHasEntries':
    case 'UndoBlockedProcessed':
    case 'UndoBlockedNewer':
      return 'skipped'
    case 'Blocked':
      return 'blocked'
    case 'Mismatch':
    case 'UndoConflict':
      return 'attention'
    default:
      return 'failed'
  }
}

export interface PushResultCounts {
  pushed: number
  upToDate: number
  skippedProcessed: number
  skippedHasEntries: number
  blocked: number
  failed: number
  mismatch: number
}

export function countPushResults(results: readonly MyobPushUserResult[]): PushResultCounts {
  const count = (status: MyobPushStatus) => results.filter((item) => item.status === status).length
  return {
    pushed: count('Pushed'),
    upToDate: count('UpToDate'),
    skippedProcessed: count('SkippedProcessed'),
    skippedHasEntries: count('SkippedHasEntries'),
    blocked: count('Blocked'),
    // 未列出的状态（Running 等）也算失败：宁可让人看一眼
    failed: results.filter((item) => classifyPushStatus(item.status) === 'failed').length,
    mismatch: count('Mismatch'),
  }
}

/** 可以「重试」的：失败（含周期被占用这类临时原因）。拦截项 / 已发薪不能靠重试解决。 */
export function getRetryableUserGuids(results: readonly MyobPushUserResult[]): string[] {
  return results.filter((item) => classifyPushStatus(item.status) === 'failed').map((item) => item.userGuid)
}

/** 「有记录未覆盖」的：可以让人确认后「覆盖并重推」。 */
export function getOverwriteCandidateGuids(results: readonly MyobPushUserResult[]): string[] {
  return results.filter((item) => item.status === 'SkippedHasEntries').map((item) => item.userGuid)
}

/** 合并：同一员工后来的结果覆盖先前的（重试 / 覆盖重推后列表只显示最新结果）。 */
export function mergePushResults(previous: readonly MyobPushUserResult[], next: readonly MyobPushUserResult[]): MyobPushUserResult[] {
  const byUser = new Map(previous.map((item) => [item.userGuid, item]))
  for (const item of next) {
    byUser.set(item.userGuid, item)
  }
  return [...byUser.values()]
}

// ───────────────────────── 文案键 ─────────────────────────

/** 结果汇总标签的文案键（键写成字面量，供页面文案契约测试扫描）。 */
export const PUSH_COUNT_LABEL_KEYS: Readonly<Record<keyof PushResultCounts, string>> = {
  pushed: 'myob.push.run.count.pushed',
  upToDate: 'myob.push.run.count.upToDate',
  skippedHasEntries: 'myob.push.run.count.skippedHasEntries',
  skippedProcessed: 'myob.push.run.count.skippedProcessed',
  blocked: 'myob.push.run.count.blocked',
  failed: 'myob.push.run.count.failed',
  mismatch: 'myob.push.run.count.mismatch',
}

/** 结果汇总标签的显示顺序与颜色；数量为 0 的不显示。 */
export const PUSH_COUNT_ORDER: ReadonlyArray<{ key: keyof PushResultCounts; tone: 'success' | 'default' | 'error' | 'warning' }> = [
  { key: 'pushed', tone: 'success' },
  { key: 'upToDate', tone: 'success' },
  { key: 'skippedHasEntries', tone: 'default' },
  { key: 'skippedProcessed', tone: 'default' },
  { key: 'blocked', tone: 'default' },
  { key: 'failed', tone: 'error' },
  { key: 'mismatch', tone: 'warning' },
]

export const PUSH_STATUS_LABEL_KEYS: Readonly<Record<string, string>> = {
  Pushed: 'myob.push.run.status.Pushed',
  UpToDate: 'myob.push.run.status.UpToDate',
  SkippedProcessed: 'myob.push.run.status.SkippedProcessed',
  SkippedHasEntries: 'myob.push.run.status.SkippedHasEntries',
  Blocked: 'myob.push.run.status.Blocked',
  Failed: 'myob.push.run.status.Failed',
  Mismatch: 'myob.push.run.status.Mismatch',
  Undone: 'myob.push.run.status.Undone',
  UndoConflict: 'myob.push.run.status.UndoConflict',
  UndoBlockedProcessed: 'myob.push.run.status.UndoBlockedProcessed',
  UndoBlockedNewer: 'myob.push.run.status.UndoBlockedNewer',
  Running: 'myob.push.run.status.Failed',
}

export function getPushStatusLabelKey(status: MyobPushStatus): string {
  return PUSH_STATUS_LABEL_KEYS[status] ?? 'myob.push.run.status.Failed'
}

/** antd Tag 颜色（颜色之外状态都有文字）。 */
export function getPushStatusTone(status: MyobPushStatus): 'success' | 'default' | 'warning' | 'error' {
  switch (classifyPushStatus(status)) {
    case 'success':
      return 'success'
    case 'skipped':
      return 'default'
    case 'attention':
      return 'warning'
    default:
      return 'error'
  }
}

/** 后端错误码 → 文案键（键写成字面量）。未知码返回 null，界面回退后端 message，不展示原始码。 */
export const PUSH_CODE_MESSAGE_KEYS: Readonly<Record<string, string>> = {
  MYOB_PUSH_DISABLED: 'myob.push.run.code.disabled',
  MYOB_PUSH_INVALID_REQUEST: 'myob.push.run.code.invalidRequest',
  MYOB_PUSH_PERIOD_NOT_ENDED: 'myob.push.run.code.periodNotEnded',
  MYOB_PUSH_IN_PROGRESS: 'myob.push.run.code.inProgress',
  MYOB_PUSH_BATCH_NOT_FOUND: 'myob.push.run.code.batchNotFound',
  MYOB_PUSH_NOT_READY: 'myob.push.run.code.notReady',
  MYOB_PUSH_HINT_NOT_ACKNOWLEDGED: 'myob.push.run.code.hintNotAcknowledged',
  MYOB_PUSH_AUTH_FAILED: 'myob.push.run.code.authFailed',
  MYOB_PUSH_RECORD_FAILED: 'myob.push.run.code.recordFailed',
  MYOB_PUSH_READ_FAILED: 'myob.push.run.code.readFailed',
  MYOB_PUSH_READBACK_FAILED: 'myob.push.run.code.readbackFailed',
  MYOB_PUSH_READBACK_MISMATCH: 'myob.push.run.code.readbackMismatch',
  MYOB_PUSH_STALE: 'myob.push.run.code.stale',
  MYOB_PUSH_NOTHING_TO_UNDO: 'myob.push.run.code.nothingToUndo',
  MYOB_TIMESHEET_FORBIDDEN: 'myob.push.run.code.forbidden',
  MYOB_TIMESHEET_INVALID_PERIOD: 'myob.push.run.code.invalidPeriod',
  MYOB_CONNECTION_NOT_ACTIVE: 'myob.push.run.code.connectionInactive',
  CATEGORY_NOT_MAPPED: 'myob.push.run.code.categoryNotMapped',
}

export function getPushCodeMessageKey(code: string | null | undefined): string | null {
  return PUSH_CODE_MESSAGE_KEYS[(code ?? '').trim()] ?? null
}

// ───────────────────────── 预览行的推送状态 ─────────────────────────

export interface PushStateView {
  labelKey: string
  tone: 'default' | 'success' | 'warning' | 'error' | 'processing'
  /** 悬停说明的文案键；没有为 null。 */
  tipKey: string | null
  /** 最近一次记录时间（有记录才有）。 */
  lastAt: string | null
}

export function getPushStateView(push: MyobPreviewPushState): PushStateView {
  const lastAt = push.lastPushedAt
  switch (push.state) {
    case 'Pushed':
      return { labelKey: 'myob.push.pushState.Pushed', tone: 'success', tipKey: null, lastAt }
    case 'NeedsRepush':
      return { labelKey: 'myob.push.pushState.NeedsRepush', tone: 'warning', tipKey: 'myob.push.pushState.needsRepushTip', lastAt }
    case 'ProcessedSkipped':
      return { labelKey: 'myob.push.pushState.ProcessedSkipped', tone: 'default', tipKey: 'myob.push.pushState.processedTip', lastAt }
    case 'Failed':
      return { labelKey: 'myob.push.pushState.Failed', tone: 'error', tipKey: 'myob.push.pushState.failedTip', lastAt }
    case 'Mismatch':
      return { labelKey: 'myob.push.pushState.Mismatch', tone: 'warning', tipKey: 'myob.push.pushState.mismatchTip', lastAt }
    default:
      return { labelKey: 'myob.push.pushState.NotPushed', tone: 'default', tipKey: null, lastAt: null }
  }
}

// ───────────────────────── 推送记录与撤销 ─────────────────────────

/** 批次是否还能撤销：有真正写过 MYOB 且没撤销的员工。 */
export function canUndoBatch(batch: MyobPushBatchSummary): boolean {
  return batch.undoable > 0
}

export interface BatchOutcomeChip {
  key: 'pushed' | 'upToDate' | 'skipped' | 'failed' | 'mismatch' | 'undone'
  labelKey: string
  count: number
}

/** 批次汇总里数量大于 0 的结局，按重要程度排序（失败 / 待核对排前）。 */
export function getBatchOutcomeChips(batch: MyobPushBatchSummary): BatchOutcomeChip[] {
  const chips: BatchOutcomeChip[] = [
    { key: 'failed', labelKey: 'myob.push.batches.chip.failed', count: batch.failed },
    { key: 'mismatch', labelKey: 'myob.push.batches.chip.mismatch', count: batch.mismatch },
    { key: 'pushed', labelKey: 'myob.push.batches.chip.pushed', count: batch.pushed },
    { key: 'upToDate', labelKey: 'myob.push.batches.chip.upToDate', count: batch.upToDate },
    { key: 'skipped', labelKey: 'myob.push.batches.chip.skipped', count: batch.skipped },
    { key: 'undone', labelKey: 'myob.push.batches.chip.undone', count: batch.undone },
  ]
  return chips.filter((chip) => chip.count > 0)
}

/** 撤销结果的整体结论：全部撤销 / 部分撤销 / 一个都没撤销。 */
export function summarizeUndo(results: readonly MyobPushUserResult[]): { kind: 'all' | 'partial' | 'none'; undone: number; total: number } {
  const undone = results.filter((item) => item.status === 'Undone').length
  const total = results.length
  return { kind: total > 0 && undone === total ? 'all' : undone > 0 ? 'partial' : 'none', undone, total }
}
