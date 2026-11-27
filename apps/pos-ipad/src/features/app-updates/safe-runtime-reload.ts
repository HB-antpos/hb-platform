/**
 * 原生包是否已带「App 内 reload 不闪退」补丁（patches/expo-modules-core+3.0.30.patch）。
 *
 * expo-modules-core 3.0.30 原版在 Updates.reloadAsync() 拆旧运行时时，
 * SharedObjectRegistry 异步析构旧运行时上的 jsi::WeakObject，必然闪退（build ≤ 22）。
 * 补丁把清空改为同步，并在 global.expo 上挂 `__hbpos_safe_runtime_reload__ = true`。
 * 标记由原生层在 JS 执行前写入，OTA 改不了它，所以旧原生包收到新 JS 也只会得到 false，
 * 继续走「上滑关闭后重开」。
 */
export const SAFE_RUNTIME_RELOAD_MARKER = "__hbpos_safe_runtime_reload__";

export function hasSafeRuntimeReload(
  globalObject: unknown = globalThis,
): boolean {
  try {
    const expo = (globalObject as { expo?: unknown } | null)?.expo;
    if (!expo || typeof expo !== "object") return false;
    return (
      (expo as Record<string, unknown>)[SAFE_RUNTIME_RELOAD_MARKER] === true
    );
  } catch {
    return false;
  }
}
