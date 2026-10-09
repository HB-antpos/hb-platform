import { useMemo } from "react";
import { useQueries, type UseQueryResult } from "@tanstack/react-query";
import { apiClient } from "@/shared/api/client";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import en from "@/locales/en/releaseOverview.json";
import zh from "@/locales/zh/releaseOverview.json";
import {
  appDownloadsApi,
  handheldPolicy,
  ipadRollout,
  otaPolicy,
} from "../api";
import type { AppDownloadPlatform } from "../types";
import { getWpfReleases } from "../../wpf-versions/api";
import type { WpfRelease } from "../../wpf-versions/types";
import {
  buildReleaseOverview,
  normalizeMobileAndroidNativePolicy,
  readHandheldManaged,
  readLaneAudit,
  type AuditedHandheldPolicy,
  type AuditedIpadOtaRollout,
  type AuditedOtaPolicy,
  type LaneSource,
  type ReleaseOverviewCopy,
  type ReleaseOverviewModel,
  type ReleaseOverviewSources,
} from "./logic";

/** 总览所有查询的公共前缀：保存策略后 invalidateQueries({ queryKey: RELEASE_OVERVIEW_QUERY_KEY }) 即可整体刷新。 */
export const RELEASE_OVERVIEW_QUERY_KEY = ["release-center-overview"] as const;

export function useReleaseOverviewCopy(): ReleaseOverviewCopy {
  const { language } = useAppTranslation("common");
  return language === "en" ? en : zh;
}

// 员工端 OTA / iPad OTA / 手持策略：现有 appDownloadsApi 的归一结果丢掉了 updatedAt / updatedBy，
// 这里直接读同一接口，复用 api-contract 的归一函数，再从原始响应补读审计字段。
async function getUnwrapped(path: string) {
  const response = await apiClient.get(path);
  return unwrapApiEnvelope<unknown>(response.data);
}

async function loadMobileOtaPolicy(
  platform: AppDownloadPlatform,
): Promise<AuditedOtaPolicy> {
  // 与 Web 总览一致只看 production 环境。
  const raw = await getUnwrapped(
    `/app-update-policies/mobile-ota/production/${platform}`,
  );
  return { ...otaPolicy(raw), ...readLaneAudit(raw) };
}

async function loadIpadOtaRollout(): Promise<AuditedIpadOtaRollout> {
  const raw = await getUnwrapped("/pos-ipad/ota-rollout");
  return { ...ipadRollout(raw), ...readLaneAudit(raw) };
}

async function loadHandheldPolicies(): Promise<AuditedHandheldPolicy[]> {
  const raw = await getUnwrapped("/app-update-policies/pos-handheld");
  const record =
    raw && typeof raw === "object" && !Array.isArray(raw)
      ? (raw as Record<string, unknown>)
      : {};
  const rows = Array.isArray(raw)
    ? raw
    : Array.isArray(record.policies)
      ? record.policies
      : [];
  return rows.map((row) => ({
    ...handheldPolicy(row),
    ...readLaneAudit(row),
    managed: readHandheldManaged(row),
  }));
}

async function loadMobileAndroidNativePolicy() {
  return normalizeMobileAndroidNativePolicy(
    await getUnwrapped("/app-update-policies/mobile-android"),
  );
}

async function loadNative(app: "mobile-ios" | "pos-ipad") {
  const [policy, releases] = await Promise.all([
    appDownloadsApi.getNativePolicy(app),
    appDownloadsApi.getIosReleases(app),
  ]);
  return { policy, releases };
}

/** WPF 策略摘要要读该通道全量版本（含停用）：第一页 100 条，再按 total 翻页（同 wpf-versions 页）。 */
async function loadAllWpfReleases(channel: "production" | "preview") {
  const firstPage = await getWpfReleases({
    channel,
    includeDisabled: true,
    page: 1,
    pageSize: 100,
  });
  const all: WpfRelease[] = [...firstPage.items];
  const pageSize = Math.max(1, firstPage.pageSize);
  const pageCount = Math.max(1, Math.ceil(firstPage.total / pageSize));
  for (let page = 2; page <= pageCount; page += 1) {
    const next = await getWpfReleases({
      channel,
      includeDisabled: true,
      page,
      pageSize,
    });
    all.push(...next.items);
    if (next.items.length === 0) break;
  }
  return all;
}

