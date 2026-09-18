using Soltec.FacturaParser.Endpoints;
using Soltec.FacturaParser.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<List<TenantOptions>>(builder.Configuration.GetSection("Tenants"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<TenantApiKeyMiddleware>();

app.MapFacturaParserEndpoints();

app.Run();
