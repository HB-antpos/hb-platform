/**
 * 数量编辑的快捷数量：清零 + 起订量的 1/2/3/4/8 倍。
 * 起订量为 1 时退化为 1/2/3/4/8，避免给出不能整除起订量的数字。
 */
export function buildQuantityPresets(step: number) {
  const safeStep = Number.isFinite(step) && step > 0 ? Math.floor(step) : 1;
  const multiples = [1, 2, 3, 4, 8].map((factor) => factor * safeStep);
  return [0, ...multiples];
}
