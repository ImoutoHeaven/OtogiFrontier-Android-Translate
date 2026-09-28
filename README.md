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
- 将游戏 `/Assets/font` 请求改写为 `http://otogi-font.invalid/Assets/font`，BestHTTP
  发送钩子读取 APK 预置的 Noto Sans CJK SC AssetBundle 作为响应。固定来源位于
  `files/UserData/OtogiTranslate/font`，与游戏管理的 `files/Assets/font` 缓存分开保存。
- `OtogiTranslate.dll` 同时提供角色剧情、成人与场景 JSON 解锁：HTTP 400/404
  时用 `UserData/OtogiCgUnlock` 缓存替换响应；缺文件时按 `OtogiCgUnlock.cfg` 的
  `Root` 下载（默认 GitHub raw）。

剧情与 UI 翻译替换游戏响应和界面中的日文。同一 DLL 在
`/api/MAdults/MonsterMAdults/{id}`、`/api/MScenes/{id}`、`/api/episode/monsters/{id}`、
`/api/episode/spirits/{id}` 的 400/404 上使用本地缓存，并把 `characters.json` 中的角色
id 合并进 `/api/Episode/CharacterStory`。静止画、语音和 BGM 从官方 CDN 加载。好感度与
账号权限由游戏服务端处理。

## 环境

- Windows 10/11
- PowerShell 7（`pwsh`）
- 可用的 Docker daemon
- `OTOGI_ADB` 或 PATH 中的 `adb`
- `ANDROID_SERIAL`，默认 `127.0.0.1:5555`
- 可选 `OTOGI_INPUT_APK` 指向官方单体 APK
- ARM64 Android 设备，或支持 ARM64 native bridge 的 Android 模拟器
- 包名为 `jp.co.dmm.dmmgames.kms`、签名匹配项目内固定摘要且包含
  `lib/arm64-v8a/libil2cpp.so` 的 DMM 单体 APK

## 构建、安装和检查

APK 的 `Application` 入口在 LemonLoader 加载前，通过 Android 的 `getExternalFilesDir`
准备自身的 `il2cpp` 目录。安装后的初始化使用普通应用权限，独立于 ADB、`su` 和开发脚本；
DMM 登录、网络访问与设备的 ARM64/Loader 相容性仍是运行前置条件。

在模拟器的 DMM GAMES STORE 中更新官方版。首次构建运行：

```powershell
pwsh -NoProfile -NonInteractive -File ./release.ps1 -CreateKey
```

后续更新复用 `keys/otogi-dev.keystore`：

```powershell
pwsh -NoProfile -NonInteractive -File ./release.ps1
```

`release.ps1` 从设备提取官方 APK，调用 Docker 构建并签名安装包，覆盖安装 prototype
包，刷新插件和字体，再执行运行时 smoke test。成功以
`PASS source verification, build, install, and runtime smoke test` 结束。

已有官方 APK 时设置 `OTOGI_INPUT_APK` 后运行：

```powershell
pwsh -NoProfile -NonInteractive -File ./release.ps1
```

只生成产物时设置 `OTOGI_INPUT_APK` 后运行：

```powershell
pwsh -NoProfile -NonInteractive -File ./run.ps1 -CreateKey
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
有效词典命中后替换响应；下载中或失败时沿用游戏原文。

成人 API 使用 `MAdultId`，翻译仓库使用普通剧情的 `MSceneId`。插件从角色剧情列表建立
`MAdultId -> MSceneId` 映射，映射就绪后加载成人词典。

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

有 `Root` 时，缺文件则 GET `{Root}/adults/{id}.json`、`{Root}/scenes/{id}.json`、
`{Root}/episodes/{id}.json` 或 `{Root}/characters.json`；空 `Root` 只读本地缓存。JSON 形状见
[`ImoutoHeaven/otogi-scenes`](https://github.com/ImoutoHeaven/otogi-scenes)。传输使用 IL2CPP
`UnityWebRequest`：同一时间一个请求、30 秒超时、HTTP 404 在当前进程内记住、408/429/5xx
最多三次。启动时若缺少 `characters.json` 则加入下载队列；剧情列表中的非 0 `MAdultId` /
`MSceneId` 会预取对应 JSON。`EpisodeScenarioCell.SetupCell` 将条目标为可观看；本地已有对应
成人 JSON 时，`CharacterStoryScene` 按成人演出进入。

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
SceneTranslation = true

[UI]
ScanIntervalSeconds = 0.5
LogSeenText = false
```

