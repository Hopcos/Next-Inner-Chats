using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NextChats.Api;
using NextChats.Core.Configuration;
using NextChats.Infrastructure;
using NextChats.Infrastructure.Data;
using Serilog;
using Serilog.Events;

// ================= Serilog（日志脱敏由托管 provider 统一处理） =================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/nextchats-.log", rollingInterval: RollingInterval.Day, outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

var securityOptions = builder.Configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
if (string.IsNullOrWhiteSpace(securityOptions.JwtKey))
{
    securityOptions.JwtKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    Log.Warning("未配置 Security:JwtKey，已生成随机临时密钥（重启后旧 Token 失效）");
}

builder.Services.Configure<SecurityOptions>(o =>
{
    o.EncryptionKey = securityOptions.EncryptionKey;
    o.JwtKey = securityOptions.JwtKey;
    o.JwtIssuer = securityOptions.JwtIssuer;
    o.JwtAudience = securityOptions.JwtAudience;
    o.JwtExpireMinutes = securityOptions.JwtExpireMinutes;
    o.ProceedOnInjection = securityOptions.ProceedOnInjection;
});

builder.Services.AddOptions<NextChats.Core.Configuration.PolicyOptions>().Bind(builder.Configuration.GetSection("Policy"));
builder.Services.AddOptions<NextChats.Core.Configuration.ContextOptions>().Bind(builder.Configuration.GetSection("Context"));
builder.Services.AddOptions<NextChats.Core.Configuration.ToolTrimOptions>().Bind(builder.Configuration.GetSection("ToolTrim"));
builder.Services.AddOptions<NextChats.Core.Configuration.BuiltinToolOptions>().Bind(builder.Configuration.GetSection("BuiltinTool"));

builder.Services.AddNextChatsInfrastructure(builder.Configuration);

// ================= 认证与授权（JWT + 角色隔离） =================
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 保持原始声明类型（"role"/"uid"），否则 JwtSecurityTokenHandler 默认映射会改写声明
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = securityOptions.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = securityOptions.JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(NextChats.Api.Security.JwtTokenFactory.GetKey(securityOptions)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", p => p.RequireClaim("role", "admin"));
});

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        // 枚举以字符串序列化（前端筛选/显示依赖 "Pending"/"Approved" 等字符串）
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddCors(o => o.AddPolicy("web", p =>
{
    var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"];
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// ================= 初始化数据库 + 种子数据 =================
// SQLite 相对路径基于进程工作目录解析，先确保 data 目录存在
Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "data"));
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data"));
await app.Services.InitializeDatabaseAsync();
await app.Services.SeedAsync(app.Logger);

app.UseMiddleware<NextChats.Api.Middleware.ApiErrorMiddleware>();
app.UseSerilogRequestLogging(o =>
{
    o.EnrichDiagnosticContext = (diag, http) =>
    {
        diag.Set("UserId", http.User.FindFirstValue("uid"));
        diag.Set("TraceId", http.TraceIdentifier);
    };
});
// 前端静态资源（wwwroot 下的 dist 产物）：默认文档 index.html + 静态文件，需在认证之前
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Json(new { status = "ok", time = DateTimeOffset.UtcNow }));
// SPA 回退：非 /api 的未知路径一律返回 index.html（前端路由由 Vue Router 接管）
app.MapFallbackToFile("index.html");

app.Run();

namespace NextChats.Api
{
    public partial class Program
    {
    }
}
