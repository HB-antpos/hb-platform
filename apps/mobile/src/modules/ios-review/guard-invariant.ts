// 审核离线守卫（iosReviewOfflineGuardActive）的一致性不变量。纯函数，不依赖 React Native。
//
// 守卫的含义是「认证完成之前，审核构建先 fail-closed」，只应该在【未认证】阶段为真。
// 登录成功会把它清掉，但退出 → 换账号登录的过程里有若干迟到的重新布防动作
// （清会话、恢复会话、设备识别等），可能在登录成功之后又把它置回真。
// 守卫一旦粘在真上：人脸比对、通知、推送令牌注册、未读数轮询等所有按它判断的功能都会
// 误以为在审核模式（提示「审核模式不提供此功能」），直到重启 App 才恢复。
//
// 这里给出判定：已认证的真实会话（非审核会话）出现守卫为真，就是不一致状态，应当释放。
// 审核会话（sessionKind=iosReview 或审核 marker 仍在）与未认证阶段一律不动，保持 fail-closed。

export interface IosReviewGuardInvariantState {
  isAuthenticated: boolean;
  sessionKind: string;
  iosReviewOfflineGuardActive: boolean;
}

export function shouldReleaseIosReviewGuard(
  state: IosReviewGuardInvariantState,
  reviewSessionMarkerActive: boolean,
): boolean {
  return (
    state.iosReviewOfflineGuardActive &&
    state.isAuthenticated &&
    state.sessionKind !== "iosReview" &&
    !reviewSessionMarkerActive
  );
}