启用 LLM 时设置 `Enable = true`，填写 `Endpoint`、`Model` 和接口所需的 `ApiKey`，再重启
游戏。允许的 LLM 端点为 HTTPS，以及 loopback 与 Android Emulator 宿主地址 `10.0.2.2` 的
HTTP。`ApiKey` 以明文保存在应用专属目录。

扫描器处理 `TMPro.TMP_Text` 和 `UnityEngine.UI.Text` 中长度为 1–1000 个字符且包含假名的
文本。缓存位于同目录的 `OtogiTranslate.cache.jsonl`，最多加载 10000 项。通过校验的译文
保留富文本标签、转义序列、占位符和换行并替换当前文本；未通过校验的条目在本次进程内
继续显示原文。
`RetryCount` 表示首次请求之外的重试次数，范围为 0–5。408、429、5xx 和传输错误使用
退避；其他 4xx 暂停当前进程的 LLM 队列。图片、Sprite 和 Texture 中的文字沿用资源
内容。

### 剧情整段翻译

`SceneTranslation = true` 时，剧情响应（`MScenes`、`MAdults`、`Mstory`）先经社群词典改写，
仍含假名的台词（`Phrase` / `Serif`）按场景整段送往 LLM。请求按剧情顺序携带全部台词作为
上下文：说话者以稳定代号（`MMonsterId`，或 rich scene 中 `IsTalking` 角色的 ID，否则为
显示名）和显示名表示，旁白的代号为 `null`，场景标题一并附上；只有带 `id` 的行需要翻译。
单个请求约含 8000 字符原文，更长的场景按行切分。整段请求优先于逐句请求，超时为
`TimeoutSeconds` 的 4 倍（30–300 秒）。

响应的 `version`、`scene`、条目数量与 `id` 顺序全部匹配时采纳；其中空白、与原文相同或
保护标记不一致的条目单独转入逐句路径。结构不匹配或请求最终失败时，该请求的全部台词
转入逐句路径，整个场景在本次进程内不再发起整段请求；同一场景的其他切分各自校验。
请求进行中，扫描器暂缓这些台词的逐句请求；首次传输失败即解除暂缓。

结果写入 `UserData/OtogiTranslate/llm-scenes/<类型>/<ID>.json`，按行位置记录原文与译文，
同时进入逐句缓存，供首次播放中后续出现的台词直接使用。再次进入剧情时，原文仍一致的行在
响应阶段直接改写，零请求；被拒绝的行记为 `null`，保持逐句路径。

## 验证

### Smoke test

```powershell
pwsh -NoProfile -NonInteractive -File ./test-loader.ps1
```

该测试安装 `out/` 中的 APK，核对设备上 `OtogiTranslate.dll` 和字体哈希，并要求
LemonLoader assembly、剧情响应、60 FPS、马赛克、字体、词典传输、运行时驱动 hook 以及
`[OtogiCgUnlock] installed` 完成初始化。测试刷新 Plugins 与字体；登录、LLM 配置、翻译缓存
和官方包数据沿用设备现有文件。开发测试在 `su` 可用时使用它读取文件证据，其他环境使用
普通 ADB shell；文件访问失败时明确报错。应用目录与 emulator 标记由 APK 自行初始化。

### 干净安装与首次启动

```powershell
python ./e2e_first_launch.py out/Original.prototype-signed.apk jp.co.dmm.dmmgames.kms.prototype
```

前置：测试包名尚未安装。脚本仅安装、启动并观察日志，要求 APK 自行完成目录准备及插件
初始化。此测试使用普通 ADB 命令，截图与筛选后的启动日志写入 `out/e2e-first-launch/`。
已安装的包会明确报错，以保留其账号与数据。

### 字体更新回归

```powershell
python ./e2e_font.py
```

前置：具有 `su` 的 1920×1080 模拟器、可自动登录的 DMM 测试账号，以及已安装的构建产物。
测试冷启动游戏、删除游戏的字体缓存、进入资源下载，要求实际字体请求成功、固定来源哈希
匹配。截图与 Loader 日志写入 `out/e2e-font/`；登录与翻译配置沿用设备现有文件。

### 角色剧情远程 E2E

```powershell
python ./e2e_characters.py
```

