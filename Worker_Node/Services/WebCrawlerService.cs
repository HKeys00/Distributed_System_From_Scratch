using Data;
using Data.Models.Status;
using Data.Models.Task;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client.Events;
using Shared.DTOs;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Worker_Node.Services
{
    /// <summary>
    /// Hosted service that consumes crawl jobs from RabbitMQ. For each message it fetches the page
    /// at the supplied URL and collects any child URLs referenced from it. The crawler is
    /// intentionally friendly: it identifies itself with a clear User-Agent, uses a short
    /// politeness delay, enforces a request timeout, and only follows http/https links.
    /// </summary>
    public class WebCrawlerService : IHostedService
    {
        #region Fields

        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly RabbitService _rabbitService;
        private readonly HttpClient _httpClient;
        private readonly ILogger<WebCrawlerService> _logger;
        private string? _consumerTag;

        private const string UserAgent = "DistributedSystemCrawler/1.0 (+friendly-bot)";
        private static readonly TimeSpan PolitenessDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private static readonly Regex HrefPattern = new(
            @"<a\b[^>]*?href\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="WebCrawlerService"/> class.
        /// Pulls a named <see cref="HttpClient"/> from the factory and configures it with the
        /// crawler's User-Agent and request timeout so every outbound request looks identical.
        /// </summary>
        /// <param name="rabbitService">Provider used to obtain a RabbitMQ channel.</param>
        /// <param name="httpClientFactory">Factory that produces the configured HttpClient.</param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public WebCrawlerService(
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            RabbitService rabbitService,
            IHttpClientFactory httpClientFactory,
            ILogger<WebCrawlerService> logger)
        {
            _dbContextFactory = dbContextFactory;
            _rabbitService = rabbitService;
            _logger = logger;

            _httpClient = httpClientFactory.CreateClient(nameof(WebCrawlerService));
            _httpClient.Timeout = RequestTimeout;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Subscribes to the outbox queue and begins consuming crawl jobs. A prefetch of one is
        /// used so a single worker only takes on one job at a time, making throttling predictable.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel startup.</param>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var channel = await _rabbitService.GetChannelAsync();
            await channel.BasicQosAsync(0, 1, false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnMessageReceivedAsync;

            _consumerTag = await channel.BasicConsumeAsync(
                "outbox",
                autoAck: false,
                consumerTag: "web-crawler-consumer",
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer: consumer,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Cancels the active consumer registration so no further messages are delivered.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel shutdown.</param>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var channel = await _rabbitService.GetChannelAsync();
            if (_consumerTag != null)
            {
                await channel.BasicCancelAsync(_consumerTag, false, cancellationToken);
            }
        }

        /// <summary>
        /// Callback invoked by the RabbitMQ consumer for each delivered message. Decodes the URL
        /// from the message body, runs a crawl, logs any discovered child URLs and then acks the
        /// message. The message is acked on both success and failure so a bad URL does not wedge
        /// the queue — retry policy should be handled upstream by the relay/outbox.
        /// </summary>
        /// <param name="sender">The consumer that raised the event.</param>
        /// <param name="args">The delivery envelope, including the message body and delivery tag.</param>
        private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs args)
        {
            if (sender is not AsyncEventingBasicConsumer consumer)
            {
                return;
            }

            CrawlMessage? job = null;
            try
            {
                job = JsonSerializer.Deserialize<CrawlMessage>(args.Body.Span);    
                if (job == null)
                {
                    await consumer.Channel.BasicRejectAsync(args.DeliveryTag, false);
                    _logger.LogError("Failed to deserialize message, {message}", Encoding.UTF8.GetString(args.Body.ToArray()));
                    return;
                }
            } catch (Exception ex)
            {
                await consumer.Channel.BasicRejectAsync(args.DeliveryTag, false);
                _logger.LogError("Unexpected error occured while processing message, {error}", ex.Message);
                return;
            }

            _logger.LogInformation("Received crawl job for {key}, {url}", job.IdempotencyId, job.Url);
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            var existingSuccess = await context.Successes.FirstOrDefaultAsync(s => s.IdempotencyId == job.IdempotencyId);
            if (existingSuccess != null)
            {
                //Between creating the task and this worker receiving it, the site had been scraped.
                await consumer.Channel.BasicAckAsync(args.DeliveryTag, false);
                return;
            }
            var existingFailure = await context.Conflicts.FirstOrDefaultAsync(c => c.TaskId == job.TaskId && c.Attempt == job.Attempt);
            if (existingFailure != null)
            {
                //Duplicate message.
                await consumer.Channel.BasicAckAsync(args.DeliveryTag, false);
                return;
            }

            string[]? childUrls;
            try
            {
                childUrls = await CrawlAsync(job.Url, CancellationToken.None);
                childUrls = Array.Empty<string>(); //Test with only one task.
                _logger.LogInformation("Crawl of {url} found {count} child urls", job.Url, childUrls.Length);
            }
            catch(HttpRequestException ex)
            {
                _logger.LogWarning("Crawl of {url} returned status code {code}", job.Url, ex.StatusCode);
                //TODO: Don't really like this being the workers job but fine for now
                
                await using var transaction = await context.Database.BeginTransactionAsync();
                
                await context.Database.ExecuteSqlInterpolatedAsync(                                                                                           
                    $@"UPDATE ""Tasks""                                                                                                                       
                        SET ""SentAt"" = NULL,                                                                                                                 
                            ""NextAttemptAt"" = now() + (interval '30 seconds' * power(2, ""Attempts""))                                                        
                        WHERE ""TaskId"" = {job.TaskId}"
                );

                context.Add(new Conflict()
                {
                    TaskId = job.TaskId,
                    IdempotencyId = job.IdempotencyId,
                    Reason = ex.Message,
                    Attempt = job.Attempt
                });
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                
                await consumer.Channel.BasicAckAsync(args.DeliveryTag, false);
                return;
            }

            await context.Successes.AddAsync(new Success(job.TaskId, job.IdempotencyId));

            foreach(var child in childUrls)
            {
                await context.Tasks.AddAsync(new WorkItem(child));
            }

            try
            {
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to save changes to database with message, {message}", ex.Message);
                //TODO Publish to a retry queue rather than immdeiately retrying
                await consumer.Channel.BasicRejectAsync(args.DeliveryTag, true);
                return;
            }

            await consumer.Channel.BasicAckAsync(args.DeliveryTag, false);
        }

        /// <summary>
        /// Performs a single polite GET against the supplied URL and returns any absolute child
        /// URLs found in anchor tags on the resulting page. Non-HTML responses and non-success
        /// status codes produce an empty result. A small politeness delay is applied before the
        /// request is issued to avoid hammering the target host.
        /// </summary>
        /// <param name="url">The absolute URL to crawl.</param>
        /// <param name="cancellationToken">Token used to cancel the HTTP request.</param>
        /// <returns>A de-duplicated array of absolute http/https child URLs found on the page.</returns>
        private async Task<string[]> CrawlAsync(string url, CancellationToken cancellationToken)
        {
            await Task.Delay(PolitenessDelay, cancellationToken);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
            {
                _logger.LogWarning("Invalid crawl url skipped: {url}", url);
                return Array.Empty<string>();
            }

            using var response = await _httpClient.GetAsync(baseUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ExtractChildUrls(html, baseUri);
        }

        /// <summary>
        /// Scans the provided HTML for anchor href attributes and returns the set of unique
        /// absolute http/https URLs. Relative hrefs are resolved against the page's base URI.
        /// Fragment-only, mailto and javascript hrefs are filtered out so the output only contains
        /// things a crawler can meaningfully follow.
        /// </summary>
        /// <param name="html">The raw HTML content returned by the target page.</param>
        /// <param name="baseUri">The URI of the page, used to resolve relative hrefs.</param>
        /// <returns>A de-duplicated array of absolute URLs.</returns>
        private static string[] ExtractChildUrls(string html, Uri baseUri)
        {
            var matches = HrefPattern.Matches(html);
            var results = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in matches)
            {
                var raw = match.Groups[1].Value.Trim();
                if (raw.Length == 0
                    || raw.StartsWith('#')
                    || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                    || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Uri.TryCreate(baseUri, raw, out var absolute)
                    && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                {
                    results.Add(absolute.ToString());
                }
            }

            return results.ToArray();
        }

        #endregion
    }
}
