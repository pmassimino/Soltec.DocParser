using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Soltec.DocParser.Endpoints;
using Soltec.DocParser.Services;
using Soltec.DocParser.Tenancy;
using Soltec.DocParser.Uso;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Autenticación delegada en Soltec.Suscripcion: el cliente hace login allá (POST /api/login) y
// manda el JWT como "Authorization: Bearer". Acá se valida localmente con la clave compartida.
var suscripcion = builder.Configuration.GetSection("Suscripcion").Get<SuscripcionOptions>() ?? new();
if (string.IsNullOrEmpty(suscripcion.Jwt.Key) || string.IsNullOrEmpty(suscripcion.UrlService))
    throw new InvalidOperationException("Falta configurar Suscripcion:UrlService y Suscripcion:Jwt:Key (la misma clave Jwt:Key de Soltec.Suscripcion).");
builder.Services.Configure<SuscripcionOptions>(builder.Configuration.GetSection("Suscripcion"));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidIssuer = suscripcion.Jwt.Issuer,
        ValidAudience = suscripcion.Jwt.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(suscripcion.Jwt.Key)),
        NameClaimType = "name",
        RoleClaimType = "role"
    };
});
// Todo endpoint requiere token salvo los marcados con AllowAnonymous (p.ej. /api/health).
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(nameof(SuscripcionMiddleware), c => c.Timeout = TimeSpan.FromSeconds(15));

// Registro de uso por cliente (facturas procesadas, IA, tokens, costo) en SQLite. Una ruta
// relativa en ConnectionStrings:Uso se toma desde la carpeta de la app, no desde el directorio
// de trabajo del proceso (que bajo IIS o un servicio no es la carpeta de la app).
var usoConnection = new SqliteConnectionStringBuilder(builder.Configuration.GetConnectionString("Uso") ?? "Data Source=Data/uso.db");
if (!Path.IsPathRooted(usoConnection.DataSource))
    usoConnection.DataSource = Path.Combine(builder.Environment.ContentRootPath, usoConnection.DataSource);
Directory.CreateDirectory(Path.GetDirectoryName(usoConnection.DataSource)!);
builder.Services.AddDbContext<UsoDbContext>(o => o.UseSqlite(usoConnection.ToString()));
builder.Services.Configure<UsoOptions>(builder.Configuration.GetSection("Uso"));
builder.Services.AddScoped<IRegistroUsoService, RegistroUsoService>();
builder.Services.AddScoped<IUsoEstadisticasService, UsoEstadisticasService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UsoDbContext>();
    await db.Database.MigrateAsync();
    // WAL: los reportes pueden leer mientras se graban registros de otros requests.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

OcrExtraction.Configurar(Path.Combine(app.Environment.ContentRootPath, "tessdata"));
AiExtraction.Configurar(
    builder.Configuration["Anthropic:ApiKey"],
    builder.Configuration["Anthropic:Modelo"]);
DeepSeekExtraction.Configurar(
    builder.Configuration["DeepSeek:ApiKey"],
    builder.Configuration["DeepSeek:Modelo"],
    builder.Configuration["DeepSeek:BaseUrl"],
    builder.Configuration.GetValue("DeepSeek:EnviarImagenes", true),
    builder.Configuration.GetValue("DeepSeek:MaxPaginas", 3));
IaFallback.Configurar(builder.Configuration.GetSection("Ia:Orden").Get<string[]>());

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<SuscripcionMiddleware>();

app.MapFacturaCompraEndpoints();
app.MapUsoAdminEndpoints();

app.Run();
