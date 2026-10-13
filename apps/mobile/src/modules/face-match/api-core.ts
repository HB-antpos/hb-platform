// 人脸比对 / 摄像头接口调用（HTTP 客户端由外部注入：生产用 apiClient，测试用假客户端抓取请求）。
//
// 安全约定：
// 1. 响应不含对象键、COS 地址、腾讯云凭据、PersonId、FaceId；图片只能经专用内容接口由服务端转发字节；
// 2. 所有请求带 Cache-Control: no-store（服务端同样返回 no-store），不让系统 HTTP 缓存保留含生物识别信息的响应；
// 3. 图片请求带 Authorization（由 apiClient 拦截器加）取字节，立即转成 base64 data URI 交给调用方放进组件 state，
//    绝不写磁盘、不进 react-query 缓存、不拼进 URL 查询串；图片请求关闭中心日志上报（X-Skip-Center-Log）；
// 4. 本文件禁止出现 console.*，由 source-contract 测试静态守住。
import type { AxiosRequestConfig } from "axios";
import { fromByteArray } from "base64-js";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { FACE_CAMERAS_API_BASE, FACE_MATCH_API_BASE } from "./constants";
import type { CreateEntryInput } from "./entry-form";
import { buildListParams } from "./entries-logic";
import { buildCreateEntryMultipart } from "./multipart";
import {
  normalizeFaceCamera,
  normalizeFaceCameraList,
  normalizeFaceMatchConfig,
  normalizeFaceStoreProfile,
  normalizeFaceUnwelcomeEntry,
  normalizeFaceUnwelcomeEntryPage,
  normalizeFaceUnwelcomeTask,
  normalizeFaceUnwelcomeTaskPage,
} from "./normalize";
import type {
  FaceCamera,
  FaceCameraList,
  FaceMatchConfig,
  FaceMatchPage,
  FaceReviewVerdict,
  FaceStoreProfile,
  FaceStoreProfileUpdateRequest,
  FaceUnwelcomeEntry,
  FaceUnwelcomeListQuery,
  FaceUnwelcomeTask,
  UpdateFaceCameraRequest,
} from "./types";

