import { DecodeUTF8, Gunzip } from "fflate";

import {
  normalizeCatalogFileDeletedLookup,
  normalizeCatalogFileItem,
  type CatalogDeletedLookup,
  type CatalogLookupItem,
} from "./hbpos-catalog-remote";

import {
  HbposApiError,
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@/core/api/hbpos-api";
import type { components } from "@hb/pos-api-client/openapi";

/**
 * 整文件目录下载（与 WPF 同一协议）：服务端按版本把目录写成不可变的 gzip NDJSON 文件，
 * 客户端按 Range 分段下载到缓存目录（断点续传）、整份 SHA-256 校验后，边解压边逐行写入 staging。
 * 任何一步失败都由调用方回退分页协议；这里绝不触碰 active 目录。
 */

const FULL_FORMAT = "hbpos-catalog-full-v1";
const DELTA_FORMAT = "hbpos-catalog-delta-v1";
const FILE_PATH_PREFIX = "api/v1/catalog/files/";
const PART_SUFFIX = ".part";
const VERIFIED_SUFFIX = ".ndjson.gz";
const DEFAULT_SEGMENT_BYTES = 4 * 1024 * 1024;
const DEFAULT_READ_CHUNK_BYTES = 1024 * 1024;
const DEFAULT_RETRY_DELAYS_MS: readonly number[] = [3_000, 10_000, 30_000];

type GeneratedFileSyncPlan = components["schemas"]["CatalogFileSyncPlanResponse"];

export type CatalogDownloadFile = Readonly<{
  kind: "full" | "delta";
  format: string;
  path: string;
  bytes: number;
  sha256: string;
}>;

export type CatalogFileSyncPlan = Readonly<{
  storeCode: string;
  mode: "noChange" | "delta" | "full";
  baseCatalogVersion: string | null;
  targetCatalogVersion: string;
  targetTotal: number;
  deltaOperationCount: number | null;
  file: CatalogDownloadFile | null;
}>;

/** 只按文件名操作同一个缓存目录；平台实现负责把名称映射到真实路径。 */
export interface CatalogFileStorePort {
  size(name: string): Promise<number | null>;
  append(name: string, bytes: Uint8Array): Promise<void>;
  /** 清空（不存在则创建空文件）。 */
  truncate(name: string): Promise<void>;
  readChunks(name: string, chunkSize: number): AsyncIterable<Uint8Array>;
  readAll(name: string): Promise<Uint8Array<ArrayBuffer>>;
  rename(from: string, to: string): Promise<void>;
  delete(name: string): Promise<void>;
  list(): Promise<readonly string[]>;
}

/** 返回小写十六进制 SHA-256。 */
export type CatalogFileDigest = (bytes: Uint8Array<ArrayBuffer>) => Promise<string>;

export interface CatalogFileSyncPort {
  getPlan(input: Readonly<{
    storeCode: string;
    baseCatalogVersion: string | null;
    signal?: AbortSignal;
  }>): Promise<CatalogFileSyncPlan>;
  /** 下载并校验，返回缓存目录内已校验文件的名称；字节数或 SHA-256 不符的文件绝不返回。 */
  download(input: Readonly<{
    file: CatalogDownloadFile;
    signal?: AbortSignal;
    onBytes?: (downloadedBytes: number, totalBytes: number) => void;
  }>): Promise<string>;
  readFull(
    name: string,
    expected: Readonly<{ storeCode: string; catalogVersion: string; totalCount: number }>,
    batchSize: number,
    signal?: AbortSignal,
  ): AsyncIterable<readonly CatalogLookupItem[]>;
  readDelta(
    name: string,
    expected: Readonly<{
      storeCode: string;
      baseCatalogVersion: string;
      targetCatalogVersion: string;
      operationCount: number;
    }>,
    signal?: AbortSignal,
  ): Promise<Readonly<{
    items: readonly CatalogLookupItem[];
    deletedLookups: readonly CatalogDeletedLookup[];
  }>>;
  release(name: string): Promise<void>;
}

export type HbposCatalogFileSyncOptions = Readonly<{
  transport: HbposTransport;
  store: CatalogFileStorePort;
  digest: CatalogFileDigest;
  segmentBytes?: number;
  readChunkBytes?: number;
  retryDelaysMs?: readonly number[];
  delay?: (milliseconds: number, signal?: AbortSignal) => Promise<void>;
}>;

export class HbposCatalogFileSync implements CatalogFileSyncPort {
  private readonly segmentBytes: number;
  private readonly readChunkBytes: number;
  private readonly retryDelaysMs: readonly number[];
  private readonly delay: (milliseconds: number, signal?: AbortSignal) => Promise<void>;

  public constructor(private readonly options: HbposCatalogFileSyncOptions) {
    this.segmentBytes = options.segmentBytes ?? DEFAULT_SEGMENT_BYTES;
    this.readChunkBytes = options.readChunkBytes ?? DEFAULT_READ_CHUNK_BYTES;
    this.retryDelaysMs = options.retryDelaysMs ?? DEFAULT_RETRY_DELAYS_MS;
    this.delay = options.delay ?? delayMilliseconds;
  }

  public async getPlan(input: Readonly<{
    storeCode: string;
    baseCatalogVersion: string | null;
    signal?: AbortSignal;
  }>): Promise<CatalogFileSyncPlan> {
    const response = await this.options.transport.request<HbposEnvelope<GeneratedFileSyncPlan>>({
      method: "GET",
      url: "/api/v1/catalog/files/sync-plan",
      params: {
        storeCode: input.storeCode,
        baseCatalogVersion: input.baseCatalogVersion ?? undefined,
      },
      // 首次生成文件可能需要数秒；由页面生命周期信号负责主动取消。
      timeoutMs: 0,
      ...(input.signal ? { signal: input.signal } : {}),
    });
    return normalizeFilePlan(unwrapHbposEnvelope(response.data), input);
  }

  public async download(input: Readonly<{
    file: CatalogDownloadFile;
    signal?: AbortSignal;
    onBytes?: (downloadedBytes: number, totalBytes: number) => void;
  }>): Promise<string> {
    const { file } = input;
    const sha256 = file.sha256;
    const partName = sha256 + PART_SUFFIX;
    const verifiedName = sha256 + VERIFIED_SUFFIX;
    await this.removeOtherFiles(sha256);
    if (await this.options.store.size(verifiedName) === file.bytes) {
      if (await this.matches(verifiedName, sha256)) {
        input.onBytes?.(file.bytes, file.bytes);
        return verifiedName;
      }
      await this.options.store.delete(verifiedName);
    }

    // 残片损坏或范围被拒时只允许从头重下一次，避免反复下载同一个坏文件。
    let restartedFromScratch = false;
    let retryAttempt = 0;
    while (true) {
      throwIfAborted(input.signal);
      try {
        await this.downloadRemaining(file, partName, input.signal, input.onBytes);
      } catch (error) {
        throwIfAborted(input.signal);
        if (isRangeRejected(error) && !restartedFromScratch) {
          await this.options.store.delete(partName);
          restartedFromScratch = true;
          continue;
        }
        if (isTransientDownloadError(error) && retryAttempt < this.retryDelaysMs.length) {
          // 中文注释：断网、网关错误都保留残片，等一会儿从断点继续。
          await this.delay(this.retryDelaysMs[retryAttempt++] ?? 0, input.signal);
          continue;
        }
        throw error;
      }

      if (await this.matches(partName, sha256)) {
        await this.options.store.rename(partName, verifiedName);
        return verifiedName;
      }
      // 中文注释：整份校验不过说明残片被污染或中途换了内容，删掉从头再下一次；仍不对就放弃，交给分页协议。
      await this.options.store.delete(partName);
      if (restartedFromScratch) {
        throw fileError("Catalog file checksum does not match.", "CATALOG_FILE_CHECKSUM_MISMATCH");
      }
      restartedFromScratch = true;
    }
  }

  public async *readFull(
    name: string,
    expected: Readonly<{ storeCode: string; catalogVersion: string; totalCount: number }>,
    batchSize: number,
    signal?: AbortSignal,
  ): AsyncIterable<readonly CatalogLookupItem[]> {
    let header: FullHeader | null = null;
    let batch: CatalogLookupItem[] = [];
    let count = 0;
    for await (const line of this.readLines(name, signal)) {
      if (header === null) {
        header = parseFullHeader(line);
        if (
          header.format !== FULL_FORMAT
          || header.storeCode !== expected.storeCode
          || header.catalogVersion !== expected.catalogVersion
          || header.totalCount !== expected.totalCount
        ) {
          throw fileError("Catalog full file header does not match the sync plan.", "CATALOG_FILE_INVALID");
        }
        continue;
      }
      batch.push(normalizeFileLine(line, normalizeCatalogFileItem));
      count += 1;
      if (batch.length >= batchSize) {
        yield batch;
        batch = [];
      }
    }
    if (header === null) throw fileError("Catalog full file is empty.", "CATALOG_FILE_INVALID");
    if (batch.length > 0) yield batch;
    if (count !== header.totalCount) {
      throw fileError("Catalog full file is incomplete.", "CATALOG_FILE_INVALID");
    }
  }

  public async readDelta(
    name: string,
    expected: Readonly<{
      storeCode: string;
      baseCatalogVersion: string;
      targetCatalogVersion: string;
      operationCount: number;
    }>,
    signal?: AbortSignal,
  ): Promise<Readonly<{
    items: readonly CatalogLookupItem[];
    deletedLookups: readonly CatalogDeletedLookup[];
  }>> {
    let header: DeltaHeader | null = null;
    // 增量操作数受服务端阈值（5000）约束，整体放进内存后一次写入 delta staging。
    const items: CatalogLookupItem[] = [];
    const deletedLookups: CatalogDeletedLookup[] = [];
    for await (const line of this.readLines(name, signal)) {
      if (header === null) {
        header = parseDeltaHeader(line);
        if (
          header.format !== DELTA_FORMAT
          || header.storeCode !== expected.storeCode
          || header.baseCatalogVersion !== expected.baseCatalogVersion
          || header.targetCatalogVersion !== expected.targetCatalogVersion
          || header.operationCount !== expected.operationCount
        ) {
          throw fileError("Catalog delta file header does not match the sync plan.", "CATALOG_FILE_INVALID");
        }
        continue;
      }
      const operation = parseJsonLine(line) as Readonly<{ item?: unknown; deleted?: unknown }>;
      const hasItem = operation.item !== null && operation.item !== undefined;
      const hasDeleted = operation.deleted !== null && operation.deleted !== undefined;
      if (hasItem === hasDeleted) {
        throw fileError("Catalog delta line must contain exactly one operation.", "CATALOG_FILE_INVALID");
      }
      if (hasItem) {
        items.push(normalizeFileLine(operation.item, normalizeCatalogFileItem));
      } else {
        deletedLookups.push(normalizeFileLine(operation.deleted, normalizeCatalogFileDeletedLookup));
      }
    }
    if (header === null) throw fileError("Catalog delta file is empty.", "CATALOG_FILE_INVALID");
    if (items.length + deletedLookups.length !== header.operationCount) {
      throw fileError("Catalog delta file is incomplete.", "CATALOG_FILE_INVALID");
    }
    return { items, deletedLookups };
  }

  public async release(name: string): Promise<void> {
    await this.options.store.delete(name);
  }

  private async downloadRemaining(
    file: CatalogDownloadFile,
    partName: string,
    signal: AbortSignal | undefined,
    onBytes: ((downloadedBytes: number, totalBytes: number) => void) | undefined,
  ): Promise<void> {
    const store = this.options.store;
    let offset = await store.size(partName) ?? 0;
    if (offset > file.bytes) {
      await store.truncate(partName);
      offset = 0;
    }
    if (offset === 0) await store.truncate(partName);
    onBytes?.(offset, file.bytes);
    while (offset < file.bytes) {
      throwIfAborted(signal);
      const end = Math.min(offset + this.segmentBytes, file.bytes) - 1;
      const response = await this.options.transport.request<ArrayBuffer>({
        method: "GET",
        url: "/" + file.path,
        headers: {
          Range: `bytes=${offset}-${end}`,
          // 中文注释：ETag 是内容哈希；残片属于别的内容时服务端回整份 200，下面据此丢弃残片。
          "If-Range": `"${file.sha256}"`,
        },
        responseType: "arraybuffer",
        timeoutMs: 0,
        ...(signal ? { signal } : {}),
      });
      const bytes = new Uint8Array(response.data);
      if (response.status === 200) {
        if (bytes.length !== file.bytes) {
          throw fileError("Catalog file length does not match the plan.", "CATALOG_FILE_RANGE_INVALID");
        }
        await store.truncate(partName);
        await store.append(partName, bytes);
        offset = bytes.length;
      } else if (response.status === 206 && bytes.length === end - offset + 1) {
        await store.append(partName, bytes);
        offset += bytes.length;
      } else {
        throw fileError("Catalog file range response is invalid.", "CATALOG_FILE_RANGE_INVALID");
      }
      onBytes?.(offset, file.bytes);
    }
  }

  private async matches(name: string, sha256: string): Promise<boolean> {
    const digest = await this.options.digest(await this.options.store.readAll(name));
    return digest.toLowerCase() === sha256;
  }

  private async removeOtherFiles(currentSha256: string): Promise<void> {
    // 中文注释：缓存目录只留当前文件的残片/成品，旧版本残片不再有用，及时清掉。
    for (const name of await this.options.store.list()) {
      if (!name.startsWith(currentSha256)) await this.options.store.delete(name);
    }
  }

  private async *readLines(name: string, signal: AbortSignal | undefined): AsyncIterable<string> {
    const pending: string[] = [];
    let carry = "";
    let failure: unknown = null;
    const decoder = new DecodeUTF8((text, final) => {
      const parts = (carry + text).split("\n");
      carry = parts.pop() ?? "";
      for (const part of parts) {
        if (part.length > 0) pending.push(part);
      }
      if (final && carry.length > 0) {
        pending.push(carry);
        carry = "";
      }
    });
    const gunzip = new Gunzip((chunk, final) => decoder.push(chunk, final));
    const push = (chunk: Uint8Array, final: boolean): void => {
      try {
        gunzip.push(chunk, final);
      } catch (error) {
        failure = error;
      }
      if (failure !== null) {
        throw fileError("Catalog file is not valid gzip.", "CATALOG_FILE_INVALID");
      }
    };
    for await (const chunk of this.options.store.readChunks(name, this.readChunkBytes)) {
      throwIfAborted(signal);
      push(chunk, false);
      // 中文注释：每块解压后立即交出整行，内存只占一块压缩数据展开后的文本。
      yield* pending.splice(0, pending.length);
    }
    push(new Uint8Array(0), true);
    yield* pending.splice(0, pending.length);
  }
}

type FullHeader = Readonly<{
  format: unknown;
  storeCode: unknown;
  catalogVersion: unknown;
  totalCount: unknown;
}>;

type DeltaHeader = Readonly<{
  format: unknown;
  storeCode: unknown;
  baseCatalogVersion: unknown;
  targetCatalogVersion: unknown;
  operationCount: unknown;
}>;

function parseFullHeader(line: string): FullHeader {
  return parseJsonLine(line) as FullHeader;
}

function parseDeltaHeader(line: string): DeltaHeader {
  return parseJsonLine(line) as DeltaHeader;
}

function parseJsonLine(line: string): unknown {
  let value: unknown;
  try {
    value = JSON.parse(line);
  } catch {
    throw fileError("Catalog file contains invalid JSON.", "CATALOG_FILE_INVALID");
  }
  if (value === null || typeof value !== "object") {
    throw fileError("Catalog file line must be an object.", "CATALOG_FILE_INVALID");
  }
  return value;
}

function normalizeFileLine<T>(value: unknown, normalize: (value: unknown) => T): T {
  try {
    return normalize(typeof value === "string" ? parseJsonLine(value) : value);
  } catch (error) {
    if (error instanceof HbposApiError && error.code === "CATALOG_FILE_INVALID") throw error;
    // 字段校验失败统一归为文件无效，调用方据此回退分页协议。
    throw fileError("Catalog file contains an invalid row.", "CATALOG_FILE_INVALID");
  }
}

function normalizeFilePlan(
  source: GeneratedFileSyncPlan,
  requested: Readonly<{ storeCode: string; baseCatalogVersion: string | null }>,
): CatalogFileSyncPlan {
  const mode = source.mode;
  if (mode !== "noChange" && mode !== "delta" && mode !== "full") throw invalidPlan("mode");
  if (source.storeCode !== requested.storeCode) throw invalidPlan("storeCode");
  const baseCatalogVersion = source.baseCatalogVersion ?? null;
  if (baseCatalogVersion !== requested.baseCatalogVersion) throw invalidPlan("baseCatalogVersion");
  if (typeof source.targetCatalogVersion !== "string" || source.targetCatalogVersion.length === 0) {
    throw invalidPlan("targetCatalogVersion");
  }
  const targetTotal = source.targetTotal;
  if (typeof targetTotal !== "number" || !Number.isSafeInteger(targetTotal) || targetTotal < 0) {
    throw invalidPlan("targetTotal");
  }
  const deltaOperationCount = source.deltaOperationCount ?? null;
  if (deltaOperationCount !== null && (!Number.isSafeInteger(deltaOperationCount) || deltaOperationCount < 0)) {
    throw invalidPlan("deltaOperationCount");
  }
  const file = source.file ? normalizeFile(source.file) : null;
  if ((mode === "noChange") !== (file === null)) throw invalidPlan("file");
  if (file !== null && file.kind !== mode) throw invalidPlan("file.kind");
  if (mode === "delta" && deltaOperationCount === null) throw invalidPlan("deltaOperationCount");
  return {
    storeCode: source.storeCode,
    mode,
    baseCatalogVersion,
    targetCatalogVersion: source.targetCatalogVersion,
    targetTotal,
    deltaOperationCount,
    file,
  };
}

function normalizeFile(source: components["schemas"]["CatalogDownloadFileDto"]): CatalogDownloadFile {
  const kind = source.kind;
  if (kind !== "full" && kind !== "delta") throw invalidPlan("file.kind");
  const expectedFormat = kind === "full" ? FULL_FORMAT : DELTA_FORMAT;
  if (source.format !== expectedFormat) throw invalidPlan("file.format");
  // 只接受同一 API 下的文件路由；传输层另有同源校验。
  if (typeof source.path !== "string" || !source.path.startsWith(FILE_PATH_PREFIX)) throw invalidPlan("file.path");
  const bytes = source.bytes;
  if (typeof bytes !== "number" || !Number.isSafeInteger(bytes) || bytes <= 0) throw invalidPlan("file.bytes");
  const sha256 = typeof source.sha256 === "string" ? source.sha256.toLowerCase() : "";
  // 缓存文件名直接取哈希，格式不对一律拒绝，避免路径注入。
  if (!/^[0-9a-f]{64}$/.test(sha256)) throw invalidPlan("file.sha256");
  return { kind, format: source.format, path: source.path, bytes, sha256 };
}

function isRangeRejected(error: unknown): boolean {
  const candidate = error as Readonly<{ status?: unknown; code?: unknown }> | null;
  return candidate?.status === 416 || candidate?.code === "CATALOG_FILE_RANGE_INVALID";
}

function isTransientDownloadError(error: unknown): boolean {
  if (!(error instanceof HbposApiError)) return false;
  if (error.code === "REQUEST_ABORTED") return false;
  return error.kind === "transport"
    || error.status === 502
    || error.status === 503
    || error.status === 504;
}

function throwIfAborted(signal: AbortSignal | undefined): void {
  if (signal?.aborted) {
    throw new HbposApiError("Catalog file download was cancelled.", {
      kind: "transport",
      code: "REQUEST_ABORTED",
    });
  }
}

function delayMilliseconds(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, milliseconds);
    signal?.addEventListener?.("abort", () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

function invalidPlan(field: string): HbposApiError {
  return fileError(`Catalog file sync plan field is invalid: ${field}.`, "CATALOG_FILE_PLAN_INVALID");
}

function fileError(message: string, code: string): HbposApiError {
  return new HbposApiError(message, { kind: "envelope", code });
}
