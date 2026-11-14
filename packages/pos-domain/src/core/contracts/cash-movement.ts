import { auditActorPayload } from "./audit-actor";
import type { DailyCloseScope } from "./daily-close";
import type { AuditEventDraft } from "./order";

/**
 * 收银端现金存入 / 取出（Cash In / Out）的领域合同与纯函数。
 * 口径以服务端 CashMovementSyncService / Hbpos.Contracts.CashMovements 为准：
 * 本地必须先满足服务端校验，否则服务端会永久拒绝这条记录。
 *
 * 金额一律用整数分；记录写入后不可修改，输错只能再写一条「冲正」记录
 * （voidsMovementGuid 指向原记录，原因码与金额同原记录，方向与自然方向相反）。
 */

/** 原因码（与服务端 CashMovementContractConstants 一致，区分大小写保存）。 */
export const CASH_MOVEMENT_REASON_CODES = Object.freeze([
  "ChangeTopUp",
  "OtherIn",
  "SafeDrop",
  "PaidOut",
] as const);
export type CashMovementReasonCode = (typeof CASH_MOVEMENT_REASON_CODES)[number];

export type CashMovementDirection = "In" | "Out";

/** 每个原因码的自然方向：普通记录的方向必须等于它，冲正记录与它相反。 */
export const CASH_MOVEMENT_NATURAL_DIRECTIONS: Readonly<
  Record<CashMovementReasonCode, CashMovementDirection>
> = Object.freeze({
  ChangeTopUp: "In",
  OtherIn: "In",
  SafeDrop: "Out",
  PaidOut: "Out",
});

/** 备注必填的原因码（冲正记录由界面另行要求备注，见 validateCashMovementInput）。 */
export const CASH_MOVEMENT_NOTE_REQUIRED_REASONS: readonly CashMovementReasonCode[] =
  Object.freeze(["OtherIn", "PaidOut"] as const);

/** 单笔金额上限：1,000,000.00 元 = 100,000,000 分（含）。 */
export const CASH_MOVEMENT_MAXIMUM_AMOUNT_CENTS = 100_000_000;
export const CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH = 200;
export const CASH_MOVEMENT_MINIMUM_REQUIRED_NOTE_LENGTH = 2;
export const CASH_MOVEMENT_MAXIMUM_CASHIER_ID_LENGTH = 64;
export const CASH_MOVEMENT_MAXIMUM_CASHIER_NAME_LENGTH = 128;
export const CASH_MOVEMENT_MAXIMUM_DISPLAY_NUMBER_LENGTH = 32;
export const CASH_MOVEMENT_MAXIMUM_SCOPE_CODE_LENGTH = 128;

/** 审计事件类型（服务端白名单已有）。 */
export const CASH_MOVEMENT_AUDIT_EVENT_TYPES = Object.freeze({
  in: "CASH_IN",
  out: "CASH_OUT",
  void: "CASH_MOVEMENT_VOID",
} as const);

export const CASH_MOVEMENT_UPLOAD_STATES = Object.freeze([
  "pending",
  "uploading",
  "synced",
  "rejected",
] as const);
export type CashMovementUploadState = (typeof CASH_MOVEMENT_UPLOAD_STATES)[number];

/** 记录的不可变事实（对应服务端 CashMovementSync，另含本机作用域）。 */
export type CashMovementFact = Readonly<{
  movementGuid: string;
  direction: CashMovementDirection;
  reasonCode: CashMovementReasonCode;
  amountCents: number;
  /** trim 后的备注；没有备注为 null。 */
  note: string | null;
  /** 非空表示这是一条冲正记录，指向被冲正的原记录。 */
  voidsMovementGuid: string | null;
  /** 门店营业日（yyyy-MM-dd，按门店营业时区，不是设备时区）。 */
  businessDate: string;
  occurredAtIso: string;
  cashierId: string;
  cashierName: string;
  /** 收银员没有权限、由他人扫码授权时的授权人；本人有权限为 null。 */
  authorizedById: string | null;
  authorizedByName: string | null;
  storeCode: string;
  deviceCode: string;
}>;

/** 尚未入库的草稿：流水号由仓储在事务内按「本库全部记录数 + 1」分配。 */
export type CashMovementDraft = CashMovementFact;

