using EchoDeck.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EchoDeck.Core.Services;

public class JobCleanupService(
    JobStore jobStore,
    EchoDeckOptions options,
    ILogger<JobCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            await CleanupAsync();
        }
    }

    private Task CleanupAsync()
    {
        var retention = TimeSpan.FromHours(options.JobRetentionHours);
        var oldJobs = jobStore.GetOlderThan(retention);

        foreach (var job in oldJobs)
        {
            try
            {
                var jobDir = options.GetJobDir(job.JobId);
                if (Directory.Exists(jobDir))
                {
                    Directory.Delete(jobDir, recursive: true);
                    logger.LogInformation("Cleaned up job {JobId}", job.JobId);
                }
                jobStore.Remove(job.JobId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up job {JobId}", job.JobId);
            }
        }

        return Task.CompletedTask;
    }
}
