using EchoDeck.Core.Models;
using EchoDeck.Core.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EchoDeck.Mcp.Tools;

[McpServerToolType]
public static class GetJobStatusTool
{
    [McpServerTool(Name = "get_job_status"), Description("Check the status of a video generation job. Poll this until status is 'completed' or 'failed'.")]
    public static object GetJobStatus(
        [Description("The job_id returned by generate_video")] string job_id,
        JobStore jobStore,
        EchoDeckOptions options)
    {
        var job = jobStore.Get(job_id);
        if (job == null)
            return new { error = $"Job '{job_id}' not found. It may have expired and been cleaned up." };

        var result = new
        {
            job_id = job.JobId,
            status = job.Status.ToString().ToLowerInvariant(),
            progress = job.Progress,
            steps = job.Steps.Select(s => new { name = s.Name, status = s.Status, detail = s.Detail }),
            output_url = job.Status == JobStatus.Completed ? job.OutputUrl : null,
            warnings = job.Warnings,
            error = job.Error,
            message = job.Status == JobStatus.Completed
                ? $"Video ready. Download from: {job.OutputUrl}\nNote: file will be deleted after {options.JobRetentionHours} hours."
                : null,
        };

        return result;
    }
}
