using System.Net;
using System.Text;

namespace Poyra.Tests.Unit;

internal sealed record LidioRecordedRequest(string Path, string Body, Dictionary<string, string> Headers);

/// <summary>
/// Sahte Lidio sunucusu — yolu yanıtla eşler, gelen istekleri (gövde + başlıklar) kaydeder.
/// Konnektör ve istemci testleri GERÇEK HTTP'den geçer; sahte bir HttpMessageHandler
/// başlık/gövde serileştirmesini atlatırdı.
/// </summary>
internal sealed class FakeLidio : IAsyncDisposable
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = [];
    private readonly List<LidioRecordedRequest> _requests = [];
    private HttpListener _listener = null!;

    public string BaseUrl { get; private set; } = "";

    public IReadOnlyList<string> Paths
    {
        get { lock (_requests) return [.. _requests.Select(r => r.Path)]; }
    }

    public void Respond(string path, HttpStatusCode status, string body)
        => _responses[path] = (status, body);

    public LidioRecordedRequest Last(string path)
    {
        lock (_requests)
            return _requests.LastOrDefault(r => r.Path == path)
                   ?? throw new InvalidOperationException($"'{path}' hiç çağrılmadı.");
    }

    public Task StartAsync()
    {
        // Port İŞLETİM SİSTEMİNDEN alınır; rastgele seçim paralel testlerde çakışıyordu.
        _listener = new HttpListener();
        BaseUrl = BosPort.Bagla(_listener);

        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var path = context.Request.Url!.AbsolutePath;
                var request = new LidioRecordedRequest(path, await reader.ReadToEndAsync(),
                    context.Request.Headers.AllKeys.Where(k => k is not null)
                        .ToDictionary(k => k!, k => context.Request.Headers[k]!));

                lock (_requests) _requests.Add(request);

                var (status, body) = _responses.GetValueOrDefault(path, (HttpStatusCode.NotFound, "{}"));
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                context.Response.Close();
            }
        });

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        return ValueTask.CompletedTask;
    }
}
