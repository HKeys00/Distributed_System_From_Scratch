using RabbitMQ.Client;

namespace Worker_Node.Services
{
    /// <summary>
    /// Service for creating and managing rabbit MQ connections and channels.
    /// </summary>
    public class RabbitService
    {
        #region Fields

        private IConnection? _connection;
        private IChannel? _channel;

        #endregion

        #region Methods

        public async Task<IConnection> GetConnectionAsync()
        {
            if (_connection == null || !_connection.IsOpen)
            {
                var factory = new ConnectionFactory() { HostName = "rabbitmq", Port = 5672 };
                _connection = await factory.CreateConnectionAsync();
                _channel = await _connection.CreateChannelAsync(null);

                await _channel.QueueDeclareAsync("outbox", true, true);
            }

            return _connection;
        }

        public async Task<IChannel> GetChannelAsync()
        {
            if (_connection == null || !_connection.IsOpen)
            {
                //Log error trying to get channel from null or closed connection.
                _ = await GetConnectionAsync();
            }

            if ( _channel == null || !_channel.IsOpen)
            {
                _channel = await _connection!.CreateChannelAsync(null);
                await _channel.QueueDeclareAsync("outbox", true, true);
            }

            return _channel;
        }

        #endregion
    }
}
