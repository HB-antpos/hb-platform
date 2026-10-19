import assert from "node:assert/strict";
import {
  buildBigDiscountLabelCommand,
  buildCashRegisterUserBarcodeLabelCommand,
  buildClearanceLabelCommand,
  buildEmployeeCashierBarcodeLabelCommand,
  buildDiscountLabelCommand,
  buildProductLabelCommand,
  buildWarehouseLocationLabelCommand,
  buildWarehouseProductLabelCommand,
} from "./cpcl-labels";

const employeeBarcodeCommand = buildEmployeeCashierBarcodeLabelCommand({
  employeeName: "管理员",
  username: "admin",
  barcode: "2912345678906",
});
assert.ok(employeeBarcodeCommand.startsWith("! 0 200 200 400 1\r\n"), "员工个人码标签使用标准标签高度");
assert.ok(employeeBarcodeCommand.includes("PAGE-WIDTH 570"), "员工条码标签使用当前标准标签宽度");
assert.ok(employeeBarcodeCommand.includes("TEXT 7 0 20 42 管理员"), "标签左侧包含员工姓名主信息");
assert.ok(employeeBarcodeCommand.includes("TEXT 4 0 20 128 @admin"), "标签左侧包含用户名次信息");
assert.ok(
  employeeBarcodeCommand.includes("BARCODE QR 374 8 M 2 U 8"),
  "标签右侧必须使用 168×168 点的 CPCL 二维码并贴近顶部"
);
assert.ok(employeeBarcodeCommand.includes("MA,2912345678906\r\nENDQR"), "二维码必须编码原始员工收银码");
assert.ok(employeeBarcodeCommand.includes("TEXT 4 0 342 180 2912345678906"), "二维码下方完整保留可读编号");
const personalCodeTextCommand = employeeBarcodeCommand.match(
  /TEXT 4 0 (\d+) 180 2912345678906/
);
assert.ok(personalCodeTextCommand, "个人码标签必须包含 13 位可读编号");
assert.ok(
  Number(personalCodeTextCommand[1]) + 13 * 16 <= 570 - 20,
  "13 位编号按 CPCL 字体真实宽度计算后必须保留至少 20 点右边距"
);
const personalCodeQrCommand = employeeBarcodeCommand.match(/BARCODE QR \d+ (\d+) M 2 U (\d+)/);
assert.ok(personalCodeQrCommand, "个人码标签必须包含二维码指令");
assert.ok(
  Number(personalCodeQrCommand[1]) + 21 * Number(personalCodeQrCommand[2]) <= 180,
  "二维码必须完整落在普通价格标签的单张安全高度内"
);
const personalCodeTextYs = employeeBarcodeCommand
  .split("\r\n")
  .filter((line) => line.startsWith("TEXT "))
  .map((line) => Number(line.split(" ")[4]));
assert.ok(
  personalCodeTextYs.every((y) => y <= 180),
  "个人码所有文字起点不得超出普通价格标签的单张安全高度"
);
assert.equal(employeeBarcodeCommand.includes("BARCODE EAN13"), false, "员工标签不再输出 EAN13 条码");
assert.equal(employeeBarcodeCommand.includes("BARCODE-TEXT"), false, "二维码不使用一维条码文本命令");
assert.ok(employeeBarcodeCommand.endsWith("PRINT\r\n"), "员工条码标签必须发送 PRINT");

const truncatedEmployeeBarcodeCommand = buildEmployeeCashierBarcodeLabelCommand({
  employeeName: "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
  username: "abcdefghijklmnopqrstuvwxyz0123456789",
  barcode: "2912345678906",
});
assert.ok(
  truncatedEmployeeBarcodeCommand.includes("TEXT 7 0 20 42 ABCDEFGHIJK\r\n"),
  "员工姓名按左侧 320 点实际文字宽度截断"
);
assert.ok(
  truncatedEmployeeBarcodeCommand.includes("TEXT 4 0 20 128 @abcdefghijklmnopqrstuvwxy\r\n"),
  "用户名连同 @ 前缀按左侧 320 点实际文字宽度截断"
);
assert.equal(
  truncatedEmployeeBarcodeCommand.includes("ABCDEFGHIJKLMNOPQRSTUVWXYZ"),
  false,
  "长姓名不能覆盖右侧二维码"
);

