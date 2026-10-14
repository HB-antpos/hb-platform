import assert from "node:assert/strict";
import {
  normalizeFaceCameraList,
  normalizeFaceGateReport,
  normalizeFaceMatchConfig,
  normalizeFaceStoreProfile,
  normalizeFaceUnwelcomeEntry,
  normalizeFaceUnwelcomeEntryPage,
  normalizeFaceUnwelcomeTask,
  normalizeFaceUnwelcomeTaskPage,
} from "./normalize";

// ───────── 配置：缺失的权限标志一律当 false（不放大授权） ─────────
{
  const empty = normalizeFaceMatchConfig(null);
  assert.equal(empty.enabled, false);
  assert.deepEqual(empty.permissions, {
    manage: false,
    trafficView: false,
    unwelcomeManage: false,
    unwelcomeApprove: false,
    unwelcomeReview: false,
  });
  const partial = normalizeFaceMatchConfig({ enabled: true, region: "ap-singapore", permissions: { unwelcomeReview: true, manage: "yes" } });
  assert.equal(partial.enabled, true);
  assert.equal(partial.region, "ap-singapore");
  assert.equal(partial.permissions.unwelcomeReview, true);
  assert.equal(partial.permissions.manage, false, "非 true 的值不算有权限");
  assert.equal(partial.objectStoreConfigured, false);
}

// ───────── 闸门：老后端没有 warnings / hasWarnings ─────────
{
  const report = normalizeFaceGateReport({ facets: [{ facet: "match", requested: true, missing: [] }], canSave: true });
  assert.deepEqual(report.facets[0].warnings, []);
  assert.equal(report.hasWarnings, false);
  const withWarning = normalizeFaceGateReport({ facets: [{ facet: "match", requested: true, warnings: ["piaReference"] }], canSave: true });
  assert.equal(withWarning.hasWarnings, true, "标志位缺失时以实际警告条目为准");
}

// ───────── 档案：22 个可编辑字段原样保留（含 null） ─────────
{
  const profile = normalizeFaceStoreProfile({
    storeCode: "1008",
    storeName: "Bankstown",
    exists: true,
    mode: 2,
    evaluateMatch: false,
    staffExclusionEnabled: true,
    trafficDedupEnabled: false,
    unwelcomeListEnabled: true,
    unwelcomeAutoConfirmScore: 88.5,
    allowedSubjects: "",
    piaReference: "PIA-7",
    signageInstalledOn: "2026-09-01",
    legalSignoffAt: "2026-10-01T03:00:00Z",
    crossBorderRegion: "ap-singapore",
    timeZoneId: "Australia/Sydney",
    effectiveTimeZoneId: "Australia/Sydney",
    jurisdiction: "NSW",
    crossBorderReviewValid: true,
    gates: { facets: [], canSave: true },
  });
  assert.equal(profile.mode, 2);
  assert.equal(profile.allowedSubjects, null, "空串规范成 null");
  assert.equal(profile.piaReference, "PIA-7");
  assert.equal(profile.signageInstalledOn, "2026-09-01");
  assert.equal(profile.legalSignoffAt, "2026-10-01T03:00:00Z");
  assert.equal(profile.staffNoticeDate, null);
  assert.equal(profile.crossBorderRegion, "ap-singapore");
  assert.equal(profile.crossBorderReviewValid, true);
  // 命中自动确认阈值：服务端的数字原样保留；0（关闭）是合法值；缺失 / 非数字取默认 90
  assert.equal(profile.unwelcomeAutoConfirmScore, 88.5);
  assert.equal(normalizeFaceStoreProfile({ unwelcomeAutoConfirmScore: 0 }).unwelcomeAutoConfirmScore, 0);
  assert.equal(normalizeFaceStoreProfile({}).unwelcomeAutoConfirmScore, 90);
  assert.equal(normalizeFaceStoreProfile({ unwelcomeAutoConfirmScore: "70" }).unwelcomeAutoConfirmScore, 90);
  assert.equal(normalizeFaceStoreProfile({ unwelcomeAutoConfirmScore: null }).unwelcomeAutoConfirmScore, 90);
}

// ───────── 条目 ─────────
{
  const entry = normalizeFaceUnwelcomeEntry({
    entryId: "e1",
    incidentCategory: 3,
    categoryNote: "  note  ",
    evidenceRef: "INC-1",
    storeScope: ["1008", "", 5, "1009"],
    approvalsNeeded: 2,
    approvalsDone: 1,
    status: 0,
    photoCount: 3,
    expiringSoon: true,
    daysToExpiry: 12,
    canApprove: true,
  });
  assert.deepEqual(entry.storeScope, ["1008", "1009"], "只保留非空字符串");
  assert.equal(entry.canApprove, true);
  assert.equal(entry.approvalsDone, 1);
  assert.equal(normalizeFaceUnwelcomeEntry({ canApprove: "true" }).canApprove, false, "canApprove 只认布尔 true");
  assert.equal(normalizeFaceUnwelcomeEntry({}).cleanupPending, false);

  const page = normalizeFaceUnwelcomeEntryPage({ items: [{ entryId: "a" }, { entryId: "b" }], page: 2, hasMore: true });
  assert.equal(page.items.length, 2);
  assert.equal(page.page, 2);
  assert.equal(page.hasMore, true);
  assert.equal(page.pageSize, 20);
  assert.equal(normalizeFaceUnwelcomeEntryPage(undefined).items.length, 0);
}

