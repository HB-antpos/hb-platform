// 挂在已登录的 shell 布局里的薄钩子：把「会话阶段」同步给推送运行时，并在进入主界面 / 回到前台时尝试注册推送令牌。
// 全部逻辑在 push-runtime.ts 与各 core 文件里；这里只负责在合适的时机调用，且所有调用都是即发即弃、永不抛错。
import { useEffect } from "react";
import { AppState } from "react-native";
import { resolvePushSessionPhase } from "./push-events-core";
import { registerPushForCurrentSession, setPushSessionPhase, startPushEvents } from "./push-runtime";

export interface UsePushRegistrationInput {
  /** 个人账号会话（sessionKind === "account"）、已登录、非 iOS 审核态。 */
  eligible: boolean;
  /** 主界面已就绪（菜单加载完成、不在改密 / 恢复会话流程中），此时才允许导航和注册。 */
  ready: boolean;
  /** 账号变化时重新尝试。 */
  userGuid: string | null | undefined;
}

export function usePushRegistration({ eligible, ready, userGuid }: UsePushRegistrationInput) {
  const phase = resolvePushSessionPhase({ eligible, ready });

  useEffect(() => {
    setPushSessionPhase(phase);
  }, [phase]);

  // 离开主界面（登出 / 被踢回登录页）时回到未登录阶段：之后到达的点击会被丢弃，不暂存。
  useEffect(() => () => setPushSessionPhase("signed-out"), []);

  useEffect(() => {
    if (phase !== "ready") return;
    void startPushEvents();
    if (AppState.currentState === "active") void registerPushForCurrentSession();
    // 回到前台时重新尝试；去重、节流、取令牌失败后的 1 小时间隔都在 registerPushToken 里。
    const subscription = AppState.addEventListener("change", (next) => {
      if (next === "active") void registerPushForCurrentSession();
    });
    return () => subscription.remove();
  }, [phase, userGuid]);
}
