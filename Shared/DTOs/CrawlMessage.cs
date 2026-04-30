namespace Shared.DTOs
{
    public record CrawlMessage(Guid TaskId, Guid CorrelationId, string IdempotencyId, string Url, int Attempt); 
}