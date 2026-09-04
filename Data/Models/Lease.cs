namespace Data.Models
{
    /// <summary>
    /// The lease item for a specific domain.
    /// </summary>
    public class Lease
    {
        /// <summary>
        /// The actual domain that is being protected.
        /// </summary>
        public string Domain { get; set; } = null!;

        /// <summary>
        /// The last time this domain was held to crawl.
        /// </summary>
        public DateTime LastSeenAt { get; set; }
    }
}
