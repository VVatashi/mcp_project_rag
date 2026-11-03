using System.Reflection;

using ModelContextProtocol.Protocol;

using ProjectRag.Core;
using ProjectRag.Core.Storage;
using ProjectRag.Server;

// The local host of the knowledge base: the web API under /api, the MCP endpoint /mcp and the dashboard.
// The content root is the output folder, so the program finds its settings and the dashboard from any working folder.
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ApplicationName = "ProjectRag.Server",
    ContentRootPath = AppContext.BaseDirectory,
});

// The settings of one machine go to appsettings.Local.json, which Git ignores.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
builder.Services.AddKnowledgeBase(builder.Configuration);
builder.Services.AddProblemDetails();

string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "project-rag", Title = "ProjectRag knowledge base", Version = version };
        options.ServerInstructions = KnowledgeTools.Instructions;
    })
    // The tools need no state between the calls. Without sessions, a client works on after a restart of the server.
    .WithHttpTransport(static options => options.Stateless = true)
    .WithTools<KnowledgeTools>();

WebApplication app = builder.Build();

// A database that is down or old stops the start with the exit code 1, before the server opens its port.
try
{
    DatabaseMigrator.MigrateUp(builder.Configuration.GetConnectionString(Database.ConnectionStringName)!, app.Services.GetRequiredService<ILoggerFactory>());
}
#pragma warning disable CA1031
catch (Exception exception)
#pragma warning restore CA1031
{
    app.Logger.LogError(exception, "The migration of the database failed. Start the database with \"docker compose up -d\" and check ConnectionStrings:ProjectRag.");
    return 1;
}

app.UseExceptionHandler();
app.UseTokenCheck();
app.UseDefaultFiles();
// The bundles of the dashboard carry a hash in their names. The page that names them must not stay in the cache after an update.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = static context =>
    {
        if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache";
    },
});
app.MapKnowledgeApi();
app.MapMcp("/mcp");

// The dashboard routes its pages in the browser, so the server answers each of its addresses with the page.
// An unknown address of the web API stays an error.
app.Map("/api/{**path}", static () => Results.NotFound());

if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
    app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = static context => context.Context.Response.Headers.CacheControl = "no-cache" });
else
    app.MapGet("/", static () => "The dashboard is not built. Run \"npm run build\" in src/ProjectRag.Dashboard.");

app.Logger.LogInformation("ProjectRag {Version} serves the dashboard on {Urls}, the web API under /api and MCP on /mcp.", version, string.Join(", ", app.Urls.DefaultIfEmpty(builder.Configuration["Urls"] ?? string.Empty)));
await app.RunAsync();
return 0;
