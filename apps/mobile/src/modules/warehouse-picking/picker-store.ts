import { create } from "zustand";
import type { AccessControl } from "@/modules/auth/types";
import type { PickerIdentity } from "./types";

/**
 * 登录账号本人能否直接拣货：仓库角色，或拣货权限及其别名（管理仓库、管理仓库订货）。
 * 与后端 WarehousePickingController 的终端校验同口径；纯设备会话没有账号，只能扫员工码。
 */
export function canAccountPick(access: Pick<AccessControl, "isAdmin" | "isWarehouseManager" | "isWarehouseStaff" | "hasPermission">) {
  return (
    access.isAdmin ||
    access.isWarehouseManager ||
    access.isWarehouseStaff ||
    access.hasPermission("Warehouse.Picking") ||
    access.hasPermission("Warehouse.Manage") ||
    access.hasPermission("Warehouse.ManageOrders")
  );
}

interface PickerState {
  picker: PickerIdentity | null;
  setPicker: (picker: PickerIdentity) => void;
  clearPicker: () => void;
}

/**
 * 当前拣货人只放内存、不落盘：App 重启或换人都要重新确认，
 * 避免共用 PDA 上一班次的员工身份被下一个人继续使用。
 */
export const usePickerStore = create<PickerState>((set) => ({
  picker: null,
  setPicker: (picker) => set({ picker }),
  clearPicker: () => set({ picker: null }),
}));

/** 账号本人拣货须仍是当前登录账号；员工码凭证须未过期（提前 1 分钟视为过期，避免请求途中失效）。 */
export function isPickerUsable(
  picker: PickerIdentity | null,
  currentUserGuid: string | null,
  nowMs: number,
): picker is PickerIdentity {
  if (!picker) return false;
  if (picker.method === "account") {
    return Boolean(currentUserGuid) && picker.userGuid.toLowerCase() === currentUserGuid!.toLowerCase();
  }
  if (!picker.ticket || !picker.expiresAtUtc) return false;
  const expiresAt = Date.parse(picker.expiresAtUtc);
  return Number.isFinite(expiresAt) && expiresAt - 60_000 > nowMs;
}
