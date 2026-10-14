// 推送模块的小型异步工具：超时包装。不依赖 React Native。

/** 超时；调用方统一按「失败」处理，不区分具体原因。 */
export class PushTimeoutError extends Error {
  constructor() {
    super("PUSH_TIMEOUT");
    this.name = "PushTimeoutError";
  }
}

/** 给 promise 加超时：超时后 reject，原 promise 之后的结果 / 异常都被吞掉（不产生未处理的 rejection）。 */
export function withTimeout<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new PushTimeoutError()), timeoutMs);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timer);
        reject(error);
      },
    );
  });
}
