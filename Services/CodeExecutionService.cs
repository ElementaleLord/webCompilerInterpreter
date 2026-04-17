using System.Diagnostics;
using System.Runtime.InteropServices;
using webCompilerInterpreter.Models;

namespace OnlineCompiler.Services
{
    public class CodeExecutionService : ICodeExecutionService
    {
        // a max cutoff to stop infinite loop hogging server resources
        private const int TimeoutMs = 10_000; // 10 sec

        // adding new langs needs a new case here but the core runner can stay the same
        public async Task<ExecutionResult> ExecuteAsync(string code, string language)
        {
            return language switch
            {
                "py" => await RunWithProcessAsync
                (
                    code,
                    fileExtension: ".py",
                    executable: "python",
                    buildArgs: filePath => filePath
                ),
                "js" => await RunWithProcessAsync
                (
                    code,
                    fileExtension: ".js",
                    executable: "node",
                    buildArgs: filePath => filePath
                ),
                "ts" => await RunWithProcessAsync
                (
                    code,
                    fileExtension: ".ts",
                    executable: "node",
                    buildArgs: filePath => filePath
                ),
                "c" => await RunCAsync(code),
                "cpp" => await RunCPPAsync(code),

                _ => new ExecutionResult
                {
                    Output = $"Language '{language}' is not supported yet.",
                    IsError = true,
                    ExecutionTimeMs = 0,
                }
            };
        }

