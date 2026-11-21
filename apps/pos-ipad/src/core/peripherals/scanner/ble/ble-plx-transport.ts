/**
 * react-native-ble-plx → BleScannerTransport 适配层。
 *
 * 与 ble-scanner-core.ts / ble-scanner-controller.ts 一样，本文件在 iPad POS 与移动端
 * 各有一份逐字相同的拷贝，由 apps/mobile/src/modules/scanner/ble/ble-shared-sync.test.ts 校验。
 *
 * 旧原生包（OTA 推到没有 ble-plx 的包）里 `NativeModules.BlePlx` 不存在，
 * 必须先用 isBleScannerNativeAvailable() 探测，探测不到就隐藏扫码枪入口。
 * 注意：ble-plx 3.x 原生模块注册名是 `BlePlx`（iOS RCT_EXPORT_MODULE() / Android
 * BlePlxModule.NAME）；`BleClientManager` 只是它内部的 Swift 类名，不是 JS 能看到的模块。
 */
import { NativeModules, PermissionsAndroid, Platform } from "react-native";
import {
  BleManager,
  type Characteristic,
  type Device,
  State,
  type Subscription,
} from "react-native-ble-plx";

import type {
  BleAdapterState,
  BleCharacteristicInfo,
  BleDiscoveredDevice,
  BleScannerTransport,
} from "./ble-scanner-controller";
import { decodeBase64, normalizeBleUuid } from "./ble-scanner-core";

export function isBleScannerNativeAvailable(): boolean {
  return Boolean((NativeModules as Record<string, unknown>).BlePlx);
}

let sharedManager: BleManager | null = null;

/** 全 App 只建一个 BleManager（ble-plx 的要求）。 */
function manager(): BleManager {
  if (!sharedManager) sharedManager = new BleManager();
  return sharedManager;
}

function mapState(state: State): BleAdapterState {
  switch (state) {
    case State.PoweredOn:
      return "on";
    case State.PoweredOff:
    case State.Resetting:
      return "off";
    case State.Unauthorized:
      return "unauthorized";
    case State.Unsupported:
      return "unsupported";
    default:
      return "unknown";
  }
}

/** Android 12+ 需要运行时申请 BLUETOOTH_SCAN / BLUETOOTH_CONNECT；更早的版本扫描需要定位权限。 */
async function ensureAndroidPermissions(): Promise<boolean> {
  if (Platform.OS !== "android") return true;
  const apiLevel = typeof Platform.Version === "number" ? Platform.Version : Number(Platform.Version);
  const permissions =
    apiLevel >= 31
      ? [
          PermissionsAndroid.PERMISSIONS.BLUETOOTH_SCAN,
          PermissionsAndroid.PERMISSIONS.BLUETOOTH_CONNECT,
        ]
      : [PermissionsAndroid.PERMISSIONS.ACCESS_FINE_LOCATION];
  const result = await PermissionsAndroid.requestMultiple(permissions);
  return permissions.every((permission) => result[permission] === PermissionsAndroid.RESULTS.GRANTED);
}

function toDiscovered(device: Device): BleDiscoveredDevice {
  return Object.freeze({
    id: device.id,
    name: device.localName ?? device.name ?? null,
    rssi: typeof device.rssi === "number" ? device.rssi : null,
    serviceUuids: Object.freeze((device.serviceUUIDs ?? []).map(normalizeBleUuid)),
  });
}

function toCharacteristic(characteristic: Characteristic): BleCharacteristicInfo {
  return Object.freeze({
    serviceUuid: normalizeBleUuid(characteristic.serviceUUID),
    uuid: normalizeBleUuid(characteristic.uuid),
    notifiable: characteristic.isNotifiable || characteristic.isIndicatable,
    readable: characteristic.isReadable,
  });
}

function removeSafely(subscription: Subscription | null | undefined): void {
  try {
    subscription?.remove();
  } catch {
    // 原生侧已经释放时 remove 可能抛错，忽略。
  }
}

export function createBlePlxTransport(): BleScannerTransport {
  return {
    async getState() {
      if (!isBleScannerNativeAvailable()) return "unsupported";
      return mapState(await manager().state());
    },

    onStateChange(listener) {
      if (!isBleScannerNativeAvailable()) return () => undefined;
      const subscription = manager().onStateChange((state) => listener(mapState(state)), false);
      return () => removeSafely(subscription);
    },

    startScan(onDevice, onError) {
      let stopped = false;
      void ensureAndroidPermissions().then(
        (granted) => {
          if (stopped) return;
          if (!granted) {
            onError(Object.assign(new Error("Bluetooth permission denied"), { code: "permission" }));
            return;
          }
          void manager().startDeviceScan(null, { allowDuplicates: false }, (error, device) => {
            if (stopped) return;
            if (error) {
              onError(error);
              return;
            }
            if (device) onDevice(toDiscovered(device));
          });
        },
        (error: unknown) => onError(error instanceof Error ? error : new Error(String(error))),
      );
      return () => {
        stopped = true;
        void Promise.resolve(manager().stopDeviceScan()).catch(() => undefined);
      };
    },

    async connect(id) {
      if (!(await ensureAndroidPermissions())) {
        throw Object.assign(new Error("Bluetooth permission denied"), { code: "permission" });
      }
      // 不传 timeout：iOS 的 connect 本身不超时；Android autoConnect=true 由系统在设备
      // 重新广播时接上。扫码枪休眠期间请求一直挂着，按扳机唤醒约 1–2 秒连回。
      await manager().connectToDevice(id, { autoConnect: Platform.OS === "android" });
    },

    async cancelConnection(id) {
      try {
        await manager().cancelDeviceConnection(id);
      } catch {
        // 本来就没连上时会报错，忽略。
      }
    },

    onDisconnected(id, listener) {
      const subscription = manager().onDeviceDisconnected(id, (error) => {
        listener(error ?? null);
      });
      return () => removeSafely(subscription);
    },

    async discover(id) {
      const ble = manager();
      await ble.discoverAllServicesAndCharacteristicsForDevice(id);
      const services = await ble.servicesForDevice(id);
      const result: BleCharacteristicInfo[] = [];
      for (const service of services) {
        const characteristics = await ble.characteristicsForDevice(id, service.uuid);
        result.push(...characteristics.map(toCharacteristic));
      }
      return Object.freeze(result);
    },

    monitor(id, serviceUuid, characteristicUuid, onValue, onError) {
      const subscription = manager().monitorCharacteristicForDevice(
        id,
        serviceUuid,
        characteristicUuid,
        (error, characteristic) => {
          if (error) {
            onError(error);
            return;
          }
          if (characteristic?.value) onValue(decodeBase64(characteristic.value));
        },
      );
      return () => removeSafely(subscription);
    },

    async read(id, serviceUuid, characteristicUuid) {
      const characteristic = await manager().readCharacteristicForDevice(
        id,
        serviceUuid,
        characteristicUuid,
      );
      return characteristic.value ? decodeBase64(characteristic.value) : new Uint8Array();
    },

    async readRssi(id) {
      const device = await manager().readRSSIForDevice(id);
      return typeof device.rssi === "number" ? device.rssi : null;
    },
  };
}
