# Prompt 采集器（C# / SQL Server）

用于采集 `https://ai.codefather.cn/prompt` 列表页及每个提示词详情页内容，并写入 SQL Server 的 `ai.dbo.prompt` 表。

## 1. 初始化数据库表

在 SQL Server 中执行：

- `sql/create_prompt_table.sql`

该脚本会：
- 自动创建数据库 `ai`（若不存在）
- 创建 `prompt` 表与唯一索引（按 `prompt_url` 去重）

## 2. 运行采集程序

### 方式 A：环境变量配置连接串

```bash
export AI_SQLSERVER_CONNECTION='Server=.;Database=ai;Trusted_Connection=True;TrustServerCertificate=True'
dotnet run
```

### 方式 B：命令行参数传连接串

```bash
dotnet run -- --conn "Server=.;Database=ai;Trusted_Connection=True;TrustServerCertificate=True"
```

## 3. 程序逻辑说明

- 从 `/prompt` 开始采集列表页
- 自动翻页（识别“下一页”或 `page=N+1`）
- 提取每条详情链接（`/prompt/...`）
- 进入详情页采集：标题、摘要、提示词正文、标签
- 使用 SQL `MERGE` 按 `prompt_url` 做 Upsert（存在则更新，不存在则插入）

## 4. 项目结构

- `Program.cs`：采集逻辑 + 入库逻辑
- `sql/create_prompt_table.sql`：建库建表脚本
- `PromptCrawler.csproj`：.NET 项目依赖

## 5. 依赖

- .NET 7.0
- NuGet 包：
  - `HtmlAgilityPack`
  - `Dapper`
  - `Microsoft.Data.SqlClient`
