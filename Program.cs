using Soltec.DocParser.Endpoints;
using Soltec.DocParser.Services;
using Soltec.DocParser.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<List<TenantOptions>>(builder.Configuration.GetSection("Tenants"));

var app = builder.Build();

OcrExtraction.Configurar(Path.Combine(app.Environment.ContentRootPath, "tessdata"));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<TenantApiKeyMiddleware>();

app.MapFacturaCompraEndpoints();

app.Run();
