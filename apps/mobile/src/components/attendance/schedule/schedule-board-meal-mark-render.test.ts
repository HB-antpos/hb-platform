import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 排班看板「有无午饭」标识的渲染测试：真正渲染 ScheduleBoard，
// 验证三种状态（自动含午饭 / 手动指定 / 长班次无午饭）、×2、短班次与请假不标识、读屏标签、图例小字，竖屏列表与横屏 7 列都要有。
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
/** 把 style 数组摊平成一个对象，方便断言边框 / 底色。 */
const flatten = (style: unknown) => ([] as object[]).concat(style as object).filter(Boolean).reduce((merged, item) => ({ ...merged, ...item }), {}) as Record<string, unknown>;

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
  mockModule("react-native-svg", {
    __esModule: true, default: host("Svg"), Path: host("Path"), G: host("G"), Rect: host("Rect"), Line: host("Line"), Text: host("SvgText"),
  });
  mockModule(require.resolve("../../../shared/i18n/use-app-translation"), { useAppTranslation: () => ({ t: lookup, language: "zh" }) });
  mockModule(require.resolve("../approvals/approval-ui"), { PatternFill: host("PatternFill") });

  const { createRoot } = await import("test-renderer");
  const { ScheduleBoard } = await import("./ScheduleBoard");
  const { assignStaffColors, buildStaffLegend, groupScheduleBoardDays } = await import("../../../modules/attendance/schedule-board");
  const { MEAL_NONE_TONE, MEAL_SLASH_PATH } = await import("../../../modules/attendance/meal-mark");

  const people = ["cody", "dion", "rylie", "terry", "charlie"];
  const week = ["2026-11-16", "2026-11-17", "2026-11-18", "2026-11-19", "2026-11-20", "2026-11-21", "2026-11-22"];
  const shift = (userGuid: string, start: string, end: string, extra: Record<string, unknown> = {}) => ({
    scheduleGuid: `${userGuid}-${start}`, storeCode: "1002", userGuid, workDate: week[0],
    startTime: `${start}:00`, endTime: `${end}:00`, status: "Active", isMine: false, ...extra,
  }) as never;
  const marked = [
    shift("cody", "09:00", "17:30"),                       // 自动含午饭
    shift("dion", "09:00", "19:30"),                       // 10.5 小时自动 2 次
    shift("rylie", "12:00", "17:30", { mealBreakCount: 1 }), // 手动指定含午饭
    shift("terry", "09:00", "17:30", { mealBreakCount: 0 }), // 长班次手动不扣 → 无午饭
    shift("charlie", "15:00", "17:30"),                    // 短班次：不标识
    shift("charlie", "00:00", "00:00", { leaveType: "AnnualLeave", scheduleGuid: "leave-1" }), // 请假：不标识
  ];
  const nameOf = (guid: string) => guid.charAt(0).toUpperCase() + guid.slice(1);

  const render = async (schedules: never[], layout: "list" | "columns") => {
    const props = {
      layout, days: groupScheduleBoardDays(week, schedules, nameOf), today: "2026-11-16",
      colors: assignStaffColors(people.map((guid) => ({ userGuid: guid, name: nameOf(guid) }))),
      nameOf, ageOf: () => undefined, flagsOf: () => ({ warn: false, locked: false }),
      legend: buildStaffLegend(schedules, nameOf), onToggleHighlight: () => undefined,
      freeByDay: week.map(() => []), onPressCard: () => undefined,
    };
    const root = createRoot();
    await act(async () => { root.render(React.createElement(ScheduleBoard as any, props)); });
    const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const all = (type: string) => container.queryAll((node) => node.type === type);
    return {
      // 标识本体：隐藏给读屏的装饰性 View（里面有叉勺 Svg）
      badges: () => all("View").filter((node) => node.props.accessibilityElementsHidden === true),
      labels: () => all("Pressable").map((node) => String(node.props.accessibilityLabel ?? "")),
      texts: () => all("Text").map((node) => textOf(node)),
      slashPaths: () => all("Path").filter((node) => node.props.d === MEAL_SLASH_PATH),
    };
  };

  for (const layout of ["list", "columns"] as const) {
    const view = await render(marked, layout);
    const badges = view.badges();
    // 4 个标识：cody / dion / rylie / terry；charlie 的短班次与请假卡片都没有
    assert.equal(badges.length, 4, `${layout}：只有 4 张卡片有标识（短班次、请假不标识）`);
    assert.equal(badges.filter((badge) => badge.children.length).length, 4);

    const byText = (needle: string) => badges.find((badge) => textOf(badge).includes(needle));
    // 自动 2 次：旁边写 ×2
    assert.ok(byText("×2"), `${layout}：10.5 小时自动 2 次，标识旁写 ×2`);
    assert.equal(badges.filter((badge) => textOf(badge).includes("×")).length, 1, "只有 2 次的才写 ×N");

    // 无午饭：恰好一个斜杠图标，橙色底 + 橙色虚线框
    assert.equal(view.slashPaths().length, 1, `${layout}：只有长班次手动不扣的那一张有斜杠`);
    const none = badges.find((badge) => flatten(badge.props.style).backgroundColor === MEAL_NONE_TONE.background);
    assert.ok(none, "无午饭用橙色底");
    assert.equal(flatten(none.props.style).borderColor, MEAL_NONE_TONE.border);
    assert.equal(flatten(none.props.style).borderStyle, "dashed");

    // 手动指定含午饭：虚线框（跟随卡片文字色）；自动含午饭：没有边框
    const dashed = badges.filter((badge) => flatten(badge.props.style).borderStyle === "dashed");
    assert.equal(dashed.length, 2, "虚线框 = 手动指定 1 个 + 无午饭 1 个");
    const autoBadges = badges.filter((badge) => flatten(badge.props.style).borderStyle !== "dashed");
    assert.equal(autoBadges.length, 2, "自动含午饭的两张（cody、dion）没有虚线框");

    // 读屏：卡片标签末尾带「含午饭 N 次」/「无午饭（手动不扣）」；短班次没有
    const labels = view.labels();
    assert.ok(labels.includes("Cody 09:00–17:30，含午饭 1 次"), `${layout}：自动含午饭的读屏文案`);
    assert.ok(labels.includes("Dion 09:00–19:30，含午饭 2 次"), "2 次");
    assert.ok(labels.includes("Rylie 12:00–17:30，含午饭 1 次"), "手动指定 1 次");
    assert.ok(labels.includes("Terry 09:00–17:30，无午饭（手动不扣）"), "无午饭的读屏文案");
    assert.ok(labels.includes("Charlie 15:00–17:30"), "短班次的读屏文案不带午饭信息");

    // 图例下方一行小字说明
    assert.ok(view.texts().includes("叉勺＝含午饭　橙色斜杠＝长班次无午饭"), `${layout}：有标识时显示图例小字`);
  }

  // 本周一个标识都没有（只有短班次）：不画标识，也不显示图例小字
  {
    const view = await render([shift("charlie", "15:00", "17:30")] as never[], "list");
    assert.equal(view.badges().length, 0);
    assert.ok(!view.texts().some((text) => text.includes("叉勺")), "没有标识就不显示图例小字");
  }

  // 已取消的班次不标识
  {
    const view = await render([shift("cody", "09:00", "17:30", { status: "Cancelled" })] as never[], "list");
    assert.equal(view.badges().length, 0, "已取消的 8.5 小时班次不标识");
  }

  console.log("schedule-board meal-mark render tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
