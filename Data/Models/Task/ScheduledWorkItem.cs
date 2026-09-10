using System.ComponentModel.DataAnnotations.Schema;

namespace Data.Models.Task
{
    /// <summary>
    /// Read-only projection from the 'scheduled' database view. Surfaces tasks that have
    /// not yet been sent and whose next attempt is scheduled for a future point in time,
    /// so the relay can observe backlog waiting on backoff.
    /// </summary>
    public class ScheduledWorkItem : IWorkItem
    {
        /// <summary>
        /// Auto-incrementing surrogate primary key inherited from the Tasks table.
        /// </summary>
        [Column(TypeName = "int8")]
        public long Id { get; set; }

        /// <summary>
        /// Stable external identifier for the task. Correlation key between the database
        /// and broker.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// Tracing identifier carried through from the originating request.
        /// </summary>
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// SHA-256 hash of the normalised URL. Used as the idempotency key so duplicate
        /// submissions of the same URL can be detected and rejected.
        /// </summary>
        public required string IdempotencyId { get; set; }

        /// <summary>
        /// The absolute URL the work item targets.
        /// </summary>
        public required string Url { get; set; }

        /// <summary>
        /// Timestamp the underlying Tasks row was inserted.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Always null for rows in the scheduled view — a row is only scheduled while it
        /// is still waiting to be sent.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? SentAt { get; set; }

        /// <summary>
        /// Timestamp at which the task was marked as published to the message broker.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? PublishedAt { get; set; }

        /// <summary>
        /// Timestamp at which the task is next eligible to be sent. Always strictly in
        /// the future for rows in this view.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? NextAttemptAt { get; set; }

        /// <summary>
        /// Number of times the relay has sent this message.
        /// </summary>
        public int Attempt { get; set; }

        /// <summary>
        /// Fencing token of the relay leader that most recently published this task.
        /// 0 for rows in the scheduled view since the task has not been sent yet.
        /// </summary>
        [Column(TypeName = "int8")]
        public long SentByToken { get; set; }
    }
}
