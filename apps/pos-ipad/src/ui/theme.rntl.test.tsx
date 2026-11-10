import { afterEach, expect, test } from "@jest/globals";
import { act, render } from "@testing-library/react-native";
import { Text } from "react-native";

import {
  applyPosTheme,
  buildPosPaperTheme,
  createPosStyles,
  getActivePosThemeId,
  POS_THEMES,
  posColors,
  usePosTheme,
  usePosThemeVersion,
} from "./theme";

afterEach(async () => {
  await act(async () => {
    applyPosTheme("classic");
  });
});

test("posColors 始终指向当前主题的色板", () => {
  expect(getActivePosThemeId()).toBe("classic");
  expect(posColors.accent).toBe(POS_THEMES.classic.palette.accent);

  applyPosTheme("ocean");

  expect(posColors.accent).toBe(POS_THEMES.ocean.palette.accent);
  expect(Object.keys(posColors)).toEqual(
    Object.keys(POS_THEMES.ocean.palette),
  );
});

test("createPosStyles 按当前主题惰性求值，并按主题缓存", () => {
  let factoryRuns = 0;
  const styles = createPosStyles((colors) => {
    factoryRuns += 1;
    return { card: { backgroundColor: colors.surface, padding: 8 } };
  });

  // 创建时不求值：模块加载阶段不会把颜色定死。
  expect(factoryRuns).toBe(0);
  const classicCard = styles.card;
  expect(classicCard).toEqual({
    backgroundColor: POS_THEMES.classic.palette.surface,
    padding: 8,
  });

  applyPosTheme("dark");
  expect(styles.card.backgroundColor).toBe(POS_THEMES.dark.palette.surface);

  applyPosTheme("classic");
  expect(styles.card).toBe(classicCard);
  expect(factoryRuns).toBe(2);
});

test("订阅主题的组件在切换后重绘，未知主题与重复切换不触发重绘", async () => {
  let renders = 0;
  function Probe() {
    usePosThemeVersion();
    renders += 1;
    return <Text testID="accent">{posColors.accent}</Text>;
  }

  const screen = await render(<Probe />);
  expect(screen.getByTestId("accent").props.children).toBe(
    POS_THEMES.classic.palette.accent,
  );
  const rendersBefore = renders;

  await act(async () => applyPosTheme("forest"));
  expect(screen.getByTestId("accent").props.children).toBe(
    POS_THEMES.forest.palette.accent,
  );
  const rendersAfterSwitch = renders;
  expect(rendersAfterSwitch).toBeGreaterThan(rendersBefore);

  await act(async () => {
    applyPosTheme("forest");
    applyPosTheme("neon" as never);
  });
  expect(renders).toBe(rendersAfterSwitch);
  expect(getActivePosThemeId()).toBe("forest");
});

test("usePosTheme 返回当前主题定义，Paper 主题跟随主操作色与深浅", async () => {
  function Probe() {
    const theme = usePosTheme();
    return <Text testID="theme">{theme.id}</Text>;
  }
  const screen = await render(<Probe />);
  await act(async () => applyPosTheme("dark"));
  expect(screen.getByTestId("theme").props.children).toBe("dark");

  const paper = buildPosPaperTheme("dark");
  expect(paper.dark).toBe(true);
  expect(paper.colors.primary).toBe(POS_THEMES.dark.palette.accent);
  expect(buildPosPaperTheme("classic").dark).toBe(false);
});
