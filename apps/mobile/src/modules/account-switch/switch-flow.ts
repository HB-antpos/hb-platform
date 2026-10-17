// 账号切换 / 添加账号 / 退出的流水线（纯逻辑，所有副作用都由 deps 注入，便于单测）。
//
// 核心顺序（切换到已保存账号）：
//   拦截检查 → 关闸门 → 刷新目标令牌 → 立刻把新令牌写回保险库 → 用新令牌核对身份 →
//   处理当前账号（已保存则回写最新令牌 / 要求保存则入库）→ 通知推送离开 → 激活新会话 → 放闸门 → 善后（吊销未保存的旧会话）。
// 刷新令牌每次使用都会轮转（旧的立刻作废），所以「刷新成功 → 立刻落盘」之间不能有任何可能中断的步骤。
//
// 注意：本流程里对目标账号的请求（刷新、核对身份）必须走「不经过 apiClient 拦截器」的裸请求，
// 否则会被自己关上的闸门挡住而死锁，或者误触全局的 401 处理把当前账号登出。

import {
  VAULT_MAX_ENTRIES,
  buildEntryFromUser,
  describeEntry,
  findEntry,
  normalizeGuid,
  pickNextUsableEntry,
  withFreshToken,
  type EntrySourceUser,
  type VaultEntry,
} from "./vault";
import type { VaultStore } from "./vault-store";

export interface CurrentSnapshot<U extends EntrySourceUser> {
  user: U;
  /** 活动槽位里当前账号最新的刷新令牌（可能已被自动刷新轮转过，比保险库里的新）。 */
  refreshToken: string;
  /** 只有 "account"（个人账号登录）才允许使用多账号。 */
  sessionKind: string;
}

export interface SessionPayload<U extends EntrySourceUser> {
  accessToken: string;
  refreshToken: string;
  user: U;
}

export interface SwitchDeps<U extends EntrySourceUser> {
  now(): number;
  apiHost(): string;
  vault: Pick<VaultStore, "load" | "upsert" | "update" | "remove" | "markNeedsRelogin">;
  readCurrent(): Promise<CurrentSnapshot<U> | null>;
  /** 班中（已上班未下班）。 */
  isOnShift(): Promise<boolean>;
  /** 有没做完的操作（如拣货未保存），切换会丢数据。 */
  hasPendingWork(): boolean;
  /** 裸请求：用刷新令牌换新令牌。失败时抛出带 response.status 的错误。 */
  refreshWithToken(refreshToken: string): Promise<{ accessToken: string; refreshToken: string }>;
  /** 裸请求：用显式 Bearer 令牌读取该令牌对应的用户。 */
  fetchUserWithToken(accessToken: string): Promise<U>;
  /** 裸请求：用户名 + 密码登录，不碰活动槽位。 */
  loginWithCredentials(username: string, password: string): Promise<SessionPayload<U>>;
  /** 等 apiClient 里正在进行的令牌刷新结束（没有则立即返回）。 */
  settleTokenRefresh(): Promise<void>;
  /** 通知后端吊销一个刷新令牌（尽力而为，不抛错）。 */
  revokeSession(refreshToken: string): Promise<void>;
  /** 推送：让当前（旧）账号的设备注册离开；必须在激活新会话前调用，尽力而为。 */
  notifyLeaving(): Promise<void>;
  /** 把活动槽位 + 全部账号相关内存状态换成新会话；失败时自行清场并抛错。 */
  activate(session: SessionPayload<U>): Promise<void>;
  /** 无可切换账号时走原来的完整退出。 */
  fullLogout(): Promise<void>;
  gate: { begin(): void; end(options: { sessionChanged: boolean }): void };
}

export type SwitchFailure =
  | "notAccountSession" // 设备 / 审核会话不开放多账号
  | "onShift" // 班中
  | "busy" // 有没做完的操作
  | "unknownAccount" // 保险库里没有这个账号
  | "sameAccount"
  | "needsRelogin" // 令牌失效 / 账号被限制 / 过期：要重新输入密码
  | "networkError" // 网络或服务端错误：稍后重试，令牌没丢
  | "identityMismatch" // 令牌对应的人和保存的不是同一个
  | "vaultFull"
  | "activationFailed"; // 激活到一半失败：已回到登录页

export type SwitchResult = { ok: true } | { ok: false; reason: SwitchFailure };

export function getHttpStatus(error: unknown): number | undefined {
  const status = (error as { response?: { status?: unknown } } | null)?.response?.status;
  return typeof status === "number" ? status : undefined;
}

