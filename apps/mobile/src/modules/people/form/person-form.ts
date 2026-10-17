/**
 * 新增 / 编辑整页表单共用的纯函数：姓名四段（姓 / 名 / 中间名 / 昵称）的预填、差异比较与请求体拼装。
 *
 * 姓名写入契约（后端另一个 PR 实现，尚未部署）：
 * - 创建：四段都带（空的不带）；
 * - 更新：null / 不传 = 不改，空串 = 清空，只带有变化的段；
 * - 无论哪种，只要姓 / 名 / 中间名任一段有变化，必须同时带上合成后的 `fullName`，
 *   这样旧后端（忽略四段新字段）也能把全名保存对，新后端则按四段重算。
 */
import { cleanNamePart, composeFullName, splitLegacyFullName } from "../person-name";
import type { PersonNameField, PersonNameInput } from "@/modules/users/validation";

export const NAME_PART_KEYS: readonly PersonNameField[] = ["familyName", "givenName", "middleName", "nickname"];

/** 参与合成全名的三段（昵称不参与）。 */
const FULL_NAME_PART_KEYS: readonly PersonNameField[] = ["familyName", "givenName", "middleName"];

export const EMPTY_PERSON_NAME: PersonNameInput = { familyName: "", givenName: "", middleName: "", nickname: "" };

/** 请求体里的姓名四段；只包含需要发送的键。 */
export type NamePartsPayload = Partial<Record<PersonNameField, string>>;

export interface PersonNameRecord {
  fullName?: string | null;
  givenName?: string | null;
  middleName?: string | null;
  familyName?: string | null;
  nickname?: string | null;
}

/** 清理后的值：去首尾空白、折叠内部空白，空值统一成 ""。 */
function clean(value?: string | null) {
  return cleanNamePart(value) ?? "";
}

/**
 * 把后端返回的姓名记录变成表单预填值。
 * - 详情里姓 / 名 / 中间名任一段有值：直接用，不再猜；
 * - 只有 fullName（存量账号）：用 splitLegacyFullName 预填，`legacySplit=true`，
 *   界面据此在姓名区下给「由全名自动拆分，请核对姓和名」的小提示；
 * - 昵称始终直接读取。
 */
export function buildPersonNameValues(source: PersonNameRecord): { values: PersonNameInput; legacySplit: boolean } {
  const nickname = clean(source.nickname);
  const hasStructured = FULL_NAME_PART_KEYS.some((key) => clean(source[key]) !== "");
  if (hasStructured) {
    return {
      values: {
        familyName: clean(source.familyName),
        givenName: clean(source.givenName),
        middleName: clean(source.middleName),
        nickname,
      },
      legacySplit: false,
    };
  }

  const full = clean(source.fullName);
  if (!full) {
    return { values: { ...EMPTY_PERSON_NAME, nickname }, legacySplit: false };
  }
  const split = splitLegacyFullName(full);
  return {
    values: { familyName: split.familyName ?? "", givenName: split.givenName ?? "", middleName: split.middleName ?? "", nickname },
    legacySplit: true,
  };
}

/** 姓 / 名 / 中间名 / 昵称是否都为空（新增表单的「是否填过」判断）。 */
export function isPersonNameEmpty(values: PersonNameInput) {
  return NAME_PART_KEYS.every((key) => clean(values[key]) === "");
}

export interface NameDiff {
  /** 只含有变化的段；空串表示清空。 */
  parts: NamePartsPayload;
  /** 姓 / 名 / 中间名任一段有变化时才有：合成后的全名（三段都被清空时为空串）。 */
  fullName?: string;
  /** 有变化的段数（含昵称），用于「保存 · n 项改动」。 */
  changedCount: number;
  /** 姓 / 名 / 中间名是否被改动（决定是否要求姓、名必填、是否带 fullName）。 */
  nameChanged: boolean;
}

/** 对比基线与当前表单值，得到只含变化段的请求片段。 */
export function diffNameParts(baseline: PersonNameInput, current: PersonNameInput): NameDiff {
  const parts: NamePartsPayload = {};
  let changedCount = 0;
  for (const key of NAME_PART_KEYS) {
    const next = clean(current[key]);
    if (next !== clean(baseline[key])) {
      parts[key] = next;
      changedCount += 1;
    }
  }
  const nameChanged = FULL_NAME_PART_KEYS.some((key) => key in parts);
  return {
    parts,
    ...(nameChanged ? { fullName: composeFullName(current) ?? "" } : {}),
    changedCount,
    nameChanged,
  };
}

/**
 * 创建请求里的姓名片段：有值的段才带；`fullName` 始终是合成值（旧后端只认它）。
 * 返回对象不含值为 undefined 的键，方便直接展开进请求体。
 */
export function buildCreateNameBody(values: PersonNameInput): { fullName?: string } & NamePartsPayload {
  const body: { fullName?: string } & NamePartsPayload = {};
  const fullName = composeFullName(values);
  if (fullName) body.fullName = fullName;
  for (const key of NAME_PART_KEYS) {
    const value = clean(values[key]);
    if (value) body[key] = value;
  }
  return body;
}

/**
 * 更新请求里 `fullName` 的取值：
 * 姓名有变化用合成值；没变化沿用详情里原来的全名；两者都没有时，若详情已有结构化姓名则用其合成值，
 * 避免后端把缺省的 fullName 当成「清空」。
 */
export function resolveUpdateFullName(input: {
  diff: NameDiff;
  currentFullName?: string | null;
  baselineNames: PersonNameInput;
}): string | undefined {
  if (input.diff.fullName !== undefined) return input.diff.fullName;
  const original = clean(input.currentFullName);
  if (original) return original;
  return composeFullName(input.baselineNames) ?? undefined;
}
