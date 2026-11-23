import assert from "node:assert/strict";
import {
  buildPackCartonLabelCommand,
  buildPackCartonLabelSpec,
  buildPackPalletLabelCommand,
  buildPackPalletLabelSpec,
} from "./cpcl-labels";

/**
 * 把指令拆成行；TEXT 行解析出字体、坐标、当前 SETMAG 倍率与内容，便于断言不越界。
 * VTEXT（左侧类型条的竖排文字，逆时针转 90° 自下而上读）单独放进 vtexts。
 */
function parse(command: string) {
  const rows = command.split("\r\n").filter(Boolean);
  let mag: [number, number] = [1, 1];
  const texts: { font: number; x: number; y: number; scaleX: number; scaleY: number; value: string }[] = [];
  const vtexts: { font: number; x: number; y: number; scaleX: number; scaleY: number; value: string }[] = [];
  for (const row of rows) {
    const magMatch = row.match(/^SETMAG (\d+) (\d+)$/);
    if (magMatch) {
      mag = [Math.max(1, Number(magMatch[1])), Math.max(1, Number(magMatch[2]))];
      continue;
    }
    const textMatch = row.match(/^(V?)TEXT (\d+) 0 (\d+) (\d+) (.*)$/);
    if (textMatch) {
      (textMatch[1] ? vtexts : texts).push({
        font: Number(textMatch[2]),
        x: Number(textMatch[3]),
        y: Number(textMatch[4]),
        scaleX: mag[0],
        scaleY: mag[1],
        value: textMatch[5],
      });
    }
  }
  return { rows, texts, vtexts };
}

// 打包标签的文字只用 font 7（ASCII 12×24 点，按 SETMAG 放大）。不用 font 0：设计时按 8×9 点排版，
// 2026-11-23 TC26 + 蓝牙标签机首次真机打印实测它约 12 点宽、更高，导致 FROM 压到 TO、右下文字被裁出页边。
function right(textRow: { font: number; x: number; scaleX: number; value: string }) {
  return textRow.x + textRow.value.length * 12 * textRow.scaleX;
}

function bottom(textRow: { font: number; y: number; scaleY: number }) {
  return textRow.y + 24 * textRow.scaleY;
}

// 去向类型与后端 PackDestinationTypes 一致：0 未指定、1 本地存放、2 其他分店、3 总仓。
const carton = {
  cartonNo: "BX2611130042",
  originName: "Sunnybank",
  destinationType: 2,
  destinationName: "Inala",
  carrierName: null,
  skuCount: 5,
  pieceCount: 86,
  orderNo: null,
  printedAt: "13 NOV 14:32",
  operatorName: "Lily",
};

// ---- 箱标签 · 标准 570×208：分店发给另一家分店，自己送 ----
const cartonCommand = buildPackCartonLabelCommand(carton);
const cartonParsed = parse(cartonCommand);
assert.ok(cartonCommand.startsWith("! 0 200 200 208 1\r\nPAGE-WIDTH 570\r\n"), "箱标签页高 208、页宽 570");
assert.ok(cartonCommand.endsWith("PRINT\r\n"), "箱标签必须发送 PRINT");
assert.ok(cartonParsed.rows.includes("BARCODE-TEXT OFF"), "不继承上一张标签的条码可读数字");
assert.ok(cartonParsed.vtexts.some((row) => row.value === "BOX"), "左侧类型条竖排写 BOX");
assert.ok(cartonParsed.vtexts.some((row) => row.value === "CARTON"), "类型条里竖排写 CARTON（不再横排超出黑条）");
assert.ok(!cartonParsed.texts.some((row) => row.value === "BOX" || row.value === "CARTON"), "BOX / CARTON 不再用横排 TEXT 打印");
const cartonVtextBox = cartonParsed.vtexts.find((row) => row.value === "BOX");
const cartonVtextCaption = cartonParsed.vtexts.find((row) => row.value === "CARTON");
assert.ok(cartonVtextBox && cartonVtextCaption && cartonVtextBox.y > cartonVtextCaption.y, "自下而上读：BOX 在下、CARTON 在上");
assert.ok(cartonParsed.rows.includes("INVERSE-LINE 0 0 70 0 208"), "左侧整条反白成黑条");
assert.ok(!cartonParsed.rows.some((row) => row.startsWith("BOX ")), "箱标签没有外框");
assert.ok(cartonParsed.texts.some((row) => row.value === "FROM SUNNYBANK"), "FROM 取后端返回的发出地（分店名）");
assert.ok(cartonParsed.texts.some((row) => row.value === "DIRECT"), "要发出且没有运输公司：底部左侧打 DIRECT");
assert.ok(!cartonCommand.includes("HB WAREHOUSE"), "不再写死总仓名");
assert.ok(cartonParsed.texts.some((row) => row.value === "TO"), "第二行有 TO");
const cartonDestination = cartonParsed.texts.find((row) => row.value === "INALA");
assert.ok(cartonDestination, "去向分店名大写");
assert.deepEqual([cartonDestination.scaleX, cartonDestination.scaleY], [2, 2], "去向用最大字");
const cartonPrefix = cartonParsed.texts.find((row) => row.value === "BX261113");
const cartonTail = cartonParsed.texts.find((row) => row.value === "0042");
assert.ok(cartonPrefix && cartonTail, "编号拆成前 8 位与流水尾号");
assert.deepEqual([cartonPrefix.scaleX, cartonTail.scaleX], [1, 2], "前 8 位小字、尾号大字");
assert.ok(cartonParsed.texts.some((row) => row.value === "5 SKU - 86 PCS"), "数量为 N SKU - M PCS");
assert.ok(!cartonParsed.rows.some((row) => row.startsWith("BARCODE 128")), "箱标签不再印一维码");
assert.ok(cartonCommand.includes("BARCODE QR 457 6 M 2 U 5\r\nMA,BX2611130042\r\nENDQR"), "右上二维码内容是完整箱号");
assert.ok(!cartonParsed.texts.some((row) => row.value.startsWith("ORDER")), "没有订货单时不打订货单号");
assert.ok(cartonParsed.texts.some((row) => row.value === "13 NOV 14:32 LILY"), "右下时间与操作人");
assert.ok(
  cartonParsed.rows.every((row) => /^[\x20-\x7e]*$/.test(row)),
  "英文数据的标签指令全部是 ASCII"
);

