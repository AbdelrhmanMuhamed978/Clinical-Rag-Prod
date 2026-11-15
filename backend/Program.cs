using ClinicalRag.Api.Data;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});
// SDK transport/exception logs can contain URLs, header values or rejected input. Use our fixed summaries instead.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Critical);
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None); // Framework request-start logs contain raw paths/queries.
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.None);

builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    var original = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
        context.HttpContext.Request.Path.Value?.EndsWith("/rules/preview", StringComparison.OrdinalIgnoreCase) == true
            ? new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(new { error = "Invalid preview request. Check the definition and input lengths." })
            : context.HttpContext.Request.Path.StartsWithSegments("/api/admin")
                ? new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(new { error = "Invalid operations request. Check the required fields and input lengths." })
                : original(context);
});
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddDbContext<AppDbContext>(o =>
{
    if (builder.Configuration.GetValue<bool>("ClinicalRag:UseInMemory"))
        o.UseInMemoryDatabase("ClinicalRagTest");
    else
        o.UseSqlServer(builder.Configuration.GetConnectionString("Default"));
});

builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
var keyRing = builder.Configuration["DataProtection:KeyRingPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "key-ring");
var protection = builder.Services.AddDataProtection().SetApplicationName("ClinicalRag.Platform")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRing));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<ColabEndpointPolicy>();
builder.Services.AddScoped<ColabConnectionStore>();
builder.Services.AddScoped<IColabConnectionProvider>(services => services.GetRequiredService<ColabConnectionStore>());
builder.Services.AddSingleton<ColabCircuitBreaker>();
builder.Services.AddSingleton<IColabRetryDelay, ColabRetryDelay>();
builder.Services.AddTransient<ColabResilienceHandler>();
builder.Services.AddHttpClient<IColabClient, ColabClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(services => services.GetRequiredService<ColabEndpointPolicy>().CreateHandler())
    .AddHttpMessageHandler<ColabResilienceHandler>();
builder.Services.AddHttpClient("Pipeline", http => http.Timeout = Timeout.InfiniteTimeSpan).RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) });
builder.Services.AddScoped<ReadinessService>();
builder.Services.AddSingleton<ChunkVectorCache>();
builder.Services.AddScoped<ChunkSearchService>();
builder.Services.AddScoped<EmbeddingBatchClient>();
builder.Services.AddScoped<DocumentEmbeddingService>();
builder.Services.AddSingleton<PipelineClient>();
builder.Services.AddSingleton<IPipelineClient>(services => services.GetRequiredService<PipelineClient>());
builder.Services.AddSingleton<IRulePreviewClient>(services => services.GetRequiredService<PipelineClient>());
builder.Services.AddScoped<RuleService>();
builder.Services.AddScoped<IConfigService, ConfigService>();
builder.Services.AddScoped<IConfigTestRunProvider, NoConfigTestRunProvider>();
builder.Services.AddScoped<TestHarnessService>();
builder.Services.AddScoped<IFirebaseTokenValidator, FirebaseTokenValidator>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<DocumentParser>();
if (builder.Configuration.GetValue<bool>("Documents:AutomaticParsing", true))
    builder.Services.AddHostedService<DocumentParsingWorker>();
if (builder.Configuration.GetValue<bool>("Documents:AutomaticFileCleanup", true))
    builder.Services.AddHostedService<UploadCleanupWorker>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(RequestCorrelation.Header, "Retry-After")));

var firebaseProjectId = builder.Configuration["Firebase:ProjectId"];
if (!string.IsNullOrWhiteSpace(firebaseProjectId))
{
    builder.Services.AddAuthentication("Bearer").AddJwtBearer("Bearer", o =>
    {
        o.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
        o.Audience = firebaseProjectId;
        o.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://securetoken.google.com/{firebaseProjectId}",
            ValidateAudience = true,
            ValidAudience = firebaseProjectId,
            ValidateLifetime = true
        };
    });
}
else
{
    // Dev mode: no Firebase configured -> allow anonymous (for local testing)
    builder.Services.AddAuthentication("Dev").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthHandler>("Dev", _ => { });
}
builder.Services.AddAuthorization(options => options.AddPolicy(PlatformAdmin.Policy, policy =>
    policy.RequireAuthenticatedUser().RequireAssertion(context => PlatformAdmin.IsAllowed(context.User, builder.Configuration))));

var app = builder.Build();
app.UseMiddleware<RequestObservabilityMiddleware>();

app.Use(async (context, next) =>
{
    // Include automatic validation/authentication errors, not only successful previews.
    if (context.Request.Path.Value?.EndsWith("/rules/preview", StringComparison.OrdinalIgnoreCase) == true)
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Apply migrations on startup only when explicitly opted in. Defaults to true in
// Development so a fresh local database reaches the current schema automatically.
// Production must NOT auto-migrate at runtime; apply migrations via a generated
// SQL script (dotnet ef migrations script --idempotent) during deployment.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var autoMigrate = app.Configuration.GetValue<bool>("Database:AutoMigrate",
        string.Equals(app.Environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase));
    // InMemory databases are created fresh per scope and do not support migrations;
    // only run Migrate() against a real relational provider (SQL Server).
    if (autoMigrate && !string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
    {
        try { db.Database.Migrate(); }
        catch (Exception exception)
        {
            app.Logger.LogCritical("Database migration failed {ExceptionType}", exception.GetType().Name);
            throw new InvalidOperationException("Database migration failed. Check deployment connectivity and migration permissions.");
        }
    }
}

app.Run();
