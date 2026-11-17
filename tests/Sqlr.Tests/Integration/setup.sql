-- Integration-test fixture. Recreates the SqlrTest / SqlrOther databases from scratch.
-- Run automatically by the tests (through sqlr's own GO-aware runner).
IF DB_ID('SqlrTest') IS NOT NULL BEGIN ALTER DATABASE SqlrTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SqlrTest; END
IF DB_ID('SqlrOther') IS NOT NULL BEGIN ALTER DATABASE SqlrOther SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SqlrOther; END
GO
CREATE DATABASE SqlrTest;
CREATE DATABASE SqlrOther;
GO
USE SqlrOther;
CREATE TABLE dbo.OtherThing (Id int PRIMARY KEY, Label nvarchar(50));
GO
USE SqlrTest;
GO
CREATE SCHEMA sales;
GO
CREATE SCHEMA archive;
GO
CREATE TABLE sales.Customers (
    CustomerId int IDENTITY PRIMARY KEY,
    Name       nvarchar(100) NOT NULL,
    Email      varchar(200) NULL,
    CreatedAt  datetime2 DEFAULT SYSUTCDATETIME(),
    Balance    decimal(12,2) DEFAULT 0,
    IsActive   bit DEFAULT 1,
    ExternalId uniqueidentifier DEFAULT NEWID());
CREATE TABLE sales.Orders (
    OrderId    int IDENTITY PRIMARY KEY,
    CustomerId int NOT NULL REFERENCES sales.Customers(CustomerId),
    OrderDate  date NOT NULL,
    Total      money NOT NULL,
    Notes      nvarchar(max) NULL,
    Payload    varbinary(max) NULL);
CREATE INDEX IX_Orders_Customer ON sales.Orders (CustomerId, OrderDate DESC) INCLUDE (Total);
CREATE TABLE archive.Orders (ArchivedOrderId int PRIMARY KEY, ArchivedAt datetimeoffset NOT NULL, Reason nvarchar(200));
CREATE TABLE dbo.[Order Details] ([Line Id] int PRIMARY KEY, [Order Id] int, Qty smallint, Price float, Doc xml NULL, Place geography NULL, Dur time NULL);
CREATE TABLE dbo.BigTable (Id int PRIMARY KEY, Val nvarchar(50));
GO
CREATE VIEW sales.ActiveCustomers AS SELECT CustomerId, Name, Email FROM sales.Customers WHERE IsActive = 1;
GO
CREATE PROCEDURE sales.GetCustomer @Id int, @Verbose bit = 0 AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM sales.Customers WHERE CustomerId = @Id;
END
GO
CREATE FUNCTION dbo.AddOne(@x int) RETURNS int AS BEGIN RETURN @x + 1 END
GO
INSERT sales.Customers (Name, Email, Balance) VALUES (N'Ada Lovelace', 'ada@example.com', 1234.56), (N'Grace Hopper', NULL, 0), (N'Ünïcødé 名前', 'u@example.com', -12.5);
INSERT sales.Orders (CustomerId, OrderDate, Total, Notes, Payload) VALUES
    (1, '2026-01-02', 99.95, N'line one
line two	tabbed', 0xDEADBEEF),
    (2, '2026-02-03', 10, NULL, NULL),
    (1, '2026-03-04', 5.5, REPLICATE(N'long text ', 100), 0x00);
INSERT archive.Orders VALUES (1, '2025-12-31 23:59:59 +01:00', N'old');
INSERT dbo.[Order Details] VALUES (1, 1, 2, 3.14159, N'<a><b>1</b></a>', geography::Point(47.6, -122.3, 4326), '12:34:56.789');
;WITH n AS (SELECT TOP (250000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.BigTable SELECT i, CONCAT(N'row ', i) FROM n;
