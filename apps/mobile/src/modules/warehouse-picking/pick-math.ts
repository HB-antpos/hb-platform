import type { PickedByEntry, PickProgressLine, PickRoute, PickScope, PickSheetLine } from "./types";

export type PickLineStatus = "notStarted" | "partial" | "complete" | "over";

/** 中包数为空或 ≤0 视为未设置：扫码不能按中包累加，需要先补录。 */
export function hasMinOrderQuantity(line: Pick<PickSheetLine, "minOrderQuantity">): boolean {
  return typeof line.minOrderQuantity === "number" && line.minOrderQuantity > 0;
}

export function lineStatus(line: Pick<PickSheetLine, "orderedQuantity" | "pickedTotal">): PickLineStatus {
  if (line.pickedTotal <= 0) return line.orderedQuantity <= 0 ? "complete" : "notStarted";
  if (line.pickedTotal < line.orderedQuantity) return "partial";
  if (line.pickedTotal === line.orderedQuantity) return "complete";
  return "over";
}

/** 标记了“货位没货”且仍没拣齐：剩余数量视为拣不到，不再出现在“接下来”里。 */
export function isStockout(line: Pick<PickSheetLine, "stockout" | "orderedQuantity" | "pickedTotal">): boolean {
  return Boolean(line.stockout) && line.pickedTotal < line.orderedQuantity;
}

/** 还要去拣的行：未开始或部分拣，且没被标成货位没货。 */
export function isOpenLine(line: Pick<PickSheetLine, "stockout" | "orderedQuantity" | "pickedTotal">): boolean {
  const status = lineStatus(line);
  return (status === "notStarted" || status === "partial") && !isStockout(line);
}

/** 还差几件、按当前中包数大约还要扫几次；超拣时 remaining 为负。 */
export function lineRemaining(line: PickSheetLine): { remaining: number; scans: number | null } {
  const remaining = line.orderedQuantity - line.pickedTotal;
  if (remaining <= 0 || !hasMinOrderQuantity(line)) {
    return { remaining, scans: null };
  }
  return { remaining, scans: Math.ceil(remaining / (line.minOrderQuantity as number)) };
}

/** 件数换算成“几个中包 + 余几件”。 */
export function splitIntoPacks(pieces: number, pack: number | null): { packs: number; rest: number } | null {
  if (!pack || pack <= 0) return null;
  const safe = Math.max(0, Math.trunc(pieces));
  return { packs: Math.floor(safe / pack), rest: safe % pack };
}

export interface PickSheetSummary {
  lineCount: number;
  completeLineCount: number;
  shortLineCount: number;
  /** 少拣行里被标成“货位没货”的行数（是 shortLineCount 的子集）。 */
  stockoutLineCount: number;
  overLineCount: number;
  orderedPieces: number;
  pickedPieces: number;
}

export function summarizeLines(lines: readonly PickSheetLine[]): PickSheetSummary {
  let completeLineCount = 0;
  let shortLineCount = 0;
  let stockoutLineCount = 0;
  let overLineCount = 0;
  let orderedPieces = 0;
  let pickedPieces = 0;
  for (const line of lines) {
    orderedPieces += line.orderedQuantity;
    pickedPieces += line.pickedTotal;
    const status = lineStatus(line);
    if (status === "complete") completeLineCount += 1;
    else if (status === "over") overLineCount += 1;
    else {
      shortLineCount += 1;
      if (isStockout(line)) stockoutLineCount += 1;
    }
  }
  return { lineCount: lines.length, completeLineCount, shortLineCount, stockoutLineCount, overLineCount, orderedPieces, pickedPieces };
}

/** 各拣货人合计件数（完成页“谁拣了多少”），按件数降序。 */
export function summarizePickers(lines: readonly PickSheetLine[]): PickedByEntry[] {
  const totals = new Map<string, PickedByEntry>();
  for (const line of lines) {
    for (const entry of line.pickedBy) {
      const current = totals.get(entry.pickerUserGuid);
      if (current) {
        current.quantity += entry.quantity;
      } else {
        totals.set(entry.pickerUserGuid, { ...entry });
      }
    }
  }
  return Array.from(totals.values())
    .filter((entry) => entry.quantity !== 0)
    .sort((a, b) => b.quantity - a.quantity);
}

const collator = new Intl.Collator("en", { numeric: true, sensitivity: "base" });

/** 一行可能绑定多个配货位（后端按编码排序后以逗号连接）：走位按第一个算。 */
export function primaryLocation(locationCode: string | null | undefined): string {
  return locationCode?.split(",")[0]?.trim() ?? "";
}

export function hasLocation(line: Pick<PickSheetLine, "locationCode">): boolean {
  return primaryLocation(line.locationCode).length > 0;
}

export interface ParsedLocation {
  zone: string;
  row: number;
  /** 排号原文（保留前导零，如 "04"），用于换排提示。 */
  rowLabel: string;
  bay: number;
  level: number;
}

