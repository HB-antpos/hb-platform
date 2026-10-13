// multipart/form-data 请求体的纯构造。
// 为什么手工构造而不用 RN 的 FormData：RN 的 FormData 文件部分只能引用磁盘上的 file:// 地址，参考照片是生物识别信息，
// 不能为了上传把它落盘到缓存目录；手工拼出的字节只存在于内存，axios 以 ArrayBuffer 直接发送。
import { toByteArray } from "base64-js";
import type { CreateEntryInput } from "./entry-form";

export interface MultipartField {
  name: string;
  value: string;
}

export interface MultipartFile {
  name: string;
  filename: string;
  contentType: string;
  bytes: Uint8Array;
}

/** UTF-8 编码（含代理对）；不依赖 TextEncoder，Hermes 各版本行为一致。 */
export function encodeUtf8(text: string): Uint8Array {
  const bytes: number[] = [];
  for (let index = 0; index < text.length; index += 1) {
    let codePoint = text.charCodeAt(index);
    if (codePoint >= 0xd800 && codePoint <= 0xdbff && index + 1 < text.length) {
      const next = text.charCodeAt(index + 1);
      if (next >= 0xdc00 && next <= 0xdfff) {
        codePoint = 0x10000 + ((codePoint - 0xd800) << 10) + (next - 0xdc00);
        index += 1;
      }
    }
    if (codePoint >= 0xd800 && codePoint <= 0xdfff) codePoint = 0xfffd; // 孤立代理项
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

/** 头部里的名字只允许普通字符，引号、反斜杠、换行一律剔除（防止头部注入）。 */
function sanitizeHeaderValue(value: string): string {
  return value.replace(/["\\\r\n]/g, "");
}

/** 随机边界；不得出现在任何字段内容里（照片是二进制，理论上可能撞上，概率可忽略）。 */
export function createMultipartBoundary(random: () => number = Math.random): string {
  const part = () => Math.floor(random() * 0xffffffff).toString(16).padStart(8, "0");
  return `----HbFaceMatch${part()}${part()}`;
}

/** 拼出完整的 multipart 请求体（字段在前，文件在后，结尾带终止边界）。 */
export function buildMultipartBody(
  fields: readonly MultipartField[],
  files: readonly MultipartFile[],
  boundary: string,
): Uint8Array {
  const chunks: Uint8Array[] = [];
  const text = (value: string) => chunks.push(encodeUtf8(value));
  for (const field of fields) {
    text(`--${boundary}\r\nContent-Disposition: form-data; name="${sanitizeHeaderValue(field.name)}"\r\n\r\n`);
    text(field.value);
    text("\r\n");
  }
  for (const file of files) {
    text(
      `--${boundary}\r\nContent-Disposition: form-data; name="${sanitizeHeaderValue(file.name)}"; ` +
        `filename="${sanitizeHeaderValue(file.filename)}"\r\nContent-Type: ${sanitizeHeaderValue(file.contentType)}\r\n\r\n`,
    );
    chunks.push(file.bytes);
    text("\r\n");
  }
  text(`--${boundary}--\r\n`);

  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const body = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    body.set(chunk, offset);
    offset += chunk.length;
  }
  return body;
}

export interface BuiltMultipartRequest {
  /** 恰好等于请求体长度的 ArrayBuffer（axios 对 ArrayBufferView 会取整块 buffer，必须没有多余字节）。 */
  body: ArrayBuffer;
  contentType: string;
}

/**
 * 提交名单条目的 multipart：字段名与 Web 的 buildUnwelcomeEntryFormData 完全一致 ——
 * incidentCategory、categoryNote（有才带）、evidenceRef、storeScope（逗号分隔）、expiresAtUtc，文件字段 photos 重复 1–5 次。
 * 文件名是固定的 photo-N.jpg，不带原始文件名与任何 EXIF。
 */
export function buildCreateEntryMultipart(
  input: CreateEntryInput,
  boundary: string = createMultipartBoundary(),
): BuiltMultipartRequest {
  const fields: MultipartField[] = [{ name: "incidentCategory", value: String(input.incidentCategory) }];
  if (input.categoryNote) fields.push({ name: "categoryNote", value: input.categoryNote });
  fields.push(
    { name: "evidenceRef", value: input.evidenceRef },
    { name: "storeScope", value: input.storeScope.join(",") },
    { name: "expiresAtUtc", value: input.expiresAtUtc },
  );
  const files: MultipartFile[] = input.photos.map((photo, index) => ({
    name: "photos",
    filename: `photo-${index + 1}.jpg`,
    contentType: "image/jpeg",
    bytes: toByteArray(photo.base64),
  }));
  const bytes = buildMultipartBody(fields, files, boundary);
  const body = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
  return { body, contentType: `multipart/form-data; boundary=${boundary}` };
}
