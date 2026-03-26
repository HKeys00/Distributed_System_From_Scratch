namespace Shared.Models
{
    /// <summary>
    /// Represents a unit of work with associated metadata, payload, and execution details.
    /// </summary>
    public class WorkItem
    {
        /// <summary>
        /// Gets or sets the unique identifier for the task.
        /// </summary>
        public int TaskId { get; set; }

        /// <summary>
        /// Gets or sets the type of task this work item corresponds to.
        /// </summary>
        public required string TaskType { get; set; }

        /// <summary>
        /// The type of execution model this work item belongs to.
        /// </summary>
        public required string ExecutionType { get; set; }

        /// <summary>
        /// The payload data of the work item.
        /// </summary>
        public required dynamic Payload { get; set; }

        /// <summary>
        /// When this work item was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// The number of retries this work item has undergone.
        /// </summary>
        public int Retries { get; set; }
    }
}
