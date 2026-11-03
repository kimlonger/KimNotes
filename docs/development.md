# 开发与数据维护

## 编译运行

技术栈：C# / .NET Framework 4.8 / WinForms，旧式 csproj 与 `packages.config`；第三方依赖为 Newtonsoft.Json 13.0.3。

1. 使用 Visual Studio 2019 / 2022 打开 `KimNotes.sln`，安装 .NET Framework 4.8 目标包并还原 NuGet 包。
2. 如需使用 OCR / 翻译，在项目根目录自行创建 `baidu-credentials.local.json`：

   ```json
   {
     "TRANSLATE_APP_ID": "你的翻译 App ID",
     "TRANSLATE_SECRET": "你的翻译密钥",
     "OCR_API_KEY": "你的 OCR API Key",
     "OCR_SECRET": "你的 OCR Secret Key"
   }
   ```

   此文件已被 Git 忽略，构建时嵌入应用，不在 ZIP 中单独分发。公开源码不包含服务密钥；未提供此文件仍可编译，OCR / 翻译不可用。

3. 按 F5 运行，或在 Visual Studio 开发者命令提示符中执行：

   ```powershell
   msbuild KimNotes.csproj -p:Configuration=Release
   ```

### 官网源码

官网源码位于 [`website/`](website/)，静态页面和图片在 `website/public/`，由 Cloudflare Worker 提供服务。安装 Node.js 与 npm 后，可在该目录执行 `npm ci`、`npm run dev`；有对应 Cloudflare 权限时执行 `npm run deploy` 发布。

## 数据存放与备份

默认数据目录为 `%AppData%\KimNotes\`，便签和截图目录可在设置中修改。

| 文件 / 目录 | 用途 |
|---|---|
| `config.txt` | 截图热键、默认应用主题、存储路径、开机启动、无痕模式和提醒方式 |
| `notes\` | 默认便签正文目录，RTF 格式 |
| `notes\*.rtf.meta.json` | 每张便签的主题配色，备份 / 迁移时需与正文一起保留 |
| `open-notes.json` | 上次打开的已保存便签列表 |
| `screenshot-history\` | 可唤回的最近截图缓存，独立于用户导出的图片 |
| `images\` | 默认截图保存目录 |
| `index.json` | 便签全文搜索索引 |
| `todos.json` | 待办清单 |
| `reminders-fired.json` | 提醒去重记录 |

正文、配置与 JSON 使用同目录临时文件原子替换，覆盖时保留上一份 `.bak`。数据损坏时可从有效备份恢复；没有有效备份时保留损坏文件。更换便签目录时复制正文与配色文件，原目录保留；目标中存在内容不同的同名文件时阻止切换。

无痕模式之外，清空已经保存的便签会正常保存空内容。请在手动迁移或清理数据前备份整个数据目录与自定义便签目录。

## 回归验证

```powershell
powershell -ExecutionPolicy Bypass -File tests\run.ps1
```

测试使用临时隔离目录，不读写真实用户便签、不调用百度接口、不更改开机启动设置。覆盖独立主题、空闲精简、图片保存与渲染、全文索引、截图会话和标注、提醒去重、日期时间输入、滚动与缩放、备份恢复、写入失败重试及无痕模式等场景。可通过 `KIMNOTES_DATA_DIR` 指定独立数据目录。

几何与渲染模拟不替代真实多屏混合 DPI、输入法、长期挂机及在线接口验证。

## 使用说明

- 关闭所有窗口后仍会常驻托盘。托盘右键可新建便签、打开便签列表 / 待办清单 / 设置，或退出程序；左键唤回已打开的便签，没有已打开便签时新建。
- Windows 11 可能将新托盘图标放入任务栏 `^` 溢出区，可自行拖至可见区域。
