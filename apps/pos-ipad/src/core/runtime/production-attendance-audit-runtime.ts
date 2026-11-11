import {
  CurrentCashierSession,
  type TrustedCashierLease,
  type TrustedCashierSession,
} from "./current-cashier-session";

import {
  AttendanceAuditPresenter,
} from "@/features/attendance-audit/attendance-audit-presenter";
import type {
  AttendanceAuditRuntimeFactory,
  AttendanceQrDisplaySource,
} from "@/features/attendance-audit/attendance-audit-runtime";
import {
  AttendanceQrController,
  type AttendanceConnectivityPort,
  type AttendanceDeviceContext,
  type AttendanceDeviceContextPort,
  type AttendanceQrCachePort,
  type AttendanceQrCryptoPort,
  type AttendanceSchedulerPort,
} from "@/features/attendance-audit/attendance-qr-controller";
import type {
  AttendanceSecurityRemotePort,
} from "@hb/pos-api-client/features/attendance-audit/hbpos-attendance-security-api";
import {
  OperationAuditPresenter,
  type OperationAuditReadPort,
  type OperationAuditRawRecord,
} from "@hb/pos-domain/features/attendance-audit/operation-audit-presenter";

type TerminalScope = Readonly<{ storeCode: string; deviceCode: string }>;

/** 连通性快照只能由组合根提供，route 不得构造初始 online 状态。 */
export interface AttendanceRuntimeConnectivityPort
  extends AttendanceConnectivityPort {
  currentOnline(): boolean;
}

export type ProductionAttendanceAuditRuntimeDependencies = Readonly<{
  currentCashier: CurrentCashierSession;
  terminal: TerminalScope;
  connectivity: AttendanceRuntimeConnectivityPort;
  deviceContext: AttendanceDeviceContextPort;
  qrCache: AttendanceQrCachePort;
  qrCrypto: AttendanceQrCryptoPort;
  scheduler: AttendanceSchedulerPort;
  attendanceSecurity: AttendanceSecurityRemotePort;
  localAudit: OperationAuditReadPort;
  remoteAudit: OperationAuditReadPort;
  clock: Readonly<{ now(): number }>;
}>;

export class AttendanceAuditRuntimeError extends Error {
  public constructor(
    public readonly code:
      | "ATTENDANCE_AUDIT_SESSION_UNAVAILABLE"
      | "ATTENDANCE_AUDIT_SCOPE_INVALID"
      | "ATTENDANCE_AUDIT_REMOTE_OFFLINE",
  ) {
    super(code);
    this.name = "AttendanceAuditRuntimeError";
  }
}

/**
 * 公开面只保留既有的零参数 factory。所有 Keychain handle、A256 临时材料、缓存
 * 与审计传输实现均封装在 presenter 下面，页面不能伪造门店、设备或 online 初值。
 */
export function createProductionAttendanceAuditRuntime(
  input: ProductionAttendanceAuditRuntimeDependencies,
): AttendanceAuditRuntimeFactory {
  const terminal = normalizeTerminal(input.terminal);

  return Object.freeze({
    createPresenter(): AttendanceAuditPresenter {
      const lease = input.currentCashier.createLease();
      const session = requireScopedLease(lease, terminal);
      const guarded = new LeaseBoundAttendancePorts({
        input,
        lease,
        terminal,
      });
      const qr = createLeaseBoundQrController(input, guarded);
      const audit = new OperationAuditPresenter({
        // 中文注释：在线初值来自 runtime 的受信任连通性快照，不接收 route 参数。
        initialOnline: input.connectivity.currentOnline(),
        permissions: session.permissionCodes,
        read: guarded.audit,
        trustedDeviceCode: terminal.deviceCode,
        trustedStoreCode: terminal.storeCode,
      });
      return new AttendanceAuditPresenter({ audit, qr });
    },
    createQrController(): AttendanceQrDisplaySource {
      // 中文注释：收银页的考勤码与考勤页同一套 lease 守卫——登出、换人或换设备后
      // 定时器不再签发，端口调用直接抛错，不会用旧收银员身份继续出码。
      const lease = input.currentCashier.createLease();
      requireScopedLease(lease, terminal);
      return createLeaseBoundQrController(
        input,
        new LeaseBoundAttendancePorts({ input, lease, terminal }),
      );
    },
  });
}

