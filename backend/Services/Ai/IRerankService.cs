namespace ClinicalRag.Api.Services.Ai;

public interface IRerankService
{
    Task<List<RerankResult>> RerankAsync(string query, List<string> documents, int? topN = null);
}

public record RerankResult(int Index, double Score, string Text);
