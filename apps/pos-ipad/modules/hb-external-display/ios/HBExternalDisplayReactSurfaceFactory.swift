import Expo
import UIKit

enum HBExternalDisplayReactSurfaceError: LocalizedError {
  case appDelegateUnavailable
  case rootViewFactoryUnavailable
  case reactHostUnavailable

  var errorDescription: String? {
    switch self {
    case .appDelegateUnavailable:
      return "expo-app-delegate-unavailable"
    case .rootViewFactoryUnavailable:
      return "react-root-view-factory-unavailable"
    case .reactHostUnavailable:
      return "react-host-unavailable"
    }
  }
}

enum HBExternalDisplayReactSurfaceFactory {
  static func makeSurface(
    initialProperties: [AnyHashable: Any]
  ) throws -> UIView {
    precondition(Thread.isMainThread)

    guard
      let appDelegate = UIApplication.shared.delegate as? ExpoAppDelegate
    else {
      throw HBExternalDisplayReactSurfaceError.appDelegateUnavailable
    }
    // 必须拿到 Expo 的工厂子类：它的 view(withModuleName:) 会重新走一遍
    // ReactDelegate handler 链（expo-updates 等的“主 root 启动流程”），
    // release 包里会再次调用 AppController.start() 并触发
    // precondition 闪退（真机接外屏即崩溃的根因）。
    guard
      let rootViewFactory =
        appDelegate.factory?.rootViewFactory as? ExpoReactRootViewFactory
    else {
      throw HBExternalDisplayReactSurfaceError.rootViewFactoryUnavailable
    }
    // 第二 surface 只能挂到主 root 已经启动的 React 实例上；实例还不存在时
    // 不能由客显抢先创建（那样会绕过 expo-updates 选定的 bundle）。
    guard rootViewFactory.value(forKey: "reactHost") != nil else {
      throw HBExternalDisplayReactSurfaceError.reactHostUnavailable
    }

    // superView 直接调用 RCTRootViewFactory 的实现：复用现有 React 实例，
    // 只新建一个 Fabric surface，不触碰主窗口和更新启动流程。
    let surface = rootViewFactory.superView(
      withModuleName: "HBExternalDisplay",
      initialProperties: initialProperties,
      launchOptions: nil
    )
    surface.isUserInteractionEnabled = false
    surface.isOpaque = false
    surface.backgroundColor = .clear
    return surface
  }
}
