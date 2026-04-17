namespace webCompilerInterpreter.Models
{
    public class CompilerViewModel
    {
        // the code written by user
        public string Code { get; set; } = string.Empty;
        // the lang selected, has default as python
        public string SelectedLanguage { get; set; } = "python";
        // the text resulting from compiling / interpreting the given code
        public string? Output { get; set; }
        // a flag to seperate errors and normal output its for style purposes
        public bool IsError { get; set; }
        // statistic variable to show execution time may be null if execution failed
        public long? ExecutionTimeMs { get; set; }
        // dict mapping radia button vals to disp names
        public static readonly Dictionary<string, string> SupportedLanguages = new()
        {
            {"py", "Python"},
            {"js", "JavaScript"},
            {"ts", "TypeScript"},
            {"c", "C"},
            {"cpp", "C++"},
            // to add a new language add a new pair here and
            // implement it in CodeExecutionService
            // {"rad btn val", "Display name"},
        };
    }
}
