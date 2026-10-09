import type { PaymentAttempt } from "@hb/pos-domain/core/contracts/payment";

/**
 * 与 apps/pos-wpf/src/Hbpos.Contracts/Linkly/LinklyAttemptTxnRef.cs 逐位一致的 TxnRef 派生算法：
 * 交易类型字符 + SHA-256(`HBPOS-LINKLY-TXNREF-V1|<类型>|<attemptGuid 小写 D 格式>`) 前 75 位的 Base32。
 *
 * 为什么必须一致：POS 在发请求前就把该引用落到本地 attempt，请求里只带 attemptGuid，Hbpos.Api 按同一算法
 * 派生出同一个值送终端。create 响应丢失后，恢复只能靠这个顶层 TxnRef 认领会话——通知里的 UID/TxnRef
 * 会被服务端剥离或二次脱敏，不能当证据。算法只能有这一份语义，改动即破坏两端已落库记录的对应关系；
 * 跨语言向量见 test-fixtures/linkly-attempt-txnref/vectors.json（C# 与 TS 共用）。
 */
const HASH_INPUT_PREFIX = "HBPOS-LINKLY-TXNREF-V1";
const BASE32_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const CANONICAL_GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
const NIL_GUID = "00000000-0000-0000-0000-000000000000";

export type LinklyTxnRefType = "P" | "R";

/** attemptId 是标准 GUID（大小写不限）时返回小写 D 格式，否则 null——与 .NET Guid.ToString("D") 一致。 */
export function canonicalLinklyAttemptGuid(attemptId: unknown): string | null {
  if (typeof attemptId !== "string") return null;
  const normalized = attemptId.trim().toLowerCase();
  return CANONICAL_GUID.test(normalized) && normalized !== NIL_GUID ? normalized : null;
}

export function createLinklyAttemptTxnRef(
  transactionType: LinklyTxnRefType,
  attemptGuid: string,
): string {
  const guid = canonicalLinklyAttemptGuid(attemptGuid);
  if (guid === null) throw new Error("LINKLY_ATTEMPT_GUID_INVALID");
  if (transactionType !== "P" && transactionType !== "R") {
    throw new Error("LINKLY_TXN_TYPE_INVALID");
  }
  const digest = sha256(utf8Bytes(`${HASH_INPUT_PREFIX}|${transactionType}|${guid}`));
  let result: string = transactionType;
  for (let index = 0; index < 15; index += 1) {
    let value = 0;
    const bitOffset = index * 5;
    for (let bit = 0; bit < 5; bit += 1) {
      const absoluteBit = bitOffset + bit;
      const byteValue = digest[Math.floor(absoluteBit / 8)]!;
      value = (value << 1) | ((byteValue >> (7 - (absoluteBit % 8))) & 1);
    }
    result += BASE32_ALPHABET[value];
  }
  return result;
}

/**
 * 由本地 attempt 身份派生应落库的 Linkly TxnRef；attemptId 不是标准 GUID 时返回 null
 * （此时既不预置 TxnRef 也不向后端发送 attemptGuid，沿用旧的 UID 通知认领路径）。
 */
export function deriveLinklyAttemptTxnRef(
  attempt: Pick<PaymentAttempt, "attemptId" | "operation">,
): string | null {
  const guid = canonicalLinklyAttemptGuid(attempt.attemptId);
  if (guid === null) return null;
  return createLinklyAttemptTxnRef(attempt.operation === "refund" ? "R" : "P", guid);
}

function utf8Bytes(value: string): Uint8Array {
  const bytes: number[] = [];
  for (const symbol of value) {
    const codePoint = symbol.codePointAt(0)!;
    if (codePoint < 0x80) {
      bytes.push(codePoint);
    } else if (codePoint < 0x800) {
      bytes.push(0xc0 | (codePoint >> 6), 0x80 | (codePoint & 0x3f));
    } else if (codePoint < 0x10000) {
      bytes.push(0xe0 | (codePoint >> 12), 0x80 | ((codePoint >> 6) & 0x3f), 0x80 | (codePoint & 0x3f));
    } else {
      bytes.push(
        0xf0 | (codePoint >> 18),
        0x80 | ((codePoint >> 12) & 0x3f),
        0x80 | ((codePoint >> 6) & 0x3f),
        0x80 | (codePoint & 0x3f),
      );
    }
  }
  return Uint8Array.from(bytes);
}

const SHA256_K = Uint32Array.from([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

/**
 * 同步纯 JS SHA-256：React Native/Hermes 没有同步的 node:crypto，而 TxnRef 必须在 attempt 落库前同步算出。
 * 输入只有 ASCII GUID，数据量极小，不追求吞吐。
 */
function sha256(message: Uint8Array): Uint8Array {
  const bitLength = message.length * 8;
  const paddedLength = (((message.length + 9 + 63) >> 6) << 6);
  const padded = new Uint8Array(paddedLength);
  padded.set(message);
  padded[message.length] = 0x80;
  const view = new DataView(padded.buffer);
  // 输入远小于 2^32 位，高 32 位恒为 0。
  view.setUint32(paddedLength - 8, Math.floor(bitLength / 0x100000000), false);
  view.setUint32(paddedLength - 4, bitLength >>> 0, false);

  const hash = Uint32Array.from([
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
  ]);
  const words = new Uint32Array(64);
  const rotr = (value: number, amount: number) => (value >>> amount) | (value << (32 - amount));

  for (let offset = 0; offset < paddedLength; offset += 64) {
    for (let index = 0; index < 16; index += 1) {
      words[index] = view.getUint32(offset + index * 4, false);
    }
    for (let index = 16; index < 64; index += 1) {
      const w15 = words[index - 15]!;
      const w2 = words[index - 2]!;
      const s0 = rotr(w15, 7) ^ rotr(w15, 18) ^ (w15 >>> 3);
      const s1 = rotr(w2, 17) ^ rotr(w2, 19) ^ (w2 >>> 10);
      words[index] = (words[index - 16]! + s0 + words[index - 7]! + s1) >>> 0;
    }

    let [a, b, c, d, e, f, g, h] = hash as unknown as [
      number, number, number, number, number, number, number, number,
    ];
    for (let index = 0; index < 64; index += 1) {
      const sum1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
      const choice = (e & f) ^ (~e & g);
      const temp1 = (h + sum1 + choice + SHA256_K[index]! + words[index]!) >>> 0;
      const sum0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
      const majority = (a & b) ^ (a & c) ^ (b & c);
      const temp2 = (sum0 + majority) >>> 0;
      h = g;
      g = f;
      f = e;
      e = (d + temp1) >>> 0;
      d = c;
      c = b;
      b = a;
      a = (temp1 + temp2) >>> 0;
    }
    hash[0] = (hash[0]! + a) >>> 0;
    hash[1] = (hash[1]! + b) >>> 0;
    hash[2] = (hash[2]! + c) >>> 0;
    hash[3] = (hash[3]! + d) >>> 0;
    hash[4] = (hash[4]! + e) >>> 0;
    hash[5] = (hash[5]! + f) >>> 0;
    hash[6] = (hash[6]! + g) >>> 0;
    hash[7] = (hash[7]! + h) >>> 0;
  }

  const digest = new Uint8Array(32);
  const out = new DataView(digest.buffer);
  for (let index = 0; index < 8; index += 1) out.setUint32(index * 4, hash[index]!, false);
  return digest;
}
