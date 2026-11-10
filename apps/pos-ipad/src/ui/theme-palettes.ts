/**
 * iPad POS 主题色板（纯数据，不依赖 React Native，可在 tsx 测试里直接导入）。
 *
 * 约定：界面代码只引用语义色名（accent、surface、ink…），不写十六进制。
 * 每套主题必须给出完整的一组色值，新增色名时四套主题要同时补齐。
 */
export const POS_THEME_IDS = ["classic", "ocean", "forest", "dark"] as const;

export type PosThemeId = (typeof POS_THEME_IDS)[number];

export const DEFAULT_POS_THEME_ID: PosThemeId = "classic";

export type PosPalette = Readonly<{
  /** 主文字与图标。 */
  ink: string;
  /** 次要文字、说明、占位标签。 */
  mutedInk: string;
  /** 页面底色。 */
  canvas: string;
  /** 卡片、按钮、输入框底色。 */
  surface: string;
  /** 比 surface 略深的分区底色（列表区、汇总条）。 */
  surfaceAlt: string;
  /** 更深一档的底色（缩略图占位、进度条槽）。 */
  surfaceSunken: string;
  /** 普通分隔线与卡片描边。 */
  border: string;
  /** 可点按钮与输入框的强描边。 */
  borderStrong: string;

  /** 主操作色：每屏唯一的主按钮、当前选中项。 */
  accent: string;
  /** 主操作色的浅底（选中的导航项、提示条）。 */
  accentSoft: string;
  /** 主按钮按下与底边阴影。 */
  accentPressed: string;
  /** 主操作色上的文字。 */
  onAccent: string;
  /** 选中行、选中块的浅底。 */
  selectedSurface: string;
  /** 分段控件选中段、深色标签的底色。 */
  inverseSurface: string;
  /** inverseSurface 上的文字。 */
  onInverse: string;

  /** 顶栏底色与其上的文字。 */
  chrome: string;
  chromeInk: string;
  chromeMuted: string;
  chromeChip: string;
  chromeBorder: string;

  /** 禁用态。 */
  disabledSurface: string;
  disabledBorder: string;
  disabledInk: string;
  /** 输入框占位文字。 */
  placeholder: string;
  /** 弹窗遮罩。 */
  overlay: string;
  shadow: string;

  /** 状态色：成功 / 提醒 / 危险 / 信息，各带浅底与浅描边。 */
  green: string;
  greenSoft: string;
  greenBorder: string;
  yellow: string;
  yellowSoft: string;
  yellowBorder: string;
  /** 黄色浅底上的文字。 */
  yellowInk: string;
  red: string;
  redSoft: string;
  redBorder: string;
  blue: string;
  blueSoft: string;
  blueBorder: string;

  /**
   * 兼容旧代码的别名：等同 accent / accentSoft。
   * 旧界面用 orange 表示主操作色，换主题后它跟随主题，不一定是橙色。
   */
  orange: string;
  orangeSoft: string;
}>;

export type PosThemeDefinition = Readonly<{
  id: PosThemeId;
  /** 深色主题需要浅色的系统状态栏文字。 */
  dark: boolean;
  label: Readonly<{ zh: string; en: string }>;
  description: Readonly<{ zh: string; en: string }>;
  palette: PosPalette;
}>;

type PaletteInput = Omit<PosPalette, "orange" | "orangeSoft">;

function palette(input: PaletteInput): PosPalette {
  return Object.freeze({
    ...input,
    orange: input.accent,
    orangeSoft: input.accentSoft,
  });
}

/** 浅色主题共用的状态色，保证四种状态在三套浅色主题里观感一致。 */
const LIGHT_STATUS = {
  green: "#277C63",
  greenSoft: "#E5F3ED",
  greenBorder: "#9CCDBC",
  yellow: "#B98516",
  yellowSoft: "#FFF5CF",
  yellowBorder: "#E4CC84",
  yellowInk: "#7A5A0E",
  red: "#B73932",
  redSoft: "#F9E7E5",
  redBorder: "#E3B1AC",
  blue: "#235C8C",
  blueSoft: "#E7F0F8",
  blueBorder: "#B4CDE2",
} as const;

