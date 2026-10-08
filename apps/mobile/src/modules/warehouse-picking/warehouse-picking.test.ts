import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizePickMutation, normalizePickOrderList, normalizePickSheet, normalizePickSlipClaim, readPickingError } from "./api-normalization";
import { settleWritesBeforeExit } from "./exit-guard";
import { buildPickCodeIndex, isSlipCode, normalizeScanCode, resolvePickScan } from "./code-resolver";
import {
  firstOpenLine,
  hasAssignments,
  hasMinOrderQuantity,
  incompleteSegments,
  isMyLine,
  isMySegment,
  isOpenLine,
  isStockout,
  lineInScope,
  lineRemaining,
  lineStatus,
  mergeProgressLines,
  parseLocationCode,
  routeTurn,
  scopeCounts,
  sortLinesByLocation,
  splitIntoPacks,
  summarizeLines,
  summarizePickers,
  summarizeSegments,
  upNextLines,
} from "./pick-math";
import {
  activeTeammates,
  claimNoticeFromClaim,
  claimRouteParams,
  parseClaimRouteParams,
  pickedByParts,
  pickerInitials,
  relativeMinutes,
  stockoutReasonKey,
  teammateByLine,
} from "./pick-view-model";
import { isPickerUsable } from "./picker-store";
import { PICK_MATCH, PICK_STOCKOUT_REASON, type PickCodeEntry, type PickSheetLine, type PickStockout } from "./types";

function line(overrides: Partial<PickSheetLine> & Pick<PickSheetLine, "detailGuid">): PickSheetLine {
  return {
    productCode: overrides.detailGuid.toUpperCase(),
    itemNumber: null,
    barcode: null,
    productName: null,
    productImage: null,
    locationCode: null,
    orderedQuantity: 12,
    minOrderQuantity: 12,
    isSet: false,
    setChildren: [],
    pickedTotal: 0,
    pickedBy: [],
    stockout: null,
    assigneeUserGuid: null,
    assigneeName: null,
    assignmentSegmentNo: null,
    ...overrides,
  };
}

const stockout: PickStockout = {
  reason: PICK_STOCKOUT_REASON.locationEmpty,
  markedByName: "Mia Wong",
  markedAtUtc: "2026-09-30T02:00:00Z",
  pickedAtMark: 12,
};

const codes: PickCodeEntry[] = [
  { code: "9312345678905", target: "line", detailGuids: ["d-cup"], matchedBy: PICK_MATCH.barcode, label: null },
  { code: "6901234567892", target: "line", detailGuids: ["d-set"], matchedBy: PICK_MATCH.setChild, label: "Small box" },
  { code: "SHARED", target: "line", detailGuids: ["d-cup", "d-set"], matchedBy: PICK_MATCH.barcode, label: null },
  { code: "A-03-12-02", target: "location", detailGuids: ["d-cup"], matchedBy: null, label: "A-03-12-02" },
];

test("扫码解析：订单条码、商品码、套装子码、货位码、同码多行与未知码", () => {
  const index = buildPickCodeIndex(codes);

  assert.deepEqual(resolvePickScan(" hbso:2026-0418\r", index), { kind: "order", code: "HBSO:2026-0418", orderNo: "2026-0418" });
  assert.deepEqual(resolvePickScan("9312345678905", index), {
    kind: "line", code: "9312345678905", detailGuid: "d-cup", matchedBy: PICK_MATCH.barcode, label: null,
  });
  const child = resolvePickScan("6901234567892", index);
  assert.equal(child.kind, "line");
  assert.equal(child.kind === "line" && child.label, "Small box");
  assert.deepEqual(resolvePickScan("a-03-12-02", index), { kind: "location", code: "A-03-12-02", detailGuids: ["d-cup"] });
  assert.equal(resolvePickScan("SHARED", index).kind, "multiple");
  // 当前行在候选里时直接落在当前行，不再弹选择。
  const current = resolvePickScan("SHARED", index, "d-set");
  assert.equal(current.kind === "line" && current.detailGuid, "d-set");
  assert.deepEqual(resolvePickScan("0000", index), { kind: "none", code: "0000" });
  assert.equal(normalizeScanCode("\t abc\n"), "ABC");
});