/** 只用到 apiClient 的这三个方法；apiClient 是 AxiosInstance，满足此形状。 */
export interface FaceMatchHttp {
  get(url: string, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
  post(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
  put(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
}

const NO_STORE_HEADERS = { "Cache-Control": "no-store", Pragma: "no-cache" } as const;
const IMAGE_ACCEPT = "image/jpeg, image/png";
/** 提交条目最多 5 张照片、约 25MB 的请求体，移动网络上要给足时间（默认 30 秒不够）。 */
export const CREATE_ENTRY_TIMEOUT_MS = 120_000;
export const IMAGE_TIMEOUT_MS = 30_000;

// 路径里的编号发请求前先校验，避免 ".." 之类被当成路径穿越而指向别的接口（编号来自服务端数据，这是第二道防线）。
const IMAGE_PATH_TASK_ID = /^[A-Za-z0-9-]{1,64}$/;
/** 参考照片最多 5 张（下标 0-4）；多留余量，真实上限以任务的 referencePhotoCount 为准。 */
const MAX_REFERENCE_INDEX = 9;

function profilePath(storeCode: string): string {
  return `${FACE_MATCH_API_BASE}/profiles/${encodeURIComponent(storeCode)}`;
}

function entryPath(entryId: string): string {
  return `${FACE_MATCH_API_BASE}/unwelcome/entries/${encodeURIComponent(entryId)}`;
}

function cameraPath(cameraId: string): string {
  return `${FACE_CAMERAS_API_BASE}/${encodeURIComponent(cameraId)}`;
}

function failImageTarget(): never {
  throw Object.assign(new Error("FACE_MATCH_IMAGE_GONE"), { code: "FACE_MATCH_IMAGE_GONE" });
}

/** 按文件头识别 JPEG / PNG（服务端同样只会返回这两种）；识别不出来的字节一律不当图片显示。 */
export function detectImageMime(bytes: Uint8Array): "image/jpeg" | "image/png" | null {
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  if (
    bytes.length >= 8 &&
    bytes[0] === 0x89 &&
    bytes[1] === 0x50 &&
    bytes[2] === 0x4e &&
    bytes[3] === 0x47 &&
    bytes[4] === 0x0d &&
    bytes[5] === 0x0a &&
    bytes[6] === 0x1a &&
    bytes[7] === 0x0a
  ) {
    return "image/png";
  }
  return null;
}

function toBytes(data: unknown): Uint8Array {
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  return new Uint8Array(0);
}

export function createFaceMatchApi(http: FaceMatchHttp) {
  const json = (config: AxiosRequestConfig = {}): AxiosRequestConfig => ({
    ...config,
    headers: { ...NO_STORE_HEADERS, ...(config.headers as Record<string, string> | undefined) },
  });
  const data = (response: { data: unknown }) => unwrapApiEnvelope<unknown>(response.data);

  async function fetchImageDataUri(path: string, signal?: AbortSignal): Promise<string> {
    const response = await http.get(path, {
      responseType: "arraybuffer",
      timeout: IMAGE_TIMEOUT_MS,
      signal,
      headers: { ...NO_STORE_HEADERS, Accept: IMAGE_ACCEPT, "X-Skip-Center-Log": "1" },
    });
    const bytes = toBytes(response.data);
    const mime = detectImageMime(bytes);
    if (!mime) {
      throw Object.assign(new Error("FACE_MATCH_IMAGE_UNSUPPORTED"), { code: "FACE_MATCH_IMAGE_UNSUPPORTED" });
    }
    return `data:${mime};base64,${fromByteArray(bytes)}`;
  }

  return {
    // ───────── 配置与权限标志（持有任一 FaceMatch.* 权限即可读取；仅有摄像机管理权限会得到 403） ─────────
    async getConfig(signal?: AbortSignal): Promise<FaceMatchConfig> {
      return normalizeFaceMatchConfig(data(await http.get(`${FACE_MATCH_API_BASE}/config`, json({ signal }))));
    },

    // ───────── 门店档案（FaceMatch.Manage） ─────────
    async getProfile(storeCode: string, signal?: AbortSignal): Promise<FaceStoreProfile> {
      return normalizeFaceStoreProfile(data(await http.get(profilePath(storeCode), json({ signal }))));
    },

    /** PUT 是整体替换：请求体必须由 buildProfileUpdateRequest 构造（手机不编辑的字段原样带回）。 */
    async updateProfile(storeCode: string, request: FaceStoreProfileUpdateRequest): Promise<FaceStoreProfile> {
      return normalizeFaceStoreProfile(data(await http.put(profilePath(storeCode), request, json())));
    },

    // ───────── 不受欢迎人员名单：条目 ─────────
    async listEntries(query: FaceUnwelcomeListQuery, signal?: AbortSignal): Promise<FaceMatchPage<FaceUnwelcomeEntry>> {
      const response = await http.get(
        `${FACE_MATCH_API_BASE}/unwelcome/entries`,
        json({ params: buildListParams(query), signal }),
      );
      return normalizeFaceUnwelcomeEntryPage(data(response));
    },

    /** multipart（内存拼装，不落盘）：字段名与 Web 一致，见 multipart.ts。 */
    async createEntry(input: CreateEntryInput, signal?: AbortSignal): Promise<FaceUnwelcomeEntry> {
      const { body, contentType } = buildCreateEntryMultipart(input);
      const response = await http.post(`${FACE_MATCH_API_BASE}/unwelcome/entries`, body, {
        headers: { ...NO_STORE_HEADERS, "Content-Type": contentType },
        timeout: CREATE_ENTRY_TIMEOUT_MS,
        // 请求体已经是最终字节，禁止 axios 再做任何序列化。
        transformRequest: [(payload: unknown) => payload],
        signal,
      });
      return normalizeFaceUnwelcomeEntry(data(response));
    },

    async approveEntry(entryId: string): Promise<FaceUnwelcomeEntry> {
      return normalizeFaceUnwelcomeEntry(data(await http.post(`${entryPath(entryId)}/approve`, undefined, json())));
    },

    async withdrawEntry(entryId: string, reason: string): Promise<FaceUnwelcomeEntry> {
      return normalizeFaceUnwelcomeEntry(data(await http.post(`${entryPath(entryId)}/withdraw`, { reason }, json())));
    },

    async setDisputeNote(entryId: string, note: string): Promise<FaceUnwelcomeEntry> {
      return normalizeFaceUnwelcomeEntry(data(await http.post(`${entryPath(entryId)}/dispute-note`, { note }, json())));
    },

    // ───────── 命中复核 ─────────
    async listTasks(query: FaceUnwelcomeListQuery, signal?: AbortSignal): Promise<FaceMatchPage<FaceUnwelcomeTask>> {
      const response = await http.get(
        `${FACE_MATCH_API_BASE}/unwelcome/tasks`,
        json({ params: buildListParams(query), signal }),
      );
      return normalizeFaceUnwelcomeTaskPage(data(response));
    },

    /** 两人复核：match = 是同一个人；notMatch = 不是（任何一方 notMatch 即驳回）。第二人必须不同于第一人，由后端强制。 */
    async reviewTask(taskId: string, verdict: FaceReviewVerdict): Promise<FaceUnwelcomeTask> {
      const path = `${FACE_MATCH_API_BASE}/unwelcome/tasks/${encodeURIComponent(taskId)}/review`;
      return normalizeFaceUnwelcomeTask(data(await http.post(path, { verdict }, json())));
    },

    /** 命中抓拍：GET /unwelcome/tasks/{id}/image（需 Review 权限，服务端写审计）。返回 data URI，调用方只放组件 state。 */
    async fetchTaskImage(taskId: string, signal?: AbortSignal): Promise<string> {
      if (!IMAGE_PATH_TASK_ID.test(taskId)) failImageTarget();
      return fetchImageDataUri(`${FACE_MATCH_API_BASE}/unwelcome/tasks/${taskId}/image`, signal);
    },

    /** 条目参考照片：GET /unwelcome/tasks/{id}/reference/{index}（index 从 0 起，张数见任务的 referencePhotoCount）。 */
    async fetchReferenceImage(taskId: string, index: number, signal?: AbortSignal): Promise<string> {
      if (!IMAGE_PATH_TASK_ID.test(taskId) || !Number.isInteger(index) || index < 0 || index > MAX_REFERENCE_INDEX) {
        failImageTarget();
      }
      return fetchImageDataUri(`${FACE_MATCH_API_BASE}/unwelcome/tasks/${taskId}/reference/${index}`, signal);
    },

    // ───────── 摄像头（FaceCapture.Cameras.Manage）：只读列表与启停 ─────────
    async listCameras(signal?: AbortSignal): Promise<FaceCameraList> {
      return normalizeFaceCameraList(data(await http.get(FACE_CAMERAS_API_BASE, json({ signal }))));
    },

    /** PUT 是整体替换：请求体必须由 buildCameraEnabledRequest 构造（只改 enabled，其余原样带回）。 */
    async updateCamera(cameraId: string, request: UpdateFaceCameraRequest): Promise<FaceCamera> {
      return normalizeFaceCamera(data(await http.put(cameraPath(cameraId), request, json())));
    },
  };
}

export type FaceMatchApi = ReturnType<typeof createFaceMatchApi>;
