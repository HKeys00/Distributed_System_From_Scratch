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
                .HasIndex(w => w.Id)
                .HasFilter("\"PublishedAt\" IS NULL");

            modelBuilder.Entity<WorkItem>()
                .HasIndex(w => w.TaskId);
        }

        #endregion
    }
}