冷启动清空 `UserData/OtogiCgUnlock`，从 GitHub raw 拉取 `characters.json`，进入角色剧情页后
`characters-merged monsters>=975 spirits>=149`，锁定条目出现 `originalStatus=400` 或 `404` 替换。
截图写入 `out/e2e/`。前置：1920×1080 横屏、已登录测试账号、设备能访问 GitHub raw。
需要以 `su` 访问应用目录时，设置环境变量 `OTOGI_ADB_ROOT=1`。

### 剧情整段翻译 E2E

```powershell
$env:OTOGI_ADB = 'C:/path/to/adb.exe'
python ./e2e_scene_llm.py
```

前置：具有 `su` 的 1920×1080 模拟器、已登录测试账号、已安装的构建产物。脚本在宿主
`127.0.0.1:18765` 启动模拟 OpenAI 端点并通过 `adb reverse` 映射到设备，暂存并在结束时
恢复设备的 LLM 配置、逐句缓存、场景缓存和场景 `10001` 的词典；空词典让全部台词进入整段
路径。测试依次验证：首播发出一次带说话者上下文的整段请求且无重复逐句请求；重播零请求
直接改写；结构错误与 HTTP 500 均转入逐句路径。截图与模拟端点请求记录写入
`out/e2e-scene/`。

### 真实游戏 E2E

E2E 使用以下固定前置条件：

- 1920×1080 横屏模拟器
- 已登录并已解锁灰姑娘 Flow 1 的测试账号
- 设备 `[LLM]` 配置启用，且端点、模型和有效 `ApiKey` 与命令参数一致
- 调用进程通过 `OTOGI_LLM_ENDPOINT` 和 `OTOGI_LLM_MODEL` 提供私有测试参数
- 当前游戏首页和角色剧情页面布局

```powershell
pwsh -NoProfile -NonInteractive -File ./test-e2e.ps1 `
  -ExpectedEndpoint $env:OTOGI_LLM_ENDPOINT `
  -ExpectedModel $env:OTOGI_LLM_MODEL
```

需要以 `su` 访问应用目录时，向 `test-e2e.ps1` 添加 `-UseSu`。

测试从冷启动进入剧情页面，远端获取 `MScenes/10001` 和 `MAdults/10001`，再将成人词典
应用到 API 场景 `210011`。通过条件包括：

- LLM 请求完成并由实际 UI setter 应用
- 普通剧情至少替换 100 项
- 成人剧情至少替换 60 项
- Spine 马赛克材质补丁实际命中
- 设备字体哈希匹配 payload
- SurfaceFlinger 游戏图层为 60 Hz/60 FPS，10 秒采样至少 500 帧且平均帧率至少 58

截图、Loader 日志、配置校验结果和帧统计写入 `e2e/<timestamp>/`。测试完整备份词典目录和
LLM 缓存，并在结束阶段恢复；成功以 `PASS real game E2E` 结束。

## 兼容边界

随包 bootstrap 使用非递归目录创建复制 Mono 配置，APK 的 `Application` 入口提前准备
其父目录。构建器为该入口追加独立 DEX，并保留原始 DEX；带自定义 `Application` 的输入
会触发构建错误，供人工确认其初始化顺序。

运行验证覆盖 `OtogiTranslate` 的 native IL2CPP hooks。LemonLoader 0.5.7 加载该游戏的
`UnityEngine.CoreModule` support module 时会记录 `TypeLoadException`。Prototype manifest
包含 `android:debuggable=true`，适用环境为个人使用及专用测试设备。

## 组件、数据与许可

构建使用固定版本的：

- [`LemonLoader/MelonLoader_057`](https://github.com/LemonLoader/MelonLoader_057) `0.2.0.1`
- [`Apktool`](https://github.com/iBotPeaches/Apktool) `3.0.3`
- [`MelonLoader.UnityDependencies`](https://github.com/LavaGang/MelonLoader.UnityDependencies) `2022.3.62`

Loader payload 面向 ARM64 Android，x86_64 宿主通过模拟器 native bridge 执行。输出包使用
本地开发密钥，适合个人测试。官方 APK、DMM 账号数据、签名密钥、LLM 凭据和完整翻译
资源留在本地或上游。仓库公开内容为有权发布的源码和资源。

字体 payload 为 Noto Sans CJK SC，许可见 [`LICENSES/OFL-1.1.txt`](LICENSES/OFL-1.1.txt)。
游戏、商标和原始资源归各权利人所有。第三方组件沿用各自许可。
