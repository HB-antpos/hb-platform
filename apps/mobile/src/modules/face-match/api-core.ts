// 人脸比对 / 摄像头接口调用（HTTP 客户端由外部注入：生产用 apiClient，测试用假客户端抓取请求）。
//
// 安全约定：
// 1. 响应不含对象键、COS 地址、腾讯云凭据、PersonId、FaceId；图片只能经专用内容接口由服务端转发字节；
// 2. 所有请求带 Cache-Control: no-store（服务端同样返回 no-store），不让系统 HTTP 缓存保留含生物识别信息的响应；
// 3. 图片请求带 Authorization（由 apiClient 拦截器加）取字节，立即转成 base64 data URI 交给调用方放进组件 state，
//    绝不写磁盘、不进 react-query 缓存、不拼进 URL 查询串；图片请求关闭中心日志上报（X-Skip-Center-Log）；
// 4. 列表缩略图走 POST /results/thumbnails（一批最多 30 项、响应是 base64 JPEG）：同样 no-store、不进中心日志，
//    编号只放请求体；返回值只交给调用方放内存（use-thumbnails），绝不进 react-query / 磁盘 / 日志；
// 5. 新增摄像头 / 重新生成令牌的响应带一次性地址（含 token）：请求关闭中心日志上报，且只经 normalizeFaceCameraSecretResult
//    取出地址交给调用方放进组件局部 state（不用 useMutation 的结果缓存、不进 react-query / 磁盘 / 日志）；
// 6. 本文件禁止出现 console.*，由 source-contract 测试静态守住。
import type { AxiosRequestConfig } from "axios";
import { fromByteArray } from "base64-js";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { FACE_CAMERAS_API_BASE, FACE_MATCH_API_BASE, FACE_NOTIFICATIONS_API_BASE, THUMBNAIL_BATCH_MAX } from "./constants";
import type { CreateEntryInput } from "./entry-form";
import { CAMERA_ID_PATTERN } from "./cameras-logic";
import { normalizeFaceCameraSecretResult, type FaceCameraSecretResult } from "./camera-secret";
import { buildCaptureListParams, buildHitListParams, buildHitSummaryParams } from "./capture-logic";
import { buildListParams } from "./entries-logic";
import { FACE_HIT_NOTIFICATION_TYPE } from "./hit-unread";
import { buildCreateEntryMultipart } from "./multipart";
import {
  normalizeFaceCamera,
  normalizeFaceCameraCapturePage,
  normalizeFaceCameraList,
  normalizeFaceCaptureResultPage,
  normalizeFaceHitReadAllResult,
  normalizeFaceHitResultPage,
  normalizeFaceHitSummary,
  normalizeFaceHitUnreadCount,
  normalizeFaceMatchConfig,
  normalizeFaceStoreProfile,
  normalizeFaceThumbnailResults,
  normalizeFaceUnwelcomeEntry,
  normalizeFaceUnwelcomeEntryPage,
  normalizeFaceUnwelcomeTask,
  normalizeFaceUnwelcomeTaskPage,
} from "./normalize";
import type {
  CreateFaceCameraRequest,
  FaceCamera,
  FaceCameraCapturePage,
  FaceCameraList,
  FaceCaptureResult,
  FaceCaptureResultQuery,
  FaceHitReadAllResult,
  FaceHitResult,
  FaceHitResultQuery,
  FaceHitSummary,
  FaceHitSummaryQuery,
  FaceMatchConfig,
  FaceMatchPage,
  FaceReviewVerdict,
  FaceStoreProfile,
  FaceStoreProfileUpdateRequest,
  FaceThumbnailRequestItem,
  FaceThumbnailResult,
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
/** 抓拍编号与图片编号：服务端只接受 1–32 位字母数字（FaceCaptureResultService.IdPattern）。 */
const CAPTURE_PATH_ID = /^[A-Za-z0-9]{1,32}$/;
/** 缩略图项目的编号校验：抓拍编号同抓拍图（1–32 位字母数字），命中任务编号同任务图。不合法的项不发给服务端，当作 denied。 */
function isValidThumbnailItem(item: FaceThumbnailRequestItem): boolean {
  if (item.kind === "capture") return CAPTURE_PATH_ID.test(item.id);
  if (item.kind === "hit") return IMAGE_PATH_TASK_ID.test(item.id);
  return false;
}

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

function failInvalidId(): never {
  throw Object.assign(new Error("FACE_MATCH_INVALID_ID"), { code: "FACE_MATCH_INVALID_ID" });
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

    /** 一人复核：match = 是同一个人（任务已确认）；notMatch = 不是（任务驳回）。判断一次即结束；谁能复核由后端的 canReview 决定。 */
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

    // ───────── 抓拍与比对结果（FaceCapture.Images.View） ─────────
    /** 列表只含结构化数据与图片编号（不含图片内容），可以进 react-query；照片另用 fetchCaptureImage 在详情里按需读取。 */
    async listCaptureResults(query: FaceCaptureResultQuery, signal?: AbortSignal): Promise<FaceMatchPage<FaceCaptureResult>> {
      const response = await http.get(
        `${FACE_MATCH_API_BASE}/results`,
        json({ params: buildCaptureListParams(query), signal }),
      );
      return normalizeFaceCaptureResultPage(data(response));
    },

    /** 抓拍图（人脸小图 / 背景图）：GET /results/{captureId}/images/{imageId}，服务端每次读取都写审计。返回 data URI，调用方只放组件 state。 */
    async fetchCaptureImage(captureId: string, imageId: string, signal?: AbortSignal): Promise<string> {
      if (!CAPTURE_PATH_ID.test(captureId) || !CAPTURE_PATH_ID.test(imageId)) failImageTarget();
      return fetchImageDataUri(`${FACE_MATCH_API_BASE}/results/${captureId}/images/${imageId}`, signal);
    },

    // ───────── 黑名单命中流（FaceCapture.Images.View）：按命中任务查，不依赖留存抓拍 ─────────
    /** 列表只含结构化数据（不含图片内容、对象键），可以进 react-query；命中图另用 fetchHitImage 在详情里按需读取。 */
    async listHits(query: FaceHitResultQuery, signal?: AbortSignal): Promise<FaceMatchPage<FaceHitResult>> {
      const response = await http.get(`${FACE_MATCH_API_BASE}/results/hits`, json({ params: buildHitListParams(query), signal }));
      return normalizeFaceHitResultPage(data(response));
    },

    /** 各状态条数（同样的门店范围与时间条件）。 */
    async getHitSummary(query: FaceHitSummaryQuery, signal?: AbortSignal): Promise<FaceHitSummary> {
      const response = await http.get(`${FACE_MATCH_API_BASE}/results/hits/summary`, json({ params: buildHitSummaryParams(query), signal }));
      return normalizeFaceHitSummary(data(response));
    },

    /** 把当前登录人那条命中通知标为已读（幂等）；返回该类型的剩余未读数，调用方用它写回红色数字缓存。 */
    async markHitRead(taskId: string): Promise<number> {
      if (!IMAGE_PATH_TASK_ID.test(taskId)) failInvalidId();
      return normalizeFaceHitUnreadCount(data(await http.post(`${FACE_MATCH_API_BASE}/results/hits/${taskId}/read`, undefined, json())));
    },

    /** 命中图原图：GET /results/hits/{taskId}/image，服务端每次读取都写审计。返回 data URI，调用方只放组件 state。 */
    async fetchHitImage(taskId: string, signal?: AbortSignal): Promise<string> {
      if (!IMAGE_PATH_TASK_ID.test(taskId)) failImageTarget();
      return fetchImageDataUri(`${FACE_MATCH_API_BASE}/results/hits/${taskId}/image`, signal);
    },

    /**
     * 「全部标为已读」：只标「黑名单命中」类通知（type 查询参数），返回本次条数与该类型剩余未读数。
     * 注意它标的是当前登录人的全部命中通知，不受列表上的门店 / 时间筛选限制。
     */
    async markAllHitsRead(): Promise<FaceHitReadAllResult> {
      const response = await http.post(`${FACE_NOTIFICATIONS_API_BASE}/read-all`, undefined, json({ params: { type: FACE_HIT_NOTIFICATION_TYPE } }));
      return normalizeFaceHitReadAllResult(data(response));
    },

    // ───────── 缩略图（FaceCapture.Images.View）：一批最多 30 项，服务端每个请求只记一条审计 ─────────
    /**
     * 批量取缩略图（最长边 ≤128px 的 JPEG，base64）。结果按入参顺序返回，只含服务端回了的项：
     * - 编号不合法的项不发给服务端，直接当 denied；全都不合法 / 入参为空时不发请求；
     * - 一批超过 30 项属于调用方的编程错误，直接抛错而不是悄悄截断；
     * - 请求带 no-store、不进中心日志；返回值只能放内存（组件 state / ref），不得进 react-query、磁盘或日志。
     */
    async fetchThumbnails(items: readonly FaceThumbnailRequestItem[], signal?: AbortSignal): Promise<FaceThumbnailResult[]> {
      if (items.length === 0) return [];
      if (items.length > THUMBNAIL_BATCH_MAX) {
        throw Object.assign(new Error("FACE_MATCH_THUMBNAIL_BATCH_TOO_LARGE"), { code: "FACE_MATCH_THUMBNAIL_BATCH_TOO_LARGE" });
      }
      const valid = items.filter(isValidThumbnailItem);
      let returned: FaceThumbnailResult[] = [];
      if (valid.length > 0) {
        const response = await http.post(
          `${FACE_MATCH_API_BASE}/results/thumbnails`,
          { items: valid.map(({ kind, id }) => ({ kind, id })) },
          json({ signal, timeout: IMAGE_TIMEOUT_MS, headers: { "X-Skip-Center-Log": "1" } }),
        );
        returned = normalizeFaceThumbnailResults(data(response));
      }
      const byKey = new Map(returned.map((result) => [`${result.kind}:${result.id}`, result]));
      const ordered: FaceThumbnailResult[] = [];
      for (const item of items) {
        if (!isValidThumbnailItem(item)) {
          ordered.push({ kind: item.kind, id: item.id, status: "denied", imageBase64: null });
          continue;
        }
        const found = byKey.get(`${item.kind}:${item.id}`);
        if (found) ordered.push(found);
      }
      return ordered;
    },

    // ───────── 摄像头（FaceCapture.Cameras.Manage）：列表、启停、编辑、新增、重新生成令牌、最近抓拍 ─────────
    async listCameras(signal?: AbortSignal): Promise<FaceCameraList> {
      return normalizeFaceCameraList(data(await http.get(FACE_CAMERAS_API_BASE, json({ signal }))));
    },

    /** PUT 是整体替换：请求体必须由 buildCameraEnabledRequest（只改 enabled）或 buildEditCameraRequest（编辑表单）构造。 */
    async updateCamera(cameraId: string, request: UpdateFaceCameraRequest): Promise<FaceCamera> {
      return normalizeFaceCamera(data(await http.put(cameraPath(cameraId), request, json())));
    },

    /** 新增摄像头；响应里的一次性地址只此一次（库里只存哈希），不进中心日志。 */
    async createCamera(request: CreateFaceCameraRequest): Promise<FaceCameraSecretResult> {
      return normalizeFaceCameraSecretResult(
        data(await http.post(FACE_CAMERAS_API_BASE, request, json({ headers: { "X-Skip-Center-Log": "1" } }))),
      );
    },

    /** 重新生成令牌：旧令牌最多 30 秒后失效，新地址只此一次，不进中心日志。 */
    async rotateCameraToken(cameraId: string): Promise<FaceCameraSecretResult> {
      if (!CAMERA_ID_PATTERN.test(cameraId)) failInvalidId();
      return normalizeFaceCameraSecretResult(
        data(await http.post(`${cameraPath(cameraId)}/rotate-token`, undefined, json({ headers: { "X-Skip-Center-Log": "1" } }))),
      );
    },

    /** 最近抓拍（元数据）；图片元数据只在调用者持有 FaceCapture.Images.View 时才有。 */
    async listCameraCaptures(cameraId: string, page: number, pageSize: number, signal?: AbortSignal): Promise<FaceCameraCapturePage> {
      if (!CAMERA_ID_PATTERN.test(cameraId)) failInvalidId();
      return normalizeFaceCameraCapturePage(
        data(await http.get(`${cameraPath(cameraId)}/captures`, json({ params: { page, pageSize }, signal }))),
      );
    },

    /** 抓拍图片内容（服务端转发字节，需要 FaceCapture.Images.View，每次读取服务端都写审计）；返回 data URI，只放内存。 */
    async fetchCameraCaptureImage(cameraId: string, captureId: string, imageId: string, signal?: AbortSignal): Promise<string> {
      if (!CAMERA_ID_PATTERN.test(cameraId) || !CAPTURE_PATH_ID.test(captureId) || !CAPTURE_PATH_ID.test(imageId)) failImageTarget();
      return fetchImageDataUri(`${cameraPath(cameraId)}/captures/${captureId}/images/${imageId}/content`, signal);
    },
  };
}

export type FaceMatchApi = ReturnType<typeof createFaceMatchApi>;
