namespace Shared.DTOs
{
    public record CrawlMessage(string IdempotencyId, string Url); 
}