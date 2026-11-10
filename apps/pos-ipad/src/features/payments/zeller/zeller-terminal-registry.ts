import type { ZellerProbeTerminal } from "@/features/zeller-probe/zeller-probe-runner";

/**
 * Zeller 终端登记处：界面层把 SDK 客户端登记进来，运行时里的收款适配器从这里取。
 *
 * 中文注释：Zeller SDK 只能在 React 组件树里取得（它的刷卡窗口由 SDK 自己渲染），而收款适配器是运行时里的普通对象，
 * 两边不能互相引用。这里是唯一的交接点，方向有两个：
 * - 界面 → 运行时：SDK 就绪时登记终端，卸载时注销；适配器每次用之前现取，取不到就按「请求没有发出」处理。
 * - 运行时 → 界面：SDK 默认不加载（它的入口会替换全局 URL，还会触碰 5 个原生库）。运行时发现需要 Zeller
 *   （设置里启用了 Zeller，或有 Zeller 的付款要核实）时调用 request()，界面层订阅到之后才去加载 SDK。
 */
export type ZellerTerminalSource = Readonly<{
  /** 当前可用的 Zeller 终端；SDK 没有加载、还没就绪或已卸载时为 null。 */
  current(): ZellerProbeTerminal | null;
  /** 告诉界面层「需要 Zeller 终端」，让它开始加载 SDK。重复调用无副作用；不保证立刻可用。 */
  request?(): void;
  /**
   * 扣款时是否隐藏 SDK 自带的全屏窗口（设置里的开关）。缺省视为 false。
   * 只对扣款有效：SDK 的退款没有这个选项，配对必须弹窗。
   */
  hideSdkUi?(): boolean;
}>;

export class ZellerTerminalRegistry implements ZellerTerminalSource {
  private terminal: ZellerProbeTerminal | null = null;
  private wanted = false;
  private readonly listeners = new Set<() => void>();

  public current(): ZellerProbeTerminal | null {
    return this.terminal;
  }

  public request(): void {
    if (this.wanted) return;
    this.wanted = true;
    for (const listener of [...this.listeners]) listener();
  }

  /** 运行时是否已经提出过需要 Zeller 终端。提出后本次进程内不再撤回。 */
  public isWanted = (): boolean => this.wanted;

  private hideSdkUiValue = false;

  /** 扣款时是否隐藏 SDK 窗口；由界面层按本机设置写入，适配器每次扣款前现读。 */
  public hideSdkUi = (): boolean => this.hideSdkUiValue;

  public setHideSdkUi(hidden: boolean): void {
    this.hideSdkUiValue = hidden;
  }

  /** 订阅「需要 Zeller 终端」的变化；返回取消订阅函数。签名与 useSyncExternalStore 兼容。 */
  public subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

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
