import assert from "node:assert/strict";
import test from "node:test";

import {
  CustomerDisplayProductImageCache,
  detectImageExtension,
  type ProductImageCacheFile,
  type ProductImageCacheFileSystemPort,
} from "./product-image-cache";

const ROOT = "file:///cache/customer-display-product-images/";
const JPEG = Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0, 0x10, 0x4a, 0x46]);
const PNG = Uint8Array.from([
  0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0,
]);
const WEBP = Uint8Array.from([
  0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50,
]);
const GIF = Uint8Array.from([0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0]);
const HTML = new TextEncoder().encode("<html><body>nope</body></html>");

// sha256Hex 测试替身：把 URL 末尾的数字扩成 64 位十六进制。
const hashOf = (url: string) =>
  (url.match(/(\d+)\D*$/u)?.[1] ?? "0").padStart(64, "0");
const sha256Hex = async (value: string) => hashOf(value);

function bytes(length: number, head: Uint8Array = JPEG): Uint8Array {
  const body = new Uint8Array(length);
  body.set(head.subarray(0, Math.min(head.length, length)));
  return body;
}

test("识别 JPEG/PNG/WebP/GIF 魔数，其它内容（HTML、SVG、过短）一律不是图片", () => {
  assert.equal(detectImageExtension(JPEG), ".jpg");
  assert.equal(detectImageExtension(PNG), ".png");
  assert.equal(detectImageExtension(WEBP), ".webp");
  assert.equal(detectImageExtension(GIF), ".gif");
  assert.equal(detectImageExtension(HTML), null);
  assert.equal(
    detectImageExtension(new TextEncoder().encode("<svg xmlns='x'></svg>")),
    null,
  );
  assert.equal(detectImageExtension(Uint8Array.from([0xff, 0xd8])), null);
  // RIFF 但不是 WEBP（如 WAV）
  assert.equal(
    detectImageExtension(
      Uint8Array.from([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45]),
    ),
    null,
  );
});

test("下载到临时文件、校验大小与真实格式后移动为正式文件，并按文件头决定扩展名", async () => {
  const files = new MemoryFiles();
  files.network.set("https://cdn.example.com/p/1", bytes(1_000, PNG));
  const cache = new CustomerDisplayProductImageCache({
    rootUri: ROOT,
    files,
    sha256Hex,
  });

  const uri = await cache.fetch("https://cdn.example.com/p/1");

  assert.equal(uri, `${ROOT}${"1".padStart(64, "0")}.png`);
  assert.deepEqual(files.trace, [
    `mkdir:${ROOT}`,
    `mkdir:${ROOT}`,
    `delete:${ROOT}${"1".padStart(64, "0")}.download`,
    `download:https://cdn.example.com/p/1->${ROOT}${"1".padStart(64, "0")}.download`,
    `move:${ROOT}${"1".padStart(64, "0")}.download->${uri}`,
  ]);
  assert.equal(files.has(`${ROOT}${"1".padStart(64, "0")}.download`), false);
  assert.equal(cache.touch(uri), true);
});

test("已缓存的 URL 不再下载；同一 URL 并发请求共享一次下载", async () => {
  const files = new MemoryFiles();
  files.network.set("https://cdn.example.com/p/2", bytes(500));
  const cache = new CustomerDisplayProductImageCache({
    rootUri: ROOT,
    files,
    sha256Hex,
  });

  const [first, second] = await Promise.all([
    cache.fetch("https://cdn.example.com/p/2"),
    cache.fetch("https://cdn.example.com/p/2"),
  ]);
  assert.equal(first, second);
  assert.equal(files.downloadCount, 1);

  assert.equal(await cache.fetch("https://cdn.example.com/p/2"), first);
  assert.equal(files.downloadCount, 1);
});

test("超限、空文件、非图片内容和下载失败都不会留下文件，也不会进入缓存", async () => {
  const files = new MemoryFiles();
  const cache = new CustomerDisplayProductImageCache({
    rootUri: ROOT,
    files,
    sha256Hex,
    maxBytes: 2_048,
  });
  files.network.set("https://cdn.example.com/p/3", bytes(4_096));
  files.network.set("https://cdn.example.com/p/4", new Uint8Array(0));
  files.network.set("https://cdn.example.com/p/5", HTML);

  for (const id of ["3", "4", "5", "6"]) {
    await assert.rejects(() => cache.fetch(`https://cdn.example.com/p/${id}`));
  }
  assert.deepEqual(files.uris(), []);

  // 失败不会毒化后续重试：补上合法内容后可以成功。
  files.network.set("https://cdn.example.com/p/3", bytes(1_024));
  assert.equal(
    await cache.fetch("https://cdn.example.com/p/3"),
    `${ROOT}${"3".padStart(64, "0")}.jpg`,
  );
});

test("拒绝非 http(s) 与带凭据的地址，且不触碰文件系统", async () => {
  const files = new MemoryFiles();
  const cache = new CustomerDisplayProductImageCache({
    rootUri: ROOT,
    files,
    sha256Hex,
  });
  for (const url of [
    "file:///etc/passwd",
    "ftp://cdn.example.com/p/1",
    "https://user:pw@cdn.example.com/p/1",
    "not a url",
  ]) {
    await assert.rejects(() => cache.fetch(url), url);
  }
  assert.deepEqual(files.trace, []);
});

