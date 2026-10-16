// 摄像头「一次性地址」（含 token）的纯逻辑：响应归一化、脱敏显示、一次性展示状态机。
// 与网页后台 SecretRevealModal 同一约定，且更严：
// 1. token 与完整地址只出现在新增 / 重新生成这一次响应里，库里只存哈希，后端也不会再给；
// 2. 这里只保留「摄像头地址」一份（token 已包含在地址里，不单独保存、不单独展示）；
// 3. 地址只能放在组件局部 state，关闭 / 卸载 / 退到后台立刻清空；不得写进 react-query / 磁盘 / 日志 / 错误对象 / 路由参数；
// 4. 本文件不依赖 React Native，可在 Node 下测试；禁止出现 console.*（由 source-contract 测试守住）。
import { normalizeFaceCamera } from "./normalize";
import type { FaceCamera } from "./types";

export interface FaceCameraSecretResult {
  camera: FaceCamera;
  /** 完整的摄像机推送地址（末段是 token）。 */
  cameraUrl: string;
}

/** 服务层在响应缺地址时的固定标记：此时服务端可能已完成创建 / 重新生成，提示用户先刷新列表核对。 */
export const FACE_CAMERA_SECRET_MISSING = "FACE_CAMERA_SECRET_MISSING";

/**
 * 新增 / 重新生成的响应。只取 camera 与 cameraUrl；响应里单独的 token 字段不读取、不保留。
 * 缺地址时抛带固定错误码的错误（消息里不含任何响应内容）。
 */
export function normalizeFaceCameraSecretResult(value: unknown): FaceCameraSecretResult {
  const raw = value && typeof value === "object" ? (value as Record<string, unknown>) : {};
  const cameraUrl = typeof raw.cameraUrl === "string" ? raw.cameraUrl.trim() : "";
  const camera = normalizeFaceCamera(raw.camera);
  if (!cameraUrl || !camera.cameraId) {
    throw Object.assign(new Error(FACE_CAMERA_SECRET_MISSING), { code: FACE_CAMERA_SECRET_MISSING });
  }
  return { camera, cameraUrl };
}

const MASK = "••••••••";

/**
 * 脱敏摄像机地址：保留协议 + 域名 + 固定路径前缀，最后一段（token）整体替换为掩码，不保留任何 token 字符。
 * 不是合法地址（缺最后一段）时整体返回掩码，宁可多藏。
 */
export function maskCameraUrl(url: string | null | undefined): string {
  const text = (url ?? "").trim();
  const cut = text.lastIndexOf("/");
  if (!text || cut < 0 || cut === text.length - 1) return MASK;
  return `${text.slice(0, cut + 1)}${MASK}`;
}

/**
 * 一次性展示状态。关闭即整体替换成 CLOSED 常量，内存里不留任何引用。
 */
export type SecretRevealState =
  | { open: false }
  | { open: true; kind: "created" | "rotated"; cameraId: string; cameraUrl: string };

export const SECRET_REVEAL_CLOSED: SecretRevealState = Object.freeze({ open: false });

/** 打开展示框：只从结果里取地址，不接收也不保存 token 本身。 */
export function openSecretReveal(kind: "created" | "rotated", result: FaceCameraSecretResult): SecretRevealState {
  return { open: true, kind, cameraId: result.camera.cameraId, cameraUrl: result.cameraUrl };
}

/** 关闭展示框：无论当前状态，一律回到已清空状态。 */
export function closeSecretReveal(): SecretRevealState {
  return SECRET_REVEAL_CLOSED;
}

/** 状态里是否还持有地址（测试与卸载自检用）。 */
export function holdsSecret(state: SecretRevealState): boolean {
  return state.open && state.cameraUrl.length > 0;
}