/** 所有文字不越过右边界和安全高度；二维码范围内的文字不得压到二维码。 */
function assertWithinBounds(command: string, width: number, label: string) {
  const { rows, texts, vtexts } = parse(command);
  const qr =rows.map((row) => row.match(/^BARCODE QR (\d+) (\d+) M 2 U (\d+)$/)).find(Boolean);
  assert.ok(qr, `${label}：必须有二维码`);
  const qrX = Number(qr[1]);
  const qrBottom = Number(qr[2]) + 21 * Number(qr[3]);
  assert.ok(qrX + 21 * Number(qr[3]) <= width - 8, `${label}：二维码不越过右边界`);
  assert.ok(
    texts.every((row) => row.font === 7),
    `${label}：文字一律用 font 7（font 0 在真机上约 12 点宽、更高，不是 8×9）`
  );
  for (const row of texts) {
    assert.ok(right(row) <= width - 8, `${label}：「${row.value}」不越过右边界`);
    assert.ok(bottom(row) <= 196, `${label}：「${row.value}」在单张安全高度内`);
    if (row.y < qrBottom) {
      assert.ok(right(row) <= qrX - 8, `${label}：「${row.value}」不压二维码`);
    }
  }
  // 文字框两两不重叠（真机首印时 FROM 行压到 TO 行、右下两行互压都是这类问题），也不与二维码框相交。
  type Box = { value: string; left: number; top: number; right: number; bottom: number };
  const overlaps = (a: Box, b: Box) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
  const boxes: Box[] = texts.map((row) => ({ value: row.value, left: row.x, top: row.y, right: right(row), bottom: bottom(row) }));
  const qrBox: Box = { value: "二维码", left: qrX, top: Number(qr[2]), right: qrX + 21 * Number(qr[3]), bottom: qrBottom };
  for (let i = 0; i < boxes.length; i++) {
    assert.ok(!overlaps(boxes[i], qrBox), `${label}：「${boxes[i].value}」不与二维码框相交`);
    for (let j = i + 1; j < boxes.length; j++) {
      assert.ok(!overlaps(boxes[i], boxes[j]), `${label}：「${boxes[i].value}」与「${boxes[j].value}」文字框重叠`);
    }
  }
  assert.ok(!rows.some((row) => row.startsWith("BARCODE 128")), `${label}：不再印一维码`);

  // 左侧类型条的竖排文字：VTEXT 逆时针转 90°，文字起点 (x, y) 在最下端，向上占 字数×字宽×倍率，向右占 24×倍率（font 7）。
  // 整个文字框必须落在 INVERSE-LINE 反白出来的黑条里，否则超出的部分会印成白底黑字。
  const bar = rows.map((row) => row.match(/^INVERSE-LINE (\d+) (\d+) (\d+) \d+ (\d+)$/)).find(Boolean);
  assert.ok(bar, `${label}：必须有反白黑条`);
  const [barLeft, barTop, barRight] = [Number(bar[1]), Number(bar[2]), Number(bar[3])];
  const barBottom = barTop + Number(bar[4]);
  assert.ok(vtexts.length >= 2, `${label}：类型条有两行竖排文字`);
  for (const row of vtexts) {
    assert.equal(row.font, 7, `${label}：竖排文字用 font 7（12×24，宽度已知）`);
    const length = row.value.length * 12 * row.scaleX;
    const across = 24 * row.scaleY;
    assert.ok(row.x >= barLeft && row.x + across <= barRight, `${label}：「${row.value}」横向不超出黑条`);
    assert.ok(row.y - length >= barTop && row.y <= barBottom, `${label}：「${row.value}」纵向不超出黑条`);
  }
}
assertWithinBounds(cartonCommand, 570, "箱标签");

