// 人脸比对模块的源码契约测试：
// 1. 隐私：参考照片 / 命中抓拍不写磁盘缓存、不进日志与诊断上报、不进 react-query 缓存、不拼进 URL 查询串；
// 2. 范围：只调用约定内的接口，接口路径与权限码和后端、Web 保持一致；摄像头的新增 / 重新生成令牌只在一次性展示里碰令牌（见下）；
// 3. 文案：页面不硬编码中文，代码里用到的每个文案键中英文都有，且插值参数一致；
// 4. 路由薄壳只做转发。
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { HIT_STATUS_KEYS } from "./capture-logic";
import { PROFILE_TIME_ZONES } from "./constants";
import { FACE_MATCH_ERROR_CODES, STATUS_ERROR_CODES } from "./errors";
import { normalizeFaceCaptureResult, normalizeFaceHitResult, normalizeFaceHitSummary, normalizeFaceThumbnailResults } from "./normalize";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const repoRoot = resolve(mobileRoot, "../..");

function listFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const full = join(directory, name);
    return statSync(full).isDirectory() ? listFiles(full) : [full];
  });
}

const read = (path: string) => readFileSync(path, "utf8");
const isTest = (name: string) => /\.test\.tsx?$/.test(name);
const moduleFiles = listFiles(moduleDir).filter((file) => /\.tsx?$/.test(file) && !isTest(file));
const sourceOf = (name: string) => read(join(moduleDir, name));

function stripComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
}

