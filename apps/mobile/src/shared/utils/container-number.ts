/**
 * 货柜编号遮罩（与后端 ContainerNumberMasking、Web 同一规则）：只保留后 4 位，前面每个字符换成 *，保持原长度。
 * 4 位及以内与空值原样返回；已遮罩的值再遮罩不变（无权限账号后端已返回遮罩值，前端重复遮罩也安全）。
 */
export const CONTAINER_NUMBER_VISIBLE_TAIL = 4;
export const CONTAINER_NUMBER_MASK_CHAR = "*";

export function maskContainerNumber(value: string | null | undefined): string {
  const text = value ?? "";
  if (text.length <= CONTAINER_NUMBER_VISIBLE_TAIL) return text;
  return (
    CONTAINER_NUMBER_MASK_CHAR.repeat(text.length - CONTAINER_NUMBER_VISIBLE_TAIL) +
    text.slice(-CONTAINER_NUMBER_VISIBLE_TAIL)
  );
}

/** showFull 只有在账号有「查看完整货柜编号」权限且用户点了显示按钮时才为 true。 */
export function formatContainerNumber(value: string | null | undefined, showFull: boolean): string {
  return showFull ? value ?? "" : maskContainerNumber(value);
}

/** 文件名用：去掉 *（Windows 文件名不允许），遮罩值只剩后 4 位。 */
export function containerNumberFileNamePart(value: string | null | undefined): string {
  return (value ?? "").split(CONTAINER_NUMBER_MASK_CHAR).join("");
}
