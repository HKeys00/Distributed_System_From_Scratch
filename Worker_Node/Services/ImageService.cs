
using RabbitMQ.Client.Events;
using System.Runtime.InteropServices;

namespace Worker_Node.Services
{
    public class ImageService : IHostedService
    {
        #region Fields

        private readonly RabbitService _rabbitService;
        private string? _consumerTag;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageService"/> class.
        /// </summary>
        /// <param name="rabbitService">The injected rabbit service</param>
        public ImageService(RabbitService rabbitService)
        {
            _rabbitService = rabbitService;
        }

        #endregion

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var channel = await _rabbitService.GetChannelAsync();
            //Throttling just one request per worker
            await channel.BasicQosAsync(0, 1, false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += PerformJobAsync;

            _consumerTag = await channel.BasicConsumeAsync("outbox", false, "image-consumer", false,  false, null, consumer, cancellationToken);
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var channel = await _rabbitService.GetChannelAsync();
            if (_consumerTag != null)
            {
                await channel.BasicCancelAsync(_consumerTag, false, cancellationToken);
            }
        }

        /// <summary>
        /// Pretends to perform work.
        /// </summary>
        private async Task PerformJobAsync(object sender, BasicDeliverEventArgs args)
        {
            if (sender is AsyncEventingBasicConsumer consumer)
            {
                await Task.Delay(15000);
                await consumer.Channel.BasicAckAsync(args.DeliveryTag, false);
            }
        }
    }
}
