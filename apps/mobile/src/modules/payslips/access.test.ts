import assert from "node:assert/strict";
import type { Store } from "@/modules/shop/types";
import { canViewOthersPayslips, canViewPayslips, resolvePayslipStoreOptions } from "./access";

const has = (...codes: string[]) => (code: string) => codes.includes(code);

// 入口：登录 + 非审核壳即可，不要求权限（没有权限的员工只看本人）
assert.equal(canViewPayslips(true, false), true);
assert.equal(canViewPayslips(false, false), false, "未登录不能进入");
assert.equal(canViewPayslips(true, true), false, "iOS 审核壳不提供");

// 能否看别人：管理员或两个查看权限任一；下载 / 邮件权限不算
assert.equal(canViewOthersPayslips(false, has("Myob.Payslip.ViewManagedStore")), true);
assert.equal(canViewOthersPayslips(false, has("Myob.Payslip.ViewAllStores")), true);
assert.equal(canViewOthersPayslips(true, () => false), true, "管理员不需要权限码");
assert.equal(canViewOthersPayslips(false, has("Myob.Payslip.Download")), false, "只有下载权限只能看本人");
assert.equal(canViewOthersPayslips(false, has("Myob.Payslip.Email")), false, "只有邮件权限只能看本人");
assert.equal(canViewOthersPayslips(false, () => false), false);

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
