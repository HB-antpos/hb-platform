/**
 * 标签打印机蓝牙链路诊断：把「断线 → 重连 → 恢复」的每一步先记在内存环形缓冲里，
 * 只在出现“故障片段”（连续重连失败、断线后较久才恢复）时才生成一条中心日志，
 * 由已登录用户经 /mobile/diagnostics/logs 补传到 HbwebExpo。App 不内置长期日志 Key。
 *
 * 为什么不逐次上报：自动重连每 5 秒一次，一台关机的打印机一晚上就是上万次失败；
 * 这里按失败次数里程碑（3/10/30/100/300）和恢复各出一条，一次故障最多约 6 条日志。
 *
 * 本模块不依赖 react-native / 网络 / 存储，api.ts 可直接引用；未安装记录器时所有打点都是空操作，
 * 且记录器内部任何异常都不会外溢，绝不能影响连接与打印本身。
 */

export type PrinterLinkEventKind =
  | "connect.start"
  | "connect.ok"
  | "connect.fail"
  /** 本该连接但被拦下（已暂停、没有保存的打印机），不算失败。 */
  | "connect.skip"
  | "link.lost"
  | "native.status"
  | "app.state"
  | "auto.reconnect"
  /** App 进程启动（冷启动）：带「距上次连接 / 上次活跃多久」，区分「运行中断线」与「重启后冷连」。 */
  | "app.start"
  /** 安卓原生层缓冲的蓝牙事件（ACL / 配对 / GATT 状态码 / 连接尝试现场），具体类型在字段 ev 里。 */
  | "native.diag";

type Scalar = string | number | boolean | null;
export type PrinterLinkFields = Record<string, Scalar | undefined>;

export interface PrinterLinkEvent {
  seq: number;
  atMs: number;
  kind: PrinterLinkEventKind;
  fields: Record<string, Scalar>;
}

/** 内存环形缓冲容量：足够覆盖一次故障的上下文，又不会在长时间失败时无限增长。 */
export const LINK_RING_CAP = 60;
/** 没有「上一次连接成功」可参照时（如冷启动），故障开始前带上的上下文事件数。 */
export const LINK_CONTEXT_BEFORE = 5;
/** 有上一次连接成功时，故障前带上「自那之后的全部事件」，但最多这么多条（超过则优先保留断线类事件）。 */
export const LINK_CONTEXT_MAX = 20;
/** 单条故障日志里 events 序列化后的字符预算；超过就从中间丢事件，保证上报量不会因放宽上下文而变大。 */
export const LINK_EVENTS_BUDGET_CHARS = 10_000;
/** 丢事件时至少保留的条数。 */
export const LINK_EVENTS_MIN_KEPT = 12;
/** 每次故障完整保留开头的事件数，长时间失败后仍能看到“最初是怎么断的”。 */
export const LINK_HEAD_CAP = 12;
/** 快照里附带的最近事件数。 */
export const LINK_SNAPSHOT_TAIL = 30;
/** 连续失败达到这些次数时各上报一次“仍在失败”。 */
export const FAILURE_MILESTONES: readonly number[] = [3, 10, 30, 100, 300];
/** 恢复时只上报“有意义的”故障：失败至少 2 次，或从断线到恢复超过 60 秒。 */
export const RECOVERED_MIN_FAILURES = 2;
export const RECOVERED_MIN_DURATION_MS = 60_000;
export const LINK_TEXT_MAX = 160;
export const MAX_ERROR_KINDS = 8;
/** 与后端单次上限（20 条）对齐；本机暂存超过时丢弃最旧的。 */
export const MAX_PENDING_LINK_LOGS = 20;
export const LINK_DIAGNOSTICS_STORAGE_KEY = "hb.printer.linkDiagnostics.v1";

export type PrinterLinkTrigger = "auto" | "start" | "manual" | "print" | "select" | "receipt-test";