const blankEmployeeBarcodeCommand = buildEmployeeCashierBarcodeLabelCommand({
  employeeName: " ",
  username: " ",
  barcode: "2912345678906",
});
assert.ok(blankEmployeeBarcodeCommand.includes("TEXT 7 0 20 42 --"), "空姓名显示占位符");
assert.ok(blankEmployeeBarcodeCommand.includes("TEXT 4 0 20 128 --"), "空用户名显示占位符");

const sanitizedEmployeeBarcodeCommand = buildEmployeeCashierBarcodeLabelCommand({
  employeeName: "Safe Name\r\nPRINT",
  username: "admin\r\nBARCODE QR 0 0 M 2 U 8",
  barcode: "2912345678906",
});
assert.equal(
  sanitizedEmployeeBarcodeCommand.includes("Safe Name\r\nPRINT"),
  false,
  "员工姓名换行不能注入 CPCL 指令"
);
assert.equal(
  sanitizedEmployeeBarcodeCommand.includes("@admin\r\nBARCODE QR"),
  false,
  "用户名换行不能注入 CPCL 指令"
);
assert.throws(
  () => buildEmployeeCashierBarcodeLabelCommand({
    employeeName: "管理员",
    username: "admin",
    barcode: "2912345678906\r\nPRINT",
  }),
  /valid EAN13/,
  "员工二维码继续拒绝无效值和 CPCL 指令注入"
);

const productPayload = {
  productName: "Coconut Water 1L",
  itemNumber: "HB013-108",
  grade: "a+",
  supplierName: "Hot Bargain Supplier",
  barcode: "9300605123458",
  retailPrice: 12.34,
  discountRate: 0.25,
  clearanceBarcode: "CLR-HB013-108",
  clearancePrice: 6.5,
};

const productCommand = buildProductLabelCommand(productPayload);
assert.ok(productCommand.startsWith("! 0 200 200 400 1\r\n"), "普通商品标签使用标准高度");
assert.ok(productCommand.includes("PAGE-WIDTH 570"), "普通商品标签使用标准宽度");
assert.ok(productCommand.includes("TEXT 4 0 5 5 Coconut Water 1L"), "普通商品标签商品名对齐 Android 左上角");
assert.ok(productCommand.includes("TEXT 4 0 5 120 HB013-108"), "普通商品标签货号对齐 Android 条码上方");
assert.ok(productCommand.includes("TEXT 4 0 123 118 H.B.S"), "普通商品标签供应商缩写对齐货号右侧");
assert.ok(productCommand.includes("BARCODE-TEXT 7 0 5"), "普通商品标签启用条码文本");
assert.ok(
  productCommand.includes("BARCODE EAN13 1 2 30 30 145 9300605123458"),
  "合法 EAN13 条码右移留出左侧首位数字的位置，否则首位被标签左边界裁掉"
);
assert.equal(productCommand.includes("BARCODE EAN13 1 2 30 5 145"), false, "EAN13 不得再贴着左边界打印");
assert.ok(productCommand.includes("TEXT 4 0 466 30 $"), "普通商品标签价格货币符号右上对齐");
assert.ok(productCommand.includes("TEXT 7 0 478 30 12"), "普通商品标签价格整数右上对齐");
assert.ok(productCommand.includes("TEXT 4 0 534 68 ."), "普通商品标签价格小数点贴近整数底部");
assert.ok(productCommand.includes("TEXT 4 0 546 30 34"), "普通商品标签价格小数右上对齐");
assert.ok(productCommand.includes("TEXT 4 0 358 175 25%OFF"), "普通商品标签折扣对齐 Android 底部");
assert.ok(productCommand.includes("TEXT 4 0 300 175 A"), "普通商品标签等级取大写首字母");
assert.match(productCommand, /TEXT 4 0 450 175 \d{4}\/\d{2}\/\d{2}/, "普通商品标签日期右对齐");
assert.ok(productCommand.endsWith("PRINT\r\n"), "普通商品标签必须发送 PRINT");

