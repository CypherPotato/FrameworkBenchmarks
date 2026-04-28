using SiskBenchmarks;
using Sisk.Core.Http;

var app = HttpServer.CreateBuilder(host =>
{
    host.UseListeningPort("http://+:8080/");
    host.UseMinimalConfiguration();
}).Build();

BenchmarkApplication.Map(app.Router);

app.Start();
