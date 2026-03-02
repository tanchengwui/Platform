using Microsoft.EntityFrameworkCore;
using Platform.Api.Security;

namespace Platform.Api.Data;

public static class Seeder
{
    public static async Task SeedAsync(PlatformDbContext db)
    {
        // ---- Permissions (upsert by Code) ----
        var permissions = new[]
        {
            new Permission { Code = "APP_CREATE",  Description = "Create apps" },
            new Permission { Code = "APP_PUBLISH", Description = "Create drafts / publish versions" },
            new Permission { Code = "ENV_DEPLOY",  Description = "Deploy to environments" },
            new Permission { Code = "USER_ADMIN",  Description = "Manage users/roles" },
            new Permission { Code = "AUDIT_VIEW",  Description = "View audit logs" }
        };

        foreach (var p in permissions)
        {
            var exists = await db.Permissions.AnyAsync(x => x.Code == p.Code);
            if (!exists) db.Permissions.Add(p);
        }
        await db.SaveChangesAsync();

        // ---- Default tenant ----
        Tenant tenant;
        if (!await db.Tenants.AnyAsync())
        {
            tenant = new Tenant { Name = "DefaultTenant" };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }
        else
        {
            tenant = await db.Tenants.OrderBy(x => x.CreatedAt).FirstAsync();
        }

        // ---- Roles (Admin/Developer/Viewer) ----
        async Task<Role> EnsureRoleAsync(string roleName)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.Name == roleName);
            if (role is not null) return role;

            role = new Role { TenantId = tenant.TenantId, Name = roleName };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return role;
        }

        var adminRole = await EnsureRoleAsync("Admin");
        var devRole   = await EnsureRoleAsync("Developer");
        var viewRole  = await EnsureRoleAsync("Viewer");

        var permByCode = await db.Permissions.ToDictionaryAsync(p => p.Code, p => p.PermissionId);

        // ---- Role → Permission mapping ----
        // Admin: all
        var adminPerms = permByCode.Keys.ToArray();

        // Developer: can create apps, publish, deploy, view audit
        var devPerms = new[] { "APP_CREATE", "APP_PUBLISH", "ENV_DEPLOY", "AUDIT_VIEW" };

        // Viewer: audit only (and can read apps via [Authorize] endpoints)
        var viewerPerms = new[] { "AUDIT_VIEW" };

        async Task EnsureRolePermsAsync(Role role, IEnumerable<string> codes)
        {
            var wanted = codes.Select(c => permByCode[c]).ToHashSet();

            var existing = await db.RolePermissions
                .Where(x => x.RoleId == role.RoleId)
                .Select(x => x.PermissionId)
                .ToListAsync();

            // add missing
            foreach (var pid in wanted.Except(existing))
                db.RolePermissions.Add(new RolePermission { RoleId = role.RoleId, PermissionId = pid });

            // remove extras (keeps mapping clean)
            foreach (var pid in existing.Except(wanted))
            {
                var rp = await db.RolePermissions.FirstAsync(x => x.RoleId == role.RoleId && x.PermissionId == pid);
                db.RolePermissions.Remove(rp);
            }

            await db.SaveChangesAsync();
        }

        await EnsureRolePermsAsync(adminRole, adminPerms);
        await EnsureRolePermsAsync(devRole, devPerms);
        await EnsureRolePermsAsync(viewRole, viewerPerms);

        // ---- Default admin user ----
        var adminUser = await db.Users.FirstOrDefaultAsync(u => u.TenantId == tenant.TenantId && u.Email == "admin@local");
        if (adminUser is null)
        {
            adminUser = new User
            {
                TenantId = tenant.TenantId,
                Email = "admin@local",
                PasswordHash = PasswordHasher.Hash("Admin123!"),
                IsActive = true
            };
            db.Users.Add(adminUser);
            await db.SaveChangesAsync();
        }

        var hasAdminRole = await db.UserRoles.AnyAsync(ur => ur.UserId == adminUser.UserId && ur.RoleId == adminRole.RoleId);
        if (!hasAdminRole)
        {
            db.UserRoles.Add(new UserRole { UserId = adminUser.UserId, RoleId = adminRole.RoleId });
            await db.SaveChangesAsync();
        }

        // ---- Seed DEV environment ----
        var hasDevEnv = await db.Environments.AnyAsync(e => e.TenantId == tenant.TenantId && e.Name == "DEV");
        if (!hasDevEnv)
        {
            db.Environments.Add(new AppEnvironment
            {
                TenantId = tenant.TenantId,
                Name = "DEV",
                Type = EnvironmentType.DEV,
                BaseUrl = "http://localhost:5225"
            });
            await db.SaveChangesAsync();
        }
    }
}
