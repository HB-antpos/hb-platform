/**
 * 设置页的 Zeller 页签（配对、启用）在当前构建里是否开放。
 *
 * 中文注释：
 * 1. 非 production 构建（开发包、preview 包）一直开放。
 * 2. production 构建默认关闭：Zeller 收款还没有经过真机和刷卡机验证，不能让门店误用。
 * 3. 例外：构建时显式带 `EXPO_PUBLIC_HBPOS_ENABLE_ZELLER=1`（EAS 档位 `production-zeller`）。
 *    这个档位和 production 走同一个 TestFlight 渠道，只用于让测试员在 TestFlight 包里验证 Zeller。
 *    只认字面量 "1"，其他任何值（空、true、0）都不开放，避免手滑配错。
 *
 * 注意：Expo 只会在构建时替换 `process.env.EXPO_PUBLIC_*` 的字面量写法，
 * 所以这里不直接读 process.env，由调用方把两个字面量传进来。
 */
export function isZellerSettingsAvailable(
  input: Readonly<{
    buildProfile: string | undefined;
    enableZeller: string | undefined;
  }>,
): boolean {
  if (input.buildProfile !== "production") return true;
  return input.enableZeller?.trim() === "1";
}
