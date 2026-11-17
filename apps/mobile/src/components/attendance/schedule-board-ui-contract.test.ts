import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// 手机端排班「按天分组看板」对齐 Web 的源码接线契约：看板 / 横屏 7 列、图例高亮、员工 × 日期批量排班、改派、本周。
// 规则本身由 modules/attendance/schedule-board 的单测覆盖，这里只防止接线被改丢。
const directory = dirname(fileURLToPath(import.meta.url));
const card = readFileSync(join(directory, "ScheduleManagementCard.tsx"), "utf8");
const board = readFileSync(join(directory, "schedule/ScheduleBoard.tsx"), "utf8");
const screen = readFileSync(join(directory, "AttendanceScreen.tsx"), "utf8");
const readLocale = (language: string) =>
  JSON.parse(readFileSync(join(directory, `../../locales/${language}/screens/attendance.json`), "utf8")) as {
    scheduleManagement: Record<string, any>;
  };

// 视图：周＝按天看板（默认）、按人＝原员工行表格（保留长按拖动）、日＝按小时
assert.ok(card.includes('useState<"week" | "staff" | "day">("week")'), "默认进入按天看板");
assert.ok(card.includes('(["week", "staff", "day"] as const).map((mode) => ('), "视图切换有 周 / 按人 / 日");
assert.ok(card.includes('viewMode === "staff" ? (\n            renderScheduleArea("card")'), "按人视图仍是原表格");
assert.ok(card.includes('renderBoardArea("list")'), "竖屏周视图用按天看板");
assert.ok(card.includes('viewMode === "staff" ? renderScheduleArea("fullscreen") : renderBoardArea("columns")'), "横屏全屏是 7 列看板");

// 看板数据：与 Web 同一套配色 / 排序 / 图例 / 可上班未排
assert.ok(card.includes("assignStaffColors(rows.map((row) => ({ userGuid: row.userGuid, name: row.employeeName })))"), "员工颜色用共享算法");
assert.ok(card.includes("groupScheduleBoardDays(days, schedules, boardNameOf)"), "每天的班次按开始时间 → 看板显示名排");
assert.ok(card.includes("buildStaffLegend(schedules, boardNameOf)"), "顶部员工图例用看板显示名");
assert.ok(card.includes("findAvailableUnscheduled(day, rows.map((row) => row.userGuid), schedules, availabilityMap)"), "每天可上班未排名单");
assert.ok(board.includes("onToggleHighlight(item.userGuid)") && board.includes("styles.dimmed"), "图例点选高亮，其他人变淡");
assert.ok(board.includes("schedulePaidMinutes(schedule)"), "卡片工时按排班计算，不能用打卡算出的 paidMinutes");

// 员工名「昵称 / 全名」（与 Web 同一套）：默认昵称、名字兜底；只影响看板，颜色 / 弹窗 / 转移仍用全名
const nameSwitch = readFileSync(join(directory, "schedule/StaffNameModeSwitch.tsx"), "utf8");
assert.ok(card.includes("useState<StaffNameMode>(DEFAULT_STAFF_NAME_MODE)"), "默认显示昵称");
assert.ok(card.includes("readStaffNameMode(AppAsyncStorage)") && card.includes("writeStaffNameMode(AppAsyncStorage, mode)"), "偏好记在本机 AsyncStorage");
assert.ok(card.includes("!staffNameChosenRef.current"), "用户已经点过开关时，迟到的读取结果不能覆盖");
assert.ok(card.includes("resolveStaffNames(") && card.includes("const boardNameOf = useCallback("), "看板显示名按模式 + 重名区分算出");
assert.ok(card.includes("nameOf={boardNameOf}") && card.includes("fullNameOf={nameOfUser}"), "看板用看板显示名，读屏用全名");
assert.ok(card.includes("nameMode={staffNameMode}") && card.includes("onChangeNameMode={changeStaffNameMode}"), "开关接到看板");
assert.ok(card.includes("nameParts: {\n          username: user.username,"), "员工行带着昵称 / 名 / 姓 / 全名 / 登录名");
assert.ok(card.includes("nameParts: { fullName: schedule.employeeName }"), "只有排班没有员工资料的行退回排班里的名字");
// 颜色仍按全名分配（上面已断言 assignStaffColors 吃 row.employeeName）；弹窗里的名字一律不跟开关走
assert.ok(card.includes("<Text style={styles.matrixName} numberOfLines={1}>{nameOfUser(userGuid)}</Text>"), "员工 × 日期批量排班里的员工名仍是全名");
assert.ok(card.includes('t("scheduleManagement.transfer.action", { name: nameOfUser(transferTo) })'), "转移班次里的员工名仍是全名");
assert.ok(card.includes("employeeName: nameOfUser(userGuid)"), "按人找行的兜底仍用全名");
assert.ok(board.includes("fullNameOf?.(schedule.userGuid) ?? name") && board.includes("fullNameOf?.(item.userGuid) ?? item.name"), "卡片 / 图例的无障碍文案用全名");
assert.ok(board.includes("<StaffNameModeSwitch mode={nameMode} onChange={onChangeNameMode} compact={columns} />"), "开关放在图例行最左侧");
assert.ok(nameSwitch.includes('accessibilityRole="switch"') && nameSwitch.includes("accessibilityState={{ checked: nickname }}"), "开关对读屏是 switch，并读出当前状态");
assert.ok(nameSwitch.includes("minHeight: 44"), "竖屏触控区 ≥ 44pt");