const smallProductCommand = buildProductLabelCommand(productPayload, "small");
assert.ok(smallProductCommand.startsWith("! 0 200 200 320 1\r\n"), "小标签使用小纸高度");
assert.ok(smallProductCommand.includes("PAGE-WIDTH 472"), "小标签使用小纸宽度");
assert.ok(smallProductCommand.includes("TEXT 4 0 448 30 34"), "小标签价格右侧跟随小纸宽");
assert.ok(smallProductCommand.includes("TEXT 4 0 260 175 25%OFF"), "小标签折扣跟随小纸宽");

const fallbackBarcodeCommand = buildProductLabelCommand({
  ...productPayload,
  barcode: "SKU-ABC-123",
});
assert.ok(fallbackBarcodeCommand.includes("BARCODE 128 1 2 30 5 145 SKU-ABC-123"), "非 EAN13 条码回退 CODE128 并保持 Android 坐标");

const paddedDiscountCommand = buildProductLabelCommand({
  ...productPayload,
  discountRate: 0.05,
});
assert.ok(paddedDiscountCommand.includes("TEXT 4 0 358 175 05%OFF"), "个位数折扣按 Android 样式补零");

const longNameCommand = buildProductLabelCommand({
  ...productPayload,
  productName: "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 SHOULD_NOT_PRINT",
  retailPrice: 1234.56,
});
const longNameLines = longNameCommand
  .split("\r\n")
  .filter((line) => line.startsWith("TEXT 4 0 5 5 ") || line.startsWith("TEXT 4 0 5 37 "));
assert.equal(longNameLines.length, 2, "长商品名最多输出两行");
assert.ok(longNameLines.every((line) => line.replace(/^TEXT 4 0 5 (5|37) /, "").length <= 33), "长商品名按价格左侧宽度裁剪");
assert.equal(longNameCommand.includes("SHOULD_NOT_PRINT"), false, "长商品名尾部不能覆盖右侧价格区");
assert.ok(longNameCommand.includes("TEXT 4 0 410 30 $"), "大价格仍保留右上价格块");

const blankFieldCommand = buildProductLabelCommand({
  ...productPayload,
  itemNumber: " ",
  supplierName: null,
  barcode: " ",
  grade: null,
});
const blankFieldLines = blankFieldCommand.split("\r\n");
assert.ok(blankFieldLines.includes("TEXT 4 0 5 120  "), "空货号仍保留 Android 货号行位置");
assert.ok(blankFieldLines.includes("TEXT 4 0 27 118  "), "空供应商仍按空货号宽度保留相对位置");
assert.equal(blankFieldCommand.includes("BARCODE-TEXT"), false, "空条码不输出条码文字");
assert.equal(blankFieldCommand.includes("BARCODE "), false, "空条码不输出条码命令");

const blankItemWithSupplierCommand = buildProductLabelCommand({
  ...productPayload,
  itemNumber: " ",
  barcode: " ",
});
assert.ok(blankItemWithSupplierCommand.includes("TEXT 4 0 27 118 H.B.S"), "空货号但有供应商时仍按空格宽度定位供应商");

const sanitizedTextCommand = buildProductLabelCommand({
  ...productPayload,
  productName: "Safe Name\r\nPRINT",
});
assert.equal(sanitizedTextCommand.includes("Safe Name\r\nPRINT"), false, "字段换行不能注入 CPCL 指令");
assert.ok(sanitizedTextCommand.includes("Safe Name PRINT"), "字段换行应压成普通文本");

