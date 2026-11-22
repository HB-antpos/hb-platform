/**
 * 1bit 单色位图：每行 rowBytes 字节、高位在左、1 = 黑，与 CPCL EG / CG 的数据布局一致。
 * 标签排版在 JS 里用它裁剪、拼合文字位图，再整体下发（纯 JS，可走 OTA）。
 */
export class MonoBitmap {
  readonly width: number;
  readonly height: number;
  readonly rowBytes: number;
  readonly bits: Uint8Array;

  constructor(width: number, height: number, bits?: Uint8Array) {
    if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1) {
      throw new Error(`Invalid bitmap size: ${width}x${height}`);
    }
    this.width = width;
    this.height = height;
    this.rowBytes = Math.ceil(width / 8);
    const expected = this.rowBytes * height;
    if (bits && bits.length !== expected) {
      throw new Error(`Bitmap data has ${bits.length} bytes, expected ${expected}.`);
    }
    this.bits = bits ?? new Uint8Array(expected);
  }

  static filled(width: number, height: number, black: boolean) {
    const bitmap = new MonoBitmap(width, height);
    if (black) bitmap.fillRect(0, 0, width, height, true);
    return bitmap;
  }

  get(x: number, y: number) {
    if (x < 0 || y < 0 || x >= this.width || y >= this.height) return false;
    return (this.bits[y * this.rowBytes + (x >> 3)] & (0x80 >> (x & 7))) !== 0;
  }

  set(x: number, y: number, black: boolean) {
    if (x < 0 || y < 0 || x >= this.width || y >= this.height) return;
    const index = y * this.rowBytes + (x >> 3);
    const mask = 0x80 >> (x & 7);
    this.bits[index] = black ? this.bits[index] | mask : this.bits[index] & ~mask;
  }

  fillRect(x: number, y: number, width: number, height: number, black: boolean) {
    const x0 = Math.max(0, Math.floor(x));
    const y0 = Math.max(0, Math.floor(y));
    const x1 = Math.min(this.width, Math.floor(x + width));
    const y1 = Math.min(this.height, Math.floor(y + height));
    for (let row = y0; row < y1; row += 1) {
      for (let col = x0; col < x1; col += 1) this.set(col, row, black);
    }
  }

  /**
   * 裁出 (x, y, width, height) 区域；超出原图的部分按 outside 填充（反白文字补边要填黑）。
   */
  crop(x: number, y: number, width: number, height: number, outside = false) {
    const result = new MonoBitmap(Math.max(1, width), Math.max(1, height));
    for (let row = 0; row < result.height; row += 1) {
      for (let col = 0; col < result.width; col += 1) {
        const sx = x + col;
        const sy = y + row;
        const inside = sx >= 0 && sy >= 0 && sx < this.width && sy < this.height;
        if (inside ? this.get(sx, sy) : outside) result.set(col, row, true);
      }
    }
    return result;
  }

  /**
   * 把 source 画到 (dx, dy)：mode = "or" 只叠加黑点（文字拼接用）；"copy" 连白点一起覆盖（反白块里放白字用）。
   */
  draw(source: MonoBitmap, dx: number, dy: number, mode: "or" | "copy" = "or") {
    const ox = Math.round(dx);
    const oy = Math.round(dy);
    for (let row = 0; row < source.height; row += 1) {
      const ty = oy + row;
      if (ty < 0 || ty >= this.height) continue;
      for (let col = 0; col < source.width; col += 1) {
        const tx = ox + col;
        if (tx < 0 || tx >= this.width) continue;
        const black = source.get(col, row);
        if (black) this.set(tx, ty, true);
        else if (mode === "copy") this.set(tx, ty, false);
      }
    }
  }

  /** 最近邻缩放（只用于超长货位代码这类极端兜底，对应 Android createScaledBitmap）。 */
  scale(width: number, height: number) {
    const result = new MonoBitmap(Math.max(1, Math.floor(width)), Math.max(1, Math.floor(height)));
    for (let row = 0; row < result.height; row += 1) {
      const sy = Math.min(this.height - 1, Math.floor((row * this.height) / result.height));
      for (let col = 0; col < result.width; col += 1) {
        const sx = Math.min(this.width - 1, Math.floor((col * this.width) / result.width));
        if (this.get(sx, sy)) result.set(col, row, true);
      }
    }
    return result;
  }

  /** 大写十六进制，供 CPCL EG 使用。 */
  toHex() {
    let hex = "";
    for (const byte of this.bits) hex += HEX[byte];
    return hex;
  }
}

const HEX = Array.from({ length: 256 }, (_, value) => value.toString(16).padStart(2, "0").toUpperCase());