test("拣货数量：状态、剩余扫码次数、中包换算与汇总", () => {
  const cup = line({ detailGuid: "d-cup", orderedQuantity: 36, pickedTotal: 24, minOrderQuantity: 12 });
  const over = line({ detailGuid: "d-over", orderedQuantity: 12, pickedTotal: 18 });
  const missing = line({ detailGuid: "d-miss", orderedQuantity: 24, pickedTotal: 0, minOrderQuantity: null });
  const done = line({ detailGuid: "d-done", orderedQuantity: 12, pickedTotal: 12 });

  assert.equal(lineStatus(cup), "partial");
  assert.equal(lineStatus(over), "over");
  assert.equal(lineStatus(missing), "notStarted");
  assert.equal(lineStatus(line({ detailGuid: "d-zero", orderedQuantity: 0, pickedTotal: 0 })), "complete");
  assert.deepEqual(lineRemaining(cup), { remaining: 12, scans: 1 });
  assert.deepEqual(lineRemaining(missing), { remaining: 24, scans: null });
  assert.deepEqual(splitIntoPacks(30, 12), { packs: 2, rest: 6 });
  assert.equal(splitIntoPacks(30, null), null);
  assert.deepEqual(summarizeLines([cup, over, missing, done]), {
    lineCount: 4, completeLineCount: 1, shortLineCount: 2, stockoutLineCount: 0, overLineCount: 1, orderedPieces: 84, pickedPieces: 54,
  });
});

test("中包数为空或为 0 都按未设置处理：不能按中包累加，走补录", () => {
  for (const minOrderQuantity of [null, 0]) {
    const unset = line({ detailGuid: "d-unset", orderedQuantity: 24, minOrderQuantity });
    assert.equal(hasMinOrderQuantity(unset), false, `中包数 ${minOrderQuantity} 应视为未设置`);
    assert.deepEqual(lineRemaining(unset), { remaining: 24, scans: null });
    assert.equal(splitIntoPacks(24, minOrderQuantity), null);
  }
  assert.equal(hasMinOrderQuantity(line({ detailGuid: "d-set", minOrderQuantity: 1 })), true);
});

test("按货位走一趟（默认 M 型）：自然排序、接下来只列未拣齐、默认落在第一条未拣齐", () => {
  const lines = sortLinesByLocation([
    line({ detailGuid: "d-10", locationCode: "A-10-01-01" }),
    line({ detailGuid: "d-none", locationCode: null }),
    line({ detailGuid: "d-2", locationCode: "A-02-01-01", pickedTotal: 12 }),
    line({ detailGuid: "d-3", locationCode: "A-03-01-01" }),
  ]);

  assert.deepEqual(lines.map((item) => item.detailGuid), ["d-2", "d-3", "d-10", "d-none"]);
  assert.equal(firstOpenLine(lines)?.detailGuid, "d-3");
  assert.deepEqual(upNextLines(lines, "d-3", 3).map((item) => item.detailGuid), ["d-10", "d-none"]);
  assert.deepEqual(upNextLines(lines, "d-none", 3).map((item) => item.detailGuid), ["d-3", "d-10"]);
});

test("货位编码按区-排-列-层解析：列、层可省略，多个配货位取第一个，不规范编码返回 null", () => {
  assert.deepEqual(parseLocationCode("A-03-12-02"), { zone: "A", row: 3, rowLabel: "03", bay: 12, level: 2 });
  assert.deepEqual(parseLocationCode("b-04-07"), { zone: "B", row: 4, rowLabel: "04", bay: 7, level: 0 });
  assert.deepEqual(parseLocationCode("A-01"), { zone: "A", row: 1, rowLabel: "01", bay: 0, level: 0 });
  assert.equal(parseLocationCode("A-03-12-02, B-01-01-01")?.zone, "A");
  for (const irregular of ["OLD-A01", "LOC-PRIORITY", "A", "A-01-02-03-04", "", null]) {
    assert.equal(parseLocationCode(irregular), null, `${irregular} 应视为不规范`);
  }
});

