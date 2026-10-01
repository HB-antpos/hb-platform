import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import { gzipSync } from "node:zlib";

import {
  HbposCatalogFileSync,
  type CatalogDownloadFile,
  type CatalogFileStorePort,
} from "./catalog-file-sync";
import type { CatalogDeletedLookup, CatalogLookupItem } from "./hbpos-catalog-remote";

import {
  HbposApiError,
  type HbposTransport,
  type HbposTransportRequest,
  type HbposTransportResponse,
} from "@/core/api/hbpos-api";

// 文件格式与服务端 CatalogDownloadFileStore 相同：gzip NDJSON，头一行 + 每行一条。
const item = (lookupCode: string, overrides: Partial<CatalogLookupItem> = {}): CatalogLookupItem => ({
  storeCode: "S1",
  productCode: `P-${lookupCode}`,
  referenceCode: null,
  displayName: `商品 ${lookupCode}`,
  lookupCode,
  lookupCodeNormalized: lookupCode.toUpperCase(),
  itemNumber: `I-${lookupCode}`,
  barcode: lookupCode,
  retailPrice: 1.25,
  priceSource: 1,
  priceSourceLabel: "store",
  quantityFactor: 1,
  updatedAt: "2026-10-01T09:00:00+00:00",
  rowVersion: `ROW-${lookupCode}`,
  productImage: null,
  discountRate: null,
  isSpecialProduct: false,
  ...overrides,
});

function gzipLines(lines: readonly unknown[]): Uint8Array<ArrayBuffer> {
  const text = lines.map((line) => JSON.stringify(line)).join("\n") + "\n";
  return new Uint8Array(gzipSync(Buffer.from(text, "utf8")));
}

function fullFile(version: string, items: readonly CatalogLookupItem[], headerTotal = items.length): Uint8Array<ArrayBuffer> {
  return gzipLines([
    { format: "hbpos-catalog-full-v1", storeCode: "S1", catalogVersion: version, generatedAt: "2026-10-01T09:00:00+00:00", totalCount: headerTotal },
    ...items,
  ]);
}

function deltaFile(
  operations: readonly Readonly<{ item?: CatalogLookupItem; deleted?: CatalogDeletedLookup }>[],
  operationCount = operations.length,
): Uint8Array<ArrayBuffer> {
  return gzipLines([
    { format: "hbpos-catalog-delta-v1", storeCode: "S1", baseCatalogVersion: "v1", targetCatalogVersion: "v2", generatedAt: "2026-10-01T09:00:00+00:00", targetTotal: 10, operationCount },
    ...operations.map((operation) => ({ item: operation.item ?? null, deleted: operation.deleted ?? null })),
  ]);
}

function sha256(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}

function descriptor(bytes: Uint8Array, kind: "full" | "delta" = "full"): CatalogDownloadFile {
  return {
    kind,
    format: kind === "full" ? "hbpos-catalog-full-v1" : "hbpos-catalog-delta-v1",
    path: `api/v1/catalog/files/${kind}?storeCode=S1`,
    bytes: bytes.length,
    sha256: sha256(bytes),
  };
}

class MemoryFileStore implements CatalogFileStorePort {
  public readonly files = new Map<string, Uint8Array>();

  public async size(name: string): Promise<number | null> {
    return this.files.get(name)?.length ?? null;
  }

  public async append(name: string, bytes: Uint8Array): Promise<void> {
    const current = this.files.get(name) ?? new Uint8Array(0);
    const next = new Uint8Array(current.length + bytes.length);
    next.set(current);
    next.set(bytes, current.length);
    this.files.set(name, next);
  }

  public async truncate(name: string): Promise<void> {
    this.files.set(name, new Uint8Array(0));
  }

  public async *readChunks(name: string, chunkSize: number): AsyncIterable<Uint8Array> {
    const bytes = this.files.get(name) ?? new Uint8Array(0);
    for (let offset = 0; offset < bytes.length; offset += chunkSize) {
      yield bytes.slice(offset, offset + chunkSize);
    }
  }

  public async readAll(name: string): Promise<Uint8Array<ArrayBuffer>> {
    return new Uint8Array(this.files.get(name) ?? new Uint8Array(0));
  }

  public async rename(from: string, to: string): Promise<void> {
    const bytes = this.files.get(from);
    assert.ok(bytes);
    this.files.delete(from);
    this.files.set(to, bytes);
  }

