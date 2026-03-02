# Prompt 采集器（C# WinForms / SQL Server）

这是一个 .NET 7 的桌面程序（WinForms），用于采集 `https://ai.codefather.cn/prompt` 列表页与详情页的提示词，并写入 SQL Server 的 `ai.dbo.prompt` 表。

## 1. 初始化数据库表

在 SQL Server 中执行：

- `sql/create_prompt_table.sql`

该脚本会：
- 自动创建数据库 `ai`（若不存在）
- 创建 `prompt` 表与唯一索引（按 `prompt_url` 去重）

## 2. 运行程序

```bash
dotnet run
```

启动后会看到窗口，点击 **“开始”** 按钮才会执行采集与入库。

## 3. 数据库连接串

已按需求将数据库连接串写入代码：

- `CrawlerLogic.cs` 中 `CrawlerBootstrap.ConnectionString`

默认值：

```text
Server=.;Database=ai;Trusted_Connection=True;TrustServerCertificate=True
```

你可以直接修改该常量为自己的 SQL Server 连接信息。

## 4. 程序逻辑说明

- 从 `/prompt` 开始采集列表页
- 自动翻页（识别“下一页”或 `page=N+1`）
- 提取每条详情链接（`/prompt/...`）
- 进入详情页采集：标题、摘要、提示词正文、标签
- 使用 SQL `MERGE` 按 `prompt_url` 做 Upsert（存在则更新，不存在则插入）

## 5. 项目结构

- `Program.cs`：WinForms 程序入口
- `MainForm.cs`：桌面界面与“开始”按钮事件
- `CrawlerLogic.cs`：抓取逻辑 + 入库逻辑 + 连接串
- `sql/create_prompt_table.sql`：建库建表脚本
