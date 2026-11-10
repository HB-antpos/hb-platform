import { useCallback, useEffect, useMemo, useState } from "react";

import {
  zellerTerminalRegistry,
  type ZellerTerminalRegistry,
} from "@/features/payments/zeller/zeller-terminal-registry";
import {
  readZellerHideSdkUi,
  saveZellerHideSdkUi,
} from "@/ui/preferences/terminal-ui-preferences";

/** 设置页开关的取值与修改入口。 */
export type ZellerHideSdkUiControl = Readonly<{
  /** true：扣款时不弹出 Zeller SDK 的全屏窗口。 */
  value: boolean;
  onChange(value: boolean): void;
}>;

/**
 * 「收款时不弹出 Zeller 窗口」开关：本机设置，默认关闭。
 *
 * 中文注释：
 * 1. 启动时同步读本机保存的值，并写进终端登记处；收款适配器每次扣款前从登记处现读，
 *    所以改完开关马上对下一笔扣款生效，不需要重启。
 * 2. 切换时先写内存（登记处）再异步落盘：落盘失败不回滚当前选择，也不影响收银。
 * 3. 只影响扣款。SDK 的退款没有隐藏窗口的选项，配对必须弹窗。
 */
export function useZellerHideSdkUiControl(
  registry: Pick<ZellerTerminalRegistry, "setHideSdkUi"> = zellerTerminalRegistry,
): ZellerHideSdkUiControl {
  const [hidden, setHidden] = useState(readZellerHideSdkUi);

  // 首次渲染后把本机保存的值交给运行时（登记处默认 false）。
  useEffect(() => {
    registry.setHideSdkUi(hidden);
  }, [hidden, registry]);

  const onChange = useCallback(
    (next: boolean) => {
      setHidden(next);
      registry.setHideSdkUi(next);
      void saveZellerHideSdkUi(next);
    },
    [registry],
  );

  return useMemo(() => ({ value: hidden, onChange }), [hidden, onChange]);
}
