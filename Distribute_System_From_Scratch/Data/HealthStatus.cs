using Distributed_System_From_Scratch.Enums;
namespace Distributed_System_From_Scratch.Data
{
    public class HealthStatus
    {
        public required string Node { get; set; }

        public required long Incarnation { get; set; }

        public DateTime LastSeen { get; set; }

        public NodeStatus Status {get; set; }
    }
}
