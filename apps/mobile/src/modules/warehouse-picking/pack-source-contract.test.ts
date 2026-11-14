import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// 拣货与装箱联动（二期）的源码契约：不带箱时拣货写入不变、新页面独立路由、设备会话规则不变、文案键齐全。

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const read = (relativePath: string) => readFile(resolve(mobileRoot, relativePath), "utf8");

function count(source: string, pattern: RegExp) {
  return (source.match(pattern) ?? []).length;
}

test("拣货页只通过 withCartonGuid 往写请求里加 cartonGuid：不带箱时与旧版逐字节一致", async () => {
  const source = await read("src/modules/warehouse-picking/screens/PickingScreen.tsx");
  const writes = count(source, /\bpostPickRecord\(/g) + count(source, /\bputPickLineTotal\(/g);
  assert.equal(writes, 3, "拣货页有 3 处带数量的写入：扫码、加减、改总数");
  assert.equal(count(source, /\bwithCartonGuid\(/g), 3, "三处写入都要经过 withCartonGuid");
  assert.equal(/\bcartonGuid\s*:/.test(source), false, "拣货页不能直接写 cartonGuid 字段");
  // 当前箱在入队时取（不是发送时），换箱不会让已入队的扫码落到别的箱上。
  assert.equal(count(source, /packGetCartonGuid\(\)/g), 3);
  // 装箱逻辑都在 hook 与组件里，拣货页只最小接入。
  assert.match(source, /useOrderCarton\(/);
  assert.match(source, /<PickCartonBar /);
  assert.match(source, /<PickSealSheet /);
});

test("拣货写入没有当前箱时请求体里没有 cartonGuid 字段", async () => {
  const api = await read("src/modules/warehouse-picking/api.ts");
  assert.match(api, /cartonGuid\?: string \| null/, "cartonGuid 是可选字段，不传则与旧版一致");
  // 网络重试沿用拣货的 clientRequestId 幂等，不引入新的重试逻辑。
  const hook = await read("src/modules/warehouse-picking/use-order-carton.ts");
  assert.equal(/withNetworkRetry|setTimeout/.test(hook), false, "装箱 hook 不自带重试逻辑");
});

test("新页面是独立路由、按文件路径引入，不进拣货模块的入口导出", async () => {
  for (const [route, screen] of [
    ["app/(shell)/warehouse-picking/[orderGuid]/pack.tsx", "@/modules/warehouse-picking/screens/PackLaterScreen"],
    ["app/(shell)/warehouse-picking/[orderGuid]/pack-review.tsx", "@/modules/warehouse-picking/screens/PackReviewScreen"],
    ["app/(shell)/packing/receive-order.tsx", "@/modules/packing/screens/ReceiveOrderScreen"],
  ] as const) {
    await access(resolve(mobileRoot, route));
    assert.ok((await read(route)).includes(`from "${screen}"`), `${route} 要按文件路径引入页面`);
  }
  const barrel = await read("src/modules/warehouse-picking/index.ts");
  assert.equal(/PackLaterScreen|PackReviewScreen/.test(barrel), false, "先拣后装 / 核对页不放进拣货入口导出，没进页面就不加载");
  const packingBarrel = await read("src/modules/packing/index.ts");
  assert.equal(/ReceiveOrderScreen/.test(packingBarrel), false);
});

test("设备会话规则不变：打包仍对设备会话屏蔽，拣货仍开放；新页面都在这两个前缀下", async () => {
  const source = await read("src/modules/navigation/default-route.ts");
  const block = /const DEVICE_MODE_BLOCKED_ROUTE_NAMES = new Set\(\[([\s\S]*?)\]\);/.exec(source)?.[1] ?? "";
  assert.match(block, /"packing"/, "打包路由对设备会话屏蔽（接口只收账号）");
  assert.equal(/"warehouse-picking"/.test(block), false, "拣货路由对 PDA 设备会话仍然开放");
  // 全屏作业前缀已覆盖新页面。
  const navigation = await read("src/modules/navigation/primary-navigation.ts");
  assert.match(navigation, /FULL_SCREEN_TASK_ROUTE_PREFIXES = \["\/warehouse-picking", "\/packing"/);
});

test("先拣后装的「+1」请求体是绝对件数：设置数量走 PUT items，不用增量接口", async () => {
  const api = await read("src/modules/warehouse-picking/pack-api.ts");
  assert.match(api, /apiClient\.put\(/);
  assert.match(api, /\{ quantity \}/);
  const screen = await read("src/modules/warehouse-picking/screens/PackLaterScreen.tsx");
  assert.match(screen, /setOrderCartonItem\(/);
  assert.equal(/scanIntoCarton|postPickRecord/.test(screen), false, "先拣后装不能走会改拣货数的接口");
});

test("核对页「按装箱数改拣货数」不带 cartonGuid", async () => {
  const screen = await read("src/modules/warehouse-picking/screens/PackReviewScreen.tsx");
  assert.match(screen, /putPickLineTotal\(/);
  assert.equal(/cartonGuid\s*:/.test(screen), false);
  assert.equal(/withCartonGuid/.test(screen), false);
});

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

const NEW_PICKING_FILES = [
  "src/modules/warehouse-picking/use-order-carton.ts",
  "src/modules/warehouse-picking/components/PickCartonBar.tsx",
  "src/modules/warehouse-picking/components/PickSealSheet.tsx",
  "src/modules/warehouse-picking/components/PackFinishCard.tsx",
  "src/modules/warehouse-picking/components/UpNextList.tsx",
  "src/modules/warehouse-picking/components/CurrentLineCard.tsx",
  "src/modules/warehouse-picking/screens/PackLaterScreen.tsx",
  "src/modules/warehouse-picking/screens/PackReviewScreen.tsx",
  "src/modules/warehouse-picking/screens/PickFinishScreen.tsx",
  "src/modules/warehouse-picking/screens/PickOrderListView.tsx",
  "src/modules/warehouse-picking/screens/PickingScreen.tsx",
];
const NEW_PACKING_FILES = [
  "src/modules/packing/screens/ReceiveOrderScreen.tsx",
  "src/modules/packing/components/OrderReceiveSheet.tsx",
  "src/modules/packing/components/OrderReceiveEntry.tsx",
  "src/modules/packing/screens/ReceiveScreen.tsx",
];

async function missingKeys(files: string[], localePath: string) {
  const zh = JSON.parse(await read(`src/locales/zh/screens/${localePath}.json`)) as Json;
  const missing: string[] = [];
  for (const file of files) {
    const source = await read(file);
    for (const match of source.matchAll(/\bt\(\s*"([A-Za-z][\w.]*)"/g)) {
      // 带 count 的键用 i18next 复数写法（key_one / key_other），也算存在。
      const exists = [match[1], `${match[1]}_other`, `${match[1]}_one`].some((key) => typeof lookup(zh, key) === "string");
      if (!exists) missing.push(`${file}: ${match[1]}`);
    }
  }
  return missing;
}

test("新增与改动的界面里用到的文案键都在 zh 文案里（命名空间按文件）", async () => {
  assert.deepEqual(await missingKeys(NEW_PICKING_FILES, "warehousePicking"), []);
  assert.deepEqual(await missingKeys(NEW_PACKING_FILES, "packing"), []);
});

test("新增文案中英占位符一致：改动 / 新增的分组里每个键的 {{变量}} 两种语言相同", async () => {
  for (const [localePath, groups] of [
    ["warehousePicking", ["carton", "review", "packLater", "finish", "orders", "errors"]],
    ["packing", ["orderReceive", "receive"]],
  ] as const) {
    const zh = JSON.parse(await read(`src/locales/zh/screens/${localePath}.json`)) as Json;
    const en = JSON.parse(await read(`src/locales/en/screens/${localePath}.json`)) as Json;
    for (const group of groups) {
      for (const [key, zhText] of flatten((zh[group] ?? {}) as Json, `${group}.`)) {
        const enText = lookup(en, key);
        assert.equal(typeof enText, "string", `${localePath} 英文缺少 ${key}`);
        // 中文复数写法（_one/_other）只比较英文有对应键的情况。
        assert.equal(placeholders(enText as string), placeholders(zhText), `${localePath} ${key} 的占位符中英不一致`);
      }
    }
  }
});