/**
 * 货位编码按「区-排-列-层」解析：A-03-12-02 = A 区 03 排 12 列 2 层；列、层可省略（A-03、A-03-12）。
 * 一排对应一条通道。区可以是字母或数字，排、列、层必须是数字；不规范的编码返回 null。
 */
export function parseLocationCode(raw: string | null | undefined): ParsedLocation | null {
  const code = primaryLocation(raw);
  if (!code) return null;
  const parts = code.split("-").map((part) => part.trim());
  if (parts.length < 2 || parts.length > 4) return null;
  const [zone, rowText, ...rest] = parts;
  if (!/^[A-Za-z0-9]+$/.test(zone) || ![rowText, ...rest].every((part) => /^\d+$/.test(part))) return null;
  const [bay = 0, level = 0] = rest.map(Number);
  return { zone: zone.toUpperCase(), row: Number(rowText), rowLabel: rowText, bay, level };
}

/** S 型在双数排反向走：列号从大到小；M 型每排都从通道口进，列号一律从小到大。 */
export function isDescendingRow(location: Pick<ParsedLocation, "row">, route: PickRoute): boolean {
  return route === "s" && location.row % 2 === 0;
}

type SortableLine = Pick<PickSheetLine, "locationCode" | "itemNumber" | "productCode">;

/**
 * 按走位顺序走一趟：先区、再排；排内按列号（S 型双数排倒序），同列按层从低到高。
 * 编码不规范的货位排在规范货位之后按编码自然排序；未绑定货位的行排最后，按货号兜底保证顺序稳定。
 */
export function sortLinesByLocation<T extends SortableLine>(lines: readonly T[], route: PickRoute = "m"): T[] {
  return [...lines].sort((a, b) => {
    const locationA = primaryLocation(a.locationCode);
    const locationB = primaryLocation(b.locationCode);
    if (!locationA !== !locationB) return locationA ? -1 : 1;
    if (locationA && locationB) {
      const parsedA = parseLocationCode(locationA);
      const parsedB = parseLocationCode(locationB);
      if (!parsedA !== !parsedB) return parsedA ? -1 : 1;
      if (parsedA && parsedB) {
        const byRoute =
          collator.compare(parsedA.zone, parsedB.zone) ||
          parsedA.row - parsedB.row ||
          (isDescendingRow(parsedA, route) ? parsedB.bay - parsedA.bay : parsedA.bay - parsedB.bay) ||
          parsedA.level - parsedB.level;
        if (byRoute !== 0) return byRoute;
      }
      const byLocation = collator.compare(locationA, locationB);
      if (byLocation !== 0) return byLocation;
    }
    return collator.compare(a.itemNumber || a.productCode, b.itemNumber || b.productCode);
  });
}

/** 换排提示：下一行与上一行不在同一区同一排时，返回要转入的排与该排的走向。 */
export function routeTurn(
  previous: Pick<PickSheetLine, "locationCode"> | null,
  next: Pick<PickSheetLine, "locationCode">,
  route: PickRoute,
): { zone: string; rowLabel: string; descending: boolean } | null {
  const to = parseLocationCode(next.locationCode);
  if (!to) return null;
  const from = previous ? parseLocationCode(previous.locationCode) : null;
  if (from && from.zone === to.zone && from.row === to.row) return null;
  return { zone: to.zone, rowLabel: to.rowLabel, descending: isDescendingRow(to, route) };
}

/** “我的”指谁：扫了分单就是那一段，否则是派给（或已领取到）当前拣货人的行。 */
export interface MineContext {
  pickerUserGuid: string | null;
  segmentNo: number | null;
  /** 正在帮的段号（“帮 某某”范围）；没在帮忙为空。 */
  helpSegmentNo?: number | null;
}

type ScopeLine = Pick<PickSheetLine, "locationCode" | "assigneeUserGuid" | "assignmentSegmentNo">;

export function isMyLine(line: Pick<PickSheetLine, "assigneeUserGuid" | "assignmentSegmentNo">, mine: MineContext): boolean {
  if (mine.segmentNo != null) return line.assignmentSegmentNo === mine.segmentNo;
  return Boolean(mine.pickerUserGuid && line.assigneeUserGuid && line.assigneeUserGuid.toLowerCase() === mine.pickerUserGuid.toLowerCase());
}

/** 订单有没有拣货分配（任意一行带段号即有）。 */
export function hasAssignments(lines: readonly Pick<PickSheetLine, "assignmentSegmentNo">[]): boolean {
  return lines.some((line) => line.assignmentSegmentNo != null);
}

export function lineInScope(line: ScopeLine, scope: PickScope, mine?: MineContext): boolean {
  if (scope === "all") return true;
  if (scope === "mine") return mine ? isMyLine(line, mine) : false;
  if (scope === "help") return mine?.helpSegmentNo != null && line.assignmentSegmentNo === mine.helpSegmentNo;
  return (scope === "located") === hasLocation(line);
}

