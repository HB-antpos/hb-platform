// 位置请求的轻量提示（Snackbar）状态：运行时写入，根布局里的 LocationRequestNotice 读取并展示。
// 只放「提示类型 / 请求人姓名 / 条数」，不含任何坐标；仅内存，不落盘。
import { create } from "zustand";
import type { LocationRequestNotice } from "./location-request-logic";

interface LocationRequestNoticeState {
  /** 递增编号：同一类提示连续出现时也能让 Snackbar 重新显示。 */
  seq: number;
  notice: LocationRequestNotice | null;
  show: (notice: LocationRequestNotice) => void;
  dismiss: () => void;
}

export const useLocationRequestNoticeStore = create<LocationRequestNoticeState>((set) => ({
  seq: 0,
  notice: null,
  show: (notice) => set((state) => ({ seq: state.seq + 1, notice })),
  dismiss: () => set({ notice: null }),
}));
