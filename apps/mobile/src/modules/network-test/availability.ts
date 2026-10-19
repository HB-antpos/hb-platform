import { isIosReviewBuildEnabled } from "@/modules/ios-review/config";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { getIosReviewBuildContext, useAuthStore } from "@/store/auth-store";

/**
 * 网络测试会对外网、服务器、COS 发真实请求，且不经过审核离线拦截（直接用全局 fetch），
 * 所以 iOS 审核会话中、以及审核构建在提交登录前的离线守卫阶段，入口隐藏、测试页也不发请求。
 */
export function useNetworkTestAvailable(): boolean {
  const offlineGuardActive = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  if (isIosReviewSessionActive()) return false;
  return !(offlineGuardActive && isIosReviewBuildEnabled(getIosReviewBuildContext()));
}
