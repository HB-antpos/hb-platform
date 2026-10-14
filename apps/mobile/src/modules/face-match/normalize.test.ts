import assert from "node:assert/strict";
import {
  normalizeFaceCameraList,
  normalizeFaceCaptureResult,
  normalizeFaceCaptureResultPage,
  normalizeFaceGateReport,
  normalizeFaceHitReadAllResult,
  normalizeFaceHitResult,
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


// ───────── 抓拍与比对结果：缺失字段不崩，未评估时没有判定，命中必须有任务编号 ─────────
{
  const full = normalizeFaceCaptureResult({
    captureId: "c1",
    storeCode: "1042",
    storeName: "TestStore",
    cameraId: "s1042-entrance-01",
    capturedAtUtc: "2026-10-14T01:20:03Z",
    faceCount: 2,
    isDuplicate: false,
    evaluated: true,
    decision: "newVisitor",
    staffScorePercent: 9,
    visitScorePercent: 31,
    faceImageId: "f1",
    sceneImageId: "s1",
    hit: {
      taskId: "t1",
      scorePercent: 87,
      band: "high",
      reviewStatus: 0,
      reviewStatusName: "Pending",
      imageAvailable: true,
      notifyState: "sent",
      notifiedAtUtc: "2026-10-14T01:21:02Z",
      notifyRecipientCount: 3,
      notifyReadCount: 1,
    },
  });
  assert.equal(full.decision, "newVisitor");
  assert.equal(full.staffScorePercent, 9);
  assert.equal(full.hit?.band, "high");
  assert.equal(full.hit?.notifyRecipientCount, 3);
  assert.equal(full.hit?.notifiedAtUtc, "2026-10-14T01:21:02Z");

  const bare = normalizeFaceCaptureResult({ captureId: "c2", evaluated: "yes", staffScorePercent: "9", hit: { scorePercent: 80 } });
  assert.equal(bare.evaluated, false, "非 true 的值不算已评估");
  assert.equal(bare.decision, null);
  assert.equal(bare.staffScorePercent, null, "分数不是数字就当没有，不编造 0");
  assert.equal(bare.faceImageId, null);
  assert.equal(bare.hit, null, "没有任务编号的命中无法关联复核，当作没有命中");
  assert.equal(normalizeFaceCaptureResult({ hit: { taskId: "t", band: "weird" } }).hit?.band, "normal", "未知档位按一般处理");

  const page = normalizeFaceCaptureResultPage({ items: [{ captureId: "c1" }, { captureId: "c2" }], page: 2, pageSize: 20, hasMore: true });
  assert.equal(page.items.length, 2);
  assert.equal(page.page, 2);
  assert.equal(page.hasMore, true);
  assert.deepEqual(normalizeFaceCaptureResultPage(null).items, []);
}

// ───────── 抓拍流里 hit 的新增字段：autoConfirmed / unread / notificationId（缺失、类型漂移都不崩） ─────────
{
  const full = normalizeFaceCaptureResult({ captureId: "c1", hit: { taskId: "t1", autoConfirmed: true, unread: true, notificationId: "n-1" } });
  assert.equal(full.hit?.autoConfirmed, true);
  assert.equal(full.hit?.unread, true);
  assert.equal(full.hit?.notificationId, "n-1");
  // 旧后端没有这三个字段：不是自动确认、不是未读、没有通知编号
  const legacy = normalizeFaceCaptureResult({ captureId: "c2", hit: { taskId: "t2", scorePercent: 90 } });
  assert.equal(legacy.hit?.autoConfirmed, false);
  assert.equal(legacy.hit?.unread, false);
  assert.equal(legacy.hit?.notificationId, null);
  // 只认明确的 true：字符串 "true" / 数字 1 / null 都不算
  const drift = normalizeFaceCaptureResult({ captureId: "c3", hit: { taskId: "t3", autoConfirmed: "true", unread: 1, notificationId: 42 } });
  assert.equal(drift.hit?.autoConfirmed, false);
  assert.equal(drift.hit?.unread, false);
  assert.equal(drift.hit?.notificationId, null, "通知编号只接受非空字符串");
  assert.equal(normalizeFaceCaptureResult({ captureId: "c4", hit: { taskId: "t4", notificationId: "  " } }).hit?.notificationId, null);
}

// ───────── 命中流的一行：契约 §2.1 的全部字段 ─────────
{
  const row = normalizeFaceHitResult({
    taskId: "a".repeat(32),
    captureId: "c1",
    storeCode: "1042",
    storeName: "TestStore",
    cameraId: "cam-1",
    capturedAtUtc: "2026-10-14T08:40:22Z",
    scorePercent: 87,
    band: "high",
    reviewStatus: 2,
    reviewStatusName: "confirmed",
    autoConfirmed: true,
    imageAvailable: true,
    unread: true,
    notificationId: "n1",
    notifyState: "sent",
    notifiedAtUtc: "2026-10-14T08:40:30Z",
    notifyRecipientCount: 4,
    notifyReadCount: 1,
    canReview: true,
    decision: "repeatVisitor",
    staffScorePercent: 3,
    visitScorePercent: 100,
    faceCount: 1,
  });
  assert.equal(row.taskId, "a".repeat(32));
  assert.equal(row.captureId, "c1");
  assert.equal(row.storeName, "TestStore");
  assert.equal(row.band, "high");
  assert.equal(row.reviewStatus, 2);
  assert.equal(row.autoConfirmed, true);
  assert.equal(row.unread, true);
  assert.equal(row.notificationId, "n1");
  assert.equal(row.notifiedAtUtc, "2026-10-14T08:40:30Z");
  assert.equal(row.canReview, true);
  assert.equal(row.decision, "repeatVisitor");
  assert.equal(row.visitScorePercent, 100);
  assert.equal(row.faceCount, 1);
  // 字段名集合固定（跨端契约测试会拿它和后端 FaceHitResultDto 比对）
  assert.deepEqual(Object.keys(normalizeFaceHitResult({})).sort(), [
    "autoConfirmed", "band", "cameraId", "canReview", "captureId", "capturedAtUtc", "decision", "faceCount", "imageAvailable", "notificationId",
    "notifiedAtUtc", "notifyReadCount", "notifyRecipientCount", "notifyState", "reviewStatus", "reviewStatusName", "scorePercent",
    "staffScorePercent", "storeCode", "storeName", "taskId", "unread", "visitScorePercent",
  ]);

  // 缺失字段：Match 模式没有留存抓拍（captureId 为 null）、没有评估（decision / 分数 / faceCount 为 null）；布尔一律 false
  const bare = normalizeFaceHitResult({ taskId: "t" });
  assert.equal(bare.captureId, null);
  assert.equal(bare.storeName, null);
  assert.equal(bare.decision, null);
  assert.equal(bare.staffScorePercent, null);
  assert.equal(bare.visitScorePercent, null);
  assert.equal(bare.faceCount, null);
  assert.equal(bare.notificationId, null);
  assert.equal(bare.notifiedAtUtc, null);
  assert.equal(bare.band, "normal");
  assert.equal(bare.unread, false);
  assert.equal(bare.canReview, false, "能否复核只认服务端的 canReview，缺失当 false，不放大授权");
  assert.equal(bare.imageAvailable, false);
  assert.equal(bare.autoConfirmed, false);
  assert.equal(bare.scorePercent, 0);
  assert.equal(bare.notifyState, "");
  // 类型漂移不崩：只认明确的 true / 数字 / 字符串
  const drift = normalizeFaceHitResult({
    taskId: "t",
    unread: "true",
    canReview: 1,
    imageAvailable: "yes",
    autoConfirmed: "true",
    scorePercent: "88",
    reviewStatus: "2",
    faceCount: "1",
    staffScorePercent: Number.NaN,
    band: "weird",
    captureId: 7,
  });
  assert.equal(drift.unread, false);
  assert.equal(drift.canReview, false);
  assert.equal(drift.imageAvailable, false);
  assert.equal(drift.autoConfirmed, false);
  assert.equal(drift.scorePercent, 0);
  assert.equal(drift.reviewStatus, 0);
  assert.equal(drift.faceCount, null);
  assert.equal(drift.staffScorePercent, null);
  assert.equal(drift.band, "normal", "未知档位按一般处理");
  assert.equal(drift.captureId, null);
  assert.equal(normalizeFaceHitResult(null).taskId, "");
  assert.equal(normalizeFaceHitResult("oops").taskId, "");

  // 分页：没有任务编号的行无法打开 / 标记已读 / 取缩略图，丢弃；其余字段照常
  const page = normalizeFaceHitResultPage({ items: [{ taskId: "t1" }, { storeCode: "no-id" }, { taskId: "t2" }, null, 5], page: 2, pageSize: 50, hasMore: true });
  assert.deepEqual(page.items.map((item) => item.taskId), ["t1", "t2"]);
  assert.equal(page.page, 2);
  assert.equal(page.pageSize, 50);
  assert.equal(page.hasMore, true);
  assert.deepEqual(normalizeFaceHitResultPage(null).items, []);
  assert.equal(normalizeFaceHitResultPage({ items: "nope" }).items.length, 0);
}

// ───────── 命中流汇总：缺失 / 漂移的数字按 0；total 缺失用四个状态之和；unread 与之正交 ─────────
{
  assert.deepEqual(normalizeFaceHitSummary({ total: 5, unread: 3, pending: 2, autoConfirmed: 2, confirmed: 0, dismissed: 1 }), {
    total: 5,
    unread: 3,
    pending: 2,
    autoConfirmed: 2,
    confirmed: 0,
    dismissed: 1,
  });
  assert.deepEqual(normalizeFaceHitSummary(null), { total: 0, unread: 0, pending: 0, autoConfirmed: 0, confirmed: 0, dismissed: 0 });
  assert.deepEqual(normalizeFaceHitSummary({ unread: 4, pending: 1, autoConfirmed: 1, confirmed: 1, dismissed: 1 }), {
    total: 4,
    unread: 4,
    pending: 1,
    autoConfirmed: 1,
    confirmed: 1,
    dismissed: 1,
  }, "total 缺失时 = 四个状态之和（unread 不计入）");
  const drift = normalizeFaceHitSummary({ total: "9", unread: -3, pending: 2.9, autoConfirmed: Number.NaN, confirmed: null, dismissed: "1" });
  assert.equal(drift.unread, 0, "负数按 0");
  assert.equal(drift.pending, 2, "取整");
  assert.equal(drift.autoConfirmed, 0);
  assert.equal(drift.total, 2, "total 不是数字时用四个状态之和");
}

// ───────── 标记已读的返回值 ─────────
assert.deepEqual(normalizeFaceHitReadAllResult({ updated: 3, unreadCount: 0 }), { updated: 3, unreadCount: 0 });
assert.deepEqual(normalizeFaceHitReadAllResult({ unreadCount: 2 }), { updated: 0, unreadCount: 2 });
assert.deepEqual(normalizeFaceHitReadAllResult(null), { updated: 0, unreadCount: 0 });
assert.deepEqual(normalizeFaceHitReadAllResult({ updated: -1, unreadCount: "4" }), { updated: 0, unreadCount: 0 });
assert.equal(normalizeFaceHitUnreadCount({ unreadCount: 7 }), 7);
assert.equal(normalizeFaceHitUnreadCount({ unreadCount: -1 }), 0);
assert.equal(normalizeFaceHitUnreadCount(null), 0);
assert.equal(normalizeFaceHitUnreadCount(5), 0, "返回体不是对象按 0（不信任裸数字，避免把别的接口的数写进红色数字）");

// ───────── 缩略图响应：imageBase64 只接受字符串；状态不认识按 none；kind 不认识的项丢弃 ─────────
{
  const results = normalizeFaceThumbnailResults({
    items: [
      { kind: "capture", id: "c1", status: "ok", imageBase64: "/9j/AAAA" },
      { kind: "hit", id: "t1", status: "none", imageBase64: null },
      { kind: "hit", id: "t2", status: "gone" },
      { kind: "capture", id: "c3", status: "denied", imageBase64: "should-be-dropped" },
    ],
  });
  assert.deepEqual(results, [
    { kind: "capture", id: "c1", status: "ok", imageBase64: "/9j/AAAA" },
    { kind: "hit", id: "t1", status: "none", imageBase64: null },
    { kind: "hit", id: "t2", status: "gone", imageBase64: null },
    { kind: "capture", id: "c3", status: "denied", imageBase64: null },
  ], "只有 ok 才保留图片内容");
  // ok 但没有可用图片（缺失 / 空串 / 不是字符串）→ none
  for (const bad of [undefined, null, "", 42, { a: 1 }, ["/9j/"]]) {
    assert.deepEqual(normalizeFaceThumbnailResults({ items: [{ kind: "capture", id: "c1", status: "ok", imageBase64: bad }] }), [
      { kind: "capture", id: "c1", status: "none", imageBase64: null },
    ], `imageBase64=${JSON.stringify(bad)}`);
  }
  // 状态不认识 → none（占位，不声称已删除）
  assert.deepEqual(normalizeFaceThumbnailResults({ items: [{ kind: "hit", id: "t", status: "teapot", imageBase64: "/9j/" }] }), [{ kind: "hit", id: "t", status: "none", imageBase64: null }]);
  // kind 不认识 / id 缺失的项丢弃
  assert.deepEqual(normalizeFaceThumbnailResults({ items: [{ kind: "face", id: "x", status: "ok", imageBase64: "/9j/" }, { kind: "hit", status: "ok", imageBase64: "/9j/" }, null, "x"] }), []);
  assert.deepEqual(normalizeFaceThumbnailResults(null), []);
  assert.deepEqual(normalizeFaceThumbnailResults({ items: "nope" }), []);
  assert.deepEqual(Object.keys(normalizeFaceThumbnailResults({ items: [{ kind: "hit", id: "t", status: "none" }] })[0]).sort(), ["id", "imageBase64", "kind", "status"]);
}

console.log("normalize.test.ts: ok");
