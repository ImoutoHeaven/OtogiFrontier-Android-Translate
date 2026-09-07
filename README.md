# Otogi Frontier Android Patcher

将 DMM 版《オトギフロンティア》单体 ARM64 APK 构建为可与官方版共存的
LemonLoader 安装包。目标包名为 `jp.co.dmm.dmmgames.kms.prototype`；编译、修补、
签名和产物校验均在 Docker 中完成。

## 功能

- 使用 [`alex343425/otogitranslate`](https://github.com/alex343425/otogitranslate)
  的简体中文词典转换角色、成人、世界和支线剧情响应。
- 通过 OpenAI Chat Completions 兼容接口翻译运行时日文 UI 文本。
- 将 Unity 目标帧率固定为 60 FPS。
- 将 Spine `SkeletonMosaic` 材质的 `_BlockSize` 设为 `0.001`。
- 将游戏字体请求重定向到 Noto Sans CJK SC，并随 APK 预置同一字体。
- `OtogiTranslate.dll` 同时提供角色剧情、成人与场景 JSON 解锁：HTTP 400/404
  时替换 `UserData/OtogiCgUnlock` 缓存，缺文件时从 GitHub raw 下载。

剧情与 UI 翻译处理游戏已经返回的文本。同一 DLL 在
`/api/MAdults/MonsterMAdults/{id}`、`/api/MScenes/{id}`、`/api/episode/monsters/{id}`、
`/api/episode/spirits/{id}` 的 400/404 上使用本地缓存，并把 `characters.json` 中的角色
id 合并进 `/api/Episode/CharacterStory`。静止画、语音和 BGM 从官方 CDN 加载。好感度与
账号权限由游戏服务端处理。

## 环境

- Windows 10/11
- PowerShell 7（`pwsh`）
- 可用的 Docker daemon
- 安装检查所需的 `adb`
- 支持 ARM64 native bridge 的 Android 模拟器
- 包名为 `jp.co.dmm.dmmgames.kms`、签名匹配项目内固定摘要且包含
  `lib/arm64-v8a/libil2cpp.so` 的 DMM 单体 APK

默认 ADB serial 为 `127.0.0.1:5555`。`-Serial` 可选择其他设备。

## 构建、安装和检查

在模拟器的 DMM GAMES STORE 中更新官方版。首次构建运行：

```powershell
pwsh -NoProfile -NonInteractive -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe" `
  -CreateKey
```

后续更新复用 `keys/otogi-dev.keystore`：

```powershell
pwsh -NoProfile -NonInteractive -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe"
```

`release.ps1` 从设备提取官方 APK，调用 Docker 构建并签名安装包，覆盖安装 prototype
包，刷新插件和字体，再执行运行时 smoke test。成功以
`PASS source verification, build, install, and runtime smoke test` 结束。

已有官方 APK 时可直接指定输入：

```powershell
pwsh -NoProfile -NonInteractive -File ./release.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe" `
  -InputApk "D:\Downloads\Original.apk"
```

只生成产物时运行：

```powershell
pwsh -NoProfile -NonInteractive -File ./run.ps1 `
  -InputApk "D:\Downloads\Original.apk" `
  -CreateKey
```

`run.ps1` 使用临时 `docker run --rm` 容器。输入和 payload 以只读方式挂载，输出仅写入
`out/`，签名密钥仅写入 `keys/`。

### 产物

```text
out/Original.prototype-signed.apk
out/Original.prototype-signed.apk.sha256
out/Original.prototype-signed.apk.build-info.txt
out/OtogiTranslate.dll
```

同一 prototype 包使用同一密钥才能覆盖安装。请备份
`keys/otogi-dev.keystore`。`input/`、`keys/`、`out/`、`e2e/`、APK、日志和运行时缓存均由
`.gitignore` 排除。

## 剧情词典

响应与词典的对应关系为：

```text
api/MScenes/{MSceneId}                    -> MScenes/{MSceneId}_gb.json
api/MAdults/MonsterMAdults/{MAdultId}     -> MAdults/{mapped MSceneId}_gb.json
api/Episode/MStory/{MStoryId}             -> Mstory/{MStoryId}_gb.json
```

设备词典位于：

```text
/sdcard/Android/data/<package>/files/UserData/OtogiTranslate/<type>/<id>_gb.json
```

本地有效词典优先。插件从角色、世界和支线列表预取缺少的词典，并通过 IL2CPP
`UnityWebRequest` 写入设备目录。传输错误、HTTP 408、429 和 5xx 最多尝试三次；HTTP
404 在当前进程内标记为不可用。无效本地文件成功移动为 `.invalid` 后进入远端下载队列。
词典仍在下载、下载失败或响应处理失败时，原始游戏响应直接进入后续流程。

成人 API 使用 `MAdultId`，翻译仓库使用普通剧情的 `MSceneId`。插件从角色剧情列表建立
`MAdultId -> MSceneId` 映射；映射就绪后加载成人词典，映射待建立时保留原始成人响应。

## 角色剧情解锁

解锁逻辑编进 `OtogiTranslate.dll`，随 Lemon `copyToData/Plugins` 一起下发。
设备缓存：

```text
/sdcard/Android/data/<package>/files/UserData/OtogiCgUnlock/adults/{id}.json
/sdcard/Android/data/<package>/files/UserData/OtogiCgUnlock/scenes/{id}.json
/sdcard/Android/data/<package>/files/UserData/OtogiCgUnlock/episodes/{id}.json
/sdcard/Android/data/<package>/files/UserData/OtogiCgUnlock/characters.json
```

首次启动在游戏目录创建：

```text
/sdcard/Android/data/<package>/files/OtogiCgUnlock.cfg
```

```ini
[Remote]
Root = https://raw.githubusercontent.com/ImoutoHeaven/otogi-scenes/refs/heads/main
```

`Root` 为空时只读本地缓存。缺文件时 GET `{Root}/adults/{id}.json`、`{Root}/scenes/{id}.json`、
`{Root}/episodes/{id}.json` 或 `{Root}/characters.json`。传输使用 IL2CPP `UnityWebRequest`：
同一时间一个请求、30 秒超时、HTTP 404 在当前进程内记住、408/429/5xx 最多三次。启动时若
缺少 `characters.json` 则加入下载队列；剧情列表中的非 0 `MAdultId` / `MSceneId` 会预取对应
JSON。

## LLM UI 翻译

首次启动在设备创建：

```text
/sdcard/Android/data/<package>/files/OtogiTranslate.cfg
```

生成的配置为：

```ini
[LLM]
Enable = false
Endpoint = http://10.0.2.2:11434/v1/chat/completions
Model = qwen2.5:7b
ApiKey =
TimeoutSeconds = 30
RetryCount = 2
RequestsPerSecond = 2
MaxQueue = 128

[UI]
ScanIntervalSeconds = 0.5
LogSeenText = false
```

启用 LLM 时设置 `Enable = true`，填写 `Endpoint`、`Model` 和接口所需的 `ApiKey`，再重启
游戏。远程接口使用 HTTPS；loopback 与 Android Emulator 宿主地址 `10.0.2.2` 也可使用 HTTP。`ApiKey` 以明文保存在应用专属目录中，提交与共享范围应排除
该配置文件。

扫描器处理 `TMPro.TMP_Text` 和 `UnityEngine.UI.Text` 中长度为 1–1000 个字符且包含假名的
文本。缓存位于同目录的 `OtogiTranslate.cache.jsonl`，最多加载 10000 项。有效响应保留
富文本标签、转义序列、占位符和换行；其他响应使当前文本在本次进程内继续显示原文。
`RetryCount` 表示首次请求之外的重试次数，范围为 0–5。408、429、5xx 和传输错误使用
退避；其他 4xx 暂停当前进程的 LLM 队列。图片、Sprite 和 Texture 中的文字沿用资源
内容。

## 验证

### Smoke test

```powershell
pwsh -NoProfile -NonInteractive -File ./test-loader.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe"
```

该测试安装 `out/` 中的 APK，核对设备上 `OtogiTranslate.dll` 和字体哈希，并要求
LemonLoader assembly、剧情响应、60 FPS、马赛克、字体、词典传输、运行时驱动 hook 以及
`[OtogiCgUnlock] installed` 完成初始化。登录状态、LLM 配置、翻译缓存和官方包数据保持原状。

### 角色剧情远程 E2E

```powershell
python ./e2e_characters.py
```

冷启动清空 `UserData/OtogiCgUnlock`，从 GitHub raw 拉取 `characters.json`，进入角色剧情页后
`characters-merged monsters>=975 spirits>=149`，锁定条目出现 `originalStatus=400` 或 `404` 替换。
截图写入 `out/e2e/`。前置：1920×1080 横屏、已登录测试账号、设备能访问 GitHub raw。

### 真实游戏 E2E

E2E 使用以下固定前置条件：

- 1920×1080 横屏模拟器
- 已登录并已解锁灰姑娘 Flow 1 的测试账号
- 设备 `[LLM]` 配置启用，且端点、模型和有效 `ApiKey` 与命令参数一致
- 调用进程通过 `OTOGI_LLM_ENDPOINT` 和 `OTOGI_LLM_MODEL` 提供私有测试参数
- 当前游戏首页和角色剧情页面布局

```powershell
pwsh -NoProfile -NonInteractive -File ./test-e2e.ps1 `
  -AdbPath "C:\Android\platform-tools\adb.exe" `
  -ExpectedEndpoint $env:OTOGI_LLM_ENDPOINT `
  -ExpectedModel $env:OTOGI_LLM_MODEL
```

测试从冷启动进入剧情页面，远端获取 `MScenes/10001` 和 `MAdults/10001`，再将成人词典
应用到 API 场景 `210011`。通过条件包括：

- LLM 请求完成并由实际 UI setter 应用
- 普通剧情至少替换 100 项
- 成人剧情至少替换 60 项
- Spine 马赛克材质补丁实际命中
- 设备字体哈希匹配 payload
- SurfaceFlinger 游戏图层为 60 Hz/60 FPS，10 秒采样至少 500 帧且平均帧率至少 58

截图、Loader 日志、配置校验结果和帧统计写入 `e2e/<timestamp>/`；端点、模型和凭据值保持
在证据文件之外。测试完整备份词典目录和 LLM 缓存，并在结束阶段恢复；成功以
`PASS real game E2E` 结束。

## 兼容边界

运行验证覆盖 `OtogiTranslate` 的 native IL2CPP hooks。LemonLoader 0.5.7 在该游戏中加载
`UnityEngine.CoreModule` support module 时会记录 `TypeLoadException`；传统 managed Mods
仍处于待验证范围。Prototype manifest 包含 `android:debuggable=true` 和
`MANAGE_EXTERNAL_STORAGE`，适用环境为专用测试设备。

## 组件、数据与许可

构建使用固定版本的：

- [`LemonLoader/MelonLoader_057`](https://github.com/LemonLoader/MelonLoader_057)
- [`Apktool`](https://github.com/iBotPeaches/Apktool)
- [`MelonLoader.UnityDependencies`](https://github.com/LavaGang/MelonLoader.UnityDependencies)

Loader payload 面向 ARM64 Android，x86_64 宿主通过模拟器 native bridge 执行。输出包使用
本地开发密钥，适合个人测试。官方 APK、DMM 账号数据、签名密钥、LLM 凭据和完整翻译
资源保存在各自的本地或上游位置。公开分发内容应限于有权发布的源码和资源。

字体 payload 为 Noto Sans CJK SC，许可见 [`LICENSES/OFL-1.1.txt`](LICENSES/OFL-1.1.txt)。
游戏、商标和原始资源归各权利人所有。本仓库除第三方组件许可外未声明项目级开源许可。