test("M 型每排列号都从小到大；S 型双数排从大到小；同列按层；不规范编码在后、无货位最后", () => {
  const input = [
    line({ detailGuid: "d-none", locationCode: null, itemNumber: "HB1" }),
    line({ detailGuid: "d-old", locationCode: "OLD-A01" }),
    line({ detailGuid: "a4-07", locationCode: "A-04-07-01" }),
    line({ detailGuid: "a4-18", locationCode: "A-04-18-03" }),
    line({ detailGuid: "a3-15", locationCode: "A-03-15-01" }),
    line({ detailGuid: "a3-12-l2", locationCode: "A-03-12-02" }),
    line({ detailGuid: "a3-12-l1", locationCode: "A-03-12-01" }),
    line({ detailGuid: "b1-02", locationCode: "B-01-02-01" }),
  ];

  assert.deepEqual(
    sortLinesByLocation(input).map((item) => item.detailGuid),
    ["a3-12-l1", "a3-12-l2", "a3-15", "a4-07", "a4-18", "b1-02", "d-old", "d-none"],
  );
  assert.deepEqual(
    sortLinesByLocation(input, "s").map((item) => item.detailGuid),
    ["a3-12-l1", "a3-12-l2", "a3-15", "a4-18", "a4-07", "b1-02", "d-old", "d-none"],
  );
  // A-10 排在 A-02 之后（数字比较，不是字符串比较）。
  assert.deepEqual(
    sortLinesByLocation([line({ detailGuid: "x10", locationCode: "A-10-01" }), line({ detailGuid: "x2", locationCode: "A-02-01" })]).map(
      (item) => item.detailGuid,
    ),
    ["x2", "x10"],
  );
});

test("换排提示：同一区同一排不提示，换排时给出该排走向", () => {
  const a3 = line({ detailGuid: "a3", locationCode: "A-03-12-02" });
  const a3b = line({ detailGuid: "a3b", locationCode: "A-03-15-01" });
  const a4 = line({ detailGuid: "a4", locationCode: "A-04-18-03" });
  const none = line({ detailGuid: "none", locationCode: null });

  assert.equal(routeTurn(a3, a3b, "m"), null);
  assert.deepEqual(routeTurn(a3b, a4, "m"), { zone: "A", rowLabel: "04", descending: false });
  assert.deepEqual(routeTurn(a3b, a4, "s"), { zone: "A", rowLabel: "04", descending: true });
  assert.deepEqual(routeTurn(null, a3, "s"), { zone: "A", rowLabel: "03", descending: false });
  assert.equal(routeTurn(a4, none, "m"), null);
});

test("拣货范围：有货位 / 无货位按是否绑定配货位划分，计数覆盖全部行", () => {
  const located = line({ detailGuid: "d-loc", locationCode: "A-03-12-02" });
  const blank = line({ detailGuid: "d-blank", locationCode: "  " });
  const none = line({ detailGuid: "d-none", locationCode: null });

  assert.equal(lineInScope(located, "located"), true);
  assert.equal(lineInScope(located, "unlocated"), false);
  assert.equal(lineInScope(blank, "unlocated"), true);
  assert.equal(lineInScope(none, "all"), true);
  // 没有传“我的”上下文时 mine 为 0（订单没有分配）。
  assert.deepEqual(scopeCounts([located, blank, none]), { mine: 0, help: 0, all: 3, located: 1, unlocated: 2 });
});

test("货位没货：标了且未拣齐的行不再待拣，接下来跳过它；拣齐后标记不再算数", () => {
  const out = line({ detailGuid: "d-out", locationCode: "A-03-12-02", orderedQuantity: 36, pickedTotal: 12, stockout });
  const next = line({ detailGuid: "d-next", locationCode: "A-03-15-01" });
  const refilled = line({ detailGuid: "d-refill", orderedQuantity: 12, pickedTotal: 12, stockout });

  assert.equal(isStockout(out), true);
  assert.equal(isOpenLine(out), false);
  assert.equal(isStockout(refilled), false);
  const sorted = sortLinesByLocation([next, out]);
  assert.equal(firstOpenLine(sorted)?.detailGuid, "d-next");
  assert.deepEqual(upNextLines(sorted, null, 3).map((item) => item.detailGuid), ["d-next"]);
  assert.equal(summarizeLines([out, next]).stockoutLineCount, 1);
  assert.equal(summarizeLines([out, next]).shortLineCount, 2);
  // 全部行都没货时，默认仍落在第一行，不会没有当前行。
  assert.equal(firstOpenLine([out])?.detailGuid, "d-out");
});

