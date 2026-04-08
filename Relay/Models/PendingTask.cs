namespace Relay.Models
{
    /// <summary>
    /// Class for holding a message that has been sent but not acked.
    /// </summary>
    internal struct PendingTask
    {
        /// <summary>
        /// The date time of the object sent.
        /// </summary>
        public DateTime SentAt { get; set; }

        /// <summary>
        /// The id of the associated work item.
        /// </summary>
        public Guid TaskId { get; set; }
    }
}