export function scopeCounts(lines: readonly ScopeLine[], mine?: MineContext): Record<PickScope, number> {
  const located = lines.filter(hasLocation).length;
  return {
    mine: mine ? lines.filter((line) => isMyLine(line, mine)).length : 0,
    help: lines.filter((line) => lineInScope(line, "help", mine)).length,
    all: lines.length,
    located,
    unlocated: lines.length - located,
  };
}

export interface SegmentSummary {
  segmentNo: number;
  assigneeUserGuid: string | null;
  assigneeName: string | null;
  lineCount: number;
  completeLineCount: number;
  stockoutLineCount: number;
  pickedPieces: number;
  orderedPieces: number;
}

/** 完成页按段汇总：每段负责人（或待领取）、已拣齐与没货的品种数、件数；没有分配的行不计。 */
export function summarizeSegments(lines: readonly PickSheetLine[]): SegmentSummary[] {
  const bySegment = new Map<number, SegmentSummary>();
  for (const line of lines) {
    if (line.assignmentSegmentNo == null) continue;
    const segment = bySegment.get(line.assignmentSegmentNo) ?? {
      segmentNo: line.assignmentSegmentNo,
      assigneeUserGuid: line.assigneeUserGuid,
      assigneeName: line.assigneeName,
      lineCount: 0,
      completeLineCount: 0,
      stockoutLineCount: 0,
      pickedPieces: 0,
      orderedPieces: 0,
    };
    segment.lineCount += 1;
    segment.orderedPieces += line.orderedQuantity;
    segment.pickedPieces += line.pickedTotal;
    if (line.pickedTotal >= line.orderedQuantity) segment.completeLineCount += 1;
    else if (isStockout(line)) segment.stockoutLineCount += 1;
    bySegment.set(line.assignmentSegmentNo, segment);
  }
  return Array.from(bySegment.values()).sort((a, b) => a.segmentNo - b.segmentNo);
}

/** 一段处理完：段内每个品种拣齐或标了货位没货（与后端提交拦截同口径，半拣没标算没完）。 */
export function isSegmentSettled(segment: SegmentSummary): boolean {
  return segment.completeLineCount + segment.stockoutLineCount >= segment.lineCount;
}

/** 还没处理完的段，按段号升序；有分配的订单里这些段没完时不能提交整单。 */
export function incompleteSegments(lines: readonly PickSheetLine[]): SegmentSummary[] {
  return summarizeSegments(lines).filter((segment) => !isSegmentSettled(segment));
}

/** 这段是不是我的：扫分单领取进来的按段号，否则按负责人是我。 */
export function isMySegment(segment: Pick<SegmentSummary, "segmentNo" | "assigneeUserGuid">, mine: MineContext): boolean {
  return isMyLine({ assignmentSegmentNo: segment.segmentNo, assigneeUserGuid: segment.assigneeUserGuid }, mine);
}

/** “接下来”：从当前行之后按走位顺序取还要拣的行（不含货位没货），绕回开头，不含当前行。 */
export function upNextLines(sortedLines: readonly PickSheetLine[], currentDetailGuid: string | null, limit: number): PickSheetLine[] {
  const startIndex = currentDetailGuid
    ? sortedLines.findIndex((line) => line.detailGuid === currentDetailGuid) + 1
    : 0;
  const ordered = [...sortedLines.slice(startIndex), ...sortedLines.slice(0, Math.max(0, startIndex))];
  return ordered
    .filter((line) => line.detailGuid !== currentDetailGuid)
    .filter(isOpenLine)
    .slice(0, limit);
}

/** 进入拣货页时默认落在第一条还要拣的行上；全部拣齐或都没货时落在第一行。 */
export function firstOpenLine(sortedLines: readonly PickSheetLine[]): PickSheetLine | null {
  return sortedLines.find(isOpenLine) ?? sortedLines[0] ?? null;
}

/** 把服务端返回的行进度合并回拣货单（轮询或写入后），不改动未返回的行。 */
export function mergeProgressLines(lines: readonly PickSheetLine[], progress: readonly PickProgressLine[]): PickSheetLine[] {
  if (progress.length === 0) return [...lines];
  const byGuid = new Map(progress.map((line) => [line.detailGuid, line]));
  return lines.map((line) => {
    const next = byGuid.get(line.detailGuid);
    if (!next) return line;
    return {
      ...line,
      pickedTotal: next.pickedTotal,
      pickedBy: next.pickedBy,
      minOrderQuantity: next.minOrderQuantity,
      stockout: next.stockout,
      assigneeUserGuid: next.assigneeUserGuid,
      assigneeName: next.assigneeName,
      assignmentSegmentNo: next.assignmentSegmentNo,
    };
  });
}
