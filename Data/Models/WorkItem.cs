using System.ComponentModel.DataAnnotations.Schema;

namespace Data.Models
{
    /// <summary>
    /// Represents a unit of work with associated metadata, payload, and execution details.
    /// </summary>
    public class WorkItem
    {
        /// <summary>
        /// Gets or sets the ordering identifier for the task.
        /// </summary>
        [Column(TypeName = "int8")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier for the task.
        /// </summary>
        public Guid TaskId { get; set; }

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
        [Column(TypeName = "jsonb")]
        public required string Payload { get; set; }

        /// <summary>
        /// When this work item was created.
        /// </summary>
        [Column(TypeName = "timestamptz")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// The number of retries this work item has undergone.
        /// </summary>
        public int Retries { get; set; }
    }
}
