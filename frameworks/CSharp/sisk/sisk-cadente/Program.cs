using Sisk.Cadente.CoreEngine;
using Sisk.Core.Http;
using SiskBenchmarks;

var app = HttpServer.CreateBuilder(host =>
{
    host.UseEngine<CadenteHttpServerEngine>();
    host.UseListeningPort("http://0.0.0.0:8080/");
    host.UseMinimalConfiguration();
}).Build();

BenchmarkApplication.Map(app.Router);

app.Start();
