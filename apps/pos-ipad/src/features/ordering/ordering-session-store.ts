import { create } from "zustand";

import type { OrderingUser } from "./ordering-auth";

/**
 * 订货模式当前登录的员工（只在内存里）。
 *
 * 中文注释：故意不落盘——App 被划掉或重启后必须重新登录，共用的 iPad 上不会一直挂着某个员工的账号。
 * 收银员登录状态在 cashier-login-store，两边互不影响。
 */
type OrderingSessionState = Readonly<{
  user: OrderingUser | null;
  signIn(user: OrderingUser): void;
  signOut(): void;
}>;

export const useOrderingSessionStore = create<OrderingSessionState>((set) => ({
  user: null,
  signIn: (user) => set({ user }),
  signOut: () => set({ user: null }),
}));
