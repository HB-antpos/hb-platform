import { AndroidOutlined, AppleOutlined } from '@ant-design/icons'
import { Button, QRCode } from 'antd'
import { useEffect, useMemo, useState } from 'react'

import { fetchIosAppStoreUrl } from '../../services/appDownloadService'

import { buildAndroidDownloadUrl, detectAppPlatform, type AppPlatform } from './appDownloadLogic'

import './appDownload.css'

type IosLinkState =
  | { status: 'loading' }
  | { status: 'ready'; url: string }
  | { status: 'error' }

const PAGE_TITLE = 'Get the HB staff app'

/**
 * 员工 App 下载短链（免登录、英文）。短信里永远只放这一个链接，指向最新版：
 * iPhone / iPad 自动跳 App Store，Android 自动下载最新 APK，电脑显示两个二维码让员工用手机扫。
 * 自动跳转的同时页面上始终留一个手动按钮，跳转被拦截或失败时还能点。
 */
export default function AppDownloadPage() {
  const platform = useMemo<AppPlatform>(() => detectAppPlatform(window.navigator), [])
  const androidUrl = useMemo(
    () => buildAndroidDownloadUrl(import.meta.env.VITE_API_BASE_URL, window.location.origin),
    [],
  )
  const [ios, setIos] = useState<IosLinkState>({ status: 'loading' })
  // 重试只重新请求 App Store 链接，不整页刷新。
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    // 公开页不该被搜索引擎收录。
    const robots = document.createElement('meta')
    robots.name = 'robots'
    robots.content = 'noindex,nofollow'
    document.head.append(robots)
    const previousTitle = document.title
    document.title = `${PAGE_TITLE} | Hot Bargain`
    return () => {
      robots.remove()
      document.title = previousTitle
    }
  }, [])

  // Android 不需要 App Store 链接；iPhone / iPad 要跳转，电脑要给 iPhone 二维码。
  useEffect(() => {
    if (platform === 'android') return undefined
    const controller = new AbortController()
    setIos({ status: 'loading' })
    fetchIosAppStoreUrl(controller.signal)
      .then((url) => {
        if (!controller.signal.aborted) setIos({ status: 'ready', url })
      })
      .catch(() => {
        if (!controller.signal.aborted) setIos({ status: 'error' })
      })
    return () => controller.abort()
  }, [platform, attempt])

  // 自动分流：安卓直接下载，iPhone / iPad 拿到商店链接后跳转。replace 不留历史记录，返回键不会回到跳转页。
  useEffect(() => {
    if (platform === 'android') {
      window.location.replace(androidUrl)
    } else if (platform === 'ios' && ios.status === 'ready') {
      window.location.replace(ios.url)
    }
  }, [platform, androidUrl, ios])

  const retry = () => setAttempt((value) => value + 1)

  if (platform === 'desktop') {
    return (
      <main className="app-download-page">
        <section className="app-download-card">
          <h1 className="app-download-title">{PAGE_TITLE}</h1>
          <p className="app-download-sub">Scan with your phone to install the latest version.</p>
          <div className="app-download-qrs">
            <div className="app-download-qr">
              {ios.status === 'ready' ? (
                <QRCode value={ios.url} size={148} bordered={false} errorLevel="M" />
              ) : ios.status === 'loading' ? (
                <QRCode value="-" size={148} bordered={false} status="loading" />
              ) : (
                <div className="app-download-qr-error">
                  <span>Could not load the App Store link.</span>
                  <Button size="small" onClick={retry}>Try again</Button>
                </div>
              )}
              iPhone
              <small>App Store</small>
            </div>
            <div className="app-download-qr">
              <QRCode value={androidUrl} size={148} bordered={false} errorLevel="M" />
              Android
              <small>Latest APK</small>
            </div>
          </div>
        </section>
      </main>
    )
  }

  const isIos = platform === 'ios'
  return (
    <main className="app-download-page">
      <section className="app-download-card">
        <h1 className="app-download-title">{PAGE_TITLE}</h1>
        <p className="app-download-sub">
          {isIos
            ? (ios.status === 'error' ? 'We could not open the App Store automatically.' : 'Opening the App Store…')
            : 'Starting the download of the latest version…'}
        </p>
        <div className="app-download-actions">
          {isIos ? (
            ios.status === 'ready' ? (
              <Button type="primary" size="large" icon={<AppleOutlined />} href={ios.url}>
                Open the App Store
              </Button>
            ) : ios.status === 'error' ? (
              <Button type="primary" size="large" icon={<AppleOutlined />} onClick={retry}>
                Try again
              </Button>
            ) : (
              <Button type="primary" size="large" icon={<AppleOutlined />} loading>
                Open the App Store
              </Button>
            )
          ) : (
            <Button type="primary" size="large" icon={<AndroidOutlined />} href={androidUrl}>
              Download for Android
            </Button>
          )}
        </div>
        <p className="app-download-note">
          {isIos
            ? 'If nothing happens, tap the button above.'
            : 'If the download does not start, tap the button above.'}
        </p>
      </section>
    </main>
  )
}
