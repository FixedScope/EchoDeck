using EchoDeck.Core.Models;
using EchoDeck.Core.Services;

namespace EchoDeck.Core.Pipeline;

public class PipelineOrchestrator(
    SlideExtractor slideExtractor,
    ISlideRenderer slideRenderer,
    AudioGenerator audioGenerator,
    VideoAssembler videoAssembler,
    EchoDeckOptions options)
{
    public async Task RunAsync(
        VideoJob job,
        Stream pptxStream,
        string pptxPath,
        string voiceId,
        IProgress<ProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        var jobDir = options.GetJobDir(job.JobId);
        Directory.CreateDirectory(jobDir);

        job.Status = JobStatus.Processing;

        try
        {
            // Step 1: Extract slides
            await RunStep(job, "extract_slides", progress, async () =>
            {
                job.Slides = slideExtractor.Extract(pptxStream);

                // Warn about silent slides
                var silentSlides = job.Slides.Where(s => s.IsSilent).ToList();
                foreach (var s in silentSlides)
                    job.Warnings.Add($"Slide {s.Index + 1} has no speaker notes — will use {options.SlidePaddingMs / 1000.0:F1}s silence.");

                return $"{job.Slides.Count} slides found" +
                       (silentSlides.Count > 0 ? $", {silentSlides.Count} silent" : string.Empty);
            });

            // Step 2: Render slides
            await RunStep(job, "render_slides", progress, async () =>
            {
                var slideDir = Path.Combine(jobDir, "slides");
                var paths = await slideRenderer.RenderAsync(pptxPath, job.Slides.Count, slideDir, ct);

                for (int i = 0; i < job.Slides.Count && i < paths.Count; i++)
                    job.Slides[i].ImagePath = paths[i];

                // Emit thumbnail previews
                if (progress != null)
                {
                    for (int i = 0; i < paths.Count; i++)
                    {
                        var thumbB64 = await ThumbnailBase64Async(paths[i]);
                        progress.Report(new ProgressEvent
                        {
                            JobId = job.JobId,
                            StepName = "render_slides",
                            StepStatus = "running",
                            Detail = $"Slide {i + 1} rendered",
                            Progress = job.Progress,
                            ThumbnailBase64 = thumbB64,
                        });
                    }
                }

                return $"{paths.Count}/{job.Slides.Count} screenshots";
            });

            // Step 3: Generate audio
            await RunStep(job, "generate_audio", progress, async () =>
            {
                var audioDir = Path.Combine(jobDir, "audio");
                var stepProgress = new Progress<string>(msg =>
                    progress?.Report(new ProgressEvent
                    {
                        JobId = job.JobId,
                        StepName = "generate_audio",
                        StepStatus = "running",
                        Detail = msg,
                        Progress = job.Progress,
                    }));

                await audioGenerator.GenerateAsync(
                    job.Slides, voiceId, audioDir, options.MaxConcurrentTts, stepProgress, ct);

                var silentCount = job.Slides.Count(s => s.IsSilent);
                return $"{job.Slides.Count - silentCount}/{job.Slides.Count} audio segments" +
                       (silentCount > 0 ? $" ({silentCount} silent)" : string.Empty);
            });

            // Step 4: Assemble video
            await RunStep(job, "assemble_video", progress, async () =>
            {
                var (w, h) = options.ParseResolution();
                var stepProgress = new Progress<string>(msg =>
                    progress?.Report(new ProgressEvent
                    {
                        JobId = job.JobId,
                        StepName = "assemble_video",
                        StepStatus = "running",
                        Detail = msg,
                        Progress = job.Progress,
                    }));

                var outputPath = await videoAssembler.AssembleAsync(job.Slides, jobDir, w, h, stepProgress, ct);
                job.OutputPath = outputPath;

                var totalDuration = job.Slides.Sum(s => s.DurationSeconds);
                var span = TimeSpan.FromSeconds(totalDuration);
                return $"{span.Minutes}m {span.Seconds}s final duration";
            });

            job.Status = JobStatus.Completed;
            job.Progress = 100;
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Failed;
            job.Error = "Job was cancelled.";
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;

            progress?.Report(new ProgressEvent
            {
                JobId = job.JobId,
                StepName = "pipeline",
                StepStatus = "failed",
                Detail = ex.Message,
                Progress = job.Progress,
            });
        }
    }

    private async Task RunStep(
        VideoJob job,
        string stepName,
        IProgress<ProgressEvent>? progress,
        Func<Task<string>> action)
    {
        var step = job.GetStep(stepName);
        step.Status = "running";

        var stepIndex = job.Steps.IndexOf(step);
        job.Progress = (stepIndex * 100) / job.Steps.Count;

        progress?.Report(new ProgressEvent
        {
            JobId = job.JobId,
            StepName = stepName,
            StepStatus = "running",
            Detail = "Starting...",
            Progress = job.Progress,
        });

        var detail = await action();

        step.Status = "completed";
        step.Detail = detail;
        job.Progress = ((stepIndex + 1) * 100) / job.Steps.Count;

        progress?.Report(new ProgressEvent
        {
            JobId = job.JobId,
            StepName = stepName,
            StepStatus = "completed",
            Detail = detail,
            Progress = job.Progress,
        });
    }

    private static async Task<string?> ThumbnailBase64Async(string imagePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath);
            return Convert.ToBase64String(bytes);
        }
        catch { return null; }
    }
}