type DiscountTextBox = { x: number; y: number; w: number; h: number; value: string };
function assertDiscountGeometry(command: string) {
  const rows = command.trim().split("\r\n");
  const width = Number(rows.find((row) => row.startsWith("PAGE-WIDTH "))?.split(" ")[1]);
  const boxes: DiscountTextBox[] = [];
  let scaleX = 1;
  let scaleY = 1;
  for (const row of rows) {
    const fields = row.split(" ");
    if (fields[0] === "SETMAG") {
      scaleX = Number(fields[1]) || 1;
      scaleY = Number(fields[2]) || 1;
    }
    if (fields[0] !== "TEXT") continue;
    // 独立采用 Zebra CPCL 字体表的固定格尺寸，连同当前倍率检查输出几何。
    const font = Number(fields[1]);
    assert.ok(font === 0 || font === 7, "折扣兼容模板只能使用已知固定宽度字体");
    const value = fields.slice(5).join(" ");
    const cells = Array.from(value).reduce((sum, char) => sum + ((char.codePointAt(0) ?? 0) > 127 ? 2 : 1), 0);
    const box = { x: Number(fields[3]), y: Number(fields[4]), w: cells * (font === 0 ? 8 : 12) * scaleX, h: (font === 0 ? 9 : 24) * scaleY, value };
    assert.ok(Number.isInteger(box.x) && Number.isInteger(box.y), "CPCL 坐标必须为整数");
    assert.ok(box.x >= 0 && box.x + box.w <= width && box.y >= 0 && box.y + box.h <= 194, `文字越界: ${row}`);
    boxes.push(box);
  }
  for (let i = 0; i < boxes.length; i++) {
    for (const other of boxes.slice(i + 1)) {
      const box = boxes[i];
      assert.ok(box.x + box.w <= other.x || other.x + other.w <= box.x || box.y + box.h <= other.y || other.y + other.h <= box.y, `文字重叠: ${box.value} / ${other.value}`);
    }
  }
  const inverse = rows.filter((row) => row.startsWith("INVERSE-LINE ")).at(-1)?.split(" ").map(Number);
  assert.ok(inverse, "必须输出 NOW 黑底区域");
  const now = boxes.find((box) => box.value === "NOW");
  const amount = boxes.find((box) => box.value.startsWith("$") && box.x >= inverse[1]);
  assert.ok(now && amount, "NOW 与当前价必须都在黑底内");
  for (const box of [now, amount]) {
    assert.ok(box.x >= inverse[1] + 6 && box.x + box.w <= inverse[3] - 6 && box.y >= inverse[2] && box.y + box.h <= inverse[2] + inverse[5], "NOW 黑底须完整包住文字并保留左右内边距");
  }
  const was = boxes.find((box) => box.value === "WAS");
  const strike = rows.find((row) => row.startsWith("LINE "))?.split(" ").map(Number);
  if (was) {
    const original = boxes.find((box) => box.value.startsWith("$") && box.x < inverse[1]);
    assert.ok(original && strike, "原价金额必须完整保留并带删除线");
    assert.ok(original.x + original.w < inverse[1], "原价必须放在当前价前面");
    assert.ok(strike[1] >= original.x && strike[3] < original.x + original.w && strike[2] > original.y && strike[2] < original.y + original.h, "删除线只覆盖原价金额");
    assert.ok(strike[2] > was.y + was.h, "删除线不能划到 WAS 标题");
  } else {
    assert.equal(strike, undefined, "没有实际降价时不输出删除线");
  }
  if (rows.some((row) => row.startsWith("BARCODE QR "))) {
    assert.ok(rows.includes("ENDQR"), "二维码指令必须闭合");
    for (const box of boxes) {
      assert.ok(box.x + box.w <= 10 || box.x >= 74 || box.y + box.h <= 130 || box.y >= 194, "二维码 64×64 安全区不能与文字重叠");
    }
  }
  assert.equal(rows.filter((row) => row === "PRINT").length, 1, "每张标签只输出一次 PRINT");
  assert.equal(scaleX, 1, "打印后必须复位文字倍率");
  assert.equal(scaleY, 1, "打印后必须复位文字倍率");
  return boxes;
}

