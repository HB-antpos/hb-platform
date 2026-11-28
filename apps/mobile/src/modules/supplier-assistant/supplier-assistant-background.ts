import type { AssistantApiResult } from "./supplier-assistant-session";

/**
 * 大屏收银 App 内「订货助手」的后台：接住网页里插件脚本发来的消息，代它调用订货助手接口。
 *
 * 中文注释：
 * 1. 对应浏览器插件 service-worker.js 的消息分发，只实现 App 第一阶段用到的几种。
 * 2. 网页窗口里插件脚本和供应商网站脚本同处一个 JS 环境，网站脚本也能往这里发消息，所以每条消息都要：
 *    ① 来源网址必须属于当前打开的这家供应商；② 供应商编码必须与当前供应商一致；③ 分店一律用设备绑定的分店。
 * 3. 后端还会再按令牌里的分店收口，这里是第一道闸。
 */
export type SupplierProfileLite = Readonly<{
  supplierCode: string;
  displayName: string;
  enabled: boolean;
  origins: readonly string[];
  hasSearch: boolean;
  categoryPassiveEnabled: boolean;
}>;

export type LocateRequest = Readonly<{
  supplierCode: string;
  itemNumber: string;
  source: "button" | "scan";
}>;

export type BackgroundResponse = Readonly<Record<string, unknown>>;

export type SupplierAssistantRequester = (
  path: string,
  init: Readonly<{ method: "GET" | "POST"; body?: unknown }>,
) => Promise<AssistantApiResult>;

export type SupplierAssistantBackgroundOptions = Readonly<{
  request: SupplierAssistantRequester;
  /** 当前网页窗口打开的是哪家供应商（没有则为 null）。 */
  getActiveProfile(): SupplierProfileLite | null;
  /** 设备绑定的分店（令牌里的分店）。 */
  getStoreCode(): string | null;
  /** 商品按钮被点 / 扫码定位到商品：App 打开右侧采购周期面板。 */
  onLocate(request: LocateRequest): void;
  now?: () => number;
}>;

const MAX_ITEM_NUMBERS = 100;
const MAX_ITEM_NUMBER_LENGTH = 64;
const MAX_BARCODE_LENGTH = 64;
// 被动分类采集：与插件同口径的本地节流（每分钟最多 20 次），超出直接丢弃，后端另有限流。
const CAPTURE_WINDOW_MS = 60_000;
const CAPTURE_MAX_PER_WINDOW = 20;
const MAX_CAPTURE_PAYLOAD_CHARS = 200_000;

