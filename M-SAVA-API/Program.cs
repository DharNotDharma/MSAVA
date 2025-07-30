﻿using M_SAVA_API.Filters;
using M_SAVA_API.Handlers;
using M_SAVA_API.Middleware;
using M_SAVA_BLL.Services;
using M_SAVA_BLL.Services.Interfaces;
using M_SAVA_DAL.Contexts;
using M_SAVA_DAL.Models;
using M_SAVA_DAL.Repositories;
using M_SAVA_INF.Environment;
using M_SAVA_INF.Managers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Text.Json;
using Serilog;
using Serilog.Events;
using M_SAVA_BLL.Loggers;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Information)
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithProcessId()
    .WriteTo.Async(a => a.File(
        path: "Logs/serilog-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: null,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"
    ))
    .CreateLogger();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

// Register controllers
builder.Services.AddControllers(options =>
{
    options.Filters.Add<TaintedPathFilter>();
});

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new NotBannedRequirement())
        .Build();
});

// Swagger setup WITH JWT SUPPORT
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "M-SAVA-API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });

    c.OperationFilter<OctetStreamOperationFilter>();
    c.OperationFilter<AuthorizeCheckOperationFilter>();
});

// Register local environment
builder.Services.AddSingleton<ILocalEnvironment, LocalEnvironment>();
var env = LocalEnvironment.Instance;

// Register main database context
string baseDbConnectionString = $"Host={env.GetValue("postgres_basedb_host")};Port={env.GetValue("postgres_basedb_port")};Database={env.GetValue("postgres_basedb_dbname")};Username={env.GetValue("postgres_basedb_user")};Password={env.GetValue("postgres_basedb_password")};Ssl Mode={env.GetValue("postgres_basedb_ssl_mode")}";
builder.Services.AddDbContext<BaseDataContext>(options =>
    options.UseNpgsql(baseDbConnectionString));

// Register IHttpContextAccessor
builder.Services.AddHttpContextAccessor();

// Register services
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IReturnFileService, ReturnFileService>();
builder.Services.AddScoped<ISaveFileService, SaveFileService>();
builder.Services.AddScoped<ILoginService, LoginService>();
builder.Services.AddScoped<ISearchFileService, SearchFileService>();
builder.Services.AddScoped<ISeedingService, SeedingService>();
builder.Services.AddScoped<AccessGroupService>();
builder.Services.AddScoped<InviteCodeService>();

// Register custom loggers
builder.Services.AddScoped<ServiceLogger>();

// Register singletons
builder.Services.AddSingleton<IAuthorizationHandler, NotBannedHandler>();

// Register repositories
builder.Services.AddScoped<IIdentifiableRepository<UserDB>, IdentifiableRepository<UserDB>>();
builder.Services.AddScoped<IIdentifiableRepository<InviteCodeDB>, IdentifiableRepository<InviteCodeDB>>();
builder.Services.AddScoped<IIdentifiableRepository<AccessCodeDB>, IdentifiableRepository<AccessCodeDB>>();
builder.Services.AddScoped<IIdentifiableRepository<AccessGroupDB>, IdentifiableRepository<AccessGroupDB>>();
builder.Services.AddScoped<IIdentifiableRepository<JwtDB>, IdentifiableRepository<JwtDB>>();
builder.Services.AddScoped<IIdentifiableRepository<SavedFileDataDB>, IdentifiableRepository<SavedFileDataDB>>();
builder.Services.AddScoped<IIdentifiableRepository<SavedFileReferenceDB>, IdentifiableRepository<SavedFileReferenceDB>>();
// Log repositories
builder.Services.AddScoped<IIdentifiableRepository<UserLogDB>, IdentifiableRepository<UserLogDB>>();
builder.Services.AddScoped<IIdentifiableRepository<AccessLogDB>, IdentifiableRepository<AccessLogDB>>();
builder.Services.AddScoped<IIdentifiableRepository<ErrorLogDB>, IdentifiableRepository<ErrorLogDB>>();
builder.Services.AddScoped<IIdentifiableRepository<GroupLogDB>, IdentifiableRepository<GroupLogDB>>();

// Register managers
builder.Services.AddScoped<FileManager>();

// JWT Authentication setup
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = env.GetValue("jwt_issuer_name"),
            ValidateAudience = true,
            ValidAudience = env.GetValue("jwt_issuer_audience"),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero, 
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(LocalEnvironment.Instance.GetSigningKeyBytes())
        };
    });

WebApplication app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "Handled {RequestPath}";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
    };
});

app.UseRouting();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "Data")),
    RequestPath = "/api/files/public",
    OnPrepareResponse = ctx =>
    {
        var filePath = ctx.File.PhysicalPath;
        var metaPath = filePath + ".meta.json";

        if (!System.IO.File.Exists(metaPath))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Context.Abort();
            return;
        }

        try
        {
            var metaJson = System.IO.File.ReadAllText(metaPath);
            var metaDoc = JsonDocument.Parse(metaJson);
            bool anyPublic = false;
            if (metaDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in metaDoc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("PublicDownload", out var publicProp) && publicProp.GetBoolean())
                    {
                        anyPublic = true;
                        break;
                    }
                }
            }
            if (!anyPublic)
            {
                ctx.Context.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Context.Abort();
            }
        }
        catch
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Context.Abort();
        }
    }
});

app.UseMiddleware<ExceptionCatcherMiddleware>();

app.UseAuthentication();
app.UseMiddleware<RequestContextMiddleware>();
app.UseAuthorization();

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<ISeedingService>();
    seeder.Seed();
}

app.Run();
