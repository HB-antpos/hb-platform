import { createContext, useContext, type ReactNode } from "react";
import type { MobileOtaManualCheckResult } from "./mobile-ota-update";

type MobileOtaManualCheck = () => Promise<MobileOtaManualCheckResult>;

const MobileOtaManualCheckContext = createContext<MobileOtaManualCheck>(
  async () => Object.freeze({ status: "disabled" }),
);

export function useMobileOtaManualCheck() {
  return useContext(MobileOtaManualCheckContext);
}

type MobileOtaUpdateBoundaryProps = Readonly<{
  onManualCheck: MobileOtaManualCheck;
  children: ReactNode;
}>;

/**
 * OTA 更新一律在后台静默进行：检查、下载、失败、断网都不渲染任何占屏或拦截界面，
 * 业务页面始终直接可用；下载完成后由 hook 弹一次「重启应用」提醒。
 * 本组件只把设置页「检查更新」入口通过 context 交给页面使用。
 */
export function MobileOtaUpdateBoundary({ onManualCheck, children }: MobileOtaUpdateBoundaryProps) {
  return (
    <MobileOtaManualCheckContext.Provider value={onManualCheck}>
      {children}
    </MobileOtaManualCheckContext.Provider>
  );
}
