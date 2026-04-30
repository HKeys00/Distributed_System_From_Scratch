namespace Shared.Constants
{
    /// <summary>
    /// Names used to carry a correlation id across HTTP, log scopes and the broker envelope.
    /// Centralised here so the controller, middleware, relay and worker all agree on the
    /// same key.
    /// </summary>
    public static class CorrelationConstants
    {
        /// <summary>
        /// HTTP header read on inbound requests and echoed on responses. Clients can supply
        /// their own id; the middleware mints one when the header is missing or malformed.
        /// </summary>
        public const string HeaderName = "X-Correlation-ID";

        /// <summary>
        /// Key under which the resolved correlation id is stashed on
        /// <c>HttpContext.Items</c> so downstream controllers can read it without re-parsing
        /// the header.
        /// </summary>
        public const string HttpContextItemKey = "CorrelationId";

        /// <summary>
        /// Property name used when pushing the correlation id into the logging scope. Every
        /// structured log line emitted inside the scope carries this field.
        /// </summary>
        public const string LogScopeKey = "CorrelationId";
    }
}