test("没货原因文案：未绑定货位时“货位空了”说成“找不到”", () => {
  assert.equal(stockoutReasonKey(PICK_STOCKOUT_REASON.locationEmpty, true), "stockout.reasonLocationEmpty");
  assert.equal(stockoutReasonKey(PICK_STOCKOUT_REASON.locationEmpty, false), "stockout.reasonNotFound");
  assert.equal(stockoutReasonKey(PICK_STOCKOUT_REASON.damaged, false, true), "stockout.shortDamaged");
  assert.equal(stockoutReasonKey(PICK_STOCKOUT_REASON.wrongProduct, true, true), "stockout.shortWrongProduct");
});

test("分单条码：HBSP 前缀优先于商品码，订单码仍按 HBSO 识别", () => {
  const index = buildPickCodeIndex([{ code: "HBSP:SO1/2/A1", target: "line", detailGuids: ["d-x"], matchedBy: 1, label: null }]);
  assert.deepEqual(resolvePickScan(" hbsp:so1/2/a1\r", index), { kind: "slip", code: "HBSP:SO1/2/A1" });
  assert.equal(resolvePickScan("HBSP:", index).kind, "none");
  assert.equal(isSlipCode("hbsp:SO1/1/A"), true);
  assert.equal(isSlipCode("HBSO:SO1"), false);
});

test("我的范围：扫了分单按段号，否则按派给我的负责人；没有分配时不出现", () => {
  const lines = [
    line({ detailGuid: "a", locationCode: "A-01-01", assigneeUserGuid: "U-ME", assignmentSegmentNo: 1 }),
    line({ detailGuid: "b", locationCode: null, assigneeUserGuid: null, assignmentSegmentNo: 2 }),
    line({ detailGuid: "c", locationCode: "A-02-01", assigneeUserGuid: "u-li", assignmentSegmentNo: 3 }),
  ];
  const byPicker = { pickerUserGuid: "u-me", segmentNo: null };
  const bySegment = { pickerUserGuid: "u-me", segmentNo: 2 };

  assert.equal(hasAssignments(lines), true);
  assert.equal(hasAssignments([line({ detailGuid: "x" })]), false);
  assert.equal(isMyLine(lines[0], byPicker), true);
  assert.equal(isMyLine(lines[1], byPicker), false);
  assert.equal(isMyLine(lines[1], bySegment), true);
  assert.equal(lineInScope(lines[0], "mine"), false);
  assert.deepEqual(scopeCounts(lines, byPicker), { mine: 1, help: 0, all: 3, located: 2, unlocated: 1 });
  assert.deepEqual(scopeCounts(lines, bySegment).mine, 1);
});

test("完成页按段汇总：负责人或待领取、拣齐与没货品种数，没有分配的行不计", () => {
  const segments = summarizeSegments([
    line({ detailGuid: "a", orderedQuantity: 12, pickedTotal: 12, assigneeName: "Chen", assigneeUserGuid: "u-chen", assignmentSegmentNo: 1 }),
    line({ detailGuid: "b", orderedQuantity: 12, pickedTotal: 6, stockout, assigneeName: "Chen", assigneeUserGuid: "u-chen", assignmentSegmentNo: 1 }),
    line({ detailGuid: "c", orderedQuantity: 24, pickedTotal: 0, assignmentSegmentNo: 2 }),
    line({ detailGuid: "d", orderedQuantity: 6 }),
  ]);

  assert.deepEqual(
    segments.map((item) => [item.segmentNo, item.assigneeName, item.lineCount, item.completeLineCount, item.stockoutLineCount, item.pickedPieces]),
    [[1, "Chen", 2, 1, 1, 18], [2, null, 1, 0, 0, 0]],
  );
});

