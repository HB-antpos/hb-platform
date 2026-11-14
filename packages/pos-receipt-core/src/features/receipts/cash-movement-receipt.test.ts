import assert from "node:assert/strict";
import test from "node:test";

import {
  buildCashMovementReceipt,
  cashMovementReceiptToEscPosBytes,
  type CashMovementReceiptInput,
} from "./cash-movement-receipt";
import { dailyCloseReceiptDisplayWidth } from "./daily-close-receipt";

type Movement = CashMovementReceiptInput["movement"];

function movement(overrides: Partial<Movement> = {}): Movement {
  return {
    direction: "Out",
    reasonCode: "SafeDrop",
    amountCents: 30_000,
    note: "End of shift drop",
    voidsMovementGuid: null,
    occurredAtIso: "2026-11-14T08:05:00.000Z", // Brisbane 18:05
    cashierName: "Alice",
    authorizedByName: "Manager Bob",
    displayNumber: "CM-0009",
    deviceCode: "IPAD-1",
    storeCode: "S001",
    ...overrides,
  };
}

function input(overrides: Partial<CashMovementReceiptInput> = {}): CashMovementReceiptInput {
  return {
    movement: movement(),
    todayCashInCents: 12_000,
    todayCashOutCents: 30_000,
    paper: "80mm",
    reprint: false,
    brandName: "Hot Bargain",
    storeName: "Hot Bargain Mount Gravatt",
    businessTimeZone: "Australia/Brisbane",
    ...overrides,
  };
}

const RULE = "-".repeat(42);

test("快照：交店长（SafeDrop，有授权人）80mm 42 列，含 Received by 店长签收栏", () => {
  const document = buildCashMovementReceipt(input());
  assert.equal(document.width, 42);
  assert.deepEqual([...document.lines], [
    "               Hot Bargain",
    "        Hot Bargain Mount Gravatt",
    "            ==== CASH OUT ====",
    RULE,
    "Reason: Safe drop",
    "Amount                             $300.00",
    "Direction: Cash out",
    "Note: End of shift drop",
    "Till: IPAD-1",
    "Cashier: Alice",
    "Authorised: Manager Bob",
    "Time: 2026-11-14 18:05",
    "Ref: CM-0009",
    RULE,
    "Today cash in                      $120.00",
    "Today cash out                     $300.00",
    RULE,
    "Cashier sign: ____________________________",
    "",
    "Received by: _____________________________",
    RULE,
    "       KEEP THIS SLIP IN THE DRAWER",
  ]);
  // 标题、Amount 行、末行加粗。
  assert.deepEqual([...(document.emphasizedLines ?? [])], [2, 5, 21]);
});

test("快照：补零钱（ChangeTopUp）CASH IN，无授权人时不印 Authorised，无备注不印 Note，没有 Received by", () => {
  const document = buildCashMovementReceipt(
    input({
      movement: movement({
        direction: "In",
        reasonCode: "ChangeTopUp",
        amountCents: 5_005,
        note: null,
        authorizedByName: null,
        displayNumber: "CM-0001",
      }),
      brandName: undefined,
      storeName: "Mount Gravatt",
      todayCashInCents: 5_005,
      todayCashOutCents: 0,
    }),
  );
  const text = document.lines.join("\n");
  assert.match(text, /==== CASH IN ====/);
  assert.match(text, /Reason: Change top-up/);
  assert.match(text, /Amount\s+\$50\.05/);
  assert.match(text, /Direction: Cash in$/m);
  assert.doesNotMatch(text, /Authorised|Note:|Received by/);
  assert.match(text, /Today cash out\s+\$0\.00/);
  // 没有品牌名时抬头就是店名，不重复一行。
  assert.equal(document.lines.filter((line) => line.includes("Mount Gravatt")).length, 1);
});

test("PaidOut / OtherIn 的原因标签；PaidOut 虽然是取出，但只有 SafeDrop 才有店长签收栏", () => {
  const paidOut = buildCashMovementReceipt(
    input({ movement: movement({ reasonCode: "PaidOut", note: "Milk for the staff room" }) }),
  ).lines.join("\n");
  assert.match(paidOut, /Reason: Paid out/);
  assert.match(paidOut, /Note: Milk for the staff room/);
  assert.doesNotMatch(paidOut, /Received by/);
  const otherIn = buildCashMovementReceipt(
    input({ movement: movement({ direction: "In", reasonCode: "OtherIn", note: "Float" }) }),
  ).lines.join("\n");
  assert.match(otherIn, /Reason: Other cash in/);
  assert.match(otherIn, /==== CASH IN ====/);
});

