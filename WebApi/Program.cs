using System.Text;
using System.Text.Json.Serialization;
using Common.Caching;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Seeding;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.CardCache;
using WebApi.Enrollments;
using WebApi.Filters;
using WebApi.Middleware;
using WebApi.Observability;
using WebApi.Redis;
using WebApi.Schedule;
using WebApi.Waitlist;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// решение: число в кавычках — 400: веб-настройки System.Text.Json читают числа из строк, а в контракте поля числовые.
builder.Services.AddControllers(options => options.Filters.Add<TransactionConflictFilter>())
    .AddJsonOptions(options => options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddClubReadinessChecks();
builder.Services.AddSingleton<ClubMetrics>();

builder.Services.AddDbContext<ClubDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<TransactionRunner>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<WaitlistService>();
// решение: lock_timeout — от 1 мс (Postgres считает в целых мс, 0 — без предела) и меньше 30 с
// (CommandTimeout Npgsql прервал бы ожидание раньше, и вместо 503 был бы 500).
builder.Services.AddOptions<TransactionSettings>()
    .BindConfiguration(TransactionSettings.Section)
    .Validate(
        x => x.LockTimeout >= TimeSpan.FromMilliseconds(1) && x.LockTimeout < TimeSpan.FromSeconds(30),
        "Transactions:LockTimeout must be at least 1 ms and less than 30 s.")
    .ValidateOnStart();

builder.Services.AddSingleton(_ => RedisConnection.Connect(builder.Configuration.GetConnectionString("Redis")!));
// решение: предел вызова Redis меньше 5 с — иначе раньше него сработает AsyncTimeout клиента.
builder.Services.AddOptions<RedisSettings>()
    .BindConfiguration(RedisSettings.Section)
    .Validate(
        x => x.OperationTimeout >= TimeSpan.FromMilliseconds(1) && x.OperationTimeout < TimeSpan.FromSeconds(5),
        "Redis:OperationTimeout must be at least 1 ms and less than 5 s.")
    .ValidateOnStart();
builder.Services.AddSingleton<RedisGuard>();

builder.Services.AddOptions<CardCacheSettings>()
    .BindConfiguration(CardCacheSettings.Section)
    .Validate(
        x => x.CardTtl >= TimeSpan.FromSeconds(1) && x.CardTtl <= TimeSpan.FromHours(1),
        "Cache:CardTtl must be at least 1 s and at most 1 h.")
    .ValidateOnStart();
builder.Services.AddSingleton<ICardCache, CardCache>();

builder.Services.AddSingleton<ICacheService>(provider =>
    new RedisCacheService(provider.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromMinutes(5)));

builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<AccessTokenService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = x => x.Tags.Contains(ReadinessChecks.Tag) })
    .AllowAnonymous();

app.Services.GetRequiredService<RedisGuard>().ReportStart(app.Services.GetRequiredService<IConnectionMultiplexer>());

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();

    await dbContext.Database.MigrateAsync();
    await DatabaseSeeder.SeedAsync(dbContext);
}

app.Run();

public partial class Program;
