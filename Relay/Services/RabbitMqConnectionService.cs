using RabbitMQ.Client;

namespace Relay.Services
{
    /// <summary>
    /// Class for creating and holding a rabbit mq connection
    /// </summary>
    /// <remarks>Can only store one connection at a time at the moment.</remarks>
    public class RabbitMqConnectionService
    {
        #region Fields

        private readonly IConnection? _connection;
        private readonly object _lock = new object();

        #endregion

        #region Methods

        public IConnection GetConnection()

        #endregion
    }
}
