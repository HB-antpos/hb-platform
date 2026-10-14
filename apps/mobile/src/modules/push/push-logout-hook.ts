// 退出登录时的「注销推送设备」钩子注册表。
//
// auth-store 只依赖这个极小的文件，推送运行时（依赖 auth-store 读取会话）在加载时把真正的注销函数注册进来，
// 避免 auth-store ↔ 推送运行时的循环依赖。没有注册（未进入过主界面、旧包、审核态）时什么都不做。
type PushLogoutHook = () => Promise<unknown>;

let hook: PushLogoutHook | null = null;

export function registerPushLogoutHook(next: PushLogoutHook | null): void {
  hook = next;
}

/** 执行注销钩子。永远不抛错：注销推送是尽力而为，不能影响退出登录。耗时上限由钩子自己保证（3 秒）。 */
export async function runPushLogoutHook(): Promise<void> {
  const current = hook;
  if (!current) return;
  try {
    await current();
  } catch {
    // 吞掉：退出登录必须继续
  }
}