export type CashMovementCommit = Readonly<{
  movement: CashMovementDraft;
  audit: AuditEventDraft;
}>;

/** 已入库的记录：事实 + 流水号 + 是否已被冲正 + 上传状态。 */
export type LocalCashMovement = CashMovementFact &
  Readonly<{
    /** 本机流水号，如 CM-0009，仅展示用。 */
    displayNumber: string;
    /** 若本记录已被某条冲正记录指向，这里是那条冲正记录的 guid。 */
    voidedByMovementGuid: string | null;
    auditEventId: string;
    uploadState: CashMovementUploadState;
    uploadAttempts: number;
    uploadErrorCode: string | null;
    uploadedAtIso: string | null;
    createdAtIso: string;
  }>;

export type CashMovementSummary = Readonly<{
  cashInCents: number;
  cashOutCents: number;
  /** 每个原因码的净额（自然方向记加、冲正记减，一对原记录 + 冲正抵消为 0），恒 >= 0。 */
  byReason: Readonly<Record<CashMovementReasonCode, number>>;
}>;

export interface CashMovementRepositoryPort {
  /**
   * 单事务写入：算流水号、写记录、写审计（CASH_IN / CASH_OUT / CASH_MOVEMENT_VOID）。
   * 冲正时在同一事务内复核全部冲正规则。相同 movementGuid 且内容一致视为重放（replayed = true）。
   */
  append(
    commit: CashMovementCommit,
  ): Promise<Readonly<{ replayed: boolean; movement: LocalCashMovement }>>;
  getByGuid(movementGuid: string): Promise<LocalCashMovement | null>;
  /** 当前营业日（[periodFrom, periodTo) 按 occurred_at 落窗）的全部记录，按发生时间升序，已冲正的带 voidedByMovementGuid。 */
  listToday(scope: DailyCloseScope): Promise<readonly LocalCashMovement[]>;
  /** 同一窗口的存入 / 取出汇总；与日结汇总同口径，不按上传状态过滤，也不受开关影响。 */
  summarizePeriod(scope: DailyCloseScope): Promise<CashMovementSummary>;
  /** 当前营业日最近一次已归档日结的保存时间；没有为 null。供 canVoidCashMovement 使用。 */
  latestDailyCloseSavedAtIso(scope: DailyCloseScope): Promise<string | null>;
}

export type CashMovementValidationCode =
  | "REASON_INVALID"
  | "AMOUNT_INVALID"
  | "AMOUNT_TOO_LARGE"
  | "NOTE_TOO_LONG"
  | "NOTE_REQUIRED"
  | "MOVEMENT_GUID_INVALID"
  | "AUDIT_EVENT_ID_INVALID"
  | "OCCURRED_AT_INVALID"
  | "BUSINESS_TIME_ZONE_INVALID"
  | "CASHIER_INVALID"
  | "AUTHORIZER_INVALID"
  | "SCOPE_INVALID"
  | "VOID_TARGET_INVALID";

export class CashMovementValidationError extends Error {
  public constructor(
    public readonly code: CashMovementValidationCode,
    message?: string,
  ) {
    super(message ?? code);
    this.name = "CashMovementValidationError";
  }
}

/** 仓储层在事务内复核失败时抛出（冲正规则、重放不一致等），UI 据 code / denial 提示。 */
export class CashMovementRepositoryError extends Error {
  public constructor(
    public readonly code:
      | "VOID_TARGET_NOT_FOUND"
      | "VOID_NOT_ALLOWED"
      | "REPLAY_MISMATCH"
      | "COMMIT_INVALID",
    /** code = VOID_NOT_ALLOWED 时给出具体拒绝原因。 */
    public readonly denial: CashMovementVoidDenial | null = null,
    message?: string,
  ) {
    super(message ?? (denial ? `${code}:${denial}` : code));
    this.name = "CashMovementRepositoryError";
  }
}

export type CashMovementInputValidation =
  | Readonly<{
      ok: true;
      reasonCode: CashMovementReasonCode;
      amountCents: number;
      note: string | null;
    }>
  | Readonly<{ ok: false; code: CashMovementValidationCode }>;

export function isCashMovementReasonCode(
  value: unknown,
): value is CashMovementReasonCode {
  return (
    typeof value === "string" &&
    (CASH_MOVEMENT_REASON_CODES as readonly string[]).includes(value)
  );
}

