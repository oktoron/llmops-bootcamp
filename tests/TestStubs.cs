// спільні заглушки юніт-тестів ModelClient (W4/W5): фабрика HttpClient, gateway-заглушка з поведінкою на кожну модель,
// типові повідомлення й параметри запобіжника, фабрика ModelClient, серіалізація як у Results.Json. одна копія для
// тестів, що ними користуються; виняток — FallbackTests.Client (свій ScriptedHandler), його правити окремо.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

public static class Stubs
{
    // як CircuitOptions.Default (3 збої, 30 с, 5 с) — але явно, щоб тест не залежав від зміни типових
    public static readonly CircuitOptions Opts = new(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

    // системний промпт зі словом support — з ним mock віддає канонічні відповіді
    public static ChatMessage[] Msgs(string text = "Привіт") =>
        new[] { new ChatMessage("system", "support"), new ChatMessage("user", text) };

    // ModelClient над gateway-заглушкою з ручним годинником; запобіжники відомі для всіх mock-моделей одразу
    internal static ModelClient Client(GatewayStub stub, ManualTimeProvider time,
        CircuitOptions? options = null, TimeSpan? timeout = null) =>
        new(new StubHttpClientFactory(stub), "http://gateway:4000", timeout, null, options ?? Opts, time,
            ModelClient.ChainModels("mock"));

    // серіалізація — як у Results.Json: Program.cs не задає своїх опцій, тож діють web defaults
    public static JsonElement SerializeWeb(object body)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

// IHttpClientFactory, що віддає клієнт над заданим handler; handler не звільняється разом із клієнтом
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

// gateway-заглушка: поведінка кожної моделі задається окремо (без налаштування — 200 з usage); рахує виклики по моделях
public sealed class GatewayStub : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _behavior = new();

    public int Calls(string model) => _calls.TryGetValue(model, out var n) ? n : 0;
    public int Total => _calls.Values.Sum();

    public void Set(string model, Func<CancellationToken, Task<HttpResponseMessage>> behavior) => _behavior[model] = behavior;
    public void Code(string model, int code) => Set(model, _ => Task.FromResult(Resp(code, model)));
    public void Ok(params string[] models)
    {
        foreach (var m in models)
            Code(m, 200);
    }
    // 200 з тілом, що не розпарситься
    public void Garbage(string model) => Set(model, _ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json at all") }));
    public void Throw(string model) => Set(model, _ => throw new HttpRequestException("connection refused"));
    // 400 із заданим тілом помилки (напр. відмова LiteLLM через вміст запиту)
    public void BadRequest(string model, string body) => Set(model, _ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        string model;
        using (var doc = JsonDocument.Parse(body))
            model = doc.RootElement.GetProperty("model").GetString()!;
        _calls.AddOrUpdate(model, 1, (_, n) => n + 1);
        return _behavior.TryGetValue(model, out var f) ? await f(ct) : Resp(200, model);
    }

    // тіло 200 від gateway: відповідь асистента з usage
    public static string OkBody(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content } } },
        usage = new { prompt_tokens = 10, completion_tokens = 5 }
    });

    // тіло помилки у форматі LiteLLM
    public const string ErrorBody = "{\"error\":{\"message\":\"boom\"}}";

    // тіла 400 у форматі LiteLLM: відмова через вміст запиту і помилка конфігурації (описка в аліасі)
    public const string ContextWindowBody =
        "{\"error\":{\"message\":\"litellm.ContextWindowExceededError: litellm.BadRequestError: OpenAIException - This model's maximum context length is 128000 tokens\",\"type\":null,\"param\":null,\"code\":\"400\"}}";
    public const string InvalidModelBody =
        "{\"error\":{\"message\":\"Invalid model name passed in model=gpt-nope\",\"type\":\"invalid_request_error\",\"param\":\"model\",\"code\":\"400\"}}";

    // 200 — відповідь «Відповідь {model}» з usage; інший код — тіло помилки
    public static HttpResponseMessage Resp(int code, string model) => new((HttpStatusCode)code)
    {
        Content = new StringContent(code == 200 ? OkBody($"Відповідь {model}") : ErrorBody, Encoding.UTF8, "application/json")
    };
}
