// 幂等请求号持有者：一次表单会话只生成一个 clientRequestId，
// 提交失败（弱网、超时、业务校验不过）后重试沿用同一个，只有提交成功才换新的。
// 同一个 clientRequestId 重复提交时服务端返回已有记录（success=true），所以弱网下「请求其实已落库、响应丢了」的重试不会重复记账。

export interface ClientRequestIdHolder {
  /** 取当前请求号；首次调用时生成，之后一直返回同一个，直到 rotate。 */
  current(): string;
  /** 提交成功后换新号；下一次 current() 会生成新的。返回被换掉的旧号，便于测试与日志。 */
  rotate(): string | null;
}

export function createClientRequestIdHolder(generate: () => string): ClientRequestIdHolder {
  let value: string | null = null;
  return {
    current() {
      if (value == null) value = generate();
      return value;
    },
    rotate() {
      const previous = value;
      value = null;
      return previous;
    },
  };
}
