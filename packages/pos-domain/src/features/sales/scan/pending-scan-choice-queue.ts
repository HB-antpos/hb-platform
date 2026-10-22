/** 队列上限（与 WPF PendingScanChoiceQueue.Capacity 一致）。满了拒收并明确报警，不静默丢。 */
export const PENDING_SCAN_CHOICE_CAPACITY = 20;

/**
 * 一次「一个条码对应多个商品」的扫码，等待收银员回来选定具体商品。
 * 每次扫码一个条目：同一条码连续扫两次也是两个条目（两件实物可能是不同商品，不能合并）。
 */
export type PendingScanChoice<TPayload> = Readonly<{
  /** 队列内递增编号；选定 / 放弃都按它定位，防止界面上的旧点击落到别的条目。 */
  id: number;
  /** 扫到的查询码（已去首尾空白），仅用于提示与日志。 */
  lookupCode: string;
  /** 入队时间（毫秒），用于日志里的等待时长。 */
  createdAtMs: number;
  /** 与队列里上一个条目是同一个码：提示收银员这可能是重复扫码。 */
  sameLookupCodeAsPrevious: boolean;
  payload: TPayload;
}>;

export type PendingScanChoiceQueueOptions = Readonly<{
  capacity?: number;
  now?: () => number;
}>;

/**
 * 「一码多商品」待选队列（对齐 WPF c685663c5）：先进先出，互不覆盖、互不合并。
 * 纯内存、无 UI 依赖；队列自己不决定何时入队 / 清空，由收银 presenter 按业务规则调用。
 * 每次变化都替换成新的冻结数组，界面可直接按引用判断是否需要重绘。
 */
export class PendingScanChoiceQueue<TPayload> {
  private items: readonly PendingScanChoice<TPayload>[] = Object.freeze([]);
  private nextId = 0;
  private readonly capacity: number;
  private readonly now: () => number;

  public constructor(options: PendingScanChoiceQueueOptions = {}) {
    const capacity = options.capacity ?? PENDING_SCAN_CHOICE_CAPACITY;
    if (!Number.isSafeInteger(capacity) || capacity <= 0) {
      throw new RangeError("Pending scan choice capacity must be positive.");
    }
    this.capacity = capacity;
    this.now = options.now ?? Date.now;
  }

  public get size(): number {
    return this.items.length;
  }

  public get maxSize(): number {
    return this.capacity;
  }

  /** 队首（最早的待选），队列为空时为 null。 */
  public get current(): PendingScanChoice<TPayload> | null {
    return this.items[0] ?? null;
  }

  public snapshot(): readonly PendingScanChoice<TPayload>[] {
    return this.items;
  }

  /** 入队；队列已满返回 null（调用方须提示收银员：这次扫码没有被记录）。 */
  public tryEnqueue(
    lookupCode: string,
    payload: TPayload,
  ): PendingScanChoice<TPayload> | null {
    if (this.items.length >= this.capacity) return null;
    const normalized = normalize(lookupCode);
    const previous = this.items[this.items.length - 1];
    const entry: PendingScanChoice<TPayload> = Object.freeze({
      id: (this.nextId += 1),
      lookupCode: lookupCode.trim(),
      createdAtMs: this.now(),
      sameLookupCodeAsPrevious:
        previous !== undefined && normalize(previous.lookupCode) === normalized,
      payload,
    });
    this.items = Object.freeze([...this.items, entry]);
    return entry;
  }

  public get(id: number): PendingScanChoice<TPayload> | null {
    return this.items.find((item) => item.id === id) ?? null;
  }

  /** 移除指定条目（选定入车 / 放弃）；条目已不存在返回 null。 */
  public remove(id: number): PendingScanChoice<TPayload> | null {
    const entry = this.get(id);
    if (!entry) return null;
    this.items = Object.freeze(this.items.filter((item) => item.id !== id));
    return entry;
  }

  /** 清空并返回被清掉的条目（用于逐条记日志）。 */
  public clear(): readonly PendingScanChoice<TPayload>[] {
    const removed = this.items;
    if (removed.length > 0) this.items = Object.freeze([]);
    return removed;
  }

  /** 条目从入队到现在等了多久（毫秒，不为负）。 */
  public waitedMs(entry: PendingScanChoice<TPayload>): number {
    return Math.max(0, Math.round(this.now() - entry.createdAtMs));
  }
}

function normalize(value: string): string {
  return value.trim().toUpperCase();
}
