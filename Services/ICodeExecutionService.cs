using webCompilerInterpreter.Models;

namespace OnlineCompiler.Services
{
    public interface ICodeExecutionService
    {
        // function prototype to run given code based on given language and return the result
        Task<ExecutionResult> ExecuteAsync(string code, string language);
    }
}
