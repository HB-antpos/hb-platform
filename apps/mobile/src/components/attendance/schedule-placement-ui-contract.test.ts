import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// 手机端排班「对齐 Web」的源码接线契约：固定常用班次、冲突 / 用餐标记、班次块「×」、点选目标格（移动 / 复制 / 托盘添加）。
// 交互本身由 schedule-placement(.actions) 的单测覆盖，这里只防止接线被改丢。
const directory = dirname(fileURLToPath(import.meta.url));
const card = readFileSync(join(directory, "ScheduleManagementCard.tsx"), "utf8");
const screen = readFileSync(join(directory, "AttendanceScreen.tsx"), "utf8");
const readLocale = (language: string) =>
  JSON.parse(readFileSync(join(directory, `../../locales/${language}/screens/attendance.json`), "utf8")) as {
    scheduleManagement: Record<string, any>;
  };

// 常用班次固定 5 个，默认 09:00–17:30，不再有旧的 QUICK_SHIFTS 与对应文案
assert.ok(card.includes("SHIFT_PRESETS.map((shift) => ("), "编辑器里的常用班次来自固定常量");
assert.ok(!card.includes("QUICK_SHIFTS"), "旧的常用班次常量应已移除");
assert.ok(card.includes("startTime: DEFAULT_SHIFT.startTime") && card.includes("endTime: DEFAULT_SHIFT.endTime"), "新建班次默认时段取 DEFAULT_SHIFT");

// 格内标记：与员工上报冲突、手动指定的用餐次数
assert.ok(card.includes("getAvailabilityConflict(item, cell.availability)"), "格内要判断与员工上报的冲突");
assert.ok(card.includes("styles.conflictMark") && card.includes("styles.mealBadge"), "格内显示冲突与用餐小标记");
assert.ok(card.includes("getMealBadge(item.mealBreakCount)"), "用餐标记只在手动指定时出现");

// 班次块「×」：每个班次块可删除（先确认），确认文案告知会取消待审批考勤并可找回
assert.ok(card.includes("onClose={() => confirmDelete(item)}"), "编辑器里每个班次块要有「×」");
assert.ok(card.includes("onPress={() => confirmDelete()}"), "底部删除按钮不能把点击事件当成班次传入");

// 点选目标格：移到 / 复制到、托盘添加；放置模式下点格子放下，提醒要二次确认，重叠直接拒绝
assert.ok(card.includes('startPlacement("move", editingTarget.schedule!)') && card.includes('startPlacement("copy", editingTarget.schedule!)'), "编辑器要有「移到… / 复制到…」");
assert.ok(card.includes("startPresetPlacement(preset)"), "常用班次托盘点一下进入添加模式");
assert.ok(card.includes("placement ? handlePlacementPress(row, day) : openCell(row, day, index, cell)"), "放置模式下点格子是放下，不是打开编辑器");
assert.ok(/verdict\.state === "bad"[\s\S]{0,200}badOverlap/.test(card), "时间重叠不能放");
assert.ok(/verdict\.state === "warn"[\s\S]{0,600}warnConfirm/.test(card), "员工上报冲突要二次确认");
assert.ok(card.includes("disabled={editingLocked || isBusy}"), "已有打卡的班次不能移动 / 复制");

// 长按拖动：长按班次 / 常用班次拿起，拖到目标格松手；落点处理与点选共用 commitPlacement，不另起一套规则
assert.ok(card.includes("onLongPress={() => handleCellLongPress(variant, cell)}"), "有班次的格子长按拿起班次");
assert.ok(card.includes("onLongPress={() => handlePresetLongPress(variant, preset)}"), "常用班次托盘长按拿起");
assert.ok(card.includes("getDragStartVerdict(cell, isLocked)"), "拿起前要走锁定判断（已有打卡不能拖）");
assert.ok(/handlePlacementPress[\s\S]{0,200}commitPlacement\(placement, row, day\)/.test(card), "点选放下走 commitPlacement");
assert.ok(/finishDrag[\s\S]{0,900}commitPlacement\(runtime\.source, row, day\)/.test(card), "拖动松手也走 commitPlacement（重叠拒绝、冲突二次确认）");
assert.ok(card.includes("PanResponder.create") && card.includes("{...dragPanResponder.panHandlers}"), "拖动用 RN 自带 PanResponder（不引入原生依赖，可 OTA）");
assert.ok(card.includes("scrollEnabled={!drag}"), "拖动中锁住日期区横向滚动");
assert.ok(/onPanResponderRelease[\s\S]{0,300}dragApiRef\.current\.release\(/.test(card), "松手用抬起点重算落点（兜住被合并 / 丢掉的 move）");
assert.ok(card.includes("DRAG_AUTO_SCROLL_DWELL_MS") && card.includes("runtime.edgeSince"), "靠边自动滚动要先停留一会儿，避免列在手底下漂移");
assert.ok(card.includes("onDragActiveChange?.(true)") && card.includes("onDragActiveChange?.(false)"), "拖动开始 / 结束通知屏幕层");
assert.ok(screen.includes("scrollEnabled={!isScheduleDragging}") && screen.includes("onDragActiveChange={setIsScheduleDragging}"), "拖动期间屏幕层锁住整页纵向滚动");

// 屏幕层：本周打卡只在有打卡查看权限时拉取；放下走 executePlacement，成功后给撤销
assert.ok(/managedPunchScheduleGuidsQuery[\s\S]{0,700}canViewManagedPunches/.test(screen), "本周打卡只在有打卡查看权限时拉取");
assert.ok(screen.includes("punchesKnown={managedPunchScheduleGuidsQuery.isSuccess}"), "拿不到打卡时要告诉网格走保守锁定");
assert.ok(screen.includes("executePlacement("), "放下的落地走 executePlacement");
assert.ok(screen.includes("placeShiftMutation.mutate({ source, target })"), "卡片的放下回调接到 mutation");
assert.ok(/setSnackbarUndo\(\(\) => async \(\) => \{[\s\S]{0,300}await undo\(\);/.test(screen), "放下成功后提供撤销");
assert.ok(screen.includes("placeShiftMutation.isPending ||"), "放下期间排班整体置忙");

// 文案：中英文键齐全，旧的 quickShifts 已删除
for (const language of ["zh", "en"]) {
  const { scheduleManagement } = readLocale(language);
  assert.equal(scheduleManagement.quickShifts, undefined, `${language} 不应再有 quickShifts`);
  for (const key of [
    "trayTitle", "trayHint", "bannerMove", "bannerCopy", "bannerAdd", "cancel", "moveAction", "copyAction",
    "lockedNotice", "lockedUnknownNotice", "badOverlap", "warnTitle", "warnUnavailable", "warnOutside", "warnConfirm",
    "added", "copied", "moved", "movedPublished", "downgradedSuffix", "transferDeleteFailed", "undo", "undone", "undoFailed", "failed", "closeA11y",
  ]) {
    assert.equal(typeof scheduleManagement.placement?.[key], "string", `${language} 缺 scheduleManagement.placement.${key}`);
  }
  assert.equal(typeof scheduleManagement.cell?.mealBadge, "string", `${language} 缺 cell.mealBadge`);
  assert.equal(typeof scheduleManagement.editor?.conflictUnavailable, "string", `${language} 缺 editor.conflictUnavailable`);
  assert.equal(typeof scheduleManagement.editor?.conflictOutside, "string", `${language} 缺 editor.conflictOutside`);
}

console.log("schedule-placement-ui-contract.test.ts: ok");