// 点卡片沿用原编辑面板；放置模式下每天头部是目标按钮，落点沿用 commitPlacement
assert.ok(card.includes("onPressCard={openBoardCard}"), "看板点卡片打开编辑面板");
assert.ok(card.includes("onDrop: (dayIndex) => commitPlacement(placement, rowOfUser(placingSchedule.userGuid), days[dayIndex])"), "放置模式点某天放下");

// 员工 × 日期批量排班：入口（＋ 排班 / 空白日 / 可上班未排），计划与提交
assert.ok(card.includes('setBatchTarget({ mode: "matrix" })'), "矩阵批量排班入口");
assert.ok(card.includes("onPressFree={(dayIndex) => openMatrixBatch({ dates: [days[dayIndex]], userGuids: freeByDay[dayIndex] })}"), "可上班未排预选这天和这些人");
assert.ok(card.includes("buildBatchMatrix({"), "批量计划由 buildBatchMatrix 算出");
assert.ok(card.includes("onCreateBatch(batchMatrixTargets(matrixCells).map((cell) => ({ ...shift, userGuid: cell.userGuid, workDate: cell.date })))"), "只提交新建 / 提醒且未排除的格子，一律草稿");
assert.ok(card.includes("onPress={() => openMatrixBatch()}"), "底部操作有「＋ 排班」");

// 改派：走「移动到另一员工」，重叠直接拦下
assert.ok(card.includes("const transferEditing = () => {"), "编辑面板可改派");
assert.ok(card.includes('if (verdictFor(source, target, day).state === "bad") {'), "改派前检查时间重叠");
assert.ok(card.includes("commitPlacement(source, target, day);"), "改派落地沿用放置逻辑（含撤销）");

// 本周 + 相对周
assert.ok(screen.includes("onThisWeek={() => setManagerWeekStartDate(getWeekStartDate())}"), "考勤页接上「本周」");
assert.ok(screen.includes("currentWeekStartDate={getWeekStartDate()}"), "考勤页传入本周周一");
assert.ok(card.includes("getRelativeWeekOffset(weekStartDate, currentWeekStartDate)"), "标出下 N 周 / 上 N 周");

// 双语文案齐全
for (const language of ["zh", "en"]) {
  const sm = readLocale(language).scheduleManagement;
  for (const key of ["thisWeek", "weekLater", "weekEarlier"]) assert.ok(sm[key], `${language} 缺 ${key}`);
  assert.ok(sm.view.staff, `${language} 缺 view.staff`);
  for (const key of ["legendMeta", "dayStat", "dayEmpty", "today", "add", "availableUnscheduled", "schedule"]) assert.ok(sm.board[key], `${language} 缺 board.${key}`);
  for (const key of ["ok", "warn", "bad", "noop"]) assert.ok(sm.board.drop[key], `${language} 缺 board.drop.${key}`);
  for (const key of ["label", "nickname", "full", "a11y", "hint"]) assert.ok(sm.board.nameMode[key], `${language} 缺 board.nameMode.${key}`);
  for (const key of ["open", "title", "employees", "dates", "total", "create", "warn", "skip", "previewHint", "pickHint"]) assert.ok(sm.matrix[key], `${language} 缺 matrix.${key}`);
  for (const key of ["existing", "leave", "unavailable"]) assert.ok(sm.matrix.reason[key], `${language} 缺 matrix.reason.${key}`);
  for (const key of ["title", "pick", "action", "hint"]) assert.ok(sm.transfer[key], `${language} 缺 transfer.${key}`);
}

console.log("schedule-board-ui-contract tests passed");
