import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const attendanceSource = readFileSync(
  resolve(currentDirectory, "../../components/attendance/AttendanceScreen.tsx"),
  "utf8",
);
// 「人员管理」合并后，店员列表逻辑迁到 store-staff-list.tsx（原 users/index.tsx 只剩渲染 Hub 的薄路由）。
const usersSource = readFileSync(
  resolve(currentDirectory, "../users/store-staff-list.tsx"),
  "utf8",
);

assert.match(
  attendanceSource,
  /const posEnabledStores = useMemo\(\(\) => getPosEnabledStores\(stores\), \[stores\]\);[\s\S]*?const managerStores = useMemo\([\s\S]*?posEnabledStores\.filter\(isPrimaryStore\)[\s\S]*?const sectionStores = isManagementMode \? managerStores : posEnabledStores;/,
  "考勤选择候选必须先限制为 POS 已启用门店，再保留管理页 isPrimary 范围",
);
assert.match(
  attendanceSource,
  /qrStore = resolveAttendanceQrStore\(resolvedQr, stores\)/,
  "考勤二维码授权必须继续使用原始已分配门店集合",
);
assert.doesNotMatch(
  attendanceSource,
  /resolveAttendanceQrStore\(resolvedQr, posEnabledStores\)/,
  "Picker 展示过滤不得收窄二维码的既有授权集合",
);
assert.match(
  attendanceSource,
  /<StorePickerModal[\s\S]{0,240}stores=\{sectionStores\}/,
  "考勤 Picker 必须只展示当前页面的 POS 启用候选",
);
assert.match(
  attendanceSource,
  /resolveScopedStoreCode\(\{[\s\S]{0,360}deviceBoundStoreCode:[\s\S]{0,180}isDeviceMode,[\s\S]{0,180}stores: sectionStores/,
  "考勤设备模式必须独立保留绑定门店 code，不得被 Picker 候选清空",
);
assert.match(
  attendanceSource,
  /findStoreByCode\(stores, selectedStoreCode\)/,
  "考勤当前门店名称必须从原始作用域门店读取",
);

const manageableStoresStart = usersSource.indexOf("  const manageableStores = useMemo(");
const selectionEffectStart = usersSource.indexOf("  useEffect(() => {", manageableStoresStart);
assert.ok(manageableStoresStart >= 0 && selectionEffectStart > manageableStoresStart,
  "必须能够隔离用户页操作授权范围");
const manageableStoresSource = usersSource.slice(manageableStoresStart, selectionEffectStart);

assert.match(
  manageableStoresSource,
  /getManageableStoresForSession\(\{[\s\S]*?stores,[\s\S]*?deviceBoundStore: deviceBoundStoreCode \? stores\.find/,
  "用户操作权限和设备绑定必须继续从原始门店集合计算",
);
assert.doesNotMatch(
  manageableStoresSource,
  /stores:\s*posEnabledStores|posEnabledStores\.find/,
  "用户 Picker 过滤不得进入操作授权计算",
);

const selectionEffectEnd = usersSource.indexOf("  const managedStore = useMemo(", selectionEffectStart);
assert.ok(selectionEffectEnd > selectionEffectStart, "必须能够隔离用户页门店选择恢复逻辑");
const selectionEffectSource = usersSource.slice(selectionEffectStart, selectionEffectEnd);

assert.match(
  selectionEffectSource,
  /posEnabledStores\.some\([\s\S]*?posEnabledStores\.find/,
  "账号模式的当前选择和记忆选择必须由 POS 启用候选重新校验",
);
assert.match(
  usersSource,
  /<StoreSwitchSheet[\s\S]{0,260}stores=\{posEnabledStores\}[\s\S]{0,180}includeAllOption=\{!deviceBoundStoreCode\}/,
  "用户 Picker 必须使用 POS 启用候选，并在设备绑定模式禁用全部门店选项",
);

console.log("pos-store-picker-contract.test.ts: ok");
