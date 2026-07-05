namespace Data.Models
{
    public class Leader
    {
        public int Id {get; set;}
        public DateTime LastSeenAt {get; set;}
        public long Token {get; set;}
        public string? ContainerId {get; set;}
    }
}