/**
 * 登录页「服务器设置」里的手动「清除本机登录缓存」。
 *
 * 背景：iOS 的 Keychain 在卸载 App 后依然保留，而 App 没有「首次安装清理 Keychain」的逻辑，
 * 残留/异常的登录凭据只能手动清。只清账号会话（令牌、用户、收银打印记录、会话类型标记），
 * 不碰设备绑定与设备账号凭据——那些需要激活码才能重新开通。
 */
export type KeychainResetResult =
  | { status: "ok" }
  // 清理流程跑完但 Keychain 里仍读得到账号会话项
  | { status: "residual"; items: string[] }
  // 清理后 Keychain 写入/读回自检失败：设备级 Keychain 异常的直接证据
  | { status: "probeFailed"; message: string }
  | { status: "failed"; message: string };

export type KeychainResetDeps = {
  /** 与退出登录同一入口：带互斥、内存态与持久态一并清理，且保留设备绑定 */
  clearLocalSession: () => Promise<void>;
  listResidue: () => Promise<string[]>;
  probeReadWrite: () => Promise<void>;
};

function messageOf(error: unknown) {
  const text = error instanceof Error ? error.message : String(error ?? "");
  return text.trim() || "unknown error";
}

export async function resetLocalKeychainSession(deps: KeychainResetDeps): Promise<KeychainResetResult> {
  try {
    await deps.clearLocalSession();
  } catch (error) {
    return { status: "failed", message: messageOf(error) };
  }

  // 核对环节自身出错（读 Keychain 抛异常）同样说明 Keychain 不正常，按自检失败处理。
  let residue: string[];
  try {
    residue = await deps.listResidue();
  } catch (error) {
    return { status: "probeFailed", message: messageOf(error) };
  }
  if (residue.length > 0) {
    return { status: "residual", items: residue };
  }

  try {
    await deps.probeReadWrite();
  } catch (error) {
    return { status: "probeFailed", message: messageOf(error) };
  }
  return { status: "ok" };
}
