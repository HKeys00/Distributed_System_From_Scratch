using Data;
using Data.Models.Status;
using Data.Models.Task;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client.Events;
using Shared.Constants;
using Shared.DTOs;
using Shared.Helpers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

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
        private PartitionedRateLimiter<string> _bucket;

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

            _bucket = PartitionedRateLimiter.Create<string, string>(domain =>
            {
                   return RateLimitPartition.GetTokenBucketLimiter(
                        partitionKey: domain,
                        factory: key => new TokenBucketRateLimiterOptions
                        {
                            TokenLimit = 1,
                            TokensPerPeriod = 1,
                            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }
                    );
            });
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
            
            CrawlMessage? job;
            try
            {
                job = JsonSerializer.Deserialize<CrawlMessage>(args.Body.Span);
                if (job == null)
                {
                    await consumer.Channel.BasicRejectAsync(args.DeliveryTag, false);
                    AppMetrics.Worker.Fetches.WithLabels("deserialize_error").Inc();
                    var raw = Encoding.UTF8.GetString(args.Body.ToArray());
                    if (raw.Length > 512)
                    {
                        raw = raw[..512] + "...";
                    }
                    _logger.LogError("Failed to deserialize message {Body}", raw);
                    return;
                }
            } catch (Exception ex)
            {
                await consumer.Channel.BasicRejectAsync(args.DeliveryTag, false);
                AppMetrics.Worker.Fetches.WithLabels("deserialize_error").Inc();
                _logger.LogError(ex, "Unexpected error occurred while deserializing message");
                return;
            }

            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                [CorrelationConstants.LogScopeKey] = job.CorrelationId,
                ["TaskId"] = job.TaskId,
                ["IdempotencyId"] = job.IdempotencyId,
                ["Url"] = job.Url,
                ["Attempt"] = job.Attempt,
                ["DeliveryTag"] = args.DeliveryTag
            });

            _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Received crawl job",
                job.CorrelationId, job.TaskId);

            _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Attempting to aquire token for job", job.CorrelationId, job.TaskId);
            
            var uri = new Uri(job.Url);
            var lease = await _bucket.AcquireAsync(uri.Host);

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            if (!lease.IsAcquired)
            {
                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Failed to aquire token for job", job.CorrelationId, job.TaskId);
                
                var random = new Random();
                var retry = TimeSpan.FromSeconds(5);
                lease.TryGetMetadata(MetadataName.RetryAfter, out retry);
                retry.Add(TimeSpan.FromSeconds(random.Next(10)));
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE ""Tasks""
                        SET ""SentAt"" = NULL,
                            ""NextAttemptAt"" = now() + ({retry.TotalSeconds} * interval '1 second')
                        WHERE ""TaskId"" = {job.TaskId}"
                );

                await consumer.Channel.BasicRejectAsync(args.DeliveryTag, false);
                await context.SaveChangesAsync();

                lease.Dispose();
                return;
            }

            _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Aquired token for job", job.CorrelationId, job.TaskId);
            try
            {
                await ProcessMessage(context, job);
                await consumer.Channel.BasicAckAsync(args.DeliveryTag, false);
            } 
            catch
            {
                await consumer.Channel.BasicRejectAsync(args.DeliveryTag, false);
            } 
            finally
            {
                lease.Dispose();
            }
        }

        private async Task ProcessMessage(ApplicationDbContext context, CrawlMessage job)
        {
            var existingSuccess = await context.Successes.FirstOrDefaultAsync(s => s.IdempotencyId == job.IdempotencyId);
            if (existingSuccess != null)
            {
                //Between creating the task and this worker receiving it, the site had been scraped.
                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Skipping crawl, idempotency id already recorded as success",
                    job.CorrelationId, job.TaskId);
                AppMetrics.Worker.Fetches.WithLabels("already_done").Inc();
                return;
            }

            var existingFailure = await context.Conflicts.FirstOrDefaultAsync(c => c.TaskId == job.TaskId && c.Attempt == job.Attempt);
            if (existingFailure != null)
            {
                //Duplicate message.
                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Skipping crawl, duplicate delivery for this attempt {attempt}",
                    job.CorrelationId, job.TaskId, job.Attempt);
                AppMetrics.Worker.Fetches.WithLabels("duplicate").Inc();
                return;
            }

            string[]? childUrls;
            var fetchStart = Stopwatch.GetTimestamp();
            try
            {
                childUrls = await CrawlAsync(job.Url, CancellationToken.None);
                AppMetrics.Worker.FetchDurationSeconds.Observe(Stopwatch.GetElapsedTime(fetchStart).TotalSeconds);
                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Crawl completed with {ChildCount} child urls",
                    job.CorrelationId, job.TaskId, childUrls.Length);
            }
            catch(HttpRequestException ex)
            {
                AppMetrics.Worker.FetchDurationSeconds.Observe(Stopwatch.GetElapsedTime(fetchStart).TotalSeconds);
                AppMetrics.Worker.Fetches.WithLabels("http_error").Inc();
                //Commenting this out for now because logs are getting messy
                // _logger.LogWarning(ex, "CorrelationId={CorrelationId} TaskId={TaskId} Crawl returned status code {StatusCode}",
                //     job.CorrelationId, job.TaskId, ex.StatusCode);

                //TODO: Don't really like this being the workers job but fine for now
                await using var transaction = await context.Database.BeginTransactionAsync();

                if (job.Attempt == 5)
                {       //DLQ
                    await context.DLQ.AddAsync(new Dead()
                    {
                        TaskId = job.TaskId,
                        CorrelationId = job.CorrelationId,
                        IdempotencyId = job.IdempotencyId
                    });

                    AppMetrics.Worker.DeadLettered.Inc();
                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _logger.LogWarning("CorrelationId={CorrelationId} TaskId={TaskId} Task moved to DLQ after {Attempt} attempts",
                        job.CorrelationId, job.TaskId, job.Attempt);
                    throw;
                }

                await context.Database.ExecuteSqlInterpolatedAsync(                                                                                           
                    $@"UPDATE ""Tasks""                                                                                                                       
                        SET ""SentAt"" = NULL,                                                                                                                 
                            ""NextAttemptAt"" = now() + (interval '30 seconds' * power(2, ""Attempt"")),
                            ""Attempt"" = ""Attempt"" + 1                                                    
                        WHERE ""TaskId"" = {job.TaskId}"
                );

                context.Add(new Conflict()
                {
                    TaskId = job.TaskId,
                    CorrelationId = job.CorrelationId,
                    IdempotencyId = job.IdempotencyId,
                    Reason = ex.Message,
                    Attempt = job.Attempt
                });
                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Added conflict for task at attempt {Attempt}", job.CorrelationId, job.TaskId, job.Attempt);
                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Retrying Task, attempt number {Attempt}", job.CorrelationId, job.TaskId, job.Attempt + 1);

                AppMetrics.Worker.Retries.Inc();
                throw;
            }

            await context.Successes.AddAsync(new Success(job.TaskId, job.CorrelationId, job.IdempotencyId));

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
                _logger.LogError(ex, "CorrelationId={CorrelationId} TaskId={TaskId} Failed to save changes to database", job.CorrelationId, job.TaskId);
                //TODO Publish to a retry queue rather than immdeiately retrying
                throw;
            }

            AppMetrics.Worker.Fetches.WithLabels("success").Inc();
            _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Crawl marked as success, {ChildCount} new tasks queued", job.CorrelationId, job.TaskId, childUrls.Length);
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
                _logger.LogWarning("Invalid crawl url skipped: {Url}", url);
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
