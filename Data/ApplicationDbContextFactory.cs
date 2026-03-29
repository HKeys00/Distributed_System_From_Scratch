using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Data
{
    public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
    {
        #region Methods

        /// <inheritdoc />
        public ApplicationDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            optionsBuilder.UseNpgsql("Host=postgres_db;Port=5432;Database=mydatabase;Username=myuser;Password=mypassword");

            return new ApplicationDbContext(optionsBuilder.Options);
        }

        #endregion
    }
}
