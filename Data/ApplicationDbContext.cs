using Data.Models.Status;
using Data.Models.Task;
using Microsoft.EntityFrameworkCore;

namespace Data
{
    public class ApplicationDbContext : DbContext
    {
        #region Tables

        /// <summary>
        /// Gets or sets the table containing all the work items for the system.
        /// </summary>
        public DbSet<WorkItem> Tasks { get; set; }

        /// <summary>
        /// Gets or sets the table containing message sitting in the outbox.
        /// </summary>
        public DbSet<OutboxWorkItem> Outbox { get; set; }

        /// <summary>
        /// Gets or sets the view containing messages that are stale.
        /// </summary>
        public DbSet<StaleWorkItem> StaleTasks { get; set; }

        /// <summary>
        /// Represents the collection of Conflict entities in the database context.
        /// </summary>
        public DbSet<Conflict> Conflicts { get; set; }

        /// <summary>
        /// Represents the database table for Success entities.
        /// </summary>
        public DbSet<Success> Successes { get; set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="ApplicationDbContext"/>
        /// </summary>
        /// <param name="options">context options.</param>
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        #endregion

        #region Methods

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<WorkItem>()
                .HasIndex(w => w.TaskId)
                .IsUnique();

            modelBuilder.Entity<WorkItem>()
                .Property(w => w.CreatedAt)
                .HasDefaultValueSql("clock_timestamp()");

            modelBuilder.Entity<Success>()
            .HasIndex(s => s.IdempotencyId)
            .IsUnique();

            modelBuilder.Entity<Success>()
            .Property(s => s.FinishedAt)
            .HasDefaultValueSql("clock_timestamp()");

            modelBuilder.Entity<Conflict>()
            .Property(c => c.FailedAt)
            .HasDefaultValueSql("clock_timestamp()");

            modelBuilder.Entity<OutboxWorkItem>().ToView("outbox");
            modelBuilder.Entity<StaleWorkItem>().ToView("staletasks");
        }

        #endregion
    }
}
