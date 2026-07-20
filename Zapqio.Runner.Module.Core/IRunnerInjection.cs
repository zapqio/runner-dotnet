namespace Zapqio.Runner.Core
{
    /// <summary>
    /// Marker interface for dependency injection in runner modules.
    /// Classes implementing this interface are automatically registered in DI container.
    /// </summary>
    /// <remarks>
    /// Registered services are shared across all modules and can be injected
    /// as dependencies in any other loaded module.
    /// </remarks>
    public interface IRunnerInjection
    {
    }
}
