using Radiostations.Components;
using Radiostations.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<SQLiteProvider>();
builder.Services.AddScoped<MySqlProvider>();
builder.Services.AddScoped<MssqlProvider>();
builder.Services.AddScoped<PostgresProvider>();
builder.Services.AddScoped<XmlParserService>();
builder.Services.AddScoped<SystemTestingService>();
builder.WebHost.UseStaticWebAssets();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
