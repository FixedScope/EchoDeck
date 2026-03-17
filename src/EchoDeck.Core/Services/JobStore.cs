using EchoDeck.Core.Models;

namespace EchoDeck.Core.Services;

public class JobStore
{
    private readonly Dictionary<string, VideoJob> _jobs = new();
    private readonly object _lock = new();

    public VideoJob CreateJob()
    {
        var job = new VideoJob();
        lock (_lock) { _jobs[job.JobId] = job; }
        return job;
    }

    public VideoJob? Get(string jobId)
    {
        lock (_lock) { return _jobs.TryGetValue(jobId, out var job) ? job : null; }
    }

    public List<VideoJob> GetOlderThan(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        lock (_lock) { return _jobs.Values.Where(j => j.CreatedAt < cutoff).ToList(); }
    }

    public void Remove(string jobId)
    {
        lock (_lock) { _jobs.Remove(jobId); }
    }
}
