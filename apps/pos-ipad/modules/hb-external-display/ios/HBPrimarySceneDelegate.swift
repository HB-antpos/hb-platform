import UIKit

@MainActor
public protocol HBPrimaryWindowAppDelegate: AnyObject {
  var window: UIWindow? { get set }
}

/**
 * Expo AppDelegate 拥有唯一的主 UIWindow 和 React Native root。
 * 该 delegate 只将既有主窗口接管到已连接的 application scene。
 */
@MainActor
public final class HBPrimarySceneDelegate: UIResponder, UIWindowSceneDelegate {
  public var window: UIWindow?

  public func scene(
    _ scene: UIScene,
    willConnectTo session: UISceneSession,
    options connectionOptions: UIScene.ConnectionOptions
  ) {
    guard session.role == .windowApplication else { return }
    guard let windowScene = scene as? UIWindowScene else { return }
    guard
      let appDelegate =
        UIApplication.shared.delegate as? HBPrimaryWindowAppDelegate,
      let appWindow = appDelegate.window
    else {
      NSLog("[HBPrimarySceneDelegate] 主窗口不可用，拒绝创建第二个 root")
      return
    }
    if
      let existingWindowScene = appWindow.windowScene,
      existingWindowScene !== windowScene,
      existingWindowScene.session.role == .windowApplication,
      existingWindowScene.activationState != .unattached
    {
      NSLog("[HBPrimarySceneDelegate] 主窗口仍附着到另一主 Scene，拒绝接管")
      return
    }

    appWindow.windowScene = windowScene
    window = appWindow
    appWindow.makeKeyAndVisible()
    Self.registerExternalDisplayAccessory(on: appWindow.rootViewController)

    forward(urlContexts: connectionOptions.urlContexts)
    for userActivity in connectionOptions.userActivities {
      forward(userActivity: userActivity)
    }
  }

  public func sceneDidDisconnect(_ scene: UIScene) {
    guard window?.windowScene === scene else { return }
    window = nil
  }

  /// 外屏客显登记只做一次：记住已登记的根控制器和登记对象（iOS 27 类型，用 AnyObject 存）。
  private static weak var externalDisplayAccessoryOwner: UIViewController?
  private static var externalDisplayAccessoryRegistration: AnyObject?

  /**
   * iOS 27 SDK 编译的 App，系统不再仅凭 Info.plist 的 ExternalDisplayNonInteractive 场景清单建外屏场景：
   * 必须由界面上的 view controller 调 registerSceneAccessory 登记，否则 SpringBoard 只做整屏镜像
   * （iPadOS 27 M3 真机日志：登记了 non interactive presentation 却始终不向 App 要场景，随即 clone mirror）。
   * iPadOS 26 及以下仍按 Info.plist 自动连接，这里不做任何事。
   */
  private static func registerExternalDisplayAccessory(
    on controller: UIViewController?
  ) {
    #if compiler(>=6.4)
      if #available(iOS 27.0, *) {
        guard let controller else {
          NSLog("[HBPrimarySceneDelegate] 主窗口没有根控制器，无法登记外屏客显")
          return
        }
        // 主场景断开重连时根控制器不变，避免重复登记。
        if externalDisplayAccessoryOwner === controller,
          externalDisplayAccessoryRegistration != nil
        {
          return
        }

        // 名称与 Info.plist 场景清单（plugins/with-hb-external-display.js）一致，同时显式指定代理类。
        let configuration = UISceneConfiguration(
          name: "HB External Customer Display",
          sessionRole: .windowExternalDisplayNonInteractive
        )
        configuration.delegateClass = HBExternalDisplaySceneDelegate.self

        let registration = controller.registerSceneAccessory(
          .externalNonInteractive(sceneConfiguration: configuration)
        )
        registration.isEnabled = true
        externalDisplayAccessoryOwner = controller
        externalDisplayAccessoryRegistration = registration
      }
    #endif
  }

  public func scene(
    _ scene: UIScene,
    openURLContexts URLContexts: Set<UIOpenURLContext>
  ) {
    forward(urlContexts: URLContexts)
  }

  public func scene(
    _ scene: UIScene,
    continue userActivity: NSUserActivity
  ) {
    forward(userActivity: userActivity)
  }

  private func forward(urlContexts: Set<UIOpenURLContext>) {
    for context in urlContexts {
      _ = UIApplication.shared.delegate?.application?(
        UIApplication.shared,
        open: context.url,
        options: [:]
      )
    }
  }

  private func forward(userActivity: NSUserActivity) {
    _ = UIApplication.shared.delegate?.application?(
      UIApplication.shared,
      continue: userActivity,
      restorationHandler: { _ in }
    )
  }
}
