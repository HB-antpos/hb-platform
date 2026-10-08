/**
 * 拣货派单的纯逻辑：分段品种数调整、员工配色、分单打印的换排提示。
 * 分段本身由后端按 M 型走位切（预览接口），这里只负责经理在窗口里加减品种数时的边界移动。
 */

/** 员工分段配色：蓝、橙、青、紫、品红、深绿，按段号循环；与白字对比度都在 4.5:1 以上。 */
export const PICKER_SEGMENT_COLORS = ['#1677ff', '#d46b08', '#08979c', '#9254de', '#c41d7f', '#389e0d'] as const

export function segmentColor(segmentNo: number): string {
  const index = (Math.max(1, segmentNo) - 1) % PICKER_SEGMENT_COLORS.length
  return PICKER_SEGMENT_COLORS[index]
}

/** 按品种数平均分：除不尽的余数从排在前面的人开始每人多一个（与后端 SplitEvenly 一致）。 */
export function evenSegmentCounts(lineCount: number, pickerCount: number): number[] {
  if (pickerCount <= 0) return []
  const base = Math.floor(lineCount / pickerCount)
  const remainder = lineCount % pickerCount
  return Array.from({ length: pickerCount }, (_, index) => base + (index < remainder ? 1 : 0))
}

/**
 * 加减某一段的品种数：只移动它和相邻一段之间的边界，合计不变、每段仍是走位上的连续区间。
 * 加一个从后一段挪过来（最后一段从前一段挪）；减一个还给后一段（最后一段还给前一段）。
 * 相邻段已经是 0 个、或本段已经是 0 个时不变，返回原数组。
 */
export function adjustSegmentCount(counts: readonly number[], index: number, delta: 1 | -1): readonly number[] {
  if (counts.length < 2 || index < 0 || index >= counts.length) return counts
  const neighbor = index === counts.length - 1 ? index - 1 : index + 1
  const next = [...counts]
  if (delta === 1) {
    if (next[neighbor] <= 0) return counts
    next[neighbor] -= 1
    next[index] += 1
  } else {
    if (next[index] <= 0) return counts
    next[index] -= 1
    next[neighbor] += 1
  }
  return next
}

/** 各段品种数与员工顺序对得上、合计等于订单品种数，才能拿去预览。 */
export function segmentCountsValid(counts: readonly number[], pickerCount: number, lineCount: number): boolean {
  return (
    counts.length === pickerCount &&
    counts.every((count) => Number.isInteger(count) && count >= 0) &&
    counts.reduce((sum, count) => sum + count, 0) === lineCount
  )
}

/**
 * 订单详情里「拣货分配」卡片是否显示。
 * 只读订单（已完成、已取消等不能再派单）且没有任何分配时，卡片里既没有数据也没有可做的操作，整块隐藏；
 * 能派单的订单（已提交、配货中）即使还没分配也要显示，因为订货明细里只有这张卡片能发起「分配拣货」。
 */
export function shouldShowPickingAssignmentSection(input: { assigneeCount: number; assignable: boolean }): boolean {
  return input.assignable || input.assigneeCount > 0
}

export const MAX_SEGMENTS = 10

/**
 * 改份数：保留前面各段已指定的员工，多出来的段待扫码领取（null），少了就截掉末尾的段。
 * 份数限定在 1–10（与后端一致）。
 */
export function resizeSegmentPickers(pickers: readonly (string | null)[], count: number): (string | null)[] {
  const target = Math.min(MAX_SEGMENTS, Math.max(1, Math.trunc(count)))
  return Array.from({ length: target }, (_, index) => pickers[index] ?? null)
}

/** 给某一段指定员工：同一个人不能同时负责两段，指给新段时从原来那段撤下（原段变为待领取）。 */
export function assignSegmentPicker(pickers: readonly (string | null)[], index: number, pickerUserGuid: string | null): (string | null)[] {
  return pickers.map((current, position) => {
    if (position === index) return pickerUserGuid
    return pickerUserGuid && current === pickerUserGuid ? null : current
  })
}

export interface SlipLineLocation {
  zone?: string | null
  rowLabel?: string | null
}

/**
 * 分单打印的换排提示：返回每一行之前要不要插一行“转入 X 区 NN 排”。
 * 第一行有货位时也提示；无货位或编码不规范（没有区排）的行不提示，第一次出现时插一行“无货位 · 按商品找”。
 */
export function slipTurnMarkers(lines: readonly SlipLineLocation[]): ({ kind: 'row'; zone: string; rowLabel: string; first: boolean } | { kind: 'unlocated' } | null)[] {
  let previousKey: string | null = null
  let unlocatedShown = false
  return lines.map((line, index) => {
    if (line.zone && line.rowLabel) {
      const key = `${line.zone}-${line.rowLabel}`
      if (key === previousKey) return null
      previousKey = key
      return { kind: 'row', zone: line.zone, rowLabel: line.rowLabel, first: index === 0 }
    }
    previousKey = null
    if (unlocatedShown) return null
    unlocatedShown = true
    return { kind: 'unlocated' }
  })
}

