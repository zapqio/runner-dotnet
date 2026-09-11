namespace Zapqio.Runner.Core
{
    /// <summary>
    /// Interface for runner methods executed in pipelines.
    /// </summary>
    public interface IRunnerMethod
    {
        /// <summary>
        /// Display name of the method shown in UI.
        /// </summary>
        public string NameMethod();

        /// <summary>
        /// Input data type for JSON deserialization.
        /// </summary>
        public Type InData();

        /// <summary>
        /// Output data type for JSON serialization.
        /// </summary>
        public Type OutData();

        /// <summary>
        /// Executes the method with JSON input data.
        /// </summary>
        /// <remarks>
        /// A runner configured with <c>MaxConcurrency</c> above 1 calls this method concurrently for
        /// different jobs - including the same method on the same instance, because methods are
        /// singletons for the lifetime of the process. The runner adds no locking: any state kept in
        /// fields must be synchronised by the module itself, and a module that cannot guarantee that
        /// should only be deployed on a runner with <c>MaxConcurrency</c> of 1. Per-job data
        /// (operation id, attempt id) is available through <see cref="JobContext.Current"/>.
        /// </remarks>
        /// <param name="data">JSON serialized input</param>
        /// <returns>JSON serialized output</returns>
        public Task<string> Run(string data);
    }
}
