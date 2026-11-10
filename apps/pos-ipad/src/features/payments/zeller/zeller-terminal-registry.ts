import type { ZellerProbeTerminal } from "@/features/zeller-probe/zeller-probe-runner";

/**
 * Zeller 终端登记处：界面层把 SDK 客户端登记进来，运行时里的收款适配器从这里取。
 *
 * 中文注释：Zeller SDK 只能在 React 组件树里取得（它的刷卡窗口由 SDK 自己渲染），而收款适配器是运行时里的普通对象，
 * 两边不能互相引用。这里是唯一的交接点：界面层 SDK 就绪时登记，卸载时注销；适配器每次用之前现取，
 * 取不到就说明这台 iPad 此刻不能向 Zeller 发请求，由适配器按「请求没有发出」处理。
 */
export type ZellerTerminalSource = Readonly<{
  /** 当前可用的 Zeller 终端；SDK 没有加载、还没就绪或已卸载时为 null。 */
  current(): ZellerProbeTerminal | null;
}>;

export class ZellerTerminalRegistry implements ZellerTerminalSource {
  private terminal: ZellerProbeTerminal | null = null;

  public current(): ZellerProbeTerminal | null {
    return this.terminal;
  }

  /**
   * 登记终端，返回注销函数。只注销自己登记的那一个：
   * 页面切换时新旧两个界面可能短暂并存，旧界面卸载不能把新界面刚登记的终端清掉。
   */
  public register(terminal: ZellerProbeTerminal): () => void {
    this.terminal = terminal;
    return () => {
      if (this.terminal === terminal) this.terminal = null;
    };
  }
}

/** 进程内唯一的登记处：界面层与运行时共用。 */
export const zellerTerminalRegistry = new ZellerTerminalRegistry();
