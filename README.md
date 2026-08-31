# Otogi Frontier Android Patcher

将 DMM 版《オトギフロンティア》官方 APK 构建为可与官方版共存的
LemonLoader 版本。构建、签名和校验全部在 Docker 中完成；官方 APK、账号数据、
签名密钥和 LLM 凭据不进入仓库。

默认输出包名：`jp.co.dmm.dmmgames.kms.prototype`。

## 功能

- 从 [`alex343425/otogitranslate`](https://github.com/alex343425/otogitranslate)
  加载角色剧情翻译，也支持设备上的本地词典。
- 将游戏目标帧率固定为 60 FPS。
- 移除 Spine `SkeletonMosaic` 材质动态添加的马赛克。
- 使用统一的简体中文字体，避免同一句文字出现不同字形。
- 可选扫描 `TMPro.TMP_Text` 和 `UnityEngine.UI.Text`，通过兼容
  OpenAI Chat Completions 的接口翻译日文 UI。

## 要求

- Windows 10/11 与 PowerShell 7（`pwsh`）。
- 可用的 Docker daemon。
- DMM GAMES STORE 安装或提供的官方 DMM 版 APK。
- 运行安装检查时需要 `adb` 和一台支持 ARM64 native bridge 的 Android 模拟器。

工作流只接受包名 `jp.co.dmm.dmmgames.kms`、受信任签名且包含
`lib/arm64-v8a/libil2cpp.so` 的单体 APK。Split APK 和 Google Play 版不受支持。

## 一键更新

先在模拟器的 DMM GAMES STORE 中更新官方版，再运行：

```powershell
pwsh -NoProfile -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe" `
  -CreateKey
```

`release.ps1` 会：

1. 从已连接设备提取官方 APK；
2. 校验包名、官方签名和 APK 完整性；
3. 构建并签名 `.prototype` 安装包；
4. 覆盖安装到模拟器；
5. 刷新插件和字体文件；
6. 检查 LemonLoader、翻译钩子、60 FPS、去马赛克和字体钩子。

`-CreateKey` 只用于第一次构建。之后运行同一命令但去掉该参数：

```powershell
pwsh -NoProfile -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe"
```

如果已经有官方 APK，可跳过设备提取：

```powershell
pwsh -NoProfile -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe" `
  -InputApk "D:\Downloads\Original.apk"
```

默认 ADB serial 是 `127.0.0.1:5555`；需要时使用 `-Serial` 修改。

## 只构建

不安装到设备：

```powershell
pwsh -NoProfile -File ./run.ps1 `
  -InputApk "D:\Downloads\Original.apk" `
  -CreateKey
```

产物位于 `out/`：

- `Original.prototype-signed.apk`
- `Original.prototype-signed.apk.sha256`
- `Original.prototype-signed.apk.build-info.txt`
- `OtogiTranslate.dll`

签名密钥位于 `keys/otogi-dev.keystore`。请备份它；同一包名只有使用同一密钥签名
才能覆盖安装。源 APK、输出 APK、密钥、日志、缓存和测试证据均已被 Git 忽略。

## 剧情翻译

插件处理以下响应：

```text
api/MAdults/MonsterMAdults/{id} -> MAdults/{id}_gb.json
api/MScenes/{id}                -> MScenes/{id}_gb.json
api/Episode/MStory/{id}         -> Mstory/{id}_gb.json
```

本地词典路径：

```text
/sdcard/Android/data/<package>/files/UserData/OtogiTranslate/<type>/<id>_gb.json
```

插件在本地词典不存在时尝试从 `otogitranslate` 下载对应文件。LemonLoader 0.5.7
的 Mono 网络栈在部分模拟器上不稳定，因此本地词典是可靠路径。下载失败、JSON
无效或钩子异常时保留游戏原始响应。

## LLM UI 翻译

首次启动会创建：

```text
/sdcard/Android/data/<package>/files/OtogiTranslate.cfg
```

默认关闭。编辑后重启游戏：

```ini
[LLM]
Enable = true
Endpoint = https://example.test/v1/chat/completions
Model = example-model
ApiKey =
TimeoutSeconds = 30
RetryCount = 2
RequestsPerSecond = 2
MaxQueue = 128

[UI]
ScanIntervalSeconds = 0.5
LogSeenText = false
```

接口必须使用 HTTPS；仅 loopback 和 Android Emulator 的 `10.0.2.2` 允许 HTTP。
`ApiKey` 以明文保存在设备的包专属目录中，不会写入插件日志，请勿提交或分享该
配置文件。

扫描器只把含平假名或片假名的文本加入队列。纯汉字保持不变，避免已经翻译的中文
再次入队。翻译缓存写入同目录下的 `OtogiTranslate.cache.jsonl`。富文本标签、转义
序列和占位符不一致的响应会被拒绝。图片、Sprite 和 Texture 中烘焙的文字无法由
组件扫描器翻译。

## 运行检查

已有构建产物时可以单独运行：

```powershell
pwsh -NoProfile -File ./test-loader.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe"
```

检查会替换 `.prototype` 包的插件、字体和 Loader 日志，但保留登录状态、LLM 配置、
翻译缓存和官方应用数据。实际剧情显示仍应人工确认一次。

## 兼容性与分发

- Loader 固定为 LemonLoader/MelonLoader 0.5.7 ARM64 emulator 版本。
- 输出 APK 使用本地开发密钥，适合个人安装，不代表官方发行包。
- 仓库不包含官方 APK、DMM 凭据或完整翻译资源。
- 不要公开分发官方 APK、构建后的 APK、签名密钥或含 API key 的配置。
- 游戏、商标和原始资源归其权利人所有；使用者须遵守 DMM 及相关组件条款。

构建会下载固定版本的
[`LemonLoader/MelonLoader_057`](https://github.com/LemonLoader/MelonLoader_057)、
[`Apktool`](https://github.com/iBotPeaches/Apktool) 和
[`MelonLoader.UnityDependencies`](https://github.com/LavaGang/MelonLoader.UnityDependencies)。
字体 payload 使用 Noto Sans CJK SC，按
[`LICENSES/OFL-1.1.txt`](LICENSES/OFL-1.1.txt) 提供。除各第三方组件自己的许可外，
本仓库未附带项目级开源许可证。
