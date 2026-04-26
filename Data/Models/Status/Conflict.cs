using System.ComponentModel.DataAnnotations.Schema;
namespace Data.Models.Status
{
    /// <summary>
    /// Audit record written when a task cannot be accepted — typically because its
    /// idempotency key clashes with an existing task. Preserves enough detail to
    /// diagnose the clash after the fact without keeping the rejected row in Tasks.
    /// </summary>
    public class Conflict
    {
        /// <summary>
        /// Auto-incrementing surrogate primary key of this conflict record.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Stable external identifier for the task. Correlation key between the database
        /// and broker.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// SHA-256 hash of the URL that triggered the conflict. Matches the
        /// IdempotencyId of an existing task in the Tasks table.
        /// </summary>
        public required string IdempotencyId {get; set;}

        /// <summary>
        /// Timestamp the conflict was recorded. Defaults to clock_timestamp() on the
        /// database.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime FailedAt { get; set; }

        /// <summary>
        /// Human-readable explanation of why the task was rejected.
        /// </summary>
        public required string Reason { get; set; }

        /// <summary>
        /// The attempt number for this task.
        /// </summary>
        public required int Attempt {get; set;}
    }
}
