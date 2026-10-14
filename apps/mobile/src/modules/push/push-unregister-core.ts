// 退出登录时注销本机推送令牌（尽力而为）。依赖全部由外部注入，不依赖 React Native。
//
// 硬性约束：
// 1. 绝不能阻塞或拖慢退出登录：整个注销（含等待进行中的注册、必要时补取令牌、发 DELETE）最多 3 秒，
//    超时立刻放弃并中止请求；任何失败（网络、404、后端未上线、原生模块缺失）一律吞掉，永不抛错；
// 2. 只有「内存里已有令牌」或「权限已授予且能取到令牌」才会发 DELETE；没有令牌就什么都不做（也不弹任何授权框）；
// 3. iOS 审核态零网络请求；令牌只放请求体；不写日志、不落盘。
//
// 说明：本函数无法覆盖「会话被服务端判失效（401）后本地被动清会话」的路径——那时已没有可用的访问令牌，
// 令牌留在服务端，直到同一台设备下次登录（后端会把同一令牌转给新账号）。
import { checkUnavailableGate, type PushRegistrationState } from "./push-registration-core";
import { isExpoPushToken, type PushNative, type PushSessionSnapshot } from "./push-types";

/** 注销的总时限。 */
export const PUSH_UNREGISTER_TIMEOUT_MS = 3000;

export type PushUnregisterResult =
  | { status: "unregistered" }
  | { status: "skipped"; reason: "ios-review" | "not-signed-in" | "no-token" }
  | { status: "failed"; reason: "timeout" | "request-failed" };

export interface PushUnregisterDeps {
  state: PushRegistrationState;
  projectId: string | null;
  now(): number;
  getSession(): PushSessionSnapshot;
  loadNative(): Promise<PushNative | null>;
  /** 发 DELETE；signal 在超时时被中止。失败抛出即可。 */
  deleteToken(token: string, accountGuid: string, signal: AbortSignal): Promise<void>;
  /** 总时限，默认 3 秒；测试里缩短。 */
  timeoutMs?: number;
}

export async function unregisterPushToken({ deps }: { deps: PushUnregisterDeps }): Promise<PushUnregisterResult> {
  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<"timeout">((resolve) => {
    timer = setTimeout(() => {
      controller.abort();
      resolve("timeout");
    }, deps.timeoutMs ?? PUSH_UNREGISTER_TIMEOUT_MS);
  });
  // work 在超时后可能继续跑一小段，它的结果与异常都在这里被消化，不会变成未处理的 rejection。
  const work: Promise<PushUnregisterResult> = run(deps, controller.signal).catch(
    (): PushUnregisterResult => ({ status: "failed", reason: "request-failed" }),
  );
  try {
    const outcome = await Promise.race([work, deadline]);
    return outcome === "timeout" ? { status: "failed", reason: "timeout" } : outcome;
  } finally {
    if (timer) clearTimeout(timer);
    // 无论成败都丢弃本进程的上报记录：下一个登录的账号要重新上报（后端会把同一令牌转给它）。
    deps.state.epoch += 1;
    deps.state.uploaded = null;
    deps.state.held = null;
  }
}

async function run(deps: PushUnregisterDeps, signal: AbortSignal): Promise<PushUnregisterResult> {
  const { state } = deps;
  const session = deps.getSession();
  if (session.iosReview) return { status: "skipped", reason: "ios-review" };
  const accountGuid = session.accountGuid;
  if (!accountGuid) return { status: "skipped", reason: "not-signed-in" };

  // 进行中的注册先落定：保证服务端先收到 POST 再收到 DELETE，不会出现「注销后又被注册上」。
  // 这里的等待同样受总时限约束（超时由外层放弃）。
  if (state.inFlight) await state.inFlight.catch(() => undefined);
  if (signal.aborted) return { status: "failed", reason: "timeout" };

  let token = state.held?.accountGuid === accountGuid ? state.held.token : null;
  if (!token) token = await fetchGrantedToken(deps, signal);
  if (!token) return { status: "skipped", reason: "no-token" };
  if (signal.aborted) return { status: "failed", reason: "timeout" };

  await deps.deleteToken(token, accountGuid, signal);
  return { status: "unregistered" };
}

/** 内存里没有令牌时的补救：仅当权限早已授予、真机、没有被「取令牌失败」闸门拦住时才取；绝不弹授权框。 */
async function fetchGrantedToken(deps: PushUnregisterDeps, signal: AbortSignal): Promise<string | null> {
  if (checkUnavailableGate(deps.state, deps.now())) return null;
  if (!deps.projectId) return null;
  const native = await deps.loadNative();
  if (!native || !native.isPhysicalDevice || signal.aborted) return null;
  if ((await native.getPermissionStatus()) !== "granted" || signal.aborted) return null;
  try {
    const token = await native.getExpoPushToken(deps.projectId);
    return isExpoPushToken(token) ? token : null;
  } catch {
    return null;
  }
}
