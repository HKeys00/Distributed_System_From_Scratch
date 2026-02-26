namespace Distributed_System_From_Scratch.Middleware.Options
{
    public class MaxConcurrentRequestsOptions
    {
        /// <summary>
        /// Gets or sets if throttling is enabled
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The endpoints that should be throttled.
        /// </summary>
        public required string[] EndPoints { get; set; }
    }
}
