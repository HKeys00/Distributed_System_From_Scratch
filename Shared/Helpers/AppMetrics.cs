using Prometheus;

namespace Shared.Helpers;

public static class AppMetrics
{
    public static class Controllers
    {
        public static readonly Counter TasksAccepted = Metrics.CreateCounter(
            "controllers_tasks_accepted_total",
            "Number of crawl requests received by the controller, labelled by outcome.",
            new CounterConfiguration { LabelNames = new[] { "result" } });
    }

    public static class Relay
    {
        public static readonly Counter OutboxPublishes = Metrics.CreateCounter(
            "relay_outbox_publishes_total",
            "Number of outbox messages published to the broker, labelled by result.",
            new CounterConfiguration { LabelNames = new[] { "result" } });

        public static readonly Histogram OutboxPublishAckSeconds = Metrics.CreateHistogram(
            "relay_outbox_publish_ack_seconds",
            "Time between publishing a message and receiving the broker ack.",
            new HistogramConfiguration
            {
                Buckets = Histogram.ExponentialBuckets(start: 0.001, factor: 2, count: 12)
            });

        public static readonly Histogram OutboxPublishBatchSize = Metrics.CreateHistogram(
            "relay_outbox_publish_batch_size",
            "Number of messages sent in a single publish batch.",
            new HistogramConfiguration
            {
                Buckets = new double[] { 1, 5, 10, 25, 50, 100, 250 }
            });

        public static readonly Gauge OutboxDepth = Metrics.CreateGauge(
            "relay_outbox_depth",
            "Current number of unpublished items in the outbox view.");

        public static readonly Gauge OutboxOldestUnpublishedSeconds = Metrics.CreateGauge(
            "relay_outbox_oldest_unpublished_age_seconds",
            "Age, in seconds, of the oldest unpublished task in the outbox view (0 if empty).");

        public static readonly Gauge StaleDepth = Metrics.CreateGauge(
            "relay_stale_depth",
            "Current number of items in the stale tasks view.");

        public static readonly Gauge StaleOldestSeconds = Metrics.CreateGauge(
            "relay_stale_oldest_age_seconds",
            "Age, in seconds, since the oldest stale task was last dispatched (0 if empty).");

        public static readonly Counter StaleTokenTaskUpdates = Metrics.CreateCounter(
            "relay_stale_token_task_updates_total",
            "Number of task update attempts rejected because the relay's fencing token was no longer current.");

        public static readonly Counter OutboxNacks = Metrics.CreateCounter(
            "relay_outbox_nacks_total",
            "Number of outbox publishes rejected by the broker via basic.nack.");

        public static readonly Gauge IsLeader = Metrics.CreateGauge(
            "relay_is_leader",
            "1 when this relay instance currently holds leadership, 0 otherwise. Sum across instances should equal 1.");

        public static readonly Counter LeadershipPromotions = Metrics.CreateCounter(
            "relay_leadership_promotions_total",
            "Number of times this relay instance has been promoted to leader.");
    }

    public static class Worker
    {
        public static readonly Counter Fetches = Metrics.CreateCounter(
            "worker_fetches_total",
            "Number of crawl jobs processed, labelled by outcome.",
            new CounterConfiguration { LabelNames = new[] { "outcome" } });

        public static readonly Histogram FetchDurationSeconds = Metrics.CreateHistogram(
            "worker_fetch_duration_seconds",
            "Wall-clock duration of a single page fetch and parse.",
            new HistogramConfiguration
            {
                Buckets = new double[] { 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 15, 25, 30 }
            });

        public static readonly Counter Retries = Metrics.CreateCounter(
            "worker_retries_total",
            "Number of failed crawls scheduled for retry (non-terminal).");

        public static readonly Counter DeadLettered = Metrics.CreateCounter(
            "worker_dead_lettered_total",
            "Number of tasks moved to the DLQ after exhausting retries.");
    }
}
