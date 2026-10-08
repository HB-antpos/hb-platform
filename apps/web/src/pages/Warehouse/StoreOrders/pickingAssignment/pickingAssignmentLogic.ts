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
 * 分单打印的提示行：返回每一行之前要不要插一行。
 * 只在第一行有货位时提示起始排（“X 区 NN 排 · 列号从小到大”）；换排处不再插“折返”行。
 * 无货位或编码不规范（没有区排）的行第一次出现时插一行“无货位 · 按商品找”。
 */
export function slipTurnMarkers(lines: readonly SlipLineLocation[]): ({ kind: 'start'; zone: string; rowLabel: string } | { kind: 'unlocated' } | null)[] {
  let unlocatedShown = false
  return lines.map((line, index) => {
    if (line.zone && line.rowLabel) {
      return index === 0 ? { kind: 'start', zone: line.zone, rowLabel: line.rowLabel } : null
    }
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
  | { kind: 'start'; zone: string; rowLabel: string }
  | { kind: 'unlocated' }
  | { kind: 'line'; index: number; line: T }

/** 分单明细行：首行前插起始排提示、无货位段开头插提示，行号按商品从 1 起。 */
export function buildSlipRows<T extends SlipLineLocation>(lines: readonly T[]): SlipRow<T>[] {
  const markers = slipTurnMarkers(lines)
  const rows: SlipRow<T>[] = []
  lines.forEach((line, index) => {
    const marker = markers[index]
    if (marker?.kind === 'start') rows.push({ kind: 'start', zone: marker.zone, rowLabel: marker.rowLabel })
    else if (marker?.kind === 'unlocated') rows.push({ kind: 'unlocated' })
    rows.push({ kind: 'line', index: index + 1, line })
  })
  return rows
}

/**
 * A4 分单的版面常量（毫米）。页面容器是固定 297mm 且 overflow: hidden，放多了会被直接裁掉，所以按真实高度分页：
 * 商品行与提示行的高度由页面用同一组常量写进 CSS；两个“可放行数”是扣掉页头、汇总框、表头、页脚后的余量，
 * 页面渲染后还会实测溢出并自动收紧（见 PickingSlipsPage）。
 */
export const SLIP_LINE_ROW_MM = 8
export const SLIP_MARKER_ROW_MM = 6
/** 第一页放明细的可用高度（有页头、条码、汇总框，汇总框里“其他段”只占一行时）。 */
export const SLIP_FIRST_PAGE_BODY_MM = 200
/** 续页放明细的可用高度（只有一行订单页头）。 */
export const SLIP_NEXT_PAGE_BODY_MM = 242

/** “其他段”文字超过一行时汇总框会变高，第一页相应少放行：按每行约 60 个字符估算，每多一行扣 5mm。 */
export function slipFirstPageBodyMm(otherSegmentsText: string, shrinkMm = 0): number {
  const extraLines = Math.max(0, Math.ceil(otherSegmentsText.length / 60) - 1)
  return SLIP_FIRST_PAGE_BODY_MM - extraLines * 5 - shrinkMm
}

/**
 * 分单按高度分页：整行放得下才放，绝不让一行跨页或被页边切掉；提示行不单独落在页尾（挪到下一页开头）。
 * 至少返回一页（空段也打一页，方便员工知道没有要拣的）；单行比整页还高时也独占一页，避免死循环。
 */
export function paginateSlipRowsByHeight<T>(
  rows: readonly T[],
  firstPageMm: number,
  nextPageMm: number,
  rowMm: (row: T) => number,
  isMarker: (row: T) => boolean,
): T[][] {
  const pages: T[][] = []
  let cursor = 0
  while (cursor < rows.length) {
    const capacity = pages.length === 0 ? firstPageMm : nextPageMm
    let end = cursor
    let used = 0
    while (end < rows.length && (end === cursor || used + rowMm(rows[end]) <= capacity + 1e-6)) {
      used += rowMm(rows[end])
      end += 1
    }
    if (end < rows.length && end - 1 > cursor && isMarker(rows[end - 1])) end -= 1
    pages.push(rows.slice(cursor, end))
    cursor = end
  }
  return pages.length > 0 ? pages : [[]]
}

/**
 * 分单页地址里没有订单（从别的页签点回来时，页签只记路径、丢了 ?orders=）时，改用上次打开过的查询串。
 * 地址里有订单就以地址为准；两边都没有返回 null。
 */
export function resolveSlipsSearch(currentSearch: string, remembered: string | null | undefined): string | null {
  const hasOrders = (search: string) => new URLSearchParams(search).get('orders')?.split(',').some((guid) => guid.trim()) ?? false
  if (hasOrders(currentSearch)) return currentSearch
  return remembered && hasOrders(remembered) ? remembered : null
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
