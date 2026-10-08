import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(directory, "container-detail-screen.tsx"), "utf8");
const compact = (value: string) => value.replace(/\s+/g, " ").trim();
const zh = JSON.parse(readFileSync(join(directory, "../../locales/zh/screens/containerDetail.json"), "utf8")) as { messages: Record<string, string>; edit: Record<string, string>; actions: Record<string, string> };

function extract(startMarker: string, endMarker: string) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start);
  assert.ok(start >= 0 && end > start, `应能隔离源码：${startMarker}`);
  return source.slice(start, end);
}

const updateMutation = compact(extract(
  "const updateMutation = useMutation({",
  "const bulkMutation = useMutation({",
));
const editModal = compact(extract(
  // 编辑弹窗已由 Paper Modal 改为 BusinessSheet（键盘避让 + 可滚动），结构与保护逻辑不变
  "<BusinessSheet visible={Boolean(editingDetail && editForm)}",
  "<BusinessSheet visible={Boolean(bulkModalType)}",
));

assert.match(source, /const \[editEnglishNameError, setEditEnglishNameError\] = useState\(""\)/);
assert.match(source, /function closeEditModal\(\)/);
assert.match(source, /function openEditModal\(detail: ContainerDetail\)/);
assert.match(source, /function handleEditEnglishNameChange\(value: string\)/);

assert.ok(
  /onSuccess: async \(\{[\s\S]*result[\s\S]*\}\) =>/.test(updateMutation),
  "保存成功回调必须读取 batch-update-details 结构化结果",
);
assert.ok(
  updateMutation.includes("reconcileContainerDetailPartialSave"),
  "部分成功必须按服务器最新行回写成功字段基线",
);
assert.ok(
  updateMutation.includes("setEditValidationErrors(validationErrors);"),
  "任意字段校验错误必须保留给编辑弹窗展示",
);
assert.match(
  updateMutation,
  /if \(validationErrors\.length \|\| conflicts\.length\) \{[\s\S]*setSnackbar\([\s\S]*return;[\s\S]*\}/,
  "任何字段或整行校验失败都必须保留编辑弹窗并阻止成功提示",
);
assert.ok(
  updateMutation.includes("invalidateDetail();"),
  "同一请求中其它字段可能已保存，收到字段错误时也必须刷新详情",
);
assert.ok(
  updateMutation.includes("setEditEnglishNameError(englishNameError?.message ?? \"\");"),
  "后端英文名称错误必须写入字段错误状态",
);
assert.match(
  updateMutation,
  /if \(validationErrors\.length \|\| conflicts\.length\) \{[\s\S]*return;[\s\S]*\}[\s\S]*closeEditModal\(\);[\s\S]*setSnackbar\(t\("messages\.detailSaved"\)\);/,
  "任意字段错误必须保留弹窗和草稿；无错误时才关闭并显示全成功",
);

assert.ok(
  editModal.includes("onDismiss={updateMutation.isPending ? () => undefined : closeEditModal}"),
  "保存期间禁止关闭编辑弹窗，空闲时才统一清理旧字段错误",
);
assert.ok(
  editModal.includes('label={t("edit.englishName")}') && zh.edit.englishName === "英文名称",
  "编辑弹窗必须保留英文名称输入",
);
assert.equal(zh.messages.detailSaved, "明细已保存", "全部保存成功的提示文案不能变");
assert.ok(
  editModal.includes("error={Boolean(editEnglishNameError)}"),
  "英文名称输入必须进入 react-native-paper 错误态",
);
assert.ok(
  editModal.includes('<HelperText type="error" visible={Boolean(editEnglishNameError)}>'),
  "英文名称输入下方必须显示 react-native-paper HelperText",
);
assert.ok(
  editModal.includes("{editEnglishNameError}"),
  "HelperText 必须展示后端原始错误信息",
);
assert.ok(
  editModal.includes("editValidationErrors.map"),
  "套装、多码等其它字段校验错误必须在弹窗中逐项展示",
);
assert.ok(
  editModal.includes("onChangeText={handleEditEnglishNameChange}"),
  "修改英文名称时必须经过清错处理",
);
assert.match(
  editModal,
  /<Button [^>]*disabled=\{updateMutation\.isPending\} onPress=\{closeEditModal\}>\{t\("actions\.cancel"\)\}<\/Button>/,
  "取消编辑必须统一清理旧字段错误，保存期间必须禁用",
);
assert.equal(zh.actions.cancel, "取消");
assert.match(
  source,
  /function closeEditModal\(\) \{[\s\S]*setEditEnglishNameError\(""\);[\s\S]*setEditingDetail\(null\);[\s\S]*setEditForm\(null\);[\s\S]*\}/,
  "关闭弹窗必须清理错误、当前明细和表单",
);
assert.match(
  source,
  /function openEditModal\(detail: ContainerDetail\) \{[\s\S]*setEditEnglishNameError\(""\);[\s\S]*setEditingDetail\(detail\);[\s\S]*setEditForm\(buildContainerDetailEditForm\(detail\)\);[\s\S]*\}/,
  "重新打开编辑必须从干净错误状态开始",
);
assert.match(
  source,
  /function handleEditEnglishNameChange\(value: string\) \{[\s\S]*setEditEnglishNameError\(""\);[\s\S]*englishName: value[\s\S]*\}/,
  "用户修改英文名称时必须立即清理旧服务端错误并保留新输入",
);
// 表格行点击：只有具备编辑权限（resolveRowPressAction）才进入编辑，且必须经统一的清错入口 openEditModal
assert.match(
  source,
  /function handleRowPress\(detail: ContainerDetail\) \{[\s\S]*resolveRowPressAction\(canEditContainer\) === "edit"[\s\S]*openEditModal\(detail\)/,
  "每次打开编辑必须使用统一的清错入口，并受编辑权限约束",
);
assert.match(source, /onRowPress=\{handleRowPress\}/, "表格行点击必须接到 handleRowPress");

console.log("container-detail-edit-contract.test.ts: ok");