/** 服务器明确拒绝了这个令牌 / 账号（需要重新登录）；其余（超时、断网、5xx）都算可重试的网络错误。 */
export function isRejectedByServer(error: unknown): boolean {
  const status = getHttpStatus(error);
  return status === 401 || status === 403 || status === 404;
}

function userGuidOf(user: EntrySourceUser): string {
  return (user.userGUID || user.userGuid || "").trim();
}

async function preflight<U extends EntrySourceUser>(
  deps: SwitchDeps<U>
): Promise<{ ok: true; current: CurrentSnapshot<U> } | { ok: false; reason: SwitchFailure }> {
  const current = await deps.readCurrent();
  if (!current || current.sessionKind !== "account") return { ok: false, reason: "notAccountSession" };
  // 班中不能切换：对外只给最简提示，不解释原因（见 UI 文案）。
  if (await deps.isOnShift()) return { ok: false, reason: "onShift" };
  if (deps.hasPendingWork()) return { ok: false, reason: "busy" };
  return { ok: true, current };
}

type LeaveMode =
  /** 当前账号留在列表里：回写最新令牌（若没保存且要求保存则入库）。 */
  | { kind: "keep"; saveIfUnsaved: boolean }
  /** 当前账号要退出：吊销它的会话（调用方已把它从列表移除）。 */
  | { kind: "signOut" };

/**
 * 公共提交段：通知推送 → 关闸门 → 等在途刷新收尾 → 重读当前账号最新令牌并处理（回写 / 入库）→ 激活 → 放闸门 → 善后。
 * 闸门只包住「写活动槽位」这一小段：目标账号的刷新 / 核对走裸请求，不需要闸门；
 * 推送注销要走 apiClient（带旧账号令牌），必须在闸门关闭之前完成，否则会被自己的闸门挡住。
 */
async function commit<U extends EntrySourceUser>(
  deps: SwitchDeps<U>,
  startedWith: CurrentSnapshot<U>,
  next: SessionPayload<U>,
  leave: LeaveMode
): Promise<SwitchResult> {
  const apiHost = deps.apiHost();

  await deps.notifyLeaving().catch(() => undefined);

  deps.gate.begin();
  // 等在途的令牌刷新收尾：刷新会轮转刷新令牌，必须读到「最新那份」再回写，否则保险库里留下的是已作废的旧令牌。
  await deps.settleTokenRefresh().catch(() => undefined);
  const current = (await deps.readCurrent()) ?? null;
  if (
    !current ||
    current.sessionKind !== "account" ||
    normalizeGuid(userGuidOf(current.user)) !== normalizeGuid(userGuidOf(startedWith.user))
  ) {
    // 准备期间当前会话已经变了（被登出 / 换人）：不再往下做，避免把别人的令牌写进保险库。
    deps.gate.end({ sessionChanged: false });
    return { ok: false, reason: "notAccountSession" };
  }

  const now = deps.now();
  let revokeOld = leave.kind === "signOut";
  if (leave.kind === "keep") {
    const saved = findEntry(await deps.vault.load(), userGuidOf(current.user));
    if (saved) {
      // 已保存：把它最新的刷新令牌写回。
      await deps.vault.update(saved.userGuid, (entry) =>
        buildEntryFromUser(current.user, current.refreshToken, apiHost, now, entry)
      );
    } else if (leave.saveIfUnsaved) {
      const status = await deps.vault.upsert(buildEntryFromUser(current.user, current.refreshToken, apiHost, now));
      if (status === "full") {
        deps.gate.end({ sessionChanged: false });
        return { ok: false, reason: "vaultFull" };
      }
    } else {
      // 未保存且用户选择不保存：切走后这个会话就没人用了，需要吊销。
      revokeOld = true;
    }
  }

  try {
    await deps.activate(next);
  } catch {
    // activate 失败时已自行清场（回登录页），会话确实变了。
    deps.gate.end({ sessionChanged: true });
    return { ok: false, reason: "activationFailed" };
  }
  deps.gate.end({ sessionChanged: true });

  // 善后（激活已成功，下面任何失败都不影响结果）。
  if (revokeOld) void deps.revokeSession(current.refreshToken).catch(() => undefined);
  return { ok: true };
}