test("帮忙：只看要帮的那段；未完成段按拣齐或标没货判定，半拣没标算没完；我的段按段号或负责人", () => {
  const lines = [
    line({ detailGuid: "a", orderedQuantity: 12, pickedTotal: 12, assigneeName: "Li", assigneeUserGuid: "u-li", assignmentSegmentNo: 1 }),
    line({ detailGuid: "b", orderedQuantity: 12, pickedTotal: 0, stockout, assigneeName: "Li", assigneeUserGuid: "u-li", assignmentSegmentNo: 1 }),
    line({ detailGuid: "c", orderedQuantity: 12, pickedTotal: 5, assigneeName: "Chen", assigneeUserGuid: "u-chen", assignmentSegmentNo: 2 }),
    line({ detailGuid: "d", orderedQuantity: 6, pickedTotal: 0, assignmentSegmentNo: 3 }),
    line({ detailGuid: "e", orderedQuantity: 6, pickedTotal: 0 }),
  ];
  const helping = { pickerUserGuid: "u-li", segmentNo: null, helpSegmentNo: 2 };

  assert.equal(lineInScope(lines[2], "help", helping), true);
  assert.equal(lineInScope(lines[0], "help", helping), false);
  assert.equal(lineInScope(lines[2], "help", { pickerUserGuid: "u-li", segmentNo: null }), false);
  assert.deepEqual(scopeCounts(lines, helping), { mine: 2, help: 1, all: 5, located: 0, unlocated: 5 });

  // 第 1 段拣齐 + 标没货算拣完；第 2 段半拣没标、第 3 段待领取没动都算没完；没分配的行不参与。
  assert.deepEqual(incompleteSegments(lines).map((segment) => segment.segmentNo), [2, 3]);
  assert.deepEqual(incompleteSegments([lines[4]]), []);
  const [chenSegment, claimable] = incompleteSegments(lines);
  assert.equal(isMySegment(chenSegment, { pickerUserGuid: "U-CHEN", segmentNo: null }), true);
  assert.equal(isMySegment(chenSegment, helping), false);
  assert.equal(isMySegment(claimable, { pickerUserGuid: "u-li", segmentNo: 3 }), true);
});

test("扫分单领取：结果转成路由参数再读回提示；参数不全时不影响进单", () => {
  const claim = normalizePickSlipClaim({
    success: true,
    data: { orderGuid: "o-1", orderNo: "SO1", segmentNo: 2, segmentCount: 3, pickerUserGuid: "u-li", pickerName: "Li Na", lineCount: 11, claimedByMe: false },
  });
  assert.equal(claim.claimedNow, false);
  assert.deepEqual(claimNoticeFromClaim(claim), { kind: "other", segmentNo: 2, segmentCount: 3, lineCount: 11, claimerName: "Li Na" });

  const params = claimRouteParams({ ...claim, claimedNow: true, claimedByMe: true });
  assert.deepEqual(params, { segment: "2", claim: "now", segments: "3", lines: "11", claimer: "Li Na" });
  assert.deepEqual(parseClaimRouteParams(params).notice, { kind: "now", segmentNo: 2, segmentCount: 3, lineCount: 11, claimerName: "Li Na" });
  assert.deepEqual(parseClaimRouteParams({ segment: "2" }), { segmentNo: 2, notice: null });
  assert.deepEqual(parseClaimRouteParams({ segment: "abc", claim: "now" }), { segmentNo: null, notice: null });
});

test("订单列表：派给我的数量可为空，各段负责人可为待领取", () => {
  const list = normalizePickOrderList({
    success: true,
    data: {
      items: [{
        orderGuid: "o-1", flowStatus: 1, lineCount: 3, totalQuantity: 30, pickedLineCount: 0,
        assignees: [{ pickerUserGuid: "u-chen", pickerName: "Chen", lineCount: 2, segmentNo: 1 }, { lineCount: 1, segmentNo: 2 }],
      }],
      counts: { all: 1, toPick: 1, picking: 0 },
    },
  });
  assert.equal(list.counts.mine, null);
  assert.equal(list.items[0].assignees[1].pickerUserGuid, null);
  assert.deepEqual(list.items[0].pickers, []);
});

