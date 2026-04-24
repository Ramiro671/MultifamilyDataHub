-- Initialize MDH database and schemas
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'MDH')
BEGIN
    CREATE DATABASE MDH;
END
GO

USE MDH;
GO

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'warehouse')
BEGIN
    EXEC('CREATE SCHEMA warehouse');
END
GO

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'hangfire')
BEGIN
    EXEC('CREATE SCHEMA hangfire');
END
GO
