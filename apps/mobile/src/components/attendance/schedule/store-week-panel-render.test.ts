import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 员工「整店排班」面板的渲染测试：真正渲染 StoreWeekPanel，覆盖
// 按天 / 表格两种视图、点日期弹出「当天在岗」（含与我同班的时长）、点名字看他的一周、
// 店长未发布（本周无引导、未来周引导去填可上班时间）、加载失败（无数据 / 有旧数据）、加载中，
// 以及隐私底线：界面上永远不出现同事的请假 / 备注 / 工时字样。
const attendanceCopy = JSON.parse(
  readFileSync(resolve(__dirname, "../../../locales/zh/screens/attendance.json"), "utf8"),
) as Record<string, unknown>;

function find(key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), attendanceCopy);
}

/** 按中文语言包查词；带 count 的键取 `_other` 复数形式（与 i18next 一致）。 */
function lookup(key: string, options?: Record<string, unknown> | string): string {
  let value = find(key);
  if (typeof value !== "string" && typeof options === "object" && options.count !== undefined) value = find(`${key}_other`);
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
  mockModule("react-native-paper", {
    Text: host("Text"), Button: host("Button"), ActivityIndicator: host("ActivityIndicator"), Icon: host("Icon"),
  });
  mockModule(require.resolve("../../../shared/i18n/use-app-translation"), { useAppTranslation: () => ({ t: lookup, language: "zh" }) });
  // 弹层：可见时把标题、副标题、内容和底部按钮都渲染出来，便于断言
  mockModule(require.resolve("../../ui/BusinessSheet"), {
    BusinessSheet: (props: { visible: boolean; title: string; subtitle?: string; children: React.ReactNode; footer?: React.ReactNode }) =>
      props.visible
        ? React.createElement("Sheet", { title: props.title },
          React.createElement("Text", { sheet: "title" }, props.title),
          props.subtitle ? React.createElement("Text", { sheet: "subtitle" }, props.subtitle) : null,
          props.children,
          props.footer)
        : null,
  });

  const { createRoot } = await import("test-renderer");
  const { StoreWeekPanel } = await import("./StoreWeekPanel");
  type Week = import("../../../modules/attendance/store-week").StoreWeek;

  const shift = (name: string, workDate: string, startTime: string, endTime: string, isMine = false) => ({
    userGuid: name.toLowerCase(), name, isMine, workDate, startTime, endTime,
  });
  const published: Week = {
    weekStartDate: "2026-11-16",
    stores: [{
      storeCode: "BRI", storeName: "Brisbane", published: true,
      shifts: [
        shift("Ken", "2026-11-16", "09:00", "17:00"),
        shift("Lena", "2026-11-20", "09:00", "17:00"),
        shift("Mia", "2026-11-20", "10:00", "18:00", true),
        shift("Tom", "2026-11-20", "12:00", "20:00"),
      ],
    }],
  };
  const unpublished: Week = { weekStartDate: "2026-11-16", stores: [{ storeCode: "BRI", storeName: "Brisbane", published: false, shifts: [] }] };

  const render = async (overrides: Record<string, unknown> = {}) => {
    const calls = { retry: 0, mine: 0 };
    const props = {
      weekStartDate: "2026-11-16", today: "2026-11-20", storeCode: "BRI", week: published,
      isLoading: false, isError: false,
      onRetry: () => { calls.retry += 1; }, onPreviousWeek: () => undefined, onNextWeek: () => undefined,
      onGoToMine: () => { calls.mine += 1; }, scopeSwitch: React.createElement("Switch", {}),
      ...overrides,
    };
    const root = createRoot();
    await act(async () => { root.render(React.createElement(StoreWeekPanel as any, props)); });
    const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const all = (type: string) => container.queryAll((node) => node.type === type);
    return {
      calls,
      texts: () => all("Text").map((node) => textOf(node)),
      pressables: () => all("Pressable"),
      buttons: () => all("Button"),
      has: (type: string) => all(type).length > 0,
      sheetTexts: (kind: string) => all("Text").filter((node) => node.props.sheet === kind).map((node) => textOf(node)),
      pressLabel: (predicate: (label: string) => boolean) => {
        const node = all("Pressable").find((item) => predicate(String(item.props.accessibilityLabel ?? "")));
        assert.ok(node, "找到对应的可点元素");
        return act(async () => { node!.props.onPress(); });
      },
      pressTab: (label: string) => {
        const node = all("Pressable").find((item) => item.props.accessibilityRole === "tab" && textOf(item) === label);
        assert.ok(node, `找到页签 ${label}`);
        return act(async () => { node!.props.onPress(); });
      },
      pressButton: (label: string) => {
        const node = all("Button").find((item) => textOf(item) === label);
        assert.ok(node, `找到按钮 ${label}`);
        return act(async () => { node!.props.onPress(); });
      },
    };
  };

  // ---------- 按天视图（默认）：每天一张卡，我的班带「我」，今天标注，概况写清班数与人数 ----------
  {
    const view = await render();
    const texts = view.texts();
    for (const name of ["Ken", "Lena", "Mia", "Tom"]) assert.ok(texts.includes(name), `按天视图显示 ${name}`);
    assert.ok(texts.includes("我"), "我的班带「我」标记");
    assert.ok(texts.includes("Mia (我)"), "图例里我写在最前并标注");
    assert.ok(texts.includes("今天 · 3 人在岗"), "今天那天写「今天 · 3 人在岗」");
    assert.ok(texts.includes("09:00–17:00") && texts.includes("12:00–20:00"), "班次写完整起止时间");
    assert.ok(texts.includes("4 班 · 4 人 · 已发布"), "概况：班数、人数、已发布");
    assert.ok(texts.includes("Brisbane"), "概况里有分店名");
    assert.ok(view.has("Switch"), "分段开关由页面传入并放在周导航下面");
    assert.ok(texts.some((text) => text.includes("只显示店长已发布的班")), "有只显示已发布班次的提示");

    // 点周五的卡：弹出当天在岗，标题带「今天」；副标题写出我的班与同班时长
    await view.pressLabel((label) => label.startsWith("周五 11-20"));
    assert.deepEqual(view.sheetTexts("title"), ["周五 11/20 · 今天 · 3 人在岗"]);
    const subtitle = view.sheetTexts("subtitle")[0];
    assert.ok(subtitle.includes("你 10–18"), "副标题写出我的班");
    assert.ok(subtitle.includes("与 Lena 同时在岗 7 小时"), "与 Lena 重叠 10:00–17:00 = 7 小时");
    assert.ok(subtitle.includes("与 Tom 同时在岗 6 小时"), "与 Tom 重叠 12:00–18:00 = 6 小时");

    // 「看我的班次」：关弹层并切回我的班次
    await view.pressButton("看我的班次");
    assert.equal(view.calls.mine, 1);
    assert.deepEqual(view.sheetTexts("title"), [], "点完弹层关闭");
  }

  // ---------- 没班的那天点开：副标题写「你这天没有班」，且没有「看我的班次」按钮 ----------
  {
    const view = await render();
    await view.pressLabel((label) => label.startsWith("周一 11-16"));
    assert.equal(view.sheetTexts("subtitle")[0], "你这天没有班");
    assert.ok(!view.buttons().some((node) => textOf(node) === "看我的班次"), "我当天没班就不给跳转按钮");
  }

  // ---------- 表格视图：我的一行置顶；点名字看他的一周 ----------
  {
    const view = await render();
    await view.pressTab("表格");
    const texts = view.texts();
    assert.ok(texts.includes("9–17") && texts.includes("10–18") && texts.includes("12–20"), "格子只写起止小时");
    assert.ok(texts.includes("在岗"), "表格底部有在岗人数一行");
    assert.ok(texts.includes("点格子看当天谁在岗，点名字看他的一周。"), "有操作提示");
    assert.ok(!texts.includes("Mia (我)"), "表格视图不再重复图例");
    const labels = view.pressables().map((node) => String(node.props.accessibilityLabel ?? ""));
    const rowNames = labels.filter((label) => label.includes("· 本周")).map((label) => label.split(" · ")[0]);
    assert.deepEqual(rowNames, ["Mia", "Ken", "Lena", "Tom"], "我的一行置顶，其余按姓名");

    await view.pressLabel((label) => label.startsWith("Ken · 本周"));
    assert.deepEqual(view.sheetTexts("title"), ["Ken · 本周 1 班"]);
    assert.ok(view.texts().includes("休息"), "他休息的日子也列出来");
  }

  // ---------- 店长还没发布：本周只说明原因；未来周引导去填可上班时间 ----------
  {
    const current = await render({ week: unpublished });
    assert.ok(current.texts().includes("Brisbane本周的班还没发布"));
    assert.ok(!current.buttons().some((node) => textOf(node) === "去填可上班时间"), "本周没发布没有可填的事，不给按钮");

    const future = await render({ week: { ...unpublished, weekStartDate: "2026-11-23" }, weekStartDate: "2026-11-23" });
    assert.ok(future.texts().includes("Brisbane下周的班还没发布"));
    await future.pressButton("去填可上班时间");
    assert.equal(future.calls.mine, 1, "去填可上班时间 → 切回我的班次");
  }

  // ---------- 加载失败 / 加载中 / 没有分店 ----------
  {
    const failed = await render({ week: undefined, isError: true });
    assert.ok(failed.texts().includes("整店排班加载失败"));
    await failed.pressButton("重试");
    assert.equal(failed.calls.retry, 1);

    const stale = await render({ isError: true });
    assert.ok(stale.texts().includes("整店排班加载失败，下面是上次的内容"), "有旧数据时保留内容并提示");
    assert.ok(stale.texts().includes("Ken"), "旧数据仍然显示");

    const loading = await render({ week: undefined, isLoading: true });
    assert.ok(loading.has("ActivityIndicator"), "加载中显示转圈");

    const none = await render({ week: { weekStartDate: "2026-11-16", stores: [] } });
    assert.ok(none.texts().includes("你还没有关联的分店，请联系店长。"));
  }

  // ---------- 隐私底线：任何视图都不出现请假 / 备注 / 工时字样 ----------
  {
    const view = await render();
    const everything = (): string => view.texts().join("|");
    for (const word of ["请假", "备注", "工时"]) assert.ok(!everything().includes(word), `按天视图不应出现「${word}」`);
    await view.pressTab("表格");
    for (const word of ["请假", "备注", "工时"]) assert.ok(!everything().includes(word), `表格视图不应出现「${word}」`);
  }

  console.log("store-week-panel-render.test.ts: ok");
}

void run().catch((error) => {
  console.error(error);
  process.exit(1);
});
