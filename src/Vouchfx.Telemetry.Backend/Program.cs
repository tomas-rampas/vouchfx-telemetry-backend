var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "vouchfx-telemetry-backend");
app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in later integration-style unit tests.
public partial class Program { }
