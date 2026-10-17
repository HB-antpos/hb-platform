// 账号切换闸门：切换期间暂停所有 API 请求，并用「会话代号」识别切换前发出的请求。
//
// 为什么需要：切换时活动槽位里的令牌会被覆盖，而后台轮询（未读数、心跳等）可能恰好在这时 401 并走刷新——
// 如果它读到的是半新半旧的令牌，会用旧账号的刷新令牌轮转，写出「新账号用户 + 旧账号令牌」的混合身份；
// 或者把「旧账号发出的请求」用新账号的令牌重放（旧账号的写操作变成新账号执行）。
// 本文件刻意不依赖任何别的模块（会被请求拦截器引用，避免循环依赖）。

let generation = 0;
let switching: Promise<void> | null = null;
let releaseSwitching: (() => void) | null = null;

/** 当前会话代号：每次账号会话被替换（切换 / 添加账号）加 1。 */
export function getAccountSessionGeneration(): number {
  return generation;
}

/** 开始切换：之后发起的请求会等到结束再发。重复调用无副作用。 */
export function beginAccountSwitch(): void {
  if (switching) return;
  switching = new Promise<void>((resolve) => {
    releaseSwitching = resolve;
  });
}

/**
 * 结束切换并放行被暂停的请求。会话确实被替换时先把代号 +1 再放行，
 * 这样被暂停的请求发出时盖的是新代号，不会被误当成「旧账号的请求」。
 */
export function endAccountSwitch(options: { sessionChanged: boolean }): void {
  if (options.sessionChanged) generation += 1;
  const release = releaseSwitching;
  switching = null;
  releaseSwitching = null;
  release?.();
}

export function isAccountSwitchInProgress(): boolean {
  return switching !== null;
}

/** 请求拦截器在发请求前调用：切换进行中就等它结束。 */
export async function waitForAccountSwitchIdle(): Promise<void> {
  while (switching) {
    await switching;
  }
}

/** 这个请求是不是在账号会话被替换之前发出的（没盖代号的请求视为不过期）。 */
export function isStaleSessionGeneration(stamp: number | undefined): boolean {
  return typeof stamp === "number" && stamp !== generation;
}

/** 仅供单测：重置模块级状态。 */
export function resetAccountSwitchGateForTests(): void {
  generation = 0;
  releaseSwitching?.();
  switching = null;
  releaseSwitching = null;
}
