import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { shouldHidePrimaryTabBar } from "../navigation/primary-navigation";
import { classifyAttachBlankError, normalizeCartonRef } from "../warehouse-picking/pack-carton-logic";

// 批量箱标（预打印空箱标）的源码契约：新页面独立路由并登记在打包前缀下、设备会话规则不变、
// 拣货端「扫空箱标」只走拣货端接口、文案键齐全且中英占位符一致。

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const read = (relativePath: string) => readFile(resolve(mobileRoot, relativePath), "utf8");

type Json = { [key: string]: Json | string };

function lookup(tree: Json, path: string): Json | string | undefined {
  let node: Json | string | undefined = tree;
  for (const part of path.split(".")) {
    if (typeof node !== "object" || node === null) return undefined;
    node = node[part];
  }
  return node;
}

function placeholders(text: string) {
  return [...text.matchAll(/\{\{\s*(\w+)\s*\}\}/g)].map((match) => match[1]).sort().join(",");
}

function flatten(tree: Json, prefix = ""): [string, string][] {
  return Object.entries(tree).flatMap(([key, value]) =>
    typeof value === "string" ? [[`${prefix}${key}`, value] as [string, string]] : flatten(value, `${prefix}${key}.`),
  );
}

test("三个新页面是独立路由、按文件路径引入，不进打包模块的入口导出", async () => {
  for (const [route, screen] of [
    ["app/(shell)/packing/batch/index.tsx", "@/modules/packing/screens/BatchListScreen"],
    ["app/(shell)/packing/batch/new.tsx", "@/modules/packing/screens/BatchCreateScreen"],
    ["app/(shell)/packing/batch/[batchGuid].tsx", "@/modules/packing/screens/BatchPrintScreen"],
  ] as const) {
    await access(resolve(mobileRoot, route));
    assert.ok((await read(route)).includes(`from "${screen}"`), `${route} 要按文件路径引入页面`);
  }
  const barrel = await read("src/modules/packing/index.ts");
  assert.equal(/Batch/.test(barrel), false, "批量箱标页面不放进打包入口导出，没进页面就不加载");
});

test("新页面都在 /packing 全屏作业前缀下：不显示全局底栏；设备会话规则不变（打包屏蔽、拣货开放）", async () => {
  for (const path of ["/packing/batch", "/packing/batch/new", "/packing/batch/abc?autoprint=1"]) {
    assert.equal(shouldHidePrimaryTabBar(path), true, `${path} 在打包前缀下，底部是操作按钮，不显示全局底栏`);
  }
  const source = await read("src/modules/navigation/default-route.ts");
  const block = /const DEVICE_MODE_BLOCKED_ROUTE_NAMES = new Set\(\[([\s\S]*?)\]\);/.exec(source)?.[1] ?? "";
  assert.match(block, /"packing"/, "打包路由仍对设备会话屏蔽（接口只收账号），批量箱标页面随它一起屏蔽");
  assert.equal(/"warehouse-picking"/.test(block), false, "PDA 设备会话仍只开放拣货");
});

test("拣货端「扫空箱标」走拣货端接口：POST orders/{o}/cartons/attach，请求体只有 cartonRef，沿用拣货人请求头", async () => {
  const api = await read("src/modules/warehouse-picking/pack-api.ts");
  assert.match(api, /\/cartons\/attach`, \{ cartonRef \}, pickerConfig\(\)/);
  assert.equal(/packing\/cartons\/batch|labels-printed|void-blank/.test(api), false, "拣货端不碰打包端的批量接口");
  const hook = await read("src/modules/warehouse-picking/use-order-carton.ts");
  assert.match(hook, /attachBlankCarton\(orderGuid, blankRef\)/);
  assert.ok(hook.indexOf("sealOrderCarton(") < hook.indexOf("attachBlankCarton("), "先封好当前箱，再关联空箱标");
  const picking = await read("src/modules/warehouse-picking/screens/PickingScreen.tsx");
  assert.equal(/attach|blank/i.test(picking), false, "拣货页本身不加改动，逻辑都在 hook 与封箱弹层里");
  const sheet = await read("src/modules/warehouse-picking/components/PickSealSheet.tsx");
  assert.match(sheet, /key: "scan"/, "「下一只箱」多了「扫空箱标」");
  assert.match(sheet, /attachBlankRef/);
});

test("扫到的空箱标内容整理与关联失败分类：404 找不到 / 409 已经用过", () => {
  assert.equal(normalizeCartonRef("  BX2611150002\r\n"), "BX2611150002");
  assert.equal(normalizeCartonRef("   "), null);
  assert.equal(normalizeCartonRef(null), null);
  assert.equal(classifyAttachBlankError("CARTON_NOT_FOUND"), "notFound");
  assert.equal(classifyAttachBlankError("CARTON_NOT_BLANK"), "notBlank");
  assert.equal(classifyAttachBlankError("ORDER_NOT_PICKABLE"), null, "订单类错误走通用提示");
  assert.equal(classifyAttachBlankError(null), null);
});

test("批量箱标改动的界面里用到的文案键都在 zh 文案里", async () => {
  const files: [string, string][] = [
    ["src/modules/packing/screens/BatchCreateScreen.tsx", "packing"],
    ["src/modules/packing/screens/BatchListScreen.tsx", "packing"],
    ["src/modules/packing/screens/BatchPrintScreen.tsx", "packing"],
    ["src/modules/packing/screens/PackingHomeScreen.tsx", "packing"],
    ["src/modules/packing/screens/CartonScreen.tsx", "packing"],
    ["src/modules/packing/components/PrinterStatusTag.tsx", "packing"],
    ["src/modules/packing/components/SealSheet.tsx", "packing"],
    ["src/modules/warehouse-picking/components/PickSealSheet.tsx", "warehousePicking"],
    ["src/modules/warehouse-picking/use-order-carton.ts", "warehousePicking"],
  ];
  const missing: string[] = [];
  for (const [file, namespace] of files) {
    const zh = JSON.parse(await read(`src/locales/zh/screens/${namespace}.json`)) as Json;
    const source = await read(file);
    for (const match of source.matchAll(/\bt\(\s*"([A-Za-z][\w.]*)"/g)) {
      const exists = [match[1], `${match[1]}_other`, `${match[1]}_one`].some((key) => typeof lookup(zh, key) === "string");
      if (!exists) missing.push(`${file}: ${match[1]}`);
    }
  }
  assert.deepEqual(missing, []);
});

test("新增文案中英占位符一致，且英文标签纸 / 术语用 Carton / Label", async () => {
  for (const [namespace, groups] of [
    ["packing", ["batch", "blank", "select", "cartonList", "errors"]],
    ["warehousePicking", ["carton"]],
  ] as const) {
    const zh = JSON.parse(await read(`src/locales/zh/screens/${namespace}.json`)) as Json;
    const en = JSON.parse(await read(`src/locales/en/screens/${namespace}.json`)) as Json;
    for (const group of groups) {
      for (const [key, zhText] of flatten((zh[group] ?? {}) as Json, `${group}.`)) {
        const enText = lookup(en, key);
        assert.equal(typeof enText, "string", `${namespace} 英文缺少 ${key}`);
        assert.equal(placeholders(enText as string), placeholders(zhText), `${namespace} ${key} 的占位符中英不一致`);
      }
    }
  }
  const en = JSON.parse(await read("src/locales/en/screens/packing.json")) as Json;
  assert.match(lookup(en, "batch.createTitle") as string, /label/i);
  assert.match(lookup(en, "blank.activatedNotice") as string, /label/i);
});