test("冲正凭条：标题 CASH MOVEMENT VOID，印 Voids 流水号与 Original 信息，方向写明 void，没有店长签收栏", () => {
  const document = buildCashMovementReceipt(
    input({
      movement: movement({
        direction: "In",
        voidsMovementGuid: "11111111-1111-4111-8111-111111111111",
        note: "entered wrong amount",
        displayNumber: "CM-0010",
      }),
      voidedOriginal: {
        displayNumber: "CM-0009",
        reasonCode: "SafeDrop",
        amountCents: 30_000,
        occurredAtIso: "2026-11-14T08:05:00.000Z",
      },
      todayCashOutCents: 0,
    }),
  );
  const text = document.lines.join("\n");
  assert.match(text, /==== CASH MOVEMENT VOID ====/);
  assert.match(text, /Voids: CM-0009/);
  assert.match(text, /Original: Safe drop \$300\.00/);
  assert.match(text, /Original time: 2026-11-14 18:05/);
  assert.match(text, /Direction: Cash in \(void\)/);
  assert.match(text, /Ref: CM-0010/);
  assert.doesNotMatch(text, /Received by/);
  assert.doesNotMatch(text, /Reason:/);
  // 冲正必须带原记录，否则无法打印。
  assert.throws(
    () =>
      buildCashMovementReceipt(
        input({ movement: movement({ voidsMovementGuid: "11111111-1111-4111-8111-111111111111" }) }),
      ),
    TypeError,
  );
});

test("补打：抬头加 REPRINT，其余不变", () => {
  const normal = buildCashMovementReceipt(input()).lines;
  const reprint = buildCashMovementReceipt(input({ reprint: true })).lines;
  assert.equal(reprint.length, normal.length + 1);
  assert.ok(reprint.some((line) => line.trim() === "*** REPRINT ***"));
  assert.deepEqual(
    reprint.filter((line) => line.trim() !== "*** REPRINT ***"),
    [...normal],
  );
});

test("58mm 窄纸 32 列：所有行不超宽；长备注与长名字自动换行", () => {
  const document = buildCashMovementReceipt(
    input({
      paper: "58mm",
      movement: movement({
        note: "A very long note that definitely needs to wrap onto several lines of the narrow paper",
        cashierName: "Alexandria Montgomery-Featherstonehaugh",
      }),
    }),
  );
  assert.equal(document.width, 32);
  assert.ok(document.lines.every((line) => dailyCloseReceiptDisplayWidth(line) <= 32));
  assert.ok(document.lines.filter((line) => /note|wrap|lines|narrow|paper/u.test(line)).length >= 2);
});

test("备注里的控制字符被清理，不会破坏版式；时间按门店营业时区而不是设备时区", () => {
  const document = buildCashMovementReceipt(
    input({
      movement: movement({ note: "line1\nline2\u0007", occurredAtIso: "2026-11-13T14:30:00.000Z" }),
    }),
  );
  assert.ok(document.lines.every((line) => !/[\u0000-\u001f]/u.test(line)));
  assert.ok(document.lines.includes("Note: line1 line2"));
  assert.ok(document.lines.includes("Time: 2026-11-14 00:30")); // Brisbane 次日零点半
  assert.throws(() => buildCashMovementReceipt(input({ businessTimeZone: "Not/AZone" })), RangeError);
});

test("ESC/POS：纯文本、逐行加粗标记、末尾走纸切纸；金额必须是整数分", () => {
  const document = buildCashMovementReceipt(input());
  const bytes = cashMovementReceiptToEscPosBytes(document);
  const boldOn = [0x1b, 0x45, 0x01];
  const matches = Array.from(bytes).filter((_, start) =>
    boldOn.every((value, offset) => bytes[start + offset] === value),
  ).length;
  assert.equal(matches, document.emphasizedLines?.length);
  assert.deepEqual(Array.from(bytes.slice(-9)), [0x1b, 0x45, 0x00, 0x1b, 0x64, 0x03, 0x1d, 0x56, 0x00]);
  assert.throws(() => buildCashMovementReceipt(input({ todayCashInCents: 1.5 })), TypeError);
});
