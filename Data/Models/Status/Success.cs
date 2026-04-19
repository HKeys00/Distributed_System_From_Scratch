namespace Data.Models.Status
{
    /// <summary>
    /// Represents the successful completion of a task, including its identifier and completion time.
    /// </summary>
    public class Success
    {
        /// <summary>
        /// The id of a task that was successfully completed.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// The date time when the task was completed.
        /// </summary>
        public DateTime FinishedAt { get; set; }
    }
}
