namespace Shared.DTOs
{
    public record CrawlMessage(Guid TaskId, string IdempotencyId, string Url, int Attempt); 
}