-- Fictional data for the README clips (DemoClips.cs). Recreated on every recording run.
IF DB_ID('SqlrDemo') IS NOT NULL
BEGIN
    ALTER DATABASE SqlrDemo SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SqlrDemo;
END
IF DB_ID('SqlrArchive') IS NOT NULL
BEGIN
    ALTER DATABASE SqlrArchive SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SqlrArchive;
END
CREATE DATABASE SqlrDemo;
CREATE DATABASE SqlrArchive;
GO

USE SqlrDemo;
GO
CREATE SCHEMA sales;
GO

CREATE TABLE dbo.Products (
    ProductId INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(80) NOT NULL,
    Category NVARCHAR(40) NOT NULL,
    Price DECIMAL(10, 2) NOT NULL
);

CREATE TABLE sales.Customers (
    CustomerId INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    City NVARCHAR(60) NOT NULL,
    State CHAR(2) NOT NULL,
    Email VARCHAR(120) NULL,
    Preferences NVARCHAR(MAX) NULL,
    CreatedAt DATETIME NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT GETDATE()
);

CREATE TABLE sales.Orders (
    OrderId INT IDENTITY PRIMARY KEY,
    CustomerId INT NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES sales.Customers (CustomerId),
    OrderDate DATE NOT NULL,
    Status VARCHAR(12) NOT NULL,
    Total DECIMAL(12, 2) NOT NULL
);
CREATE INDEX IX_Orders_CustomerId ON sales.Orders (CustomerId);

CREATE TABLE sales.OrderLines (
    OrderLineId INT IDENTITY PRIMARY KEY,
    OrderId INT NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES sales.Orders (OrderId),
    ProductId INT NOT NULL CONSTRAINT FK_OrderLines_Products REFERENCES dbo.Products (ProductId),
    Qty INT NOT NULL,
    UnitPrice DECIMAL(10, 2) NOT NULL
);
GO

INSERT INTO dbo.Products (Name, Category, Price) VALUES
    ('Cold brew, 1 L', 'Coffee', 9.50), ('Espresso beans, 1 kg', 'Coffee', 24.00),
    ('Oat milk, 6 pack', 'Dairy-free', 15.00), ('Ceramic mug', 'Merch', 12.00),
    ('Paper cups, 500', 'Supplies', 38.00), ('Croissant tray', 'Bakery', 21.00),
    ('Matcha tin', 'Tea', 18.50), ('Grinder burrs', 'Equipment', 64.00);

INSERT INTO sales.Customers (Name, City, State, Email, Preferences, CreatedAt)
SELECT v.Name, v.City, v.State, v.Email, v.Prefs, DATEADD(DAY, -v.Age, '2026-11-01')
FROM (VALUES
    ('Harbor Lane Bakery', 'Boston', 'MA', 'orders@harborlane.example', N'{"delivery":"morning","invoice":"monthly","contacts":["Ana","Rui"]}', 410),
    ('Maple Street Dental', 'Providence', 'RI', 'office@maplestreet.example', NULL, 380),
    ('Blue Fin Coffee Co', 'Boston', 'MA', 'hello@bluefin.example', N'{"delivery":"afternoon","invoice":"weekly","contacts":["Sam"]}', 350),
    ('North Pier Books', 'Portland', 'ME', 'shop@northpier.example', NULL, 330),
    ('Granite Hill Gym', 'Manchester', 'NH', 'front@granitehill.example', NULL, 300),
    ('Two Rivers Florist', 'Hartford', 'CT', 'blooms@tworivers.example', N'{"delivery":"any","invoice":"monthly","contacts":[]}', 280),
    ('Old Mill Brewery', 'Burlington', 'VT', 'taps@oldmill.example', NULL, 260),
    ('Cedar Point Clinic', 'Boston', 'MA', 'admin@cedarpoint.example', NULL, 240),
    ('Lantern Street Diner', 'Worcester', 'MA', 'eat@lantern.example', NULL, 220),
    ('Saltwater Surf Shop', 'Newport', 'RI', 'waves@saltwater.example', NULL, 200),
    ('Pinecone Daycare', 'Concord', 'NH', 'hello@pinecone.example', NULL, 180),
    ('Bright Path Tutors', 'Cambridge', 'MA', 'learn@brightpath.example', NULL, 160),
    ('Quarry Road Hardware', 'Nashua', 'NH', 'tools@quarryroad.example', NULL, 140),
    ('Seaside Yoga', 'Salem', 'MA', 'om@seaside.example', NULL, 120),
    ('Red Barn Market', 'Springfield', 'MA', 'farm@redbarn.example', NULL, 100),
    ('Copper Kettle Cafe', 'Boston', 'MA', 'brew@copperkettle.example', NULL, 80),
    ('Elm Court Apartments', 'New Haven', 'CT', 'lease@elmcourt.example', NULL, 60),
    ('Riverbend Vet', 'Lowell', 'MA', 'pets@riverbend.example', NULL, 40)
) v (Name, City, State, Email, Prefs, Age);

