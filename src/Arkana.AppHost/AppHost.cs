var builder = DistributedApplication.CreateBuilder(args);

// AI Gateway API — connects to existing PostgreSQL & Redis
var api = builder.AddProject<Projects.Arkana_Gateway_Api>("ai-gateway-api")
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:5000");

builder.Build().Run();