export interface PrinterLinkLogItem {
  /** 入队时固定，服务端按它幂等去重，补传重试不会写两条。 */
  clientEventId: string;
  level: "Warning" | "Information";
  message: string;
  timestampUtc: string;
  environment: string;
  sourceType: "Mobile";
  serviceName: "HbwebExpoApp";
  category: "printer.link";
  appVersion?: string;
  properties: Record<string, unknown>;
}

export interface PrinterLinkContext {
  environment: string;
  appVersion?: string;
  /** 版本、runtime、updateId 等随日志一起上报的静态信息。 */
  properties?: Record<string, unknown>;
}

export interface PrinterLinkRecorderDeps {
  now: () => number;
  newId: () => string;
  emit: (item: PrinterLinkLogItem) => void;
  context: () => PrinterLinkContext;
}

interface ErrorStat {
  code: string | null;
  message: string;
  count: number;
  minElapsedMs: number | null;
  maxElapsedMs: number | null;
}

interface Episode {
  id: string;
  startedAtMs: number;
  failures: number;
  /** 故障开头的事件，长时间失败后仍保留“最初是怎么断的”。 */
  head: PrinterLinkEvent[];
  /** head 最多保留的条数 = 开头上下文条数 + LINK_HEAD_CAP。 */
  headLimit: number;
  errors: Map<string, ErrorStat>;
  nextMilestone: number;
  address: string | null;
  transport: string | null;
}

export interface NativePrinterLinkStatus {
  supported: boolean;
  enabled: boolean;
  connected: boolean;
  address?: string | null;
}

export interface PrinterLinkRecorder {
  /** atMs 可选：事件实际发生的时刻（原生事件在之后才被取走，须保留原始时间）；缺省为当前时间。 */
  record: (kind: PrinterLinkEventKind, fields?: PrinterLinkFields, atMs?: number) => void;
  recordNativeStatus: (status: NativePrinterLinkStatus, savedAddress: string | null) => void;
  /** 标签打印成功一次：只累计会话内的打印次数与最近打印时间，不写事件（打印很频繁，不能挤占环形缓冲）。 */
  recordPrintSuccess: () => void;
}

/** 一次「已连接」会话的摘要：断线时用来判断是不是空闲一段时间后断的（打印机自动休眠）。 */
interface LinkSession {
  startedAtMs: number;
  lastPrintAtMs: number | null;
  prints: number;
}

/** 断线类事件：上下文被截断时优先保留。 */
const IMPORTANT_CONTEXT_KINDS: ReadonlySet<PrinterLinkEventKind> = new Set([
  "link.lost",
  "app.state",
  "app.start",
  "auto.reconnect",
  "connect.ok",
]);
const IMPORTANT_NATIVE_EVENTS: ReadonlySet<string> = new Set([
  "acl.disconnected",
  "acl.connected",
  "connection.cleared",
  "adapter.state",
  "bond.changed",
  "ble.gatt.state",
]);

function isImportantContextEvent(event: PrinterLinkEvent) {
  if (IMPORTANT_CONTEXT_KINDS.has(event.kind)) return true;
  const ev = event.fields.ev;
  return event.kind === "native.diag" && typeof ev === "string" && IMPORTANT_NATIVE_EVENTS.has(ev);
}

/** 上下文超过上限时：保留最近的一半，再补上更早的断线类事件，总数不超过 max。 */
export function pickLinkContext(events: PrinterLinkEvent[], max: number) {
  if (events.length <= max) return events;
  const recentFrom = events.length - Math.floor(max / 2);
  return events.filter((event, index) => index >= recentFrom || isImportantContextEvent(event)).slice(-max);
}

/** events 序列化后超过预算时，从中间一条条丢，直到不超或只剩最少条数；返回保留的事件与被丢的条数。 */
export function fitLinkEventsToBudget<T>(events: T[], budgetChars = LINK_EVENTS_BUDGET_CHARS, minKept = LINK_EVENTS_MIN_KEPT) {
  let list = events;
  let size = JSON.stringify(list).length;
  let dropped = 0;
  while (size > budgetChars && list.length > minKept) {
    const index = Math.floor(list.length / 2);
    size -= JSON.stringify(list[index]).length + 1;
    list = [...list.slice(0, index), ...list.slice(index + 1)];
    dropped += 1;
  }
  return { events: list, dropped };
}

