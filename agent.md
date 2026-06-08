# Agent Notes

## 项目来源

- 上游项目：`https://github.com/jxlpzqc/TMSpeech`
- 当前 fork：`https://github.com/dustheart25/TMSpeech`
- 当前工作分支：`openai-compatible-translation`
- 本地源码目录：`C:\tmp\TMSpeech-pr-lowercase`
- 本地测试发布目录：`C:\tmp\TMSpeech-pr-lowercase\publish`
- 本地可运行程序：`C:\tmp\TMSpeech-pr-lowercase\publish\TMSpeech.exe`

TMSpeech 是一个 Windows 桌面字幕工具，核心流程是获取系统/麦克风音频，通过识别插件生成字幕，并在桌面窗口中显示识别结果。当前分支在原项目基础上加入了 OpenAI-compatible 翻译插件、双语字幕显示、英文小写修正、历史记录和字幕导出等改动。

## 本地构建环境

- .NET SDK 安装目录：`D:\Dev\dotnet`
- 已安装 SDK：`.NET 6.0.428`、`.NET 8.0.421`
- 用户环境变量已配置：`DOTNET_ROOT` 和 `Path`
- 推荐发布命令：

```powershell
& 'D:\Dev\dotnet\dotnet.exe' publish -r win-x64 -c Release -o C:\tmp\TMSpeech-pr-lowercase\publish --self-contained true .\src\TMSpeech\TMSpeech.csproj
```

发布时可能出现大量既有 warning。只要命令退出码为 0，并最终输出到 `publish`，即可继续本地测试。`nbeauty` 可能提示 `hostfxr.dll.bak Access is denied`，目前观察不影响最终发布结果。

## 已完成修改记录

### 1. OpenAI-compatible 翻译插件

- 新增 OpenAI-compatible 翻译器插件。
- 支持自定义 Base URL、模型、API Key、目标语言等配置。
- API Key 字段改为密码输入，并增加测试按钮，用于检查 API Key 和模型是否可用。
- 插件发布时避免继承主程序 RID/self-contained 配置，修复插件依赖 runtime pack 导致的加载失败。

相关提交：

- `1438f4c Build plugins without runtime pack dependencies`
- `dc464ce Support password plugin config fields`
- `163b070 Show bilingual subtitles and test translator settings`

### 2. 英文小写修复

- SherpaOnnx 识别英文时会强制转换为小写，避免全大写字幕影响阅读。
- 修改位置：`src/Plugins/TMSpeech.Recognizer.SherpaOnnx/SherpaOnnxRecognizer.cs`
- 关键逻辑：`text = text.ToLowerInvariant();`

相关提交：

- `70cfe62 Add lowercase option for SherpaOnnx text`

### 3. 双语字幕显示和多句缓存

- 字幕支持原文和译文上下分开显示。
- 最近字幕缓存增加到 4 句，避免译文返回时只闪现一句。
- 原文实时显示，译文回来后更新对应句子。

相关提交：

- `f1af9b1 Keep recent bilingual captions visible`
- `365ea1e Add bilingual caption colors and record export`

### 4. 字幕颜色分别设置

- 设置页新增“翻译文字颜色”。
- 原识别文字继续使用原来的“字体颜色”。
- 字幕控件现在按结构化字幕行渲染，可分别绑定原文颜色和译文颜色。

主要文件：

- `src/TMSpeech.Core/CaptionTextInfo.cs`
- `src/TMSpeech.Core/ConfigTypes.cs`
- `src/TMSpeech.GUI/Controls/CaptionView.axaml`
- `src/TMSpeech.GUI/Controls/CaptionView.axaml.cs`
- `src/TMSpeech.GUI/ViewModels/ConfigViewModel.cs`
- `src/TMSpeech.GUI/ViewModels/MainViewModel.cs`
- `src/TMSpeech.GUI/Views/ConfigWindow.axaml`
- `src/TMSpeech.GUI/Views/MainWindow.axaml`

### 5. 识别记录写入译文并导出

- 翻译成功后，普通识别日志会写入 `译文: ...`。
- 历史记录窗口会把同一句更新为：

```text
原文
译文
```

- 停止识别时会在普通日志同目录自动导出：
  - `{原日志名}.bilingual.txt`
  - `{原日志名}.srt`

导出依赖“常规设置”里的识别日志路径。如果未配置日志路径，则不会生成导出文件。

相关提交：

- `365ea1e Add bilingual caption colors and record export`

## 本地测试建议

1. 打开 `C:\tmp\TMSpeech-pr-lowercase\publish\TMSpeech.exe`。
2. 在设置里确认：
   - 识别器选择 SherpaOnnx。
   - 翻译功能已启用。
   - OpenAI-compatible 翻译插件 API Key、Base URL、模型配置正确。
   - “字体颜色”和“翻译文字颜色”分别设置为容易区分的颜色。
   - 常规设置里配置了识别日志保存路径。
3. 开始识别英文音频：
   - 英文识别字幕应显示为小写。
   - 译文应在原文下方显示。
   - 屏幕上最多保留最近 4 句双语字幕。
4. 打开历史记录：
   - 译文返回后，对应历史项应变成原文加译文。
5. 停止识别：
   - 在日志目录检查普通 `.txt`、`.bilingual.txt` 和 `.srt` 文件。

## 注意事项

- 用户要求只推送到 fork，不向上游项目发 PR。
- 当前分支已推送到 `dustheart25/TMSpeech` 的 `openai-compatible-translation`。
- 发布目录中的模型文件很重要，尤其是 `publish\models\encoder.onnx`。如果缺失，SherpaOnnx 会报 `Cannot find model file: models\encoder.onnx`。
- 如果构建遇到 `obj\gitversion.json` 权限问题，通常是旧构建产物由本机用户创建而沙箱用户无权覆盖。用本机用户权限删除或重新发布即可。

## 2026-06-08 补充：大模型纠错与字幕缓存设置

- 新增全局“大模型纠错”开关，和“启用翻译”彼此独立。
- 支持只纠错不翻译、只翻译不纠错、纠错加翻译、两者都关闭四种状态。
- 当纠错和翻译都启用时，OpenAI-compatible 插件会尽量用一次请求同时返回 `corrected_text` 和 `translated_text`。
- 字幕显示保持原识别文本实时出现；纠错结果和译文返回后追加到同一句下方，减少等待大模型时的空白感。
- 设置页“外观”中新增“字幕缓存句数”，默认值为 4，可在 1 到 10 之间调节。
- 停止识别时导出的 `.bilingual.txt` 和 `.srt` 会包含已有的纠错文本和译文。

## 2026-06-08 补充：三类字幕颜色

- 设置页“外观”中新增“纠错文字颜色”。
- 当前字幕颜色分工：
  - “字体颜色”：原识别文字。
  - “纠错文字颜色”：大模型纠错后的文字。
  - “翻译文字颜色”：译文。
- 纠错文字默认使用浅蓝色，和原识别白色、译文黄色区分。

## 2026-06-08 补充：启动后手动开始识别

- 程序启动后不再自动开始识别，需要用户手动点击播放按钮后才开始采集和识别。
- 默认配置 `general.StartOnLaunch` 改为 `false`。
- 对已经保存过旧配置的用户，启动时如果检测到 `general.StartOnLaunch=true`，会自动写回 `false`，避免旧配置继续触发自动识别。
- 设置页中隐藏“启动开始识别”选项，避免误开启。
