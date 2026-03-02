using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Platform.ServiceCenter;
using Platform.ServiceCenter.Components;
using Platform.ServiceCenter.Services;

var builder = WebApplication.CreateBuilder(args);

// Razor Components (Blazor Web App - Interactive Server)
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

// ✅ Anti-forgery services (required when endpoints carry antiforgery metadata)
builder.Services.AddAntiforgery();

// ✅ Protected storage (token persistence)
builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<ProtectedSessionStorage>();

// ✅ IMPORTANT: Register SimpleAuthStateProvider ONCE,
// and bind AuthenticationStateProvider to the SAME instance.
builder.Services.AddScoped<SimpleAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<SimpleAuthStateProvider>());

// ✅ Blazor authorization (component-level). We intentionally do NOT enable ASP.NET
// server-side auth middleware, because ServiceCenter uses token-based UI auth.
builder.Services.AddAuthorizationCore();

// App services
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<ApiClient>();

// HttpClient for API
builder.Services.AddHttpClient("api", http =>
{
    var baseUrl =
        builder.Configuration.GetValue<string>("PlatformApiBaseUrl")
        ?? builder.Configuration["Api:BaseUrl"]
        ?? "http://localhost:5002";
    http.BaseAddress = new Uri(baseUrl);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// If you are only running http profile, do NOT force https redirect in dev
// app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

// ✅ If any endpoint uses antiforgery metadata
app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();
