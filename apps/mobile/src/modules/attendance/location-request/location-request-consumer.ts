// 位置请求待办的「消费」流程（依赖全部由外部注入，不依赖 React Native，可在 Node 下测试）。
//
// 一轮消费：校验资格 → 拉待办 → 筛选（复核未过期 / 本会话未处理）→ 取「一次」位置（本轮所有待办共用）→ 逐条响应。
//
// 并发与防抖（都在内存里，不落盘）：
// - 同一时刻只允许一轮在跑（in-flight 锁）。自动触发遇到在跑的一轮直接放弃；
//   「刚收到位置请求推送」和交互触发不能被吞掉——请求很可能是刚创建的，在跑的那一轮拉到的是旧列表，
//   所以记一个「补跑」标记，这一轮结束后立刻再跑一次（交互优先于非交互）；
// - 自动触发（冷启动 / 回前台）拉待办至少间隔 15 秒；收到位置请求推送与交互触发不受此限；
// - 同一 requestGuid 在一次会话内成功响应、或被服务端判为终态失败后，不再重复处理；
// - 网络错误 / 5xx / 响应格式异常只在「下次触发」时重试，本模块绝不自己循环重试。
//
// 安全边界：
// - 取位置只经注入的 captureLocation：非交互模式权限没授予就静默跳过，绝不弹系统权限框；
// - 每个 await 之后都复核「还是同一个账号」，换号 / 登出后不再继续上报；
// - 日志只记错误短码与计数，不含坐标。
import {
  buildReportedNotice,
  buildRespondRequestBody,
  bypassesFetchThrottle,
  classifyLocationRequestError,
  isInteractiveTrigger,
  isLocationFreshForRequest,
  LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS,
  selectActionableRequests,
  type AttendanceLocationRequest,
  type LocationRequestDeviceContext,
  type LocationRequestNotice,
  type LocationRequestTrigger,
  type RespondLocationRequestBody,
} from "./location-request-logic";
import type { LocationCaptureOutcome } from "./location-request-capture";

export type LocationRequestConsumeOutcome =
  | { kind: "skipped"; reason: "not-eligible" | "in-flight" | "throttled" | "permission-not-granted" }
  /** 拉到了待办列表，但没有需要响应的请求。 */
  | { kind: "idle" }
  | { kind: "failed"; stage: "list" | "location" | "respond" }
  | { kind: "done"; responded: number; discarded: number; retryLater: number };

export interface LocationRequestConsumerDeps {
  now(): number;
  /** 当前能消费待办的账号编号；未登录 / 非个人账号 / 没有 Attendance.Punch.Self / iOS 审核态返回 null。 */
  resolveUserGuid(): string | null;
  listPending(userGuid: string): Promise<AttendanceLocationRequest[]>;
  respond(userGuid: string, requestGuid: string, body: RespondLocationRequestBody): Promise<AttendanceLocationRequest>;
  /** 取一次位置；interactive=true 才允许走系统权限申请。 */
  captureLocation(interactive: boolean): Promise<LocationCaptureOutcome>;
  getDeviceContext(): Promise<LocationRequestDeviceContext>;
  /** 至少有一条请求响应成功后的轻量提示。 */
  showNotice(notice: LocationRequestNotice): void;
  /** 只写短码与计数，不含坐标。 */
  warn(message: string, detail?: Record<string, unknown>): void;
}