  public async delete(name: string): Promise<void> {
    this.files.delete(name);
  }

  public async list(): Promise<readonly string[]> {
    return [...this.files.keys()];
  }
}

/** 按 Range 语义从内存提供文件；可注入断网、内容损坏、忽略 Range 与业务错误。 */
class RangeTransport implements HbposTransport {
  public readonly requests: HbposTransportRequest[] = [];
  public failOnRequests = new Set<number>();
  public corruptNextFull = 0;
  public ignoreRangeOnce = false;
  public httpError: HbposApiError | null = null;
  public plan: unknown = null;

  public constructor(private readonly body: Uint8Array) {}

  public async request<T>(request: HbposTransportRequest): Promise<HbposTransportResponse<T>> {
    this.requests.push(request);
    if (request.url === "/api/v1/catalog/files/sync-plan") {
      return { status: 200, data: { success: true, data: this.plan } as T };
    }
    if (this.httpError) throw this.httpError;
    if (this.failOnRequests.delete(this.requests.length)) {
      throw new HbposApiError("offline", { kind: "transport", code: "NO_HTTP_RESPONSE" });
    }
    let body = this.body;
    if (this.corruptNextFull > 0 && request.headers?.Range?.startsWith("bytes=0-")) {
      this.corruptNextFull -= 1;
      body = new Uint8Array(body);
      body[body.length - 1] = (body[body.length - 1] ?? 0) ^ 0xff;
    }
    if (this.ignoreRangeOnce) {
      this.ignoreRangeOnce = false;
      return { status: 200, data: toArrayBuffer(body) as T };
    }
    const match = /^bytes=(\d+)-(\d+)$/.exec(request.headers?.Range ?? "");
    assert.ok(match, "file requests must carry a closed byte range");
    const start = Number(match[1]);
    const end = Number(match[2]);
    return { status: 206, data: toArrayBuffer(body.slice(start, end + 1)) as T };
  }
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
}

function createSync(transport: HbposTransport, store: MemoryFileStore, delays: number[] = []): HbposCatalogFileSync {
  return new HbposCatalogFileSync({
    transport,
    store,
    digest: async (bytes) => sha256(bytes),
    segmentBytes: 997,
    readChunkBytes: 1_024,
    delay: async (milliseconds) => { delays.push(milliseconds); },
  });
}

async function collect(iterable: AsyncIterable<readonly CatalogLookupItem[]>): Promise<CatalogLookupItem[][]> {
  const batches: CatalogLookupItem[][] = [];
  for await (const batch of iterable) batches.push([...batch]);
  return batches;
}

const manyItems = Array.from({ length: 1_200 }, (_, index) => item(`93${String(index).padStart(10, "0")}`));

test("按 Range 分段下载并整份校验，读回的行与服务端写入逐条一致", async () => {
  const bytes = fullFile("v2", manyItems);
  const transport = new RangeTransport(bytes);
  const store = new MemoryFileStore();
  const reported: number[] = [];

  const name = await createSync(transport, store).download({
    file: descriptor(bytes),
    onBytes: (downloaded) => reported.push(downloaded),
  });

  assert.equal(name, `${sha256(bytes)}.ndjson.gz`);
  assert.deepEqual([...store.files.keys()], [name]);
  const ranges = transport.requests.map((request) => request.headers?.Range);
  assert.equal(ranges[0], "bytes=0-996");
  assert.equal(ranges.length, Math.ceil(bytes.length / 997));
  assert.equal(transport.requests[0]?.headers?.["If-Range"], `"${sha256(bytes)}"`);
  assert.equal(transport.requests[0]?.responseType, "arraybuffer");
  assert.equal(reported.at(-1), bytes.length);

  const batches = await collect(createSync(transport, store).readFull(name, { storeCode: "S1", catalogVersion: "v2", totalCount: 1_200 }, 500));
  assert.deepEqual(batches.map((batch) => batch.length), [500, 500, 200]);
  assert.deepEqual(batches.flat(), manyItems);
});

