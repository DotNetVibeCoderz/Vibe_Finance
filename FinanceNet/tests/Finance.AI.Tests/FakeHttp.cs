using System.Net;
using System.Text;

namespace Finance.AI.Tests;

/// <summary>Scripted HTTP handler: returns queued responses and records requests.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, string ContentType)> _responses = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    public FakeHandler Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        _responses.Enqueue((status, body, contentType));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        var (status, text, type) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.InternalServerError, "no response queued", "text/plain");
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, type) };
    }

    public HttpClient Client() => new(this);
}