// ---- 去向文案：TO 与 FROM 行的 VIA / DIRECT 规则 ----
// 本地存放：TO 打 STORAGE，FROM 不加 VIA / DIRECT
const storage = parse(
  buildPackCartonLabelCommand({ ...carton, destinationType: 1, destinationName: null, carrierName: "Ignored Carrier" })
);
assert.ok(storage.texts.some((row) => row.value === "STORAGE"), "本地存放 TO 打 STORAGE");
assert.ok(storage.texts.some((row) => row.value === "FROM SUNNYBANK"), "本地存放 FROM 不打 VIA / DIRECT，也不打运输公司");
// 未指定：UNASSIGNED
const unassigned = parse(buildPackCartonLabelCommand({ ...carton, destinationType: 0, destinationName: null, operatorName: "Tom" }));
assert.ok(unassigned.texts.some((row) => row.value === "UNASSIGNED"), "未指定去向打 UNASSIGNED");
assert.ok(unassigned.texts.some((row) => row.value === "FROM SUNNYBANK"), "未指定 FROM 不打 VIA / DIRECT");
assert.ok(unassigned.texts.some((row) => row.value === "13 NOV 14:32 TOM"));
// 要发出但缺收货方名称：不打空 TO，退回 UNASSIGNED
assert.ok(
  parse(buildPackCartonLabelCommand({ ...carton, destinationName: null })).texts.some((row) => row.value === "UNASSIGNED"),
  "缺收货方名称时 TO 打 UNASSIGNED"
);
// 总仓 + 运输公司：VIA
const viaCarrier = parse(
  buildPackCartonLabelCommand({ ...carton, destinationType: 3, destinationName: "Warehouse", carrierName: "Border Express" })
);
assert.ok(viaCarrier.texts.some((row) => row.value === "FROM SUNNYBANK"), "FROM 行只留发出地");
assert.ok(viaCarrier.texts.some((row) => row.value === "VIA BORDER EXPRESS"), "有运输公司：底部左侧打完整的 VIA <承运商>");
assert.ok(viaCarrier.texts.some((row) => row.value === "WAREHOUSE"), "总仓作为收货方打总仓名");
// 总仓发出的箱：FROM 取总仓名；订货单号打在右下
const fromWarehouse = parse(
  buildPackCartonLabelCommand({ ...carton, originName: "HB Warehouse", orderNo: "2026-0412" })
);
assert.ok(fromWarehouse.texts.some((row) => row.value === "FROM HB WAREHOUSE"));
assert.ok(fromWarehouse.texts.some((row) => row.value === "DIRECT"));
assert.ok(fromWarehouse.texts.some((row) => row.value === "ORDER 2026-0412"), "有订货单时右下打订货单号");

// ---- 箱标签 · 超长字段截断而不是溢出 ----
const longCommand = buildPackCartonLabelCommand({
  ...carton,
  originName: "Hot Bargain Central Distribution Warehouse Brisbane South Side Number Two",
  destinationName: "Sunnybank Hills Shopping Town Level Two Extension",
  skuCount: 12345,
  pieceCount: 1234567,
  orderNo: "2026-0412-EXTRA-LONG-ORDER-NUMBER",
  operatorName: "Alexandria Montgomery",
});
const longParsed = parse(longCommand);
assertWithinBounds(longCommand, 570, "超长箱标签");
const longDestination = longParsed.texts.find((row) => row.value.startsWith("SUNNYBANK"));
assert.ok(longDestination, "超长去向仍然打印");
assert.equal(longDestination.scaleX, 1, "去向放不下时先缩一号");
assert.ok(longDestination.value.length < "SUNNYBANK HILLS SHOPPING TOWN LEVEL TWO EXTENSION".length, "缩号后仍放不下则截断");
const longFrom = longParsed.texts.find((row) => row.value.startsWith("FROM "));
assert.ok(longFrom && longFrom.value.length < 70, "超长发出地截断");
assert.ok(longCommand.includes("MA,BX2611130042\r\nENDQR"), "截断不影响二维码内容");
assert.equal(longParsed.rows.filter((row) => row === "PRINT").length, 1, "截断不产生多余行");

