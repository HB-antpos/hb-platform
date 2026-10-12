// 门店人脸抓拍摄像机管理接口的前端类型（后端契约：/api/react/v1/face-cameras，权限 FaceCapture.Cameras.Manage）。
// 所有 *Utc 字段都是带 Z 的 UTC ISO 字符串；cameraEventTime 是摄像机自带偏移的 ISO 字符串。

export interface FaceCamera {
  cameraId: string
  storeCode: string
  storeName: string | null
  deviceModel: string | null
  macAddress: string | null
  note: string | null
  enabled: boolean
  createdAtUtc: string
  updatedAtUtc: string
  lastSeenAtUtc: string | null
  /** 服务端已算好：启用且 onlineWindowMinutes 内有推送。 */
  online: boolean
  captureCount24h: number
  faceCount24h: number
  parseFailureCount24h: number
  lastEventAtUtc: string | null
}

export interface FaceCameraList {
  items: FaceCamera[]
  generatedAtUtc: string
  onlineWindowMinutes: number
  statsWindowHours: number
}

/** 新增 / 编辑共用的字段（已规范化：trim、空串转 null）。 */
export interface FaceCameraFields {
  storeCode: string
  note: string | null
  deviceModel: string | null
  macAddress: string | null
}

export interface CreateFaceCameraRequest extends FaceCameraFields {
  cameraId: string
}

export interface UpdateFaceCameraRequest extends FaceCameraFields {
  enabled: boolean
}

/**
 * 新增 / 轮换 token 的响应。token 与 cameraUrl 只在这一次响应里出现，
 * 只能放在组件局部 state，关闭展示框后立即清空；不得写入任何缓存、存储、日志或错误对象。
 */
export interface FaceCameraSecretResult {
  camera: FaceCamera
  token: string
  cameraUrl: string
}

/** 0 Parsed / 1 PartiallyParsed / 2 Unparsed / 3 NotFaceEvent。 */
export type FaceCaptureParseStatus = 0 | 1 | 2 | 3

/** 0 人脸图 / 1 背景图 / 2 其他。 */
export type FaceCaptureImageKind = 0 | 1 | 2

/** 人脸框：rectNormalized 为 true 时是 0~1 的比例，否则是像素（相对背景图原始宽高）。 */
export interface FaceCaptureImageRect {
  x: number
  y: number
  w: number
  h: number
}

/**
 * 单张图片的元数据。没有对象键、COS 地址、哈希：图片字节只能经
 * GET .../captures/{captureId}/images/{imageId}/content 由服务端转发，且需要权限 FaceCapture.Images.View。
 */
export interface FaceCaptureImageMeta {
  imageId: string
  kind: FaceCaptureImageKind
  /** cos | local；local 表示图片仍在接收服务本地磁盘，后台无法读取。 */
  storage: string
  width: number | null
  height: number | null
  byteSize: number
  faceIndex: number | null
  rect: FaceCaptureImageRect | null
  rectNormalized: boolean | null
  faceScore: number | null
}

export interface FaceCapture {
  captureId: string
  receivedAtUtc: string
  cameraEventTime: string | null
  eventType: string | null
  faceCount: number
  parseStatus: FaceCaptureParseStatus
  parseNote: string | null
  isDuplicate: boolean
  /** 仅当页面级 canViewImages 为 true 时才可能非空。 */
  images: FaceCaptureImageMeta[]
}

export interface FaceCapturePage {
  items: FaceCapture[]
  page: number
  pageSize: number
  hasMore: boolean
  /** 调用者是否持有 FaceCapture.Images.View；为 false 时每条记录的 images 都是空数组。 */
  canViewImages: boolean
}
