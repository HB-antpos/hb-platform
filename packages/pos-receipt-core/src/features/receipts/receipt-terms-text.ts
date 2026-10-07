import type { ReceiptTermsBlock } from "./receipt-document";

/**
 * 总部下发的「退款券使用说明（VOUCHER TERMS）/ 分期条款（INSTALLMENT TERMS）」自定义正文。
 *
 * - 内容是多行纯文本，一行一条；标题仍由收银端固定打印，字段只是标题下面的正文。
 * - 缺省 / null / 空串 / 纯空白 = 未定制，按内置默认文案打印（默认文案常量保持逐字不变）。
 * - 这两个字段挂在各端「冻结设置」的同一层（与 paper / locale / store 并列），由组合根按需带入。
 */
export type ReceiptCustomTermsText = Readonly<{
  /** 退款券券面底部「VOUCHER TERMS」的正文。 */
  voucherTerms?: string | null | undefined;
  /** 进行中分期小票底部「INSTALLMENT TERMS」的正文。 */
  installmentTerms?: string | null | undefined;
}>;

/**
 * 自定义正文长度上限，按 UTF-16 码元计（即 JS 的 `.length`），与服务端及本机设置存储同口径。
 * 设置存储、设置页输入框与同步校验都引用这个数值，不要在别处另写 600。
 */
export const RECEIPT_TERMS_TEXT_MAX_LENGTH = 600;

/**
 * 与退货政策同口径：只放行 CR / LF / TAB，其余 C0、DEL、C1 控制字符一律不允许。
 * 自定义文本不再像默认文案那样「只含可打印 ASCII」，所以这道净化是防止 ESC/POS 指令注入的必要关卡。
 */
const UNSAFE_CONTROL_CHARACTERS = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/u;

/** 是否为合规的自定义正文：必须是字符串、不超长、不含不允许的控制字符。空白文本也算合规（表示未定制）。 */
export function isValidReceiptTermsText(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length <= RECEIPT_TERMS_TEXT_MAX_LENGTH &&
    !UNSAFE_CONTROL_CHARACTERS.test(value)
  );
}

/**
 * 把自定义正文拆成可打印的正文行：按 `\r\n|\r|\n` 拆行 → 逐行 trim → 丢弃空行。
 * 行内残留的 TAB 换成单个空格（TAB 在票面上没有意义，也不能原样进入 ESC/POS 字节流）。
 * 不合规（非字符串、超长、含不允许的控制字符）一律视为没有正文，返回空数组。
 */
export function receiptTermsTextLines(value: unknown): readonly string[] {
  if (!isValidReceiptTermsText(value)) return [];
  const lines: string[] = [];
  for (const rawLine of value.split(/\r\n|\r|\n/u)) {
    const line = rawLine.trim().replace(/\t/gu, " ");
    if (line.length > 0) lines.push(line);
  }
  return lines;
}

/**
 * 解析最终要打印的条款块：有可打印的自定义正文就用「默认标题 + 自定义正文」，
 * 否则（未定制、全空白、超长或含不允许的控制字符而被整份丢弃）原样返回默认块——
 * 此时输出与未引入自定义功能前逐字节一致。
 *
 * 不合规文本在这里回退默认而不是抛错：设备端写入前已整份拒绝过一次，渲染层只是纵深防御，
 * 且券面/分期小票是顾客凭证，不能因为一段文案让整张票打不出来。
 */
export function resolveReceiptTermsBlock(
  defaultBlock: ReceiptTermsBlock,
  customText: unknown,
): ReceiptTermsBlock {
  const lines = receiptTermsTextLines(customText);
  if (lines.length === 0) return defaultBlock;
  return Object.freeze({
    title: defaultBlock.title,
    lines: Object.freeze(lines),
  });
}
