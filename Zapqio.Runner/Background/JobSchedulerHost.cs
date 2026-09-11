namespace Zapqio.Runner.Background
{
    /// <summary>Hostuje pętlę <see cref="JobScheduler"/>; sam planista jest zwykłym singletonem, żeby dało się go testować bez hosta.</summary>
    public sealed class JobSchedulerHost : BackgroundService
    {
        private readonly JobScheduler _scheduler;
        private readonly ILogger<JobSchedulerHost> _logger;

        public JobSchedulerHost(JobScheduler scheduler, ILogger<JobSchedulerHost> logger)
        {
            _scheduler = scheduler;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Pojemność w pierwszym wpisie, żeby w logu było widać, czy runner wystartował z tą, której oczekiwano.
            _logger.LogInformation("Planista zadań: pojemność {MaxConcurrency}", _scheduler.MaxConcurrency);
            return _scheduler.RunAsync(stoppingToken);
        }
    }
}
