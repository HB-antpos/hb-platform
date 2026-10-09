import { normalizeProductImageCacheRootUri } from "@/core/peripherals/customer-display/local-advertisement-uri";

/** 单张商品缩略图的体积上限；下载完成后超限的文件直接丢弃。 */
export const PRODUCT_IMAGE_MAX_BYTES = 2 * 1024 * 1024;
/** 缓存条目上限，超出后按最近使用时间淘汰最久未用的条目。 */
export const PRODUCT_IMAGE_MAX_ENTRIES = 300;

const SNIFF_LENGTH = 16;
const CACHED_FILE_PATTERN = /^([a-f0-9]{64})(\.jpg|\.png|\.webp|\.gif)$/u;

export type ProductImageCacheFile = Readonly<{
  uri: string;
  /** 文件修改时间（毫秒）；读不到时为 null。仅用于启动后恢复 LRU 顺序。 */
  modifiedAtMs: number | null;
}>;

export interface ProductImageCacheFileSystemPort {
  ensureDirectory(uri: string): Promise<void>;
  getSize(uri: string): Promise<number | null>;
  download(remoteUrl: string, destinationUri: string): Promise<void>;
  /** 读取文件头部若干字节用来识别真实图片格式；文件不存在返回 null。 */
  readHeader(uri: string, length: number): Promise<Uint8Array | null>;
  move(sourceUri: string, destinationUri: string): Promise<void>;
  deleteIfExists(uri: string): Promise<void>;
  listFiles(rootUri: string): Promise<readonly ProductImageCacheFile[]>;
}

export type CustomerDisplayProductImageCacheOptions = Readonly<{
  rootUri: string;
  files: ProductImageCacheFileSystemPort;
  sha256Hex(material: string): Promise<string>;
  maxBytes?: number;
  maxEntries?: number;
  now?: () => number;
}>;

type CacheEntry = {
  hash: string;
  uri: string;
  lastUsedMs: number;
};

/**
 * 商品缩略图本地缓存：按需下载 → `.download` 临时文件 → 大小与真实格式（魔数）校验 → 移动为正式文件。
 *
 * - 文件名为远端 URL 的 SHA-256 + 由文件头识别出的扩展名，不信任 URL/响应头里声明的类型；
 * - 同一 URL 的并发请求共享同一次下载；
 * - 条目超过上限时按最近使用时间淘汰，淘汰与启动清理均为 best-effort；
 * - 目录里不符合命名规则的文件不会被计入，也不会被删除（仅清理自己遗留的 `.download` 临时文件）。
 */
export class CustomerDisplayProductImageCache {
  public readonly rootUri: string;
  private readonly maxBytes: number;
  private readonly maxEntries: number;
  private readonly now: () => number;
  private readonly entriesByUri = new Map<string, CacheEntry>();
  private readonly uriByHash = new Map<string, string>();
  private readonly inflight = new Map<string, Promise<string>>();
  private ready: Promise<void> | null = null;

  public constructor(
    private readonly options: CustomerDisplayProductImageCacheOptions,
  ) {
    const normalized = normalizeProductImageCacheRootUri(options.rootUri);
    this.rootUri = normalized.endsWith("/") ? normalized : `${normalized}/`;
    this.maxBytes = options.maxBytes ?? PRODUCT_IMAGE_MAX_BYTES;
    this.maxEntries = options.maxEntries ?? PRODUCT_IMAGE_MAX_ENTRIES;
    this.now = options.now ?? Date.now;
  }

  /**
   * 确保目录存在并扫描既有缓存（含清理遗留临时文件、超限淘汰）。可重复调用，只执行一次；
   * 目录或列表读取失败时降级为空缓存，后续下载仍会各自报错。
   */
  public initialize(): Promise<void> {
    this.ready ??= this.scan();
    return this.ready;
  }

  /** 缓存里当前是否仍有该本地文件（被淘汰后为 false）。命中时刷新最近使用时间。 */
  public touch(localUri: string): boolean {
    const entry = this.entriesByUri.get(localUri);
    if (!entry) return false;
    entry.lastUsedMs = this.now();
    return true;
  }

  /**
   * 取得远端图片的本地 file URI：已缓存则直接返回，否则下载并校验。
   * 失败（地址不可信、体积/格式不合规、下载异常）抛错，由调用方决定退避。
   */
  public async fetch(remoteUrl: string): Promise<string> {
    assertDownloadableUrl(remoteUrl);
    await this.initialize();
    const hash = (await this.options.sha256Hex(remoteUrl)).toLowerCase();
    if (!/^[a-f0-9]{64}$/u.test(hash)) {
      throw new TypeError("Product image cache hash is invalid.");
    }
    const existingUri = this.uriByHash.get(hash);
    if (existingUri !== undefined && this.touch(existingUri)) {
      return existingUri;
    }
    const pending = this.inflight.get(hash);
    if (pending) return pending;
    const task = this.download(remoteUrl, hash).finally(() => {
      this.inflight.delete(hash);
    });
    this.inflight.set(hash, task);
    return task;
  }

