import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  activateDevice,
  disableDevice,
  fetchAppDeviceStatuses,
  fetchAppDeviceStatusSummary,
  fetchAppVersionDistribution,
  fetchDeviceManagementDevices,
  fetchDeviceVersionSummary,
  lockDevice,
} from "@/modules/device-management/api";
import type {
  AppDeviceStatusQuery,
  DeviceManagementActionPayload,
  DeviceManagementListViewResult,
  DeviceManagementQuery,
} from "@/modules/device-management/types";

export function deviceManagementQueryKey(query: DeviceManagementQuery = {}) {
  return ["deviceManagement", query] as const;
}

export function useDeviceManagementDevices(query: DeviceManagementQuery = {}, enabled = true) {
  return useQuery({
    queryKey: deviceManagementQueryKey(query),
    enabled,
    queryFn: () => fetchDeviceManagementDevices(query),
  });
}

export function appDeviceStatusQueryKey(query: AppDeviceStatusQuery = {}) {
  return ["appDeviceStatus", query] as const;
}

export function useAppDeviceStatuses(query: AppDeviceStatusQuery = {}, enabled = true) {
  return useQuery({
    queryKey: appDeviceStatusQueryKey(query),
    enabled,
    queryFn: () => fetchAppDeviceStatuses(query),
  });
}

export function useAppDeviceStatusSummary(
  query: Omit<AppDeviceStatusQuery, "onlineState" | "pageNumber" | "pageSize"> = {},
  enabled = true
) {
  return useQuery({
    queryKey: ["appDeviceStatusSummary", query] as const,
    enabled,
    queryFn: () => fetchAppDeviceStatusSummary(query),
  });
}

/** 设备注册的版本汇总：筛选条件与列表一致，但不含版本条件，点选版本后其余版本仍可切换。 */
export function useDeviceVersionSummary(
  query: Omit<DeviceManagementQuery, "pageNumber" | "pageSize" | "appVersions" | "versionState"> = {},
  enabled = true
) {
  return useQuery({
    queryKey: ["deviceVersionSummary", query] as const,
    enabled,
    queryFn: () => fetchDeviceVersionSummary(query),
  });
}

export function useAppVersionDistribution(
  query: Pick<AppDeviceStatusQuery, "keyword" | "storeCode" | "deviceSystem"> = {},
  enabled = true
) {
  return useQuery({
    queryKey: ["appVersionDistribution", query] as const,
    enabled,
    queryFn: () => fetchAppVersionDistribution(query),
  });
}

export function useDeviceManagementList(query: DeviceManagementQuery = {}, enabled = true) {
  return useQuery({
    queryKey: deviceManagementQueryKey(query),
    enabled,
    queryFn: async (): Promise<DeviceManagementListViewResult> => {
      const result = await fetchDeviceManagementDevices(query);
      return {
        items: result.devices,
        total: result.pagination.totalCount,
        pageNumber: result.pagination.pageNumber,
        pageSize: result.pagination.pageSize,
        totalPages: result.pagination.totalPages,
      };
    },
  });
}

export function useDeviceManagementMutations(_query?: DeviceManagementQuery) {
  const queryClient = useQueryClient();
  const invalidateDeviceManagement = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ["deviceManagement"] }),
      queryClient.invalidateQueries({ queryKey: ["deviceVersionSummary"] }),
    ]);

  const activateMutation = useMutation({
    mutationFn: ({ id }: DeviceManagementActionPayload) => activateDevice(id),
    onSuccess: invalidateDeviceManagement,
  });

  const disableMutation = useMutation({
    mutationFn: ({ id }: DeviceManagementActionPayload) => disableDevice(id),
    onSuccess: invalidateDeviceManagement,
  });

  const lockMutation = useMutation({
    mutationFn: ({ id }: DeviceManagementActionPayload) => lockDevice(id),
    onSuccess: invalidateDeviceManagement,
  });

  return {
    activateMutation,
    disableMutation,
    lockMutation,
  };
}
