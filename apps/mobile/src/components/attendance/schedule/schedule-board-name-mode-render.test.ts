import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 排班看板「昵称 / 全名」开关的渲染测试：真正渲染 ScheduleBoard，
// 验证默认显示昵称（没填用名 / 全名兜底）、开关文案与状态、点按回调、读屏标签用全名、横屏紧凑版触控区。
const attendanceCopy = JSON.parse(
  readFileSync(resolve(__dirname, "../../../locales/zh/screens/attendance.json"), "utf8"),
) as Record<string, unknown>;

/** 按中文语言包查词；第二个参数是字符串时当作默认值（与 i18next 一致）。 */
function lookup(key: string, options?: Record<string, unknown> | string): string {
  const value = key
    .split(".")
    .reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), attendanceCopy);
  if (typeof value !== "string") {
    if (typeof options === "string") return options;
    throw new Error(`中文语言包缺少键：${key}`);
  }
  const params = typeof options === "object" ? options : undefined;
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(params?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };
const textOf = (node: Node | string): string => (typeof node === "string" ? node : node.children.map(textOf).join(""));

async function run() {
  Object.assign(globalThis, { __DEV__: false, IS_REACT_ACT_ENVIRONMENT: true, React });
  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };
  const rendererReactFilename = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReactFilename !== require.resolve("react")) mockModule(rendererReactFilename, React);

  const host = (type: string) => {
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props, props.children as React.ReactNode);
    Stub.displayName = type;
    return Stub;
  };
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"), Pressable: host("Pressable"), ScrollView: host("ScrollView"),
  });
  mockModule("react-native-paper", { Text: host("Text") });
  // 午饭标识用 react-native-svg 画图标：测试里换成会留下节点的占位，便于断言
  mockModule("react-native-svg", {
    __esModule: true, default: host("Svg"), Path: host("Path"), G: host("G"), Rect: host("Rect"), Line: host("Line"), Text: host("SvgText"),
  });
  mockModule(require.resolve("../../../shared/i18n/use-app-translation"), { useAppTranslation: () => ({ t: lookup, language: "zh" }) });
  mockModule(require.resolve("../approvals/approval-ui"), { PatternFill: host("PatternFill") });

  const { createRoot } = await import("test-renderer");
  const { ScheduleBoard } = await import("./ScheduleBoard");
  const { assignStaffColors, buildStaffLegend, groupScheduleBoardDays } = await import("../../../modules/attendance/schedule-board");
  const { resolveStaffNames } = await import("../../../modules/attendance/staff-display-name");
  type Mode = "nickname" | "full";

  // 样例沿用设计图：Cody 有昵称；Taylor 没填昵称 → 名；Zhan Chen 只有全名 → 全名
  const people = [
    { userGuid: "cody", nickname: "Cody", givenName: "Cody", fullName: "Cody Johnson", username: "cody" },
    { userGuid: "taylor", givenName: "Taylor", fullName: "Taylor Keating", username: "taylor" },
    { userGuid: "zhan", fullName: "Zhan Chen", username: "zhan" },
  ];
  const fullNames = new Map(people.map((person) => [person.userGuid, person.fullName]));
  const week = ["2026-11-16", "2026-11-17", "2026-11-18", "2026-11-19", "2026-11-20", "2026-11-21", "2026-11-22"];
  const shift = (userGuid: string, workDate: string) => ({
    scheduleGuid: `${userGuid}-${workDate}`, storeCode: "1002", userGuid, workDate,
    startTime: "09:00:00", endTime: "17:30:00", status: "Active", isMine: false,
  }) as never;
  const schedules = [shift("cody", week[0]), shift("taylor", week[0]), shift("zhan", week[1])];

  const render = async (mode: Mode, extra: Record<string, unknown> = {}) => {
    const names = resolveStaffNames(people, mode);
    const nameOf = (guid: string) => names.get(guid) ?? guid;
    const props = {
      layout: "list", days: groupScheduleBoardDays(week, schedules, nameOf), today: "2026-11-16",
      colors: assignStaffColors(people.map((person) => ({ userGuid: person.userGuid, name: person.fullName }))),
      nameOf, fullNameOf: (guid: string) => fullNames.get(guid) ?? guid, nameMode: mode,
      ageOf: () => undefined, flagsOf: () => ({ warn: false, locked: false }),
      legend: buildStaffLegend(schedules, nameOf), onToggleHighlight: () => undefined,
      freeByDay: week.map(() => []), onPressCard: () => undefined,
      ...extra,
    };
    const root = createRoot();
    await act(async () => { root.render(React.createElement(ScheduleBoard as any, props)); });
    const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const all = (type: string) => container.queryAll((node) => node.type === type);
    const pressables = () => all("Pressable");
    return {
      switchNode: () => pressables().find((node) => node.props.accessibilityRole === "switch"),
      // 看板上出现的所有文字（图例名 + 卡片名 + 时间等）
      texts: () => all("Text").map((node) => textOf(node)),
      labels: () => pressables().map((node) => String(node.props.accessibilityLabel ?? "")),
    };
  };
  const press = (node: Node) => act(async () => { node.props.onPress(); });

  // ---------- 昵称模式（默认）：图例与卡片显示昵称，没填昵称的用名 / 全名兜底 ----------
  {
    const changes: string[] = [];
    const view = await render("nickname", { onChangeNameMode: (mode: string) => changes.push(mode) });
    const texts = view.texts();
    for (const shown of ["Cody", "Taylor", "Zhan Chen"]) assert.ok(texts.includes(shown), `看板应显示 ${shown}`);
    for (const hidden of ["Cody Johnson", "Taylor Keating"]) assert.ok(!texts.includes(hidden), `昵称模式不应显示 ${hidden}`);

    // 读屏标签一律用全名，避免只读昵称认不出人
    const labels = view.labels();
    assert.ok(labels.includes("高亮 Cody Johnson 的班次"), "图例的读屏文案用全名");
    // 卡片读屏标签以「全名 + 时间」开头；8.5 小时的班次末尾还会带「含午饭 1 次」（见 schedule-board-meal-mark-render.test.ts）
    assert.ok(labels.some((label) => label.startsWith("Taylor Keating 09:00–17:30")), "卡片的读屏文案用全名 + 时间");

    // 开关：switch 角色、当前状态为开（昵称）、文案「员工名 / 昵称」
    const toggle = view.switchNode()!;
    assert.ok(toggle, "有开关");
    assert.deepEqual(toggle.props.accessibilityState, { checked: true });
    assert.deepEqual(toggle.props.accessibilityValue, { text: "昵称" });
    assert.equal(toggle.props.accessibilityLabel, "员工名显示方式");
    assert.ok(textOf(toggle).includes("昵称") && !textOf(toggle).includes("全名"), "轨道里写当前状态「昵称」");
    assert.ok(view.texts().includes("员工名"), "开关左边有「员工名」标签");
    // 竖屏触控区 ≥ 44pt
    const style = ([] as object[]).concat(toggle.props.style).filter(Boolean).reduce((merged, item) => ({ ...merged, ...item }), {}) as { minHeight?: number };
    assert.equal(style.minHeight, 44);

    await press(toggle);
    assert.deepEqual(changes, ["full"], "昵称模式下点开关 → 切到全名");
  }

  // ---------- 全名模式：显示完整姓名，开关为关（全名），点一下回到昵称 ----------
  {
    const changes: string[] = [];
    const view = await render("full", { onChangeNameMode: (mode: string) => changes.push(mode) });
    const texts = view.texts();
    for (const shown of ["Cody Johnson", "Taylor Keating", "Zhan Chen"]) assert.ok(texts.includes(shown), `全名模式应显示 ${shown}`);
    assert.ok(!texts.includes("Cody"), "全名模式不显示昵称");
    const toggle = view.switchNode()!;
    assert.deepEqual(toggle.props.accessibilityState, { checked: false });
    assert.deepEqual(toggle.props.accessibilityValue, { text: "全名" });
    assert.ok(textOf(toggle).includes("全名") && !textOf(toggle).includes("昵称"), "轨道里写当前状态「全名」");
    await press(toggle);
    assert.deepEqual(changes, ["nickname"], "全名模式下点开关 → 切到昵称");
  }

  // ---------- 横屏 7 列看板：紧凑版，纵向靠 hitSlop 补足触控区 ----------
  {
    const view = await render("nickname", { layout: "columns", onChangeNameMode: () => undefined });
    const toggle = view.switchNode()!;
    assert.deepEqual(toggle.props.hitSlop, { top: 6, bottom: 6, left: 4, right: 4 });
    const style = ([] as object[]).concat(toggle.props.style).filter(Boolean).reduce((merged, item) => ({ ...merged, ...item }), {}) as { minHeight?: number };
    assert.equal(style.minHeight, 32, "横屏容器 32pt，加上下 hitSlop 共 44pt");
  }

  // ---------- 上层没接回调就不出现开关（看板仍正常显示）----------
  {
    const view = await render("nickname");
    assert.equal(view.switchNode(), undefined);
    assert.ok(view.texts().includes("Cody"));
  }

  console.log("schedule-board name-mode render tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