export function oppositeCashMovementDirection(
  direction: CashMovementDirection,
): CashMovementDirection {
  return direction === "In" ? "Out" : "In";
}

/**
 * 界面与构造函数共用的输入校验（服务端会永久拒绝的情况必须在本地先挡住，且不静默截断）：
 * - 金额：整数分，> 0，<= 100,000,000 分（整数分天然满足「最多 2 位小数」）；
 * - 备注：trim 后 <= 200，超长直接拒绝；
 * - OtherIn / PaidOut 的普通记录备注 trim 后 >= 2 字；冲正记录（isVoid）界面要求 >= 2 字（服务端不强制，本地更严）。
 */
export function validateCashMovementInput(
  input: Readonly<{
    reasonCode: unknown;
    amountCents: unknown;
    note?: string | null | undefined;
    isVoid?: boolean | undefined;
  }>,
): CashMovementInputValidation {
  if (!isCashMovementReasonCode(input.reasonCode)) {
    return { ok: false, code: "REASON_INVALID" };
  }
  const reasonCode = input.reasonCode;
  const amountCents = input.amountCents;
  if (
    typeof amountCents !== "number" ||
    !Number.isSafeInteger(amountCents) ||
    amountCents <= 0
  ) {
    return { ok: false, code: "AMOUNT_INVALID" };
  }
  if (amountCents > CASH_MOVEMENT_MAXIMUM_AMOUNT_CENTS) {
    return { ok: false, code: "AMOUNT_TOO_LARGE" };
  }
  const trimmed = typeof input.note === "string" ? input.note.trim() : "";
  if (trimmed.length > CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH) {
    return { ok: false, code: "NOTE_TOO_LONG" };
  }
  const noteRequired =
    input.isVoid === true ||
    CASH_MOVEMENT_NOTE_REQUIRED_REASONS.includes(reasonCode);
  if (
    noteRequired &&
    trimmed.length < CASH_MOVEMENT_MINIMUM_REQUIRED_NOTE_LENGTH
  ) {
    return { ok: false, code: "NOTE_REQUIRED" };
  }
  return {
    ok: true,
    reasonCode,
    amountCents,
    note: trimmed.length === 0 ? null : trimmed,
  };
}

/** 流水号 CM-0001：本库全部记录数 + 1（含冲正），不按日重置，仅展示用。 */
export function formatCashMovementDisplayNumber(sequence: number): string {
  if (!Number.isSafeInteger(sequence) || sequence < 1) {
    throw new CashMovementValidationError(
      "SCOPE_INVALID",
      "Cash movement sequence must be a positive integer.",
    );
  }
  return `CM-${String(sequence).padStart(4, "0")}`;
}

/**
 * 发生时刻所属的门店营业日（yyyy-MM-dd）。必须按门店营业时区（不是设备时区），
 * 与 daily-close-domain 的 businessDateInTimeZone 同一算法，保证日结窗口与明细归日一致。
 */
export function cashMovementBusinessDate(
  occurredAtIso: string,
  businessTimeZone: string,
): string {
  const instant = Date.parse(occurredAtIso);
  if (!Number.isFinite(instant)) {
    throw new CashMovementValidationError("OCCURRED_AT_INVALID");
  }
  const zone = businessTimeZone.trim();
  if (zone.length === 0) {
    throw new CashMovementValidationError("BUSINESS_TIME_ZONE_INVALID");
  }
  let parts: Intl.DateTimeFormatPart[];
  try {
    parts = new Intl.DateTimeFormat("en-CA", {
      calendar: "gregory",
      day: "2-digit",
      month: "2-digit",
      numberingSystem: "latn",
      timeZone: zone,
      year: "numeric",
    }).formatToParts(new Date(instant));
  } catch {
    throw new CashMovementValidationError("BUSINESS_TIME_ZONE_INVALID");
  }
  const byType = new Map(parts.map((part) => [part.type, part.value]));
  const date = `${byType.get("year") ?? ""}-${byType.get("month") ?? ""}-${byType.get("day") ?? ""}`;
  if (!/^\d{4}-\d{2}-\d{2}$/u.test(date)) {
    throw new CashMovementValidationError("BUSINESS_TIME_ZONE_INVALID");
  }
  return date;
}