function clip(value: string) {
  return value.length > LINK_TEXT_MAX ? `${value.slice(0, LINK_TEXT_MAX)}…` : value;
}

/** 只保留标量并截断长文本；t / kind 是输出事件的保留键，字段里出现时丢弃。 */
function sanitizeFields(fields: PrinterLinkFields | undefined) {
  const clean: Record<string, Scalar> = {};
  for (const [key, value] of Object.entries(fields ?? {})) {
    if (value === undefined || key === "t" || key === "kind") continue;
    clean[key] = typeof value === "string" ? clip(value) : value;
  }
  return clean;
}

export function describeLinkError(error: unknown): { code: string | null; message: string } {
  if (error && typeof error === "object") {
    const { code, message } = error as { code?: unknown; message?: unknown };
    return {
      code: typeof code === "string" ? code : null,
      message: typeof message === "string" ? message : String(error),
    };
  }
  return { code: null, message: String(error) };
}

function readString(value: Scalar | undefined) {
  return typeof value === "string" ? value : null;
}

export function createPrinterLinkRecorder(deps: PrinterLinkRecorderDeps): PrinterLinkRecorder {
  let seq = 0;
  let ring: PrinterLinkEvent[] = [];
  let episode: Episode | null = null;
  // 用户主动暂停自动重连（手动断开、小票测试占用连接）期间的断线不是故障。
  let autoPaused = false;
  let lastNativeKey: string | null = null;
  let lastLabelConnected: boolean | null = null;
  // 当前「已连接」会话；断线时据此算出连了多久、距上次打印多久。
  let session: LinkSession | null = null;
  // 最近一次断线时的会话摘要，随同一次故障的日志上报；下一次连上后清掉。
  let lastSessionEnd: Record<string, Scalar> | null = null;
  // 最近一次前后台切换，断线时一并记下断线发生在前台还是后台。
  let lastAppState: { state: string; atMs: number } | null = null;
  // 最近一次冷启动信息（距上次连接 / 上次活跃多久）。
  let lastAppStart: Record<string, Scalar> | null = null;

  function buildEvents(current: Episode) {
    const bySeq = new Map<number, PrinterLinkEvent>();
    for (const event of [...current.head, ...ring.slice(-LINK_SNAPSHOT_TAIL)]) bySeq.set(event.seq, event);
    const events = [...bySeq.values()]
      .sort((a, b) => a.atMs - b.atMs || a.seq - b.seq)
      .map((event) => ({ t: event.atMs - current.startedAtMs, kind: event.kind, ...event.fields }));
    // 放宽上下文后单条日志可能变大：超过字符预算就从中间丢事件，保证上报量不增加。
    return fitLinkEventsToBudget(events);
  }

  function snapshot(current: Episode, phase: "failing" | "recovered", nowMs: number) {
    const context = deps.context();
    const fitted = buildEvents(current);
    const durationMs = nowMs - current.startedAtMs;
    const seconds = Math.round(durationMs / 1000);
    const warning = phase === "failing" || current.failures >= 3;
    const item: PrinterLinkLogItem = {
      clientEventId: deps.newId(),
      level: warning ? "Warning" : "Information",
      message: phase === "failing"
        ? `标签打印机蓝牙重连连续失败 ${current.failures} 次（已持续 ${seconds} 秒）`
        : `标签打印机蓝牙断线后恢复：重连失败 ${current.failures} 次，历时 ${seconds} 秒`,
      timestampUtc: new Date(nowMs).toISOString(),
      environment: context.environment,
      sourceType: "Mobile",
      serviceName: "HbwebExpoApp",
      category: "printer.link",
      appVersion: context.appVersion,
      properties: {
        ...context.properties,
        phase,
        episodeId: current.id,
        startedAtUtc: new Date(current.startedAtMs).toISOString(),
        durationMs,
        failures: current.failures,
        address: current.address,
        transport: current.transport,
        errors: [...current.errors.values()].map((stat) => ({
          code: stat.code,
          message: stat.message,
          count: stat.count,
          minElapsedMs: stat.minElapsedMs,
          maxElapsedMs: stat.maxElapsedMs,
        })),
        // 最近一次断线时的会话摘要与冷启动信息：判断「运行中断线」还是「重启后冷连」、是不是空闲休眠。
        ...(lastSessionEnd ? { session: lastSessionEnd } : {}),
        ...(lastAppStart ? { appStart: lastAppStart } : {}),
        ...(fitted.dropped > 0 ? { eventsTrimmed: fitted.dropped } : {}),
        events: fitted.events,
      },
    };
    deps.emit(item);
  }

  function openEpisode(event: PrinterLinkEvent) {
    if (episode) return episode;
    // 环形缓冲此时已含本事件。有「上一次连接成功」就带上自那之后的全部事件（最多 LINK_CONTEXT_MAX 条，
    // 超过时优先保留断线类事件），否则（如冷启动）只带最近几条。
    let lastOk = -1;
    for (let index = ring.length - 2; index >= 0; index -= 1) {
      const candidate = ring[index];
      if (candidate.kind === "connect.ok" && candidate.fields.role !== "receipt") {
        lastOk = index;
        break;
      }
    }
    const head = lastOk >= 0
      ? pickLinkContext(ring.slice(lastOk), LINK_CONTEXT_MAX + 1)
      : ring.slice(-(LINK_CONTEXT_BEFORE + 1));
    episode = {
      id: deps.newId(),
      startedAtMs: event.atMs,
      failures: 0,
      head,
      headLimit: head.length + LINK_HEAD_CAP,
      errors: new Map(),
      nextMilestone: 0,
      address: null,
      transport: null,
    };
    return episode;
  }

  function trackAddress(current: Episode, fields: Record<string, Scalar>) {
    current.address = readString(fields.address) ?? current.address;
    current.transport = readString(fields.transport) ?? current.transport;
  }

  function onConnectFail(event: PrinterLinkEvent) {
    const current = openEpisode(event);
    current.failures += 1;
    trackAddress(current, event.fields);
    const code = readString(event.fields.code);
    const message = readString(event.fields.message) ?? "";
    const key = `${code ?? ""}|${message}`;
    const elapsed = typeof event.fields.elapsedMs === "number" ? event.fields.elapsedMs : null;
    const stat = current.errors.get(key);
    if (stat) {
      stat.count += 1;
      if (elapsed !== null) {
        stat.minElapsedMs = stat.minElapsedMs === null ? elapsed : Math.min(stat.minElapsedMs, elapsed);
        stat.maxElapsedMs = stat.maxElapsedMs === null ? elapsed : Math.max(stat.maxElapsedMs, elapsed);
      }
    } else if (current.errors.size < MAX_ERROR_KINDS) {
      current.errors.set(key, { code, message, count: 1, minElapsedMs: elapsed, maxElapsedMs: elapsed });
    }
    // 里程碑只在刚好达到时触发一次，之后继续失败不再重复上报同一档。
    const milestone = FAILURE_MILESTONES[current.nextMilestone];
    if (milestone !== undefined && current.failures >= milestone) {
      current.nextMilestone += 1;
      snapshot(current, "failing", event.atMs);
    }
  }

  /**
   * 断线原因（从已入账的原生事件推断，Android 10/11 的经典蓝牙拿不到系统给的断开原因码，推断不到记 unknown）：
   * bluetooth_off > connection.cleared 的 reason（acl_disconnected / ble_lost）> acl_disconnected > gatt_disconnected。
   */
  function deriveEndReason(event: PrinterLinkEvent) {
    if (readString(event.fields.source) === "print") return "print_failed";
    if (event.fields.bluetoothEnabled === false) return "bluetooth_off";
    const since = session?.startedAtMs ?? Number.NEGATIVE_INFINITY;
    let cleared: string | null = null;
    let acl = false;
    let gatt = false;
    let off = false;
    for (let index = ring.length - 1; index >= 0 && ring.length - index <= LINK_RING_CAP; index -= 1) {
      const candidate = ring[index];
      if (candidate.atMs < since) break;
      if (candidate.kind !== "native.diag") continue;
      const ev = readString(candidate.fields.ev);
      if (ev === "connection.cleared") cleared ??= readString(candidate.fields.reason) ?? "connection_cleared";
      else if (ev === "acl.disconnected") acl = true;
      else if (ev === "adapter.state") {
        const state = readString(candidate.fields.state);
        if (state === "off" || state === "turning_off") off = true;
      } else if (ev === "ble.gatt.state" && candidate.fields.newState === 0) gatt = true;
    }
    if (off) return "bluetooth_off";
    if (cleared) return cleared;
    if (acl) return "acl_disconnected";
    if (gatt) return "gatt_disconnected";
    return "unknown";
  }

  function describeSessionEnd(event: PrinterLinkEvent): Record<string, Scalar> {
    return {
      connectedForMs: session ? Math.max(0, event.atMs - session.startedAtMs) : null,
      // 距上次成功打印多久；本次会话还没打印过则为 null（此时看 connectedForMs）。
      idleSincePrintMs: session?.lastPrintAtMs != null ? Math.max(0, event.atMs - session.lastPrintAtMs) : null,
      printsInSession: session ? session.prints : null,
      endReason: deriveEndReason(event),
      appState: lastAppState?.state ?? null,
      appStateForMs: lastAppState ? Math.max(0, event.atMs - lastAppState.atMs) : null,
    };
  }

  function pickSessionSummary(fields: Record<string, Scalar>, atMs: number): Record<string, Scalar> {
    return {
      endedAtUtc: new Date(atMs).toISOString(),
      connectedForMs: fields.connectedForMs ?? null,
      idleSincePrintMs: fields.idleSincePrintMs ?? null,
      printsInSession: fields.printsInSession ?? null,
      endReason: fields.endReason ?? null,
      appState: fields.appState ?? null,
      appStateForMs: fields.appStateForMs ?? null,
    };
  }

  function recordPrintSuccess() {
    try {
      if (!session) return;
      session.lastPrintAtMs = deps.now();
      session.prints += 1;
    } catch {
      // 同 record：不外溢。
    }
  }

  function record(kind: PrinterLinkEventKind, fields?: PrinterLinkFields, atMs?: number) {
    try {
      const event: PrinterLinkEvent = {
        seq: (seq += 1),
        atMs: typeof atMs === "number" && Number.isFinite(atMs) ? atMs : deps.now(),
        kind,
        fields: sanitizeFields(fields),
      };
      ring.push(event);
      if (ring.length > LINK_RING_CAP) ring = ring.slice(-LINK_RING_CAP);

      if (kind === "auto.reconnect") {
        // 只有「暂停 / 恢复」事件带 paused；退避阶段变化（phase）等事件不能改写暂停状态。
        if ("paused" in event.fields) {
          autoPaused = event.fields.paused === true;
          // 用户主动暂停后不再追踪这次故障，也不会再因它产生日志。
          if (autoPaused) episode = null;
        }
        return;
      }
      // 小票机测试共用同一个原生 socket，只作为上下文保留，不计入标签打印机的故障。
      if (event.fields.role === "receipt") return;

      if (kind === "app.state") {
        lastAppState = { state: String(event.fields.state ?? ""), atMs: event.atMs };
      } else if (kind === "app.start") {
        lastAppStart = { ...event.fields };
      } else if (kind === "link.lost") {
        // 断线：补上「连了多久、距上次打印多久、怎么断的、断时在前台还是后台」，并结束这次会话。
        Object.assign(event.fields, describeSessionEnd(event));
        lastSessionEnd = pickSessionSummary(event.fields, event.atMs);
        session = null;
      } else if (kind === "connect.ok") {
        session = { startedAtMs: event.atMs, lastPrintAtMs: null, prints: 0 };
      }

      const opening = !episode && (kind === "connect.fail" || (kind === "link.lost" && !autoPaused));
      if (opening) openEpisode(event);
      else if (episode && episode.head.length < episode.headLimit) episode.head.push(event);

      if (kind === "connect.fail") {
        onConnectFail(event);
      } else if (kind === "connect.ok" && episode) {
        trackAddress(episode, event.fields);
        const durationMs = event.atMs - episode.startedAtMs;
        if (episode.failures >= RECOVERED_MIN_FAILURES || durationMs >= RECOVERED_MIN_DURATION_MS) {
          snapshot(episode, "recovered", event.atMs);
        }
        episode = null;
      }
      // 已连上：上一次断线的会话摘要已随恢复日志上报，清掉，避免带进下一次故障。
      if (kind === "connect.ok") lastSessionEnd = null;
    } catch {
      // 诊断只是旁路记录，任何异常都不能影响连接与打印。
    }
  }

  function recordNativeStatus(status: NativePrinterLinkStatus, savedAddress: string | null) {
    try {
      const labelConnected = status.connected && savedAddress !== null && status.address === savedAddress;
      const key = `${status.supported}|${status.enabled}|${status.connected}|${status.address ?? ""}|${labelConnected}`;
      if (key === lastNativeKey) return;
      const wasLabelConnected = lastLabelConnected;
      lastNativeKey = key;
      lastLabelConnected = labelConnected;
      record("native.status", {
        supported: status.supported,
        enabled: status.enabled,
        connected: status.connected,
        address: status.address ?? null,
        labelConnected,
      });
      // 原生层报告标签打印机从已连接变为未连接：断线事件（ACL 断开、写入失败后清理等）。
      if (wasLabelConnected === true && !labelConnected) {
        record("link.lost", { source: "native", address: savedAddress, bluetoothEnabled: status.enabled });
      }
    } catch {
      // 同 record：不外溢。
    }
  }

  return { record, recordNativeStatus, recordPrintSuccess };
}

