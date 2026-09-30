import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { TAB_PATHS } from "./default-route";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");

const EXISTING_REAL_PAGE_FILES = [
  "app/(auth)/login.tsx",
  "app/privacy.tsx",
  "app/(shell)/home.tsx",
  "app/(shell)/cart.tsx",
  "app/(shell)/orders.tsx",
  "app/(shell)/preorders/index.tsx",
  "app/(shell)/preorders/[activationGuid].tsx",
  "app/(shell)/installment-orders.tsx",
  "app/(shell)/store-vouchers.tsx",
  "app/(shell)/seasonal-cards.tsx",
  "app/(shell)/warehouse.tsx",
  "app/(shell)/containers/index.tsx",
  "app/(shell)/containers/[containerGuid]/index.tsx",
  "app/(shell)/product-query.tsx",
  "app/(shell)/product-insights.tsx",
  "app/(shell)/domestic-purchase.tsx",
  "app/(shell)/local-supplier-invoices.tsx",
  "app/(shell)/advertisements.tsx",
  "app/(shell)/promotions.tsx",
  "app/(shell)/reports.tsx",
  "app/(shell)/attendance-personal.tsx",
  "app/(shell)/attendance-management.tsx",
  "app/(shell)/users/index.tsx",
  "app/(shell)/staff/[userGuid].tsx",
  "app/(shell)/users/[userGuid]/access.tsx",
  "app/(shell)/users/[userGuid]/pos-terminal-permissions.tsx",
  "app/(shell)/device-management.tsx",
  "app/(shell)/employee-profile.tsx",
  "app/(shell)/employee-profile-review/index.tsx",
  "app/(shell)/employee-profile-review/[requestId].tsx",
  "app/(shell)/settings.tsx",
] as const;

const EXISTING_TAB_PATHS = {
  home: "/(shell)/home",
  orders: "/(shell)/orders",
  cart: "/(shell)/cart",
  warehouse: "/(shell)/warehouse",
  "domestic-purchase": "/(shell)/domestic-purchase",
  "local-supplier-invoices": "/(shell)/local-supplier-invoices",
  "installment-orders": "/(shell)/installment-orders",
  advertisements: "/(shell)/advertisements",
  promotions: "/(shell)/promotions",
  reports: "/(shell)/reports",
  "store-vouchers": "/(shell)/store-vouchers",
  "seasonal-cards": "/(shell)/seasonal-cards",
  containers: "/(shell)/containers",
  "attendance-personal": "/(shell)/attendance-personal",
  "attendance-management": "/(shell)/attendance-management",
  "product-query": "/(shell)/product-query",
  "product-insights": "/(shell)/product-insights",
  users: "/(shell)/users",
  "user-admin": "/(shell)/user-admin",
  roles: "/(shell)/roles",
  "employee-profile": "/(shell)/employee-profile",
  "employee-profile-review": "/(shell)/employee-profile-review",
  "device-management": "/(shell)/device-management",
  settings: "/(shell)/settings",
} as const;

async function assertFileExists(relativePath: string) {
  await access(resolve(mobileRoot, relativePath));
}

async function run() {
  assert.equal(
    EXISTING_REAL_PAGE_FILES.length,
    31,
    "导航改版前的 31 个真实业务页面必须全部保留"
  );
  await Promise.all(EXISTING_REAL_PAGE_FILES.map(assertFileExists));
  await assertFileExists("app/(shell)/workbench.tsx");
  await assertFileExists("app/(shell)/containers/_layout.tsx");
  await assertFileExists("app/(shell)/users/_layout.tsx");

  assert.equal(
    EXISTING_REAL_PAGE_FILES.filter((path) => path.startsWith("app/(shell)/")).length,
    29,
    "除认证和隐私政策外，所有真实业务页面（含详情）都必须位于 Shell Stack"
  );
  await assert.rejects(
    () => access(resolve(mobileRoot, "app/(tabs)")),
    { code: "ENOENT" },
    "迁移完成后不得遗留旧 Tabs 目录"
  );

  for (const [routeName, expectedPath] of Object.entries(EXISTING_TAB_PATHS)) {
    assert.equal(
      TAB_PATHS[routeName],
      expectedPath,
      `既有深链 ${routeName} 不得因工作台改版改变`
    );
  }

  assert.equal(
    TAB_PATHS.workbench,
    "/(shell)/workbench",
    "工作台是唯一允许新增的 Tab 路由"
  );
  assert.equal(
    "task-center" in TAB_PATHS,
    false,
    "生产路由表不得包含已移除的任务中心"
  );

  const [shellLayout, rootLayout, appIndex, legacyAttendance, navigationSources, zhCommon, enCommon] =
    await Promise.all([
      readFile(resolve(mobileRoot, "app/(shell)/_layout.tsx"), "utf8"),
      readFile(resolve(mobileRoot, "app/_layout.tsx"), "utf8"),
      readFile(resolve(mobileRoot, "app/index.tsx"), "utf8"),
      readFile(resolve(mobileRoot, "app/(shell)/attendance.tsx"), "utf8"),
      readFile(resolve(mobileRoot, "src/modules/navigation/default-route.ts"), "utf8"),
      readFile(resolve(mobileRoot, "src/locales/zh/common.json"), "utf8"),
      readFile(resolve(mobileRoot, "src/locales/en/common.json"), "utf8"),
    ]);

  assert.match(shellLayout, /initialRouteName:\s*"workbench"/);
  assert.match(shellLayout, /<Stack\b/);
  assert.doesNotMatch(shellLayout, /\bTabs\b/);
  assert.match(rootLayout, /<Stack\.Screen\s+name="\(shell\)"/);
  assert.doesNotMatch(rootLayout, /<Stack\.Screen\s+name="\(tabs\)"/);
  assert.match(appIndex, /\/?\(shell\)\/workbench/);
  assert.match(legacyAttendance, /\/?\(shell\)\/attendance-personal/);
  assert.doesNotMatch(navigationSources, /task-center/);
  assert.doesNotMatch(shellLayout, /name="task-center"/);

  const zhTabs = JSON.parse(zhCommon).tabs;
  const enTabs = JSON.parse(enCommon).tabs;
  assert.deepEqual(
    {
      workbench: zhTabs.workbench,
      scan: zhTabs.scan,
      checkIn: zhTabs.checkIn,
      reports: zhTabs.reports,
      me: zhTabs.me,
    },
    {
      workbench: "工作台",
      scan: "扫码查询",
      checkIn: "打卡",
      reports: "报表",
      me: "我的",
    },
    "中文一级导航文案必须精确匹配产品定义"
  );
  assert.deepEqual(
    {
      workbench: enTabs.workbench,
      scan: enTabs.scan,
      checkIn: enTabs.checkIn,
      reports: enTabs.reports,
      me: enTabs.me,
    },
    {
      workbench: "Home",
      scan: "Scan",
      checkIn: "Check in",
      reports: "Reports",
      me: "Me",
    },
    "英文一级导航文案必须精确匹配产品定义"
  );
}

void run();
