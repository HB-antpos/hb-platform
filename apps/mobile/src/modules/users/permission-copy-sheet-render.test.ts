import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 复制权限弹层（两步：选人 → 确认）的真实渲染测试：
// 方向切换、分店范围（店长不能查全部分店）、预览 / 写入的调用参数、确认页逐人明细与跳过项、
// 合并 ⇄ 覆盖切换会作废旧预览并重新预览。
const zh = JSON.parse(
  readFileSync(resolve(__dirname, "../../locales/zh/screens/peopleAccess.json"), "utf8"),
) as Record<string, unknown>;

function resolveKey(key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>(
      (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
      zh,
    );
}

function lookup(key: string, options?: Record<string, unknown>): string {
  if (key.startsWith("common:")) return key;
  let value = resolveKey(key);
  if (options && typeof options.count === "number") {
    const plural = resolveKey(`${key}_other`);
    if (typeof plural === "string") value = plural;
  }
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

function textOf(node: Node | string): string {
  if (typeof node === "string") return node;
  return node.children.map(textOf).join("");
}

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
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props);
    Stub.displayName = type;
    return Stub;
  };
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
    ScrollView: host("ScrollView"),
  });
  mockModule("react-native-paper", {
    Text: host("Text"),
    Icon: host("Icon"),
    Button: host("Button"),
    Searchbar: host("Searchbar"),
    ActivityIndicator: host("ActivityIndicator"),
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });

  // 同事列表：记录每次查询用的分店参数（undefined=不查，null=全部分店，字符串=某一家）。
  const storeQueries: (string | null | undefined)[] = [];
  const colleagues = [
    { userGUID: "u-cur", username: "emily", fullName: "Emily Chen", status: 1, roleNames: ["StoreStaff"] },
    { userGUID: "u-jack", username: "jack.nguyen", fullName: "Jack Nguyen", givenName: "Jack", familyName: "Nguyen", nickname: "Jacky", status: 1, roleNames: ["StoreStaff"] },
    { userGUID: "u-wang", username: "wang.fang", fullName: "王芳", status: 1, roleNames: ["StoreStaff"] },
    { userGUID: "u-off", username: "off", fullName: "Disabled Dan", status: 0, roleNames: ["StoreStaff"] },
  ];
  mockModule(require.resolve("./hooks"), {
    useStoreUsers: (storeCode?: string | null) => {
      storeQueries.push(storeCode);
      return {
        data: storeCode === undefined ? undefined : colleagues,
        isLoading: false,
        isError: false,
        refetch: () => undefined,
      };
    },
  });
  mockModule(require.resolve("./pos-permission-sheet"), {
    POS_PERMISSION_COLORS: { brandSoft: "#e", infoSoft: "#e" },
    PosPermissionSheet: (props: Record<string, unknown>) =>
      React.createElement(
        "Sheet",
        { title: props.title, subtitle: props.subtitle, dismissable: props.dismissable },
        props.header as React.ReactNode,
        props.children as React.ReactNode,
        props.footer as React.ReactNode,
      ),
  });

  const { createRoot } = await import("test-renderer");
  const { PermissionCopySheet } = await import("./permission-copy-sheet");

  const root = createRoot();
  const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  const all = (type: string) => container.queryAll((node) => node.type === type);
  const texts = () => all("Text").map(textOf);
  const sheet = () => all("Sheet")[0];
  const tab = (label: string) =>
    all("Pressable").find((node) => node.props.accessibilityRole === "tab" && textOf(node) === label)!;
  const chip = (label: string) =>
    all("Pressable").find((node) => node.props.accessibilityRole === "button" && textOf(node).startsWith(label));
  const candidateRows = () =>
    all("Pressable").filter(
      (node) => node.props.accessibilityRole === "checkbox" || node.props.accessibilityRole === "radio",
    );
  const button = (predicate: (node: Node) => boolean) => all("Button").find(predicate)!;
  // 点击会触发异步预览 / 写入：在 act 里等几轮微任务，让其后的状态更新也落在 act 内。
  const flush = async () => {
    for (let index = 0; index < 5; index += 1) await Promise.resolve();
  };
  const press = (node: Node) =>
    act(async () => {
      node.props.onPress();
      await flush();
    });
  const footerButtons = () => all("Button");

  const previews: unknown[] = [];
  const commits: unknown[] = [];
  const copied: unknown[] = [];
  let previewDelay: Promise<void> = Promise.resolve();
  const makePreview = (input: { sourceUserGuid: string; targetUserGuids: string[]; mode: string }) => ({
    sourceUserGuid: input.sourceUserGuid,
    mode: input.mode as "merge" | "replace",
    preview: true,
    sourceDirectPermissionCodes: ["Cash.Manage", "Staff.ReviewProfile", "Reports.Hours.View"],
    transferablePermissionCodes: ["Cash.Manage", "Reports.Hours.View"],
    skippedPermissionCodes: ["Staff.ReviewProfile"],
    targets: input.targetUserGuids.map((guid, index) => ({
      userGuid: guid,
      added: index === 0 ? ["Cash.Manage", "Reports.Hours.View"] : [],
      removed: input.mode === "replace" && index === 0 ? ["Warehouse.Pick"] : [],
      unchangedCount: index === 0 ? 0 : 3,
    })),
  });
  const element = (overrides: Partial<React.ComponentProps<typeof PermissionCopySheet>> = {}) =>
    React.createElement(PermissionCopySheet, {
      initialDirection: "toOthers",
      currentEmployeeGuid: "u-cur",
      currentEmployeeName: "Emily Chen",
      storeFilter: {
        options: [
          { storeCode: "1008", storeName: "Bankstown" },
          { storeCode: "1012", storeName: "Liverpool" },
        ],
        initialStoreCode: "1008",
        allowAll: true,
      },
      actorUserGuid: "u-actor",
      resolvePermissionName: (code: string) => ({ "Cash.Manage": "现金管理", "Reports.Hours.View": "员工工时报表", "Staff.ReviewProfile": "员工资料审核", "Warehouse.Pick": "拣货" })[code] ?? code,
      onPreview: async (input: { sourceUserGuid: string; targetUserGuids: string[]; mode: string }) => {
        previews.push(input);
        await previewDelay;
        return makePreview(input) as never;
      },
      onCommit: async (input: { sourceUserGuid: string; targetUserGuids: string[]; mode: string }) => {
        commits.push(input);
        return { ...makePreview(input), preview: false } as never;
      },
      onDismiss: () => undefined,
      onCopied: (result: unknown) => copied.push(result),
      ...overrides,
    });

  try {
    // 1. 选人：管理员默认目标员工所属分店，可切分店 / 全部分店；当前员工与已停用的不在候选里。
    await act(() => root.render(element()));
    assert.equal(sheet().props.title, lookup("copy.toOthersTitle", { name: "Emily Chen" }));
    assert.equal(sheet().props.subtitle, lookup("copy.storeSubtitle", { store: "Bankstown" }));
    assert.equal(storeQueries.at(-1), "1008", "默认查目标员工所属分店");
    assert.deepEqual(
      candidateRows().map((node) => String(node.props.accessibilityLabel)),
      ["Jack Nguyen，Jacky，@jack.nguyen", "王芳，@wang.fang"],
      "候选人不含当前员工与已停用员工；昵称带在名字后",
    );
    assert.ok(chip("全部分店") && chip("Bankstown") && chip("Liverpool"), "管理员可以切分店 / 全部分店");
    await act(() => chip("全部分店")!.props.onPress());
    assert.equal(storeQueries.at(-1), null, "选「全部分店」才查全部（只有管理员有这个选项）");
    await act(() => chip("Liverpool")!.props.onPress());
    assert.equal(storeQueries.at(-1), "1012");

    // 2. 复制给他人可多选；下一步按钮写明人数；预览用合并方式。
    assert.ok(footerButtons().some((node) => textOf(node) === lookup("copy.nextIdle")));
    await act(() => candidateRows()[0].props.onPress());
    await act(() => candidateRows()[1].props.onPress());
    const next = footerButtons().find((node) => textOf(node).startsWith("下一步"))!;
    assert.equal(textOf(next), "下一步 · 复制给 2 人");
    await press(next);
    assert.deepEqual(previews.at(-1), {
      sourceUserGuid: "u-cur",
      targetUserGuids: ["u-jack", "u-wang"],
      mode: "merge",
    }, "默认合并，来源是当前员工");

    // 3. 确认页：逐人列出 +N、没变化的沉底，跳过项带原因，合并 / 覆盖在页面里切换。
    assert.equal(sheet().props.title, lookup("copy.confirm.title"));
    assert.equal(sheet().props.subtitle, "从 Emily Chen 复制直接权限 · 方式：合并");
    assert.ok(texts().includes("Jack Nguyen") && texts().includes("王芳"));
    assert.ok(texts().includes("+ 现金管理、员工工时报表"), "增加的权限逐项列出");
    assert.ok(texts().includes("+2"), "+2 的数量标签");
    assert.ok(texts().includes("已有全部 3 项，不变"), "没变化的说明已有多少项");
    assert.ok(
      texts().includes(lookup("copy.confirm.skipped", { count: 1, names: "「员工资料审核」" })),
      "被跳过的项写明原因（超出授权范围）",
    );
    assert.ok(texts().includes(lookup("copy.mode.merge")) && texts().includes(lookup("copy.mode.replace")));
    const submit = () => footerButtons().find((node) => textOf(node) === "确认复制给 1 人")!;
    assert.equal(submit().props.disabled, false, "有变化的人数 = 1，按钮写明确认复制给 1 人");

    // 4. 切到覆盖：旧预览作废并重新预览，期间不能确认；回来后列出会被移除的权限。
    let releasePreview: () => void = () => undefined;
    previewDelay = new Promise<void>((done) => {
      releasePreview = done;
    });
    await act(async () => {
      tab(lookup("copy.mode.replace")).props.onPress();
    });
    assert.deepEqual(previews.at(-1), {
      sourceUserGuid: "u-cur",
      targetUserGuids: ["u-jack", "u-wang"],
      mode: "replace",
    });
    assert.equal(
      footerButtons().find((node) => textOf(node) === "确认复制给 1 人")!.props.disabled,
      true,
      "重新预览期间旧预览已作废，不能确认",
    );
    assert.ok(texts().includes(lookup("copy.confirm.recalculating")), "提示正在重新计算");
    await act(async () => {
      releasePreview();
      await flush();
    });
    previewDelay = Promise.resolve();
    assert.equal(sheet().props.subtitle, "从 Emily Chen 复制直接权限 · 方式：覆盖");
    assert.ok(texts().includes("− 拣货"), "覆盖会移除的权限也要列出");
    assert.ok(texts().includes("−1"));
    assert.equal(submit().props.disabled, false);

    // 5. 确认写入：用覆盖方式提交，成功后交给 onCopied。
    await press(submit());
    assert.deepEqual(commits.at(-1), {
      sourceUserGuid: "u-cur",
      targetUserGuids: ["u-jack", "u-wang"],
      mode: "replace",
    });
    assert.equal(copied.length, 1);

    // 6. 返回选人：保留选择；切到「从同事复制」会清空选择且只能单选。
    await act(() => button((node) => textOf(node) === lookup("copy.confirm.back")).props.onPress());
    assert.equal(sheet().props.title, lookup("copy.toOthersTitle", { name: "Emily Chen" }));
    assert.equal(candidateRows().filter((node) => node.props.accessibilityState.checked).length, 2, "返回选人保留已选的人");
    await act(() => tab(lookup("copy.direction.fromColleague")).props.onPress());
    assert.equal(sheet().props.title, lookup("copy.fromColleagueTitle", { name: "Emily Chen" }));
    assert.equal(candidateRows().filter((node) => node.props.accessibilityState.checked).length, 0, "切方向清空选择");
    assert.ok(candidateRows().every((node) => node.props.accessibilityRole === "radio"), "从同事复制只能选一个");
    await act(() => candidateRows()[0].props.onPress());
    await act(() => candidateRows()[1].props.onPress());
    assert.equal(candidateRows().filter((node) => node.props.accessibilityState.checked).length, 1);
    const fromColleagueNext = footerButtons().find((node) => textOf(node) === lookup("copy.nextFromColleague"))!;
    await press(fromColleagueNext);
    assert.deepEqual(previews.at(-1), {
      sourceUserGuid: "u-wang",
      targetUserGuids: ["u-cur"],
      mode: "merge",
    }, "从同事复制：来源是所选同事，目标只有当前员工");
    assert.ok(
      footerButtons().some((node) => textOf(node) === lookup("copy.confirm.submitSelf")),
      "单人目标的确认按钮不写人数",
    );
    await act(() => root.unmount());

    // 7. 店长：只能在自己管理的分店里选，没有分店时不发请求，也没有「全部分店」。
    storeQueries.length = 0;
    const managerRoot = createRoot();
    const managerContainer = managerRoot.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    await act(() =>
      managerRoot.render(
        element({
          storeFilter: { options: [{ storeCode: "1008", storeName: "Bankstown" }], initialStoreCode: "1008", allowAll: false },
        }),
      ),
    );
    assert.equal(storeQueries.at(-1), "1008");
    assert.equal(
      managerContainer.queryAll((node) => node.type === "Pressable" && textOf(node) === lookup("copy.allStores")).length,
      0,
      "店长没有「全部分店」",
    );
    await act(() => managerRoot.unmount());
    // 弹层每次打开都是全新挂载：没有可用分店的店长不发请求。
    const noStoreRoot = createRoot();
    const noStoreContainer = noStoreRoot.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    await act(() => noStoreRoot.render(element({ storeFilter: { options: [], initialStoreCode: "", allowAll: false } })));
    assert.equal(storeQueries.at(-1), undefined, "店长没有可用分店时不发请求（绝不查全部分店）");
    assert.ok(
      noStoreContainer.queryAll((node) => node.type === "Text").map(textOf).includes(lookup("copy.unavailable")),
    );
    await act(() => noStoreRoot.unmount());
  } finally {
    // 根已在上面卸载；重复卸载会抛错，忽略。
    try {
      await act(() => root.unmount());
    } catch {
      // ignore
    }
  }
}

run()
  .then(() => {
    console.log("permission-copy-sheet-render.test.ts: ok");
  })
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
