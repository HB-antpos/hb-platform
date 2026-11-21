export type ReceiptCode128Run = Readonly<{
  bar: boolean;
  modules: number;
}>;

export type ReceiptCode128Encoding = Readonly<{
  /** ESC/POS Function B 的 ASCII 载荷，集合切换不是扫码结果的一部分。 */
  payload: string;
  runs: readonly ReceiptCode128Run[];
  moduleCount: number;
}>;

const START_B = 104;
const START_C = 105;
const CODE_B = 100;
const CODE_C = 99;
const STOP = 106;
const QUIET_ZONE_MODULES = 20;

const PATTERNS = [
  "212222", "222122", "222221", "121223", "121322", "131222",
  "122213", "122312", "132212", "221213", "221312", "231212",
  "112232", "122132", "122231", "113222", "123122", "123221",
  "223211", "221132", "221231", "213212", "223112", "312131",
  "311222", "321122", "321221", "312212", "322112", "322211",
  "212123", "212321", "232121", "111323", "131123", "131321",
  "112313", "132113", "132311", "211313", "231113", "231311",
  "112133", "112331", "132131", "113123", "113321", "133121",
  "313121", "211331", "231131", "213113", "213311", "213131",
  "311123", "311321", "331121", "312113", "312311", "332111",
  "314111", "221411", "431111", "111224", "111422", "121124",
  "121421", "141122", "141221", "112214", "112412", "122114",
  "122411", "142112", "142211", "241211", "221114", "413111",
  "241112", "134111", "111242", "121142", "121241", "114212",
  "124112", "124211", "411212", "421112", "421211", "212141",
  "214121", "412121", "111143", "111341", "131141", "114113",
  "114311", "411113", "411311", "113141", "114131", "311141",
  "411131", "211412", "211214", "211232", "2331112",
] as const;

type CodeSet = "B" | "C";
type Solution = Readonly<{
  codes: readonly number[];
  tokens: readonly string[];
}>;

/**
 * 纯函数生成可扫描为原始字符串的 Code 128。动态规划只插入 B/C 集合切换，
 * 因此 GUID 的数字长段会压缩，但扫码结果仍是完整规范化 orderGuid。
 */
export function receiptCode128(value: string): ReceiptCode128Encoding {
  if (!/^[\x20-\x7e]+$/u.test(value)) {
    throw new TypeError("Code 128 value must be non-empty printable ASCII.");
  }

  const memo = new Map<string, Solution>();
  const solve = (index: number, set: CodeSet): Solution => {
    if (index === value.length) return { codes: [], tokens: [] };
    const key = `${index}:${set}`;
    const cached = memo.get(key);
    if (cached) return cached;

    const candidates: Solution[] = [];
    if (set === "B") {
      const character = value[index]!;
      const tail = solve(index + 1, "B");
      candidates.push({
        codes: [character.charCodeAt(0) - 32, ...tail.codes],
        tokens: [character === "{" ? "{{" : character, ...tail.tokens],
      });
      if (isDigitPair(value, index)) {
        const pair = value.slice(index, index + 2);
        const cTail = solve(index + 2, "C");
        candidates.push({
          codes: [CODE_C, Number(pair), ...cTail.codes],
          tokens: ["{C", pair, ...cTail.tokens],
        });
      }
    } else {
      if (isDigitPair(value, index)) {
        const pair = value.slice(index, index + 2);
        const tail = solve(index + 2, "C");
        candidates.push({
          codes: [Number(pair), ...tail.codes],
          tokens: [pair, ...tail.tokens],
        });
      }
      const bTail = solve(index, "B");
      candidates.push({
        codes: [CODE_B, ...bTail.codes],
        tokens: ["{B", ...bTail.tokens],
      });
    }

    const best = candidates.reduce((current, candidate) =>
      candidate.codes.length < current.codes.length ? candidate : current);
    memo.set(key, best);
    return best;
  };

  const starts: readonly Readonly<{
    code: number;
    token: string;
    solution: Solution;
  }>[] = [
    { code: START_B, token: "{B", solution: solve(0, "B") },
    { code: START_C, token: "{C", solution: solve(0, "C") },
  ];
  const start = starts.reduce((current, candidate) =>
    candidate.solution.codes.length < current.solution.codes.length
      ? candidate
      : current);
  const dataCodes = [start.code, ...start.solution.codes];
  const checksum = dataCodes.reduce(
    (total, code, index) => total + (index === 0 ? code : code * index),
    0,
  ) % 103;
  const symbolCodes = [...dataCodes, checksum, STOP];
  const runs: ReceiptCode128Run[] = [];
  for (const code of symbolCodes) {
    const pattern = PATTERNS[code];
    if (!pattern) throw new Error("Code 128 pattern is unavailable.");
    for (const width of pattern) {
      runs.push({
        bar: runs.length % 2 === 0,
        modules: Number(width),
      });
    }
  }
  return {
    payload: `${start.token}${start.solution.tokens.join("")}`,
    runs,
    moduleCount: runs.reduce((total, run) => total + run.modules, 0),
  };
}

export function receiptCode128ModuleWidth(
  encoding: ReceiptCode128Encoding,
  paper: "58mm" | "80mm",
  /**
   * 首选模块宽度。订单短码条码对齐 WPF 用 3 点（更容易扫），放不下再退到通用的 2 点；
   * 不传则只使用 2 点，其它条码的输出保持不变。
   */
  preferredModuleWidth: 2 | 3 = 2,
): number | null {
  const printableDots = paper === "58mm" ? 384 : 576;
  const candidates = preferredModuleWidth === 3 ? [3, 2] : [2];
  for (const moduleWidth of candidates) {
    const requiredDots = (
      encoding.moduleCount + QUIET_ZONE_MODULES
    ) * moduleWidth;
    // 通用 ESC/POS 最小只用可移植且可扫描的 2 点；放不下时由调用方
    // 省略一维码，禁止用单点模块生成不可靠的“伪成功”。
    if (requiredDots <= printableDots) return moduleWidth;
  }
  return null;
}

function isDigitPair(value: string, index: number): boolean {
  return /^\d{2}$/u.test(value.slice(index, index + 2));
}
