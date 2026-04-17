// ============================================================
//  ExecutionResult.cs
//  Located in:  Models/
//
//  PURPOSE:
//    A simple data-transfer object (DTO) that CodeExecutionService
//    returns to the Controller after running code.
//
//    Keeping this separate from CompilerViewModel means the service
//    layer doesn't need to know anything about MVC or the View —
//    it just produces a plain result.
// ============================================================

namespace webCompilerInterpreter.Models
{
    public class ExecutionResult
    {
        /// <summary>
        /// The combined output to display. Contains stdout on success,
        /// stderr or an error message on failure.
        /// </summary>
        public string Output { get; set; } = string.Empty;

        /// <summary>
        /// True when the process returned a non-zero exit code or
        /// wrote to stderr — signals a runtime / compile error.
        /// </summary>
        public bool IsError { get; set; }

        /// <summary>
        /// Wall-clock milliseconds from process start to exit.
        /// </summary>
        public long ExecutionTimeMs { get; set; }
    }
}
