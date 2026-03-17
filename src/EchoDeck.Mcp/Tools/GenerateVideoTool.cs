using EchoDeck.Core.Models;
using EchoDeck.Core.Pipeline;
using EchoDeck.Core.Services;
using EchoDeck.Mcp.Hubs;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EchoDeck.Mcp.Tools;

[McpServerToolType]
public static class GenerateVideoTool
{
    [McpServerTool(Name = "generate_video"), Description(
        "Generate a narrated MP4 video from a PowerPoint presentation. " +
        "The .pptx must have speaker notes on the slides to narrate. " +
        "First call get_upload_slot and upload the file via the returned curl command to get a file_id. " +
        "Returns a job_id to poll with get_job_status.")]
    public static async Task<object> GenerateVideo(
        [Description("file_id returned by the /upload/pptx endpoint after uploading the .pptx file")] string file_id,
        [Description("ElevenLabs voice ID. Use list_voices to see options. Defaults to first configured voice.")] string? voice_id,
        [Description("Video resolution: '1920x1080' (default), '1280x720', or '3840x2160'")] string? resolution,
        [Description("Transition style: 'none' (default) or 'crossfade'")] string? transition,
        JobStore jobStore,
        PipelineOrchestrator orchestrator,
        TempFileServer tempFileServer,
        EchoDeckOptions options,
        IHubContext<ProgressHub> hubContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GenerateVideoTool");

        // Resolve uploaded file
        var uploadDir = Path.Combine(options.DataDir, "uploads");
        var pptxUploadPath = Path.Combine(uploadDir, file_id + ".pptx");
        if (!File.Exists(pptxUploadPath))
            return new { error = $"file_id '{file_id}' not found. Call get_upload_slot and upload the file first." };

        // Resolve voice
        var voices = options.ParseVoices();
        if (voices.Count == 0)
            return new { error = "No ElevenLabs voices configured. Set ELEVENLABS_VOICES env var." };

        var resolvedVoiceId = voice_id ?? voices[0].Id;

        // Apply resolution override
        if (resolution != null) options.DefaultResolution = resolution;

        // Create job
        var job = jobStore.CreateJob();
        var jobDir = options.GetJobDir(job.JobId);
        Directory.CreateDirectory(jobDir);

        // Move uploaded file into the job directory
        var pptxPath = Path.Combine(jobDir, "input.pptx");
        File.Move(pptxUploadPath, pptxPath);

        // Register with temp file server (for Office Online renderer)
        tempFileServer.Register(job.JobId, pptxPath);

        // Fire and forget pipeline — job status is polled via get_job_status
        _ = Task.Run(async () =>
        {
            try
            {
                var progressReporter = new Progress<ProgressEvent>(async evt =>
                {
                    try
                    {
                        await hubContext.Clients.Group(job.JobId).SendAsync("progress", evt);
                    }
                    catch { /* SignalR errors shouldn't fail the pipeline */ }
                });

                using var stream = File.OpenRead(pptxPath);
                await orchestrator.RunAsync(job, stream, pptxPath, resolvedVoiceId, progressReporter);

                // Set the output URL
                if (job.OutputPath != null)
                {
                    var relativePath = Path.GetRelativePath(options.DataDir, job.OutputPath)
                        .Replace('\\', '/');
                    job.OutputUrl = $"{options.BaseUrl.TrimEnd('/')}/jobs-data/{relativePath}";
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pipeline failed for job {JobId}", job.JobId);
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
            }
            finally
            {
                tempFileServer.Unregister(job.JobId);
            }
        });

        return new
        {
            job_id = job.JobId,
            message = $"Job started. Poll get_job_status with job_id='{job.JobId}' to track progress.",
        };
    }
}