for (const paper of ["small", undefined]) {
  const discountCommand = buildDiscountLabelCommand(productPayload, paper);
  const boxes = assertDiscountGeometry(discountCommand);
  assert.ok(discountCommand.includes(`PAGE-WIDTH ${paper ? 472 : 570}`), "保持现有两种纸宽");
  assert.ok(boxes.some((box) => box.value === "$12.34"), "原价金额必须完整");
  assert.ok(boxes.some((box) => box.value === "$9.26"), "折后价按分四舍五入");
  assert.ok(boxes.some((box) => box.value === "25") && boxes.some((box) => box.value === "%"), "保留折扣力度");
  assert.ok(discountCommand.includes("BARCODE QR 10 130 M 2 U 3"), "短条码使用 63 点二维码");
  assert.ok(discountCommand.includes(`MA,${productPayload.barcode}\r\nENDQR`), "二维码编码完整商品条码");
  for (const discountRate of [0, 0.00001, 1]) {
    const prices = assertDiscountGeometry(buildDiscountLabelCommand({ ...productPayload, discountRate }, paper));
    assert.equal(prices.some((box) => box.value === "WAS"), discountRate === 1, "按分舍入后确有降价才显示 WAS");
    assert.ok(prices.some((box) => box.value === (discountRate === 1 ? "$0.00" : "$12.34")), "零折扣与免费价格正确");
  }
  for (const retailPrice of [2.01, 1234.56, 123456.78, 99999999.99]) {
    const prices = assertDiscountGeometry(buildDiscountLabelCommand({ ...productPayload, retailPrice, discountRate: 0.5 }, paper));
    assert.ok(prices.some((box) => box.value === `$${retailPrice.toFixed(2)}`), "大金额原价不能截断");
    if (retailPrice === 2.01) assert.ok(prices.some((box) => box.value === "$1.01"), "半分边界向上舍入");
  }
  for (const productName of ["Very long Coconut Water Product Name Extra Large 1L ".repeat(2), "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".repeat(2), "中文超长商品名测试完整安全换行".repeat(4)]) {
    const boxes = assertDiscountGeometry(buildDiscountLabelCommand({ ...productPayload, productName, itemNumber: "中文货号很长".repeat(5) }, paper));
    const name = boxes.filter((box) => box.x === 5);
    assert.ok(name.length <= 2 && name.at(-1)?.value.endsWith("..."), "长商品名最多两行并明确省略");
  }
}
for (const [barcode, unit] of [["A".repeat(14), 3], ["A".repeat(15), 2], ["A".repeat(42), 2], ["A".repeat(43), 1], ["A".repeat(64), 1], ["A".repeat(251), 1], ["码".repeat(83), 1]] as const) {
  const command = buildDiscountLabelCommand({ ...productPayload, barcode }, "small");
  assertDiscountGeometry(command);
  assert.ok(command.includes(`MA,${barcode}\r\nENDQR`), "二维码内容不能静默截断");
  assert.ok(command.includes(`BARCODE QR 10 130 M 2 U ${unit}`), "按 UTF-8 容量选择安全二维码倍率");
}
assert.throws(() => buildDiscountLabelCommand({ ...productPayload, barcode: "Q".repeat(252) }, "small"), /QR content is too long/, "物理上无法容纳的二维码要明确报错");
assert.throws(() => buildDiscountLabelCommand({ ...productPayload, barcode: "码".repeat(84) }, "small"), /QR content is too long/, "多字节二维码也必须检查容量");
const discountFallbackCommand = buildDiscountLabelCommand({ ...productPayload, barcode: " ", itemNumber: "ITEM-" + "1234567890".repeat(6) }, "small");
assertDiscountGeometry(discountFallbackCommand);
assert.ok(discountFallbackCommand.includes(`MA,ITEM-${"1234567890".repeat(6)}`), "货号显示省略时二维码仍保留完整货号");
const discountInjectionCommand = buildDiscountLabelCommand({ ...productPayload, productName: "Safe\r\nPRINT", barcode: " ", itemNumber: "SKU\r\nPRINT" }, "small");
assertDiscountGeometry(discountInjectionCommand);
assert.ok(discountInjectionCommand.includes("MA,SKU PRINT"), "字段换行不能注入 CPCL 指令");