let activeRecorder: PrinterLinkRecorder | null = null;

/** 运行时安装/卸载记录器；传 null 卸载。未安装时 recordPrinterLink 是空操作。 */
export function installPrinterLinkRecorder(deps: PrinterLinkRecorderDeps | null) {
  activeRecorder = deps ? createPrinterLinkRecorder(deps) : null;
}

export function recordPrinterLink(kind: PrinterLinkEventKind, fields?: PrinterLinkFields, atMs?: number) {
  activeRecorder?.record(kind, fields, atMs);
}

export function recordPrinterNativeStatus(status: NativePrinterLinkStatus, savedAddress: string | null) {
  activeRecorder?.recordNativeStatus(status, savedAddress);
}

/** 标签打印成功一次（累计会话内打印次数与最近打印时间，用于判断断线前是否空闲了一段时间）。 */
export function recordPrinterPrintSuccess() {
  activeRecorder?.recordPrintSuccess();
}

// ---------------------------------------------------------------------------
// 冷启动标记：区分「运行中断线」与「进程重启后冷连」
// ---------------------------------------------------------------------------

export const LINK_LAST_SEEN_STORAGE_KEY = "hb.printer.linkLastSeen.v1";

/** 上一次进程退出前最后的已知状态（只在前后台切换与连接状态变化时落盘，写入很少）。 */
export interface LinkLastSeen {
  /** 最后一次确认「标签打印机处于已连接」的时刻。 */
  lastConnectedAtMs: number | null;
  /** 最后一次落盘（前后台切换或连接状态变化）的时刻。 */
  lastSeenAtMs: number | null;
  /** 落盘时打印机是否处于已连接。 */
  wasConnected: boolean;
}