        // Generic runner used for most languages
        private static async Task<ExecutionResult> RunWithProcessAsync
            (
                string code, string fileExtension,
                string executable, Func<string, string> buildArgs
            )
        {
            // create a file with the given extension
            string tempPath = Path.ChangeExtension(Path.GetTempFileName(), fileExtension);

            try
            {
                // write the code to the temp file
                await File.WriteAllTextAsync(tempPath, code);

                var psi = new ProcessStartInfo
                {
                    FileName  = executable,
                    Arguments = buildArgs(tempPath),
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    RedirectStandardInput  = false,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                
                var sw = Stopwatch.StartNew();
                using var process = new Process { StartInfo = psi };

                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();

                // capture stdout and stderr of the process
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
                };

                // start the process and read from streams to avoid buffer deadlocks
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                // wait for process to exit or timeout
                bool finished = await process.WaitForExitAsync
                    (new CancellationTokenSource(TimeoutMs).Token)
                    .ContinueWith(t => !t.IsCanceled);

                sw.Stop();

                if (!finished)
                {
                    try
                    {
                        // try to kill the WHOLE process tree avoids leaving potential process children
                        process.Kill(entireProcessTree: true); 
                    }
                    catch { }

                    return new ExecutionResult
                    {
                        Output= $"Execution timed out after {TimeoutMs / 1000} seconds.",
                        IsError= true,
                        ExecutionTimeMs= sw.ElapsedMilliseconds
                    };
                }

                // do some output post-processing
                string stdout= stdoutBuilder.ToString().TrimEnd();
                string stderr= stderrBuilder.ToString().TrimEnd();
                int exitCode= process.ExitCode;
                bool isError= exitCode != 0 || !string.IsNullOrWhiteSpace(stderr);
                string output;

                if (!string.IsNullOrWhiteSpace(stderr))
                {// if stderr is NOT null or empty append it before stdpout
                    output= string.IsNullOrWhiteSpace(stdout) ?
                        stderr : stderr+ "\n\n%%%% OUTPUT %%%%\n"+ stdout;
                    // handle case of empty stdout
                }
                else
                {
                    output = string.IsNullOrWhiteSpace(stdout) ?
                        "(program produced no output)" : stdout;
                    // handle case of empty stdout
                }

                return new ExecutionResult
                {
                    Output= output,
                    IsError= isError,
                    ExecutionTimeMs= sw.ElapsedMilliseconds
                };
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        // an exclusive runner for C
        private static async Task<ExecutionResult> RunCAsync(string code)
        {
            string sourcePath= Path.ChangeExtension(Path.GetTempFileName(), ".c");
            string exeName= Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
            string exePath= Path.Combine(Path.GetTempPath(),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 
                exeName + ".exe" : exeName);

            try
            {
                // write the code to .c file
                await File.WriteAllTextAsync(sourcePath, code);

                var compilePsi = new ProcessStartInfo
                {
                    FileName = "gcc",
                    Arguments = $"-o \"{exePath}\" \"{sourcePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var compileStdout = new System.Text.StringBuilder();
                var compileStderr = new System.Text.StringBuilder();

                // capture stdout and stderr of the process
                using var compile = new Process { StartInfo = compilePsi };
                {
                    compile.OutputDataReceived += (_, e) =>
                    { 
                        if (e.Data is not null) compileStdout.AppendLine(e.Data);
                    };
                    compile.ErrorDataReceived  += (_, e) =>
                    { 
                        if (e.Data is not null) compileStderr.AppendLine(e.Data);
                    };

                    // start the process and read from streams to avoid buffer deadlocks
                    compile.Start();
                    compile.BeginOutputReadLine();
                    compile.BeginErrorReadLine();

                    // wait for process to exit or timeout
                    bool compileFinished = await compile.WaitForExitAsync(new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t => !t.IsCanceled);

                    if (!compileFinished)
                    {
                        try
                        {
                            // try to kill the WHOLE process tree avoids leaving potential process children
                            compile.Kill(entireProcessTree: true);
                        }
                        catch { }

                        return new ExecutionResult
                        {
                            Output = $"Compilation timed out after {TimeoutMs / 1000} seconds.",
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }

                    int compileExit = compile.ExitCode;
                    string cOut = compileStdout.ToString().TrimEnd();
                    string cErr = compileStderr.ToString().TrimEnd();

                    if (compileExit != 0 || !string.IsNullOrWhiteSpace(cErr))
                    {
                        // if cerr is null get cOut else
                        // append to cErr a blank or cOut if its not null
                        string combined = string.IsNullOrWhiteSpace(cErr) ?
                            cOut : cErr + (string.IsNullOrWhiteSpace(cOut) ?
                            "" : "\n\n--- compiler stdout ---\n" + cOut);

                        return new ExecutionResult
                        {
                            Output = combined,
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }
                }

                // handling linux potential exec perms issues
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var chmod = new ProcessStartInfo
                        {
                            FileName = "/bin/chmod",
                            Arguments = $"+x \"{exePath}\"",
                            RedirectStandardOutput = false,
                            RedirectStandardError = false,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var p = Process.Start(chmod);
                        await p.WaitForExitAsync();
                        // this is as best as i understand supposed to
                        // give the compiled binary of the temp file execution permissions
                    }
                    catch { }
                }

                var runPsi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // works under the assumption that there will 
                // be no additional args beyond the file for now

                var sw = Stopwatch.StartNew();
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();

                using var run = new Process { StartInfo = runPsi };
                {
                    // capture stdout and stderr of the process
                    run.OutputDataReceived += (_, e) => {
                        if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
                    };
                    run.ErrorDataReceived += (_, e) => {
                        if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
                    };

                    // start the process and read from streams to avoid buffer deadlocks
                    run.Start();
                    run.BeginOutputReadLine();
                    run.BeginErrorReadLine();

                    // wait for process to exit or timeout
                    bool finished = await run.WaitForExitAsync
                        (new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t => !t.IsCanceled);

                    sw.Stop();

                    if (!finished)
                    {
                        try 
                        { 
                            // try to kill the WHOLE process tree avoids leaving potential process children
                            run.Kill(entireProcessTree: true); 
                        } 
                        catch { }

                        return new ExecutionResult
                        {
                            Output = $"Execution timed out after {TimeoutMs / 1000} seconds.",
                            IsError = true,
                            ExecutionTimeMs = sw.ElapsedMilliseconds
                        };
                    }

                    string stdout = stdoutBuilder.ToString().TrimEnd();
                    string stderr = stderrBuilder.ToString().TrimEnd();
                    int exitCode = run.ExitCode;
                    bool isError = exitCode != 0 || !string.IsNullOrWhiteSpace(stderr);
                    string output;

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {// if stderr is NOT null or empty append it before stdpout
                        output = string.IsNullOrWhiteSpace(stdout) ?
                            stderr : stderr + "\n\n%%%% OUTPUT %%%%\n" + stdout;
                        // handle case of empty stdout
                    }
                    else
                    {
                        output = string.IsNullOrWhiteSpace(stdout) ?
                            "(program produced no output)" : stdout;
                        // handle case of empty stdout
                    }

                    return new ExecutionResult
                    {
                        Output = output,
                        IsError = isError,
                        ExecutionTimeMs = sw.ElapsedMilliseconds
                    };
                }
            }
            catch (Exception ex)
            {
                return new ExecutionResult
                {
                    Output = $"An error occurred while compiling or running C code:\n{ex.Message}",
                    IsError = true,
                    ExecutionTimeMs = 0
                };
            }
            finally
            {
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
                if (File.Exists(exePath)) File.Delete(exePath);// cleanup for .exe files
            }
        }

        // an exclusive runner for C++
        private static async Task<ExecutionResult> RunCPPAsync(string code)
        {
            string sourcePath = Path.ChangeExtension(Path.GetTempFileName(), ".cpp");
            string exeName = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
            string exePath = Path.Combine(Path.GetTempPath(),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
                exeName + ".exe" : exeName);

            try
            {
                // write the code to .c file
                await File.WriteAllTextAsync(sourcePath, code);

                var compilePsi = new ProcessStartInfo
                {
                    FileName = "g++",
                    Arguments = $"-o \"{exePath}\" \"{sourcePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var compileStdout = new System.Text.StringBuilder();
                var compileStderr = new System.Text.StringBuilder();

                // capture stdout and stderr of the process
                using var compile = new Process { StartInfo = compilePsi };
                {
                    compile.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) compileStdout.AppendLine(e.Data);
                    };
                    compile.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) compileStderr.AppendLine(e.Data);
                    };

                    // start the process and read from streams to avoid buffer deadlocks
                    compile.Start();
                    compile.BeginOutputReadLine();
                    compile.BeginErrorReadLine();

                    // wait for process to exit or timeout
                    bool compileFinished = await compile.WaitForExitAsync(new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t => !t.IsCanceled);

                    if (!compileFinished)
                    {
                        try
                        {
                            // try to kill the WHOLE process tree avoids leaving potential process children
                            compile.Kill(entireProcessTree: true);
                        }
                        catch { }

                        return new ExecutionResult
                        {
                            Output = $"Compilation timed out after {TimeoutMs / 1000} seconds.",
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }

                    int compileExit = compile.ExitCode;
                    string cOut = compileStdout.ToString().TrimEnd();
                    string cErr = compileStderr.ToString().TrimEnd();

                    if (compileExit != 0 || !string.IsNullOrWhiteSpace(cErr))
                    {
                        // if cerr is null get cOut else
                        // append to cErr a blank or cOut if its not null
                        string combined = string.IsNullOrWhiteSpace(cErr) ?
                            cOut : cErr + (string.IsNullOrWhiteSpace(cOut) ?
                            "" : "\n\n--- compiler stdout ---\n" + cOut);

                        return new ExecutionResult
                        {
                            Output = combined,
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }
                }

                // handling linux potential exec perms issues
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var chmod = new ProcessStartInfo
                        {
                            FileName = "/bin/chmod",
                            Arguments = $"+x \"{exePath}\"",
                            RedirectStandardOutput = false,
                            RedirectStandardError = false,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var p = Process.Start(chmod);
                        await p.WaitForExitAsync();
                        // this is as best as i understand supposed to
                        // give the compiled binary of the temp file execution permissions
                    }
                    catch { }
                }

                var runPsi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // works under the assumption that there will 
                // be no additional args beyond the file for now

                var sw = Stopwatch.StartNew();
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();

                using var run = new Process { StartInfo = runPsi };
                {
                    // capture stdout and stderr of the process
                    run.OutputDataReceived += (_, e) => {
                        if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
                    };
                    run.ErrorDataReceived += (_, e) => {
                        if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
                    };

                    // start the process and read from streams to avoid buffer deadlocks
                    run.Start();
                    run.BeginOutputReadLine();
                    run.BeginErrorReadLine();

                    // wait for process to exit or timeout
                    bool finished = await run.WaitForExitAsync
                        (new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t => !t.IsCanceled);

                    sw.Stop();

                    if (!finished)
                    {
                        try
                        {
                            // try to kill the WHOLE process tree avoids leaving potential process children
                            run.Kill(entireProcessTree: true);
                        }
                        catch { }

                        return new ExecutionResult
                        {
                            Output = $"Execution timed out after {TimeoutMs / 1000} seconds.",
                            IsError = true,
                            ExecutionTimeMs = sw.ElapsedMilliseconds
                        };
                    }

                    string stdout = stdoutBuilder.ToString().TrimEnd();
                    string stderr = stderrBuilder.ToString().TrimEnd();
                    int exitCode = run.ExitCode;
                    bool isError = exitCode != 0 || !string.IsNullOrWhiteSpace(stderr);
                    string output;

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {// if stderr is NOT null or empty append it before stdpout
                        output = string.IsNullOrWhiteSpace(stdout) ?
                            stderr : stderr + "\n\n%%%% OUTPUT %%%%\n" + stdout;
                        // handle case of empty stdout
                    }
                    else
                    {
                        output = string.IsNullOrWhiteSpace(stdout) ?
                            "(program produced no output)" : stdout;
                        // handle case of empty stdout
                    }

                    return new ExecutionResult
                    {
                        Output = output,
                        IsError = isError,
                        ExecutionTimeMs = sw.ElapsedMilliseconds
                    };
                }
            }
            catch (Exception ex)
            {
                return new ExecutionResult
                {
                    Output = $"An error occurred while compiling or running C code:\n{ex.Message}",
                    IsError = true,
                    ExecutionTimeMs = 0
                };
            }
            finally
            {
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
                if (File.Exists(exePath)) File.Delete(exePath);// cleanup for .exe files
            }
        }
    }
}
