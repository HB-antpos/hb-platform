import {
  normalizeCardSyncEvidence,
  type CardSyncEvidenceV1,
  type PaymentOperation,
} from "@hb/pos-domain/core/contracts/payment";

import { ProtectedMaterialIntegrityError } from "./protected-material-integrity-error";
import type { SqliteConnectionPort } from "./types";

type PaymentProtectedMaterialEncryptor = Readonly<{
  encrypt(plaintext: string): Promise<Uint8Array>;
  decrypt(ciphertext: Uint8Array): Promise<string>;
}>;

export type PaymentProtectedMaterialBinding = Readonly<{
  attemptId: string;
  orderGuid: string;
  provider: "square" | "linkly-cloud" | "manual-card" | "zeller";
  operation: PaymentOperation;
  /** 与 attempt 账本一致：purchase 为正、refund 为负。 */
  amountCents: number;
}>;

export type PaymentProtectedMaterial = Readonly<{
  voucherReservationToken: string | null;
  cardSyncEvidence: CardSyncEvidenceV1 | null;
}>;

type PaymentProtectedEnvelopeV1 = Readonly<{
  version: 1;
  voucherReservationToken: string | null;
  cardSyncEvidence: CardSyncEvidenceV1 | null;
}>;

type PaymentProtectedMaterialRow = Readonly<{
  attempt_id: unknown;
  order_guid: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  provider_payload_ciphertext: unknown;
}>;

const ENVELOPE_V1_KEYS = new Set([
  "version",
  "voucherReservationToken",
  "cardSyncEvidence",
]);
const LEGACY_KEYS = new Set(["voucherReservationToken"]);

/**
 * 统一二次加密 envelope。空 payload 继续保存为 SQL NULL；只要存在任一
 * 受保护字段，新写入一律使用 version 1，旧版 token shape 仅用于读取兼容。
 */
export async function encryptPaymentProtectedMaterial(
  encryptor: PaymentProtectedMaterialEncryptor,
  material: Readonly<{
    voucherReservationToken: string | null;
    cardSyncEvidence: unknown | null;
  }>,
): Promise<Uint8Array | null> {
  const voucherReservationToken = normalizeVoucherReservationToken(
    material.voucherReservationToken,
  );
  const cardSyncEvidence =
    material.cardSyncEvidence === null
      ? null
      : normalizeProtectedCardSyncEvidence(material.cardSyncEvidence);
  if (voucherReservationToken === null && cardSyncEvidence === null) {
    return null;
  }
  const envelope: PaymentProtectedEnvelopeV1 = {
    version: 1,
    voucherReservationToken,
    cardSyncEvidence,
  };
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Payment protected material encryption failed.");
  }
  return ciphertext;
}

/**
 * 只把成功解密后的确定性 JSON/schema 损坏分类为 integrity error。
 * encryptor 的 Keychain、解密和 IO 错误保持原类型向上传递。
 */
export async function decryptPaymentProtectedMaterial(
  encryptor: Pick<PaymentProtectedMaterialEncryptor, "decrypt">,
  ciphertext: Uint8Array | null,
): Promise<PaymentProtectedMaterial> {
  if (ciphertext === null) {
    return Object.freeze({
      voucherReservationToken: null,
      cardSyncEvidence: null,
    });
  }
  const plaintext = await encryptor.decrypt(ciphertext);
  let parsed: unknown;
  try {
    parsed = JSON.parse(plaintext);
  } catch {
    throw new ProtectedMaterialIntegrityError(
      "PROTECTED_MATERIAL_JSON_INVALID",
    );
  }
  if (!isRecord(parsed)) {
    throw new ProtectedMaterialIntegrityError(
      "PROTECTED_MATERIAL_SHAPE_INVALID",
    );
  }

  if (!Object.hasOwn(parsed, "version")) {
    if (!hasExactKeys(parsed, LEGACY_KEYS)) {
      throw new ProtectedMaterialIntegrityError(
        "PROTECTED_MATERIAL_SHAPE_INVALID",
      );
    }
    return Object.freeze({
      voucherReservationToken: integrityVoucherReservationToken(
        parsed.voucherReservationToken,
      ),
      cardSyncEvidence: null,
    });
  }
  if (parsed.version !== 1) {
    throw new ProtectedMaterialIntegrityError(
      "PROTECTED_MATERIAL_VERSION_INVALID",
    );
  }
  if (!hasExactKeys(parsed, ENVELOPE_V1_KEYS)) {
    throw new ProtectedMaterialIntegrityError(
      "PROTECTED_MATERIAL_SHAPE_INVALID",
    );
  }

  const voucherReservationToken = integrityVoucherReservationToken(
    parsed.voucherReservationToken,
  );
  let cardSyncEvidence: CardSyncEvidenceV1 | null;
  if (parsed.cardSyncEvidence === null) {
    cardSyncEvidence = null;
  } else {
    try {
      cardSyncEvidence = normalizeProtectedCardSyncEvidence(
        parsed.cardSyncEvidence,
      );
    } catch (error) {
      if (error instanceof TypeError) {
        throw new ProtectedMaterialIntegrityError(
          "PROTECTED_MATERIAL_SHAPE_INVALID",
        );
      }
      throw error;
    }
  }
  return Object.freeze({
    voucherReservationToken,
    cardSyncEvidence,
  });
}

/**
 * 同步专用窄 reader：按 attempt 的完整耐久身份读取，不返回 voucher token，
 * 不缓存 evidence，也不把它挂到公开 PaymentAttempt。
 */
