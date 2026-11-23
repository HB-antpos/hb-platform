import assert from "node:assert/strict";
import {
  buildPackBlankCartonLabelSpec,
  buildPackCartonLabelCommand,
  packBlankSheetText,
} from "./cpcl-labels";

// 空箱标（批量预打印）：沿用 70×26 内容标的版式，条码 / 二维码内容与内容标一致（都是完整箱号），
// 但不印 SKU / PCS；没有去向时 TO 后画一条横线；右下印 LABEL i / n 与日期时间 + 操作人。

function rows(command: string) {
  return command.split("\r\n").filter(Boolean);
}
function textValues(command: string) {
  return rows(command)
    .map((row) => row.match(/^TEXT \d+ 0 \d+ \d+ (.*)$/)?.[1])
    .filter((value): value is string => value !== undefined);
}
/** 左侧类型条的竖排文字（VTEXT）。 */
function vtextValues(command: string) {
  return rows(command)
    .map((row) => row.match(/^VTEXT \d+ 0 \d+ \d+ (.*)$/)?.[1])
    .filter((value): value is string => value !== undefined);
}

const base = {
  cartonNo: "BX2611150002",
  originName: "TestStore",
  destinationType: 0,
  destinationName: null,
  carrierName: null,
  skuCount: 0,
  pieceCount: 0,
  orderNo: null,
  printedAt: "15 NOV 12:40",
  operatorName: "Sean",
  blankLabel: { index: 1, total: 20 },
};

// ---- 无去向的空箱标 ----
const blank = buildPackCartonLabelCommand(base);
const blankTexts = textValues(blank);
assert.ok(blank.startsWith("! 0 200 200 208 1\r\nPAGE-WIDTH 570\r\n"), "空箱标沿用 70×26 标准页");
assert.ok(vtextValues(blank).includes("BOX") && vtextValues(blank).includes("CARTON"), "左侧仍是竖排 BOX / CARTON 黑条");
assert.ok(rows(blank).includes("INVERSE-LINE 0 0 70 0 208"), "类型条仍整条反白");
assert.ok(blankTexts.includes("FROM TESTSTORE"), "FROM 照印发出地");
assert.ok(blankTexts.includes("TO"), "TO 保留");
assert.ok(!blankTexts.includes("UNASSIGNED"), "无去向时不印 UNASSIGNED，改画横线");
assert.ok(rows(blank).some((row) => /^LINE \d+ 74 \d+ 74 2$/.test(row)), "TO 后画一条横线");
assert.ok(!blankTexts.some((value) => /SKU|PCS/.test(value)), "空箱标不印 SKU / PCS");
assert.ok(blankTexts.includes("LABEL 01 / 20"), "右下印 LABEL 序号 / 总张数");
assert.ok(blankTexts.includes("15 NOV 12:40 SEAN"), "右下第二行是日期时间与操作人");
assert.ok(blankTexts.includes("BX261115") && blankTexts.includes("0002"), "编号仍拆成前 8 位与尾号");
assert.ok(!rows(blank).some((row) => row.startsWith("BARCODE 128")), "空箱标不印一维码");
assert.ok(blank.includes("MA,BX2611150002"), "二维码内容是完整箱号，与内容标一致");
assert.ok(rows(blank).every((row) => /^[\x20-\x7e]*$/.test(row)), "全部是 ASCII");

// ---- 二维码内容与内容标逐字一致（签收 / 组托盘 / 位置查询扫哪一张都认）----
const content = buildPackCartonLabelCommand({ ...base, blankLabel: undefined, skuCount: 5, pieceCount: 86, destinationType: 2, destinationName: "Inala" });
const qrContentOf = (command: string) => rows(command).find((row) => row.startsWith("MA,"))?.slice(3);
assert.equal(qrContentOf(blank), "BX2611150002", "空箱标二维码是完整箱号");
assert.equal(qrContentOf(blank), qrContentOf(content), "空箱标与内容标的二维码内容一致");
assert.ok(!rows(content).some((row) => row.startsWith("LINE ")), "内容标不画横线");
assert.ok(textValues(content).includes("5 SKU - 86 PCS"), "内容标照旧印 SKU / PCS");

// ---- 有去向：照印去向，不画横线 ----
const toStore = buildPackCartonLabelCommand({ ...base, destinationType: 2, destinationName: "Inala" });
assert.ok(textValues(toStore).includes("INALA"), "有去向则照印去向");
assert.ok(textValues(toStore).includes("FROM TESTSTORE") && textValues(toStore).includes("DIRECT"), "要发出的去向沿用 DIRECT 规则（底部左侧）");
assert.ok(!rows(toStore).some((row) => row.startsWith("LINE ")), "有去向不画横线");
const toStorage = buildPackCartonLabelCommand({ ...base, destinationType: 1 });
assert.ok(textValues(toStorage).includes("STORAGE") && !rows(toStorage).some((row) => row.startsWith("LINE ")), "本地存放印 STORAGE");

// ---- 小标签纸：472 宽变体，横线不越过二维码 ----
const small = buildPackCartonLabelCommand(base, "small");
assert.ok(small.includes("PAGE-WIDTH 472"), "小标签 472 宽");
const line = rows(small).find((row) => row.startsWith("LINE "));
const qr = rows(small).find((row) => row.startsWith("BARCODE QR "))?.split(" ");
assert.ok(line && qr, "小标签也有横线与二维码");
assert.ok(Number(line.split(" ")[3]) <= Number(qr[2]) - 12, "横线右端不压二维码");
assert.ok(textValues(small).includes("LABEL 01 / 20"), "小标签同样印 LABEL i / n");

// ---- LABEL 序号补零 ----
assert.equal(packBlankSheetText(1, 20), "LABEL 01 / 20");
assert.equal(packBlankSheetText(7, 9), "LABEL 07 / 09");
assert.equal(packBlankSheetText(5, 100), "LABEL 005 / 100", "满 100 张时序号补成三位");

// ---- 没有批内序号：不印 LABEL 行（旧数据兜底）----
const noSeq = buildPackBlankCartonLabelSpec({ ...base, blankLabel: { index: 0, total: 0 } });
assert.equal(noSeq.referenceLine, "", "没有序号不印 LABEL");
assert.equal(noSeq.statLine, "");
assert.equal(noSeq.toRule, true);

console.log("pack blank label tests passed");