/**
 * 解析后端的 *AtUtc 字段：库里读出的 DateTime 序列化时不带 Z，直接 Date.parse 会被当成浏览器本地时间
 * （悉尼差 10 小时）。缺时区标记时按 UTC 解析；无效值返回 NaN。
 */
export function parseUtcMs(value: string | null | undefined): number {
  if (!value) return Number.NaN
  const normalized = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`
  return Date.parse(normalized)
}

/** UTC 时间按浏览器本地时区格式化成 MM-DD HH:mm；空值或无效值显示 —。 */
export function formatUtcShort(value: string | null | undefined): string {
  const ms = parseUtcMs(value)
  if (!Number.isFinite(ms)) return '—'
  const date = new Date(ms)
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

export interface AssigneeProgressInput {
  lineCount: number
  completedLineCount?: number | null
  stockoutLineCount?: number | null
  lastActiveAtUtc?: string | null
  pickedPieces?: number | null
}

export type AssigneeStatus =
  | { kind: 'done'; stockoutLineCount: number }
  | { kind: 'picking'; minutesAgo: number }
  | { kind: 'helped' }
  | { kind: 'idle' }

/**
 * 每人进度状态：拣齐与标了没货的品种合计覆盖整段即“已拣完”；否则有过拣货操作即“拣货中”（几分钟前），
 * 负责人没动过但已有人帮拣为“有人帮拣中”，都没有为“未开始”。
 */
export function assigneeStatus(assignee: AssigneeProgressInput, nowMs: number): AssigneeStatus {
  const completed = assignee.completedLineCount ?? 0
  const stockout = assignee.stockoutLineCount ?? 0
  if (assignee.lineCount > 0 && completed + stockout >= assignee.lineCount) {
    return { kind: 'done', stockoutLineCount: stockout }
  }
  const activeAt = parseUtcMs(assignee.lastActiveAtUtc)
  if (Number.isFinite(activeAt)) {
    return { kind: 'picking', minutesAgo: Math.max(0, Math.floor((nowMs - activeAt) / 60000)) }
  }
  // 负责人自己没动过，但这段已经有人拣了（先拣完的同事来帮忙）：不能显示“未开始”。
  if ((assignee.pickedPieces ?? 0) > 0) return { kind: 'helped' }
  return { kind: 'idle' }
}

export type SlipRow<T extends SlipLineLocation> =
  | { kind: 'turn'; zone: string; rowLabel: string; first: boolean }
  | { kind: 'unlocated' }
  | { kind: 'line'; index: number; line: T }

/** 分单明细行：在换排处与无货位段开头插入提示行，行号按商品从 1 起。 */
export function buildSlipRows<T extends SlipLineLocation>(lines: readonly T[]): SlipRow<T>[] {
  const markers = slipTurnMarkers(lines)
  const rows: SlipRow<T>[] = []
  lines.forEach((line, index) => {
    const marker = markers[index]
    if (marker?.kind === 'row') rows.push({ kind: 'turn', zone: marker.zone, rowLabel: marker.rowLabel, first: marker.first })
    else if (marker?.kind === 'unlocated') rows.push({ kind: 'unlocated' })
    rows.push({ kind: 'line', index: index + 1, line })
  })
  return rows
}

/**
 * 分单分页：第一页有页头与条码，放得少；之后每页放得多。提示行不单独落在页尾（挪到下一页开头）。
 * 至少返回一页（空段也打一页，方便员工知道没有要拣的）。
 */
export function paginateSlipRows<T>(rows: readonly T[], firstPageRows: number, nextPageRows: number, isMarker: (row: T) => boolean): T[][] {
  const pages: T[][] = []
  let cursor = 0
  while (cursor < rows.length) {
    const capacity = pages.length === 0 ? firstPageRows : nextPageRows
    let end = Math.min(rows.length, cursor + Math.max(1, capacity))
    if (end < rows.length && end - 1 > cursor && isMarker(rows[end - 1])) end -= 1
    pages.push(rows.slice(cursor, end))
    cursor = end
  }
  return pages.length > 0 ? pages : [[]]
}

/**
 * 分单“同单其他段”文案：有负责人写姓名，待领取的段用调用方按界面语言给的文案。
 * 新后端给结构化的 otherSegments；旧后端只有 otherPickerNames（待领取是后端拼的中文占位串），回退原样输出。
 */
export function otherSegmentLabels(
  slip: { otherPickerNames: readonly string[]; otherSegments?: readonly { segmentNo: number; pickerName?: string | null }[] },
  unclaimedLabel: (segmentNo: number) => string,
): string[] {
  if (!slip.otherSegments) return [...slip.otherPickerNames]
  return slip.otherSegments.map((segment) => segment.pickerName?.trim() || unclaimedLabel(segment.segmentNo))
}
