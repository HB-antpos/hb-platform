import dayjs from 'dayjs'

import type { FaceCaptureResult, FaceHitResult, FaceHitResultQuery, FaceHitStatus, FaceHitSummary } from '../../../types/faceMatch'
import { resultsRangeToUtc, type ResultsTimeRange } from './logic'

// 「名单命中」视图（以命中任务为主表）的纯逻辑：状态筛选、查询条件、命中行 → 详情抽屉的形状、缩略图编号。
// 单独成文件：复核规则（logic.ts）不依赖「自动确认」等展示字段，契约测试按文件守住这一点。

// ───────────── 名单命中图缩略图 ─────────────

/** 本页需要的命中图缩略图任务编号：去重、保持顺序，最多 limit 个（后端一批上限 30，一页最多 20 行）。 */
export function collectThumbnailTaskIds(taskIds: readonly (string | null | undefined)[], limit = 30): string[] {
  const result: string[] = []
  for (const id of taskIds) {
    if (!id || result.includes(id)) continue
    result.push(id)
    if (result.length >= limit) break
  }
  return result
}

// ───────────── 名单命中视图（以命中任务为主表） ─────────────

/** 状态筛选的顺序即分段按钮的顺序；取值与后端 FaceHitStatuses 一致。 */
export const HIT_STATUS_VALUES: readonly FaceHitStatus[] = ['all', 'unread', 'pending', 'autoConfirmed', 'confirmed', 'dismissed']

export const HIT_STATUS_LABEL_KEYS: Record<FaceHitStatus, string> = {
  all: 'faceMatch.hits.status.all',
  unread: 'faceMatch.hits.status.unread',
  pending: 'faceMatch.hits.status.pending',
  autoConfirmed: 'faceMatch.hits.status.autoConfirmed',
  confirmed: 'faceMatch.hits.status.confirmed',
  dismissed: 'faceMatch.hits.status.dismissed',
}

/** 某个状态在汇总里的条数；还没有汇总（加载中 / 失败）时为 null，按钮上不显示数字。 */
export function hitStatusCount(summary: FaceHitSummary | null | undefined, status: FaceHitStatus): number | null {
  if (!summary) return null
  return status === 'all' ? summary.total : summary[status]
}

export interface HitsFilters {
  storeCode: string | null
  status: FaceHitStatus
  range: ResultsTimeRange | null
}

/** 默认：最近 7 天（与命中图保留期一致）。 */
export function defaultHitsRange(now: Date = new Date()): ResultsTimeRange {
  const day = dayjs(now)
  return { start: day.subtract(6, 'day').startOf('day'), end: day.endOf('day').startOf('minute') }
}

export function buildHitsQuery(filters: HitsFilters, page: number, pageSize: number): FaceHitResultQuery {
  return {
    storeCode: filters.storeCode || undefined,
    hitStatus: HIT_STATUS_VALUES.includes(filters.status) && filters.status !== 'all' ? filters.status : undefined,
    ...resultsRangeToUtc(filters.range),
    page,
    pageSize,
  }
}

/** 汇总的查询条件只有门店与时间（状态与分页被后端忽略）。 */
export function buildHitSummaryQuery(filters: Pick<HitsFilters, 'storeCode' | 'range'>): Pick<FaceHitResultQuery, 'storeCode' | 'fromUtc' | 'toUtc'> {
  return { storeCode: filters.storeCode || undefined, ...resultsRangeToUtc(filters.range) }
}

/**
 * 命中行 → 抓拍详情抽屉要的形状（复用同一个抽屉，origin='hits'）。命中流不返回抓拍图片编号：抽屉在这里只读命中图
 * （每次读取服务端写审计），抓拍原图到「全部抓拍」里看；判定与分数来自命中流里对应的抓拍记录（没有则未评估）。
 */
export function hitToCaptureResult(hit: FaceHitResult): FaceCaptureResult {
  return {
    captureId: hit.captureId ?? hit.taskId,
    storeCode: hit.storeCode,
    storeName: hit.storeName,
    cameraId: hit.cameraId,
    capturedAtUtc: hit.capturedAtUtc,
    faceCount: hit.faceCount ?? 0,
    isDuplicate: false,
    evaluated: hit.decision !== null,
    decision: hit.decision,
    staffScorePercent: hit.staffScorePercent,
    visitScorePercent: hit.visitScorePercent,
    faceImageId: null,
    sceneImageId: null,
    hit: {
      taskId: hit.taskId,
      scorePercent: hit.scorePercent,
      band: hit.band,
      reviewStatus: hit.reviewStatus,
      reviewStatusName: hit.reviewStatusName,
      imageAvailable: hit.imageAvailable,
      notifyState: hit.notifyState,
      notifiedAtUtc: hit.notifiedAtUtc,
      notifyRecipientCount: hit.notifyRecipientCount,
      notifyReadCount: hit.notifyReadCount,
      autoConfirmed: hit.autoConfirmed,
      unread: hit.unread,
    },
  }
}