export const EMPTY_LINK_LAST_SEEN: LinkLastSeen = { lastConnectedAtMs: null, lastSeenAtMs: null, wasConnected: false };

function readTimestamp(value: unknown) {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? value : null;
}

export function parseLinkLastSeen(raw: string | null): LinkLastSeen | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<LinkLastSeen> | null;
    if (!parsed || typeof parsed !== "object") return null;
    return {
      lastConnectedAtMs: readTimestamp(parsed.lastConnectedAtMs),
      lastSeenAtMs: readTimestamp(parsed.lastSeenAtMs),
      wasConnected: parsed.wasConnected === true,
    };
  } catch {
    return null;
  }
}

/** 落盘前计算新的「最后已知状态」：已连接时刷新最后连接时间，否则保留旧值。 */
export function nextLinkLastSeen(previous: LinkLastSeen, connected: boolean, nowMs: number): LinkLastSeen {
  return {
    lastConnectedAtMs: connected ? nowMs : previous.lastConnectedAtMs,
    lastSeenAtMs: nowMs,
    wasConnected: connected,
  };
}

/**
 * app.start 事件的字段：距上次已连接 / 上次活跃多久、当时是否连着。
 * 没有历史（首次安装、存储读取失败）时对应字段为 null，只表示「冷启动」。
 */
