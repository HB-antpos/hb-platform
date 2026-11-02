// 工资单的数据钩子：公司列表、工资单列表、详情、我方考勤工时（点开才取）、发信目标。
// 工资单是敏感数据：缓存 key 带当前用户，详情 / 工时页面卸载后很快回收（会话结束时 query 缓存整体清空）。
import { useRef } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useAuthStore } from "@/store/auth-store";
import { payslipsApi } from "./api";
import type { PayslipKey, PayslipListQuery } from "./types";

/** 一分钟内重复进入同一筛选不再重取（下拉刷新另带 refresh 绕过缓存）。 */
export const PAYSLIP_LIST_STALE_MS = 60_000;
/** 详情等敏感数据离开页面 2 分钟后回收。 */
export const PAYSLIP_DETAIL_GC_MS = 2 * 60_000;

function keyId(key: PayslipKey) {
  return [key.connectionGuid, key.employeeUid, key.paymentDate, key.periodStart ?? ""] as const;
}

export const payslipKeys = {
  companies: (userGuid: string | null) => ["payslips", userGuid, "companies"] as const,
  list: (userGuid: string | null, query: Omit<PayslipListQuery, "refresh">) =>
    ["payslips", userGuid, "list", query.connectionGuid, query.storeCode, query.from, query.to, query.keyword, query.onlyUnassigned] as const,
  detail: (userGuid: string | null, key: PayslipKey) => ["payslips", userGuid, "detail", ...keyId(key)] as const,
  hours: (userGuid: string | null, key: PayslipKey) => ["payslips", userGuid, "hours", ...keyId(key)] as const,
  emailTarget: (userGuid: string | null, key: PayslipKey) => ["payslips", userGuid, "email-target", ...keyId(key)] as const,
};

export function usePayslipCompanies() {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: payslipKeys.companies(userGuid),
    queryFn: ({ signal }) => payslipsApi.fetchCompanies(signal),
    enabled: Boolean(userGuid),
    staleTime: 5 * 60_000,
    retry: 1,
  });
}

export function usePayslipList(query: Omit<PayslipListQuery, "refresh">) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  // 下拉刷新要让后端绕过缓存：用 ref 带一次 refresh 标记，取数后立刻复位，不进 queryKey。
  const refreshNextRef = useRef(false);
  const result = useQuery({
    queryKey: payslipKeys.list(userGuid, query),
    queryFn: ({ signal }) => {
      const refresh = refreshNextRef.current;
      refreshNextRef.current = false;
      return payslipsApi.fetchList({ ...query, refresh }, signal);
    },
    enabled: Boolean(userGuid),
    staleTime: PAYSLIP_LIST_STALE_MS,
    retry: 1,
    // 切筛选时保留上一屏避免闪白。
    placeholderData: keepPreviousData,
  });
  const refreshFromSource = () => {
    refreshNextRef.current = true;
    return result.refetch();
  };
  return { ...result, refreshFromSource };
}

export function usePayslipDetail(key: PayslipKey | null) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: key ? payslipKeys.detail(userGuid, key) : ["payslips", userGuid, "detail", "none"],
    queryFn: ({ signal }) => payslipsApi.fetchDetail(key as PayslipKey, signal),
    enabled: Boolean(userGuid && key),
    staleTime: PAYSLIP_LIST_STALE_MS,
    gcTime: PAYSLIP_DETAIL_GC_MS,
    retry: 1,
  });
}

/** 「我方考勤工时」：enabled 由页面在用户点「查看我方工时」后才置 true。 */
export function usePayslipHours(key: PayslipKey | null, enabled: boolean) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: key ? payslipKeys.hours(userGuid, key) : ["payslips", userGuid, "hours", "none"],
    queryFn: ({ signal }) => payslipsApi.fetchHours(key as PayslipKey, signal),
    enabled: Boolean(userGuid && key && enabled),
    staleTime: PAYSLIP_LIST_STALE_MS,
    gcTime: PAYSLIP_DETAIL_GC_MS,
    retry: 1,
  });
}

/** 发信弹层打开时读取默认收件人（档案邮箱）、主题、文件名与自己的邮箱；每次打开重新取，确保邮箱是最新的。 */
export function usePayslipEmailTarget(key: PayslipKey | null, enabled: boolean) {
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  return useQuery({
    queryKey: key ? payslipKeys.emailTarget(userGuid, key) : ["payslips", userGuid, "email-target", "none"],
    queryFn: ({ signal }) => payslipsApi.fetchEmailTarget(key as PayslipKey, signal),
    enabled: Boolean(userGuid && key && enabled),
    staleTime: 0,
    // 弹层一关就回收：下次打开一定读到最新的档案邮箱，不沿用旧缓存。
    gcTime: 0,
    retry: 1,
  });
}
