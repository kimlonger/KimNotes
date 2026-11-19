# Guitar 应用扩展

应用 ID 为 `guitar`，使用现有 `users`、`user_apps`、应用会话与网关。迁移 `026_guitar_archive_courses.sql` 将旧课程、章节、课时、进度、问答和回复移到 `kimcloud_archive`，保留历史行、索引和外键。服务从 `kimcloud` 读取现有曲谱、个人上传、收藏、练习和社区数据。

公共资源不依赖个人上传记录。审核批准时把文件复制到 `guitar_scores`，个人记录仅引用公共 ID；删除个人上传不会级联删除公共曲谱。

```sh
go run . migrate
go run . guitar-import
go run . guitar-import --file scores.json --replace
go run . guitar-review list
go run . guitar-review export UPLOAD_UUID review.png
go run . guitar-review approve UPLOAD_UUID
go run . guitar-review reject UPLOAD_UUID "审核说明"
go run . guitar-review message MESSAGE_UUID accepted "已加入需求清单"
```

导入文件提供非空的 `scores` 数组。历史混合文件中的课程部分保留在源文件中，导入只处理曲谱。默认保留同 ID 的已有曲谱；`--replace` 更新同 ID 内容，一批曲谱在同一事务中写入。Docker 构建须包含内置 `guitar-seed.json`。

API 前缀 `/api/v1/apps/guitar/`。曲谱目录、留言和社区问题列表公开，曲谱详情、个人数据和问答发布要求登录。匿名留言有服务端频率及长度限制。每日打开按 Asia/Shanghai 日期去重。

当前审核入口为操作者 CLI，没有公开管理 HTTP 接口。持有数据库权限的维护者负责检查上传内容和分享权限。

集成测试需设置 `KIMCLOUD_TEST_DATABASE_URL`，使用已有测试环境隔离 schema。独立前端 README 提供本地开发数据库和测试脚本。
