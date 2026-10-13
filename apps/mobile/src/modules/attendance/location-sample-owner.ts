import { useAuthStore } from "@/store/auth-store";
import { SecureStorage } from "@/shared/storage/secure";
import { normalizeLocationOwnerId } from "@/modules/attendance/location-sample-queue";

type UserIdentity = { userGUID?: string; userGuid?: string } | null | undefined;

function identityOf(user: UserIdentity) {
  return normalizeLocationOwnerId(user?.userGUID || user?.userGuid);
}

/**
 * 取“当前登录用户”的稳定标识（userGUID，统一转小写），供定位队列做归属绑定：
 * 1. 优先取 auth-store 内存里已登录的用户（与 refreshCurrentUser 判断同一账号的口径一致：userGUID || userGuid）；
 * 2. 后台任务在无界面的 headless 环境里运行时 auth-store 可能还没 hydrate，
 *    此时回退到 SecureStore 里持久化的当前用户（登录时写入、登出/会话失效时 clearAll 清除）。
 * 没有登录用户返回 undefined。
 */
export async function resolveCurrentLocationOwnerId(): Promise<string | undefined> {
  const state = useAuthStore.getState();
  if (state.isAuthenticated) {
    const fromStore = identityOf(state.user);
    if (fromStore) {
      return fromStore;
    }
  }
  try {
    return identityOf(await SecureStorage.getUser<UserIdentity>());
  } catch {
    return undefined;
  }
}
