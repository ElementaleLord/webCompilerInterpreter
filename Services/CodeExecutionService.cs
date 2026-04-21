using System.Diagnostics;
using System.Runtime.InteropServices;
using webCompilerInterpreter.Models;
namespace webCompilerInterpreter.Services
{
    public class CodeExecutionService : ICodeExecutionService
    {
        // a failsafe value to act as a max cutoff to
        // stop infinite loop hogging server resources
        private const int TimeoutMs= 10_000; // 10 sec
        // NOTE: adding new langs needs a new case here
        public async Task<ExecutionResult> ExecuteAsync(string code, string language)
        {
            return language switch
            {
                "py"=> await RunWithProcessAsync
                (
                    code,
                    fileExtension: ".py",
                    executable: "python",
                    buildArgs: filePath=> filePath
                ),
                "js"=> await RunWithProcessAsync
                (
                    code,
                    fileExtension: ".js",
                    executable: "node",
                    buildArgs: filePath=> filePath
                ),
                "ts"=> await RunWithProcessAsync
                (
                    code,
                    fileExtension: ".ts",
                    executable: "node",
                    buildArgs: filePath=> filePath
                ),
                "c"=> await RunCAsync(code),
                "cpp"=> await RunCPPAsync(code),

                _=> new ExecutionResult
                {
                    Output= $"Language '{language}' is not supported yet.",
                    IsError= true,
                    ExecutionTimeMs= 0,
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
            string tempPath= Path.ChangeExtension(Path.GetTempFileName(), fileExtension);
            try
            {
                // write the code to the temp file
                await File.WriteAllTextAsync(tempPath, code);
                // create a process start info var with relavant settings
                // to allow to redirect the output and error streams for capture
                var psi= new ProcessStartInfo
                {
                    FileName= executable,
                    Arguments= buildArgs(tempPath),
                    RedirectStandardOutput= true,
                    RedirectStandardError= true,
                    RedirectStandardInput= false,
                    UseShellExecute= false,
                    CreateNoWindow= true,
                };
                // some pre start setup to capture output and error streams
                var sw= Stopwatch.StartNew();
                using var process= new Process{
                    StartInfo= psi
                };
                var stdoutBuilder= new System.Text.StringBuilder();
                var stderrBuilder= new System.Text.StringBuilder();
                // capture stdout and stderr of the process
                process.OutputDataReceived += (_, e)=>
                {
                    if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e)=>
                {
                    if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
                };
                // start the process and read from streams to avoid buffer deadlocks
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // wait for process to exit or timeout
                bool finished= await process.WaitForExitAsync
                    (new CancellationTokenSource(TimeoutMs).Token)
                    .ContinueWith(t=> !t.IsCanceled);
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
                // clean up returned values and determine if error occured
                string stdout= stdoutBuilder.ToString().TrimEnd();
                string stderr= stderrBuilder.ToString().TrimEnd();
                int exitCode= process.ExitCode;
                bool isError= exitCode != 0 || !string.IsNullOrWhiteSpace(stderr);
                string output;

                if (isError)
                {// if stdout is NOT null or empty append stderr before it
                    output= string.IsNullOrWhiteSpace(stdout) ?
                        stderr : stderr+ "\n\n%%%% OUTPUT %%%%\n"+ stdout;
                    // handle case of empty stdout using ternary operation
                }
                else
                {
                    output= string.IsNullOrWhiteSpace(stdout) ?
                        "(program produced no output)" : stdout;
                    // handle case of empty stdout using ternary operation
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
            // due to C code needing to be compiled before beign ran
            // some extra steps are needed compared to the other languages
            string sourcePath= Path.ChangeExtension(Path.GetTempFileName(), ".c");// acts as the file to be compiled
            string exeName= Path.GetFileNameWithoutExtension(Path.GetRandomFileName());// rand file name to use
            string exePath= Path.Combine(Path.GetTempPath(),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 
                exeName + ".exe" : exeName);// the compiled executable path
            // with .exe extension for windows and no extension for linux/mac
            try
            {
                // write the code to .c file
                await File.WriteAllTextAsync(sourcePath, code);
                //  build the PSI for the compilation process with gcc
                var compilePsi = new ProcessStartInfo
                {
                    FileName = "gcc",
                    Arguments = $"-o \"{exePath}\" \"{sourcePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // initialize str builders to store output and error streams
                var compileStdout = new System.Text.StringBuilder();
                var compileStderr = new System.Text.StringBuilder();
                using var compile = new Process { StartInfo = compilePsi };
                {
                    // we first capture stdout and stderr of said process
                    compile.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) compileStdout.AppendLine(e.Data);
                    };
                    compile.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) compileStderr.AppendLine(e.Data);
                    };
                    // then start the process 
                    compile.Start();
                    // and read from streams to avoid buffer deadlocks
                    compile.BeginOutputReadLine();
                    compile.BeginErrorReadLine();
                    // wait for process to exit or timeout
                    bool compileFinished = await compile.WaitForExitAsync(new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t => !t.IsCanceled);
                    if (!compileFinished)
                    {
                        try
                        {
                            // try to kill the WHOLE process tree which avoids
                            // leaving potential process children as zombies
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
                    // cleanup returned values and determine if error occured
                    int compileExit = compile.ExitCode;
                    string cOut = compileStdout.ToString().TrimEnd();
                    string cErr = compileStderr.ToString().TrimEnd();

                    // early return incase of compile error
                    if (compileExit != 0 || !string.IsNullOrWhiteSpace(cErr))
                    {
                        string combined = string.IsNullOrWhiteSpace(cErr) ?
                            (string.IsNullOrWhiteSpace(cOut) ? "(No Output Returned)" : cOut)
                            : cErr + "\n\n--- compiler stdout ---\n" +
                            (string.IsNullOrWhiteSpace(cOut) ? "(No Output Returned)" : cOut);
                        // double tenary to handle case of empty cerr and cOut
                        // "worst" case "(No Output Returned)" is given if both are empty
                        // "best" case is cErr + cOut if both have content with a divider in between
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
                            CreateNoWindow = true,
                        };
                        using var p = Process.Start(chmod);
                        await p.WaitForExitAsync();
                        // this is supposed to grant the .exe/bin file of
                        // the .c file execution permissions
                        // i work on windows so i cant test if this works on linux
                        // but from my research this is the common approach
                        // to covering this edge case
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
                // working under the assumption that there will 
                // be no additional args beyond the file for now
                // potention to add an arg field in the future
                var sw = Stopwatch.StartNew();
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();
                using var run = new Process { StartInfo = runPsi };
                {
                    // capture stdout and stderr of the process
                    run.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
                    };
                    run.ErrorDataReceived += (_, e) =>
                    {
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
                    {// early return incase of timeout
                        try
                        {
                            // try to kill the WHOLE process tree
                            // avoids leaving potential process children as zombies
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
                    // clean up returned values and determine if error occured
                    string stdout = stdoutBuilder.ToString().TrimEnd();
                    string stderr = stderrBuilder.ToString().TrimEnd();
                    int exitCode = run.ExitCode;
                    bool isError = exitCode != 0 || !string.IsNullOrWhiteSpace(stderr);
                    string output;
                    if (isError)
                    {// if stderr is NOT null or empty append it before stdpout
                        output = string.IsNullOrWhiteSpace(stdout) ?
                            stderr : stderr + "\n\n%%%% OUTPUT %%%%\n" + stdout;
                        // handle case of empty stdout using a ternary operation
                    }
                    else
                    {
                        output = string.IsNullOrWhiteSpace(stdout) ?
                            "(program produced no output)" : stdout;
                        // handle case of empty stdout using a ternary operation
                    }
                    // proper return 
                    return new ExecutionResult
                    {
                        Output = output,
                        IsError = isError,
                        ExecutionTimeMs = sw.ElapsedMilliseconds
                    };
                }
            }
            catch (Exception ex)
            {// return general error result incase of any exceptions
                return new ExecutionResult
                {
                    Output = $"An error occurred while compiling or running C code:\n{ex.Message}",
                    IsError = true,
                    ExecutionTimeMs = 0
                };
            }
            finally
            {// make sure to clean up temp files always
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
                if (File.Exists(exePath)) File.Delete(exePath);// cleanup for .exe files
            }
        }
        // an exclusive runner for C++
        private static async Task<ExecutionResult> RunCPPAsync(string code)
        {
            // due to C++ code needing to be compiled before being ran
            // some extra steps are needed compared to the other languages
            string sourcePath = Path.ChangeExtension(Path.GetTempFileName(), ".cpp");// is the file to be compiled
            string exeName = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());// rand file name to use
            string exePath = Path.Combine(Path.GetTempPath(),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
                exeName + ".exe" : exeName);// the compiled executable path
            // with .exe extension for windows and no extension for linux/mac
            try
            {
                // write the code to .c file
                await File.WriteAllTextAsync(sourcePath, code);
                //  build the PSI for the compilation process with g++
                var compilePsi= new ProcessStartInfo
                {
                    FileName= "g++",
                    Arguments= $"-o \"{exePath}\" \"{sourcePath}\"",
                    RedirectStandardOutput= true,
                    RedirectStandardError= true,
                    UseShellExecute= false,
                    CreateNoWindow= true
                };
                // initialize str builders to store output and error streams
                var compileStdout= new System.Text.StringBuilder();
                var compileStderr= new System.Text.StringBuilder();
                // capture stdout and stderr of the process
                using var compile= new Process { StartInfo= compilePsi };
                {
                    // we first capture stdout and stderr of said proces
                    compile.OutputDataReceived += (_, e)=>
                    {
                        if (e.Data is not null) compileStdout.AppendLine(e.Data);
                    };
                    compile.ErrorDataReceived += (_, e)=>
                    {
                        if (e.Data is not null) compileStderr.AppendLine(e.Data);
                    };
                    // then start the process 
                    compile.Start();
                    // and read from streams to avoid buffer deadlocks
                    compile.BeginOutputReadLine();
                    compile.BeginErrorReadLine();
                    // wait for process to exit or timeout
                    bool compileFinished= await compile.WaitForExitAsync(new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t=> !t.IsCanceled);
                    if (!compileFinished)
                    {// early return incase of timeout
                        try
                        {
                            // try to kill the WHOLE process tree
                            // avoids leaving potential process children as zombies
                            compile.Kill(entireProcessTree: true);
                        }
                        catch { }
                        // return timeout result
                        return new ExecutionResult
                        {
                            Output= $"Compilation timed out after {TimeoutMs / 1000} seconds.",
                            IsError= true,
                            ExecutionTimeMs= 0
                        };
                    }
                    // cleanup returned values and determine if error occured
                    int compileExit = compile.ExitCode;
                    string cOut = compileStdout.ToString().TrimEnd();
                    string cErr = compileStderr.ToString().TrimEnd();

                    // early return incase of compile error
                    if (compileExit != 0 || !string.IsNullOrWhiteSpace(cErr))
                    {
                        string combined = string.IsNullOrWhiteSpace(cErr) ?
                            (string.IsNullOrWhiteSpace(cOut) ? "(No Output Returned)" : cOut)
                            : cErr + "\n\n--- compiler stdout ---\n" +
                            (string.IsNullOrWhiteSpace(cOut) ? "(No Output Returned)" : cOut);
                        // double tenary to handle case of empty cerr and cOut
                        // "worst" case "(No Output Returned)" is given if both are empty
                        // "best" case is cErr + cOut if both have content with a divider in between
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
                        var chmod= new ProcessStartInfo
                        {
                            FileName= "/bin/chmod",
                            Arguments= $"+x \"{exePath}\"",
                            RedirectStandardOutput= false,
                            RedirectStandardError= false,
                            UseShellExecute= false,
                            CreateNoWindow= true
                        };

                        using var p= Process.Start(chmod);
                        await p.WaitForExitAsync();
                        // this is supposed to grant the .exe/bin file of
                        // the .c file execution permissions
                        // i work on windows so i cant test if this works on linux
                        // but from my research this is the common approach
                        // to covering this edge case
                    }
                    catch {}
                }
                // build the PSI for the process with the compiled executable
                var runPsi= new ProcessStartInfo
                {
                    FileName= exePath,
                    Arguments= "",
                    RedirectStandardOutput= true,
                    RedirectStandardError= true,
                    UseShellExecute= false,
                    CreateNoWindow= true
                };
                // works under the assumption that there will 
                // be no additional args beyond the file for now
                // potention to add an arg field in the future
                var sw= Stopwatch.StartNew();
                var stdoutBuilder= new System.Text.StringBuilder();
                var stderrBuilder= new System.Text.StringBuilder();
                using var run= new Process { StartInfo= runPsi };
                {
                    // first capture stdout and stderr of the process
                    run.OutputDataReceived += (_, e)=> {
                        if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
                    };
                    run.ErrorDataReceived += (_, e)=> {
                        if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
                    };
                    // then start the process
                    run.Start();
                    // and read from streams to avoid buffer deadlocks
                    run.BeginOutputReadLine();
                    run.BeginErrorReadLine();
                    // wait for process to exit or timeout
                    bool finished= await run.WaitForExitAsync
                        (new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t=> !t.IsCanceled);
                    sw.Stop();
                    if (!finished)
                    {// early return incase of timeout
                        try
                        {
                            // try to kill the WHOLE process tree
                            // avoids leaving potential process children as zombies
                            run.Kill(entireProcessTree: true);
                        }
                        catch {}
                        return new ExecutionResult
                        {
                            Output= $"Execution timed out after {TimeoutMs / 1000} seconds.",
                            IsError= true,
                            ExecutionTimeMs= sw.ElapsedMilliseconds
                        };
                    }
                    // clean up returned values and determine if error occured
                    string stdout= stdoutBuilder.ToString().TrimEnd();
                    string stderr= stderrBuilder.ToString().TrimEnd();
                    int exitCode= run.ExitCode;
                    bool isError= exitCode != 0 || !string.IsNullOrWhiteSpace(stderr);
                    string output;
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {// if stderr is NOT null or empty append it before stdpout
                        output= string.IsNullOrWhiteSpace(stdout) ?
                            stderr : stderr + "\n\n%%%% OUTPUT %%%%\n" + stdout;
                        // handle case of empty stdout
                    }
                    else
                    {
                        output= string.IsNullOrWhiteSpace(stdout) ?
                            "(program produced no output)" : stdout;
                        // handle case of empty stdout
                    }
                    // proper return
                    return new ExecutionResult
                    {
                        Output= output,
                        IsError= isError,
                        ExecutionTimeMs= sw.ElapsedMilliseconds
                    };
                }
            }
            catch (Exception ex)
            {// return general error result incase of any exceptions
                return new ExecutionResult
                {
                    Output= $"An error occurred while compiling or running C code:\n{ex.Message}",
                    IsError= true,
                    ExecutionTimeMs= 0
                };
            }
            finally
            {// make sure to clean up temp files always
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
                if (File.Exists(exePath)) File.Delete(exePath);// cleanup for .exe files
            }
        }
    }
}
