using FraudScoreApi.FraudScore;
using FraudScoreApi.Ready;
using FraudScoreApi.Storage;

var builder = WebApplication.CreateSlimBuilder(args);

// ---- Kestrel tweaks pra reduzir overhead por request ----
builder.WebHost.ConfigureKestrel(options =>
{
    // Sem header `Server: Kestrel` — economiza bytes + cycle por response
    options.AddServerHeader = false;

    // Desliga slow-client / slow-server protection. Em alta concorrência, os timers
    // que enforçam essas rates custam CPU em cada conexão. Pro Rinha (LAN, k6 confiável,
    // payloads ~500 B) é seguro desligar.
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;

    // Bound rígido — payloads reais ficam em ~500 B. Recusa qualquer coisa maior
    // antes de Kestrel alocar buffer.
    options.Limits.MaxRequestBodySize = 4096;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, FraudScoreJsonContext.Default);
});

var vectorsPath = Environment.GetEnvironmentVariable("VECTORS_PATH") ?? "/data/references_v1.bin";

builder.Services.AddSingleton(_ => VectorStore.Open(vectorsPath));
builder.Services.AddSingleton<IVectorSearch>(sp =>
    new IvfVectorSearch(sp.GetRequiredService<VectorStore>()));
builder.Services.AddSingleton(sp =>
    new ReadinessProbe(sp.GetRequiredService<VectorStore>()));

var app = builder.Build();

try
{
    _ = app.Services.GetRequiredService<VectorStore>();
    _ = app.Services.GetRequiredService<IVectorSearch>();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[startup] FATAL: {ex.Message}");
    Environment.Exit(1);
}

app.MapFraudScore();
app.MapReady();

app.Run();