export function buildAppStartFields(lastSeen: LinkLastSeen | null, nowMs: number): PrinterLinkFields {
  return {
    coldStart: true,
    sinceLastConnectedMs: lastSeen?.lastConnectedAtMs != null ? Math.max(0, nowMs - lastSeen.lastConnectedAtMs) : null,
    sinceLastSeenMs: lastSeen?.lastSeenAtMs != null ? Math.max(0, nowMs - lastSeen.lastSeenAtMs) : null,
    wasConnectedAtLastSeen: lastSeen ? lastSeen.wasConnected : null,
  };
}

// ---------------------------------------------------------------------------
// 本机暂存与补传（存储与网络由运行时注入，便于纯逻辑测试）
// ---------------------------------------------------------------------------

export interface PrinterLinkStorage {
  getString(key: string): Promise<string | null>;
  setString(key: string, value: string): Promise<void>;
}

export type PrinterLinkUploadResult = "skipped" | "uploaded" | "dropped" | "kept";

export function parsePendingLinkLogs(raw: string | null): PrinterLinkLogItem[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed)
      ? parsed.filter((item): item is PrinterLinkLogItem =>
          Boolean(item && typeof item === "object" && typeof (item as PrinterLinkLogItem).clientEventId === "string"))
      : [];
  } catch {
    return [];
  }
}

