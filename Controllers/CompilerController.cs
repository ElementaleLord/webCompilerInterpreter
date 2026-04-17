using Microsoft.AspNetCore.Mvc;
using OnlineCompiler.Services;
using webCompilerInterpreter.Models;

namespace webCompilerInterpreter.Controllers
{
    public class CompilerController : Controller
    {
        private readonly ICodeExecutionService _executionService;
        public CompilerController(ICodeExecutionService executionService)
        {
            _executionService = executionService;
        }

        // GET /Compiler
        [HttpGet]
        public IActionResult Index()
        {
            var model = new CompilerViewModel
            {
                // Pre-select python since its the default language
                SelectedLanguage= "python",
                Code= string.Empty,
                Output= null,
            };
            return View(model);
        }

        // POST /Compiler/Run
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(CompilerViewModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Code))
            {// edge case null code
                model.Output= $"// Nothing to run — write some " +
                    $"{model.SelectedLanguage} code then hit run";

                model.IsError= false;

                return View("Index", model);
            }

            // use the service to execute 
            ExecutionResult result = await _executionService.ExecuteAsync
                (model.Code, model.SelectedLanguage);
            // trivia: suing await frees the server for other requests

            // fill the model and return its view
            model.Output= result.Output;
            model.IsError= result.IsError;
            model.ExecutionTimeMs= result.ExecutionTimeMs;
            return View("Index", model);
        }
    }
}
