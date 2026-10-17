import { create } from "zustand";
import type { VaultEntry } from "./vault";

/** 切换遮罩上的四步进度（下标即步骤）：校验 → 清理 → 读取权限与菜单 → 收尾进入工作台。 */
export const SWITCH_STAGE_COUNT = 4;

/** 多账号的界面状态：已保存账号列表、切换器 / 重新登录面板开关、切换进行中的遮罩。 */
interface AccountSwitchUiState {
  /** 切换 / 添加账号进行中：外壳据此盖一层「正在切换…」遮罩，挡住误触。 */
  switching: boolean;
  /** 遮罩上显示的目标账号名（添加账号等没有名字时为空）。 */
  targetLabel: string | null;
  /** 当前进行到第几步（0 起）；等于 SWITCH_STAGE_COUNT 表示全部完成。 */
  stage: number;
  accounts: VaultEntry[];
  accountsLoaded: boolean;
  /** 当前 API 主机（规范化后），列表按它判断账号是不是同一个服务器签发的。 */
  apiHost: string;
  switcherVisible: boolean;
  reloginEntry: VaultEntry | null;
  setAccounts: (accounts: VaultEntry[], apiHost?: string) => void;
  setSwitching: (switching: boolean, targetLabel?: string | null) => void;
  setStage: (stage: number) => void;
  openSwitcher: () => void;
  closeSwitcher: () => void;
  openRelogin: (entry: VaultEntry) => void;
  closeRelogin: () => void;
  reset: () => void;
}

export const useAccountSwitchStore = create<AccountSwitchUiState>((set) => ({
  switching: false,
  targetLabel: null,
  stage: 0,
  accounts: [],
  accountsLoaded: false,
  apiHost: "",
  switcherVisible: false,
  reloginEntry: null,
  setAccounts: (accounts, apiHost) =>
    set((state) => ({ accounts, accountsLoaded: true, apiHost: apiHost ?? state.apiHost })),
  setSwitching: (switching, targetLabel = null) =>
    set({ switching, targetLabel: switching ? targetLabel : null, stage: 0 }),
  // 只增不减：流程里的几个钩子可能重复触发，进度不能倒退。
  setStage: (stage) => set((state) => (stage > state.stage ? { stage } : state)),
  openSwitcher: () => set({ switcherVisible: true }),
  closeSwitcher: () => set({ switcherVisible: false }),
  // 打开重新登录面板时收起切换器，两层弹层不叠放。
  openRelogin: (entry) => set({ reloginEntry: entry, switcherVisible: false }),
  closeRelogin: () => set({ reloginEntry: null }),
  reset: () =>
    set({
      switching: false,
      targetLabel: null,
      stage: 0,
      accounts: [],
      accountsLoaded: false,
      switcherVisible: false,
      reloginEntry: null,
    }),
}));
