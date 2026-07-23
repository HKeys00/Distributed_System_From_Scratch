using RabbitMQ.Client;

namespace Worker_Node.Services
{
    /// <summary>
    /// Service for creating and managing rabbit MQ connections and channels.
    /// </summary>
    public class RabbitService
    {
        #region Fields

        private readonly IConfiguration _configuration;

        private IConnection? _connection;
        private IChannel? _channel;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the rabbit service.
        /// </summary>
        /// <param name="configuration">The injected configuration.</param>
        public RabbitService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        #endregion

        #region Methods

        public async Task<IConnection> GetConnectionAsync()
        {
            if (_connection == null || !_connection.IsOpen)
            {
                var rabbitUri = _configuration.GetConnectionString("RabbitMq")
                    ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is not configured");

                var factory = new ConnectionFactory() { Uri = new Uri(rabbitUri) };
                _connection = await factory.CreateConnectionAsync();
                _channel = await _connection.CreateChannelAsync(null);

                await _channel.QueueDeclareAsync(queue: "outbox",
                    durable: true, exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?> { 
                        { "x-queue-type", "quorum" },
                        { "x-dead-letter-exchange", "outbox"}
                    });
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
                await _channel.QueueDeclareAsync(queue: "outbox",
                    durable: true, exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?> { 
                        { "x-queue-type", "quorum" },
                        { "x-dead-letter-exchange", "outbox"}
                    });
            }

            return _channel;
        }

        #endregion
    }
}
