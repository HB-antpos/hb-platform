import assert from "node:assert/strict";
import type { Store } from "@/modules/shop/types";
import { canViewPayslips, resolvePayslipStoreOptions } from "./access";

const has = (...codes: string[]) => (code: string) => codes.includes(code);

// 入口：登录 + 非审核壳 + 两个查看权限任一
assert.equal(canViewPayslips(true, has("Myob.Payslip.ViewManagedStore"), false), true);
assert.equal(canViewPayslips(true, has("Myob.Payslip.ViewAllStores"), false), true);
assert.equal(canViewPayslips(true, has("Myob.Payslip.Download"), false), false, "只有下载权限不能进入");
assert.equal(canViewPayslips(true, has("Myob.Payslip.Email"), false), false, "只有邮件权限不能进入");
assert.equal(canViewPayslips(false, has("Myob.Payslip.ViewAllStores"), false), false);
assert.equal(canViewPayslips(true, has("Myob.Payslip.ViewAllStores"), true), false, "iOS 审核壳不提供");

// 分店筛选范围：全部分店权限 / 管理员看全部启用 POS 的分店，其余只看管辖（isPrimary）分店
const stores = [
  { storeCode: "1014", storeName: "Kawana", isActive: true, isPrimary: true },
  { storeCode: "1008", storeName: "Bankstown", isActive: true, isPrimary: false },
  { storeCode: "9999", storeName: "Closed", isActive: false, isPrimary: true },
] as unknown as Store[];
assert.deepEqual(
  resolvePayslipStoreOptions(stores, { isAdmin: false, hasPermission: has("Myob.Payslip.ViewManagedStore") }).map((store) => store.storeCode),
  ["1014"],
);
assert.deepEqual(
  resolvePayslipStoreOptions(stores, { isAdmin: false, hasPermission: has("Myob.Payslip.ViewAllStores") }).map((store) => store.storeCode),
  ["1014", "1008"],
);
assert.deepEqual(
  resolvePayslipStoreOptions(stores, { isAdmin: true, hasPermission: () => false }).map((store) => store.storeCode),
  ["1014", "1008"],
);
