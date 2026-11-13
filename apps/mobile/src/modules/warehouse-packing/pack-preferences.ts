import { create } from "zustand";
import type { ScanCountMode } from "./types";

interface PackPreferencesState {
  /** 装箱计数方式：换箱后沿用上一只箱的选择。 */
  scanMode: ScanCountMode;
  /** 小标签（472 点宽）：与商品查询页的「小标签」开关同义，只在本次运行内记住。 */
  smallLabel: boolean;
  setScanMode: (mode: ScanCountMode) => void;
  toggleSmallLabel: () => void;
}

export const usePackPreferences = create<PackPreferencesState>((set) => ({
  scanMode: "each",
  smallLabel: false,
  setScanMode: (scanMode) => set({ scanMode }),
  toggleSmallLabel: () => set((state) => ({ smallLabel: !state.smallLabel })),
}));

export function packPrintType(smallLabel: boolean) {
  return smallLabel ? "small" : null;
}