  private async scan(): Promise<void> {
    try {
      await this.options.files.ensureDirectory(this.rootUri);
    } catch {
      return;
    }
    let listed: readonly ProductImageCacheFile[];
    try {
      listed = await this.options.files.listFiles(this.rootUri);
    } catch {
      return;
    }
    const startedAt = this.now();
    for (const file of listed) {
      const name = fileNameOf(file.uri);
      if (name.endsWith(".download")) {
        // 上次中断留下的临时文件，一律丢弃。
        await this.deleteQuietly(file.uri);
        continue;
      }
      const match = CACHED_FILE_PATTERN.exec(name);
      if (!match) continue;
      this.register({
        hash: match[1]!,
        uri: file.uri,
        lastUsedMs: file.modifiedAtMs ?? startedAt,
      });
    }
    await this.evictOverflow();
  }

  private async download(remoteUrl: string, hash: string): Promise<string> {
    const { files } = this.options;
    const tempUri = new URL(`${hash}.download`, this.rootUri).toString();
    try {
      await files.ensureDirectory(this.rootUri);
      await files.deleteIfExists(tempUri);
      await files.download(remoteUrl, tempUri);
      const size = await files.getSize(tempUri);
      if (size === null || size <= 0 || size > this.maxBytes) {
        throw new RangeError("Product image size is outside the allowed range.");
      }
      const header = await files.readHeader(tempUri, SNIFF_LENGTH);
      const extension = header ? detectImageExtension(header) : null;
      if (extension === null) {
        throw new TypeError("Product image is not a supported image format.");
      }
      const finalUri = new URL(`${hash}${extension}`, this.rootUri).toString();
      await files.move(tempUri, finalUri);
      if ((await files.getSize(finalUri)) !== size) {
        await this.deleteQuietly(finalUri);
        throw new Error("Product image size changed after move.");
      }
      this.register({ hash, uri: finalUri, lastUsedMs: this.now() });
      await this.evictOverflow();
      return finalUri;
    } catch (error) {
      await this.deleteQuietly(tempUri);
      throw error;
    }
  }

  private register(entry: CacheEntry): void {
    const previousUri = this.uriByHash.get(entry.hash);
    if (previousUri !== undefined && previousUri !== entry.uri) {
      this.entriesByUri.delete(previousUri);
    }
    this.entriesByUri.set(entry.uri, entry);
    this.uriByHash.set(entry.hash, entry.uri);
  }

  private async evictOverflow(): Promise<void> {
    const overflow = this.entriesByUri.size - this.maxEntries;
    if (overflow <= 0) return;
    const victims = [...this.entriesByUri.values()]
      .sort((left, right) => left.lastUsedMs - right.lastUsedMs)
      .slice(0, overflow);
    for (const victim of victims) {
      this.entriesByUri.delete(victim.uri);
      this.uriByHash.delete(victim.hash);
      await this.deleteQuietly(victim.uri);
    }
  }

  private async deleteQuietly(uri: string): Promise<void> {
    try {
      await this.options.files.deleteIfExists(uri);
    } catch {
      // 清理与淘汰是 best-effort，不能影响已验证的缓存条目。
    }
  }
}

/** 通过文件头魔数识别 JPEG / PNG / GIF / WebP；其它格式（含 SVG、HTML）一律返回 null。 */
export function detectImageExtension(header: Uint8Array): string | null {
  if (startsWith(header, [0xff, 0xd8, 0xff])) return ".jpg";
  if (startsWith(header, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) {
    return ".png";
  }
  if (
    startsWith(header, [0x47, 0x49, 0x46, 0x38, 0x37, 0x61]) ||
    startsWith(header, [0x47, 0x49, 0x46, 0x38, 0x39, 0x61])
  ) {
    return ".gif";
  }
  if (
    header.length >= 12 &&
    startsWith(header, [0x52, 0x49, 0x46, 0x46]) &&
    header[8] === 0x57 &&
    header[9] === 0x45 &&
    header[10] === 0x42 &&
    header[11] === 0x50
  ) {
    return ".webp";
  }
  return null;
}

function startsWith(bytes: Uint8Array, prefix: readonly number[]): boolean {
  return (
    bytes.length >= prefix.length &&
    prefix.every((value, index) => bytes[index] === value)
  );
}

function fileNameOf(uri: string): string {
  const withoutQuery = uri.split(/[?#]/u)[0] ?? "";
  return withoutQuery.split("/").at(-1) ?? "";
}

/** 缓存层的最后一道地址防线：只下载 http(s) 且不带凭据的地址（可信来源由上游解析器保证）。 */
function assertDownloadableUrl(remoteUrl: string): void {
  let parsed: URL;
  try {
    parsed = new URL(remoteUrl);
  } catch {
    throw new TypeError("Product image URL is invalid.");
  }
  if (
    (parsed.protocol !== "https:" && parsed.protocol !== "http:") ||
    parsed.username ||
    parsed.password
  ) {
    throw new TypeError("Product image URL is not downloadable.");
  }
}