export function createSupplierAssistantBackground(options: SupplierAssistantBackgroundOptions) {
  const now = options.now ?? Date.now;
  let captureTimestamps: number[] = [];

  function reject(error: string, errorCode = "FORBIDDEN"): BackgroundResponse {
    return Object.freeze({ ok: false, error, errorCode });
  }

  function apiFailure(result: AssistantApiResult, fallback: string): BackgroundResponse {
    return Object.freeze({
      ok: false,
      httpStatus: result.httpStatus,
      errorCode: result.errorCode,
      error: result.message || result.errorCode || fallback,
    });
  }

  async function handle(message: unknown, senderUrl: string | null): Promise<BackgroundResponse> {
    if (!message || typeof message !== "object") return reject("消息无效", "INVALID_MESSAGE");
    const payload = message as Record<string, unknown>;
    const type = typeof payload.type === "string" ? payload.type : "";
    const profile = options.getActiveProfile();
    // ① 来源网址必须是当前这家供应商的网站。
    if (!profile || !senderUrl || !originMatchesProfile(profile, senderUrl)) {
      return reject("来源网页不属于当前供应商");
    }
    // ② 供应商编码必须与当前供应商一致（插件脚本按页面匹配出的 profile 填写）。
    if (
      "supplierCode" in payload
      && payload.supplierCode !== undefined
      && payload.supplierCode !== profile.supplierCode
    ) {
      return reject("供应商不匹配");
    }
    const storeCode = options.getStoreCode();

    switch (type) {
      case "SUMMARY_BATCH": {
        const itemNumbers = readItemNumbers(payload.itemNumbers);
        if (!storeCode || !itemNumbers) return reject("参数缺失", "INVALID_REQUEST");
        const result = await options.request(
          "/api/react/v1/browser-extension/product-purchase-cycle-summary/batch",
          {
            method: "POST",
            body: {
              // ③ 分店一律用设备绑定的分店，不信网页传来的值。
              storeCode,
              supplierCode: profile.supplierCode,
              itemNumbers,
              salesRankingDays: normalizeRankingDays(payload.salesRankingDays),
            },
          },
        );
        return result.success ? Object.freeze({ ok: true, data: result.data }) : apiFailure(result, "摘要获取失败");
      }
      case "BARCODE_LOOKUP": {
        const barcode = readText(payload.barcode, MAX_BARCODE_LENGTH);
        if (!barcode || !profile.hasSearch) return reject("参数缺失", "INVALID_REQUEST");
        const result = await options.request("/api/react/v1/browser-extension/barcode-lookup", {
          method: "POST",
          body: { supplierCode: profile.supplierCode, barcode },
        });
        return result.success ? Object.freeze({ ok: true, data: result.data }) : apiFailure(result, "条码查询失败");
      }
      case "LOCATE_ITEM":
      case "SCAN_LOCATE_ITEM": {
        const itemNumber = readText(payload.itemNumber, MAX_ITEM_NUMBER_LENGTH);
        if (!itemNumber) return reject("参数缺失", "INVALID_REQUEST");
        options.onLocate({
          supplierCode: profile.supplierCode,
          itemNumber,
          source: type === "LOCATE_ITEM" ? "button" : "scan",
        });
        return Object.freeze({ ok: true });
      }
      case "CATEGORY_CAPTURE": {
        const capture = payload.payload;
        if (!profile.categoryPassiveEnabled) return reject("分类采集未开启", "CATEGORY_CAPTURE_DISABLED");
        if (!isPassiveCaptureFromSupplier(capture, profile, senderUrl)) {
          return reject("分类采集内容无效", "INVALID_CAPTURE");
        }
        const current = now();
        captureTimestamps = captureTimestamps.filter((at) => current - at < CAPTURE_WINDOW_MS);
        if (captureTimestamps.length >= CAPTURE_MAX_PER_WINDOW) {
          return Object.freeze({ ok: false, httpStatus: 429, errorCode: "LOCAL_RATE_LIMITED", retryAfterMs: CAPTURE_WINDOW_MS });
        }
        captureTimestamps.push(current);
        const result = await options.request(
          "/api/react/v1/browser-extension/supplier-categories/captures",
          { method: "POST", body: capture },
        );
        return result.success
          ? Object.freeze({ ok: true, data: result.data })
          : Object.freeze({ ...apiFailure(result, "分类回传失败") });
      }
      case "CATEGORY_TREE_SNAPSHOT":
      case "CATEGORY_CRAWL_PROGRESS":
        // 主动整站分类采集只在电脑浏览器插件里做；App 第一阶段不支持。
        return reject("App 内暂不支持主动分类采集", "CRAWL_NOT_SUPPORTED");
      default:
        return reject("未知消息类型", "UNKNOWN_MESSAGE");
    }
  }

  return Object.freeze({ handle });
}

/** 插件 origins 写法：https://www.dats.com.au/* 或 https://*.example.com/*。 */
export function originMatchesProfile(profile: SupplierProfileLite, url: string): boolean {
  const origin = readOrigin(url);
  if (!origin) return false;
  return profile.origins.some((pattern) => originMatchesPattern(pattern, origin));
}

export function originMatchesPattern(pattern: string, origin: string): boolean {
  const match = /^(https?):\/\/([^/]+)\/\*$/i.exec(pattern.trim());
  if (!match) return false;
  const [, scheme, host] = match;
  const parsed = /^(https?):\/\/([^/]+)$/i.exec(origin);
  if (!parsed) return false;
  if (parsed[1]!.toLowerCase() !== scheme!.toLowerCase()) return false;
  const actualHost = parsed[2]!.toLowerCase();
  const expectedHost = host!.toLowerCase();
  if (expectedHost.startsWith("*.")) {
    const suffix = expectedHost.slice(1);
    return actualHost.endsWith(suffix) && actualHost.length > suffix.length;
  }
  return actualHost === expectedHost;
}

export type SupplierNavigationDecision = Readonly<
  { action: "allow" } | { action: "upgrade"; url: string } | { action: "block" }
>;

/**
 * 供应商网页窗口里遇到一个地址该怎么办。
 *
 * 中文注释：网页窗口的 originWhitelist 同时放行 http/https，否则不在白名单里的地址会被系统交给 Safari 打开；
 * 真正的收口在这里。iOS 的 ATS 不让 App 内网页走明文 http（报 -1022），所以：
 * 1. https 照旧放行；
 * 2. 换成 https 后命中本供应商规则的 http 地址（Brazco / PJ SAS / Yatsal 首页会先 301 到本站 http 再跳回 https）
 *    → 由 App 直接改用 https 加载，跳过明文那一跳；
 * 3. 精确命中 http 规则的（TXK 整站 http）放行，能否打开取决于原生包是否给该域名开了 ATS 例外；
 * 4. 其余 http 与其他协议一律不打开（也不会再跳 Safari）。
 */