function createLeaseBoundQrController(
  input: ProductionAttendanceAuditRuntimeDependencies,
  guarded: LeaseBoundAttendancePorts,
): AttendanceQrController {
  return new AttendanceQrController({
    cache: guarded.cache,
    clock: input.clock,
    connectivity: guarded.connectivity,
    crypto: guarded.crypto,
    deviceContext: guarded.deviceContext,
    remote: guarded.attendanceSecurity,
    scheduler: guarded.scheduler,
  });
}

class LeaseBoundAttendancePorts {
  public readonly cache: AttendanceQrCachePort;
  public readonly connectivity: AttendanceConnectivityPort;
  public readonly crypto: AttendanceQrCryptoPort;
  public readonly deviceContext: AttendanceDeviceContextPort;
  public readonly scheduler: AttendanceSchedulerPort;
  public readonly attendanceSecurity: AttendanceSecurityRemotePort;
  public readonly audit: OperationAuditReadPort;

  public constructor(
    private readonly context: Readonly<{
      input: ProductionAttendanceAuditRuntimeDependencies;
      lease: TrustedCashierLease;
      terminal: TerminalScope;
    }>,
  ) {
    this.cache = Object.freeze({
      load: () => this.call(() => this.context.input.qrCache.load()),
      replace: (value: Parameters<AttendanceQrCachePort["replace"]>[0]) =>
        this.call(() => this.context.input.qrCache.replace(value)),
      clear: () => this.call(() => this.context.input.qrCache.clear()),
    });
    this.connectivity = Object.freeze({
      isOnline: () => this.call(() => this.context.input.connectivity.isOnline()),
    });
    this.crypto = Object.freeze({
      createA256Identity: () =>
        this.call(() => this.context.input.qrCrypto.createA256Identity()),
      hasA256Key: (keyHandle: string) =>
        this.call(() => this.context.input.qrCrypto.hasA256Key(keyHandle)),
      withRegistrationKey: <T>(
        keyHandle: string,
        runWithMaterial: (keyMaterialBase64Url: string) => Promise<T>,
      ) => this.withRegistrationKey(keyHandle, runWithMaterial),
      issueAttendanceQr: (
        value: Parameters<AttendanceQrCryptoPort["issueAttendanceQr"]>[0],
      ) =>
        this.call(() => this.context.input.qrCrypto.issueAttendanceQr(value)),
      destroyKey: (keyHandle: string) =>
        this.call(() => this.context.input.qrCrypto.destroyKey(keyHandle)),
    });
    this.deviceContext = Object.freeze({
      getDeviceContext: async () => {
        const context = await this.call(() =>
          this.context.input.deviceContext.getDeviceContext(),
        );
        if (!context) return null;
        if (!matchesTerminal(context, this.context.terminal)) {
          throw new AttendanceAuditRuntimeError(
            "ATTENDANCE_AUDIT_SCOPE_INVALID",
          );
        }
        return context;
      },
    });
    this.scheduler = Object.freeze({
      every: (intervalMs: number, task: () => void) =>
        this.context.input.scheduler.every(intervalMs, () => {
          // 中文注释：旧 lease 的定时器不执行任何动作，避免登出后继续刷新 QR。
          try {
            requireScopedLease(this.context.lease, this.context.terminal);
          } catch {
            return;
          }
          task();
        }),
    });
    this.attendanceSecurity = Object.freeze({
      registerAttendanceKey: (
        request: Parameters<AttendanceSecurityRemotePort["registerAttendanceKey"]>[0],
      ) =>
        this.call(() =>
          this.context.input.attendanceSecurity.registerAttendanceKey(request),
        ),
      fetchEmergencyPublicKeys: (version: number | null) =>
        this.call(() =>
          this.context.input.attendanceSecurity.fetchEmergencyPublicKeys(version),
        ),
      acknowledgeEmergencyPublicKeys: (version: number) =>
        this.call(() =>
          this.context.input.attendanceSecurity.acknowledgeEmergencyPublicKeys(version),
        ),
    });
    this.audit = Object.freeze({
      list: (request: Parameters<OperationAuditReadPort["list"]>[0]) =>
        this.readAudit("list", request),
      get: (request: Parameters<OperationAuditReadPort["get"]>[0]) =>
        this.readAudit("get", request),
    });
  }

