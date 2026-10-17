// 「有没做完的操作」登记处：页面在有未保存草稿 / 进行中的写入时登记一个检查函数，
// 切换账号前统一查询，有就提示「请先完成当前操作」，避免切换把数据弄丢。

type PendingWorkCheck = () => boolean;

const checks = new Set<PendingWorkCheck>();

/** 登记检查函数，返回注销函数（在页面卸载时调用）。 */
export function registerPendingWorkCheck(check: PendingWorkCheck): () => void {
  checks.add(check);
  return () => {
    checks.delete(check);
  };
}

export function hasPendingWork(): boolean {
  for (const check of checks) {
    try {
      if (check()) return true;
    } catch {
      // 检查函数自己出错时不拦切换。
    }
  }
  return false;
}
