import { useEffect, useRef, useState } from 'react'
import { appUpdatePolicyService } from '../../../services/appUpdatePolicyService'
import { mobileOtaPolicyService } from '../../../services/mobileOtaPolicyService'
import { posHandheldUpdatePolicyService } from '../../../services/posHandheldUpdatePolicyService'
import { getWpfAppReleases } from '../../../services/wpfVersionService'
import {
  RELEASE_LANE_DEFINITIONS,
  summarizeHandheldLane,
  summarizeIpadOtaLane,
  summarizeMobileAndroidNativeLane,
  summarizeMobileOtaLane,
  summarizeNativeLane,
  summarizeWpfLane,
  type ReleaseLaneKey,
  type ReleaseLaneSummary,
} from './releaseCenterLogic'

export type ReleaseLaneState =
  | { state: 'loading' }
  | { state: 'failed' }
  | { state: 'ready'; summary: ReleaseLaneSummary }

export type ReleaseLaneStates = Record<ReleaseLaneKey, ReleaseLaneState>

const HANDHELD_KEYS: ReleaseLaneKey[] = [
  'handheld-ios-native',
  'handheld-android-native',
  'handheld-ios-ota',
  'handheld-android-ota',
]

function initialLaneStates(): ReleaseLaneStates {
  return Object.fromEntries(
    RELEASE_LANE_DEFINITIONS.map((definition) => [definition.key, { state: 'loading' }]),
  ) as ReleaseLaneStates
}

/**
 * 并行读取全部 11 条轨道的当前策略（总览矩阵、导航状态点、轨道策略卡共用一份）。
 * 单条轨道失败只影响自己；再次刷新时保留上一次结果直到新数据到达，避免整页闪烁。
 */
export function useReleaseLaneSummaries(reloadKey: string | number): ReleaseLaneStates {
  const [lanes, setLanes] = useState<ReleaseLaneStates>(initialLaneStates)
  const requestIdRef = useRef(0)

  useEffect(() => {
    const requestId = requestIdRef.current + 1
    requestIdRef.current = requestId
    const controller = new AbortController()
    const { signal } = controller

    // 只回写本次刷新的结果：刷新或卸载后，旧请求晚到也不会覆盖界面。
    const commit = (keys: ReleaseLaneKey[], task: Promise<ReleaseLaneSummary[]>) => {
      task.then(
        (summaries) => {
          if (requestIdRef.current !== requestId) {
            return
          }
          setLanes((current) => {
            const next = { ...current }
            for (const key of keys) {
              const summary = summaries.find((item) => item.key === key)
              next[key] = summary ? { state: 'ready', summary } : { state: 'failed' }
            }
            return next
          })
        },
        (error: unknown) => {
          if (requestIdRef.current !== requestId) {
            return
          }
          console.error('Failed to load release lanes', keys, error)
          setLanes((current) => {
            const next = { ...current }
            for (const key of keys) {
              next[key] = { state: 'failed' }
            }
            return next
          })
        },
      )
    }

    commit(
      ['mobile-ios-native'],
      Promise.all([
        appUpdatePolicyService.getMobileIosNativePolicy(signal),
        appUpdatePolicyService.getIosAppStoreReleases('mobile-ios', signal),
      ]).then(([policy, releases]) => [summarizeNativeLane('mobile-ios-native', policy, releases)]),
    )
    commit(
      ['mobile-android-native'],
      appUpdatePolicyService.getMobileAndroidNativePolicy(signal)
        .then((policy) => [summarizeMobileAndroidNativeLane(policy)]),
    )
    commit(
      ['mobile-android-ota'],
      mobileOtaPolicyService.getPolicy('production', 'android', signal)
        .then((policy) => [summarizeMobileOtaLane('mobile-android-ota', policy)]),
    )
    commit(
      ['mobile-ios-ota'],
      mobileOtaPolicyService.getPolicy('production', 'ios', signal)
        .then((policy) => [summarizeMobileOtaLane('mobile-ios-ota', policy)]),
    )
    commit(
      ['ipad-ios-native'],
      Promise.all([
        appUpdatePolicyService.getPosIpadNativePolicy(signal),
        appUpdatePolicyService.getIosAppStoreReleases('pos-ipad', signal),
      ]).then(([policy, releases]) => [summarizeNativeLane('ipad-ios-native', policy, releases)]),
    )
    commit(
      ['ipad-ios-ota'],
      appUpdatePolicyService.getPosIpadOtaRollout(signal)
        .then((rollout) => [summarizeIpadOtaLane(rollout)]),
    )
    commit(
      HANDHELD_KEYS,
      posHandheldUpdatePolicyService.getPolicies(signal)
        .then((policies) => policies.map(summarizeHandheldLane)),
    )
    commit(
      ['wpf-windows'],
      getWpfAppReleases({ channel: 'production', page: 1, pageSize: 50 })
        .then((result) => [summarizeWpfLane(result.items)]),
    )

    return () => {
      controller.abort()
      requestIdRef.current += 1
    }
  }, [reloadKey])

  return lanes
}
