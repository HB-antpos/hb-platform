import { resolveTrustedProductImageUri } from "@hb/pos-domain/features/sales/runtime/trusted-product-image-uri";

import type { CustomerDisplayProductImagePort } from "./customer-display-publisher";
import type { CustomerDisplayProductImageCache } from "./product-image-cache";

/** 同时进行的「解析 + 下载」任务数上限，避免收银高峰给弱网/POS 主流程添堵。 */
export const PRODUCT_IMAGE_MAX_CONCURRENCY = 2;
/** 下载或解析出错后，同一商品至少间隔这么久才会再试。 */
export const PRODUCT_IMAGE_FAILURE_RETRY_MS = 60_000;
/** 目录里没有图片（或地址不可信）时的重查间隔；商品图通常在后台补上，不必每次发布都查。 */
export const PRODUCT_IMAGE_MISSING_RETRY_MS = 5 * 60_000;
const MAX_TRACKED_BACKOFFS = 1_000;

type ProductImageSubject = Readonly<{
  productCode: string;
  lookupCode: string;
}>;

export type CustomerDisplayProductImageResolverOptions = Readonly<{
  cache: Pick<CustomerDisplayProductImageCache, "fetch" | "touch">;
  /**
   * 把商品解析成受信任的 https（或 API 同源）图片地址；没有图片返回 null。
   * 不应在这里联网，只做本地目录查询与地址校验。
   */
  resolveRemoteUrl(
    productCode: string,
    lookupCode: string,
  ): Promise<string | null>;
  /** 某张图片刚刚落入缓存；装配处用它触发 coordinator.refresh() 重发画面。异常会被吞掉。 */
  onImageReady(): void;
  now?: () => number;
  maxConcurrency?: number;
  failureRetryMs?: number;
  missingRetryMs?: number;
}>;

/**
 * 客显商品缩略图解析器。`peek` 同步只读内存索引（商品编码 → 已缓存本地 URI）：
 * 命中直接返回；未命中返回 null 并在后台排队「解析远端地址 → 下载缓存」，
 * 完成后通过 onImageReady 通知重发画面。客显层因此永远只拿到本地 file URI，不联网。
 */
