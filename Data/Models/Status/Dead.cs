using System.ComponentModel.DataAnnotations.Schema;
namespace Data.Models.Status
{
    public class Dead
    {
        #region Properties

        /// <summary>
        /// Auto-incrementing surrogate primary key of this conflict record.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Stable external identifier for the task. Correlation key between the database
        /// and broker.
        /// </summary>
        public required Guid TaskId { get; set; }

        /// <summary>
        /// Tracing identifier inherited from the originating request, carried through the
        /// relay and worker so the DLQ record can be joined back to the task lifecycle.
        /// </summary>
        public required Guid CorrelationId { get; set; }

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
        public DateTime DeadAt { get; set; }

        #endregion
    }
}