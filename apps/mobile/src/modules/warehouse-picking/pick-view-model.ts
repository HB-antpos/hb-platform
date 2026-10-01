import { PICK_STOCKOUT_REASON, type PickParticipant, type PickSheetLine } from "./types";

/** 同事最后一次操作在这个时间窗内视为“还在拣”。 */
export const PARTICIPANT_ACTIVE_MS = 10 * 60 * 1000;

const CJK = /[㐀-鿿豈-﫿]/;

/** 头像缩写：中文名取第一个字；英文名取前两个单词首字母。 */
export function pickerInitials(name: string): string {
  const trimmed = name.trim();
  if (!trimmed) return "?";
  if (CJK.test(trimmed)) return trimmed[0];
  const words = trimmed.split(/\s+/).filter(Boolean);
  const letters = words.slice(0, 2).map((word) => word[0]!.toUpperCase());
  return letters.join("") || trimmed[0]!.toUpperCase();
}

/** 名字用于紧凑位置时只取名（英文取第一个单词，中文保持原样）。 */
export function shortPickerName(name: string): string {
  const trimmed = name.trim();
  if (CJK.test(trimmed)) return trimmed;
  return trimmed.split(/\s+/)[0] ?? trimmed;
}

/** 除自己外、在时间窗内还有操作的同事，最近活动的排前面。 */
export function activeTeammates(
  participants: readonly PickParticipant[],
  myUserGuid: string | null,
  nowMs: number,
  windowMs = PARTICIPANT_ACTIVE_MS,
): PickParticipant[] {
  return participants
    .filter((participant) => !sameGuid(participant.pickerUserGuid, myUserGuid))
    .filter((participant) => {
      const at = Date.parse(participant.lastActiveAtUtc);
      return Number.isFinite(at) && nowMs - at <= windowMs;
    })
    .sort((a, b) => Date.parse(b.lastActiveAtUtc) - Date.parse(a.lastActiveAtUtc));
}

/** “接下来”列表里标出同事正在哪一行（最后一次扫码的行）。 */
export function teammateByLine(
  participants: readonly PickParticipant[],
  myUserGuid: string | null,
  nowMs: number,
): Map<string, string> {
  const result = new Map<string, string>();
  for (const participant of activeTeammates(participants, myUserGuid, nowMs)) {
    if (participant.lastDetailGuid && !result.has(participant.lastDetailGuid)) {
      result.set(participant.lastDetailGuid, shortPickerName(participant.pickerName));
    }
  }
  return result;
}

export type RelativeTime = { kind: "justNow" } | { kind: "minutes"; count: number };

export function relativeMinutes(isoUtc: string, nowMs: number): RelativeTime {
  const at = Date.parse(isoUtc);
  const minutes = Number.isFinite(at) ? Math.floor((nowMs - at) / 60000) : 0;
  return minutes < 1 ? { kind: "justNow" } : { kind: "minutes", count: minutes };
}

export interface PickedByPart {
  name: string;
  quantity: number;
  isMe: boolean;
}

/** 当前行“谁拣了多少”：自己排第一，其余按件数降序。 */
export function pickedByParts(line: Pick<PickSheetLine, "pickedBy">, myUserGuid: string | null): PickedByPart[] {
  return line.pickedBy
    .filter((entry) => entry.quantity !== 0)
    .map((entry) => ({
      name: shortPickerName(entry.pickerName),
      quantity: entry.quantity,
      isMe: sameGuid(entry.pickerUserGuid, myUserGuid),
    }))
    .sort((a, b) => Number(b.isMe) - Number(a.isMe) || b.quantity - a.quantity);
}

/** 扫码提示里说明“扫到的是哪个子项”：只有套装子码 / 多码才有标签。 */
export function isSameProductLine(a: PickSheetLine, b: PickSheetLine) {
  return a.productCode.toLowerCase() === b.productCode.toLowerCase();
}

function sameGuid(a: string | null | undefined, b: string | null | undefined) {
  return Boolean(a) && Boolean(b) && a!.toLowerCase() === b!.toLowerCase();
}

/**
 * 没货原因的文案键：未绑定货位的行，“货位空了”说成“仓库里找不到”。
 * short 用于列表里的紧凑说明（“货位空了 · 陈伟”）。
 */
export function stockoutReasonKey(reason: number, located: boolean, short = false): string {
  const prefix = short ? "stockout.short" : "stockout.reason";
  switch (reason) {
    case PICK_STOCKOUT_REASON.wrongProduct:
      return `${prefix}WrongProduct`;
    case PICK_STOCKOUT_REASON.damaged:
      return `${prefix}Damaged`;
    default:
      return located ? `${prefix}LocationEmpty` : `${prefix}NotFound`;
  }
}