export type CashMovementEntry =
  | Readonly<{
      kind: "movement";
      /** 未校验的原因码文本，buildCashMovement 会校验。 */
      reasonCode: string;
      amountCents: number;
    }>
  | Readonly<{
      /** 冲正：原因码与金额取自原记录，方向与原记录相反。 */
      kind: "void";
      original: Pick<
        CashMovementFact,
        "movementGuid" | "reasonCode" | "amountCents" | "direction" | "voidsMovementGuid"
      >;
    }>;

export type BuildCashMovementInput = Readonly<{
  /** 客户端生成的 UUID，是跨重试的幂等键。 */
  movementGuid: string;
  /** 审计事件 id（UUID，上传时服务端要求 Guid）。 */
  auditEventId: string;
  entry: CashMovementEntry;
  note?: string | null | undefined;
  occurredAtIso: string;
  /** 门店营业时区（IANA）；business_date 用它算，不用设备时区。 */
  businessTimeZone: string;
  storeCode: string;
  deviceCode: string;
  cashier: Readonly<{
    cashierId: string;
    cashierName: string;
    userGuid: string | null;
  }>;
  /** 收银员没有权限、由他人扫码授权时填；本人有权限为 null。 */
  authorizedBy?: Readonly<{ id: string; name: string }> | null | undefined;
}>;

const UUID_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;

export function isCashMovementGuid(value: unknown): value is string {
  return typeof value === "string" && UUID_PATTERN.test(value);
}

/**
 * 构造一条待入库的现金进出（或冲正）及其审计事件。任何不满足服务端校验的输入直接抛
 * CashMovementValidationError（带 code），不会截断或修正。
 * 审计：CASH_IN / CASH_OUT / CASH_MOVEMENT_VOID，payload 只放安全键，amountDeltaCents 带符号
 * （存入为正、取出为负；冲正与原记录符号相反），correlationId = movementGuid，orderGuid = null。
 */
export function buildCashMovement(input: BuildCashMovementInput): CashMovementCommit {
  if (!isCashMovementGuid(input.movementGuid)) {
    throw new CashMovementValidationError("MOVEMENT_GUID_INVALID");
  }
  if (!isCashMovementGuid(input.auditEventId)) {
    throw new CashMovementValidationError("AUDIT_EVENT_ID_INVALID");
  }
  const instant = Date.parse(input.occurredAtIso);
  if (!Number.isFinite(instant)) {
    throw new CashMovementValidationError("OCCURRED_AT_INVALID");
  }
  const occurredAtIso = new Date(instant).toISOString();
  const businessDate = cashMovementBusinessDate(
    occurredAtIso,
    input.businessTimeZone,
  );

  let reasonCode: CashMovementReasonCode;
  let amountCents: number;
  let direction: CashMovementDirection;
  let voidsMovementGuid: string | null = null;
  const isVoid = input.entry.kind === "void";
  if (input.entry.kind === "void") {
    const original = input.entry.original;
    // 冲正不能冲冲正；原记录必须是合法的普通记录。
    if (
      original.voidsMovementGuid !== null ||
      !isCashMovementGuid(original.movementGuid) ||
      original.movementGuid.toLowerCase() === input.movementGuid.toLowerCase() ||
      !isCashMovementReasonCode(original.reasonCode) ||
      original.direction !== CASH_MOVEMENT_NATURAL_DIRECTIONS[original.reasonCode]
    ) {
      throw new CashMovementValidationError("VOID_TARGET_INVALID");
    }
    reasonCode = original.reasonCode;
    amountCents = original.amountCents;
    direction = oppositeCashMovementDirection(original.direction);
    voidsMovementGuid = original.movementGuid;
  } else {
    const checked = validateCashMovementInput({
      reasonCode: input.entry.reasonCode,
      amountCents: input.entry.amountCents,
      note: input.note,
    });
    if (!checked.ok) throw new CashMovementValidationError(checked.code);
    reasonCode = checked.reasonCode;
    amountCents = checked.amountCents;
    direction = CASH_MOVEMENT_NATURAL_DIRECTIONS[reasonCode];
  }
  // 冲正的金额也要过一遍（原记录来自本地库，理论上已合法，这里兜底）。
  const checkedNote = validateCashMovementInput({
    reasonCode,
    amountCents,
    note: input.note,
    isVoid,
  });
  if (!checkedNote.ok) throw new CashMovementValidationError(checkedNote.code);

  const cashierId = boundedText(
    input.cashier.cashierId,
    CASH_MOVEMENT_MAXIMUM_CASHIER_ID_LENGTH,
  );
  const cashierName = boundedText(
    input.cashier.cashierName,
    CASH_MOVEMENT_MAXIMUM_CASHIER_NAME_LENGTH,
  );
  if (cashierId === null || cashierName === null) {
    throw new CashMovementValidationError("CASHIER_INVALID");
  }
  let authorizedById: string | null = null;
  let authorizedByName: string | null = null;
  if (input.authorizedBy) {
    authorizedById = boundedText(
      input.authorizedBy.id,
      CASH_MOVEMENT_MAXIMUM_CASHIER_ID_LENGTH,
    );
    authorizedByName = boundedText(
      input.authorizedBy.name,
      CASH_MOVEMENT_MAXIMUM_CASHIER_NAME_LENGTH,
    );
    if (authorizedById === null || authorizedByName === null) {
      throw new CashMovementValidationError("AUTHORIZER_INVALID");
    }
  }
  const storeCode = boundedText(
    input.storeCode,
    CASH_MOVEMENT_MAXIMUM_SCOPE_CODE_LENGTH,
  );
  const deviceCode = boundedText(
    input.deviceCode,
    CASH_MOVEMENT_MAXIMUM_SCOPE_CODE_LENGTH,
  );
  if (storeCode === null || deviceCode === null) {
    throw new CashMovementValidationError("SCOPE_INVALID");
  }

  const movement: CashMovementDraft = Object.freeze({
    movementGuid: input.movementGuid,
    direction,
    reasonCode,
    amountCents,
    note: checkedNote.note,
    voidsMovementGuid,
    businessDate,
    occurredAtIso,
    cashierId,
    cashierName,
    authorizedById,
    authorizedByName,
    storeCode,
    deviceCode,
  });
  return Object.freeze({
    movement,
    audit: buildCashMovementAudit(movement, input.auditEventId, input.cashier.userGuid),
  });
}

