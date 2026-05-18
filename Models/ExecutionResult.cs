namespace webCompilerInterpreter.Models
{
    public class ExecutionResult
    {
        /// The combined output to display
        /// Contains stdout on success, stderr or an error message on failure
        public string Output { get; set; } = string.Empty;
        /// True when the process returned a non-zero exit code
        public bool IsError { get; set; }
        /// Wall-clock milliseconds from process start to exit
        public long ExecutionTimeMs { get; set; }
    }
}
