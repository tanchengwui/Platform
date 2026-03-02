using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Data;

public sealed class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<App> Apps => Set<App>();
    public DbSet<AppEnvironment> Environments => Set<AppEnvironment>(); // keep name Environments
    public DbSet<EnvironmentApp> EnvironmentApps => Set<EnvironmentApp>();
    public DbSet<AppDraft> AppDrafts => Set<AppDraft>();
    public DbSet<DraftResource> DraftResources => Set<DraftResource>();

    // Studio
    public DbSet<StudioEntity> StudioEntities => Set<StudioEntity>();
    public DbSet<StudioEntityAttribute> StudioEntityAttributes => Set<StudioEntityAttribute>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(x => new { x.TenantId, x.Email }).IsUnique();

        modelBuilder.Entity<RefreshToken>().HasIndex(x => new { x.UserId, x.TokenHash }).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(x => new { x.TenantId, x.UserId });

        modelBuilder.Entity<UserRole>().HasKey(x => new { x.UserId, x.RoleId });
        modelBuilder.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.PermissionId });

        modelBuilder.Entity<App>().HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        modelBuilder.Entity<AppEnvironment>().HasIndex(x => new { x.TenantId, x.Name }).IsUnique();

        modelBuilder.Entity<DraftResource>().HasIndex(x => new { x.DraftId, x.Type, x.Key }).IsUnique();
        modelBuilder.Entity<EnvironmentApp>().HasIndex(x => new { x.EnvId, x.AppId }).IsUnique();

        modelBuilder.Entity<DeploymentLog>().HasIndex(x => new { x.DeploymentId, x.Timestamp });

        modelBuilder.Entity<StudioEntity>().HasIndex(x => new { x.TenantId, x.AppId, x.Name }).IsUnique();
        modelBuilder.Entity<StudioEntityAttribute>().HasIndex(x => new { x.TenantId, x.AppId, x.EntityId, x.Name }).IsUnique();
    }
}
