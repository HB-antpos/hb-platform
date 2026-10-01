/** 详情页核查成功后通知列表刷新（列表与详情是 Stack 里的两个页面，返回时列表还在内存里）。 */
type Listener = () => void;
const listeners = new Set<Listener>();

export function subscribeLegacyLogReviewed(listener: Listener) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function notifyLegacyLogReviewed() {
  listeners.forEach((listener) => listener());
}
