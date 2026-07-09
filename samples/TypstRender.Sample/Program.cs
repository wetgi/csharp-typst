using Scalar.AspNetCore;
using TypstRender.Sample;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddTypstSampleRendering();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapSampleRenderEndpoints();

app.Run();