test("合并服务端进度：只改返回的行，并更新中包数与拣货人归属", () => {
  const lines = [line({ detailGuid: "d-a" }), line({ detailGuid: "d-b", minOrderQuantity: null })];
  const merged = mergeProgressLines(lines, [
    {
      detailGuid: "d-b",
      pickedTotal: 6,
      minOrderQuantity: 6,
      pickedBy: [{ pickerUserGuid: "u-mia", pickerName: "Mia Wong", quantity: 6 }],
      stockout,
      assigneeUserGuid: "u-mia",
      assigneeName: "Mia Wong",
      assignmentSegmentNo: 2,
    },
  ]);

  assert.equal(merged[0], lines[0]);
  assert.equal(merged[1].pickedTotal, 6);
  assert.equal(merged[1].minOrderQuantity, 6);
  // 同事标的没货随进度同步过来；没有标记时服务端返回 null，本地随之清掉。
  assert.deepEqual(merged[1].stockout, stockout);
  // 同事扫分单领取后，负责人随进度同步过来。
  assert.equal(merged[1].assigneeUserGuid, "u-mia");
  assert.equal(merged[1].assignmentSegmentNo, 2);
  assert.equal(mergeProgressLines(merged, [{ ...merged[1], stockout: null }])[1].stockout, null);
  assert.deepEqual(summarizePickers(merged), [{ pickerUserGuid: "u-mia", pickerName: "Mia Wong", quantity: 6 }]);
});

test("一起拣：同事在场判断、所在行、谁拣了多少与头像缩写", () => {
  const now = Date.parse("2026-09-29T10:00:00Z");
  const participants = [
    { pickerUserGuid: "u-me", pickerName: "Alex Chen", lastActiveAtUtc: "2026-09-29T09:59:30Z", lastDetailGuid: "d-cup" },
    { pickerUserGuid: "u-mia", pickerName: "Mia Wong", lastActiveAtUtc: "2026-09-29T09:58:00Z", lastDetailGuid: "d-set" },
    { pickerUserGuid: "u-old", pickerName: "Old Timer", lastActiveAtUtc: "2026-09-29T09:00:00Z", lastDetailGuid: "d-jar" },
  ];

  assert.deepEqual(activeTeammates(participants, "U-ME", now).map((item) => item.pickerUserGuid), ["u-mia"]);
  assert.deepEqual(Array.from(teammateByLine(participants, "u-me", now)), [["d-set", "Mia"]]);
  assert.deepEqual(relativeMinutes("2026-09-29T09:58:00Z", now), { kind: "minutes", count: 2 });
  assert.deepEqual(relativeMinutes("2026-09-29T09:59:40Z", now), { kind: "justNow" });
  assert.deepEqual(
    pickedByParts(line({ detailGuid: "d", pickedBy: [
      { pickerUserGuid: "u-mia", pickerName: "Mia Wong", quantity: 24 },
      { pickerUserGuid: "u-me", pickerName: "Alex Chen", quantity: 12 },
    ] }), "u-me"),
    [{ name: "Alex", quantity: 12, isMe: true }, { name: "Mia", quantity: 24, isMe: false }],
  );
  assert.equal(pickerInitials("Alex Chen"), "AC");
  assert.equal(pickerInitials("张伟"), "张");
});

test("拣货人：账号本人须仍是当前账号，员工码凭证过期前一分钟即视为失效", () => {
  const now = Date.parse("2026-09-29T10:00:00Z");
  const account = { userGuid: "U-1", name: "Alex", method: "account" as const, ticket: null, expiresAtUtc: null };
  const staff = { userGuid: "u-2", name: "Mia", method: "staffBarcode" as const, ticket: "t", expiresAtUtc: "2026-09-29T10:00:30Z" };

  assert.equal(isPickerUsable(account, "u-1", now), true);
  assert.equal(isPickerUsable(account, "u-other", now), false);
  assert.equal(isPickerUsable(staff, null, now), false);
  assert.equal(isPickerUsable({ ...staff, expiresAtUtc: "2026-09-29T22:00:00Z" }, null, now), true);
  assert.equal(isPickerUsable(null, "u-1", now), false);
});

