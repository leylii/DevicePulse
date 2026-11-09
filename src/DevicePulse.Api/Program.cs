using System.Text.Json.Serialization;
using DevicePulse.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Device status is rendered as a readable string ("Online"/"Offline") in responses.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// The real system clock in production; tests substitute a FakeTimeProvider for
// deterministic offline detection.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var timeoutSeconds = configuration.GetValue("DevicePulse:OfflineTimeoutSeconds", defaultValue: 30);
    return new DeviceRegistry(
        serviceProvider.GetRequiredService<TimeProvider>(),
        TimeSpan.FromSeconds(timeoutSeconds));
});

var app = builder.Build();

app.MapControllers();

app.Run();

// Exposes the top-level Program class to the test project's WebApplicationFactory<Program>.
public partial class Program { }