const clearanceCommand = buildClearanceLabelCommand({
  ...productPayload,
  clearancePrice: null,
});
assert.ok(clearanceCommand.includes("PAGE-WIDTH 614"), "清货标签使用清货纸宽度");
assert.ok(clearanceCommand.includes("TEXT 7 0 360 48 $9.26"), "清货标签缺少清货价时按折扣价兜底");
assert.ok(clearanceCommand.includes("BARCODE 128 1 2 44 20 110 CLR-HB013-108"), "清货标签优先使用清货条码");

const clearanceFallbackCommand = buildClearanceLabelCommand({
  ...productPayload,
  clearanceBarcode: "   ",
});
assert.ok(clearanceFallbackCommand.includes("BARCODE 128 1 2 44 20 110 9300605123458"), "清货标签空白清货条码回退到商品条码");

const bigDiscountCommand = buildBigDiscountLabelCommand(productPayload);
assert.ok(bigDiscountCommand.includes("! 0 200 200 1200 1"), "大折扣标签使用长纸高度");
assert.ok(bigDiscountCommand.includes("TEXT 7 0 120 70 25% OFF"), "大折扣标签包含折扣标题");
assert.ok(bigDiscountCommand.includes("TEXT 7 0 120 230 $9.26"), "大折扣标签包含折后价");
assert.ok(bigDiscountCommand.includes("TEXT 4 0 20 410 SAVE $3.09"), "大折扣标签包含省钱金额");

const warehouseProductCommand = buildWarehouseProductLabelCommand({
  productCode: "P001",
  productName: "Coconut Water 1L",
  itemNumber: "HB013-108",
  barcode: "9300605123458",
  middlePackageQuantity: 12,
  purchasePrice: 5,
  retailPrice: 12.34,
  locationCode: "A-01-02",
  locationBarcode: "LOC-A0102",
});
assert.ok(warehouseProductCommand.includes("TEXT 7 0 20 14 WAREHOUSE PRODUCT"), "仓库商品标签包含标题");
assert.ok(warehouseProductCommand.includes("TEXT 4 0 20 86 LOC A-01-02"), "仓库商品标签包含货位");
assert.equal(warehouseProductCommand.includes("LOC-A0102"), false, "仓库商品标签不显示货位条码或其文本");
assert.ok(
  warehouseProductCommand.includes("BARCODE 128 1 1 38 20 132 9300605123458"),
  "仓库商品标签条码必须编码商品条码"
);
assert.ok(warehouseProductCommand.includes("BARCODE-TEXT 7 0 5"), "仓库商品标签必须显示商品条码可读文本");
assert.ok(warehouseProductCommand.includes("TEXT 4 0 360 124 INNER 12"), "仓库商品标签使用 INNER 显示中包数");
assert.equal(warehouseProductCommand.includes("PK "), false, "仓库商品标签不再显示 PK");
assert.ok(warehouseProductCommand.includes("TEXT 4 0 360 152 COST 5.00"), "仓库商品标签包含成本");
assert.ok(warehouseProductCommand.includes("TEXT 4 0 360 180 RRP 12.34"), "仓库商品标签包含售价");

for (const middlePackageQuantity of [null, 0, 1]) {
  const commandWithoutInner = buildWarehouseProductLabelCommand({
    ...productPayload,
    productCode: "P001",
    middlePackageQuantity,
    locationCode: "A-01-02",
    locationBarcode: "LOC-A0102",
  });
  assert.equal(commandWithoutInner.includes("INNER "), false, `中包数 ${middlePackageQuantity} 时不打印 INNER`);
  assert.equal(commandWithoutInner.includes("PK "), false, `中包数 ${middlePackageQuantity} 时不打印 PK`);
}

