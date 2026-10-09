# 小羊便签 KimNotes

Windows 桌面便签 + 截图标注工具，单 exe，无边框卡片风格。

## 功能

- **便签卡片**：无边框卡片窗体，边缘可拉伸，标题栏常驻（图标 + 标题 + ⋯ / ✕），支持置顶。
- **历史记录**：便笺式卡片列表，本地索引做关键词搜索。
- **截图**：全局热键 `Ctrl + F1`（默认值，改 `%AppData%\KimNotes\config.txt` 里的 `shortcutKey`），框选即截，截完悬浮窗可滚轮缩放、拖动、拾色、另存。
- **标注**：矩形 / 箭头 / 马赛克 / 文字四种画笔，`Ctrl+Z` 撤销，`Esc` 结束并把标注烘进图片。
- **OCR / 翻译**：右键菜单调用百度开放平台（通用文字识别、文本翻译）。
- **待办清单**：独立存储在 `todos.json`，支持定时提醒（置顶小窗 + 铃声）。
- **主题**：8 套配色，同族窗体（便签 / 历史 / 设置 / 待办 / 提醒）统一视觉。

> OCR 和翻译用的是作者自己的百度应用，`utils/Translator.cs`、`utils/RemoteCallUtils.cs` 里是明文 appid/secret。二次开发请换成自己的应用凭证，别蹭额度。

## 技术栈

- C# / .NET Framework 4.8 / WinForms
- 旧式 csproj + `packages.config`（非 SDK-style）
- 依赖：Newtonsoft.Json 13.0.3、System.Text.Json 9.0.3 及其 BCL 垫片

## 编译运行

1. 用 Visual Studio 2019/2022 打开 `KimNotes.sln`（需装 .NET Framework 4.8 目标包）。
2. 首次打开会自动 NuGet restore；`packages/` 不入库。
3. F5 运行，或命令行：

   ```
   msbuild KimNotes.csproj -p:Configuration=Debug
   ```

## 数据存放

全部在 `%AppData%\KimNotes\`：

| 位置 | 用途 |
|---|---|
| `config.txt` | 配置：`shortcutKey` 截图热键、`theme` 主题、`notesPath` / `imagesPath` 存储目录、`checkBox1~3` 开关 |
| `notes\` | 便签正文（默认目录，键名 `notesPath`） |
| `images\` | 截图另存目录（键名 `imagesPath`） |
| `index.json` | 历史搜索索引 |
| `todos.json` | 待办清单 |
| `reminders-fired.json` | 提醒去重记录 |

删掉整个目录即回到全新状态。

## 说明

- 窗体尺寸按窗口自身 DPI 量算（`utils/UiDpi.cs`），高分屏下不裁切文字。

## 许可证

采用 [Apache License 2.0](LICENSE)，可自由使用、修改和再分发，保留版权声明即可。
