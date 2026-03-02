using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Data;
using Platform.Api.Security;
using Platform.Api.Options;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler(options => { });

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Platform.Api", Version = "v1" });
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<RefreshTokenService>();

builder.Services.AddDbContext<PlatformDbContext>(opt =>
{
    opt.UseSqlServer(builder.Configuration.GetConnectionString("PlatformDb"));
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("APP_CREATE", p => p.RequireClaim("perm", "APP_CREATE"));
    options.AddPolicy("APP_PUBLISH", p => p.RequireClaim("perm", "APP_PUBLISH"));
    options.AddPolicy("ENV_DEPLOY", p => p.RequireClaim("perm", "ENV_DEPLOY"));
    options.AddPolicy("USER_ADMIN", p => p.RequireClaim("perm", "USER_ADMIN"));
    options.AddPolicy("AUDIT_VIEW", p => p.RequireClaim("perm", "AUDIT_VIEW"));
});

builder.Services.Configure<ArtifactOptions>(builder.Configuration.GetSection("Artifacts"));

// Use a concrete options instance for simple services (keeps your existing pattern)
var artifactOptions = builder.Configuration.GetSection("Artifacts").Get<ArtifactOptions>() ?? new ArtifactOptions();
builder.Services.AddSingleton(artifactOptions);

builder.Services.AddSingleton<Platform.Api.Storage.IArtifactStorage>(sp =>
{
    var opt = sp.GetRequiredService<ArtifactOptions>();
    return opt.Provider.Equals("S3", StringComparison.OrdinalIgnoreCase)
        ? new Platform.Api.Storage.S3ArtifactStorage(opt)
        : new Platform.Api.Storage.FileSystemArtifactStorage(opt);
});

// ✅ REQUIRED for PublishController (PackageBuilder root path)

var app = builder.Build();

// ---- DB migrate + seed (roles/permissions/default tenant) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.MigrateAsync();
    await Seeder.SeedAsync(db);
}


app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
