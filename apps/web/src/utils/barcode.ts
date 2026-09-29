import JsBarcode from 'jsbarcode'

export interface BarcodeOptions {
  width?: number
  height?: number
  displayValue?: boolean
  fontSize?: number
  margin?: number
  background?: string
  lineColor?: string
}

export const defaultBarcodeOptions: BarcodeOptions = {
  width: 2,
  height: 80,
  displayValue: true,
  fontSize: 12,
  margin: 10,
  background: '#ffffff',
  lineColor: '#000000',
}

export type BarcodeFormat = 'EAN13' | 'CODE128'

export function calculateEAN13CheckDigit(firstTwelveDigits: string): number | null {
  if (!/^\d{12}$/.test(firstTwelveDigits)) {
    return null
  }

  const digits = firstTwelveDigits.split('').map(Number)
  const sum = digits.reduce((total, digit, index) => {
    const position = index + 1
    return total + digit * (position % 2 === 0 ? 3 : 1)
  }, 0)

  return (10 - (sum % 10)) % 10
}

export function isValidEAN13(barcode: string): boolean {
  if (!/^\d{13}$/.test(barcode)) {
    return false
  }

  const expectedCheckDigit = calculateEAN13CheckDigit(barcode.slice(0, 12))
  if (expectedCheckDigit === null) {
    return false
  }

  return expectedCheckDigit === Number(barcode[12])
}

export function resolveBarcodeFormat(barcode: string): BarcodeFormat {
  return isValidEAN13(barcode) ? 'EAN13' : 'CODE128'
}

export function renderBarcodeToCanvas(
  canvas: HTMLCanvasElement,
  barcode: string,
  options: BarcodeOptions = defaultBarcodeOptions,
) {
  const format = resolveBarcodeFormat(barcode)

  try {
    JsBarcode(canvas, barcode, {
      format,
      ...defaultBarcodeOptions,
      ...options,
    })
  } catch (error) {
    if (format === 'EAN13') {
      JsBarcode(canvas, barcode, {
        format: 'CODE128',
        ...defaultBarcodeOptions,
        ...options,
      })
      return
    }

    throw error
  }
}

/**
 * 只编码、不绘制：按调用方指定的码制返回条码模块序列（'1' 为黑条模块，'0' 为空白模块）。
 * 走 JsBarcode 文档化的“传入普通对象接收 encodings”方式，不依赖 DOM，调用方可自行绘制矢量条码。
 * 与 renderBarcodeToCanvas 不同，这里不做 EAN13 自动判断，码制完全由调用方决定；
 * 内容无法按该码制编码（如 CODE128 遇到中文）时返回 null，调用方据此不渲染条码。
 */
export function encodeBarcodeModules(barcode: string, format: BarcodeFormat): string | null {
  if (!barcode) {
    return null
  }

  const target: { encodings?: { data?: string }[] } = {}
  try {
    JsBarcode(target, barcode, { format })
  } catch {
    // JsBarcode 遇到非法内容抛出的是字符串而不是 Error，这里统一按“不可编码”处理。
    return null
  }

  const modules = (target.encodings ?? []).map((encoding) => encoding.data ?? '').join('')
  return /^[01]+$/.test(modules) ? modules : null
}

/**
 * 把模块序列转成单个 SVG path 的 d 属性：相邻黑条模块合并成一个矩形子路径。
 * 用一个 path 而不是逐条 rect，DOM 更轻，html2canvas 序列化 SVG 时逐节点复制的计算样式也更少。
 */
export function buildBarcodeSvgPath(modules: string, moduleWidth: number, height: number): string {
  const segments: string[] = []
  let barStart = -1

  // 多走一步到 modules.length，保证以黑条结尾的最后一段也能闭合输出。
  for (let index = 0; index <= modules.length; index += 1) {
    const isBar = modules[index] === '1'
    if (isBar && barStart < 0) {
      barStart = index
    } else if (!isBar && barStart >= 0) {
      const barWidth = (index - barStart) * moduleWidth
      segments.push(`M${barStart * moduleWidth} 0h${barWidth}v${height}h-${barWidth}z`)
      barStart = -1
    }
  }

  return segments.join('')
}

export function generateBarcodeDataUrl(
  barcode: string,
  options: BarcodeOptions = defaultBarcodeOptions,
): string {
  if (!barcode || !barcode.trim()) {
    throw new Error('条码内容不能为空')
  }

  const canvas = document.createElement('canvas')
  renderBarcodeToCanvas(canvas, barcode, options)
  return canvas.toDataURL('image/png')
}

export async function generateBarcodeImages(
  barcodes: string[],
  options: BarcodeOptions = defaultBarcodeOptions,
): Promise<Map<string, string>> {
  const barcodeMap = new Map<string, string>()

  for (const barcode of barcodes) {
    try {
      barcodeMap.set(barcode, generateBarcodeDataUrl(barcode, options))
    } catch (error) {
      console.error(`生成条码失败: ${barcode}`, error)
      barcodeMap.set(barcode, '')
    }
  }

  return barcodeMap
}
