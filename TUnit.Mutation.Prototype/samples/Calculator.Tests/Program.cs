using Microsoft.Testing.Platform.Builder;
using TUnit.Engine.Extensions;

var builder = await TestApplication.CreateBuilderAsync(args);
builder.AddSelfRegisteredExtensions(args);
builder.AddTUnit();
using var app = await builder.BuildAsync();
return await app.RunAsync();
