using System.ComponentModel.DataAnnotations.Schema;

namespace Data.Models.Task
{
    /// <summary>
    /// Shared shape of a work item as it flows through the Tasks table and its derived
    /// outbox / stale views. Exposes the identifiers, payload and lifecycle timestamps that
    /// the relay and workers rely on.
    /// </summary>
    public interface IWorkItem
    {
        /// <summary>
        /// Auto-incrementing surrogate primary key. Used for insertion ordering when paging
        /// through tasks — not exposed outside the system.
        /// </summary>
        [Column(TypeName = "int8")]
        public int Id { get; set; }

        /// <summary>
        /// Stable external identifier for the task. Used when updating row state and as the
        /// correlation key between the database and broker.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// SHA-256 hash of the normalised URL. Used as the idempotency key so duplicate
        /// submissions of the same URL can be detected and rejected.
        /// </summary>
        public string IdempotencyId { get; set; }

        /// <summary>
        /// The absolute URL the work item targets — this is what the crawler will fetch.
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// Timestamp the row was first inserted into the Tasks table.
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
        /// Number of times the relay has re-dispatched this task after it went stale.
        /// </summary>
        public int Retries { get; set; }
    }
}
