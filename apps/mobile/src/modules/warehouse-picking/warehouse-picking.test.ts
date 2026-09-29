import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizePickMutation, normalizePickSheet, readPickingError } from "./api-normalization";
import { buildPickCodeIndex, normalizeScanCode, resolvePickScan } from "./code-resolver";
import {
  firstOpenLine,
  hasMinOrderQuantity,
  lineRemaining,
  lineStatus,
  mergeProgressLines,
  sortLinesByLocation,
  splitIntoPacks,
  summarizeLines,
  summarizePickers,
  upNextLines,
} from "./pick-math";
import { activeTeammates, pickedByParts, pickerInitials, relativeMinutes, teammateByLine } from "./pick-view-model";
import { isPickerUsable } from "./picker-store";
import { PICK_MATCH, type PickCodeEntry, type PickSheetLine } from "./types";

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
    ...overrides,
  };
}

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
    lineCount: 4, completeLineCount: 1, shortLineCount: 2, overLineCount: 1, orderedPieces: 84, pickedPieces: 54,
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

test("按货位走一趟：自然排序、接下来只列未拣齐、默认落在第一条未拣齐", () => {
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

test("合并服务端进度：只改返回的行，并更新中包数与拣货人归属", () => {
  const lines = [line({ detailGuid: "d-a" }), line({ detailGuid: "d-b", minOrderQuantity: null })];
  const merged = mergeProgressLines(lines, [
    { detailGuid: "d-b", pickedTotal: 6, minOrderQuantity: 6, pickedBy: [{ pickerUserGuid: "u-mia", pickerName: "Mia Wong", quantity: 6 }] },
  ]);

  assert.equal(merged[0], lines[0]);
  assert.equal(merged[1].pickedTotal, 6);
  assert.equal(merged[1].minOrderQuantity, 6);
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