  private async withRegistrationKey<T>(
    keyHandle: string,
    use: (keyMaterialBase64Url: string) => Promise<T>,
  ): Promise<T> {
    requireScopedLease(this.context.lease, this.context.terminal);
    const value = await this.context.input.qrCrypto.withRegistrationKey(
      keyHandle,
      async (keyMaterialBase64Url) => {
        requireScopedLease(this.context.lease, this.context.terminal);
        const result = await use(keyMaterialBase64Url);
        requireScopedLease(this.context.lease, this.context.terminal);
        return result;
      },
    );
    requireScopedLease(this.context.lease, this.context.terminal);
    return value;
  }

  private async readAudit(
    method: "list",
    request: Parameters<OperationAuditReadPort["list"]>[0],
  ): Promise<readonly OperationAuditRawRecord[]>;
  private async readAudit(
    method: "get",
    request: Parameters<OperationAuditReadPort["get"]>[0],
  ): Promise<OperationAuditRawRecord | null>;
  private async readAudit(
    method: "list" | "get",
    request:
      | Parameters<OperationAuditReadPort["list"]>[0]
      | Parameters<OperationAuditReadPort["get"]>[0],
  ): Promise<readonly OperationAuditRawRecord[] | OperationAuditRawRecord | null> {
    assertAuditRequestScope(request, this.context.terminal);
    const source = request.source;
    if (source === "remote") {
      const online = await this.call(() =>
        this.context.input.connectivity.isOnline(),
      );
      if (!online) {
        throw new AttendanceAuditRuntimeError(
          "ATTENDANCE_AUDIT_REMOTE_OFFLINE",
        );
      }
    }
    const port = source === "local"
      ? this.context.input.localAudit
      : this.context.input.remoteAudit;
    const result = method === "list"
      ? await this.call(() => port.list(request as Parameters<OperationAuditReadPort["list"]>[0]))
      : await this.call(() => port.get(request as Parameters<OperationAuditReadPort["get"]>[0]));
    assertAuditResponseScope(result, this.context.terminal);
    return result;
  }

  private async call<T>(operation: () => Promise<T>): Promise<T> {
    requireScopedLease(this.context.lease, this.context.terminal);
    const result = await operation();
    requireScopedLease(this.context.lease, this.context.terminal);
    return result;
  }
}

function requireScopedLease(
  lease: TrustedCashierLease,
  terminal: TerminalScope,
): TrustedCashierSession {
  try {
    const session = lease.get();
    if (
      session.storeCode !== terminal.storeCode ||
      session.deviceCode !== terminal.deviceCode
    ) {
      throw new AttendanceAuditRuntimeError(
        "ATTENDANCE_AUDIT_SCOPE_INVALID",
      );
    }
    return session;
  } catch (error) {
    if (error instanceof AttendanceAuditRuntimeError) throw error;
    throw new AttendanceAuditRuntimeError(
      "ATTENDANCE_AUDIT_SESSION_UNAVAILABLE",
    );
  }
}

function normalizeTerminal(value: TerminalScope): TerminalScope {
  return Object.freeze({
    storeCode: requiredText(value.storeCode, "store code"),
    deviceCode: requiredText(value.deviceCode, "device code"),
  });
}

function matchesTerminal(
  context: AttendanceDeviceContext,
  terminal: TerminalScope,
): boolean {
  return (
    context.storeCode === terminal.storeCode &&
    context.deviceCode === terminal.deviceCode
  );
}

function assertAuditRequestScope(
  request: Readonly<{ storeCode: string; deviceCode: string }>,
  terminal: TerminalScope,
): void {
  if (
    request.storeCode !== terminal.storeCode ||
    request.deviceCode !== terminal.deviceCode
  ) {
    throw new AttendanceAuditRuntimeError(
      "ATTENDANCE_AUDIT_SCOPE_INVALID",
    );
  }
}

function assertAuditResponseScope(
  value: readonly OperationAuditRawRecord[] | OperationAuditRawRecord | null,
  terminal: TerminalScope,
): void {
  const records = Array.isArray(value) ? value : value ? [value] : [];
  if (
    records.some(
      (record) =>
        record.storeCode !== terminal.storeCode ||
        record.deviceCode !== terminal.deviceCode,
    )
  ) {
    throw new AttendanceAuditRuntimeError(
      "ATTENDANCE_AUDIT_SCOPE_INVALID",
    );
  }
}

function requiredText(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) throw new Error(`${label} is required.`);
  return normalized;
}
