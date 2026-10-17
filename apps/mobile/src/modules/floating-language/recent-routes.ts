import { create } from "zustand";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { QUICK_JUMP_RECENT_LIMIT } from "./quick-jump";

export const QUICK_JUMP_RECENT_STORAGE_KEY = "hb.ui.quickJumpRecent.v1";

interface StoredRecent {
  scope: string;
  routes: string[];
}

interface RecentRoutesState {
  /** 记录归属：`account:<userGuid>` 或 `device:<hardwareId>`；换人后旧记录直接作废，不会把上一个人去过的页面露给下一个人。 */
  scope: string | null;
  routes: string[];
  replace: (scope: string, routes: string[]) => void;
}

/** 读回本机记录：只有归属一致才用，路由名只收字符串、去重、限量。 */
export function normalizeStoredRecent(raw: unknown, scope: string): string[] {
  const value = (raw ?? {}) as Partial<StoredRecent>;
  if (value.scope !== scope || !Array.isArray(value.routes)) {
    return [];
  }
  const unique: string[] = [];
  for (const routeName of value.routes) {
    if (typeof routeName === "string" && routeName && !unique.includes(routeName)) {
      unique.push(routeName);
    }
  }
  return unique.slice(0, QUICK_JUMP_RECENT_LIMIT);
}

export const useQuickJumpRecentStore = create<RecentRoutesState>((set) => ({
  scope: null,
  routes: [],
  replace: (scope, routes) => {
    set({ scope, routes });
    void AppAsyncStorage.setObject(QUICK_JUMP_RECENT_STORAGE_KEY, { scope, routes }).catch(() => undefined);
  },
}));

let loadingScope: string | null = null;

/** 归属变化（登录 / 换账号 / 设备模式）时读一次本机记录；读失败当作没有记录。 */
export async function loadRecentRoutesForScope(scope: string) {
  const state = useQuickJumpRecentStore.getState();
  if (state.scope === scope || loadingScope === scope) {
    return;
  }
  loadingScope = scope;
  // 先清空内存，避免读盘期间显示上一个人的记录。
  useQuickJumpRecentStore.setState({ scope, routes: [] });
  let routes: string[] = [];
  try {
    routes = normalizeStoredRecent(await AppAsyncStorage.getObject(QUICK_JUMP_RECENT_STORAGE_KEY), scope);
  } catch {
    routes = [];
  }
  if (loadingScope === scope) {
    loadingScope = null;
    // 读盘期间已经记了新的访问：合并，以内存里较新的为先。
    const current = useQuickJumpRecentStore.getState();
    if (current.scope === scope) {
      const merged = [...current.routes, ...routes.filter((name) => !current.routes.includes(name))];
      useQuickJumpRecentStore.setState({ routes: merged.slice(0, QUICK_JUMP_RECENT_LIMIT) });
    }
  }
}

/** 仅供单测：重置模块级状态。 */
export function resetQuickJumpRecentForTests() {
  loadingScope = null;
  useQuickJumpRecentStore.setState({ scope: null, routes: [] });
}
