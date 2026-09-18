using FocusDB.Repositories.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;

namespace sec_scraper
{
    internal class ScraperConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public ScraperConnectionFactory(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DBConnection")
                ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Creates a new SqlConnection and automatically applies the
        /// SQL Session context if a TenantId is present.
        /// </summary>
        public async Task<SqlConnection> CreateConnectionAsync()
        {
            var connection = new SqlConnection(_connectionString);

            try
            {
                await connection.OpenAsync();
                return connection;
            }
            catch (Exception ex)
            {
                // Ensure we don't leave hanging open connections if OpenAsync or Context fails
                connection.Dispose();
                Console.WriteLine($"Failded to create connection. error: {ex.ToString()}");
                throw;
            }
        }
    }
}
