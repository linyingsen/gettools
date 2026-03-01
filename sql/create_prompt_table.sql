IF DB_ID(N'ai') IS NULL
BEGIN
    EXEC('CREATE DATABASE [ai]');
END;
GO

USE [ai];
GO

IF OBJECT_ID(N'dbo.prompt', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.prompt
    (
        id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        prompt_url      NVARCHAR(500) NOT NULL,
        title           NVARCHAR(300) NOT NULL,
        summary         NVARCHAR(MAX) NULL,
        prompt_text     NVARCHAR(MAX) NULL,
        tags            NVARCHAR(500) NULL,
        source_page_url NVARCHAR(500) NULL,
        raw_html        NVARCHAR(MAX) NULL,
        crawled_at      DATETIMEOFFSET(0) NOT NULL,
        updated_at      DATETIMEOFFSET(0) NOT NULL CONSTRAINT DF_prompt_updated_at DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE UNIQUE INDEX UX_prompt_prompt_url ON dbo.prompt(prompt_url);
END;
GO
