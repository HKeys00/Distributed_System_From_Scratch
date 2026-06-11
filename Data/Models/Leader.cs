namespace Data.Models
{
    public class Leader
    {
        public int Id {get; set;}
        public TimeSpan LastSeenAt {get; set;}
        public Guid PID {get; set;}
    }
}