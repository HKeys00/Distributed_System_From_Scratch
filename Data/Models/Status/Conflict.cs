namespace Data.Models.Status
{
    /// <summary>
    /// Represents a conflict with an identifier and a reason.
    /// </summary>
    public class Conflict
    {
        /// <summary>
        /// The id of the conflicted task.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The idempotency id of the conflicted task.
        /// </summary>
        public required string IdempotencyId {get; set;}

        /// <summary>
        /// The date time when the task failed.
        /// </summary>
        public DateTime FailedAt { get; set; }

        /// <summary>
        /// The reason of the being conflicted.
        /// </summary>
        public required string Reason { get; set; }
    }
}
