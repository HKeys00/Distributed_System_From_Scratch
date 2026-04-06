using Data.Models;
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
        public DbSet<WorkItem> Outbox { get; set; }

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
                .HasIndex(w => w.TaskId);

            modelBuilder.Entity<WorkItem>()
                .Property(w => w.CreatedAt)
                .HasDefaultValueSql("clock_timestamp()");
        }

        #endregion
    }
}
