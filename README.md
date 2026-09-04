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
pwsh -NoProfile -NonInteractive -File ./release.ps1 `
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
pwsh -NoProfile -NonInteractive -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe"
```

如果已经有官方 APK，可跳过设备提取：

```powershell
pwsh -NoProfile -NonInteractive -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe" `
  -InputApk "D:\Downloads\Original.apk"
```

默认 ADB serial 是 `127.0.0.1:5555`；需要时使用 `-Serial` 修改。

## 只构建

不安装到设备：

```powershell
pwsh -NoProfile -NonInteractive -File ./run.ps1 `
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
api/MAdults/MonsterMAdults/{MAdultId} -> MAdults/{mapped MSceneId}_gb.json
api/MScenes/{id}                       -> MScenes/{id}_gb.json
api/Episode/MStory/{id}                -> Mstory/{id}_gb.json
```

本地词典路径：

```text
/sdcard/Android/data/<package>/files/UserData/OtogiTranslate/<type>/<id>_gb.json
```

插件优先读取本地词典。角色、世界和支线列表会通过 IL2CPP
`UnityWebRequest` 异步预取缺少的词典并写入本地路径；临时网络错误最多尝试三次，
HTTP 404 在本次进程中记为不可用。成人词典沿用翻译仓库的普通剧情 `MSceneId`
文件名，插件使用角色列表中的 `MAdultId` → `MSceneId` 对应关系加载它；映射尚未建立
时保留成人响应原文。无效的本地词典成功隔离为 `.invalid` 后，远端副本进入下载队列；
隔离错误会写入日志并保留原文件。
词典尚未就绪、下载失败或响应处理异常时，游戏继续使用原始响应。

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
序列、实际换行和占位符保持一致时接受响应；其余文本保留原文并进入本次进程的跳过
集合。`RetryCount` 表示首次请求之外的重试次数，可设为 `0` 到 `5`；408、429、5xx
和传输错误使用退避，其他 4xx 会暂停本次进程的 LLM 请求。图片、Sprite 和 Texture
中烘焙的文字由原始资源提供。

## 运行检查

已有构建产物时，运行启动 smoke test：

```powershell
pwsh -NoProfile -NonInteractive -File ./test-loader.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe"
```

该检查覆盖安装、生产 DLL 中的 probe 排除、运行时文件哈希和所有 hook 的初始化。
它会替换 `.prototype` 包的插件、字体和 Loader 日志，并保留登录状态、LLM 配置、
翻译缓存和官方应用数据。

真实游戏 E2E 使用 1920×1080 横屏、已登录且已解锁首个灰姑娘剧情的测试账号。
设备 `[LLM]` 配置需启用，并提供与测试参数一致的端点、模型和有效 `ApiKey`。调用进程
通过私有环境变量提供端点和模型：

```powershell
pwsh -NoProfile -NonInteractive -File ./test-e2e.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe" `
  -ExpectedEndpoint $env:OTOGI_LLM_ENDPOINT `
  -ExpectedModel $env:OTOGI_LLM_MODEL
```

E2E 从冷启动进入角色剧情，自远端预取 `MScenes/10001` 和 `MAdults/10001`，再将
后者应用于 API 成人场景 `210011`。测试要求普通场景至少替换 100 项、成人场景至少
替换 60 项，并验证 LLM 请求及实际 UI setter、Spine 材质补丁、字体文件哈希以及
SurfaceFlinger 游戏图层的 60 Hz/60 FPS 状态和至少 58 FPS 的采样均值。截图、Loader
日志、配置校验结果和帧统计写入已忽略的 `e2e/<timestamp>/`；端点、模型和凭据值保持
在证据文件之外。测试结束时恢复完整的原有词典目录和 LLM 缓存。

场景 gate 诊断源由 `SCENE_GATE_PROBE` 编译符号隔离；诊断构建显式启用该符号，
标准 Docker 构建生成仅含生产功能的 DLL。smoke test 同时拒绝包含 probe 方法名的
生产 DLL。

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
