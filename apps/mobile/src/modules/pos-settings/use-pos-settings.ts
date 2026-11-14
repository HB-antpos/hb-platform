import { useQuery, type QueryClient } from "@tanstack/react-query";
import {
  fetchPosSettingsCatalog,
  fetchPosSettingsDevice,
  fetchPosSettingsDevices,
  fetchPosSettingsGlobal,
  fetchPosSettingsStore,
  fetchPosSettingsStores,
} from "./api";
import { posSettingsKeys } from "./query-keys";

/** 设置项目录：服务端清单，基本不变，缓存久一点。 */
export function usePosSettingsCatalog(enabled: boolean) {
  return useQuery({
    queryKey: posSettingsKeys.catalog(),
    queryFn: fetchPosSettingsCatalog,
    enabled,
    staleTime: 5 * 60_000,
  });
}

export function usePosSettingsGlobal(enabled: boolean) {
  return useQuery({ queryKey: posSettingsKeys.global(), queryFn: fetchPosSettingsGlobal, enabled, staleTime: 15_000 });
}

export function usePosSettingsStores(enabled: boolean) {
  return useQuery({ queryKey: posSettingsKeys.stores(), queryFn: fetchPosSettingsStores, enabled, staleTime: 15_000 });
}

export function usePosSettingsStore(storeCode: string, enabled: boolean) {
  return useQuery({
    queryKey: posSettingsKeys.store(storeCode),
    queryFn: () => fetchPosSettingsStore(storeCode),
    enabled: enabled && Boolean(storeCode),
    staleTime: 15_000,
  });
}

export function usePosSettingsDevices(storeCode: string, enabled: boolean) {
  return useQuery({
    queryKey: posSettingsKeys.devices(storeCode),
    queryFn: () => fetchPosSettingsDevices(storeCode),
    enabled: enabled && Boolean(storeCode),
    staleTime: 15_000,
  });
}

export function usePosSettingsDevice(deviceCode: string, enabled: boolean) {
  return useQuery({
    queryKey: posSettingsKeys.device(deviceCode),
    queryFn: () => fetchPosSettingsDevice(deviceCode),
    enabled: enabled && Boolean(deviceCode),
    staleTime: 15_000,
  });
}

/** 保存成功后让全局、门店列表、门店详情、设备列表、设备详情全部失效重取。 */
export function invalidatePosSettings(queryClient: QueryClient) {
  return queryClient.invalidateQueries({ queryKey: posSettingsKeys.all });
}