export function createLocationRequestConsumer(deps: LocationRequestConsumerDeps) {
  /** 本会话内已处理（成功响应或终态失败）的请求。 */
  const handled = new Set<string>();
  let stateUserGuid: string | null = null;
  let lastFetchAtMs: number | null = null;
  let running = false;
  let queuedRerun: "interactive" | "silent" | null = null;

  /** 账号变化时丢弃上一个账号的节流与去重记录。 */
  function adoptUser(userGuid: string): void {
    if (stateUserGuid === userGuid) return;
    stateUserGuid = userGuid;
    handled.clear();
    lastFetchAtMs = null;
  }

  async function run(userGuid: string, interactive: boolean): Promise<LocationRequestConsumeOutcome> {
    const stillCurrent = () => deps.resolveUserGuid() === userGuid;
    const notEligible: LocationRequestConsumeOutcome = { kind: "skipped", reason: "not-eligible" };

    // ── 1. 拉待办 ──
    let pending: AttendanceLocationRequest[];
    try {
      pending = await deps.listPending(userGuid);
    } catch (error) {
      const failure = classifyLocationRequestError(error);
      if (failure.kind === "aborted") return notEligible;
      deps.warn("[location-request] 拉取待办失败", { kind: failure.kind, code: failure.code, status: failure.status });
      return { kind: "failed", stage: "list" };
    }
    if (!stillCurrent()) return notEligible;

    // ── 2. 筛选：仍是 Pending、未过期（设备时钟只作额外保护）、本会话没处理过 ──
    const actionable = selectActionableRequests(pending, deps.now(), handled);
    if (actionable.length === 0) return { kind: "idle" };

    // ── 3. 取「一次」位置：所有待办共用一个点（通常只有 0–1 条）──
    const captured = await deps.captureLocation(interactive);
    if (!stillCurrent()) return notEligible;
    if (captured.status === "permission-not-granted" || captured.status === "review-skipped") {
      // 非交互模式权限未授予：静默跳过，等员工点通知（交互模式）再走权限申请。
      return { kind: "skipped", reason: captured.status === "review-skipped" ? "not-eligible" : "permission-not-granted" };
    }
    if (captured.status !== "ok") {
      // 员工主动点了通知却没取到位置：给一条提示，免得点了没反应；自动触发不打扰。
      if (interactive) deps.showNotice({ kind: "location-unavailable" });
      deps.warn("[location-request] 取位置失败", {
        status: captured.status,
        reason: captured.status === "unavailable" ? captured.reason : undefined,
      });
      return { kind: "failed", stage: "location" };
    }
    const location = captured.location;

    // 取到的点必须是「收到请求之后」取的：采集时间比请求创建时间早太多（取到缓存旧点），
    // 服务端会以 LOCATION_STALE 终态拒绝，这里提前识别，不消耗请求，留给下次触发重新取点。
    const fresh = actionable.filter((request) => isLocationFreshForRequest(location.locationCapturedAtUtc, request));
    let retryLater = actionable.length - fresh.length;
    if (fresh.length === 0) {
      deps.warn("[location-request] 取到的位置早于请求创建时间，稍后重试", { count: retryLater });
      return { kind: "failed", stage: "location" };
    }

    let device: LocationRequestDeviceContext = {};
    try {
      device = await deps.getDeviceContext();
    } catch {
      // 设备信息是可选补充，取不到也照常上报
    }
    if (!stillCurrent()) return notEligible;
    const body = buildRespondRequestBody(device, location);

    // ── 4. 逐条响应 ──
    const responded: AttendanceLocationRequest[] = [];
    let discarded = 0;
    for (const request of fresh) {
      if (!stillCurrent()) break;
      try {
        await deps.respond(userGuid, request.requestGuid, body);
        handled.add(request.requestGuid);
        responded.push(request);
      } catch (error) {
        const failure = classifyLocationRequestError(error);
        if (failure.kind === "aborted") break;
        if (failure.kind === "terminal") {
          // 服务端已给出确定结论（过期 / 作废 / 已下班 / 定位过期 …）：本会话不再处理这条，静默丢弃。
          handled.add(request.requestGuid);
          discarded += 1;
          deps.warn("[location-request] 响应被服务端拒绝，已丢弃", { code: failure.code, status: failure.status });
          continue;
        }
        // 网络 / 5xx：留给下次触发重试；后面的请求大概率同样失败，不再继续。
        retryLater += fresh.length - responded.length - discarded;
        deps.warn("[location-request] 响应失败，下次触发时重试", { status: failure.status });
        break;
      }
    }

    if (responded.length > 0 && stillCurrent()) {
      const notice = buildReportedNotice(responded);
      if (notice) deps.showNotice(notice);
    }
    return { kind: "done", responded: responded.length, discarded, retryLater };
  }

  async function startRun(userGuid: string, interactive: boolean): Promise<LocationRequestConsumeOutcome> {
    running = true;
    try {
      return await run(userGuid, interactive);
    } catch (error) {
      // run 内部已兜住所有已知失败；这里只防御意外异常，消费永不向外抛错。
      deps.warn("[location-request] 消费流程异常", { name: error instanceof Error ? error.name : "unknown" });
      return { kind: "failed", stage: "respond" };
    } finally {
      running = false;
      const rerun = queuedRerun;
      queuedRerun = null;
      if (rerun) {
        // 在跑期间收到过「刚收到推送 / 交互」触发：补跑一次（交互优先）。
        void consume({ trigger: rerun === "interactive" ? "push-tap" : "push-received" });
      }
    }
  }

  /**
   * 触发一轮消费。永不 reject；返回值给测试与诊断用。
   * 非交互触发在权限未授予时不会弹任何系统弹窗。
   */
  async function consume({ trigger }: { trigger: LocationRequestTrigger }): Promise<LocationRequestConsumeOutcome> {
    const userGuid = deps.resolveUserGuid();
    if (!userGuid) return { kind: "skipped", reason: "not-eligible" };
    const interactive = isInteractiveTrigger(trigger);
    const bypass = bypassesFetchThrottle(trigger);

    if (running) {
      if (bypass) {
        queuedRerun = interactive || queuedRerun === "interactive" ? "interactive" : "silent";
      }
      return { kind: "skipped", reason: "in-flight" };
    }

    adoptUser(userGuid);
    const nowMs = deps.now();
    if (!bypass && lastFetchAtMs !== null && nowMs - lastFetchAtMs < LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS) {
      return { kind: "skipped", reason: "throttled" };
    }
    lastFetchAtMs = nowMs;
    return startRun(userGuid, interactive);
  }

  /** 登出 / 会话结束：丢弃节流与去重记录（正在跑的一轮会在下一个 await 之后发现账号已变而停止）。 */
  function reset(): void {
    stateUserGuid = null;
    lastFetchAtMs = null;
    queuedRerun = null;
    handled.clear();
  }

  return { consume, reset };
}

export type LocationRequestConsumer = ReturnType<typeof createLocationRequestConsumer>;
