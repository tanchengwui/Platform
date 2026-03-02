using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Options;
using Platform.Api.Storage;
using Platform.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Same connection string as API
builder.Services.AddDbContext<PlatformDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("PlatformDb")));

// Artifact storage (same as API)
builder.Services.Configure<ArtifactOptions>(builder.Configuration.GetSection("Artifacts"));
var artifactOptions = builder.Configuration.GetSection("Artifacts").Get<ArtifactOptions>() ?? new ArtifactOptions();
builder.Services.AddSingleton(artifactOptions);

builder.Services.AddSingleton<IArtifactStorage>(sp =>
{
    var opt = sp.GetRequiredService<ArtifactOptions>();
    return opt.Provider.Equals("S3", StringComparison.OrdinalIgnoreCase)
        ? new S3ArtifactStorage(opt)
        : new FileSystemArtifactStorage(opt);
});

builder.Services.AddHttpClient("RuntimeSync", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHostedService<DeploymentWorker>();

var app = builder.Build();
await app.RunAsync();