test("超过条目上限时淘汰最久未使用的条目，touch 会刷新最近使用", async () => {
  const files = new MemoryFiles();
  let clock = 1_000;
  const cache = new CustomerDisplayProductImageCache({
    rootUri: ROOT,
    files,
    sha256Hex,
    maxEntries: 2,
    now: () => clock,
  });
  for (const id of [1, 2, 3]) {
    files.network.set(`https://cdn.example.com/p/${id}`, bytes(100));
  }

  clock = 1_001;
  const one = await cache.fetch("https://cdn.example.com/p/1");
  clock = 1_002;
  const two = await cache.fetch("https://cdn.example.com/p/2");
  clock = 1_003;
  assert.equal(cache.touch(one), true); // 1 比 2 更新
  clock = 1_004;
  const three = await cache.fetch("https://cdn.example.com/p/3");

  assert.equal(cache.touch(two), false); // 2 被淘汰
  assert.equal(files.has(two), false);
  assert.equal(cache.touch(one), true);
  assert.equal(cache.touch(three), true);
  assert.equal(files.uris().length, 2);
});

test("启动扫描：清理遗留临时文件、按修改时间淘汰超限条目、保留无关文件", async () => {
  const files = new MemoryFiles();
  const hashA = "a".repeat(64);
  const hashB = "b".repeat(64);
  const hashC = "c".repeat(64);
  files.put(`${ROOT}${hashA}.jpg`, bytes(10), 100);
  files.put(`${ROOT}${hashB}.png`, bytes(10, PNG), 300);
  files.put(`${ROOT}${hashC}.webp`, bytes(10, WEBP), 200);
  files.put(`${ROOT}${hashA}.download`, bytes(10), 400);
  files.put(`${ROOT}README.txt`, bytes(10), 1);
  const cache = new CustomerDisplayProductImageCache({
    rootUri: ROOT,
    files,
    sha256Hex,
    maxEntries: 2,
  });

  await cache.initialize();

  assert.equal(files.has(`${ROOT}${hashA}.download`), false);
  assert.equal(files.has(`${ROOT}${hashA}.jpg`), false); // 最久未用被淘汰
  assert.equal(cache.touch(`${ROOT}${hashB}.png`), true);
  assert.equal(cache.touch(`${ROOT}${hashC}.webp`), true);
  assert.equal(files.has(`${ROOT}README.txt`), true);
});

test("启动扫描失败时降级为空缓存，下载仍可进行", async () => {
  const files = new MemoryFiles();
  files.failList = true;
  files.network.set("https://cdn.example.com/p/7", bytes(100));
  const cache = new CustomerDisplayProductImageCache({
    rootUri: ROOT,
    files,
    sha256Hex,
  });
  assert.equal(
    await cache.fetch("https://cdn.example.com/p/7"),
    `${ROOT}${"7".padStart(64, "0")}.jpg`,
  );
});

test("根目录必须是有效 file URI", () => {
  assert.throws(
    () =>
      new CustomerDisplayProductImageCache({
        rootUri: "https://example.com/",
        files: new MemoryFiles(),
        sha256Hex,
      }),
    /product image cache root/,
  );
});

class MemoryFiles implements ProductImageCacheFileSystemPort {
  public readonly trace: string[] = [];
  public readonly network = new Map<string, Uint8Array>();
  public downloadCount = 0;
  public failList = false;
  private readonly store = new Map<
    string,
    { data: Uint8Array; modifiedAtMs: number }
  >();

  public put(uri: string, data: Uint8Array, modifiedAtMs: number): void {
    this.store.set(uri, { data, modifiedAtMs });
  }

  public has(uri: string): boolean {
    return this.store.has(uri);
  }

  public uris(): string[] {
    return [...this.store.keys()];
  }

  public async ensureDirectory(uri: string): Promise<void> {
    this.trace.push(`mkdir:${uri}`);
  }

  public async getSize(uri: string): Promise<number | null> {
    return this.store.get(uri)?.data.length ?? null;
  }

  public async download(remoteUrl: string, destinationUri: string): Promise<void> {
    this.trace.push(`download:${remoteUrl}->${destinationUri}`);
    this.downloadCount += 1;
    // 让出一次事件循环，模拟真实网络耗时，便于验证并发去重。
    await Promise.resolve();
    const data = this.network.get(remoteUrl);
    if (!data) throw new Error("404");
    this.store.set(destinationUri, { data, modifiedAtMs: 0 });
  }

  public async readHeader(uri: string, length: number): Promise<Uint8Array | null> {
    const file = this.store.get(uri);
    return file ? file.data.slice(0, length) : null;
  }

  public async move(sourceUri: string, destinationUri: string): Promise<void> {
    this.trace.push(`move:${sourceUri}->${destinationUri}`);
    const file = this.store.get(sourceUri);
    if (!file) throw new Error("missing source");
    this.store.delete(sourceUri);
    this.store.set(destinationUri, file);
  }

  public async deleteIfExists(uri: string): Promise<void> {
    this.trace.push(`delete:${uri}`);
    this.store.delete(uri);
  }

  public async listFiles(): Promise<readonly ProductImageCacheFile[]> {
    if (this.failList) throw new Error("list failed");
    return [...this.store.entries()].map(([uri, file]) => ({
      uri,
      modifiedAtMs: file.modifiedAtMs,
    }));
  }
}
