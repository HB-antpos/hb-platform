import { useEffect } from "react";
import { AppState } from "react-native";
import { useAuthStore } from "@/store/auth-store";
import { consumeLocationRequests, resetLocationRequestConsumer } from "./location-request-runtime";

/**
 * 位置请求待办的自动触发（非交互）：
 * - 挂载（冷启动就绪）/ 登录完成 / 账号切换时触发一次；
 * - App 每次从后台回到前台时再触发一次。
 * 另外两类触发不在这里：App 在前台时收到位置请求推送（push-runtime 的前台处理器）、
 * 员工点推送 / 点收件箱通知（交互模式，允许弹系统定位权限框）。
 *
 * 每次触发 = 拉一次待办；没有待办就什么都不做。有待办且「使用 App 期间」定位权限已授予才会取一次位置上报，
 * 权限没授予就静默跳过，自动触发绝不弹系统权限框。不请求后台权限、不启动任何后台任务。
 * 是否登录、是否个人账号、有没有 Attendance.Punch.Self、是否 iOS 审核态由消费器内部再把一道关，条件不满足根本不发请求。
 * 拉取节流（至少 15 秒）、in-flight 锁、会话内去重都在消费器里。
 *
 * 和 useLegacyLocationTrackingCleanup 一样挂在根布局：根布局在任何路由（含登录页）下都已挂载。
 * enabled 由根布局的 sideEffectsEnabled 控制（启动就绪且非 iOS 审核态守卫期）。
 */
export function useLocationRequestConsumer({ enabled }: { enabled: boolean }) {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);

  useEffect(() => {
    if (!enabled || !isAuthenticated || !userGuid) {
      // 未登录 / 已登出：丢弃上一个账号的节流与去重记录。
      resetLocationRequestConsumer();
      return;
    }

    // consumeLocationRequests 内部兜住所有错误，不会 reject。
    if (AppState.currentState === "active") {
      consumeLocationRequests({ trigger: "launch" });
    }
    const subscription = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        consumeLocationRequests({ trigger: "foreground" });
      }
    });
    return () => subscription.remove();
  }, [enabled, isAuthenticated, userGuid]);
}