/** 切换到保险库里的某个已保存账号。 */
export async function switchToAccount<U extends EntrySourceUser>(
  deps: SwitchDeps<U>,
  targetGuid: string,
  options: { saveCurrent?: boolean } = {}
): Promise<SwitchResult> {
  const pre = await preflight(deps);
  if (!pre.ok) return pre;
  const { current } = pre;

  if (normalizeGuid(userGuidOf(current.user)) === normalizeGuid(targetGuid)) {
    return { ok: false, reason: "sameAccount" };
  }

  const entries = await deps.vault.load();
  const target = findEntry(entries, targetGuid);
  if (!target) return { ok: false, reason: "unknownAccount" };

  const state = describeEntry(target, deps.now(), deps.apiHost());
  if (state === "expired") {
    await deps.vault.markNeedsRelogin(target.userGuid);
    return { ok: false, reason: "needsRelogin" };
  }
  if (state !== "ok") return { ok: false, reason: "needsRelogin" };

  // 需要把未保存的当前账号存进去，但列表已满：在动任何东西之前就拒绝。
  if (
    options.saveCurrent &&
    !findEntry(entries, userGuidOf(current.user)) &&
    entries.length >= VAULT_MAX_ENTRIES
  ) {
    return { ok: false, reason: "vaultFull" };
  }

  let tokens: { accessToken: string; refreshToken: string };
  try {
    tokens = await deps.refreshWithToken(target.refreshToken);
  } catch (error) {
    if (isRejectedByServer(error)) {
      await deps.vault.markNeedsRelogin(target.userGuid);
      return { ok: false, reason: "needsRelogin" };
    }
    return { ok: false, reason: "networkError" };
  }

  // 旧令牌此刻已作废：马上把新令牌落盘，之后无论哪一步失败都还能重试。
  await deps.vault
    .update(target.userGuid, (entry) => withFreshToken(entry, tokens.refreshToken, deps.now()))
    .catch(() => false);

  let user: U;
  try {
    user = await deps.fetchUserWithToken(tokens.accessToken);
  } catch (error) {
    if (isRejectedByServer(error)) {
      await deps.vault.markNeedsRelogin(target.userGuid);
      return { ok: false, reason: "needsRelogin" };
    }
    return { ok: false, reason: "networkError" };
  }

  if (normalizeGuid(userGuidOf(user)) !== normalizeGuid(target.userGuid)) {
    await deps.vault.markNeedsRelogin(target.userGuid);
    return { ok: false, reason: "identityMismatch" };
  }

  const result = await commit(
    deps,
    current,
    { accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, user },
    { kind: "keep", saveIfUnsaved: options.saveCurrent === true }
  );
  if (!result.ok) return result;

  // 刷新展示信息（角色 / 分店可能变了）与最近使用时间。
  await deps.vault
    .update(target.userGuid, (entry) => ({
      ...buildEntryFromUser(user, tokens.refreshToken, deps.apiHost(), deps.now(), entry),
      lastUsedAt: deps.now(),
    }))
    .catch(() => false);
  return { ok: true };
}

export interface AddAccountInput {
  username: string;
  password: string;
  /** 登录成功后是否把新账号存进切换列表（默认不保存）。 */
  save: boolean;
  /** 当前账号没保存时，是否顺手存进列表（添加页上的黄色提示对应的选择）。 */
  saveCurrent?: boolean;
}

export type AddAccountResult = SwitchResult | { ok: false; reason: "loginFailed"; error: unknown };

/** 添加账号：先用裸请求登录新账号，成功后才动当前账号。 */
export async function addAccount<U extends EntrySourceUser>(
  deps: SwitchDeps<U>,
  input: AddAccountInput
): Promise<AddAccountResult> {
  const pre = await preflight(deps);
  if (!pre.ok) return pre;
  const { current } = pre;

  const entries = await deps.vault.load();
  const currentNeedsSlot = input.saveCurrent === true && !findEntry(entries, userGuidOf(current.user));
  // 当前账号要入库却没位置：在登录新账号之前就拒绝，不白白多开一个会话。
  if (currentNeedsSlot && entries.length >= VAULT_MAX_ENTRIES) return { ok: false, reason: "vaultFull" };

  let session: SessionPayload<U>;
  try {
    session = await deps.loginWithCredentials(input.username, input.password);
  } catch (error) {
    return { ok: false, reason: "loginFailed", error };
  }

  if (normalizeGuid(userGuidOf(session.user)) === normalizeGuid(userGuidOf(current.user))) {
    // 登录的就是当前账号：多出来的这个会话没用，吊销掉。
    void deps.revokeSession(session.refreshToken).catch(() => undefined);
    return { ok: false, reason: "sameAccount" };
  }

  // 新账号要入库（勾选了保存且它还不在列表里）所需的位置，连同当前账号一起核算。
  const newGuid = userGuidOf(session.user);
  const newAlreadySaved = Boolean(findEntry(entries, newGuid));
  const slotsNeeded = (currentNeedsSlot ? 1 : 0) + (input.save && !newAlreadySaved ? 1 : 0);
  if (entries.length + slotsNeeded > VAULT_MAX_ENTRIES) {
    void deps.revokeSession(session.refreshToken).catch(() => undefined);
    return { ok: false, reason: "vaultFull" };
  }

  const result = await commit(deps, current, session, {
    kind: "keep",
    saveIfUnsaved: input.saveCurrent === true,
  });
  if (!result.ok) {
    // 新会话没能激活：它是白开的，吊销掉。
    void deps.revokeSession(session.refreshToken).catch(() => undefined);
    return result;
  }

  // 新账号：勾选了保存，或本来就在列表里，就写入 / 更新保险库。
  if (input.save || newAlreadySaved) {
    await deps.vault
      .upsert(buildEntryFromUser(session.user, session.refreshToken, deps.apiHost(), deps.now()))
      .catch(() => undefined);
  }
  return { ok: true };
}

