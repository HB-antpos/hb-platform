Pod::Spec.new do |s|
  s.name           = 'HBImageBarcode'
  s.version        = '1.0.0'
  s.summary        = 'HB 本地图片条码识别（iOS Vision）'
  s.description    = '用 Vision 框架识别相册图片中的一维 / 二维条码；expo-camera 的 scanFromURLAsync 在 iOS 上只支持二维码。'
  s.author         = 'HB'
  s.homepage       = 'https://docs.expo.dev/modules/'
  s.platforms      = { :ios => '15.1' }
  s.source         = { git: '' }
  s.static_framework = true

  s.dependency 'ExpoModulesCore'

  s.pod_target_xcconfig = {
    'DEFINES_MODULE' => 'YES',
    'SWIFT_COMPILATION_MODE' => 'wholemodule'
  }

  s.source_files = '**/*.{h,m,mm,swift,hpp,cpp}'
end
