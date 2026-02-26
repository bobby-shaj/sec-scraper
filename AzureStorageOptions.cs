namespace sec_scraper
{
    internal class AzureStorageOptions
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
    }
}
