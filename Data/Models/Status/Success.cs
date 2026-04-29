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
        public Success(string idempotencyId)
        {
            IdempotencyId = idempotencyId;
        }

        #endregion
    }
}
