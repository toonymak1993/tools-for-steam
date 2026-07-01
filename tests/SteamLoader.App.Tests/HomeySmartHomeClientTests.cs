using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Infrastructure.SmartHome;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class HomeySmartHomeClientTests
{
    [Fact]
    public async Task SetCapabilityValueAsync_SendsCamelCasePayloadWithTransactionId()
    {
        var handler = new CapturingHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        using var httpClient = new HttpClient(handler);
        var client = new HomeySmartHomeClient(httpClient);
        using var valueDocument = JsonDocument.Parse("0.5");

        await client.SetCapabilityValueAsync(
            new SmartHomeHomeyConfiguration
            {
                BaseUrl = "http://homey.local",
                SessionToken = "session-token"
            },
            "device-1",
            "dim",
            valueDocument.RootElement,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal(
            "http://homey.local/api/manager/devices/device/device-1/capability/dim",
            handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("session-token", handler.Authorization?.Parameter);
        Assert.False(string.IsNullOrWhiteSpace(handler.Body));

        using var bodyDocument = JsonDocument.Parse(handler.Body!);
        Assert.True(bodyDocument.RootElement.TryGetProperty("value", out var valueElement));
        Assert.Equal(0.5d, valueElement.GetDouble(), 3);
        Assert.True(bodyDocument.RootElement.TryGetProperty("transactionId", out var transactionIdElement));
        Assert.StartsWith("homey-api-", transactionIdElement.GetString() ?? string.Empty);
        Assert.False(bodyDocument.RootElement.TryGetProperty("Value", out _));
        Assert.False(bodyDocument.RootElement.TryGetProperty("TransactionId", out _));
    }

    [Fact]
    public async Task SetCapabilityValueAsync_UsesDetailedHomeyErrorWhenProvided()
    {
        var handler = new CapturingHandler(
            () => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"description\":\"Capability dim rejected by Homey.\"}",
                    Encoding.UTF8,
                    "application/json")
            });
        using var httpClient = new HttpClient(handler);
        var client = new HomeySmartHomeClient(httpClient);
        using var valueDocument = JsonDocument.Parse("0.5");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SetCapabilityValueAsync(
                new SmartHomeHomeyConfiguration
                {
                    BaseUrl = "http://homey.local",
                    SessionToken = "session-token"
                },
                "device-1",
                "dim",
                valueDocument.RootElement,
                CancellationToken.None));

        Assert.Equal("Capability dim rejected by Homey.", exception.Message);
    }

    [Fact]
    public async Task SetMoodAsync_SendsMoodSetRequest()
    {
        var handler = new CapturingHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        using var httpClient = new HttpClient(handler);
        var client = new HomeySmartHomeClient(httpClient);

        await client.SetMoodAsync(
            new SmartHomeHomeyConfiguration
            {
                BaseUrl = "http://homey.local",
                SessionToken = "session-token"
            },
            "mood-1",
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            "http://homey.local/api/manager/moods/mood/mood-1/set",
            handler.RequestUri?.ToString());
        Assert.Equal("{}", handler.Body);
    }

    [Fact]
    public async Task GetCatalogAsync_ParsesMoodsFromHomeyManager()
    {
        var handler = new PathAwareHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/api/manager/zones/zone" => CreateJsonResponse("{}"),
                "/api/manager/devices/device" => CreateJsonResponse("{}"),
                "/api/manager/flow/flow" => CreateJsonResponse("{}"),
                "/api/manager/flow/advancedflow" => CreateJsonResponse("{}"),
                "/api/manager/moods/mood" => CreateJsonResponse("""
                    {
                      "mood-1": {
                        "name": "Cinema",
                        "preset": "Movie Night",
                        "zone": "zone-1",
                        "devices": {
                          "device-1": { "state": { "onoff": true } },
                          "device-2": { "state": { "dim": 0.2 } }
                        }
                      }
                    }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                },
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new HomeySmartHomeClient(httpClient);

        var catalog = await client.GetCatalogAsync(
            new SmartHomeHomeyConfiguration
            {
                BaseUrl = "http://homey.local",
                SessionToken = "session-token"
            },
            CancellationToken.None);

        Assert.True(catalog.Moods.TryGetValue("mood-1", out var mood));
        Assert.NotNull(mood);
        Assert.Equal("Cinema", mood.Name);
        Assert.Equal("Movie Night", mood.Preset);
        Assert.Equal("zone-1", mood.ZoneId);
        Assert.Equal(2, mood.DeviceIds.Count);
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responseFactory;

        public CapturingHandler(Func<HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _responseFactory();
        }
    }

    private sealed class PathAwareHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public PathAwareHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }
}
