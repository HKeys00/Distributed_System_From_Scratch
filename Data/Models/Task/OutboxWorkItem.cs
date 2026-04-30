using System.ComponentModel.DataAnnotations.Schema;

namespace Data.Models.Task
{
    /// <summary>
    /// Read-only projection from the 'outbox' database view. Surfaces tasks that have not
    /// yet been successfully published to the broker, so the relay can dispatch them.
    /// </summary>
    public class OutboxWorkItem : IWorkItem
    {
        /// <summary>
        /// Auto-incrementing surrogate primary key inherited from the Tasks table. Used for
        /// insertion ordering when the relay pages through the outbox.
        /// </summary>
        [Column(TypeName = "int8")]
        public long Id { get; set; }

        /// <summary>
        /// Stable external identifier for the task. Correlation key between the database
        /// and broker.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// Tracing identifier carried through from the originating request. Forwarded into
        /// the broker message envelope so workers can stitch logs back to the HTTP call.
        /// </summary>
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// SHA-256 hash of the normalised URL. Used as the idempotency key so duplicate
        /// submissions of the same URL can be detected and rejected.
        /// </summary>
        public required string IdempotencyId { get; set; }

        /// <summary>
        /// The absolute URL the work item targets — published in the broker message body.
        /// </summary>
        public required string Url { get; set; }

        /// <summary>
        /// Timestamp the underlying Tasks row was inserted.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Timestamp of the most recent attempt by the relay to publish this task. Always
        /// null for rows appearing in the outbox view.
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
    }
}
