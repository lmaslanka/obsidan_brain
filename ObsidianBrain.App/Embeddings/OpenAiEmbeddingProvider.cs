using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ObsidianBrain.App.Config;
using ObsidianBrain.App.Utils;

namespace ObsidianBrain.App.Embeddings;

public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly EmbeddingsConfig _config;
    private readonly HttpClient _http = new();

    public OpenAiEmbeddingProvider(EmbeddingsConfig config)
    {
        _config = config;
        _http.BaseAddress = new Uri(config.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds));
    }

    public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable(_config.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key))
        {
            return BuildDeterministicFallback(input, _config.Dimensions);
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = _config.Model,
                input
            }), Encoding.UTF8, "application/json");

            try
            {
                using var response = await _http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < _config.MaxRetries && IsTransientStatusCode((int)response.StatusCode))
                    {
                        await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
                var values = new float[data.GetArrayLength()];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = data[i].GetSingle();
                }

                ValidateDimensions(values.Length);
                return values;
            }
            catch (Exception ex) when (attempt < _config.MaxRetries && IsTransientException(ex))
            {
                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
        }
    }

    public async Task<bool> HealthcheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await EmbedAsync("healthcheck", cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float[] BuildDeterministicFallback(string input, int dimensions)
    {
        var hash = Hashing.Sha256(input);
        var buffer = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            var c = hash[i % hash.Length];
            buffer[i] = (c % 31) / 31f;
        }

        return buffer;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private static bool IsTransientStatusCode(int statusCode)
    {
        return statusCode == 429 || statusCode >= 500;
    }

    private static bool IsTransientException(Exception ex)
    {
        return ex is HttpRequestException || ex is TaskCanceledException;
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        var baseDelay = Math.Max(25, _config.InitialBackoffMs);
        var multiplier = Math.Pow(2, attempt);
        var jitter = Random.Shared.Next(20, 80);
        return TimeSpan.FromMilliseconds(baseDelay * multiplier + jitter);
    }

    private void ValidateDimensions(int actualDimensions)
    {
        if (actualDimensions != _config.Dimensions)
        {
            throw new InvalidOperationException(
                $"Embedding dimensions mismatch. Expected {_config.Dimensions}, got {actualDimensions}.");
        }
    }
}
