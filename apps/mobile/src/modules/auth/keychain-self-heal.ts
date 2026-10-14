/**
 * 登录前自动修复本机 Keychain。
 *
 * 背景：iOS 卸载 App 后 Keychain 仍保留，App 又没有「首次安装清理」；个别设备上残留/异常的
 * 凭据会让登录在「服务端已成功、本机写令牌失败」处中断，换账号也一样。
 * 做法：登录联网前先自检；异常、或上次登录在 Keychain 步骤失败（留有标记）时，先强制清理再复检。
 * 只清账号会话项，不碰设备绑定与设备账号凭据。自检失败不拦登录，只把结果交给调用方记诊断。
 */
import { LoginStepTimeoutError, type LoginStage } from "./login-diagnostics";

/** 标记存 AsyncStorage（不走 Keychain）：Keychain 坏了标记也要能写进去。 */
export const KEYCHAIN_DIRTY_STORAGE_KEY = "hb.auth.keychainDirty.v1";

export type KeychainSelfHealOutcome = "healthy" | "repaired" | "unrepaired";

export type KeychainSelfHealDeps = {
  isDirty: () => Promise<boolean>;
  markDirty: () => Promise<void>;
  clearDirty: () => Promise<void>;
  /** 写入 → 读回 → 删除自检，失败抛错 */
  probeReadWrite: () => Promise<void>;
  /** 读取账号会话项并返回仍存在的键名，读取本身失败会抛错 */
  listResidue: () => Promise<string[]>;
  /** 尽力逐项删除账号会话项（含自检键），不应抛错 */
  forceClearAccountSession: () => Promise<void>;
};

export async function ensureKeychainHealthy(deps: KeychainSelfHealDeps): Promise<KeychainSelfHealOutcome> {
  const dirty = await deps.isDirty().catch(() => false);

  let healthy = true;
  try {
    await deps.listResidue();
    await deps.probeReadWrite();
  } catch {
    healthy = false;
  }
  if (healthy && !dirty) return "healthy";

  await deps.forceClearAccountSession().catch(() => undefined);

  // 复检：必须能读写，且账号会话项已经删干净，才算修好。
  try {
    await deps.probeReadWrite();
    if ((await deps.listResidue()).length > 0) throw new Error("residue remains");
  } catch {
    await deps.markDirty().catch(() => undefined);
    return "unrepaired";
  }
  await deps.clearDirty().catch(() => undefined);
  return "repaired";
}

// 这三步只做 Keychain 写入，出错一定是 Keychain 的问题（currentUser / 菜单是联网步骤，sessionMarker 写的是 AsyncStorage）。
const KEYCHAIN_WRITE_STAGES: readonly LoginStage[] = ["saveAccessToken", "saveRefreshToken", "saveUser"];
// trace 的阶段是「完成后才记」：最后完成的阶段 → 下一步才是出错的那步。
const NEXT_STAGE_AFTER: Partial<Record<LoginStage, LoginStage>> = {
  loginApi: "saveAccessToken",
  saveAccessToken: "saveRefreshToken",
  currentUser: "saveUser",
};

/** 登录失败是否发生在 Keychain 写入步骤（是则下次登录前强制清理）。 */
export function isKeychainSuspectLoginFailure(lastCompletedStage: LoginStage | undefined, error: unknown) {
  const failedStage =
    error instanceof LoginStepTimeoutError
      ? error.stage
      : lastCompletedStage
        ? NEXT_STAGE_AFTER[lastCompletedStage]
        : undefined;
  return failedStage !== undefined && KEYCHAIN_WRITE_STAGES.includes(failedStage);
}