test("接口数据校验：缺字段报错而不是补零，省略的空字段按 null 处理", () => {
  const sheet = normalizePickSheet({
    success: true,
    data: {
      orderGuid: "o-1",
      orderNo: "2026-0418",
      flowStatus: 3,
      session: { status: 1, startedAtUtc: "2026-09-29T00:00:00Z", startedByName: "Alex" },
      lines: [{
        detailGuid: "d-1", productCode: "P-1", orderedQuantity: 36, isSet: false, pickedTotal: 12,
        pickedBy: [{ pickerUserGuid: "u", pickerName: "Alex", quantity: 12 }],
      }],
      codes: [{ code: "931", target: "line", detailGuids: ["d-1"], matchedBy: 1 }],
      serverTimeUtc: "2026-09-29T00:00:01Z",
    },
  });
  assert.equal(sheet.lines[0].minOrderQuantity, null);
  assert.equal(sheet.lines[0].stockout, null);
  const marked = normalizePickMutation({
    success: true,
    data: {
      line: { detailGuid: "d-1", pickedTotal: 12, pickedBy: [], stockout: { reason: 2, markedByName: "Mia", markedAtUtc: "2026-09-30T02:00:00Z", pickedAtMark: 12 } },
      appliedDelta: 0,
    },
  });
  assert.equal(marked.line.stockout?.reason, PICK_STOCKOUT_REASON.wrongProduct);
  assert.deepEqual(sheet.lines[0].setChildren, []);
  assert.equal(sheet.storeName, null);
  assert.throws(
    () => normalizePickMutation({ success: true, data: { line: { detailGuid: "d-1" }, appliedDelta: 12 } }),
    /Invalid warehouse picking mutation/,
  );
  assert.deepEqual(
    readPickingError({ response: { status: 409, data: { success: false, errorCode: "PICKED_TOTAL_CHANGED", data: { line: 1 } } } }),
    { code: "PICKED_TOTAL_CHANGED", status: 409, data: { line: 1 } },
  );
});

test("暂存并退出：等写入队列清空，期间有写入失败就不退出", async () => {
  // 没有在途写入：直接放行。
  // 注意 getChain 要返回稳定引用（真实代码里是 writeChainRef.current）；每次新建 Promise 会让“队列没变化”永远不成立。
  const idle = Promise.resolve();
  assert.equal(await settleWritesBeforeExit(() => idle, () => 0), "ok");

  // 有在途写入：等它收尾再放行，且等待期间队列又追加了一条也要一并等完。
  const order: string[] = [];
  let chain: Promise<unknown> = new Promise((resolve) => setTimeout(() => (order.push("first"), resolve(null)), 10));
  const settled = settleWritesBeforeExit(() => chain, () => 0);
  const first = chain;
  setTimeout(() => {
    chain = first.then(() => new Promise((resolve) => setTimeout(() => (order.push("second"), resolve(null)), 10)));
  }, 5);
  assert.equal(await settled, "ok");
  assert.deepEqual(order, ["first", "second"]);

  // 等待期间有写入失败：返回 failed，不退出。
  let failures = 0;
  const failing = new Promise((resolve) => setTimeout(() => (failures += 1, resolve(null)), 5));
  assert.equal(await settleWritesBeforeExit(() => failing, () => failures), "failed");

  // 进入时就已存在的旧失败（已由服务端进度重新同步）不算数。
  failures = 3;
  assert.equal(await settleWritesBeforeExit(() => idle, () => failures), "ok");

  // 队列里的 Promise 自身 reject 也不能让退出卡住。
  const rejected = Promise.reject(new Error("boom"));
  assert.equal(await settleWritesBeforeExit(() => rejected, () => 0), "ok");
});
