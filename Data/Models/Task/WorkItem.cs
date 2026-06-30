using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Shared.Helpers;

namespace Data.Models.Task
{
    /// <summary>
    /// Row in the Tasks table representing a single crawl job and its lifecycle state —
    /// created, dispatched to the broker, completed or failed. Authoritative source for
    /// every derived view (outbox, stale tasks).
    /// </summary>
    [Table("Tasks")]
    public class WorkItem : IWorkItem
    {
        #region properties

        /// <summary>
        /// Auto-incrementing surrogate primary key. Used for insertion ordering when paging
        /// through tasks.
        /// </summary>
        [Column(TypeName = "int8")]
        public long Id { get; set; }

        /// <summary>
        /// Stable external identifier for the task. Correlation key between the database
        /// and broker; indexed as unique.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// Tracing identifier that follows a single logical request through the system.
        /// Set by the HTTP middleware (or minted there) so every log line and broker message
        /// for this task carries it.
        /// </summary>
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// SHA-256 hash of the normalised URL. Used as the idempotency key so duplicate
        /// submissions of the same URL can be detected and rejected.
        /// </summary>
        public required string IdempotencyId { get; set; }

        /// <summary>
        /// The absolute URL the work item targets — this is what the crawler will fetch.
        /// </summary>
        public required string Url { get; set; }

        /// <summary>
        /// Timestamp the row was inserted. Defaults to clock_timestamp() on the database.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Timestamp of the most recent attempt by the relay to publish this task to the
        /// broker. Null while the task is still sitting in the outbox.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? SentAt { get; set; }

        /// <summary>
        /// Timestamp at which the task was marked as published to the message broker
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? PublishedAt {get; set;}

        /// <summary>
        /// Timestamp at which the task is marked to send again.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? NextAttemptAt {get; set;}

        /// <summary>
        /// Number of times the relay has sent this message.
        /// </summary>
        public int Attempt { get; set; }

        /// <summary>
        /// Fencing token of the relay leader that most recently published this task.
        /// 0 means the task has not been sent (real tokens start at 1).
        /// </summary>
        [Column(TypeName = "int8")]
        public long SentByToken { get; set; }

        #endregion

        #region Constructor

        public WorkItem(){}

        [SetsRequiredMembers]
        public WorkItem(string url)
        {
            IdempotencyId = url.HashUrl();
            CorrelationId = Guid.NewGuid();
            TaskId = Guid.NewGuid();
            Url = url;
        }

        #endregion
    }
}
