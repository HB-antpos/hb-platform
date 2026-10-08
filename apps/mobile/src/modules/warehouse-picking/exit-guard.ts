/**
 * 「暂存并退出」前等已发出的写入都收尾：每次扫码 / 改数量都是即时写服务器的，退出时队列里可能还有在途请求（含网络重试）。
 * 写入队列每追加一次就会换一个 Promise，所以循环到“队列不再变化”为止；等待期间有写入失败就返回 failed，
 * 失败提示已由写入队列弹出，调用方留在当前页让拣货员重试，避免一条没存上的扫码被悄悄丢掉。
 */
export async function settleWritesBeforeExit(
  getChain: () => Promise<unknown>,
  failureCount: () => number,
): Promise<"ok" | "failed"> {
  const failuresBefore = failureCount();
  let chain = getChain();
  for (;;) {
    await chain.catch(() => undefined);
    const latest = getChain();
    if (latest === chain) break;
    chain = latest;
  }
  return failureCount() > failuresBefore ? "failed" : "ok";
}
