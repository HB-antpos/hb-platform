import assert from "node:assert/strict";
import test from "node:test";

import type { CustomerDisplayAdvertisementItem } from "./advertisement-api";
import {
  CustomerDisplayAdvertisementCache,
  type AdvertisementCacheFileSystemPort,
} from "./advertisement-cache";

test("素材先下载到临时文件，精确校验大小后原子移动并清理过期缓存", async () => {
  const files = new MemoryAdvertisementFiles([
    ["file:///cache/stale.png", 10],
  ]);
  const cache = new CustomerDisplayAdvertisementCache({
    rootUri: "file:///cache/",
    files,
    sha256Hex: async (value) =>
      value.endsWith("image.png") ? "a".repeat(64) : "b".repeat(64),
  });

  const cached = await cache.cache([
    advert("image", 1_024),
    advert("video", 2_048, {
      kind: "video",
      remoteUrl: "https://cdn.example.com/video.mp4",
      contentType: "video/mp4",
      objectKey: "ads/video.mp4",
      originalFileName: "video.mp4",
    }),
  ]);

  assert.deepEqual(
    cached.map((item) => item.localUri),
    [
      `file:///cache/${"a".repeat(64)}.png`,
      `file:///cache/${"b".repeat(64)}.mp4`,
    ],
  );
  assert.deepEqual(files.trace, [
    "mkdir:file:///cache/",
    `delete:file:///cache/${"a".repeat(64)}.png.download`,
    `download:https://cdn.example.com/image.png->file:///cache/${"a".repeat(64)}.png.download`,
    `move:file:///cache/${"a".repeat(64)}.png.download->file:///cache/${"a".repeat(64)}.png`,
    `delete:file:///cache/${"b".repeat(64)}.mp4.download`,
    `download:https://cdn.example.com/video.mp4->file:///cache/${"b".repeat(64)}.mp4.download`,
    `move:file:///cache/${"b".repeat(64)}.mp4.download->file:///cache/${"b".repeat(64)}.mp4`,
    "delete:file:///cache/stale.png",
  ]);
});

test("已存在同尺寸文件直接复用；尺寸不符或下载失败只跳过坏素材", async () => {
  const hash = "c".repeat(64);
  const finalUri = `file:///cache/${hash}.png`;
  const files = new MemoryAdvertisementFiles([[finalUri, 1_024]]);
  const cache = new CustomerDisplayAdvertisementCache({
    rootUri: "file:///cache/",
    files,
    sha256Hex: async () => hash,
  });
  assert.equal((await cache.cache([advert("image", 1_024)])).length, 1);
  assert.equal(
    files.trace.some((entry) => entry.startsWith("download:")),
    false,
  );

  files.set(finalUri, 5);
  files.failDownloads = true;
  assert.deepEqual(await cache.cache([advert("image", 1_024)]), []);
  assert.equal(files.has(`${finalUri}.download`), false);
});

test("缓存结果透传 orientation，缺省项保持缺省", async () => {
  const hash = "d".repeat(64);
  const files = new MemoryAdvertisementFiles([
    [`file:///cache/${hash}.png`, 1_024],
  ]);
  const cache = new CustomerDisplayAdvertisementCache({
    rootUri: "file:///cache/",
    files,
    sha256Hex: async () => hash,
  });
  const [withOrientation] = await cache.cache([
    advert("image", 1_024, { orientation: "portrait" }),
  ]);
  assert.equal(withOrientation?.orientation, "portrait");
  const [legacy] = await cache.cache([advert("image", 1_024)]);
  assert.equal(legacy?.orientation, undefined);
});

class MemoryAdvertisementFiles
  implements AdvertisementCacheFileSystemPort
{
  public readonly trace: string[] = [];
  public failDownloads = false;
  private readonly files: Map<string, number>;

  public constructor(initial: readonly (readonly [string, number])[]) {
    this.files = new Map(initial);
  }

  public async ensureDirectory(uri: string): Promise<void> {
    this.trace.push(`mkdir:${uri}`);
  }

  public async getSize(uri: string): Promise<number | null> {
    return this.files.get(uri) ?? null;
  }

  public async download(
    remoteUrl: string,
    destinationUri: string,
  ): Promise<void> {
    this.trace.push(`download:${remoteUrl}->${destinationUri}`);
    if (this.failDownloads) throw new Error("download failed");
    this.files.set(
      destinationUri,
      remoteUrl.endsWith("video.mp4") ? 2_048 : 1_024,
    );
  }

  public async move(sourceUri: string, destinationUri: string): Promise<void> {
    this.trace.push(`move:${sourceUri}->${destinationUri}`);
    const size = this.files.get(sourceUri);
    if (size === undefined) throw new Error("source missing");
    this.files.delete(sourceUri);
    this.files.set(destinationUri, size);
  }

  public async deleteIfExists(uri: string): Promise<void> {
    this.trace.push(`delete:${uri}`);
    this.files.delete(uri);
  }

  public async listFiles(): Promise<readonly string[]> {
    return [...this.files.keys()];
  }

  public set(uri: string, size: number): void {
    this.files.set(uri, size);
  }

  public has(uri: string): boolean {
    return this.files.has(uri);
  }
}

function advert(
  id: string,
  fileSize: number,
  overrides: Partial<CustomerDisplayAdvertisementItem> = {},
): CustomerDisplayAdvertisementItem {
  return {
    id,
    kind: "image",
    remoteUrl: "https://cdn.example.com/image.png",
    objectKey: "ads/image.png",
    originalFileName: "image.png",
    contentType: "image/png",
    fileSize,
    effectiveStartIso: "2026-07-27T00:00:00.000Z",
    effectiveEndIso: "2026-07-29T00:00:00.000Z",
    sortOrder: 0,
    ...overrides,
  };
}