/** 把当前（未保存的）账号存进切换列表：用活动槽位里的令牌，不需要再输密码。 */
export async function saveCurrentAccount<U extends EntrySourceUser>(
  deps: SwitchDeps<U>
): Promise<{ ok: true } | { ok: false; reason: "notAccountSession" | "vaultFull" }> {
  const current = await deps.readCurrent();
  if (!current || current.sessionKind !== "account") return { ok: false, reason: "notAccountSession" };
  const status = await deps.vault.upsert(
    buildEntryFromUser(current.user, current.refreshToken, deps.apiHost(), deps.now())
  );
  return status === "full" ? { ok: false, reason: "vaultFull" } : { ok: true };
}

/** 从列表里移除一个已保存的账号（不影响它在服务器上的会话；当前账号不能这样移除）。 */
export async function forgetSavedAccount<U extends EntrySourceUser>(
  deps: SwitchDeps<U>,
  userGuid: string
): Promise<void> {
  const entries = await deps.vault.load();
  const target = findEntry(entries, userGuid);
  if (!target) return;
  await deps.vault.remove(target.userGuid);
  // 它保存的会话不会再被使用，顺手让服务器作废（尽力而为）。
  void deps.revokeSession(target.refreshToken).catch(() => undefined);
}

export type SignOutResult = { ok: true; switchedTo: VaultEntry | null } | { ok: false; reason: SwitchFailure };

/**
 * 退出当前账号：把它从列表移除并吊销；列表里还有能直接用的账号就切过去，否则走完整退出（回登录页）。
 * 有下一个账号时沿用切换的拦截规则（班中等不能切）；没有下一个账号时不拦截，行为和以前的退出一致。
 */
export async function signOutCurrentAccount<U extends EntrySourceUser>(deps: SwitchDeps<U>): Promise<SignOutResult> {
  const current = await deps.readCurrent();
  if (!current || current.sessionKind !== "account") {
    await deps.fullLogout();
    return { ok: true, switchedTo: null };
  }

  const entries = await deps.vault.load();
  const next = pickNextUsableEntry(entries, userGuidOf(current.user), deps.now(), deps.apiHost());
  if (!next) {
    await deps.vault.remove(userGuidOf(current.user)).catch(() => undefined);
    await deps.fullLogout();
    return { ok: true, switchedTo: null };
  }

  const pre = await preflight(deps);
  if (!pre.ok) return pre;

  let tokens: { accessToken: string; refreshToken: string };
  let user: U;
  try {
    tokens = await deps.refreshWithToken(next.refreshToken);
    await deps.vault
      .update(next.userGuid, (entry) => withFreshToken(entry, tokens.refreshToken, deps.now()))
      .catch(() => false);
    user = await deps.fetchUserWithToken(tokens.accessToken);
    if (normalizeGuid(userGuidOf(user)) !== normalizeGuid(next.userGuid)) throw new Error("identity mismatch");
  } catch (error) {
    if (isRejectedByServer(error)) await deps.vault.markNeedsRelogin(next.userGuid);
    // 下一个账号起不来：用户要的是退出当前账号，照样退出（回登录页），不把人卡在原账号。
    await deps.vault.remove(userGuidOf(current.user)).catch(() => undefined);
    await deps.fullLogout();
    return { ok: true, switchedTo: null };
  }

  await deps.vault.remove(userGuidOf(current.user)).catch(() => undefined);
  const result = await commit(
    deps,
    current,
    { accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, user },
    { kind: "signOut" }
  );
  if (!result.ok) return result;
  await deps.vault
    .update(next.userGuid, (entry) => ({
      ...buildEntryFromUser(user, tokens.refreshToken, deps.apiHost(), deps.now(), entry),
      lastUsedAt: deps.now(),
    }))
    .catch(() => false);
  return { ok: true, switchedTo: next };
}
