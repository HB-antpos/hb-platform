import assert from "node:assert/strict";
import test from "node:test";

import {
  DEFAULT_POS_THEME_ID,
  isPosThemeId,
  POS_THEME_IDS,
  POS_THEMES,
  type PosPalette,
} from "./theme-palettes";

function luminance(hex: string): number {
  const channels = [1, 3, 5].map((start) => {
    const value = Number.parseInt(hex.slice(start, start + 2), 16) / 255;
    return value <= 0.03928
      ? value / 12.92
      : ((value + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(foreground: string, background: string): number {
  const [lighter, darker] = [luminance(foreground), luminance(background)].sort(
    (a, b) => b - a,
  ) as [number, number];
  return (lighter + 0.05) / (darker + 0.05);
}

test("每套主题都提供同一组完整的语义色名", () => {
  const expectedKeys = Object.keys(POS_THEMES[DEFAULT_POS_THEME_ID].palette).sort();
  assert.ok(expectedKeys.length > 30);

  for (const themeId of POS_THEME_IDS) {
    const theme = POS_THEMES[themeId];
    assert.equal(theme.id, themeId);
    assert.deepEqual(Object.keys(theme.palette).sort(), expectedKeys, themeId);
    for (const [name, value] of Object.entries(theme.palette)) {
      assert.match(value, /^(#[0-9A-F]{6}|rgba\(\d+,\d+,\d+,0?\.\d+\))$/i, `${themeId}.${name}`);
    }
    assert.ok(theme.label.zh && theme.label.en, themeId);
  }
});

test("旧色名 orange 是主操作色的别名，换主题后跟随主题", () => {
  for (const themeId of POS_THEME_IDS) {
    const palette = POS_THEMES[themeId].palette;
    assert.equal(palette.orange, palette.accent, themeId);
    assert.equal(palette.orangeSoft, palette.accentSoft, themeId);
  }
  assert.notEqual(POS_THEMES.ocean.palette.accent, POS_THEMES.classic.palette.accent);
});

test("各主题的正文、主按钮文字和顶栏文字都有足够对比度", () => {
  const pairs: readonly (readonly [keyof PosPalette, keyof PosPalette, number])[] = [
    ["ink", "surface", 7],
    ["ink", "canvas", 7],
    ["mutedInk", "surface", 4.5],
    // 主按钮文字是 18pt 以上粗体，按大字标准 3:1。
    ["onAccent", "accent", 3],
    ["onInverse", "inverseSurface", 4.5],
    ["chromeInk", "chrome", 7],
    ["red", "redSoft", 4.5],
    ["green", "greenSoft", 4],
    ["yellowInk", "yellowSoft", 4.5],
    ["blue", "blueSoft", 4.5],
  ];

  for (const themeId of POS_THEME_IDS) {
    const palette = POS_THEMES[themeId].palette;
    for (const [foreground, background, minimum] of pairs) {
      const ratio = contrast(palette[foreground], palette[background]);
      assert.ok(
        ratio >= minimum,
        `${themeId}: ${foreground} on ${background} = ${ratio.toFixed(2)}，低于 ${minimum}`,
      );
    }
  }
});

test("主题 ID 校验拒绝未知值", () => {
  assert.equal(isPosThemeId("dark"), true);
  assert.equal(isPosThemeId("neon"), false);
  assert.equal(isPosThemeId(null), false);
});