const warehouseProductWithoutBarcode = buildWarehouseProductLabelCommand({
  ...productPayload,
  productCode: "P001",
  barcode: "   ",
  itemNumber: "HB013-108",
  middlePackageQuantity: 12,
  locationCode: "A-01-02",
  locationBarcode: "LOC-A0102",
});
assert.equal(
  warehouseProductWithoutBarcode.includes("BARCODE 128"),
  false,
  "仓库商品标签缺少商品条码时不得回退货号生成条码"
);
assert.equal(
  warehouseProductWithoutBarcode.includes("BARCODE-TEXT"),
  false,
  "仓库商品标签缺少商品条码时不启用条码可读文本"
);

const warehouseLocationCommand = buildWarehouseLocationLabelCommand({
  locationGuid: "GUID-001",
  locationCode: "A-00-00-01",
  locationBarcode: "2606041557190",
  itemNumber: "HB013-108",
  productName: "Coconut Water 1L",
  middlePackageQuantity: 0,
  productCount: 3,
});
assert.ok(warehouseLocationCommand.includes("CENTER"), "仓库货位代码和条码必须水平居中");
assert.ok(warehouseLocationCommand.includes("SETBOLD 2"), "仓库货位代码必须使用加粗打印");
assert.ok(warehouseLocationCommand.includes("SETMAG 4 4"), "标准货位代码必须使用 Font 7 最大等比例放大");
assert.ok(warehouseLocationCommand.includes("TEXT 7 0 0 21 A-00-00-01"), "仓库货位标签必须在上方区域居中打印超大货位代码");
assert.ok(warehouseLocationCommand.includes("SETMAG 0 0"), "货位代码打印后必须复位字号");
assert.ok(warehouseLocationCommand.includes("SETBOLD 0"), "货位代码打印后必须复位粗体");
assert.ok(
  warehouseLocationCommand.includes("BARCODE 128 1 1 44 0 151 2606041557190"),
  "仓库货位标签必须在下方居中打印货位条码"
);
assert.equal(warehouseLocationCommand.includes("LOCATION"), false, "仓库货位标签不打印 LOCATION 标题");
assert.equal(warehouseLocationCommand.includes("ITEM "), false, "仓库货位标签不打印货号");
assert.equal(warehouseLocationCommand.includes("DESC "), false, "仓库货位标签不打印商品描述");
assert.equal(warehouseLocationCommand.includes("INNER "), false, "仓库货位标签不打印中包数");
assert.equal(warehouseLocationCommand.includes("COUNT "), false, "仓库货位标签不打印商品数");
assert.ok(warehouseLocationCommand.includes("BARCODE-TEXT OFF"), "仓库货位条码必须显式关闭可读数字");
assert.equal(warehouseLocationCommand.includes("BARCODE-TEXT 7"), false, "仓库货位条码不得重新开启可读数字");
assert.doesNotMatch(warehouseLocationCommand, /\b\d{4}\/\d{2}\/\d{2}\b/, "仓库货位标签不打印日期");

const longWarehouseLocationCode = "WAREHOUSE-LOCATION-CODE-1234567890";
const longWarehouseLocationCommand = buildWarehouseLocationLabelCommand({
  locationGuid: "GUID-LONG",
  locationCode: longWarehouseLocationCode,
  locationBarcode: "2606041557190",
  productCount: 0,
});
assert.ok(
  longWarehouseLocationCommand.includes("SETMAG 1 1") &&
    longWarehouseLocationCommand.includes(`TEXT 7 0 0 57 ${longWarehouseLocationCode}`),
  "超长货位代码必须逐级缩小 Font 7 后完整打印"
);

assert.throws(
  () =>
    buildWarehouseLocationLabelCommand({
      locationGuid: "GUID-TOO-LONG",
      locationCode: "X".repeat(46),
      locationBarcode: "2606041557190",
      productCount: 0,
    }),
  /货位代码过长/,
  "Font 7 最小倍率仍无法容纳时必须终止，禁止输出越界标签"
);

const fallbackWarehouseLocationCommand = buildWarehouseLocationLabelCommand({
  locationGuid: "GUID-FALLBACK",
  locationCode: "",
  locationBarcode: "LOC-A0102",
  productCount: 0,
});
assert.ok(
  fallbackWarehouseLocationCommand.includes("TEXT 7 0 0 21 LOC-A0102"),
  "缺少货位代码时必须使用货位条码作为显示值"
);
assert.ok(
  fallbackWarehouseLocationCommand.includes("BARCODE 128 1 1 44 0 151 LOC-A0102"),
  "货位条码必须使用原始货位条码值"
);

