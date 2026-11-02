/**
 * 纯函数 Code128 编码，与 packages/pos-receipt-core 的 receipt-code128.ts 同一算法（B/C 集合动态规划 + 校验和）。
 * apps/web 不在根 workspaces 里、Docker 构建上下文也没有 packages/，所以这里保留一份最小拷贝；
 * code128.test.ts 用参考实现算出的向量锁住，改动任何一边都会红。
 */

const START_B = 104
const START_C = 105
const CODE_B = 100
const CODE_C = 99
const STOP = 106

// 每个符号 6 个宽度（条空交替），终止符 7 个。
const PATTERNS = [
  '212222', '222122', '222221', '121223', '121322', '131222',
  '122213', '122312', '132212', '221213', '221312', '231212',
  '112232', '122132', '122231', '113222', '123122', '123221',
  '223211', '221132', '221231', '213212', '223112', '312131',
  '311222', '321122', '321221', '312212', '322112', '322211',
  '212123', '212321', '232121', '111323', '131123', '131321',
  '112313', '132113', '132311', '211313', '231113', '231311',
  '112133', '112331', '132131', '113123', '113321', '133121',
  '313121', '211331', '231131', '213113', '213311', '213131',
  '311123', '311321', '331121', '312113', '312311', '332111',
  '314111', '221411', '431111', '111224', '111422', '121124',
  '121421', '141122', '141221', '112214', '112412', '122114',
  '122411', '142112', '142211', '241211', '221114', '413111',
  '241112', '134111', '111242', '121142', '121241', '114212',
  '124112', '124211', '411212', '421112', '421211', '212141',
  '214121', '412121', '111143', '111341', '131141', '114113',
  '114311', '411113', '411311', '113141', '114131', '311141',
  '411131', '211412', '211214', '211232', '2331112',
] as const

export interface Code128Encoding {
  /** 交替的「条、空」宽度（单位：模块，第 0 个是条）。 */
  runs: readonly number[]
  /** 整个符号的模块总数，不含静区。 */
  moduleCount: number
}

function isDigitPair(value: string, index: number): boolean {
  return /^\d{2}$/u.test(value.slice(index, index + 2))
}

export function encodeCode128(value: string): Code128Encoding {
  if (!/^[\x20-\x7e]+$/u.test(value)) {
    throw new TypeError('Code 128 value must be non-empty printable ASCII.')
  }

  const memo = new Map<string, number[]>()
  const solve = (index: number, setC: boolean): number[] => {
    if (index === value.length) return []
    const key = `${index}:${setC ? 'C' : 'B'}`
    const cached = memo.get(key)
    if (cached) return cached

    let best: number[]
    if (!setC) {
      best = [value.charCodeAt(index) - 32, ...solve(index + 1, false)]
      if (isDigitPair(value, index)) {
        const viaC = [CODE_C, Number(value.slice(index, index + 2)), ...solve(index + 2, true)]
        if (viaC.length < best.length) best = viaC
      }
    } else {
      best = [CODE_B, ...solve(index, false)]
      if (isDigitPair(value, index)) {
        const stayC = [Number(value.slice(index, index + 2)), ...solve(index + 2, true)]
        if (stayC.length <= best.length) best = stayC
      }
    }
    memo.set(key, best)
    return best
  }

  const viaB = solve(0, false)
  const viaC = solve(0, true)
  // 长度相同优先 B 起始。
  const useC = viaC.length < viaB.length
  const dataCodes = [useC ? START_C : START_B, ...(useC ? viaC : viaB)]
  const checksum = dataCodes.reduce((total, code, index) => total + (index === 0 ? code : code * index), 0) % 103
  const symbolCodes = [...dataCodes, checksum, STOP]

  const runs: number[] = []
  for (const code of symbolCodes) {
    for (const width of PATTERNS[code]) runs.push(Number(width))
  }
  return { runs, moduleCount: runs.reduce((total, run) => total + run, 0) }
}