// 稍长的分店名：缩一号后完整保留，不截断。
const mediumDestination = parse(buildPackCartonLabelCommand({ ...carton, destinationName: "Sunnybank Hills" })).texts.find(
  (row) => row.value === "SUNNYBANK HILLS"
);
assert.ok(mediumDestination, "15 个字符的分店名完整保留");
assert.equal(mediumDestination.scaleX, 1, "15 个字符的分店名缩一号");

// ---- 箱标签 · 小标签 472 点宽 ----
const smallCartonCommand = buildPackCartonLabelCommand(carton, "small");
assert.ok(smallCartonCommand.startsWith("! 0 200 200 208 1\r\nPAGE-WIDTH 472\r\n"), "小标签页宽 472、页高不变");
assertWithinBounds(smallCartonCommand, 472, "小箱标签");
const smallCarton = parse(smallCartonCommand);
assert.ok(smallCarton.rows.includes("INVERSE-LINE 0 0 58 0 208"), "小标签类型条收窄到 58 点");
assert.ok(!smallCarton.rows.some((row) => row.startsWith("BARCODE 128")), "小标签同样不印一维码");
assert.ok(smallCarton.vtexts.some((row) => row.value === "BOX") && smallCarton.vtexts.some((row) => row.value === "CARTON"), "小标签 58 点黑条里也竖排 BOX / CARTON");
assert.ok(smallCartonCommand.includes("MA,BX2611130042\r\nENDQR"));

// ---- 托盘标签 · 总仓发往分店，经运输公司 ----
const pallet = {
  palletNo: "PL261113007",
  originName: "HB Warehouse",
  destinationType: 2,
  destinationName: "Cairns",
  carrierName: "Border Express",
  cartonCount: 5,
  pieceCount: 366,
  consignmentNo: "BEX 4471 0932 18",
  orderNo: null,
  printedAt: "13 NOV 15:06",
  operatorName: "Lily",
  sheetIndex: 1,
  sheetTotal: 2,
};
const palletCommand = buildPackPalletLabelCommand(pallet);
const palletParsed = parse(palletCommand);
assert.ok(palletCommand.startsWith("! 0 200 200 208 1\r\nPAGE-WIDTH 570\r\n"), "托盘标签页高 208、页宽 570");
assert.ok(palletParsed.vtexts.some((row) => row.value === "PLT"), "左侧类型条竖排写 PLT");
assert.ok(palletParsed.vtexts.some((row) => row.value === "PALLET"), "类型条里竖排写 PALLET");
assert.ok(palletParsed.rows.includes("BOX 0 0 569 207 6"), "托盘标签整圈粗黑框");
assert.ok(palletParsed.rows.includes("INVERSE-LINE 6 6 70 6 196"), "类型条只反白框内部分");
assert.ok(
  palletParsed.rows.indexOf("BOX 0 0 569 207 6") < palletParsed.rows.indexOf("INVERSE-LINE 6 6 70 6 196"),
  "先画框再反白，避免把框线反成白色"
);
assert.ok(palletParsed.texts.some((row) => row.value === "FROM HB WAREHOUSE"), "托盘 FROM 行只留发出地");
assert.ok(palletParsed.texts.some((row) => row.value === "VIA BORDER EXPRESS"), "经运输公司：底部左侧打完整的 VIA <承运商>（放在 FROM 行会被截断）");
assert.ok(palletParsed.texts.some((row) => row.value === "CAIRNS"));
assert.ok(palletParsed.texts.some((row) => row.value === "PL261113") && palletParsed.texts.some((row) => row.value === "007"));
assert.ok(palletParsed.texts.some((row) => row.value === "5 CTN - 366 PCS"), "数量为 N CTN - M PCS");
assert.ok(palletParsed.texts.some((row) => row.value === "CN BEX 4471 0932 18"), "右下第一行运单号（consignmentNo）");
assert.ok(palletParsed.texts.some((row) => row.value === "1/2" && row.font === 7), "托盘带 1/2 张数");
assert.ok(palletParsed.texts.some((row) => row.value.startsWith("13 NOV 15:06")), "右下时间完整");
assert.ok(!palletParsed.rows.some((row) => row.startsWith("BARCODE 128")), "托盘标签不再印一维码");
assert.ok(palletCommand.includes("MA,PL261113007\r\nENDQR"), "二维码内容是完整托盘号");
assertWithinBounds(palletCommand, 570 - 6, "托盘标签");

