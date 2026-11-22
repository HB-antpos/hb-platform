import { MonoBitmap } from "./mono-bitmap";

/**
 * 标签文档：排版结果的中间形式（位图块 + 原样输出的 CPCL 指令），再统一转成 EG 文本或 CG 二进制。
 * 打印与以后的界面预览共用同一份文档，保证所见即所得。
 */
export type LabelDocBitmap = { kind: "bitmap"; x: number; y: number; bitmap: MonoBitmap };
/** 条码、二维码、LINE、BARCODE-TEXT、CENTER 等打印机自带指令，按原顺序输出在全部位图之后。 */
export type LabelDocCommand = { kind: "command"; lines: string[] };
export type LabelDocItem = LabelDocBitmap | LabelDocCommand;

export type LabelDoc = {
  width: number;
  height: number;
  items: LabelDocItem[];
};

export type LabelGraphicsMode = "EG" | "CG";

export function bitmapItem(x: number, y: number, bitmap: MonoBitmap): LabelDocBitmap {
  return { kind: "bitmap", x: Math.round(x), y: Math.round(y), bitmap };
}

export function commandItem(...lines: string[]): LabelDocCommand {
  return { kind: "command", lines };
}

type Block = { x: number; y: number; bitmap: MonoBitmap };

/** EG / CG 按字节写整行：实际占用宽度是 rowBytes × 8，补齐的白点会盖住右边相邻位图。 */
function footprint(block: Block) {
  return {
    left: block.x,
    top: block.y,
    right: block.x + block.bitmap.rowBytes * 8,
    bottom: block.y + block.bitmap.height,
  };
}

function overlaps(a: Block, b: Block) {
  const fa = footprint(a);
  const fb = footprint(b);
  return fa.left < fb.right && fb.left < fa.right && fa.top < fb.bottom && fb.top < fa.bottom;
}

/**
 * 合并会互相覆盖的位图：把重叠（按字节补齐后的占用区计算）的位图按原顺序叠画成一张，直到两两不重叠。
 * 叠画用 copy：后画的位图（含白点）覆盖先画的，和打印机按顺序执行 EG 的结果一致，但不会再被补齐白边误伤。
 */
export function flattenBitmaps(blocks: readonly Block[]): Block[] {
  let current = blocks.map((block) => ({ ...block }));
  let merged = true;
  while (merged) {
    merged = false;
    outer: for (let i = 0; i < current.length; i += 1) {
      for (let j = i + 1; j < current.length; j += 1) {
        if (!overlaps(current[i], current[j])) continue;
        const a = current[i];
        const b = current[j];
        const left = Math.min(a.x, b.x);
        const top = Math.min(a.y, b.y);
        const right = Math.max(a.x + a.bitmap.width, b.x + b.bitmap.width);
        const bottom = Math.max(a.y + a.bitmap.height, b.y + b.bitmap.height);
        const canvas = new MonoBitmap(right - left, bottom - top);
        // 只按位图真实宽度叠画（不含补齐位），所以补齐白边不会再覆盖别的图。
        canvas.draw(a.bitmap, a.x - left, a.y - top, "copy");
        canvas.draw(b.bitmap, b.x - left, b.y - top, "copy");
        current = [...current.slice(0, i), { x: left, y: top, bitmap: canvas }, ...current.slice(i + 1, j), ...current.slice(j + 1)];
        merged = true;
        break outer;
      }
    }
  }
  return current;
}

function header(doc: LabelDoc) {
  return [`! 0 200 200 ${doc.height} 1`, `PAGE-WIDTH ${doc.width}`];
}

function splitItems(doc: LabelDoc) {
  const blocks: Block[] = [];
  const commands: string[] = [];
  for (const item of doc.items) {
    if (item.kind === "bitmap") {
      // 页面左 / 上之外的部分打印机不会画，裁掉以免坐标变负。
      const { x, y, bitmap } = item;
      if (x < 0 || y < 0) {
        const cropped = bitmap.crop(Math.max(0, -x), Math.max(0, -y), bitmap.width - Math.max(0, -x), bitmap.height - Math.max(0, -y));
        blocks.push({ x: Math.max(0, x), y: Math.max(0, y), bitmap: cropped });
      } else {
        blocks.push({ x, y, bitmap });
      }
    } else {
      commands.push(...item.lines);
    }
  }
  return { blocks: flattenBitmaps(blocks), commands };
}

/** EG 十六进制文本（经现有 print 通道下发，所有原生包都支持）。 */
export function labelDocToEgCommand(doc: LabelDoc): string {
  const { blocks, commands } = splitItems(doc);
  const lines = header(doc);
  for (const { x, y, bitmap } of blocks) {
    lines.push(`EG ${bitmap.rowBytes} ${bitmap.height} ${x} ${y} ${bitmap.toHex()}`);
  }
  lines.push(...commands, "PRINT");
  return lines.join("\r\n") + "\r\n";
}

function ascii(text: string) {
  const bytes = new Uint8Array(text.length);
  for (let index = 0; index < text.length; index += 1) {
    const code = text.charCodeAt(index);
    // 指令里只有 ASCII；万一混入非 ASCII（如条码内容）就报错，免得静默打错。
    if (code > 0x7f) throw new Error("CG label commands must be ASCII.");
    bytes[index] = code;
  }
  return bytes;
}

/** CG 二进制（经 writeBytes 下发，数据量约为 EG 的一半；需要新原生包且打印机支持 CG）。 */
export function labelDocToCgBytes(doc: LabelDoc): Uint8Array {
  const { blocks, commands } = splitItems(doc);
  const parts: Uint8Array[] = [ascii(header(doc).join("\r\n") + "\r\n")];
  for (const { x, y, bitmap } of blocks) {
    parts.push(ascii(`CG ${bitmap.rowBytes} ${bitmap.height} ${x} ${y} `), bitmap.bits, ascii("\r\n"));
  }
  parts.push(ascii([...commands, "PRINT"].join("\r\n") + "\r\n"));
  const output = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let offset = 0;
  for (const part of parts) {
    output.set(part, offset);
    offset += part.length;
  }
  return output;
}

/** 预览用：把全部位图画到整页（不含条码等打印机指令）。 */
export function renderLabelDocBitmap(doc: LabelDoc) {
  const page = new MonoBitmap(doc.width, doc.height);
  for (const { x, y, bitmap } of splitItems(doc).blocks) page.draw(bitmap, x, y, "copy");
  return page;
}
