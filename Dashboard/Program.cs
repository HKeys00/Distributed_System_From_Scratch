using Data;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

var dbConnectionString = "Host=postgres_db;Port=5432;Database=mydatabase;Username=myuser;Password=mypassword;";
var rabbitHost = "rabbitmq";
var rabbitPort = 5672;
var rabbitUser = "guest";
var rabbitPass = "guest";
var queueName = "outbox";

var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
optionsBuilder.UseNpgsql(dbConnectionString);

Console.Clear();
Console.WriteLine("Relay Dashboard");
Console.WriteLine("Press Ctrl+C to exit.\n");

while (true)
{
    try
    {
        using var db = new ApplicationDbContext(optionsBuilder.Options);

        int outboxCount = await db.Outbox.CountAsync();
        int conflicts = await db.Conflicts.CountAsync();
        int successes = await db.Successes.CountAsync();

        // Queue Depth
        int queueDepth = await GetRabbitQueueDepth(rabbitHost, rabbitPort, rabbitUser, rabbitPass, queueName);

        Console.SetCursorPosition(0, 3);
        Console.WriteLine($"Lag Meter:      {outboxCount} records in outbox waiting to be sent.           ");
        Console.WriteLine($"Success Ratio:  {successes} New Tasks / {conflicts} Conflicts (Idempotency hits)");
        Console.WriteLine($"Queue Depth:    {queueDepth} messages in RabbitMQ queue \"{queueName}\".           ");

        await Task.Delay(2000);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        await Task.Delay(5000);
    }
}


static async Task<int> GetRabbitQueueDepth(string host, int port, string user, string pass, string queue)
{
    var factory = new ConnectionFactory()
    {
        HostName = host,
        Port = port,
        UserName = user,
        Password = pass
    };
    using var connection = await factory.CreateConnectionAsync();
    using var channel = await connection.CreateChannelAsync();

    var result = await channel.QueueDeclareAsync(queue: "outbox",
    durable: true, exclusive: false,
    autoDelete: false,
    arguments: new Dictionary<string, object?> { { "x-queue-type", "quorum" } });

    return (int)result.MessageCount;
}