export function decideSupplierNavigation(profile: SupplierProfileLite, url: string): SupplierNavigationDecision {
  const value = url.trim();
  if (value === "about:blank" || /^https:\/\//i.test(value)) return { action: "allow" };
  if (!/^http:\/\//i.test(value)) return { action: "block" };
  const upgraded = value.replace(/^http:/i, "https:");
  if (originMatchesProfile(profile, upgraded)) return { action: "upgrade", url: upgraded };
  if (originMatchesProfile(profile, value)) return { action: "allow" };
  return { action: "block" };
}

/**
 * 供应商首页：取 origin 规则去掉 /*，有 www 开头的优先。
 * 中文注释：裸域名常会先 301 到 http 再跳 www（如 Yatsal），App 内不能走明文那一跳，直接从 www 进更稳。
 */
export function resolveProfileHomeUrl(profile: SupplierProfileLite): string | null {
  const homes: string[] = [];
  for (const pattern of profile.origins) {
    const match = /^(https?:\/\/[^/*]+)\/\*$/i.exec(pattern.trim());
    if (match) homes.push(`${match[1]}/`);
  }
  return homes.find((home) => /^https?:\/\/www\./i.test(home)) ?? homes[0] ?? null;
}

export function readOrigin(url: string): string | null {
  const match = /^(https?:\/\/[^/?#]+)/i.exec(url.trim());
  return match ? match[1]!.toLowerCase() : null;
}

/** 后端下发的 supplier-profiles → App 用得到的最小字段；不合格的条目直接丢弃。 */
export function toProfileLites(raw: unknown): SupplierProfileLite[] {
  const profiles = raw && typeof raw === "object" ? (raw as { profiles?: unknown }).profiles : null;
  if (!Array.isArray(profiles)) return [];
  const out: SupplierProfileLite[] = [];
  for (const item of profiles) {
    if (!item || typeof item !== "object") continue;
    const profile = item as Record<string, unknown>;
    const origins = Array.isArray(profile.origins)
      ? profile.origins.filter((origin): origin is string => typeof origin === "string")
      : [];
    if (
      typeof profile.supplierCode !== "string"
      || !profile.supplierCode
      || typeof profile.displayName !== "string"
      || profile.enabled !== true
      || origins.length === 0
    ) {
      continue;
    }
    const category = profile.category && typeof profile.category === "object"
      ? (profile.category as Record<string, unknown>)
      : null;
    out.push(Object.freeze({
      supplierCode: profile.supplierCode,
      displayName: profile.displayName,
      enabled: true,
      origins: Object.freeze([...origins]),
      hasSearch: !!profile.search && typeof profile.search === "object",
      categoryPassiveEnabled: category?.enabled === true && category?.passiveEnabled === true,
    }));
  }
  return out;
}

function readItemNumbers(value: unknown): string[] | null {
  if (!Array.isArray(value) || value.length === 0 || value.length > MAX_ITEM_NUMBERS) return null;
  const out: string[] = [];
  for (const item of value) {
    const text = readText(item, MAX_ITEM_NUMBER_LENGTH);
    if (!text) return null;
    out.push(text);
  }
  return out;
}

function readText(value: unknown, maxLength: number): string | null {
  if (typeof value !== "string") return null;
  const text = value.trim();
  return text.length > 0 && text.length <= maxLength ? text : null;
}

// 与插件 lib/ranking.js 的 normalizeRankingDays 同口径：只有 60 / 90 两档。
function normalizeRankingDays(value: unknown): number {
  return Number(value) === 90 ? 90 : 60;
}

/**
 * 被动分类采集载荷的最低限度核对（后端还有完整校验）：
 * 只接受 passive 模式、供应商一致、页面网址与来源网页同源、体积不过大。
 */
function isPassiveCaptureFromSupplier(
  capture: unknown,
  profile: SupplierProfileLite,
  senderUrl: string,
): boolean {
  if (!capture || typeof capture !== "object") return false;
  const body = capture as Record<string, unknown>;
  if (body.mode !== "passive" || body.supplierCode !== profile.supplierCode) return false;
  const senderOrigin = readOrigin(senderUrl);
  const pageUrl = typeof body.pageUrl === "string" ? body.pageUrl : "";
  if (!senderOrigin || readOrigin(pageUrl) !== senderOrigin) return false;
  try {
    return JSON.stringify(capture).length <= MAX_CAPTURE_PAYLOAD_CHARS;
  } catch {
    return false;
  }
}
