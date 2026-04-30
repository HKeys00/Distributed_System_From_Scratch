using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
namespace Data.Models.Status
{
    /// <summary>
    /// Audit record written when a task finishes successfully. Keeps a compact trace of
    /// completions, keyed by idempotency id, once the row has been removed from the
    /// active Tasks pipeline.
    /// </summary>
    public class Success
    {
        #region Properties 

        /// <summary>
        /// Auto-incrementing surrogate primary key of this success record.
        /// </summary>
        public int Id {get; set;}

        /// <summary>
        /// Stable external identifier for the task. Correlation key between the database
        /// and broker.
        /// </summary>
        public required Guid TaskId { get; set; }

        /// <summary>
        /// Tracing identifier inherited from the originating request, carried through the
        /// relay and worker so the success record can be joined back to the task lifecycle.
        /// </summary>
        public required Guid CorrelationId { get; set; }

        /// <summary>
        /// SHA-256 hash of the URL of the completed task. Indexed as unique so a
        /// successful completion can be looked up quickly when deduplicating future
        /// submissions.
        /// </summary>
        public required string IdempotencyId { get; set; }

        /// <summary>
        /// Timestamp the task finished. Defaults to clock_timestamp() on the database.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime FinishedAt { get; set; }

        #endregion

        #region Constructor

        public Success(){}

        [SetsRequiredMembers]
        public Success(Guid taskId, Guid correlationId, string idempotencyId)
        {
            TaskId = taskId;
            CorrelationId = correlationId;
            IdempotencyId = idempotencyId;
        }

        #endregion
    }
}