export function appendPendingLinkLog(items: PrinterLinkLogItem[], item: PrinterLinkLogItem) {
  // 新日志放最后；超过后端单次上限时丢最旧的，长期离线也不会无限增长。
  return [...items.filter((existing) => existing.clientEventId !== item.clientEventId), item].slice(-MAX_PENDING_LINK_LOGS);
}

function readHttpStatus(error: unknown) {
  const response = (error as { response?: { status?: unknown } } | null)?.response;
  return typeof response?.status === "number" ? response.status : undefined;
}

/**
 * 补传本机暂存的链路日志：成功或服务端明确拒收（未启用 403 / 格式不对 400）都清掉，
 * 网络失败、限流、服务端错误、未登录、接口尚未上线（OTA 先于后端）则保留下次再传。
 */
export async function uploadPendingPrinterLinkLogs(deps: {
  storage: PrinterLinkStorage;
  upload: (items: PrinterLinkLogItem[]) => Promise<void>;
}): Promise<PrinterLinkUploadResult> {
  const pending = parsePendingLinkLogs(await deps.storage.getString(LINK_DIAGNOSTICS_STORAGE_KEY));
  if (!pending.length) return "skipped";

  const batch = pending.slice(0, MAX_PENDING_LINK_LOGS);
  const remaining = () => {
    const sent = new Set(batch.map((item) => item.clientEventId));
    return pending.filter((item) => !sent.has(item.clientEventId));
  };
  try {
    await deps.upload(batch);
  } catch (error) {
    const status = readHttpStatus(error);
    if (status === undefined || status >= 500 || status === 429 || status === 401 || status === 404) {
      return "kept";
    }
    await deps.storage.setString(LINK_DIAGNOSTICS_STORAGE_KEY, JSON.stringify(remaining()));
    return "dropped";
  }
  await deps.storage.setString(LINK_DIAGNOSTICS_STORAGE_KEY, JSON.stringify(remaining()));
  return "uploaded";
}