test("断网后保留残片，等待后从断点继续而不是从头下载", async () => {
  const bytes = fullFile("v2", manyItems);
  const transport = new RangeTransport(bytes);
  transport.failOnRequests.add(3);
  const delays: number[] = [];

  const name = await createSync(transport, new MemoryFileStore(), delays).download({ file: descriptor(bytes) });

  assert.ok(name.endsWith(".ndjson.gz"));
  assert.deepEqual(delays, [3_000]);
  // 第 3 次请求失败后，第 4 次仍请求同一段（第 3 段），前两段不再重下。
  assert.equal(transport.requests[2]?.headers?.Range, transport.requests[3]?.headers?.Range);
  assert.equal(transport.requests[3]?.headers?.Range, "bytes=1994-2990");
});

test("上次运行留下的残片按其长度续传", async () => {
  const bytes = fullFile("v2", manyItems);
  const store = new MemoryFileStore();
  store.files.set(`${sha256(bytes)}.part`, bytes.slice(0, 1_500));
  const transport = new RangeTransport(bytes);

  await createSync(transport, store).download({ file: descriptor(bytes) });

  assert.equal(transport.requests[0]?.headers?.Range, "bytes=1500-2496");
});

test("服务端忽略 Range 回整份 200 时丢弃残片整份重写", async () => {
  const bytes = fullFile("v2", manyItems);
  const store = new MemoryFileStore();
  store.files.set(`${sha256(bytes)}.part`, new Uint8Array(400).fill(7));
  const transport = new RangeTransport(bytes);
  transport.ignoreRangeOnce = true;

  const name = await createSync(transport, store).download({ file: descriptor(bytes) });

  assert.deepEqual(store.files.get(name), bytes);
  assert.equal(transport.requests.length, 1);
});

test("整份校验失败时从头重下一次；仍失败则放弃且不留文件", async () => {
  const bytes = fullFile("v2", [item("A")]);
  const once = new RangeTransport(bytes);
  once.corruptNextFull = 1;
  const store = new MemoryFileStore();
  const name = await createSync(once, store).download({ file: descriptor(bytes) });
  assert.deepEqual(store.files.get(name), bytes);
  assert.equal(once.requests.length, 2);

  const twice = new RangeTransport(bytes);
  twice.corruptNextFull = 2;
  const emptyStore = new MemoryFileStore();
  await assert.rejects(
    () => createSync(twice, emptyStore).download({ file: descriptor(bytes) }),
    (error: unknown) => error instanceof HbposApiError && error.code === "CATALOG_FILE_CHECKSUM_MISMATCH",
  );
  assert.equal(emptyStore.files.size, 0);
});

test("业务错误不重试；瞬时错误重试用尽后抛出", async () => {
  const bytes = fullFile("v2", [item("A")]);
  const notFound = new RangeTransport(bytes);
  notFound.httpError = new HbposApiError("gone", { kind: "http", status: 404, code: "CATALOG_FILE_NOT_FOUND" });
  const delays: number[] = [];
  await assert.rejects(
    () => createSync(notFound, new MemoryFileStore(), delays).download({ file: descriptor(bytes) }),
    (error: unknown) => error instanceof HbposApiError && error.code === "CATALOG_FILE_NOT_FOUND",
  );
  assert.deepEqual(delays, []);

  const offline = new RangeTransport(bytes);
  for (let request = 1; request <= 10; request += 1) offline.failOnRequests.add(request);
  const offlineDelays: number[] = [];
  await assert.rejects(
    () => createSync(offline, new MemoryFileStore(), offlineDelays).download({ file: descriptor(bytes) }),
    (error: unknown) => error instanceof HbposApiError && error.code === "NO_HTTP_RESPONSE",
  );
  assert.deepEqual(offlineDelays, [3_000, 10_000, 30_000]);
});

test("已校验文件直接复用，并清理其他版本的残片", async () => {
  const bytes = fullFile("v2", [item("A")]);
  const store = new MemoryFileStore();
  store.files.set(`${sha256(bytes)}.ndjson.gz`, bytes);
  store.files.set(`${"b".repeat(64)}.part`, new Uint8Array([1, 2, 3]));
  const transport = new RangeTransport(bytes);

  await createSync(transport, store).download({ file: descriptor(bytes) });

  assert.equal(transport.requests.length, 0);
  assert.deepEqual([...store.files.keys()], [`${sha256(bytes)}.ndjson.gz`]);
});