// ───────── 1. 隐私：不落盘、不进日志与上报、不进缓存 ─────────
{
  const FORBIDDEN_STORAGE = [
    "writeAsStringAsync",
    "copyAsync",
    "moveAsync",
    "downloadAsync",
    "createDownloadResumable",
    "AsyncStorage",
    "AppAsyncStorage",
    "SecureStorage",
    "SecureStore",
    "MMKV",
    "localStorage",
    "sessionStorage",
    "expo-sqlite",
    "expo-media-library",
    "createAssetAsync",
    "saveToLibraryAsync",
    "expo-sharing",
    "Share.share",
  ];
  const FORBIDDEN_LOGGING = [
    "console.",
    "reportApplicationLog",
    "reportExternalFetchFailure",
    "log-center",
    "Sentry",
    "crashlytics",
    "analytics",
  ];
  const FORBIDDEN_IMAGE_CACHES = ["Image.prefetch", "Image.queryCache", "\"expo-image\"", "FastImage", "react-native-fast-image", "cache: \"force-cache\"", "cache: 'force-cache'", "cache: \"reload\"", "cache: 'reload'", "cache: \"default\""];
  // 唯一例外：thumbnail-preference.ts 用 AsyncStorage 只存一个布尔偏好（「缩略图是否显示」），不含任何人脸数据。
  const isPreferenceFile = (file: string) => /thumbnail-preference\.ts$/.test(file);
  for (const file of moduleFiles) {
    const code = stripComments(read(file));
    for (const term of [...FORBIDDEN_STORAGE, ...FORBIDDEN_LOGGING, ...FORBIDDEN_IMAGE_CACHES]) {
      if (isPreferenceFile(file) && (term === "AsyncStorage" || term === "AppAsyncStorage")) continue;
      assert.equal(code.includes(term), false, `${file} 不应出现 ${term}（生物识别照片不得落盘 / 进日志 / 进缓存）`);
    }
  }
  {
    // 偏好文件只存「缩略图是否显示」这一个布尔值：固定的键、只读写字符串 "1" / "0"，不碰对象存取，也不接触任何缩略图字节
    const preference = stripComments(sourceOf("thumbnail-preference.ts"));
    assert.ok(preference.includes('THUMBNAILS_VISIBLE_STORAGE_KEY = "faceMatch.thumbnails.visible"'));
    assert.ok(preference.includes('from "@/shared/storage/async-storage"') && !preference.includes("@react-native-async-storage"));
    assert.equal(/getObject|setObject|JSON\.stringify|base64|uri|data:/i.test(preference), false, "偏好文件不应处理对象 / 图片内容");
    assert.ok(preference.includes('visible ? "1" : "0"'));
    // 除偏好文件外，没有任何文件能引用偏好文件之外的存储（上面的循环已守住）；偏好只被钩子文件使用
    for (const file of moduleFiles) {
      if (/thumbnail-preference\.ts$|thumbnail-preference\.test\.ts$/.test(file)) continue;
      if (stripComments(read(file)).includes("thumbnail-preference")) {
        assert.ok(/use-thumbnails\.ts$/.test(file), `${file} 不应直接读写缩略图偏好（只有 use-thumbnails 钩子使用）`);
      }
    }
  }

  // 本模块里唯一碰文件系统的地方：photo-runtime 只删应用缓存目录里的临时文件，且必须校验目录前缀
  const runtime = stripComments(sourceOf("photo-runtime.ts"));
  assert.ok(runtime.includes("deleteAsync"), "压缩后必须删除选图器 / 压缩器留下的临时文件");
  assert.ok(runtime.includes("isAppCacheFile(uri, fileSystem.cacheDirectory)"), "只允许删除应用缓存目录里的文件");
  assert.equal(runtime.includes("writeAsStringAsync"), false);
  assert.equal(/FileSystem\.documentDirectory|documentDirectory/.test(runtime), false, "不得写文档目录");
  // 压缩时要求返回 base64（字节留在内存），并统一输出 JPEG
  assert.ok(runtime.includes("base64: true"));
  assert.ok(runtime.includes("SaveFormat.JPEG"));

  // 选图：不要 EXIF、不让选图器编码 base64；HEIC 在选取时转兼容格式
  const picker = stripComments(sourceOf("PhotoPickerField.tsx"));
  assert.ok(picker.includes("exif: false"));
  assert.ok(picker.includes("base64: false"));
  assert.ok(picker.includes("UIImagePickerPreferredAssetRepresentationMode.Compatible"));
  assert.ok(picker.includes("launchCameraAsync") && picker.includes("launchImageLibraryAsync"));
  assert.ok(picker.includes("discardPickedAsset"), "超出张数的选中资源也要删临时文件");

  // 图片 Image 的来源只能是内存里的 data URI / state，不是后端地址
  const modal = stripComments(sourceOf("ReviewTaskModal.tsx"));
  assert.ok(modal.includes("source={{ uri: state.uri }}"));
  assert.equal(/uri:\s*["'`]https?:/.test(modal), false);
  assert.ok(modal.includes("AppState.addEventListener"), "离开前台时必须丢弃图片");
  assert.ok(modal.includes("next !== \"active\""), "inactive（应用切换器快照前）与 background 一样处理");
  assert.ok(modal.includes("session.dispose()"));
  assert.ok(modal.includes("fetchTaskImage") && modal.includes("fetchReferenceImage"));

  // iOS 系统网络层会把响应写进缓存目录（实测 no-store 也写）：两类图片读取都要经清理器，关闭时释放
  assert.ok(modal.includes("createReviewImageScrubber()"));
  assert.ok(/scrubber\.wrap\(\(\) => faceMatchApi\.fetchTaskImage\(/.test(modal), "抓拍图读取须经清理器");
  assert.ok(/scrubber\.wrap\(\(\) => faceMatchApi\.fetchReferenceImage\(/.test(modal), "参考照片读取须经清理器");
  assert.ok(modal.includes("scrubber.release()"), "弹框关闭须释放清理器");
  const scrubRuntime = stripComments(sourceOf("http-cache-scrub-runtime.ts"));
  assert.ok(scrubRuntime.includes('Platform.OS !== "ios"'), "清理器只在 iOS 启用");
  assert.equal(/writeAsStringAsync|copyAsync|moveAsync|documentDirectory/.test(scrubRuntime), false, "清理器只读 / 删，不写");
  assert.ok(/readAsStringAsync\(uri,[^)]*length/.test(scrubRuntime), "清理器只读文件头几个字节");
  const scrubCore = stripComments(sourceOf("http-cache-scrub.ts"));
  assert.ok(scrubCore.includes('"fsCachedData"'), "只清理系统 HTTP 缓存的数据目录");

  // 图片读取函数只允许复核弹框调用：不进 react-query（会进缓存）、也不被别的页面预加载
  for (const file of moduleFiles) {
    const code = stripComments(read(file));
    if (/fetchTaskImage|fetchReferenceImage/.test(code)) {
      assert.ok(
        /ReviewTaskModal\.tsx$|api-core\.ts$/.test(file),
        `${file} 不应读取复核图片（只有复核弹框在打开后按需读取）`,
      );
    }
  }
  // 抓拍图读取函数同理：只有抓拍详情在打开后按需读取，列表 / 钩子 / 其他页面都不碰
  for (const file of moduleFiles) {
    const code = stripComments(read(file));
    if (/fetchCaptureImage/.test(code)) {
      assert.ok(
        /CaptureResultDetail\.tsx$|api-core\.ts$/.test(file),
        `${file} 不应读取抓拍图（只有抓拍详情在打开后按需读取）`,
      );
    }
  }
  const detail = stripComments(sourceOf("CaptureResultDetail.tsx"));
  assert.ok(detail.includes("source={{ uri: state.uri }}"), "抓拍图 Image 的来源只能是内存里的 data URI");
  assert.equal(/uri:\s*["'`]https?:/.test(detail), false);
  assert.ok(detail.includes("AppState.addEventListener") && detail.includes('next !== "active"'), "离开前台（含 inactive）必须关闭详情并丢弃图片");
  assert.ok(detail.includes("session.dispose()"));
  assert.ok(detail.includes("createReviewImageScrubber()") && detail.includes("scrubber.release()"), "抓拍图读取也要经 iOS 缓存清理器并在关闭时释放");
  assert.ok(/scrubber\.wrap\(\(\) => faceMatchApi\.fetchCaptureImage\(/.test(detail), "抓拍图读取须经清理器");
  assert.equal(/useQuery|useMutation|useInfiniteQuery/.test(detail), false, "抓拍详情不用 react-query 取图");
  // 列表里的真实图片只有 128px 缩略图：<Image> 只允许出现在 CaptureThumb.tsx，来源只能是内存里的 data URI；
  // 列表屏与卡片都不直接渲染图片，整图（人脸 / 背景 / 命中图）只在详情里按需读取。
  for (const name of ["FaceCaptureResultsScreen.tsx", "CaptureCard.tsx"]) {
    assert.equal(/<Image\b|source=\{\{/.test(stripComments(sourceOf(name))), false, `${name} 不直接渲染图片（缩略图统一经 CaptureThumb）`);
  }
  const thumbView = stripComments(sourceOf("CaptureThumb.tsx"));
  assert.ok(thumbView.includes("source={{ uri: display.uri }}"), "缩略图 Image 的来源只能是内存里的 data URI");
  assert.equal(/uri:\s*["'`]https?:/.test(thumbView), false);
  assert.equal(/faceMatchApi|from "\.\/api"|useQuery|useInfiniteQuery|QueryClient|react-query/.test(thumbView), false, "缩略图组件不取数、不进 react-query");
  const captureSession = stripComments(sourceOf("capture-image-session.ts"));
  assert.ok(captureSession.includes("controller.abort()"), "关闭时中止在途请求");
  const hooks = stripComments(sourceOf("use-face-match.ts"));
  assert.equal(/fetchCaptureImage|capture-image-session/.test(hooks), false, "抓拍图字节不得经过 react-query 钩子");
  assert.equal(/fetchTaskImage|fetchReferenceImage|image-session/.test(hooks), false, "图片字节不得经过 react-query 钩子");
  assert.equal(/useQuery|useMutation|useInfiniteQuery/.test(modal), false, "复核弹框不用 react-query 取图");

  // 命中图整图读取：只有详情在打开后按需读取（经清理器），列表 / 钩子 / 其他页面都不碰
  for (const file of moduleFiles) {
    const code = stripComments(read(file));
    if (/fetchHitImage/.test(code)) {
      assert.ok(/CaptureResultDetail\.tsx$|api-core\.ts$/.test(file), `${file} 不应读取命中图（只有详情在打开后按需读取）`);
    }
  }
  assert.ok(/scrubber\.wrap\(\(\) => faceMatchApi\.fetchHitImage\(/.test(detail), "命中图读取须经清理器");
  assert.equal(/fetchHitImage|fetchThumbnails|thumbnail-loader|use-thumbnails|thumbnail-preference/.test(hooks), false, "命中图 / 缩略图字节不得经过 react-query 钩子");

  // 缩略图：只放内存（组件 state / 加载器的 Map），不走 react-query、不落盘、不进日志；批请求只有缩略图钩子发起
  for (const file of moduleFiles) {
    const code = stripComments(read(file));
    if (/fetchThumbnails/.test(code)) {
      assert.ok(/use-thumbnails\.ts$|api-core\.ts$/.test(file), `${file} 不应发起缩略图批请求（只有 use-thumbnails 钩子）`);
    }
  }
  for (const name of ["thumbnail-loader.ts", "use-thumbnails.ts", "CaptureThumb.tsx", "CaptureCard.tsx"]) {
    const code = stripComments(sourceOf(name));
    assert.equal(/useQuery|useMutation|useInfiniteQuery|useQueryClient|QueryClient|@tanstack\/react-query/.test(code), false, `${name} 不用 react-query（缩略图不得进查询缓存）`);
    assert.equal(/expo-file-system|FileSystem\b|writeAsStringAsync|documentDirectory|cacheDirectory/.test(code), false, `${name} 不碰文件系统（缩略图只放内存）`);
  }
  const loader = stripComments(sourceOf("thumbnail-loader.ts"));
  assert.ok(loader.includes("controller?.abort()") && loader.includes("batchGeneration !== generation"), "清空 / 销毁时中止在途请求，迟到的响应丢弃");
  assert.ok(loader.includes("THUMBNAIL_BATCH_MAX"), "一批最多 30 项");
  const thumbHook = stripComments(sourceOf("use-thumbnails.ts"));
  assert.ok(thumbHook.includes("AppState.addEventListener") && thumbHook.includes('next !== "active"'), "切后台（含 inactive）立即清空缩略图");
  assert.ok(thumbHook.includes("loader.dispose()") && thumbHook.includes("loaderRef.current?.clear()"), "卸载 / 隐藏 / 切后台都销毁缩略图");
  // 列表屏：只加载屏幕可见的行（onViewableItemsChanged），「眼睛」隐藏时先同步清空；失败只在下拉刷新时重试
  const listCode = stripComments(sourceOf("FaceCaptureResultsScreen.tsx"));
  assert.ok(listCode.includes("onViewableItemsChanged") && listCode.includes("collectThumbnailTargets"), "只加载屏幕里看得到的行");
  assert.ok(/if \(thumbsVisible\) thumbs\.clear\(\)/.test(listCode), "点「眼睛」隐藏时同步清空缩略图并中止请求");
  assert.ok(/thumbs\.retryFailed\(\)/.test(listCode), "失败的缩略图在下拉刷新时重试");
  assert.ok(/useIsFocused\(\)/.test(listCode), "离开页面（失焦）销毁缩略图");
  assert.ok(/hit\?\.unread[\s\S]{0,60}markRead\(/.test(listCode), "打开未读的命中详情时标记已读（失败静默）");

  // 请求层：no-store、图片请求不进中心日志、图片地址不带查询串
  const core = stripComments(sourceOf("api-core.ts"));
  assert.ok(core.includes('"Cache-Control": "no-store"'));
  assert.ok(core.includes('"X-Skip-Center-Log": "1"'));
  assert.ok(core.includes('responseType: "arraybuffer"'));
  assert.equal(/\/image\?|\/reference\/\$\{index\}\?/.test(core), false, "图片地址不得带查询串");
  assert.equal(/params:/.test(core.slice(core.indexOf("async function fetchImageDataUri"), core.indexOf("return {", core.indexOf("async function fetchImageDataUri")))), false);

  // 缩略图批请求：编号只放请求体（不拼 URL）、no-store（经 json()）、不进中心日志、有超时；一批 ≤ 30 项
  {
    const start = core.indexOf("async fetchThumbnails");
    const thumbnailCall = core.slice(start, core.indexOf("// ─────────", start));
    assert.ok(start > 0 && thumbnailCall.includes("`${FACE_MATCH_API_BASE}/results/thumbnails`"));
    assert.ok(thumbnailCall.includes('"X-Skip-Center-Log": "1"') && thumbnailCall.includes("json({"), "缩略图批请求不进中心日志且带 no-store");
    assert.ok(thumbnailCall.includes("IMAGE_TIMEOUT_MS") && thumbnailCall.includes("THUMBNAIL_BATCH_MAX"));
    assert.equal(/http\.post\(\s*(`[^`]*`)/.exec(thumbnailCall)?.[1], "`${FACE_MATCH_API_BASE}/results/thumbnails`", "缩略图编号只放请求体，不拼进 URL");
    assert.ok(/export const THUMBNAIL_BATCH_MAX = 30;/.test(stripComments(sourceOf("constants.ts"))));
  }

  // 提交条目：请求体是内存里的字节，不用 RN 的 FormData（它要求文件落盘）
  assert.equal(/new FormData|FormData\(/.test(core), false);
  assert.ok(core.includes("buildCreateEntryMultipart"));
  const multipart = stripComments(sourceOf("multipart.ts"));
  assert.equal(/new FormData|FormData\(/.test(multipart), false);

  // 日志 / 诊断不记录条目内容：提交与图片代码里没有把 evidenceRef / base64 交给任何 logger（上面 console./log-center 已守住），
  // 摄像头列表归一化不保留令牌 / 地址
  const normalize = stripComments(sourceOf("normalize.ts"));
  assert.equal(/\btoken\b\s*:|cameraUrl\s*:/.test(normalize), false);
}

// ───────── 2b. 摄像头一次性地址（含 token）与最近抓拍的隐私约束 ─────────
{
  const secretDialog = stripComments(sourceOf("CameraSecretDialog.tsx"));
  const secretLogic = stripComments(sourceOf("camera-secret.ts"));
  const section = stripComments(sourceOf("CamerasSection.tsx"));
  const capturesModal = stripComments(sourceOf("CameraCapturesModal.tsx"));
  const formModal = stripComments(sourceOf("CameraFormModal.tsx"));

  // 地址不写任何存储 / 路由 / 日志
  for (const [name, code] of [["CameraSecretDialog", secretDialog], ["camera-secret", secretLogic]] as const) {
    for (const forbidden of ["AsyncStorage", "SecureStore", "MMKV", "writeAsStringAsync", "console.", "reportApplicationLog", "router.", "useLocalSearchParams", "queryClient", "useMutation", "setQueryData"]) {
      assert.equal(code.includes(forbidden), false, `${name} 不应出现 ${forbidden}`);
    }
  }
  // 新增 / 重新生成令牌的结果不能进 useMutation 的结果缓存，只放局部 state
  {
    // 用「useMutation 到下一段代码」的整段切片判断（解构里的花括号会让单行正则漏判）
    const mutationBlock = section.slice(section.indexOf("useMutation({"), section.indexOf("const list = camerasQuery.data"));
    assert.ok(mutationBlock.length > 0 && mutationBlock.includes("updateCamera("), "启停走 useMutation（不含令牌）");
    assert.equal(/createCamera|rotateCameraToken|cameraUrl/.test(mutationBlock), false, "新增 / 重新生成令牌不走 useMutation（结果会留在 mutation 缓存里）");
  }
  assert.ok(section.includes("faceMatchApi.createCamera(") && section.includes("faceMatchApi.rotateCameraToken("));
  assert.ok(section.includes("useState<SecretRevealState>(SECRET_REVEAL_CLOSED)"), "地址只放局部 state，初始为已清空常量");
  assert.ok(section.includes("setSecret(closeSecretReveal())"), "关闭即换成已清空常量");
  assert.equal(section.includes("onNotify(") && /onNotify\([^)]*cameraUrl/.test(section), false, "提示文案里不放地址");
  // 弹窗：离开前台自动关闭；安卓返回键不关闭；默认掩码；剪贴板定时清空
  assert.ok(secretDialog.includes("AppState.addEventListener") && secretDialog.includes("closeRef.current()"), "离开前台自动关闭并清空");
  assert.ok(secretDialog.includes("onRequestClose={() => undefined}"), "安卓返回键不关闭（地址只显示一次）");
  assert.ok(secretDialog.includes("maskCameraUrl(state.cameraUrl)"), "默认掩码显示");
  assert.ok(secretDialog.includes("scheduleClipboardClear(") && secretDialog.includes("CLIPBOARD_CLEAR_DELAY_MS"), "复制后定时清空剪贴板");
  assert.equal(/selectable=\{true\}|selectable\b(?!=)/.test(secretDialog), false, "掩码状态下地址不可选中（只在显示时可选）");

  // 表单不接触令牌；抓拍弹框：图片只在查看器 state，离开前台丢弃，读取后清理 iOS 缓存文件
  assert.equal(/cameraUrl|rotateCameraToken|createCamera/.test(formModal), false, "表单组件不接触地址 / 令牌");
  assert.ok(capturesModal.includes("createReviewImageScrubber()") && capturesModal.includes("scrubber.wrap("), "抓拍图片读取后清理系统缓存文件");
  assert.ok(capturesModal.includes("AppState.addEventListener") && capturesModal.includes("controller.abort()"), "离开前台关闭查看器并中止请求");
  assert.equal(/useQuery\(|useInfiniteQuery\(/.test(capturesModal.slice(capturesModal.indexOf("function CaptureImageViewer"))), false, "图片查看器不用 react-query（图片字节不进缓存）");
  assert.ok(capturesModal.includes("hasViewableImages(capture)") && capturesModal.includes("canViewImages &&"), "只有服务端 canViewImages 为真且有可看的图才显示入口");

  // 新增成功：一次性地址作为覆盖层叠在表单 Modal 里，不能「先关表单 Modal 再开地址 Modal」（iOS 同刻切换原生 Modal 会丢弹窗，令牌就没了）
  {
    const createStart = section.indexOf("faceMatchApi.createCamera(");
    const createEnd = section.indexOf("} else {", createStart);
    assert.ok(createStart > 0 && createEnd > createStart);
    assert.equal(section.slice(createStart, createEnd).includes("setForm(null)"), false, "新增成功后不关表单（地址叠在表单里，关地址时再一起收起）");
    assert.ok(section.includes("overlay={") && section.includes("<CameraSecretPanel"), "地址作为表单 Modal 的覆盖层");
    assert.ok(section.includes("<CameraSecretModal") && /secret\.open && !form/.test(section), "重新生成令牌（没有表单可叠）才单独开 Modal");
    assert.ok(/onRequestClose=\{\(\) => \(busy \|\| overlay \? undefined : onClose\(\)\)\}/.test(formModal), "有地址覆盖层时表单不响应返回键");
    // 失败路径无条件刷新列表：响应丢了 / 缺地址时服务端可能已建好摄像头
    assert.ok(/setFormError\(describeError\(error\)\);\s*void refreshList\(\);/.test(section), "新增失败也刷新列表");
  }
  // 备注单行（后端拒绝控制字符，换行进不去）
  assert.equal(formModal.includes("multiline"), false, "摄像头表单不用多行输入");
  // 抓拍查询：关闭即丢缓存（gcTime: 0），刷新只重取第一页
  {
    const hooks = stripComments(sourceOf("use-face-match.ts"));
    const start = hooks.indexOf("export function useFaceCameraCaptures");
    const body = hooks.slice(start, hooks.indexOf("export function useFaceCameras(", start));
    assert.ok(body.includes("gcTime: 0"), "抓拍查询关闭即丢缓存");
    assert.ok(body.includes("data.pages.slice(0, 1)"), "刷新先截到第一页，避免逐页重发");
  }
  // 抓拍弹框的页脚：先判「加载下一页失败」再判「有下一页」（失败后 hasNextPage 仍为真，顺序反了错误提示永远走不到）
  assert.ok(capturesModal.indexOf("query.isFetchNextPageError") < capturesModal.indexOf("query.hasNextPage"), "页脚先判加载下一页失败");
  // 图片读不到：区分「已清理」（后端 FACE_IMAGE_* / 404 / 410）与可重试的失败
  assert.ok(capturesModal.includes("isCameraImageGone(error)") && capturesModal.includes("retry(selectedId)"), "已清理与可重试要区分");
}

// ───────── 2. 范围与契约 ─────────
{
  const core = stripComments(sourceOf("api-core.ts"));
  // 摄像头新增 / 重新生成令牌：响应带一次性地址（含 token），请求必须 no-store 且不进中心日志，
  // 响应只经 normalizeFaceCameraSecretResult 取出地址；没有任何「读取 / 修改已有令牌」的接口。
  {
    const start = core.indexOf("async createCamera");
    const end = core.indexOf("async listCameraCaptures");
    const secretCalls = core.slice(start, end);
    assert.ok(start > 0 && end > start, "api-core 应包含新增与重新生成令牌的方法");
    assert.equal((secretCalls.match(/"X-Skip-Center-Log": "1"/g) ?? []).length, 2, "新增与重新生成令牌的请求都不进中心日志");
    assert.equal((secretCalls.match(/json\(\{ headers:/g) ?? []).length, 2, "两个请求都经 json()（带 no-store）");
    assert.equal((secretCalls.match(/normalizeFaceCameraSecretResult\(/g) ?? []).length, 2, "响应只经 normalizeFaceCameraSecretResult 取地址");
    assert.ok(secretCalls.includes("http.post(FACE_CAMERAS_API_BASE, request,"), "新增走 POST 摄像头集合");
    assert.ok(secretCalls.includes("/rotate-token`"), "重新生成令牌走 rotate-token");
    // 令牌只出现在这两个响应里：没有 GET 令牌 / 修改令牌的路径
    assert.equal(/\/token\b(?!s)|\/secret|cameraUrl\s*[:=]/i.test(core.replace(secretCalls, "")), false, "api-core 其余部分不接触令牌 / 地址");
  }
  // 抓拍记录与图片：只读，图片地址不带查询串，编号先校验
  assert.ok(core.includes("`${cameraPath(cameraId)}/captures`"));
  assert.ok(core.includes("/captures/${captureId}/images/${imageId}/content"));
  assert.ok(core.includes("CAMERA_ID_PATTERN.test(cameraId)"));

  // 移动端用到的每个接口，后端控制器里都有同样的路由
  const faceMatchController = read(join(repoRoot, "services/backend/BlazorApp.Api/Controllers/React/FaceMatchController.cs"));
  const camerasController = read(join(repoRoot, "services/backend/BlazorApp.Api/Controllers/React/FaceCamerasController.cs"));
  assert.ok(faceMatchController.includes('[Route("api/react/v1/face-match")]'));
  assert.ok(camerasController.includes('[Route("api/react/v1/face-cameras")]'));
  for (const route of [
    '[HttpGet("config")]',
    '[HttpGet("profiles/{storeCode}")]',
    '[HttpPut("profiles/{storeCode}")]',
    '[HttpGet("unwelcome/entries")]',
    '[HttpPost("unwelcome/entries")]',
    '[HttpPost("unwelcome/entries/{entryId}/approve")]',
    '[HttpPost("unwelcome/entries/{entryId}/withdraw")]',
    '[HttpPost("unwelcome/entries/{entryId}/dispute-note")]',
    '[HttpGet("unwelcome/tasks")]',
    '[HttpPost("unwelcome/tasks/{taskId}/review")]',
    '[HttpGet("unwelcome/tasks/{taskId}/image")]',
    '[HttpGet("unwelcome/tasks/{taskId}/reference/{index:int}")]',
    '[HttpGet("results")]',
    '[HttpGet("results/{captureId}/images/{imageId}")]',
    // 合并入口的「黑名单」命中流与缩略图
    '[HttpGet("results/hits")]',
    '[HttpGet("results/hits/summary")]',
    '[HttpPost("results/hits/{taskId}/read")]',
    '[HttpGet("results/hits/{taskId}/image")]',
    '[HttpPost("results/thumbnails")]',
  ]) {
    assert.ok(faceMatchController.includes(route), `后端缺少路由 ${route}`);
  }
  assert.ok(camerasController.includes("[HttpGet]"));
  assert.ok(camerasController.includes('[HttpPut("{cameraId}")]'));
  assert.ok(camerasController.includes("[HttpPost]"), "后端有新增摄像头");
  assert.ok(camerasController.includes('[HttpPost("{cameraId}/rotate-token")]'), "后端有重新生成令牌");
  assert.ok(camerasController.includes('[HttpGet("{cameraId}/captures")]'), "后端有最近抓拍");
  assert.ok(camerasController.includes('[HttpGet("{cameraId}/captures/{captureId}/images/{imageId}/content")]'), "后端有抓拍图片内容");
  assert.ok(camerasController.includes("[Authorize(Policy = Permissions.FaceCapture.Cameras.Manage)]"), "整个摄像头控制器只认 FaceCapture.Cameras.Manage");
  for (const path of [
    "`${FACE_MATCH_API_BASE}/config`",
    "`${FACE_MATCH_API_BASE}/unwelcome/entries`",
    "`${FACE_MATCH_API_BASE}/unwelcome/tasks`",
    "`${entryPath(entryId)}/approve`",
    "`${entryPath(entryId)}/withdraw`",
    "`${entryPath(entryId)}/dispute-note`",
    "/unwelcome/tasks/${encodeURIComponent(taskId)}/review",
    "/unwelcome/tasks/${taskId}/image",
    "/unwelcome/tasks/${taskId}/reference/${index}",
    "`${FACE_MATCH_API_BASE}/results`",
    "/results/${captureId}/images/${imageId}",
    "`${FACE_MATCH_API_BASE}/results/hits`",
    "`${FACE_MATCH_API_BASE}/results/hits/summary`",
    "/results/hits/${taskId}/read",
    "/results/hits/${taskId}/image",
    "`${FACE_MATCH_API_BASE}/results/thumbnails`",
    "`${FACE_NOTIFICATIONS_API_BASE}/read-all`",
  ]) {
    assert.ok(core.includes(path), `api-core 缺少接口 ${path}`);
  }
  assert.ok(core.includes('"/react/v1/face-match"') === false, "基础路径来自 constants");
  const constants = stripComments(sourceOf("constants.ts"));
  assert.ok(constants.includes('"/react/v1/face-match"') && constants.includes('"/react/v1/face-cameras"'));
  // 「全部标为已读」只标命中通知：POST /notifications/read-all?type=face.unwelcome-hit（后端该动作接受 type 查询参数）
  assert.ok(constants.includes('"/react/v1/notifications"'));
  assert.ok(core.includes("params: { type: FACE_HIT_NOTIFICATION_TYPE }"), "全部已读必须带 type 过滤，只标命中通知");
  assert.ok(/export const FACE_HIT_NOTIFICATION_TYPE = "face\.unwelcome-hit";/.test(stripComments(sourceOf("hit-unread.ts"))));
  const notificationsController = read(join(repoRoot, "services/backend/BlazorApp.Api/Controllers/React/NotificationsController.cs"));
  assert.ok(/\[HttpPost\("read-all"\)\][\s\S]{0,160}string\? type/.test(notificationsController), "后端 read-all 必须接受 type 查询参数，否则会把全部通知标为已读");
  // 类型常量与后端 AppNotificationTypes.FaceUnwelcomeHit 一致
  assert.ok(/FaceUnwelcomeHit\s*=\s*"face\.unwelcome-hit"/.test(read(join(repoRoot, "services/backend/BlazorApp.Shared/Models/HBweb/AppNotification.cs"))), "后端命中通知类型常量与 App 不一致");

  // 权限码与后端 Permissions.cs 完全一致（本期不新增权限码）
  const permissions = read(join(repoRoot, "services/backend/BlazorApp.Shared/Constants/Permissions.cs"));
  for (const code of ["FaceMatch.Manage", "FaceMatch.Unwelcome.Manage", "FaceMatch.Unwelcome.Approve", "FaceMatch.Unwelcome.Review", "FaceCapture.Cameras.Manage"]) {
    assert.ok(new RegExp(`=\\s*"${code.replace(/\./g, "\\.")}"`).test(permissions), `后端没有权限码 ${code}`);
  }
  for (const code of ["FaceMatch.Manage", "FaceMatch.Unwelcome.Manage", "FaceMatch.Unwelcome.Approve", "FaceMatch.Unwelcome.Review", "FaceCapture.Cameras.Manage"]) {
    for (const lang of ["zh", "en"]) {
      const labels = JSON.parse(read(join(mobileRoot, `src/locales/${lang}/accessPermissions.json`))) as Record<string, Record<string, unknown>>;
      const table = (labels.permissions ?? labels) as Record<string, unknown>;
      assert.ok(JSON.stringify(table).includes(`"${code}"`), `移动端 ${lang}/accessPermissions.json 缺少 ${code} 的文案`);
    }
  }
  assert.ok(sourceOf("constants.ts").includes("PERMISSIONS.FaceMatch.Manage"));
  // 「抓拍与比对」只认 FaceCapture.Images.View：后端权限码、移动端权限文案与常量一致
  assert.ok(/=\s*"FaceCapture\.Images\.View"/.test(permissions), "后端没有权限码 FaceCapture.Images.View");
  for (const lang of ["zh", "en"]) {
    const labels = JSON.parse(read(join(mobileRoot, `src/locales/${lang}/accessPermissions.json`)));
    assert.ok(JSON.stringify(labels).includes('"FaceCapture.Images.View"'), `移动端 ${lang}/accessPermissions.json 缺少 FaceCapture.Images.View 的文案`);
  }
  assert.ok(sourceOf("constants.ts").includes("PERMISSIONS.FaceCapture.ImagesView"));

  // 后端菜单：两个入口各自的权限口径，设备模式白名单不含它们
  const navigation = read(join(repoRoot, "services/backend/BlazorApp.Api/Services/NavigationService.cs"));
  const menuBlock = (routeName: string) => {
    const start = navigation.indexOf(`RouteName = "${routeName}"`);
    assert.ok(start >= 0, `后端 FullAppMenu 缺少 ${routeName}`);
    return navigation.slice(start, navigation.indexOf("Order =", start));
  };
  const unwelcomeMenu = menuBlock("face-unwelcome");
  for (const code of ["Unwelcome.Manage", "Unwelcome.Approve", "Unwelcome.Review"]) {
    assert.ok(unwelcomeMenu.includes(`Permissions.FaceMatch.${code}`));
  }
  assert.equal(/Permission\s*=\s*Permissions/.test(unwelcomeMenu.replace(/AnyPermissions[\s\S]*/, "")), false, "名单入口只用 AnyPermissions，不能有单一 Permission 放行");
  const settingsMenu = menuBlock("face-match-settings");
  assert.ok(settingsMenu.includes("Permissions.FaceMatch.Manage") && settingsMenu.includes("Permissions.FaceCapture.Cameras.Manage"));
  assert.equal(settingsMenu.includes("Unwelcome"), false);
  const resultsMenu = menuBlock("face-capture-results");
  assert.ok(/Permission\s*=\s*Permissions\.FaceCapture\.Images\.View/.test(resultsMenu), "抓拍与比对入口只认 FaceCapture.Images.View");
  assert.equal(resultsMenu.includes("AnyPermissions"), false);
  const deviceBase = navigation.slice(navigation.indexOf("DeviceBaseRouteNames = new("), navigation.indexOf("WarehouseDeviceRouteNames = new("));
  assert.equal(deviceBase.includes("face-"), false);

  // 合并入口 face-hub：any-of 六个权限码（持有任一即显示），标题键 tabs.faceHub，图标 face-recognition；旧三项保留给旧版 App
  const hubMenu = menuBlock("face-hub");
  for (const code of [
    "FaceCapture.Images.View",
    "FaceMatch.Unwelcome.Manage",
    "FaceMatch.Unwelcome.Approve",
    "FaceMatch.Unwelcome.Review",
    "FaceMatch.Manage",
    "FaceCapture.Cameras.Manage",
  ]) {
    assert.ok(hubMenu.includes(`Permissions.${code}`), `face-hub 菜单缺少权限 ${code}`);
  }
  assert.ok(hubMenu.includes("AnyPermissions"), "face-hub 只用 AnyPermissions（任一权限即可见）");
  assert.equal(/Permission\s*=\s*Permissions/.test(hubMenu.replace(/AnyPermissions[\s\S]*/, "")), false, "face-hub 不能有单一 Permission 放行");
  assert.ok(hubMenu.includes('"tabs.faceHub"') && hubMenu.includes('"face-recognition"'));
  // 移动端合并入口的 any-of 与后端一致（同一组码，不多不少）
  const hubAccess = stripComments(read(join(mobileRoot, "src/modules/navigation/face-hub.ts")));
  const hubAccessBlock = hubAccess.slice(hubAccess.indexOf("FACE_HUB_ACCESS_PERMISSIONS = ["), hubAccess.indexOf("] as const", hubAccess.indexOf("FACE_HUB_ACCESS_PERMISSIONS = [")));
  assert.equal(hubAccessBlock.split("FACE_MATCH_PERMISSIONS.").length - 1, 6, "合并入口的 any-of 是六个权限码");
}

// ───────── 2b. 跨端契约：App 端字段名集合与后端 DTO 一致（后端文件里有对应 record 才断言，没有则跳过并注明） ─────────
{
  const featureDir = join(repoRoot, "services/backend/BlazorApp.Api/Features/FaceMatch");
  const backendSources = existsSync(featureDir) ? readdirSync(featureDir).filter((name) => name.endsWith(".cs")).map((name) => read(join(featureDir, name))) : [];
  const camel = (name: string) => name.charAt(0).toLowerCase() + name.slice(1);

  /** 读 C# record 的成员名（支持属性式 `public T Name { get; init; }` 与位置式 `record X(T Name, ...)`），转驼峰；找不到 record 返回 null。 */
  function backendRecordFields(recordName: string): string[] | null {
    for (const source of backendSources) {
      const match = new RegExp(`record\\s+${recordName}\\b`).exec(source);
      if (!match) continue;
      let index = match.index + match[0].length;
      while (/\s/.test(source[index] ?? "")) index += 1;
      if (source[index] === "(") {
        // 位置式：按括号配对取参数表，逗号只在最外层分隔（类型里可能有泛型的逗号）
        let depth = 0;
        let end = index;
        for (; end < source.length; end += 1) {
          if (source[end] === "(" || source[end] === "<") depth += 1;
          if (source[end] === ")" || source[end] === ">") depth -= 1;
          if (depth === 0) break;
        }
        const parts: string[] = [];
        let level = 0;
        let current = "";
        for (const char of source.slice(index + 1, end)) {
          if (char === "<") level += 1;
          if (char === ">") level -= 1;
          if (char === "," && level === 0) {
            parts.push(current);
            current = "";
          } else current += char;
        }
        parts.push(current);
        return parts.map((part) => part.trim().split(/\s+/).pop() ?? "").filter(Boolean).map(camel);
      }
      const open = source.indexOf("{", index);
      let depth = 0;
      let end = open;
      for (; end < source.length; end += 1) {
        if (source[end] === "{") depth += 1;
        if (source[end] === "}") depth -= 1;
        if (depth === 0) break;
      }
      const body = source.slice(open + 1, end);
      return Array.from(body.matchAll(/public\s+[^;{}=]+?\s+(\w+)\s*\{\s*get;/g), (item) => camel(item[1]));
    }
    return null;
  }

  const sameSet = (actual: string[], expected: string[], label: string) =>
    assert.deepEqual([...actual].sort(), [...expected].sort(), `${label} 的字段名集合与后端不一致`);
  const skipped: string[] = [];
  const checks: [string, string[]][] = [
    ["FaceHitResultDto", Object.keys(normalizeFaceHitResult({}))],
    ["FaceHitSummaryDto", Object.keys(normalizeFaceHitSummary({}))],
    ["FaceCaptureResultHitDto", Object.keys((normalizeFaceCaptureResult({ hit: { taskId: "t" } }).hit ?? {}) as object)],
    ["FaceThumbnailItemDto", Object.keys(normalizeFaceThumbnailResults({ items: [{ kind: "capture", id: "a", status: "none" }] })[0] ?? {})],
    ["FaceHitReadResultDto", ["unreadCount"]],
  ];
  for (const [recordName, appFields] of checks) {
    const backendFields = backendRecordFields(recordName);
    if (backendFields === null) {
      skipped.push(recordName);
      continue;
    }
    sameSet(appFields, backendFields, recordName);
  }
  if (skipped.length > 0) console.log(`source-contract.test.ts: 后端暂无 ${skipped.join("、")}，跨端字段契约检查已跳过`);
  // 命中流筛选取值：App 的二级筛选与后端 FaceHitStatuses 一致（后端有这个类才断言）
  const statusSource = backendSources.find((source) => source.includes("class FaceHitStatuses"));
  if (statusSource) {
    const classStart = statusSource.indexOf("class FaceHitStatuses");
    const classBody = statusSource.slice(classStart, statusSource.indexOf("\n}", classStart));
    const backendStatuses = Array.from(classBody.matchAll(/const string \w+ = "(\w+)";/g), (item) => item[1]);
    assert.deepEqual([...HIT_STATUS_KEYS].sort(), [...backendStatuses].sort(), "命中流 hitStatus 取值与后端 FaceHitStatuses 不一致");
  }
}

// ───────── 3. 页面不硬编码中文 ─────────
const CJK = /[㐀-鿿＀-￯]/;
for (const file of moduleFiles.filter((name) => name.endsWith(".tsx"))) {
  const code = stripComments(read(file));
  const offending = code.split("\n").find((line) => CJK.test(line));
  assert.equal(offending, undefined, `${file} 硬编码了中文：${offending?.trim()}`);
}

// ───────── 4. 文案键中英文齐全，插值参数一致 ─────────
type Tree = { [key: string]: string | Tree };
const zh = JSON.parse(read(join(mobileRoot, "src/locales/zh/screens/faceMatch.json"))) as Tree;
const en = JSON.parse(read(join(mobileRoot, "src/locales/en/screens/faceMatch.json"))) as Tree;
const commonZh = JSON.parse(read(join(mobileRoot, "src/locales/zh/common.json"))) as Tree;
const commonEn = JSON.parse(read(join(mobileRoot, "src/locales/en/common.json"))) as Tree;

function lookup(tree: Tree, key: string): string | Tree | undefined {
  return key.split(".").reduce<string | Tree | undefined>((node, part) => (node && typeof node === "object" ? node[part] : undefined), tree);
}

function leaves(tree: Tree, prefix = ""): [string, string][] {
  return Object.entries(tree).flatMap(([key, value]) =>
    typeof value === "string" ? [[`${prefix}${key}`, value] as [string, string]] : leaves(value, `${prefix}${key}.`),
  );
}

const placeholders = (text: string) => Array.from(text.matchAll(/\{\{(\w+)\}\}/g), (match) => match[1]).sort().join(",");
for (const [key, value] of leaves(zh)) {
  const other = lookup(en, key);
  assert.equal(typeof other, "string", `英文缺少 ${key}`);
  assert.notEqual(value.trim(), "", `中文 ${key} 为空`);
  assert.notEqual((other as string).trim(), "", `英文 ${key} 为空`);
  assert.equal(placeholders(value), placeholders(other as string), `${key} 的插值参数中英文不一致`);
  assert.equal(CJK.test(other as string), false, `英文 ${key} 里含有中文`);
}
assert.equal(leaves(zh).length, leaves(en).length, "中英文键数量必须相同");

// 代码里用到的文案键
const usedKeys = new Set<string>();
const KEY_PREFIXES = /^(messages|states|tabs|hub|filters|disabled|errors|validation|unwelcome|form|photo|leave|review|settings|capture|timeline|hits|thumb)\.[A-Za-z0-9_.]+$/;
for (const file of moduleFiles) {
  const code = stripComments(read(file));
  for (const match of code.matchAll(/["'`]([A-Za-z][A-Za-z0-9_.]*\.[A-Za-z0-9_.]+)["'`]/g)) {
    if (KEY_PREFIXES.test(match[1])) usedKeys.add(match[1]);
  }
}
// 动态键族（模板字符串拼出来的键）逐个枚举
for (const kind of ["approve", "withdraw", "dispute"]) usedKeys.add(`unwelcome.${kind}.done`);
for (const kind of ["notConfigured", "forbidden", "unauthorized", "gone", "missing", "failed"]) usedKeys.add(`review.imageErrors.${kind}`);
for (const code of ["unsupportedType", "sourceTooLarge", "decodeFailed", "tooLarge", "tooMany"]) usedKeys.add(`photo.errors.${code}`);
for (const key of ["evaluateMatch", "staffExclusionEnabled", "trafficDedupEnabled", "unwelcomeListEnabled"]) usedKeys.add(`settings.profile.fields.${key}`);
for (const code of FACE_MATCH_ERROR_CODES) usedKeys.add(`errors.codes.${code}`);
for (const status of STATUS_ERROR_CODES) usedKeys.add(`errors.status.${status}`);
for (const zone of PROFILE_TIME_ZONES) usedKeys.add(`settings.profile.timeZone.${zone}`);
assert.ok(usedKeys.size > 120, `扫描到的文案键过少（${usedKeys.size}），扫描规则可能失效`);
for (const key of usedKeys) {
  assert.equal(typeof lookup(zh, key), "string", `中文缺少文案键 ${key}`);
  assert.equal(typeof lookup(en, key), "string", `英文缺少文案键 ${key}`);
}
// 反向：文案文件里没有无人使用的键（改名后遗留的死键）
const used = usedKeys;
for (const [key] of leaves(zh)) {
  assert.ok(used.has(key), `文案键 ${key} 没有任何代码使用（死键，请删除或补上用法）`);
}

// common 命名空间的键（返回、取消等）
for (const key of ["actions.back", "actions.cancel", "actions.confirm", "actions.retry"]) {
  assert.equal(typeof lookup(commonZh, key), "string");
  assert.equal(typeof lookup(commonEn, key), "string");
}

// 摄像头状态文案不写在线 / 离线
for (const [tree, pattern] of [[zh, /在线|离线/], [en, /online|offline/i]] as const) {
  for (const status of ["recentlyActive", "noRecentPush", "disabled"]) {
    const text = lookup(tree, `settings.cameras.status.${status}`) as string;
    assert.equal(pattern.test(text), false, `摄像头状态「${text}」不能写成在线 / 离线（摄像机没有心跳）`);
  }
}
// 相似度口径：任何展示相似度的文案都写「不等于是同一个人的概率」（旧写法少了「等于」二字，有歧义）
assert.ok((lookup(zh, "review.similarity") as string).includes("不等于是同一个人的概率"));
assert.ok((lookup(en, "review.similarity") as string).includes("not the probability"));
// 「抓拍」页顶部提示：写明口径，并说明「照片只在本页显示、离开即销毁、每次读取记审计」；旧的「点开详情后才读取」说法已作废
{
  const noticeZh = lookup(zh, "capture.notice") as string;
  const noticeEn = lookup(en, "capture.notice") as string;
  assert.equal(noticeZh, "分数是相似度，不等于是同一个人的概率。照片只在本页显示，离开页面即销毁，每次读取都会记审计。");
  assert.ok(/not the probability/.test(noticeEn) && /only shown on this page/.test(noticeEn) && /destroyed/.test(noticeEn) && /audited/.test(noticeEn));
  assert.equal(/点开详情后才读取|opened/.test(noticeZh + noticeEn), false);
  // 筛选条：第 2 个是「不受欢迎」（内部键仍叫 blacklist，跳转参数兼容旧链接），「仅命中」已去掉；命中标签写「不受欢迎命中 N%」（2026-10-16 与 Web 统一用词）
  assert.equal(lookup(zh, "capture.filters.blacklist"), "不受欢迎");
  assert.equal(lookup(zh, "capture.filters.hitOnly"), undefined);
  assert.equal(lookup(en, "capture.filters.hitOnly"), undefined);
  assert.equal(lookup(zh, "capture.hit.tag"), "不受欢迎命中 {{percent}}%");
  assert.equal(lookup(zh, "thumb.deleted"), "已删除");
}
// 「抓拍与比对」页顶部提示同样写明口径；通知发送情况只有人数，文案里不出现姓名
assert.ok((lookup(zh, "capture.notice") as string).includes("不等于是同一个人的概率"));
assert.ok((lookup(en, "capture.notice") as string).includes("not the probability"));
for (const key of ["capture.notify.sent", "capture.timeline.notified", "capture.timeline.read"]) {
  assert.ok(!/\{\{name\}\}/.test(lookup(zh, key) as string), `${key} 不应出现姓名插值`);
}
const AMBIGUOUS_SIMILARITY_WORDING = new RegExp(["不是同一个人", "的概率"].join(""));
for (const [key, value] of leaves(zh)) {
  assert.equal(AMBIGUOUS_SIMILARITY_WORDING.test(value), false, `${key} 仍是有歧义的旧写法，请写成「不等于是同一个人的概率」`);
}
for (const key of ["review.confirmMatch.content"]) {
  assert.ok((lookup(zh, key) as string).includes("不等于是同一个人的概率"), `${key} 要写明相似度不等于是同一个人的概率`);
}
// 状态文案
assert.equal(lookup(zh, "review.status.confirmed"), "已确认");
assert.equal(lookup(en, "review.status.confirmed"), "Confirmed");
assert.ok((lookup(zh, "review.band.high") as string) === "较高" && (lookup(zh, "review.band.normal") as string) === "一般");

// 命中复核：一个有复核权限的人判断一次即结束，界面（含旧后端兼容用的错误文案）不再出现先后复核人、需要多人的说法。
// 名单条目批准（unwelcome.*：管理员一人批准、非管理员「其他」类别需两位）是另一件事，不在检查范围内。
{
  // 用拼接构造正则，避免这些被禁用的说法在源码里留下字面量
  const TWO_PERSON_ZH = new RegExp(["两人", "两位", "第一", "第二", "二审", "一审", "不同于"].map((word) => (word === "第一" || word === "第二" ? `${word}复核` : word)).join("|"));
  const TWO_PERSON_EN = new RegExp(["two-person", "two people", "two different", "second reviewer", "first reviewer", "different person"].join("|"), "i");
  const reviewScope = (tree: Tree): [string, string][] => [
    ...leaves((lookup(tree, "review") as Tree), "review."),
    ["errors.codes.FACE_MATCH_SAME_REVIEWER", lookup(tree, "errors.codes.FACE_MATCH_SAME_REVIEWER") as string],
    ["settings.profile.hints.unwelcomeList", lookup(tree, "settings.profile.hints.unwelcomeList") as string],
  ];
  for (const [key, value] of reviewScope(zh)) {
    assert.equal(TWO_PERSON_ZH.test(value), false, `中文 ${key} 不应再出现多人复核的说法：${value}`);
  }
  for (const [key, value] of reviewScope(en)) {
    assert.equal(TWO_PERSON_EN.test(value), false, `英文 ${key} 不应再出现多人复核的说法：${value}`);
  }
  // 复核人姓名文案：旧版遗留任务的 secondReviewerName 展示为「补充复核人」
  assert.equal(lookup(zh, "review.reviewerFirst"), "复核人：{{name}}");
  assert.equal(lookup(zh, "review.reviewerSecond"), "补充复核人：{{name}}");
  // 针对后一位复核人的提示横幅与「待后一位复核」步骤文案已删除
  assert.equal(lookup(zh, "review.firstReviewer"), undefined);
  assert.equal(lookup(zh, "review.step.second"), undefined);
  assert.equal(lookup(en, "review.firstReviewer"), undefined);
  assert.equal(lookup(en, "review.step.second"), undefined);
  // 弹框不再展示那条提示横幅
  assert.equal(stripComments(sourceOf("ReviewTaskModal.tsx")).includes("review.firstReviewer"), false);
}

// ───────── 命中自动确认阈值与「自动确认」展示 ─────────
{
  const profileCode = stripComments(sourceOf("ProfileSection.tsx"));
  // 阈值输入框：数字小数键盘、% 后缀、校验错误走 validateProfileEdits（不在页面里自己写规则）
  assert.ok(profileCode.includes("settings.profile.fields.unwelcomeAutoConfirmScore"), "档案表单要有阈值输入框");
  assert.ok(profileCode.includes('keyboardType="decimal-pad"'), "阈值输入用数字小数键盘");
  assert.ok(profileCode.includes('<TextInput.Affix text="%"'), "阈值输入带 % 后缀");
  assert.ok(profileCode.includes("errors.unwelcomeAutoConfirmScore"), "阈值校验错误要显示在输入框下");
  // 保存确认框要写明阈值「原值 → 新值」（0 写成「不自动确认」）
  assert.ok(profileCode.includes("settings.profile.confirmAutoConfirm") && profileCode.includes("settings.profile.autoConfirmOff"));
  assert.ok(profileCode.includes("change.autoConfirmChange"));

  // 复核卡片：自动确认的任务有标签，复核人行显示「系统自动确认」
  const panelCode = stripComments(sourceOf("ReviewPanel.tsx"));
  assert.ok(panelCode.includes("task.autoConfirmed"));
  assert.ok(panelCode.includes("review.autoConfirmedTag") && panelCode.includes("review.autoConfirmedBy"));
  // 复核弹框不为自动确认任务开任何入口：能否复核只看服务端 canReview，自动确认任务 canReview 为 false
  assert.equal(stripComments(sourceOf("ReviewTaskModal.tsx")).includes("autoConfirmed"), false);

  // 文案：与 Web 同一句；写明「不等于是同一个人的概率」；不出现有歧义的「不是同一个人的概率」；不承诺任何自动处置
  const hintZh = lookup(zh, "settings.profile.hints.unwelcomeAutoConfirmScore") as string;
  const hintEn = lookup(en, "settings.profile.hints.unwelcomeAutoConfirmScore") as string;
  assert.ok(hintZh.includes("不等于是同一个人的概率") && !hintZh.includes("不是同一个人的概率"));
  assert.ok(hintZh.includes("填 0 表示不自动确认") && hintZh.includes("默认 90"));
  assert.ok(hintZh.includes("系统不会自动拒绝服务或通知警方"));
  assert.ok(/not the probability of being the same person/i.test(hintEn) && /never refuses service or notifies police/i.test(hintEn));
  assert.equal(lookup(zh, "review.autoConfirmedTag"), "自动确认");
  assert.equal(lookup(zh, "review.autoConfirmedBy"), "系统自动确认");
  assert.equal(lookup(en, "review.autoConfirmedTag"), "Auto-confirmed");
  assert.equal(lookup(zh, "settings.profile.autoConfirmOff"), "不自动确认");
  // 阈值常量与 Web / 后端一致
  const constants = stripComments(sourceOf("constants.ts"));
  for (const [name, value] of [["AUTO_CONFIRM_SCORE_OFF", 0], ["AUTO_CONFIRM_SCORE_MIN", 50], ["AUTO_CONFIRM_SCORE_MAX", 100], ["AUTO_CONFIRM_SCORE_DEFAULT", 90]] as const) {
    assert.ok(new RegExp(`export const ${name} = ${value};`).test(constants), `${name} 应为 ${value}`);
  }
}

// ───────── 5. 路由薄壳只做转发 ─────────
{
  const routes: [string, string][] = [
    ["app/(shell)/face-hub.tsx", "FaceHubScreen"],
    ["app/(shell)/face-unwelcome/entry-new.tsx", "EntryFormScreen"],
  ];
  for (const [file, component] of routes) {
    const path = join(mobileRoot, file);
    assert.ok(existsSync(path), `缺少路由文件 ${file}`);
    const source = read(path);
    assert.ok(source.includes(`from "@/modules/face-match/${component}"`));
    assert.ok(source.includes(`<${component} />`));
    assert.ok(source.split("\n").length <= 8, `${file} 只做转发`);
  }
  assert.ok(existsSync(join(mobileRoot, "app/(shell)/face-unwelcome/_layout.tsx")));

  // 旧三个入口只是重定向壳：落到合并页对应页签（深链、旧通知、历史书签仍可用），页面本身不再渲染旧屏幕
  const legacy: [string, string, string][] = [
    ["app/(shell)/face-capture-results.tsx", "face-capture-results", "capture"],
    ["app/(shell)/face-unwelcome/index.tsx", "face-unwelcome", "list"],
    ["app/(shell)/face-match-settings.tsx", "face-match-settings", "settings"],
  ];
  for (const [file, routeName] of legacy) {
    const path = join(mobileRoot, file);
    assert.ok(existsSync(path), `缺少路由文件 ${file}（旧入口要保留并重定向）`);
    const code = stripComments(read(path));
    assert.ok(code.includes("<Redirect "), `${file} 必须是重定向壳`);
    assert.ok(code.includes(`resolveLegacyFaceRedirectParams("${routeName}")`), `${file} 要按路由名重定向到合并页对应页签`);
    assert.equal(/Screen\b/.test(code), false, `${file} 不再渲染旧页面`);
  }
}

// ───────── 6. 「人脸比对」合并页 ─────────
{
  const hub = stripComments(sourceOf("FaceHubScreen.tsx"));
  // 三个面板都来自各自的内容面板（不含框架与守卫），页面框架与入口守卫只在合并页里有一份
  assert.ok(hub.includes("<CapturePanel") && hub.includes("<UnwelcomePanel") && hub.includes("<SettingsPanel"));
  assert.ok(/export function UnwelcomePanel\(/.test(stripComments(sourceOf("FaceUnwelcomeScreen.tsx"))));
  assert.ok(/export function SettingsPanel\(/.test(stripComments(sourceOf("FaceMatchSettingsScreen.tsx"))));
  assert.ok(hub.includes("canViewFaceHub(") && hub.includes("resolveFaceHubTabs(hasPermission)"), "合并页入口与页签都按权限判断");
  assert.ok(hub.includes("useLocalSearchParams") && hub.includes("parseFaceHubParams("), "路由参数经过白名单解析");
  assert.ok(hub.includes("initialFilter={params.filter}") && hub.includes("initialHitStatus={params.hitStatus}"), "筛选参数透传给抓拍面板");
  assert.ok(hub.includes("initialTab={params.sub}"), "sub 参数透传给名单面板");
  // 红色数字：来自导航上下文，只挂在 resolveFaceHubBadgeTab 选出的页签上，封顶文字走共用函数
  assert.ok(hub.includes("faceHitUnreadCount") && hub.includes("resolveFaceHubBadgeTab(tabs)") && hub.includes("formatUnreadBadgeText("));
  // 抓拍面板不常驻：缩略图只放内存、离开即销毁，切走页签必须卸载（名单 / 设置才允许隐藏常驻）
  assert.ok(hub.includes('key !== "capture" && visited.has(key)'), "抓拍面板切走页签即卸载");
  assert.ok(hub.includes('display: "none"'), "名单 / 设置面板隐藏而不卸载");
  // 合并页不读取任何图片字节、不碰存储
  assert.equal(/fetchCaptureImage|fetchTaskImage|fetchReferenceImage|<Image\b/.test(hub), false);
  // 导航登记：路径、可见集合、工作台入口、角色菜单目录、审核模式排除都有 face-hub
  assert.ok(read(join(mobileRoot, "src/modules/navigation/default-route.ts")).includes('"face-hub": "/(shell)/face-hub"'));
  assert.ok(read(join(mobileRoot, "src/modules/navigation/workbench.ts")).includes('routeName: "face-hub"'));
  assert.ok(/\["face-hub", "Face matching", "人脸比对", \[/.test(read(join(mobileRoot, "src/modules/identity-admin/role-menu-catalog.ts"))));
  assert.ok(/IOS_REVIEW_EXCLUDED_ROUTE_NAMES[\s\S]*"face-hub"[\s\S]*\] as const/.test(read(join(mobileRoot, "src/modules/ios-review/menu.ts"))), "iOS 审核离线壳里隐藏 face-hub");
  // 页签名 / 提示文案中英文都有（其余 hub.* 文案键由上面的「用到的键 + 死键」检查守住）
  assert.equal(lookup(zh, "hub.tabs.capture"), "抓拍");
  assert.equal(lookup(zh, "hub.tabs.list"), "名单");
  assert.equal(lookup(zh, "hub.tabs.settings"), "设置");
  assert.equal(lookup(zh, "hub.title"), "人脸比对");
  assert.equal(lookup(en, "hub.title"), "Face matching");
  assert.equal(lookup(commonZh, "tabs.faceHub"), "人脸比对");
  assert.equal(lookup(commonEn, "tabs.faceHub"), "Face matching");
}

console.log("source-contract.test.ts: ok");
