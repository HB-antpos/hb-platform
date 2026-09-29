import type { PickedByEntry, PickProgressLine, PickSheetLine } from "./types";

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
  overLineCount: number;
  orderedPieces: number;
  pickedPieces: number;
}

export function summarizeLines(lines: readonly PickSheetLine[]): PickSheetSummary {
  let completeLineCount = 0;
  let shortLineCount = 0;
  let overLineCount = 0;
  let orderedPieces = 0;
  let pickedPieces = 0;
  for (const line of lines) {
    orderedPieces += line.orderedQuantity;
    pickedPieces += line.pickedTotal;
    const status = lineStatus(line);
    if (status === "complete") completeLineCount += 1;
    else if (status === "over") overLineCount += 1;
    else shortLineCount += 1;
  }
  return { lineCount: lines.length, completeLineCount, shortLineCount, overLineCount, orderedPieces, pickedPieces };
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

/** 按货位走一趟：货位自然排序（A-2 在 A-10 前），无货位的排最后，再按货号兜底保证顺序稳定。 */
export function sortLinesByLocation<T extends Pick<PickSheetLine, "locationCode" | "itemNumber" | "productCode">>(
  lines: readonly T[],
): T[] {
  return [...lines].sort((a, b) => {
    const locationA = a.locationCode?.trim() || "";
    const locationB = b.locationCode?.trim() || "";
    if (!locationA !== !locationB) return locationA ? -1 : 1;
    const byLocation = collator.compare(locationA, locationB);
    if (byLocation !== 0) return byLocation;
    return collator.compare(a.itemNumber || a.productCode, b.itemNumber || b.productCode);
  });
}

/** “接下来”：从当前行之后按货位顺序取未拣齐的行，绕回开头，不含当前行。 */
export function upNextLines(sortedLines: readonly PickSheetLine[], currentDetailGuid: string | null, limit: number): PickSheetLine[] {
  const startIndex = currentDetailGuid
    ? sortedLines.findIndex((line) => line.detailGuid === currentDetailGuid) + 1
    : 0;
  const ordered = [...sortedLines.slice(startIndex), ...sortedLines.slice(0, Math.max(0, startIndex))];
  return ordered
    .filter((line) => line.detailGuid !== currentDetailGuid)
    .filter((line) => {
      const status = lineStatus(line);
      return status === "notStarted" || status === "partial";
    })
    .slice(0, limit);
}

/** 进入拣货页时默认落在第一条未拣齐的行上；全部拣齐时落在第一行。 */
export function firstOpenLine(sortedLines: readonly PickSheetLine[]): PickSheetLine | null {
  return (
    sortedLines.find((line) => {
      const status = lineStatus(line);
      return status === "notStarted" || status === "partial";
    }) ?? sortedLines[0] ?? null
  );
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
    };
  });
}