/** 每个数据源一条查询：单个接口失败只把自己的线路标 error，不拖垮整页。 */
const SOURCES = [
  { id: "mobile-ios-native", load: () => loadNative("mobile-ios") },
  { id: "mobile-android-native", load: loadMobileAndroidNativePolicy },
  { id: "mobile-ios-ota", load: () => loadMobileOtaPolicy("ios") },
  { id: "mobile-android-ota", load: () => loadMobileOtaPolicy("android") },
  { id: "ipad-native", load: () => loadNative("pos-ipad") },
  { id: "ipad-ota", load: loadIpadOtaRollout },
  { id: "handheld", load: loadHandheldPolicies },
  { id: "wpf-production", load: () => loadAllWpfReleases("production") },
  { id: "wpf-preview", load: () => loadAllWpfReleases("preview") },
] as const;

function toSource<T>(query: UseQueryResult<unknown>): LaneSource<T> {
  // 刷新失败但手里有上次结果时继续显示旧数据，避免下拉刷新把整行变成错误。
  if (query.data !== undefined)
    return { state: "ready", value: query.data as T };
  if (query.isError) return { state: "error" };
  return { state: "loading" };
}

export interface ReleaseOverviewResult extends ReleaseOverviewModel {
  /** 首次读取中（所有数据源都还没有结果）。 */
  isLoading: boolean;
  isFetching: boolean;
  /** 所有数据源都失败且没有可显示的数据。 */
  isAllFailed: boolean;
  /** 至少一条线路读取失败。 */
  hasFailure: boolean;
  refetch: () => Promise<void>;
}

export function useReleaseOverview(): ReleaseOverviewResult {
  const copy = useReleaseOverviewCopy();
  const queries = useQueries({
    queries: SOURCES.map((source) => ({
      queryKey: [...RELEASE_OVERVIEW_QUERY_KEY, source.id],
      queryFn: source.load as () => Promise<unknown>,
      retry: 1,
      // 总览是落地页，回来时尽快反映别处的策略变更。
      staleTime: 30 * 1000,
    })),
  });

  const sources: ReleaseOverviewSources = {
    mobileIosNative: toSource(queries[0]),
    mobileAndroidNative: toSource(queries[1]),
    mobileIosOta: toSource(queries[2]),
    mobileAndroidOta: toSource(queries[3]),
    ipadNative: toSource(queries[4]),
    ipadOta: toSource(queries[5]),
    handheld: toSource(queries[6]),
    wpfProduction: toSource(queries[7]),
    wpfPreview: toSource(queries[8]),
  };
  const dataStamp = queries.map((query) => query.dataUpdatedAt).join(",");
  const errorStamp = queries.map((query) => query.errorUpdatedAt).join(",");
  const pendingStamp = queries.map((query) => query.isPending).join(",");

  const model = useMemo(
    () => buildReleaseOverview(sources, copy),
    // sources 每次渲染都是新对象，用各查询的更新时间戳判断数据是否真的变化。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [dataStamp, errorStamp, pendingStamp, copy],
  );

  // 只重拉总览自己的查询；失败的数据源也会一并重试。
  const refetch = async () => {
    await Promise.all(queries.map((query) => query.refetch()));
  };

  return {
    ...model,
    isLoading: queries.every((query) => query.isPending),
    isFetching: queries.some((query) => query.isFetching),
    isAllFailed: queries.every(
      (query) => query.data === undefined && query.isError,
    ),
    hasFailure: model.lanes.some((lane) => lane.status === "error"),
    refetch,
  };
}
