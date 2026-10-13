import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { fromByteArray } from "base64-js";
import type { CreateEntryInput } from "./entry-form";
import { buildCreateEntryMultipart, buildMultipartBody, createMultipartBoundary, encodeUtf8 } from "./multipart";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(moduleDir, "../../../../..");

// ───────── UTF-8 编码与标准实现一致（含中文、emoji、孤立代理项） ─────────
for (const sample of ["", "abc", "证据编号 INC-1", "说明：暴力威胁😀", "éè", "a\ud800b"]) {
  const expected = new TextEncoder().encode(sample);
  assert.deepEqual(Array.from(encodeUtf8(sample)), Array.from(expected), `UTF-8: ${JSON.stringify(sample)}`);
}

// ───────── 边界 ─────────
{
  const a = createMultipartBoundary(() => 0.123456);
  assert.match(a, /^----HbFaceMatch[0-9a-f]{16}$/);
  assert.notEqual(createMultipartBoundary(() => 0.1), createMultipartBoundary(() => 0.9));
}

// ───────── 手工拼出的请求体结构 ─────────
{
  const body = buildMultipartBody(
    [{ name: "a", value: "1" }, { name: 'we"ird\r\n', value: "x" }],
    [{ name: "photos", filename: 'p"1\r\n.jpg', contentType: "image/jpeg", bytes: Uint8Array.from([0xff, 0xd8, 0xff, 0x00, 0x0d, 0x0a]) }],
    "BOUND",
  );
  const text = Buffer.from(body).toString("latin1");
  assert.ok(text.startsWith("--BOUND\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\n1\r\n"));
  assert.ok(text.includes('name="weird"'), "头部里的引号与换行被剔除，不能注入新头");
  assert.ok(text.includes('name="photos"; filename="p1.jpg"\r\nContent-Type: image/jpeg\r\n\r\n'));
  assert.ok(text.endsWith("\r\n--BOUND--\r\n"));
  // 二进制原样保留
  const marker = Buffer.from([0xff, 0xd8, 0xff, 0x00, 0x0d, 0x0a]);
  assert.ok(Buffer.from(body).includes(marker));
}

// ───────── 提交条目：字段名、顺序与 Web 完全一致 ─────────
function parseFieldNames(body: ArrayBuffer): { fields: Map<string, string[]>; fileParts: number } {
  const text = Buffer.from(body).toString("latin1");
  const fields = new Map<string, string[]>();
  let fileParts = 0;
  const partPattern = /Content-Disposition: form-data; name="([^"]+)"(; filename="([^"]*)")?\r\n(?:Content-Type: [^\r]*\r\n)?\r\n/g;
  let match: RegExpExecArray | null;
  while ((match = partPattern.exec(text))) {
    if (match[2]) {
      fileParts += 1;
      continue;
    }
    const start = match.index + match[0].length;
    const end = text.indexOf("\r\n--", start);
    const list = fields.get(match[1]) ?? [];
    list.push(Buffer.from(text.slice(start, end), "latin1").toString("utf8"));
    fields.set(match[1], list);
  }
  return { fields, fileParts };
}

function entryInput(overrides: Partial<CreateEntryInput> = {}): CreateEntryInput {
  const jpeg = Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3]);
  return {
    incidentCategory: 3,
    categoryNote: "情形说明（中文）",
    evidenceRef: "INC-2026-001",
    storeScope: ["1008", "1009"],
    expiresAtUtc: "2026-12-30T13:00:00.000Z",
    photos: [
      { key: "k1", base64: fromByteArray(jpeg), byteSize: jpeg.length, width: 10, height: 10, previewDataUri: "data:image/jpeg;base64,AA==" },
      { key: "k2", base64: fromByteArray(jpeg), byteSize: jpeg.length, width: 10, height: 10, previewDataUri: "data:image/jpeg;base64,AA==" },
    ],
    ...overrides,
  };
}

{
  const built = buildCreateEntryMultipart(entryInput(), "TESTBOUNDARY");
  assert.equal(built.contentType, "multipart/form-data; boundary=TESTBOUNDARY");
  assert.ok(built.body instanceof ArrayBuffer);
  const parsed = parseFieldNames(built.body);
  assert.deepEqual([...parsed.fields.keys()], ["incidentCategory", "categoryNote", "evidenceRef", "storeScope", "expiresAtUtc"]);
  assert.deepEqual(parsed.fields.get("incidentCategory"), ["3"]);
  assert.deepEqual(parsed.fields.get("categoryNote"), ["情形说明（中文）"], "中文说明按 UTF-8 编码");
  assert.deepEqual(parsed.fields.get("storeScope"), ["1008,1009"], "storeScope 逗号拼接");
  assert.deepEqual(parsed.fields.get("expiresAtUtc"), ["2026-12-30T13:00:00.000Z"]);
  assert.equal(parsed.fileParts, 2, "photos 重复字段，每张一个文件部分");
  const text = Buffer.from(built.body).toString("latin1");
  assert.ok(text.includes('name="photos"; filename="photo-1.jpg"'));
  assert.ok(text.includes('name="photos"; filename="photo-2.jpg"'));
  assert.ok(text.includes("Content-Type: image/jpeg"));
  // 文件内容就是压缩后的 JPEG 字节
  assert.ok(Buffer.from(built.body).includes(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3])));
  // 请求体是精确长度的 ArrayBuffer（axios 取 .buffer 时不能带多余字节）
  assert.equal(built.body.byteLength, Buffer.from(built.body).length);
}

// 非 Other 类别不带 categoryNote 字段
{
  const parsed = parseFieldNames(buildCreateEntryMultipart(entryInput({ incidentCategory: 1, categoryNote: null }), "B").body);
  assert.equal(parsed.fields.has("categoryNote"), false);
}

// ───────── 与 Web 契约一致：从 Web 源码读出 FormData 字段名逐一对照 ─────────
{
  const webSource = readFileSync(resolve(repoRoot, "apps/web/src/services/faceMatchService.ts"), "utf8");
  const start = webSource.indexOf("export function buildUnwelcomeEntryFormData");
  assert.ok(start >= 0, "Web 的 buildUnwelcomeEntryFormData 找不到了：契约对照需要更新");
  const end = webSource.indexOf("export async function createUnwelcomeEntry", start);
  const webFunction = webSource.slice(start, end);
  const webNames = Array.from(webFunction.matchAll(/form\.append\('([A-Za-z]+)'/g), (match) => match[1]);
  assert.deepEqual(webNames, ["incidentCategory", "categoryNote", "evidenceRef", "storeScope", "expiresAtUtc", "photos"]);
  const mobileFields = [...parseFieldNames(buildCreateEntryMultipart(entryInput(), "B").body).fields.keys(), "photos"];
  assert.deepEqual(mobileFields, webNames, "移动端 multipart 字段名与顺序必须和 Web 完全一致");
  assert.ok(webFunction.includes("input.storeScope.join(',')"), "Web 的 storeScope 以逗号拼接");
}

console.log("multipart.test.ts: ok");
