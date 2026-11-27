/**
 * iPad POS 主题色板（纯数据，不依赖 React Native，可在 tsx 测试里直接导入）。
 *
 * 约定：界面代码只引用语义色名（accent、surface、ink…），不写十六进制。
 * 每套主题必须给出完整的一组色值，新增色名时所有主题要同时补齐。
 */
// 顺序即设置页色样的排列顺序：浅色在前、深色在后，暖色、冷色、中性依次排开。
export const POS_THEME_IDS = [
  "classic",
  "bargain",
  "terracotta",
  "ocean",
  "violet",
  "forest",
  "graphite",
  "contrast",
  "dark",
  "midnight",
] as const;

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
    bargain: Object.freeze({
      id: "bargain",
      dark: false,
      label: { zh: "热卖红", en: "Bargain Red" },
      description: { zh: "暖白底 + 正红主按钮，招牌感最强", en: "Warm white with signature red" },
      palette: palette({
        ink: "#2A1215",
        mutedInk: "#76605F",
        canvas: "#F6F2EF",
        surface: "#FFFFFF",
        surfaceAlt: "#FAF6F3",
        surfaceSunken: "#EFE6E1",
        border: "#E3D7D1",
        borderStrong: "#2A1215",
        accent: "#D7262F",
        accentSoft: "#FDE6E5",
        accentPressed: "#A1171E",
        onAccent: "#FFFFFF",
        selectedSurface: "#FFF6F5",
        inverseSurface: "#4B0E14",
        onInverse: "#FFFFFF",
        chrome: "#4B0E14",
        chromeInk: "#FFFFFF",
        chromeMuted: "#D9A8AB",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#E8E2DE",
        disabledBorder: "#D5CBC6",
        disabledInk: "#857A78",
        placeholder: "#8C7A78",
        overlay: "rgba(75,14,20,0.55)",
        shadow: "#000000",
        ...LIGHT_STATUS,
        // 主按钮已是正红，「危险」状态改深莓红，避免「清除」「删除」和主按钮撞色。
        red: "#9B1C4E",
        redSoft: "#F8E6EE",
        redBorder: "#E2AFC4",
      }),
    }),
    terracotta: Object.freeze({
      id: "terracotta",
      dark: false,
      label: { zh: "陶土", en: "Terracotta" },
      description: { zh: "暖沙底 + 赭石主按钮", en: "Warm sand with terracotta" },
      palette: palette({
        ink: "#2B2118",
        mutedInk: "#74644F",
        canvas: "#F2EBE1",
        surface: "#FFFCF8",
        surfaceAlt: "#F8F2EA",
        surfaceSunken: "#EADFD0",
        border: "#DDCFBD",
        borderStrong: "#2B2118",
        accent: "#B4532A",
        accentSoft: "#F6E3D8",
        accentPressed: "#86391B",
        onAccent: "#FFFFFF",
        selectedSurface: "#FDF4EE",
        inverseSurface: "#3A2A1F",
        onInverse: "#FFFFFF",
        chrome: "#3A2A1F",
        chromeInk: "#FFFFFF",
        chromeMuted: "#C6B19B",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#E6DED3",
        disabledBorder: "#D3C6B5",
        disabledInk: "#8A7D6E",
        placeholder: "#8D7C69",
        overlay: "rgba(58,42,31,0.55)",
        shadow: "#000000",
        ...LIGHT_STATUS,
        // 暖底上用偏橄榄的绿和偏灰的蓝，和赭石主色更协调。
        green: "#4A7532",
        greenSoft: "#EAF2E1",
        greenBorder: "#B1CB9D",
        blue: "#2F6480",
        blueSoft: "#E4EFF4",
        blueBorder: "#AECBDA",
      }),
    }),
    violet: Object.freeze({
      id: "violet",
      dark: false,
      label: { zh: "暮光紫", en: "Twilight Violet" },
      description: { zh: "淡紫灰底 + 靛紫主按钮", en: "Soft lilac with violet" },
      palette: palette({
        ink: "#1E1A3A",
        mutedInk: "#625D80",
        canvas: "#F0EFF7",
        surface: "#FFFFFF",
        surfaceAlt: "#F7F6FC",
        surfaceSunken: "#E6E3F1",
        border: "#D7D3E8",
        borderStrong: "#1E1A3A",
        accent: "#5B45D9",
        accentSoft: "#ECE8FD",
        accentPressed: "#4231B0",
        onAccent: "#FFFFFF",
        selectedSurface: "#F6F4FE",
        inverseSurface: "#1E1A3A",
        onInverse: "#FFFFFF",
        chrome: "#1E1A3A",
        chromeInk: "#FFFFFF",
        chromeMuted: "#A8A2CF",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#E6E4EE",
        disabledBorder: "#D2CFE0",
        disabledInk: "#86829C",
        placeholder: "#827DA0",
        overlay: "rgba(30,26,58,0.55)",
        shadow: "#000000",
        ...LIGHT_STATUS,
        // 主按钮已是紫色，「信息」状态（数量 / 单价等动作键）改用青色区分。
        blue: "#0F6F83",
        blueSoft: "#E2F3F6",
        blueBorder: "#A6D3DD",
      }),
    }),
    graphite: Object.freeze({
      id: "graphite",
      dark: false,
      label: { zh: "石墨", en: "Graphite" },
      description: { zh: "黑白中性 + 黑色主按钮", en: "Neutral greys with black" },
      palette: palette({
        ink: "#161616",
        mutedInk: "#6B6B67",
        canvas: "#EFEFED",
        surface: "#FFFFFF",
        surfaceAlt: "#F7F7F5",
        surfaceSunken: "#E6E6E3",
        border: "#DCDCD8",
        borderStrong: "#161616",
        accent: "#161616",
        accentSoft: "#ECECE9",
        accentPressed: "#000000",
        onAccent: "#FFFFFF",
        selectedSurface: "#F5F5F3",
        inverseSurface: "#161616",
        onInverse: "#FFFFFF",
        chrome: "#1C1C1B",
        chromeInk: "#FFFFFF",
        chromeMuted: "#A3A39E",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#E6E6E3",
        disabledBorder: "#D3D3CF",
        disabledInk: "#80807C",
        placeholder: "#8A8A86",
        overlay: "rgba(22,22,22,0.55)",
        shadow: "#000000",
        ...LIGHT_STATUS,
      }),
    }),
    contrast: Object.freeze({
      id: "contrast",
      dark: false,
      label: { zh: "强光高对比", en: "High Contrast" },
      description: { zh: "纯黑白 + 黑底黄字主按钮，强光下也清楚", en: "Black, white and yellow for bright stores" },
      palette: palette({
        ink: "#000000",
        mutedInk: "#3A3A3A",
        canvas: "#E9E9E9",
        surface: "#FFFFFF",
        surfaceAlt: "#F4F4F4",
        surfaceSunken: "#E2E2E2",
        border: "#8F8F8F",
        borderStrong: "#000000",
        // 主操作色也会当文字色用在浅底上（选中项、角标），所以主色用黑、黄色只做选中底和主按钮文字。
        accent: "#000000",
        accentSoft: "#FFE45C",
        accentPressed: "#5A5A5A",
        onAccent: "#FFD000",
        selectedSurface: "#FFF6BF",
        inverseSurface: "#000000",
        onInverse: "#FFFFFF",
        chrome: "#000000",
        chromeInk: "#FFFFFF",
        chromeMuted: "#D2D2D2",
        chromeChip: "rgba(255,255,255,0.12)",
        chromeBorder: "rgba(255,255,255,0.6)",
        disabledSurface: "#E2E2E2",
        disabledBorder: "#B5B5B5",
        disabledInk: "#5E5E5E",
        placeholder: "#4A4A4A",
        overlay: "rgba(0,0,0,0.65)",
        shadow: "#000000",
        green: "#00662B",
        greenSoft: "#DDF5E5",
        greenBorder: "#00662B",
        yellow: "#9A5B00",
        yellowSoft: "#FFE7C2",
        yellowBorder: "#9A5B00",
        yellowInk: "#5A3300",
        red: "#B00020",
        redSoft: "#FFE3E6",
        redBorder: "#B00020",
        blue: "#0040A8",
        blueSoft: "#E1EBFF",
        blueBorder: "#0040A8",
      }),
    }),
    midnight: Object.freeze({
      id: "midnight",
      dark: true,
      label: { zh: "午夜青", en: "Midnight Teal" },
      description: { zh: "深海军蓝底 + 青绿主按钮", en: "Deep navy with teal" },
      palette: palette({
        ink: "#E8EEF8",
        mutedInk: "#93A3BD",
        canvas: "#0B1220",
        surface: "#131C2E",
        surfaceAlt: "#172238",
        surfaceSunken: "#1E2A42",
        border: "#26344F",
        borderStrong: "#5D7091",
        accent: "#2DD4BF",
        accentSoft: "#0F3533",
        accentPressed: "#169C8B",
        onAccent: "#04211D",
        selectedSurface: "#11292B",
        inverseSurface: "#2A3B57",
        onInverse: "#FFFFFF",
        chrome: "#070C16",
        chromeInk: "#FFFFFF",
        chromeMuted: "#7F8EA8",
        chromeChip: "rgba(255,255,255,0.08)",
        chromeBorder: "rgba(255,255,255,0.45)",
        disabledSurface: "#1A2436",
        disabledBorder: "#26344A",
        disabledInk: "#66758C",
        placeholder: "#7586A2",
        overlay: "rgba(0,0,0,0.6)",
        shadow: "#000000",
        // 主按钮已是青绿，「成功」状态改黄绿，保持可区分。
        green: "#9AD86B",
        greenSoft: "#1D3320",
        greenBorder: "#3F6B33",
        yellow: "#F2C14E",
        yellowSoft: "#342A12",
        yellowBorder: "#6E5A22",
        yellowInk: "#F6D98A",
        red: "#FF7A85",
        redSoft: "#3A1A22",
        redBorder: "#7A3442",
        blue: "#8AB4FF",
        blueSoft: "#16244A",
        blueBorder: "#2E4682",
      }),
    }),
  });

export function isPosThemeId(value: unknown): value is PosThemeId {
  return (
    typeof value === "string" &&
    (POS_THEME_IDS as readonly string[]).includes(value)
  );
}
