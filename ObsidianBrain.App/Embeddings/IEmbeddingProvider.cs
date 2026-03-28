namespace ObsidianBrain.App.Embeddings;

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken);
    Task<bool> HealthcheckAsync(CancellationToken cancellationToken);
}
