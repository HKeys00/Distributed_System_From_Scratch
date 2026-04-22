using System.ComponentModel.DataAnnotations.Schema;

namespace Data.Models.Task
{
    /// <summary>
    /// Read-only projection from the 'staletasks' database view. Surfaces tasks that were
    /// dispatched to the broker but have not yet been completed within the expected window,
    /// making them candidates for re-dispatch by the relay.
    /// </summary>
    public class StaleWorkItem : IWorkItem
    {
        /// <summary>
        /// Auto-incrementing surrogate primary key inherited from the Tasks table.
        /// </summary>
        [Column(TypeName = "int8")]
        public int Id { get; set; }

        /// <summary>
        /// Stable external identifier for the task. Correlation key between the database
        /// and broker.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// SHA-256 hash of the normalised URL. Used as the idempotency key so duplicate
        /// submissions of the same URL can be detected and rejected.
        /// </summary>
        public required string IdempotencyId { get; set; }

        /// <summary>
        /// The absolute URL the work item targets — re-published in the broker message body
        /// when the relay retries the task.
        /// </summary>
        public required string Url { get; set; }

        /// <summary>
        /// Timestamp the underlying Tasks row was inserted.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Timestamp of the previous dispatch attempt. Always non-null for rows in the
        /// stale view — that's what qualifies them as stale.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? SentAt { get; set; }

        /// <summary>
        /// Timestamp at which the task was marked as published to the message broker
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime? PublishedAt {get; set;}

        /// <summary>
        /// Number of times the relay has already re-dispatched this task. Incremented on
        /// each retry to support back-off and giving-up policies.
        /// </summary>
        public int Retries { get; set; }
    }
}
