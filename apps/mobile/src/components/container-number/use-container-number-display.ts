import { useCallback } from "react";
import { create } from "zustand";
import { formatContainerNumber } from "@/shared/utils/container-number";
import { useAuthStore } from "@/store/auth-store";

interface ContainerNumberRevealState {
  revealed: boolean;
  setRevealed: (revealed: boolean) => void;
}

/**
 * 「显示完整货柜编号」开关：全 App 共用、只存内存（重启 App 恢复默认遮罩），
 * 点任意一处眼睛按钮后所有货柜页面与导出文件名一起显示完整编号。
 */
export const useContainerNumberRevealStore = create<ContainerNumberRevealState>((set) => ({
  revealed: false,
  setRevealed: (revealed) => set({ revealed }),
}));

export interface ContainerNumberDisplay {
  /** 账号是否有「查看完整货柜编号」权限（决定是否显示眼睛按钮） */
  canReveal: boolean;
  /** 当前是否显示完整编号（有权限且已点开） */
  showFull: boolean;
  toggle: () => void;
  format: (value: string | null | undefined) => string;
}

export function useContainerNumberDisplay(): ContainerNumberDisplay {
  const canReveal = useAuthStore((state) => state.access.canViewFullContainerNumber);
  const revealed = useContainerNumberRevealStore((state) => state.revealed);
  const setRevealed = useContainerNumberRevealStore((state) => state.setRevealed);
  // 没有权限时即使开关残留为 true 也一律遮罩（例如切换到无权限账号）。
  const showFull = canReveal && revealed;
  const toggle = useCallback(() => setRevealed(!revealed), [revealed, setRevealed]);
  const format = useCallback(
    (value: string | null | undefined) => formatContainerNumber(value, showFull),
    [showFull]
  );
  return { canReveal, showFull, toggle, format };
}