// ---- 托盘标签 · 自己送（没有运输公司）：DIRECT，右下改打订货单号 ----
const direct = parse(
  buildPackPalletLabelCommand({
    ...pallet,
    originName: "Sunnybank",
    carrierName: null,
    destinationName: "Inala",
    consignmentNo: null,
    orderNo: "2026-0398",
    sheetIndex: 2,
  })
);
assert.ok(direct.texts.some((row) => row.value === "FROM SUNNYBANK"));
assert.ok(direct.texts.some((row) => row.value === "DIRECT"), "自己送 / 对方来取打 DIRECT");
assert.ok(direct.texts.some((row) => row.value === "ORDER 2026-0398"), "没有运单号时右下打订货单号");
assert.ok(direct.texts.some((row) => row.value === "2/2"));

// 本地存放的托盘：TO 打 STORAGE，FROM 不打 VIA / DIRECT
const palletStorage = parse(
  buildPackPalletLabelCommand({ ...pallet, destinationType: 1, destinationName: null, carrierName: null, consignmentNo: null })
);
assert.ok(palletStorage.texts.some((row) => row.value === "FROM HB WAREHOUSE"));
assert.ok(palletStorage.texts.some((row) => row.value === "STORAGE"));
// 未指定去向的托盘
const palletUnassigned = parse(
  buildPackPalletLabelCommand({ ...pallet, destinationType: 0, destinationName: null, carrierName: null, consignmentNo: null })
);
assert.ok(palletUnassigned.texts.some((row) => row.value === "FROM HB WAREHOUSE"));
assert.ok(palletUnassigned.texts.some((row) => row.value === "UNASSIGNED"));

// ---- 托盘标签 · 小标签：承运商超宽截断，右下不打操作人 ----
const smallPalletCommand = buildPackPalletLabelCommand(
  { ...pallet, carrierName: "Border Express Northern Queensland Linehaul" },
  "small"
);
const smallPallet = parse(smallPalletCommand);
assert.ok(smallPalletCommand.includes("PAGE-WIDTH 472"));
assert.ok(smallPallet.rows.includes("BOX 0 0 471 207 6"));
assertWithinBounds(smallPalletCommand, 472 - 6, "小托盘标签");
const smallRoute = smallPallet.texts.find((row) => row.value.startsWith("VIA BORDER"));
assert.ok(smallRoute && !smallRoute.value.includes("LINEHAUL"), "承运商名称超过宽度时截断");
assert.ok(smallPallet.texts.some((row) => row.value === "FROM HB WAREHOUSE"), "小托盘 FROM 行只留发出地");
assert.ok(smallPallet.texts.some((row) => row.value === "13 NOV 15:06"), "小托盘标签只打时间");
assert.ok(!smallPallet.texts.some((row) => row.value.includes("LILY")), "小托盘标签不打操作人");
assert.ok(smallPallet.texts.some((row) => row.value === "1/2"));

// 张数不合法时不打印张数；编号为空时拒绝生成。
assert.ok(
  !parse(buildPackPalletLabelCommand({ ...pallet, sheetIndex: 3, sheetTotal: 2 })).texts.some((row) => row.value.includes("/")),
  "张数越界时不打印"
);
assert.throws(() => buildPackCartonLabelCommand({ ...carton, cartonNo: " " }), /number is required/);
// 换行等控制字符不能进入指令，避免伪造 CPCL 行。
const injected = buildPackCartonLabelCommand({ ...carton, destinationName: "A\r\nPRINT\r\nB" });
assert.equal(parse(injected).rows.filter((row) => row === "PRINT").length, 1, "字段里的换行被压成空格");

// ---- 版面内容（打印与弹层预览同源）----
const cartonSpec = buildPackCartonLabelSpec(carton);
assert.equal(cartonSpec.fromLine, "FROM SUNNYBANK");
assert.equal(cartonSpec.routeLine, "DIRECT");
assert.equal(cartonSpec.destinationName, "INALA");
assert.equal(cartonSpec.sheetText, "");
const palletSpec = buildPackPalletLabelSpec(pallet);
assert.equal(palletSpec.referenceLine, "CN BEX 4471 0932 18");
assert.equal(palletSpec.sheetText, "1/2");
assert.equal(palletSpec.framed, true);

console.log("pack-labels.test.ts passed");