/**
 * 审计事件。键必须保持在安全集合内：source / action / status / reason / paymentMethod 会随上传白名单
 * 进入服务端 properties；amountDeltaCents 映射为 DTO 的 amountDelta（带符号）；
 * requesting* 三键是 actor 快照（上传期不得回读当前登录者）。
 * voidsMovementGuid 与 authorizing* 只存本地，不在上传白名单内。
 */
export function buildCashMovementAudit(
  movement: CashMovementFact,
  auditEventId: string,
  userGuid: string | null,
): AuditEventDraft {
  const isVoid = movement.voidsMovementGuid !== null;
  const action = isVoid ? "void" : movement.direction === "In" ? "in" : "out";
  const signedCents =
    movement.direction === "In" ? movement.amountCents : -movement.amountCents;
  return Object.freeze({
    eventId: auditEventId,
    eventType: CASH_MOVEMENT_AUDIT_EVENT_TYPES[action],
    occurredAtIso: movement.occurredAtIso,
    orderGuid: null,
    correlationId: movement.movementGuid,
    payload: Object.freeze({
      source: "cash-movement",
      action,
      status: "completed",
      reason: movement.reasonCode,
      paymentMethod: "Cash",
      amountDeltaCents: signedCents,
      ...(isVoid ? { voidsMovementGuid: movement.voidsMovementGuid } : {}),
      ...(movement.authorizedById !== null
        ? { authorizingCashierId: movement.authorizedById }
        : {}),
      ...auditActorPayload({
        cashierId: movement.cashierId,
        cashierName: movement.cashierName,
        userGuid,
      }),
    }),
  });
}

type SummarizableMovement = Pick<
  CashMovementFact,
  "direction" | "reasonCode" | "amountCents"
>;

/**
 * 日结口径：cashIn = ChangeTopUp + OtherIn，cashOut = SafeDrop + PaidOut，
 * 按原因码累加：方向等于自然方向的记录加、冲正记录（方向相反）减，一对「原记录 + 冲正」抵消为 0。
 * 与上传状态、开关无关。任一原因码或合计为负说明有孤立冲正，直接抛错（失败关闭，
 * 不能悄悄把负数压成 0 再让应有现金与钱箱对不上）。
 */
