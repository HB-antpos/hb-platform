// 人脸比对模块的源码契约测试：
// 1. 隐私：参考照片 / 命中抓拍不写磁盘缓存、不进日志与诊断上报、不进 react-query 缓存、不拼进 URL 查询串；
// 2. 范围：只调用约定内的接口（不新建摄像头、不碰令牌），接口路径与权限码和后端、Web 保持一致；
// 3. 文案：页面不硬编码中文，代码里用到的每个文案键中英文都有，且插值参数一致；
// 4. 路由薄壳只做转发。
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { PROFILE_TIME_ZONES } from "./constants";
import { FACE_MATCH_ERROR_CODES, STATUS_ERROR_CODES } from "./errors";

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
  for (const file of moduleFiles) {
    const code = stripComments(read(file));
    for (const term of [...FORBIDDEN_STORAGE, ...FORBIDDEN_LOGGING, ...FORBIDDEN_IMAGE_CACHES]) {
      assert.equal(code.includes(term), false, `${file} 不应出现 ${term}（生物识别照片不得落盘 / 进日志 / 进缓存）`);
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
  const hooks = stripComments(sourceOf("use-face-match.ts"));
  assert.equal(/fetchTaskImage|fetchReferenceImage|image-session/.test(hooks), false, "图片字节不得经过 react-query 钩子");
  assert.equal(/useQuery|useMutation|useInfiniteQuery/.test(modal), false, "复核弹框不用 react-query 取图");

  // 请求层：no-store、图片请求不进中心日志、图片地址不带查询串
  const core = stripComments(sourceOf("api-core.ts"));
  assert.ok(core.includes('"Cache-Control": "no-store"'));
  assert.ok(core.includes('"X-Skip-Center-Log": "1"'));
  assert.ok(core.includes('responseType: "arraybuffer"'));
  assert.equal(/\/image\?|\/reference\/\$\{index\}\?/.test(core), false, "图片地址不得带查询串");
  assert.equal(/params:/.test(core.slice(core.indexOf("async function fetchImageDataUri"), core.indexOf("return {", core.indexOf("async function fetchImageDataUri")))), false);

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

// ───────── 2. 范围与契约 ─────────
{
  const core = stripComments(sourceOf("api-core.ts"));
  // 不做：新建摄像头、重置令牌、查看 / 修改令牌
  for (const forbidden of ["rotate-token", "createCamera", "cameraUrl", "/captures", "mac/", "secret"]) {
    assert.equal(core.toLowerCase().includes(forbidden.toLowerCase()), false, `api-core 不应出现 ${forbidden}`);
  }
  assert.equal(/http\.post\(\s*FACE_CAMERAS_API_BASE/.test(core), false, "不新建摄像头");

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
  ]) {
    assert.ok(faceMatchController.includes(route), `后端缺少路由 ${route}`);
  }
  assert.ok(camerasController.includes("[HttpGet]"));
  assert.ok(camerasController.includes('[HttpPut("{cameraId}")]'));
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
  ]) {
    assert.ok(core.includes(path), `api-core 缺少接口 ${path}`);
  }
  assert.ok(core.includes('"/react/v1/face-match"') === false, "基础路径来自 constants");
  const constants = stripComments(sourceOf("constants.ts"));
  assert.ok(constants.includes('"/react/v1/face-match"') && constants.includes('"/react/v1/face-cameras"'));

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
  const deviceBase = navigation.slice(navigation.indexOf("DeviceBaseRouteNames = new("), navigation.indexOf("WarehouseDeviceRouteNames = new("));
  assert.equal(deviceBase.includes("face-"), false);
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
const KEY_PREFIXES = /^(messages|states|tabs|filters|disabled|errors|validation|unwelcome|form|photo|leave|review|settings)\.[A-Za-z0-9_.]+$/;
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
    ["app/(shell)/face-unwelcome/index.tsx", "FaceUnwelcomeScreen"],
    ["app/(shell)/face-unwelcome/entry-new.tsx", "EntryFormScreen"],
    ["app/(shell)/face-match-settings.tsx", "FaceMatchSettingsScreen"],
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
}

console.log("source-contract.test.ts: ok");
