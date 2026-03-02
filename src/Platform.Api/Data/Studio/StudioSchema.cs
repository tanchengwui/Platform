using Microsoft.EntityFrameworkCore;

namespace Platform.Api.Data.Studio;

public static class StudioSchema
{
    public static async Task EnsureAsync(PlatformDbContext db, CancellationToken ct = default)
    {
        // StudioEntities
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'dbo.StudioEntities', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StudioEntities(
        EntityId UNIQUEIDENTIFIER NOT NULL,
        TenantId UNIQUEIDENTIFIER NOT NULL,
        AppId UNIQUEIDENTIFIER NOT NULL,
        Name NVARCHAR(128) NOT NULL,
        TableName NVARCHAR(256) NOT NULL,
        CreatedAt DATETIMEOFFSET NOT NULL,
        UpdatedAt DATETIMEOFFSET NOT NULL,
        CONSTRAINT PK_StudioEntities PRIMARY KEY (EntityId)
    );

    CREATE INDEX IX_StudioEntities_Tenant_App ON dbo.StudioEntities(TenantId, AppId);
    CREATE UNIQUE INDEX UX_StudioEntities_Tenant_App_Name ON dbo.StudioEntities(TenantId, AppId, Name);
END
", ct);

        // StudioEntityAttributes
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'dbo.StudioEntityAttributes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StudioEntityAttributes(
        AttributeId UNIQUEIDENTIFIER NOT NULL,
        EntityId UNIQUEIDENTIFIER NOT NULL,
        Name NVARCHAR(128) NOT NULL,
        Type NVARCHAR(64) NOT NULL,
        Length INT NULL,
        Required BIT NOT NULL,
        CreatedAt DATETIMEOFFSET NOT NULL,
        CONSTRAINT PK_StudioEntityAttributes PRIMARY KEY (AttributeId),
        CONSTRAINT FK_StudioEntityAttributes_Entity FOREIGN KEY (EntityId) REFERENCES dbo.StudioEntities(EntityId) ON DELETE CASCADE
    );

    CREATE INDEX IX_StudioEntityAttributes_Entity ON dbo.StudioEntityAttributes(EntityId);
    CREATE UNIQUE INDEX UX_StudioEntityAttributes_Entity_Name ON dbo.StudioEntityAttributes(EntityId, Name);
END
", ct);
    }
}