test("文件头、条数、gzip 或行字段不对时一律判为文件无效", async () => {
  const cases: readonly [string, Uint8Array, Readonly<{ catalogVersion: string; totalCount: number }>][] = [
    ["header-version", fullFile("v-other", [item("A")]), { catalogVersion: "v2", totalCount: 1 }],
    ["truncated", fullFile("v2", [item("A")], 2), { catalogVersion: "v2", totalCount: 2 }],
    ["not-gzip", new TextEncoder().encode("not gzip"), { catalogVersion: "v2", totalCount: 1 }],
    ["bad-row", gzipLines([
      { format: "hbpos-catalog-full-v1", storeCode: "S1", catalogVersion: "v2", generatedAt: "x", totalCount: 1 },
      { ...item("A"), productCode: "" },
    ]), { catalogVersion: "v2", totalCount: 1 }],
  ];
  for (const [label, bytes, expected] of cases) {
    const store = new MemoryFileStore();
    store.files.set("file", bytes);
    await assert.rejects(
      () => collect(createSync(new RangeTransport(bytes), store).readFull("file", { storeCode: "S1", ...expected }, 500)),
      (error: unknown) => error instanceof HbposApiError && error.code === "CATALOG_FILE_INVALID",
      label,
    );
  }
});

test("增量文件读出 upsert 与精确删除，行内二选一且条数须与计划一致", async () => {
  const deleted: CatalogDeletedLookup = { storeCode: "S1", lookupCode: "old", lookupCodeNormalized: "OLD", deletedAt: "2026-10-01T09:00:00+00:00" };
  const bytes = deltaFile([{ item: item("NEW") }, { deleted }]);
  const store = new MemoryFileStore();
  store.files.set("delta", bytes);
  const sync = createSync(new RangeTransport(bytes), store);

  const result = await sync.readDelta("delta", { storeCode: "S1", baseCatalogVersion: "v1", targetCatalogVersion: "v2", operationCount: 2 });

  assert.deepEqual(result.items, [item("NEW")]);
  // 删除时间与分页路径同样规范化为 ISO 毫秒格式。
  assert.deepEqual(result.deletedLookups, [{ ...deleted, deletedAt: "2026-10-01T09:00:00.000Z" }]);
  // 头部条数与计划一致，失败只能来自行内容本身：一行同时含两种操作 / 实际行数少于声明。
  for (const [bad, operationCount] of [
    [gzipLines([
      { format: "hbpos-catalog-delta-v1", storeCode: "S1", baseCatalogVersion: "v1", targetCatalogVersion: "v2", targetTotal: 10, operationCount: 1 },
      { item: item("A"), deleted },
    ]), 1],
    [deltaFile([{ item: item("A") }], 2), 2],
  ] as const) {
    store.files.set("delta", bad);
    await assert.rejects(
      () => sync.readDelta("delta", { storeCode: "S1", baseCatalogVersion: "v1", targetCatalogVersion: "v2", operationCount }),
      (error: unknown) => error instanceof HbposApiError && error.code === "CATALOG_FILE_INVALID",
    );
  }
});

test("同步计划严格校验门店、基准、文件路由、格式与哈希", async () => {
  const bytes = fullFile("v2", [item("A")]);
  const valid = {
    storeCode: "S1",
    generatedAt: "2026-10-01T09:00:00+00:00",
    mode: "full",
    baseCatalogVersion: null,
    targetCatalogVersion: "v2",
    targetTotal: 1,
    file: { ...descriptor(bytes) },
    deltaOperationCount: null,
  };
  const transport = new RangeTransport(bytes);
  transport.plan = valid;
  const sync = createSync(transport, new MemoryFileStore());

  const plan = await sync.getPlan({ storeCode: "S1", baseCatalogVersion: null });
  assert.deepEqual(plan.file, descriptor(bytes));
  assert.equal(transport.requests[0]?.params?.baseCatalogVersion, undefined);

  for (const invalid of [
    { ...valid, storeCode: "S2" },
    { ...valid, baseCatalogVersion: "v0" },
    { ...valid, file: { ...valid.file, sha256: "../../etc/passwd" } },
    { ...valid, file: { ...valid.file, path: "https://evil.example/file" } },
    { ...valid, file: { ...valid.file, format: "other" } },
    { ...valid, mode: "delta" },
    { ...valid, mode: "noChange" },
  ]) {
    transport.plan = invalid;
    await assert.rejects(
      () => sync.getPlan({ storeCode: "S1", baseCatalogVersion: null }),
      (error: unknown) => error instanceof HbposApiError && error.code === "CATALOG_FILE_PLAN_INVALID",
    );
  }
});