WITH n AS (
    SELECT TOP (240) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects
)
INSERT INTO sales.Orders (CustomerId, OrderDate, Status, Total)
SELECT 1 + (i * 7) % 18,
       DATEADD(DAY, -((i * 13) % 300), '2026-11-01'),
       CASE WHEN i % 11 = 0 THEN 'cancelled' WHEN i % 5 = 0 THEN 'open' ELSE 'shipped' END,
       CAST(40 + (i * 37) % 900 + (i % 7) * 0.25 AS DECIMAL(12, 2))
FROM n;

INSERT INTO sales.OrderLines (OrderId, ProductId, Qty, UnitPrice)
SELECT o.OrderId, 1 + (o.OrderId * 3 + k.k) % 8, 1 + (o.OrderId + k.k) % 6, p.Price
FROM sales.Orders o
CROSS JOIN (VALUES (1), (2)) k (k)
JOIN dbo.Products p ON p.ProductId = 1 + (o.OrderId * 3 + k.k) % 8;
GO

CREATE VIEW sales.vActiveCustomers AS
SELECT c.CustomerId, c.Name, c.City, COUNT(o.OrderId) AS Orders, SUM(o.Total) AS Spent
FROM sales.Customers c
JOIN sales.Orders o ON o.CustomerId = c.CustomerId
WHERE o.Status <> 'cancelled'
  AND o.OrderDate >= DATEADD(MONTH, -6, '2026-11-01')
GROUP BY c.CustomerId, c.Name, c.City;
GO

CREATE PROCEDURE sales.usp_GetCustomerOrders
    @CustomerId INT,
    @Since DATE = NULL
AS
SET NOCOUNT ON;
SELECT o.OrderId, o.OrderDate, o.Status, o.Total
FROM sales.Orders o
WHERE o.CustomerId = @CustomerId
  AND (@Since IS NULL OR o.OrderDate >= @Since)
ORDER BY o.OrderDate DESC;
GO

CREATE FUNCTION dbo.fn_OrderTotal (@OrderId INT)
RETURNS DECIMAL(12, 2)
AS
BEGIN
    RETURN (SELECT SUM(l.Qty * l.UnitPrice) FROM sales.OrderLines l WHERE l.OrderId = @OrderId);
END
GO

USE SqlrArchive;
GO
CREATE SCHEMA archive;
GO
CREATE TABLE archive.Orders (
    OrderId INT PRIMARY KEY,
    CustomerId INT NOT NULL,
    OrderDate DATE NOT NULL,
    Total DECIMAL(12, 2) NOT NULL,
    ArchivedAt DATETIME NOT NULL CONSTRAINT DF_Orders_ArchivedAt DEFAULT GETDATE()
);
GO
CREATE PROCEDURE archive.usp_ArchiveCustomer
    @CustomerId INT
AS
SET NOCOUNT ON;
INSERT INTO archive.Orders (OrderId, CustomerId, OrderDate, Total)
SELECT o.OrderId, o.CustomerId, o.OrderDate, o.Total
FROM SqlrDemo.sales.Orders o
WHERE o.CustomerId = @CustomerId;
GO