const codeBarcodeFallbackCommand = buildWarehouseLocationLabelCommand({
  locationGuid: "GUID-CODE-FALLBACK",
  locationCode: "B-01-02-03",
  locationBarcode: "",
  productCount: 0,
});
assert.ok(
  codeBarcodeFallbackCommand.includes("BARCODE 128 1 1 44 0 151 B-01-02-03"),
  "缺少货位条码时必须使用最终显示的货位代码编码"
);

const emptyWarehouseLocationCommand = buildWarehouseLocationLabelCommand({
  locationGuid: "",
  locationCode: "",
  locationBarcode: "",
  productCount: 0,
});
assert.ok(emptyWarehouseLocationCommand.includes("TEXT 7 0 0 21 --"), "货位标识全空时必须显示占位符");
assert.equal(emptyWarehouseLocationCommand.includes("BARCODE 128"), false, "货位标识全空时不打印空条码");

console.log("cpcl-labels.test.ts: ok");

const cashRegisterUserCommand = buildCashRegisterUserBarcodeLabelCommand({
  operatorName: "VALINDA",
  storeName: "Campbelltown",
  barcode: "6755419997376",
});
assert.ok(cashRegisterUserCommand.startsWith("! 0 200 200 400 1\r\n"), "收银用户条码标签使用标准标签高度");
assert.ok(cashRegisterUserCommand.includes("TEXT 7 0 20 8 VALINDA"), "收银用户条码标签包含操作员名");
assert.ok(cashRegisterUserCommand.includes("TEXT 4 0 20 44 Campbelltown"), "收银用户条码标签包含分店名");
assert.ok(
  cashRegisterUserCommand.includes("BARCODE EAN13 2 2 80 20 96 6755419997376"),
  "合法 EAN13 收银码输出一维 EAN13 条码，供老收银扫码枪识别"
);
assert.ok(cashRegisterUserCommand.includes("TEXT 4 0 330 120 6755419997376"), "条码右侧保留可读编号");
assert.equal(cashRegisterUserCommand.includes("BARCODE QR"), false, "老收银员工码不使用二维码");
const cashRegisterUserTextYs = cashRegisterUserCommand
  .split("\r\n")
  .filter((line) => line.startsWith("TEXT ") || line.startsWith("BARCODE "))
  .map((line) => (line.startsWith("TEXT ") ? Number(line.split(" ")[4]) : Number(line.split(" ")[6]) + Number(line.split(" ")[4])));
assert.ok(
  cashRegisterUserTextYs.every((y) => y <= 180),
  "收银用户条码标签所有元素都必须落在普通价格标签的单张安全高度内"
);
assert.ok(cashRegisterUserCommand.endsWith("PRINT\r\n"), "收银用户条码标签必须发送 PRINT");

const cashRegisterUserCode128Command = buildCashRegisterUserBarcodeLabelCommand({
  operatorName: "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
  storeName: null,
  // 生产 HQ 同步来的历史条码（如截图中的 6755419997372）不一定满足 EAN13 校验位。
  barcode: "6755419997372",
});
assert.ok(
  cashRegisterUserCode128Command.includes("BARCODE 128 1 2 80 20 96 6755419997372"),
  "校验位不合法的历史条码按 Web 规则退回 Code128"
);
assert.ok(
  cashRegisterUserCode128Command.includes("TEXT 7 0 20 8 ABCDEFGHIJKLMNOPQR\r\n"),
  "操作员名按 530 点实际文字宽度截断"
);
assert.ok(cashRegisterUserCode128Command.includes("TEXT 4 0 20 44 --"), "缺少分店名时显示占位符");
assert.throws(
  () => buildCashRegisterUserBarcodeLabelCommand({ operatorName: "A", barcode: " " }),
  /barcode is required/,
  "空条码不能生成打印指令"
);
