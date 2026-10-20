import assert from "node:assert/strict";
import Module from "node:module";
import React, { act } from "react";

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

  let focused = false;
  let blurCount = 0;
  let keyboardVisible = false;
  const platform = { OS: "android" };
  const keyboardListeners = new Set<() => void>();
  let removedListeners = 0;
  const FakeSearchbar = React.forwardRef<{ blur: () => void; isFocused: () => boolean }, Record<string, unknown>>(function FakeSearchbar(props, ref) {
    React.useImperativeHandle(ref, () => ({
      isFocused: () => focused,
      blur: () => {
        if (!focused) return;
        focused = false;
        blurCount++;
        (props.onBlur as () => void)();
      },
    }), [props.onBlur]);
    return React.createElement("Searchbar", {
      ...props,
      onFocus: () => {
        focused = true;
        (props.onFocus as () => void)();
      },
    });
  });
  mockModule("react-native", {
    Platform: platform,
    Keyboard: {
      isVisible: () => keyboardVisible,
      addListener: (event: string, listener: () => void) => {
        assert.equal(event, "keyboardDidHide");
        keyboardListeners.add(listener);
        return { remove: () => { keyboardListeners.delete(listener); removedListeners++; } };
      },
    },
    StyleSheet: { create: (styles: object) => styles },
    // 该测试验证宽屏（非紧凑）布局下的焦点交接；窄屏「⋯」菜单布局不在这里覆盖
    useWindowDimensions: () => ({ width: 800, height: 600, scale: 1, fontScale: 1 }),
    View: "View",
  });
  mockModule("react-native-paper", {
    Searchbar: FakeSearchbar,
    Menu: Object.assign((props: Record<string, unknown>) => React.createElement("Menu", props), {
      Item: (props: Record<string, unknown>) => React.createElement("MenuItem", props),
    }),
    IconButton: (props: Record<string, unknown>) => React.createElement("IconButton", props),
    Text: (props: Record<string, unknown>) => React.createElement("Text", props),
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: (key: string) => key }),
  });
  mockModule(require.resolve("../../shared/theme/tokens"), {
    HB_COLORS: { action: "blue", outline: "grey", white: "white" },
    HB_RADIUS: { control: 8 },
    HB_SPACING: { sm: 8, xxs: 2, xs: 4 },
  });

  const { createRoot } = await import("test-renderer");
  const { SearchPanel } = await import("../../components/product-maintenance/SearchPanel");
  const events: string[] = [];
  const root = createRoot();
  await act(() => root.render(React.createElement(SearchPanel, {
    value: "HB038-12",
    onChangeText: (value: string) => events.push(`input:${value}`),
    onFocus: () => events.push("focus"),
    onBlur: () => events.push("blur"),
    onSubmit: () => events.push("submit:HB038-12"),
    onClear: () => events.push("clear"),
  })));

  try {
    const searchbar = () => root.container.queryAll((node) => node.type === "Searchbar")[0]!;
    const searchButton = () => root.container.queryAll((node) =>
      node.type === "IconButton" && node.props.accessibilityLabel === "common:actions.search")[0]!;
    const hideKeyboard = () => { for (const listener of [...keyboardListeners]) listener(); };

    assert.equal(keyboardListeners.size, 1, "挂载时应订阅键盘隐藏事件");
    await act(() => searchbar().props.onChangeText("HB038-12"));
    assert.deepEqual(events, ["input:HB038-12"], "普通手输不能自行查询或失焦");
    await act(() => hideKeyboard());
    assert.equal(blurCount, 0, "未聚焦时收起键盘不能抢其他输入框的焦点");

    await act(() => searchbar().props.onFocus());
    await act(() => searchButton().props.onPress());
    assert.deepEqual(events.slice(-3), ["focus", "blur", "submit:HB038-12"], "点击搜索必须先失焦再提交查询");
    assert.equal(blurCount, 1);

    await act(() => searchbar().props.onFocus());
    await act(() => searchbar().props.onSubmitEditing());
    assert.deepEqual(events.slice(-3), ["focus", "blur", "submit:HB038-12"], "键盘搜索动作同样先释放焦点");
    assert.equal(blurCount, 2);

    await act(() => searchbar().props.onFocus());
    await act(() => hideKeyboard());
    assert.deepEqual(events.slice(-2), ["focus", "blur"], "按返回键隐藏软键盘后必须释放搜索框焦点");
    assert.equal(blurCount, 3);
    await act(() => hideKeyboard());
    assert.equal(blurCount, 3, "已失焦后键盘事件不应重复通知");

    const renderScannerPanel = async (value: string, loading = false) => {
      await act(() => root.render(React.createElement(SearchPanel, {
        value,
        loading,
        onChangeText: (next: string) => events.push(`input:${next}`),
        onFocus: () => events.push("focus"),
        onBlur: () => events.push("blur"),
        onSubmit: () => events.push("submit"),
        onScannerInput: (barcode: string) => events.push(`scan:${barcode}`),
        onClear: () => events.push("clear"),
      })));
      await act(() => searchbar().props.onFocus());
      events.length = 0;
    };

    await renderScannerPanel("9528503822120");
    await act(() => searchbar().props.onChangeText("95285038221209528503822120"));
    assert.deepEqual(events, ["blur", "scan:9528503822120"], "实体扫码追加到可见框时，只提交新条码并释放焦点");

    await renderScannerPanel("HB038-12");
    await act(() => searchbar().props.onChangeText("HB038-129528503822005"));
    assert.deepEqual(events, ["blur", "scan:9528503822005"], "旧货号后的完整扫码必须走自动查询入口");

    keyboardVisible = true;
    await renderScannerPanel("");
    await act(() => searchbar().props.onChangeText("9528503822005"));
    assert.deepEqual(events, ["input:9528503822005"], "软键盘显示时粘贴仍保持手动搜索行为");

    keyboardVisible = false;
    await renderScannerPanel("");
    for (let length = 1; length <= 13; length++) {
      await act(() => searchbar().props.onChangeText("9528503822005".slice(0, length)));
    }
    assert.equal(events.length, 13);
    assert.ok(events.every((event) => event.startsWith("input:")), "逐字符手输不能误当整条扫码");

    await renderScannerPanel("9528503822120", true);
    await act(() => searchbar().props.onChangeText("95285038221209528503822005"));
    assert.deepEqual(events, ["blur"], "查询或弹窗门控期间收到扫码时，不改关键字或发起新查询");

    platform.OS = "ios";
    await renderScannerPanel("");
    await act(() => searchbar().props.onChangeText("9528503822005"));
    assert.deepEqual(events, ["input:9528503822005"], "Android扫码兼容不能改变iOS搜索输入");
  } finally {
    await act(() => root.unmount());
  }
  assert.equal(keyboardListeners.size, 0, "卸载时移除键盘监听");
  assert.equal(removedListeners, 1);
  console.log("ok");
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
