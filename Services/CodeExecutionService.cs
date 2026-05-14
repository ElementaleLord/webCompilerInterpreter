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
                "csharp"=> await RunCsharpAsync(code),
                "java"=> await RunJavaAsync(code),
                "lua" => await RunLuaAsync(code),
                "rust" => await RunRustAsync(code),

                _ => new ExecutionResult
                {
                    Output= $"Language '{language}' is not supported yet.",
                    IsError= true,
                    ExecutionTimeMs= 0,
                }
            };
        }
        // Generic runner used for most languages
        private static async Task<ExecutionResult> RunWithProcessAsync
            ( string code, string fileExtension,
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
                            (
                                string.IsNullOrWhiteSpace(cOut) ?
                                    "(No Output Returned)" : cOut
                            ) :
                            cErr + "\n\n--- compiler stdout ---\n" +
                                (
                                    string.IsNullOrWhiteSpace(cOut) ?
                                    "(No Output Returned)" : cOut
                                );
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
                        if (p == null ){
                            throw new Exception("Failed to start chmod process to set execution permissions on compiled C executable.");
                        }
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
                        if (p == null)
                        {
                            throw new Exception("Failed to start chmod process to set execution permissions on compiled C executable.");
                        }
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
        private static async Task<ExecutionResult> RunCsharpAsync(string code)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            string programPath = Path.Combine(tempDir, "Program.cs");
            string projectPath = Path.Combine(tempDir, "TempRun.csproj");
            string outDir = Path.Combine(tempDir, "out");

            string projectContents = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

            try
            {
                await File.WriteAllTextAsync(programPath, code);
                await File.WriteAllTextAsync(projectPath, projectContents);

                // Build the project to a known output folder
                var buildPsi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{projectPath}\" -c Release -o \"{outDir}\" --nologo",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var buildStdout = new System.Text.StringBuilder();
                var buildStderr = new System.Text.StringBuilder();
                using (var build = new Process { StartInfo = buildPsi })
                {
                    build.OutputDataReceived += (_, e) => { if (e.Data is not null) buildStdout.AppendLine(e.Data); };
                    build.ErrorDataReceived += (_, e) => { if (e.Data is not null) buildStderr.AppendLine(e.Data); };

                    build.Start();
                    build.BeginOutputReadLine();
                    build.BeginErrorReadLine();

                    bool buildFinished = await build.WaitForExitAsync(new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t => !t.IsCanceled);
                    if (!buildFinished)
                    {
                        try { build.Kill(entireProcessTree: true); } catch { }
                        return new ExecutionResult
                        {
                            Output = $"Build timed out after {TimeoutMs / 1000} seconds.",
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }

                    int buildExit = build.ExitCode;
                    string bOut = buildStdout.ToString().TrimEnd();
                    string bErr = buildStderr.ToString().TrimEnd();

                    if (buildExit != 0 || !string.IsNullOrWhiteSpace(bErr))
                    {
                        string combined = string.IsNullOrWhiteSpace(bErr) ?
                            (
                                string.IsNullOrWhiteSpace(bOut) ?
                                "(No Output Returned)" : bOut
                            ) :
                            bErr + "\n\n--- build stdout ---\n" + (string.IsNullOrWhiteSpace(bOut) ?
                                "(No Output Returned)" : bOut);

                        return new ExecutionResult
                        {
                            Output = combined,
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }
                }

                // Locate the built artifact and run it.
                string dllPath = Path.Combine(outDir, "TempRun.dll");
                string exePath = Path.Combine(outDir, "TempRun.exe");
                ProcessStartInfo runPsi;

                if (File.Exists(exePath) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    runPsi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                }
                else if (File.Exists(dllPath))
                {
                    runPsi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"\"{dllPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                }
                else
                {
                    return new ExecutionResult
                    {
                        Output = "Built application not found after build step.",
                        IsError = true,
                        ExecutionTimeMs = 0
                    };
                }

                var sw = Stopwatch.StartNew();
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();
                using (var run = new Process { StartInfo = runPsi })
                {
                    run.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
                    run.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

                    run.Start();
                    run.BeginOutputReadLine();
                    run.BeginErrorReadLine();

                    bool finished = await run.WaitForExitAsync(new CancellationTokenSource(TimeoutMs).Token)
                        .ContinueWith(t => !t.IsCanceled);
                    sw.Stop();

                    if (!finished)
                    {
                        try { run.Kill(entireProcessTree: true); } catch { }

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
                    string output = isError ? 
                        (
                            string.IsNullOrWhiteSpace(stdout) ? 
                            stderr : stderr + "\n\n%%%% OUTPUT %%%%\n" + stdout
                        ) :
                        (string.IsNullOrWhiteSpace(stdout) ? 
                            "(program produced no output)" : stdout);

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
                    Output = $"An error occurred while compiling or running C# code:\n{ex.Message}",
                    IsError = true,
                    ExecutionTimeMs = 0
                };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch { }
            }
        }
        private static async Task<ExecutionResult> RunJavaAsync(string code)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            string sourceFile = Path.Combine(tempDir, "Main.java");

            try
            {
                // Create temporary directory
                Directory.CreateDirectory(tempDir);

                // Write the Java source file
                await File.WriteAllTextAsync(sourceFile, code);

                var compilePsi = new ProcessStartInfo
                {
                    FileName = "javac",
                    Arguments = sourceFile,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir
                };

                var compileStdout = new System.Text.StringBuilder();
                var compileStderr = new System.Text.StringBuilder();

                using (var compileProcess = new Process { StartInfo = compilePsi })
                {
                    compileProcess.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) compileStdout.AppendLine(e.Data);
                    };
                    compileProcess.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) compileStderr.AppendLine(e.Data);
                    };

                    compileProcess.Start();
                    compileProcess.BeginOutputReadLine();
                    compileProcess.BeginErrorReadLine();

                    // Wait for compilation with timeout
                    bool compilationFinished = await compileProcess.WaitForExitAsync(
                        new CancellationTokenSource(TimeoutMs).Token
                    ).ContinueWith(t => !t.IsCanceled);

                    if (!compilationFinished)
                    {
                        try { compileProcess.Kill(entireProcessTree: true); }
                        catch { }

                        return new ExecutionResult
                        {
                            Output = $"Java compilation timed out after {TimeoutMs / 1000} seconds.",
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }

                    int compileExitCode = compileProcess.ExitCode;
                    string compOut = compileStdout.ToString().TrimEnd();
                    string compErr = compileStderr.ToString().TrimEnd();

                    // Check for compilation errors
                    if (compileExitCode != 0 || !string.IsNullOrWhiteSpace(compErr))
                    {
                        string errorOutput = string.IsNullOrWhiteSpace(compErr) ? 
                            (
                                string.IsNullOrWhiteSpace(compOut) ?
                                "Compilation failed with no error message" : compOut
                            ) :
                            compErr + (string.IsNullOrWhiteSpace(compOut) ?
                            "" : "\n\n--- Compilation Output ---\n" + compOut);

                        return new ExecutionResult
                        {
                            Output = errorOutput,
                            IsError = true,
                            ExecutionTimeMs = 0
                        };
                    }
                }

                var runPsi = new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = $"-cp \"{tempDir}\" Main",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir
                };

                var sw = Stopwatch.StartNew();
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();

                using (var runProcess = new Process { StartInfo = runPsi })
                {
                    runProcess.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
                    };
                    runProcess.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
                    };

                    runProcess.Start();
                    runProcess.BeginOutputReadLine();
                    runProcess.BeginErrorReadLine();

                    // Wait for execution with timeout
                    bool finished = await runProcess.WaitForExitAsync(
                        new CancellationTokenSource(TimeoutMs).Token
                    ).ContinueWith(t => !t.IsCanceled);

                    sw.Stop();

                    if (!finished)
                    {
                        try { runProcess.Kill(entireProcessTree: true); }
                        catch { }

                        return new ExecutionResult
                        {
                            Output = $"Java execution timed out after {TimeoutMs / 1000} seconds.",
                            IsError = true,
                            ExecutionTimeMs = sw.ElapsedMilliseconds
                        };
                    }

                    string stdout = stdoutBuilder.ToString().TrimEnd();
                    string stderr = stderrBuilder.ToString().TrimEnd();
                    int exitCode = runProcess.ExitCode;

                    // Determine if error occurred
                    bool isError = exitCode != 0 || !string.IsNullOrWhiteSpace(stderr);
                    string output;

                    if (isError)
                    {
                        output = string.IsNullOrWhiteSpace(stderr) ? 
                            (
                                string.IsNullOrWhiteSpace(stdout) ?
                                "Execution failed with no error message" : stdout
                            ) : 
                            stderr + (string.IsNullOrWhiteSpace(stdout) ?
                            "" : "\n\n%%%% OUTPUT %%%%\n" + stdout);
                    }
                    else
                    {
                        output = string.IsNullOrWhiteSpace(stdout) ?
                            "(program produced no output)" : stdout;
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
                    Output = $"An error occurred while executing Java code:\n{ex.Message}",
                    IsError = true,
                    ExecutionTimeMs = 0
                };
            }
            finally
            {
                // Clean up temporary directory
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch { }
            }
        }
        private static async Task<ExecutionResult> RunLuaAsync(string code)
        {
            // Lua execution logic would go here
            // For brevity, this is left as a placeholder
            return new ExecutionResult
            {
                Output = "Lua execution is not yet implemented.",
                IsError = true,
                ExecutionTimeMs = 0
            };
        }
        private static async Task<ExecutionResult> RunRustAsync(string code)
        {
            // Rust execution logic would go here
            // For brevity, this is left as a placeholder
            return new ExecutionResult
            {
                Output = "Rust execution is not yet implemented.",
                IsError = true,
                ExecutionTimeMs = 0
            };
        }
    }
}