export const POS_THEMES: Readonly<Record<PosThemeId, PosThemeDefinition>> =
  Object.freeze({
    classic: Object.freeze({
      id: "classic",
      dark: false,
      label: { zh: "经典暖色", en: "Classic Warm" },
      description: { zh: "米色底 + 橙色主按钮", en: "Warm paper with orange" },
      palette: palette({
        ink: "#10253A",
        mutedInk: "#5D6D7E",
        canvas: "#F4F1EA",
        surface: "#FFFFFF",
        surfaceAlt: "#F8F6F1",
        surfaceSunken: "#EEE9DF",
        border: "#D9D4C9",
        borderStrong: "#10253A",
        accent: "#E65A2F",
        accentSoft: "#FCEBE4",
        accentPressed: "#B8431F",
        onAccent: "#FFFFFF",
        selectedSurface: "#FFF8F4",
        inverseSurface: "#10253A",
        onInverse: "#FFFFFF",
        chrome: "#10253A",
        chromeInk: "#FFFFFF",
        chromeMuted: "#9DB0C3",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#E4E1DA",
        disabledBorder: "#D0CCC2",
        disabledInk: "#7C8287",
        placeholder: "#7B8793",
        overlay: "rgba(16,37,58,0.55)",
        shadow: "#000000",
        ...LIGHT_STATUS,
      }),
    }),
    ocean: Object.freeze({
      id: "ocean",
      dark: false,
      label: { zh: "海蓝", en: "Ocean Blue" },
      description: { zh: "冷灰底 + 蓝色主按钮", en: "Cool grey with blue" },
      palette: palette({
        ink: "#0F2742",
        mutedInk: "#5A6B80",
        canvas: "#EEF3F8",
        surface: "#FFFFFF",
        surfaceAlt: "#F5F8FB",
        surfaceSunken: "#E3EAF2",
        border: "#CDD8E4",
        borderStrong: "#0F2742",
        accent: "#1F6FD1",
        accentSoft: "#E3EEFB",
        accentPressed: "#1554A3",
        onAccent: "#FFFFFF",
        selectedSurface: "#F2F7FE",
        inverseSurface: "#0F2742",
        onInverse: "#FFFFFF",
        chrome: "#0F2742",
        chromeInk: "#FFFFFF",
        chromeMuted: "#9DB4CC",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#E4E9EF",
        disabledBorder: "#CBD3DC",
        disabledInk: "#8490A0",
        placeholder: "#7B8793",
        overlay: "rgba(15,39,66,0.55)",
        shadow: "#000000",
        ...LIGHT_STATUS,
        // 主按钮已是蓝色，「信息」状态改用靛紫，避免和主按钮混在一起。
        blue: "#5A55C8",
        blueSoft: "#ECEBFA",
        blueBorder: "#C2C0EE",
      }),
    }),
    forest: Object.freeze({
      id: "forest",
      dark: false,
      label: { zh: "松绿", en: "Forest Green" },
      description: { zh: "浅绿底 + 墨绿主按钮", en: "Soft green with teal" },
      palette: palette({
        ink: "#16302B",
        mutedInk: "#5B6E68",
        canvas: "#EFF4EF",
        surface: "#FFFFFF",
        surfaceAlt: "#F6F9F5",
        surfaceSunken: "#E3EBE3",
        border: "#CFDACF",
        borderStrong: "#16302B",
        accent: "#0F766E",
        accentSoft: "#DDF1EE",
        accentPressed: "#0B5750",
        onAccent: "#FFFFFF",
        selectedSurface: "#F1FAF8",
        inverseSurface: "#16302B",
        onInverse: "#FFFFFF",
        chrome: "#16302B",
        chromeInk: "#FFFFFF",
        chromeMuted: "#A3BDB5",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#E5EAE5",
        disabledBorder: "#CDD6CD",
        disabledInk: "#84928C",
        placeholder: "#78887F",
        overlay: "rgba(22,48,43,0.55)",
        shadow: "#000000",
        ...LIGHT_STATUS,
        // 主按钮已是墨绿，「成功」状态改用更黄一点的草绿，保持可区分。
        green: "#2B7A2F",
        greenSoft: "#E6F3E3",
        greenBorder: "#A9D0A4",
      }),
    }),
    dark: Object.freeze({
      id: "dark",
      dark: true,
      label: { zh: "深色", en: "Dark" },
      description: { zh: "深底浅字，适合暗光门店", en: "Dark surfaces for dim stores" },
      palette: palette({
        ink: "#EEF2F6",
        mutedInk: "#9FB0C0",
        canvas: "#0F1720",
        surface: "#18232F",
        surfaceAlt: "#1D2A38",
        surfaceSunken: "#243342",
        border: "#2F4050",
        borderStrong: "#7E93A8",
        accent: "#F0683C",
        accentSoft: "#3B241B",
        accentPressed: "#B8431F",
        onAccent: "#FFFFFF",
        selectedSurface: "#2A2420",
        inverseSurface: "#3A4E63",
        onInverse: "#FFFFFF",
        chrome: "#0A1118",
        chromeInk: "#FFFFFF",
        chromeMuted: "#9DB0C3",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#222E3A",
        disabledBorder: "#2F3C49",
        disabledInk: "#6F7F8F",
        placeholder: "#7F8FA0",
        overlay: "rgba(0,0,0,0.6)",
        shadow: "#000000",
        green: "#4CC39A",
        greenSoft: "#15332A",
        greenBorder: "#2B6B57",
        yellow: "#E5B94A",
        yellowSoft: "#3A3016",
        yellowBorder: "#6E5A1E",
        yellowInk: "#F0D68A",
        red: "#F0776F",
        redSoft: "#3D1E1C",
        redBorder: "#7A3A36",
        blue: "#6FB1EA",
        blueSoft: "#172C40",
        blueBorder: "#2F567A",
      }),
    }),
  });

export function isPosThemeId(value: unknown): value is PosThemeId {
  return (
    typeof value === "string" &&
    (POS_THEME_IDS as readonly string[]).includes(value)
  );
}