export class SqlitePaymentProtectedMaterialReader {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly encryptor: Pick<PaymentProtectedMaterialEncryptor, "decrypt">,
  ) {}

  public async read(
    bindingInput: PaymentProtectedMaterialBinding,
  ): Promise<CardSyncEvidenceV1 | null> {
    const binding = normalizeBinding(bindingInput);
    const row = await this.connection.getFirst<PaymentProtectedMaterialRow>(
      `SELECT attempt_id, order_guid, provider, operation, amount_cents,
        provider_payload_ciphertext
       FROM payment_attempts
       WHERE attempt_id = ?`,
      [binding.attemptId],
    );
    if (!row) return null;
    if (!rowMatchesBinding(row, binding)) {
      throw new ProtectedMaterialIntegrityError(
        "PROTECTED_MATERIAL_BINDING_MISMATCH",
      );
    }
    const ciphertext = protectedCiphertext(row.provider_payload_ciphertext);
    const material = await decryptPaymentProtectedMaterial(
      this.encryptor,
      ciphertext,
    );
    const evidence = material.cardSyncEvidence;
    if (evidence === null) return null;
    if (
      evidence.provider !== binding.provider ||
      evidence.operation !== binding.operation ||
      evidence.amountCents !== Math.abs(binding.amountCents)
    ) {
      throw new ProtectedMaterialIntegrityError(
        "PROTECTED_MATERIAL_BINDING_MISMATCH",
      );
    }
    return evidence;
  }
}

function normalizeBinding(
  input: PaymentProtectedMaterialBinding,
): PaymentProtectedMaterialBinding {
  const attemptId = nonBlankInput(input.attemptId, "attempt id");
  const orderGuid = nonBlankInput(input.orderGuid, "order guid");
  if (
    input.provider !== "square" &&
    input.provider !== "linkly-cloud" &&
    input.provider !== "manual-card" &&
    input.provider !== "zeller"
  ) {
    throw new TypeError("Payment protected material provider is invalid.");
  }
  if (input.operation !== "purchase" && input.operation !== "refund") {
    throw new TypeError("Payment protected material operation is invalid.");
  }
  if (
    !Number.isSafeInteger(input.amountCents) ||
    input.amountCents === 0 ||
    (input.operation === "purchase" && input.amountCents < 0) ||
    (input.operation === "refund" && input.amountCents > 0)
  ) {
    throw new TypeError(
      "Payment protected material amount sign is invalid.",
    );
  }
  return Object.freeze({
    attemptId,
    orderGuid,
    provider: input.provider,
    operation: input.operation,
    amountCents: input.amountCents,
  });
}

function rowMatchesBinding(
  row: PaymentProtectedMaterialRow,
  binding: PaymentProtectedMaterialBinding,
): boolean {
  return row.attempt_id === binding.attemptId &&
    row.order_guid === binding.orderGuid &&
    row.provider === binding.provider &&
    row.operation === binding.operation &&
    row.amount_cents === binding.amountCents;
}

function protectedCiphertext(value: unknown): Uint8Array | null {
  if (value === null) return null;
  if (!(value instanceof Uint8Array)) {
    throw new ProtectedMaterialIntegrityError(
      "PROTECTED_MATERIAL_SHAPE_INVALID",
    );
  }
  return value;
}

function normalizeVoucherReservationToken(value: unknown): string | null {
  if (value === null || typeof value === "string") return value;
  throw new TypeError("Voucher reservation token is invalid.");
}

function normalizeProtectedCardSyncEvidence(
  input: unknown,
): CardSyncEvidenceV1 {
  const evidence = normalizeCardSyncEvidence(input);
  const freeTextValues = [
    evidence.txnRef,
    evidence.authCode,
    evidence.cardType,
    evidence.merchantId,
    evidence.responseCode,
    evidence.responseText,
    evidence.stan,
    evidence.refundReference,
  ];
  if (
    freeTextValues.some(
      (value) => value !== null && containsUnmaskedPan(value),
    )
  ) {
    throw new TypeError(
      "Card sync evidence contains an unmasked PAN.",
    );
  }
  return evidence;
}

function containsUnmaskedPan(value: string): boolean {
  const collapsedDigits = value.replace(/\D/gu, "");
  if (
    collapsedDigits.length >= 13 &&
    collapsedDigits.length <= 19 &&
    passesLuhn(collapsedDigits)
  ) {
    return true;
  }
  for (const match of value.matchAll(/\d{13,19}/gu)) {
    const start = match.index;
    const end = start + match[0].length;
    if (
      (start > 0 && /\d/u.test(value[start - 1] ?? "")) ||
      (end < value.length && /\d/u.test(value[end] ?? ""))
    ) {
      continue;
    }
    if (passesLuhn(match[0])) return true;
  }
  return false;
}

function passesLuhn(digits: string): boolean {
  let sum = 0;
  let doubleDigit = false;
  for (let index = digits.length - 1; index >= 0; index -= 1) {
    let digit = Number(digits[index]);
    if (doubleDigit) {
      digit *= 2;
      if (digit > 9) digit -= 9;
    }
    sum += digit;
    doubleDigit = !doubleDigit;
  }
  return sum % 10 === 0;
}

function integrityVoucherReservationToken(value: unknown): string | null {
  try {
    return normalizeVoucherReservationToken(value);
  } catch {
    throw new ProtectedMaterialIntegrityError(
      "PROTECTED_MATERIAL_SHAPE_INVALID",
    );
  }
}

function hasExactKeys(
  value: Readonly<Record<string, unknown>>,
  allowed: ReadonlySet<string>,
): boolean {
  const keys = Object.keys(value);
  return keys.length === allowed.size &&
    keys.every((key) => allowed.has(key));
}

function nonBlankInput(value: unknown, label: string): string {
  if (typeof value !== "string" || value.trim() !== value || value.length === 0) {
    throw new TypeError(`Payment protected material ${label} is invalid.`);
  }
  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
