﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodeDBSyncer;

var builder = Host.CreateDefaultBuilder(args);

var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{environmentName}.json", true, true)
    .AddEnvironmentVariables()
    .Build();

// Add services to the container.
builder.ConfigureServices(services =>
{
    services.Configure<AppSettings>(config.GetSection(nameof(AppSettings)));
    services.AddHostedService<SyncDbService>();
});

var app = builder.Build();

await app.RunAsync();