// ───────── 任务：档位、相似度、复核权限 ─────────
{
  const task = normalizeFaceUnwelcomeTask({
    taskId: "t1",
    storeCode: "1008",
    score: 86.4,
    scorePercent: 86,
    band: "high",
    reviewStatus: 0,
    imageAvailable: true,
    referencePhotoCount: 3.9,
    nextStep: "second",
    canReview: true,
  });
  assert.equal(task.band, "high");
  assert.equal(task.scorePercent, 86);
  assert.equal(task.referencePhotoCount, 3);
  assert.equal(task.canReview, true);
  // 未知档位一律当「一般」，不抬高
  assert.equal(normalizeFaceUnwelcomeTask({ band: "veryHigh" }).band, "normal");
  // 缺少 scorePercent 时由 score 四舍五入
  assert.equal(normalizeFaceUnwelcomeTask({ score: 71.6 }).scorePercent, 72);
  assert.equal(normalizeFaceUnwelcomeTask({}).nextStep, "none");
  // nextStep 原样保留：新后端 first / none；旧后端的 second 由 review-logic 折算成「待复核」，这里不改写
  assert.equal(task.nextStep, "second");
  assert.equal(normalizeFaceUnwelcomeTask({ nextStep: "first" }).nextStep, "first");
  // 复核人姓名：新任务只有 firstReviewerName；旧版遗留任务才有 secondReviewerName
  const reviewed = normalizeFaceUnwelcomeTask({ firstReviewerName: "Amy", firstReviewedAtUtc: "2026-10-01T00:00:00Z", secondReviewerName: "Bob", secondReviewedAtUtc: "2026-10-02T00:00:00Z" });
  assert.equal(reviewed.firstReviewerName, "Amy");
  assert.equal(reviewed.secondReviewerName, "Bob");
  assert.equal(normalizeFaceUnwelcomeTask({}).firstReviewerName, null);
  assert.equal(normalizeFaceUnwelcomeTask({}).secondReviewerName, null);
  // canReview 只认服务端的布尔 true，不因复核人姓名推断
  assert.equal(normalizeFaceUnwelcomeTask({ firstReviewerName: "Amy", canReview: true }).canReview, true);
  assert.equal(normalizeFaceUnwelcomeTask({ firstReviewerName: "Amy", canReview: "true" }).canReview, false);
  assert.equal(normalizeFaceUnwelcomeTask({}).canReview, false);
  // 自动确认标记：只认明确的 true，缺失（旧后端）按人工任务处理
  assert.equal(normalizeFaceUnwelcomeTask({ autoConfirmed: true, reviewStatus: 2 }).autoConfirmed, true);
  assert.equal(normalizeFaceUnwelcomeTask({ autoConfirmed: "true" }).autoConfirmed, false);
  assert.equal(normalizeFaceUnwelcomeTask({}).autoConfirmed, false);
  assert.equal(normalizeFaceUnwelcomeTask({ referencePhotoCount: -2 }).referencePhotoCount, 0);
  // 任务里没有姓名、事件细节字段：即使服务端误带也不会流入
  const leaky = normalizeFaceUnwelcomeTask({ taskId: "t2", personName: "Zhang San", incidentDetail: "stole X", categoryNote: "n" }) as unknown as Record<string, unknown>;
  assert.equal("personName" in leaky, false);
  assert.equal("incidentDetail" in leaky, false);
  assert.equal("categoryNote" in leaky, false);

  assert.equal(normalizeFaceUnwelcomeTaskPage({ items: [{ taskId: "x" }], hasMore: false }).items[0].taskId, "x");
}

// ───────── 摄像头：不保留令牌 / 地址，丢弃无编号项 ─────────
{
  const list = normalizeFaceCameraList({
    items: [
      { cameraId: "c1", storeCode: "1008", enabled: true, online: true, captureCount24h: 12, lastSeenAtUtc: "2026-10-13T01:00:00Z", token: "SECRET", cameraUrl: "https://x/secret", note: "Front door" },
      { cameraId: "", storeCode: "1008" },
    ],
  });
  assert.equal(list.items.length, 1);
  const camera = list.items[0] as unknown as Record<string, unknown>;
  assert.equal(camera.captureCount24h, 12);
  assert.equal(camera.note, "Front door");
  assert.equal("token" in camera, false, "令牌不得进入页面 state");
  assert.equal("cameraUrl" in camera, false, "摄像机地址不得进入页面 state");
  assert.equal(list.onlineWindowMinutes, 10);
  assert.equal(list.statsWindowHours, 24);
}

console.log("normalize.test.ts: ok");
