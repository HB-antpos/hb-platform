// push 模块测试共用的假实现（只给 *.test.ts 引用，不会被业务代码引入）。
import type { PushDevicePayload } from "./push-api-core";
import { createPushRegistrationState, type PushRegistrationDeps } from "./push-registration-core";
import type { PushNative, PushPermissionStatus, PushResponse, PushSessionSnapshot } from "./push-types";

export const TEST_TOKEN = "ExponentPushToken[abcdEFGH0123456789]";
export const ACCOUNT_A = "aaaaaaaa-0000-0000-0000-000000000001";
export const ACCOUNT_B = "bbbbbbbb-0000-0000-0000-000000000002";

export interface FakeNativeOptions {
  isPhysicalDevice?: boolean;
  permission?: PushPermissionStatus;
  /** 弹窗后的授权结果。 */
  permissionAfterRequest?: PushPermissionStatus;
  /** 取令牌：返回令牌 / 抛错 / 永不返回。 */
  token?: string | Error | "hang";
  channelError?: Error;
  permissionError?: Error;
}

export function createFakeNative(options: FakeNativeOptions = {}) {
  const calls: string[] = [];
  const projectIds: string[] = [];
  const state = { permission: options.permission ?? "granted", token: options.token ?? TEST_TOKEN };
  const native: PushNative = {
    isPhysicalDevice: options.isPhysicalDevice ?? true,
    async ensureAndroidChannel() {
      calls.push("channel");
      if (options.channelError) throw options.channelError;
    },
    async getPermissionStatus() {
      calls.push("getPermission");
      if (options.permissionError) throw options.permissionError;
      return state.permission;
    },
    async requestPermission() {
      calls.push("requestPermission");
      state.permission = options.permissionAfterRequest ?? "granted";
      return state.permission;
    },
    async getExpoPushToken(projectId) {
      calls.push("getToken");
      projectIds.push(projectId);
      const token = state.token;
      if (token === "hang") return new Promise<string>(() => undefined);
      if (token instanceof Error) throw token;
      return token;
    },
    installForegroundHandler() {
      calls.push("installForegroundHandler");
    },
    addResponseListener() {
      calls.push("addResponseListener");
      return { remove() {} };
    },
    getLastResponse() {
      calls.push("getLastResponse");
      return null;
    },
    clearLastResponse() {
      calls.push("clearLastResponse");
    },
  };
  return { native, calls, projectIds, state };
}

export interface HarnessOptions extends FakeNativeOptions {
  session?: PushSessionSnapshot;
  platform?: "ios" | "android" | null;
  projectId?: string | null;
  /** loadNative 的行为：默认返回假原生；"missing" 返回 null；"throw" 抛错。 */
  load?: "ok" | "missing" | "throw";
  upload?: (payload: PushDevicePayload, accountGuid: string) => Promise<void>;
  tokenTimeoutMs?: number;
  uploadTimeoutMs?: number;
}

/** 注册核心的完整测试夹具：可变会话、可前进的时钟、全部调用的记录。 */
export function createRegistrationHarness(options: HarnessOptions = {}) {
  const fake = createFakeNative(options);
  const clock = { nowMs: 1_700_000_000_000 };
  const session: { current: PushSessionSnapshot } = { current: options.session ?? { iosReview: false, accountGuid: ACCOUNT_A } };
  const counters = { loadNative: 0, uploads: [] as { payload: PushDevicePayload; accountGuid: string }[] };
  const state = createPushRegistrationState();
  const deps: PushRegistrationDeps = {
    state,
    platform: options.platform === undefined ? "android" : options.platform,
    projectId: options.projectId === undefined ? "project-1" : options.projectId,
    now: () => clock.nowMs,
    getSession: () => session.current,
    async loadNative() {
      counters.loadNative += 1;
      if (options.load === "missing") return null;
      if (options.load === "throw") throw new Error("boom");
      return fake.native;
    },
    async uploadToken(payload, accountGuid) {
      counters.uploads.push({ payload, accountGuid });
      await options.upload?.(payload, accountGuid);
    },
    getAppInfo: () => ({ appVersion: "1.0.11", runtimeVersion: "1.0.11" }),
    tokenTimeoutMs: options.tokenTimeoutMs ?? 50,
    uploadTimeoutMs: options.uploadTimeoutMs ?? 50,
  };
  return { deps, state, fake, clock, session, counters };
}

export function makeResponse(overrides: Partial<PushResponse> = {}): PushResponse {
  return { id: "req-1", isDefaultAction: true, type: "face.unwelcome-hit", data: { notificationId: "n1", type: "face.unwelcome-hit" }, ...overrides };
}
