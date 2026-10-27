# 图片同步修复（2026-10-23）

原先一张便签的图片会重复保存：一份是 RTF 中的 WMF/DIB 十六进制数据，一份是云端私有 PNG 附件。即使原图未超过 10 MiB，普通截图也可能使压缩 RTF 的 Base64 超过 2 MiB，导致同步失败。

## 协议

保留原有 `rtf` / `rtfGzip` 和 `attachments`，新增可选字段：

```json
{
  "rtfGzip": "仅含文字格式与图片占位组的 gzip/base64",
  "rtfImages": [
    {
      "marker": "小写 UUID",
      "attachmentId": "私有附件 UUID",
      "properties": "\\picwgoal6345\\pichgoal4305"
    }
  ],
  "attachments": ["私有附件 UUID"]
}
```

模板中的整个图片组替换为 `{\*\kimimage <marker UUID>}`。每个标记只出现一次，每个引用都必须指向 `attachments` 中属于当前用户、当前应用的图片。一个附件允许在不同位置重复展示，重复位置分别计入重建预算。

客户端先查询 `/config` 的 `inlineImageAttachmentsVersion: 1`，能力按 CloudApi 实例缓存；网络错误不会缓存成“不支持”。旧服务端仅对旧 Pack 可接受的小便签回退旧格式；大便签保留本地原稿并给出可恢复错误，不发送无法识别的模板。

所有新版桌面请求增加 `X-Kim-Inline-Images: 1`。服务端对旧桌面拉取/读取历史中新模板返回 426；旧桌面对已使用新模板的账号推送时，在用户同步流锁内、执行任何操作前返回 426，防止静默清空图片或部分提交。网页仍使用正文和私有附件展示，不受此升级保护限制。

## 无损和响应速度

已有 PNG 图片流直接复用原始字节；WMF/DIB 使用内嵌原始像素无损编码为 PNG，不缩小尺寸、不采用 JPEG。图片仍经现有私有附件接口、内容哈希去重和账号配额保存。

还原不走现有 GDI+ EMF→WMF 路径，因为严格像素测试发现该路径每次往返会再次量化 RGB。采用标准 WMF `META_STRETCHDIB` 直接装入原 BMP/DIB 像素，恢复原图片的位置、目标大小、裁切和比例参数，并保留图片前后文本格式。真实 RichEdit 载入、再导出后也逐像素验证一致。

临时 RichEdit 控件在专用后台 STA 线程创建并销毁；图片解析、编码、散列、大字符串压缩在后台执行，取消操作会阻止后续上传与还原。导出不修改原本地文件；模板缺图、损坏、超过预算时抛出错误，由同步层隔离该便签，不落盘为成功结果。

## 边界

- 单个原图最多 10 MiB、4000 万像素；不通过有损转换绕过。
- 每张便签最多 32 个图片位置，附件列表最多 32 项。
- 模板压缩 Base64 最多 2 MiB，解压最多 32 MiB。
- 还原原生富文本合计最多 128 MiB；服务端和客户端均按每处 `像素数 × 8 + 64 KiB` 加模板长度预检，重复位置也累计。常见 3840×2160 截图可通过；超出时明确说明拆分便签，保留原图。
- WMF 目标坐标使用有符号 16 位值，单边超过 32767 像素时返回明确、可恢复的尺寸错误。
- 禁止 object/objdata/objclass/filetbl/bin（包括 `\bin4` 等带数字长度形式）；禁止缺失、重复、额外、未知标记和非白名单显示参数。最多解析 33 个标记即可拒绝恶意模板，分组深度最多 256。
- 原有附件权限、上传速率、256 MiB 账号配额和历史版本规则不变。

## 验证

Go 单元测试：

```powershell
# backend 目录；没有 DATABASE_URL 时数据库集成项会显式 skip。
go test ./... -run 'TestInlineImage|TestCompressedRichText|TestValidation' -timeout 180s
```

`TestInlineImagePrivateRoundTrip` 需在隔离测试环境的 PostgreSQL 执行：创建独立随机测试 schema，验证原图字节一致、旧 2 MiB 限制复现、新协议往返、跨用户/应用权限、解码预算和旧客户端无部分提交保护，测试结束删除自己的 schema。

原生专用回归测试单独编译为 `CloudRegression.exe`（沿用现有 InternalsVisibleTo），不要将它和旧 CloudRegression.cs 同时编译：

```powershell
& $msbuild KimNotes.csproj /t:Build /p:Configuration=Release /v:minimal /nologo
& $csc /nologo /target:exe /out:bin/Release/CloudRegression.exe /reference:bin/Release/KimNotes.exe /reference:bin/Release/Newtonsoft.Json.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.dll /reference:System.Security.dll tests/CloudImageRegression.cs
& bin/Release/CloudRegression.exe (Join-Path $env:TEMP ('KimNotes-image-tests-' + [guid]::NewGuid().ToString('N')))
```

覆盖 1100×800 确定性高熵图片（复现旧 Pack 失败）、4K 可压缩图片、逐像素一致、原生 423×287 目标区域、真实 RichEdit 再保存、文本样式和内联顺序、PNG 原字节保留、取消、旧格式/旧服务器兼容、瞬时能力查询错误重试、模板压缩炸弹和恶意标记。
