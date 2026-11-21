/**
 * 原生 SQLite 调用的时间线追踪（诊断旁路，仅在显式 begin 之后记录）。
 *
 * 用途：整个 App 只有一条 SQLite 连接，所有读写经同一个串行队列。某个业务动作（如手点
 * 「加入无码商品」）偏慢时，需要分清是「原生执行慢」还是「排队 / JS 线程忙」。驱动层的包装
 * 运行在串行队列内部，所以这里记录的是每次调用的纯原生执行时间；相邻两次调用之间的空隙
 * 就是排队与 JS 线程忙的时间。
 *
 * 约束：
 * - 未 begin 时 record 只是一次 null 判断，对平时的 SQL 路径几乎零开销。
 * - 只记录「调用类型 + SQL 动词 + 表名」，不记录 SQL 全文与参数，不带任何业务数据。
 * - 任何异常都在这里吞掉，诊断旁路不能影响数据库访问。
 */

type NativeCallEntry = Readonly<{
  label: string;
  startMs: number;
  durationMs: number;
}>;

type TraceSession = {
  readonly origin: number;
  readonly entries: NativeCallEntry[];
  dropped: number;
};

const MAX_ENTRIES = 30;
/** 单个日志属性上限 1000 字符，留出余量。 */
const MAX_OUTPUT_LENGTH = 900;

let session: TraceSession | null = null;

export function beginNativeCallTrace(now: () => number = monotonicNow): void {
  session = { origin: now(), entries: [], dropped: 0 };
}

export function isNativeCallTracing(): boolean {
  return session !== null;
}

/** 记录一次已结束的原生调用；startedAt / endedAt 与 begin 使用同一个单调时钟。 */
export function recordNativeCall(
  kind: string,
  sql: string,
  startedAt: number,
  endedAt: number,
): void {
  const active = session;
  if (!active) return;
  try {
    if (active.entries.length >= MAX_ENTRIES) {
      active.dropped += 1;
      return;
    }
    active.entries.push({
      label: describeSql(kind, sql),
      startMs: Math.max(0, Math.round(startedAt - active.origin)),
      durationMs: Math.max(0, Math.round(endedAt - startedAt)),
    });
  } catch {
    // 旁路失败不影响数据库访问。
  }
}

/**
 * 包装一次原生调用：追踪开启时记录其原生执行耗时，未开启时直接透传（只多一次 null 判断）。
 * 调用点必须位于串行队列内部，量到的才是纯原生时间。
 */
export function traceNativeCall<T>(
  kind: string,
  sql: string,
  operation: () => Promise<T>,
  now: () => number = monotonicNow,
): Promise<T> {
  if (!isNativeCallTracing()) return operation();
  const startedAt = now();
  return operation().finally(() => {
    recordNativeCall(kind, sql, startedAt, now());
  });
}

/**
 * 结束追踪并输出时间线文本：`标签@起点偏移ms+时长ms`，以 " | " 分隔。
 * 没有任何调用时返回 null；超长时截断并标注被丢弃的条数。
 */
export function endNativeCallTrace(): string | null {
  const finished = session;
  session = null;
  if (!finished || finished.entries.length === 0) return null;
  try {
    let text = finished.entries
      .map((entry) => `${entry.label}@${entry.startMs}+${entry.durationMs}`)
      .join(" | ");
    if (finished.dropped > 0) text += ` | …+${finished.dropped}`;
    return text.length > MAX_OUTPUT_LENGTH
      ? `${text.slice(0, MAX_OUTPUT_LENGTH - 1)}…`
      : text;
  } catch {
    return null;
  }
}

/** 只取 SQL 动词与第一个表名，例如 `getFirst:SELECT:catalog_snapshots`；不含 SQL 全文与参数。 */
export function describeSql(kind: string, sql: string): string {
  const verb = /^\s*([A-Za-z]+)/u.exec(sql)?.[1]?.toUpperCase() ?? "?";
  const table = /\b(?:FROM|INTO|UPDATE|TABLE)\s+([A-Za-z_][A-Za-z0-9_]*)/iu.exec(
    sql,
  )?.[1];
  return table ? `${kind}:${verb}:${table}` : `${kind}:${verb}`;
}

function monotonicNow(): number {
  return typeof performance === "undefined" ? Date.now() : performance.now();
}
