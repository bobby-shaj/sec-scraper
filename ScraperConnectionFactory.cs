using FocusDB.Repositories.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace sec_scraper
{
    internal class ScraperConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        /// <summary>
        /// This property is set manually by te SecDataProcessingService
        /// at the start of each tenant's processing scope.
        /// </summary>
        /// 
        public Guid? CurrentTenantId { get; set; }

        public ScraperConnectionFactory(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("FocusDBConnection")
                ?? throw new InvalidOperationException("FocusDBConnection string is missing from configuration.");
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

                // Apply Row-Level Security context if we are currently processing a tenant
                if (CurrentTenantId.HasValue)
                {
                    await SetSessionContextAsync(connection, CurrentTenantId.Value);
                }

                return connection;
            }
            catch (Exception ex)
            {
                // Ensure we don't leave hanging open connections if OpenAsync or Context fails
                connection.Dispose();
                Console.WriteLine($"Failded to create connection or to set tenant session context. error: {ex.ToString()}");
                throw;
            }
        }

        private async Task SetSessionContextAsync(SqlConnection connection, Guid tenantId)
        {
            using var cmd = connection.CreateCommand();

            // We use the same 'TenantId' key that your SQL RLS function expects
            cmd.CommandText = "sp_set_session_context";
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.Add(new SqlParameter("@key", "TenantId"));
            cmd.Parameters.Add(new SqlParameter("@value", tenantId));

            await cmd.ExecuteNonQueryAsync();
        }
    }
}
