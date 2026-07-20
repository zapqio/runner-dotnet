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
        /// <param name="data">JSON serialized input</param>
        /// <returns>JSON serialized output</returns>
        public Task<string> Run(string data);
    }
}
