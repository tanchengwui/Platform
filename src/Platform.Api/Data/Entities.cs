using System.ComponentModel.DataAnnotations;

namespace Platform.Api.Data;

public enum AppStatus { Active = 1, Archived = 2 }
public enum DraftStatus { Editing = 1, Published = 2 }
public enum VersionStatus { Published = 1, Deprecated = 2 }
public enum DeploymentStatus { Queued = 1, Running = 2, Succeeded = 3, Failed = 4, RolledBack = 5, Canceled = 6 }
public enum EnvironmentType { DEV = 1, STG = 2, PROD = 3 }

public sealed class Tenant
{
    [Key] public Guid TenantId { get; set; } = Guid.NewGuid();
    [MaxLength(200)] public string Name { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class User
{
    [Key] public Guid UserId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(320)] public string Email { get; set; } = default!;
    [MaxLength(500)] public string PasswordHash { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RefreshToken
{
    [Key] public Guid TokenId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }

    [MaxLength(128)] public string TokenHash { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    [MaxLength(200)] public string? Device { get; set; }
}

public sealed class Role
{
    [Key] public Guid RoleId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(100)] public string Name { get; set; } = default!;
}

public sealed class Permission
{
    [Key] public Guid PermissionId { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Code { get; set; } = default!;
    [MaxLength(300)] public string? Description { get; set; }
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}

public sealed class App
{
    [Key] public Guid AppId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(100)] public string Key { get; set; } = default!;
    [MaxLength(200)] public string Name { get; set; } = default!;
    public AppStatus Status { get; set; } = AppStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// RENAMED to avoid clash with System.Environment
public sealed class AppEnvironment
{
    [Key] public Guid EnvId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(120)] public string Name { get; set; } = default!;
    public EnvironmentType Type { get; set; } = EnvironmentType.DEV;
    [MaxLength(300)] public string? BaseUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EnvironmentApp
{
    [Key] public Guid EnvAppId { get; set; } = Guid.NewGuid();
    public Guid EnvId { get; set; }
    public Guid AppId { get; set; }
    public Guid? ActiveAppVersionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AppDraft
{
    [Key] public Guid DraftId { get; set; } = Guid.NewGuid();
    public Guid AppId { get; set; }
    public int Version { get; set; } = 1;
    public DraftStatus Status { get; set; } = DraftStatus.Editing;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DraftResource
{
    [Key] public Guid ResourceId { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    [MaxLength(50)] public string Type { get; set; } = default!; // UI_PAGE, FLOW, ENTITY_SCHEMA, SETTING
    [MaxLength(200)] public string Key { get; set; } = default!;  // e.g. Home
    public string JsonPayload { get; set; } = default!;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// =========================
// Studio (Service Studio-like)
// =========================

public sealed class StudioEntity
{
    [Key] public Guid EntityId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AppId { get; set; }
    [MaxLength(120)] public string Name { get; set; } = default!;
    [MaxLength(200)] public string TableName { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StudioEntityAttribute
{
    [Key] public Guid AttributeId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AppId { get; set; }
    public Guid EntityId { get; set; }
    [MaxLength(120)] public string Name { get; set; } = default!;
    [MaxLength(30)] public string Type { get; set; } = "string"; // string,int,bool,datetime,decimal
    public int? Length { get; set; }
    public bool Required { get; set; } = false;
    public int Ordinal { get; set; } = 0;
}

public sealed class AppVersion
{
    [Key] public Guid AppVersionId { get; set; } = Guid.NewGuid();
    public Guid AppId { get; set; }
    [MaxLength(50)] public string SemVer { get; set; } = "0.1.0";
    public int BuildNo { get; set; } = 1;
    public VersionStatus Status { get; set; } = VersionStatus.Published;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid SourceDraftId { get; set; }
}

public sealed class Package
{
    [Key] public Guid PackageId { get; set; } = Guid.NewGuid();
    public Guid AppVersionId { get; set; }
    [MaxLength(500)] public string StoragePath { get; set; } = default!;
    [MaxLength(128)] public string Sha256 { get; set; } = default!;
    public long SizeBytes { get; set; }
    public string ManifestJson { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Deployment
{
    [Key] public Guid DeploymentId { get; set; } = Guid.NewGuid();
    public Guid EnvId { get; set; }
    public Guid AppVersionId { get; set; }

    /// <summary>Attempt counter starting from 1. Retry creates a new Deployment with Attempt+1.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>If this deployment is a retry, points to the original deployment.</summary>
    public Guid? RetryOfDeploymentId { get; set; }

    /// <summary>When true, the worker should stop and mark Canceled.</summary>
    public bool CancelRequested { get; set; } = false;

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public Guid RequestedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Optional last error / summary string for quick display.</summary>
    [MaxLength(2000)] public string? LogRef { get; set; }
}


public sealed class DeploymentLog
{
    [Key] public Guid LogId { get; set; } = Guid.NewGuid();
    public Guid DeploymentId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(20)] public string Level { get; set; } = "INFO";
    [MaxLength(2000)] public string Message { get; set; } = default!;
}

public sealed class AuditLog
{
    [Key] public Guid AuditId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    [MaxLength(120)] public string Action { get; set; } = default!;
    [MaxLength(120)] public string EntityType { get; set; } = default!;
    [MaxLength(120)] public string EntityId { get; set; } = default!;
    [MaxLength(1000)] public string Summary { get; set; } = default!;
    [MaxLength(60)] public string? Ip { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
