import type { AttendanceAuditPresenter } from "./attendance-audit-presenter";
import type { AttendanceQrState } from "./attendance-qr-controller";

/**
 * 只用于「显示」考勤二维码的最小接口（收银页空车大码、侧栏考勤小格和放大弹窗）。
 * 和考勤页的 presenter 一样由组合根创建，绑定当前收银员 lease；页面只能 start / destroy。
 */
export interface AttendanceQrDisplaySource {
  getState(): AttendanceQrState;
  subscribe(listener: () => void): () => void;
  start(): void;
  destroy(): void;
}

/**
 * 组合根注入可信设备上下文、Keychain/crypto、加密缓存、可信时间和审计读 Port。
 * 路由只能调用零参数工厂，不能从导航参数伪造门店、设备或权限。
 */
export interface AttendanceAuditRuntimeFactory {
  createPresenter(): AttendanceAuditPresenter;
  /**
   * 收银页用的考勤二维码（不含审计区）。可选：没有提供时收银页不显示考勤码。
   * 同一时刻只能有一个控制器在跑（共用本机签名身份与缓存），由页面在聚焦时创建、失焦时销毁。
   */
  createQrController?(): AttendanceQrDisplaySource;
  /**
   * 登录页用的设备级考勤码：不需要收银员会话（员工到店时 iPad 常停在登录页）。
   * 与 createQrController 共用同一套本机签名身份，所以同样必须同一时刻只跑一个：
   * 登录页聚焦时创建、失焦（登录进入收银页）时销毁。可选：没有提供时登录页不显示考勤码。
   */
  createDeviceQrController?(): AttendanceQrDisplaySource;
}

export function resolveAttendanceAuditRuntimeFactory(
  services: object,
): AttendanceAuditRuntimeFactory | null {
  if (!("attendanceAudit" in services)) return null;
  const candidate = services.attendanceAudit;
  if (
    typeof candidate !== "object" ||
    candidate === null ||
    !("createPresenter" in candidate) ||
    typeof candidate.createPresenter !== "function"
  ) {
    return null;
  }
  return candidate as AttendanceAuditRuntimeFactory;
}