export class CustomerDisplayProductImageResolver
  implements CustomerDisplayProductImagePort
{
  private readonly localUriByProduct = new Map<string, string>();
  private readonly queued = new Set<string>();
  private readonly queue: ProductImageSubject[] = [];
  private readonly retryNotBefore = new Map<string, number>();
  private readonly now: () => number;
  private readonly maxConcurrency: number;
  private readonly failureRetryMs: number;
  private readonly missingRetryMs: number;
  private active = 0;
  private disposed = false;

  public constructor(
    private readonly options: CustomerDisplayProductImageResolverOptions,
  ) {
    this.now = options.now ?? Date.now;
    this.maxConcurrency = Math.max(
      1,
      options.maxConcurrency ?? PRODUCT_IMAGE_MAX_CONCURRENCY,
    );
    this.failureRetryMs = options.failureRetryMs ?? PRODUCT_IMAGE_FAILURE_RETRY_MS;
    this.missingRetryMs = options.missingRetryMs ?? PRODUCT_IMAGE_MISSING_RETRY_MS;
  }

  public peek(line: ProductImageSubject): string | null {
    const productCode = line.productCode.trim();
    if (this.disposed || productCode.length === 0) return null;

    const cached = this.localUriByProduct.get(productCode);
    if (cached !== undefined) {
      // touch 同时刷新 LRU；返回 false 表示文件已被淘汰，丢弃索引并重新排队。
      if (this.options.cache.touch(cached)) return cached;
      this.localUriByProduct.delete(productCode);
    }
    this.enqueue(productCode, line.lookupCode.trim());
    return null;
  }

  /** 停止后续排队与回调；已在途的下载会自然结束，但不再触发 onImageReady。 */
  public dispose(): void {
    this.disposed = true;
    this.queue.length = 0;
    this.queued.clear();
  }

  private enqueue(productCode: string, lookupCode: string): void {
    if (this.queued.has(productCode)) return;
    const notBefore = this.retryNotBefore.get(productCode);
    if (notBefore !== undefined) {
      if (this.now() < notBefore) return;
      this.retryNotBefore.delete(productCode);
    }
    this.queued.add(productCode);
    this.queue.push({ productCode, lookupCode });
    this.pump();
  }

  private pump(): void {
    while (
      !this.disposed &&
      this.active < this.maxConcurrency &&
      this.queue.length > 0
    ) {
      const subject = this.queue.shift()!;
      this.active += 1;
      void this.run(subject).finally(() => {
        this.active -= 1;
        this.queued.delete(subject.productCode);
        this.pump();
      });
    }
  }

  private async run(subject: ProductImageSubject): Promise<void> {
    const { productCode, lookupCode } = subject;
    try {
      const remoteUrl = await this.options.resolveRemoteUrl(
        productCode,
        lookupCode,
      );
      if (remoteUrl === null) {
        this.deferRetry(productCode, this.missingRetryMs);
        return;
      }
      const localUri = await this.options.cache.fetch(remoteUrl);
      if (this.disposed) return;
      this.localUriByProduct.set(productCode, localUri);
      try {
        this.options.onImageReady();
      } catch {
        // 刷新客显失败不能影响收银主流程。
      }
    } catch {
      this.deferRetry(productCode, this.failureRetryMs);
    }
  }

  private deferRetry(productCode: string, delayMs: number): void {
    if (this.retryNotBefore.size >= MAX_TRACKED_BACKOFFS) {
      // 退避表只是节流用的，超限时先清掉已到期项，仍超限就整体重置，宁可多试几次也不无限增长。
      const current = this.now();
      for (const [code, notBefore] of this.retryNotBefore) {
        if (notBefore <= current) this.retryNotBefore.delete(code);
      }
      if (this.retryNotBefore.size >= MAX_TRACKED_BACKOFFS) {
        this.retryNotBefore.clear();
      }
    }
    this.retryNotBefore.set(productCode, this.now() + delayMs);
  }
}

type CatalogProductMatch = Readonly<{
  productCode: string;
  lookupCode: string;
  productImage: string | null;
}>;

export type CatalogProductImageUrlResolverInput = Readonly<{
  apiBaseUrl: string;
  findExact(lookupCode: string): Promise<CatalogProductMatch | null>;
  findExactCandidates?(
    lookupCode: string,
  ): Promise<readonly CatalogProductMatch[]>;
}>;

/**
 * 与销售页 resolveTrustedCartProductDetails 同口径：用购物车行的商品编码 + 查询码
 * 回查本地目录，要求目录项与购物车行一致，再把 productImage 解析成受信任的
 * https（或 API 同源 / 本机开发）URL；任何一步不满足就当作没有图片。
 */
export function createCatalogProductImageUrlResolver(
  input: CatalogProductImageUrlResolverInput,
): (productCode: string, lookupCode: string) => Promise<string | null> {
  return async (productCode, lookupCode) => {
    const normalizedProductCode = normalizeIdentity(productCode);
    const normalizedLookupCode = normalizeIdentity(lookupCode);
    if (!normalizedProductCode || !normalizedLookupCode) return null;

    // 一码多商品时购物车行可能不是目录胜出项，需在同码候选中按商品编码取图。
    const match = input.findExactCandidates
      ? ((await input.findExactCandidates(normalizedLookupCode)).find(
          (candidate) => candidate.productCode.trim() === normalizedProductCode,
        ) ?? null)
      : await input.findExact(normalizedLookupCode);
    if (
      !match ||
      match.productCode.trim() !== normalizedProductCode ||
      match.lookupCode.trim() !== normalizedLookupCode
    ) {
      return null;
    }
    return resolveTrustedProductImageUri(match.productImage, input.apiBaseUrl);
  };
}

function normalizeIdentity(value: string): string | null {
  const normalized = value.trim();
  if (
    !normalized ||
    normalized.length > 256 ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    return null;
  }
  return normalized;
}
