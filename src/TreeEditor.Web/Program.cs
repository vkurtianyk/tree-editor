using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using TreeEditor.Web;
using TreeEditor.Web.Api;
using TreeEditor.Web.Cache;
using TreeEditor.Web.Trees;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

// Same origin as the Api that serves this app: no CORS, no service discovery in the browser.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<TreeApiClient>();
builder.Services.AddScoped<ICacheApiClient>(services => services.GetRequiredService<TreeApiClient>());

// One cache per browser tab, in memory only: a page refresh starts empty.
builder.Services.AddScoped<LocalCache>();

// Lets the toolbar's Reset and Apply reload DBTreeView from the roots.
builder.Services.AddScoped<DbTreeReload>();

await builder.Build().RunAsync();
