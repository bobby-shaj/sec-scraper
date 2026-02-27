using Azure.Identity;
using Azure.Storage.Blobs;
using FocusDB.Repositories;
using FocusDB.Repositories.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuppeteerSharp.Cdp;
using sec_scraper;


// 1. Setup the Host (but don't use RunAsync yet)
var builder = Host.CreateApplicationBuilder(args);


Console.WriteLine("--- Starting Scraper Health Check ---");

// Check Chrome Path
string chromePath = Environment.GetEnvironmentVariable("CHROME_PATH") ?? "/usr/bin/chromium";
Console.WriteLine($"Checking Chromium at {chromePath}");
if (File.Exists(chromePath))
{
    Console.WriteLine("✅ Chromium Executable Found.");
}
else
{
    Console.WriteLine("❌ ERROR: Chromium not found at specified path!");
}

// Check Database Connection
string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FocusDBConnection")
    ?? throw new InvalidOperationException("Critical: FocusDBConnection is not found in configuration.");


Console.WriteLine("Testing Database Connectivity...");
try
{
    using (var conn = new SqlConnection(connectionString))
    {
        conn.Open();

        // Optional: Run a tiny query to prove we can actually read data
        using var cmd = new SqlCommand("SELECT 1", conn);

        Console.WriteLine("✅ Azure SQL Connection Successful.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ ERROR: Database connection failed: {ex.Message}");
    // We return 1 to signal to Azure Container Jobs that the task failed
    Environment.Exit(1);
}


var azureOptions = builder.Configuration.GetSection("AzureStorage").Get<AzureStorageOptions>();
var clientOptions = new BlobClientOptions
{
    Retry =
    {
        Delay = TimeSpan.FromSeconds(2),                // Wait 2s before first retry
        MaxRetries = 3,                                 // Try up to 3 times
        Mode = Azure.Core.RetryMode.Exponential,        // Wait longer each time
        NetworkTimeout = TimeSpan.FromSeconds(30),
    }
};


if (azureOptions != null)
{
    // Check if the connection string is a placeholder used to bypass Azure Portal UI requirement (needed when using Azure Managed Identity)
    bool isPlaceholder = !string.IsNullOrWhiteSpace(azureOptions.ConnectionString) &&
                         (azureOptions.ConnectionString.Equals("unused", StringComparison.OrdinalIgnoreCase) ||
                         azureOptions.ConnectionString.Equals("ManagedIdentity", StringComparison.OrdinalIgnoreCase));

    if (!string.IsNullOrWhiteSpace(azureOptions.ConnectionString) && !isPlaceholder)
    {
        // SCENARIO 1: Valid Connection String (Local Development)
        builder.Services.AddSingleton(x =>
            new BlobServiceClient(azureOptions.ConnectionString, clientOptions));
        builder.Services.AddSingleton(x =>
        {
            var serviceClient = x.GetRequiredService<BlobServiceClient>();
            return serviceClient.GetBlobContainerClient("filing-documents");
        });
    }
    else
    {
        // SCENARIO 2: Connection String is missing or a placeholder (Azure Production and Managed Identity is being used to access Azure Storage)
        // We fall back to Managed Identity (DefaultAzureCredential)

        var accountName = builder.Configuration["AzureStorage:AccountName"]!;

        if (string.IsNullOrWhiteSpace(accountName))
        {
            // If we are here, we MUST have an account name to build the URI
            throw new InvalidOperationException("AzureStorage:AccountName must be provided when using Managed Identity.");
        }
        
        var storageUri = new Uri($"https://{accountName}.blob.core.windows.net");

        builder.Services.AddSingleton(x => 
            new BlobServiceClient(storageUri, new DefaultAzureCredential(), clientOptions));
        builder.Services.AddSingleton(x =>
        {
            var serviceClient = x.GetRequiredService<BlobServiceClient>();
            return serviceClient.GetBlobContainerClient("filing-documents");
        });
    }
}

// 2. Register your existing services 
builder.Services.AddHttpClient();
builder.Services.AddScoped<IDbConnectionFactory, ScraperConnectionFactory>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IFilingRepository, FilingRepository>();
builder.Services.AddScoped<SecDataProcessingService>();

using IHost host = builder.Build();

// EXECUTE THE SCRAPER
try
{
    using (var mainScope = host.Services.CreateScope())
    {
        var processor = mainScope.ServiceProvider.GetRequiredService<SecDataProcessingService>();

        Console.WriteLine("Starting SEC Scraper Daily Run...");
        await processor.Execute();
        Console.WriteLine("Scraper Run Completed Successfully.");
    }
}
catch (Exception ex)
{
    // Resolve the logger from the host to log the fatal error
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogCritical(ex, "The Scraper application terminated unexpectedly.");

    // Optional: Exit with a non-zero code so Azure/Monitoring knows it failed
    Environment.ExitCode = 1;
}
finally
{
    await host.StopAsync();
}