export function summarizeCashMovements(
  movements: readonly SummarizableMovement[],
): CashMovementSummary {
  const byReason: Record<CashMovementReasonCode, number> = {
    ChangeTopUp: 0,
    OtherIn: 0,
    SafeDrop: 0,
    PaidOut: 0,
  };
  for (const movement of movements) {
    if (
      !isCashMovementReasonCode(movement.reasonCode) ||
      (movement.direction !== "In" && movement.direction !== "Out") ||
      !Number.isSafeInteger(movement.amountCents) ||
      movement.amountCents <= 0
    ) {
      throw new TypeError("Cash movement record is invalid.");
    }
    const sign =
      movement.direction === CASH_MOVEMENT_NATURAL_DIRECTIONS[movement.reasonCode]
        ? 1
        : -1;
    const next = byReason[movement.reasonCode] + sign * movement.amountCents;
    if (!Number.isSafeInteger(next)) {
      throw new TypeError("Cash movement total exceeds safe integer bounds.");
    }
    byReason[movement.reasonCode] = next;
  }
  for (const reason of CASH_MOVEMENT_REASON_CODES) {
    if (byReason[reason] < 0) {
      throw new RangeError(
        `Cash movement total for ${reason} is negative (orphan void).`,
      );
    }
  }
  const cashInCents = byReason.ChangeTopUp + byReason.OtherIn;
  const cashOutCents = byReason.SafeDrop + byReason.PaidOut;
  if (!Number.isSafeInteger(cashInCents) || !Number.isSafeInteger(cashOutCents)) {
    throw new TypeError("Cash movement total exceeds safe integer bounds.");
  }
  return Object.freeze({
    cashInCents,
    cashOutCents,
    byReason: Object.freeze({ ...byReason }),
  });
}

export type CashMovementVoidDenial =
  | "IS_VOID_RECORD"
  | "NOT_TODAY"
  | "ALREADY_VOIDED"
  | "DAILY_CLOSE_SAVED";

export type CashMovementVoidEligibility =
  | Readonly<{ ok: true }>
  | Readonly<{ ok: false; reason: CashMovementVoidDenial }>;

/**
 * 冲正资格（服务端不校验，客户端必须自己守）：
 * 1. 原记录不能本身是冲正；
 * 2. 原记录的营业日必须等于「今天」（todayBusinessDate）；
 * 3. 每条原记录只能被冲正一次；
 * 4. 今天没有在该记录发生之后保存过日结（日结已经把它算进去了，再冲正会让存档与钱箱对不上）。
 *    保存时间不早于记录发生时间一律视为「之后」（含同一毫秒，宁可拒绝）。
 */
export function canVoidCashMovement(
  input: Readonly<{
    movement: Pick<
      CashMovementFact,
      "voidsMovementGuid" | "businessDate" | "occurredAtIso"
    >;
    alreadyVoided: boolean;
    todayBusinessDate: string;
    latestDailyCloseSavedAtIso: string | null;
  }>,
): CashMovementVoidEligibility {
  if (input.movement.voidsMovementGuid !== null) {
    return { ok: false, reason: "IS_VOID_RECORD" };
  }
  if (input.movement.businessDate !== input.todayBusinessDate) {
    return { ok: false, reason: "NOT_TODAY" };
  }
  if (input.alreadyVoided) {
    return { ok: false, reason: "ALREADY_VOIDED" };
  }
  if (input.latestDailyCloseSavedAtIso !== null) {
    const savedAt = Date.parse(input.latestDailyCloseSavedAtIso);
    const occurredAt = Date.parse(input.movement.occurredAtIso);
    // 时间无法解析时失败关闭：当作已日结，拒绝冲正。
    if (
      !Number.isFinite(savedAt) ||
      !Number.isFinite(occurredAt) ||
      savedAt >= occurredAt
    ) {
      return { ok: false, reason: "DAILY_CLOSE_SAVED" };
    }
  }
  return { ok: true };
}

function boundedText(value: string, maxLength: number): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (
    trimmed.length === 0 ||
    trimmed.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(trimmed)
  ) {
    return null;
  }
  return trimmed;
}
