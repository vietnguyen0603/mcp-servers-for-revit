using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// External event handler that executes code
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string TransactionModeAuto = "auto";
        public const string TransactionModeNone = "none";

        private const string GeneratedAssemblyName = "AIGeneratedCode";

        // Wrapper around the user code; the user code starts on the last line of the header.
        private const string WrapperHeader = @"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{
    public static class CodeExecutor
    {
        public static object Execute(Document document, object[] parameters)
        {
            // User code entry point
            ";

        private const string WrapperFooter = @"
        }
    }
}";

        /// <summary>Number of wrapper lines before the first user code line.</summary>
        private static readonly int HeaderLineCount = WrapperHeader.Count(c => c == '\n');

        // Code execution parameters
        private string _generatedCode;
        private object[] _executionParameters;
        private string _transactionMode = TransactionModeAuto;

        // Execution result info
        public ExecutionResultInfo ResultInfo { get; private set; }

        // Synchronization object
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // Set the code and parameters to execute
        public void SetExecutionParameters(string code, object[] parameters = null, string transactionMode = TransactionModeAuto)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            _transactionMode = transactionMode == TransactionModeNone ? TransactionModeNone : TransactionModeAuto;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // Wait for execution to complete - IWaitableExternalEventHandler implementation
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                ResultInfo = new ExecutionResultInfo();

                object result;
                if (_transactionMode == TransactionModeNone)
                {
                    result = CompileAndExecuteCode(
                        code: _generatedCode,
                        doc: doc,
                        parameters: _executionParameters
                    );
                }
                else
                {
                    using (var transaction = new Transaction(doc, "Execute AI Code"))
                    {
                        transaction.Start();

                        result = CompileAndExecuteCode(
                            code: _generatedCode,
                            doc: doc,
                            parameters: _executionParameters
                        );

                        transaction.Commit();
                    }
                }

                ResultInfo.Success = true;
                ResultInfo.Result = JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                ResultInfo.Success = false;
                ResultInfo.ErrorMessage = $"Execution failed: {DescribeException(ex)}";
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private object CompileAndExecuteCode(string code, Document doc, object[] parameters)
        {
            // Wrap the code to provide a standard entry point
            var wrappedCode = WrapperHeader + code + WrapperFooter;

            // An encoding is required to emit debug information, which gives
            // runtime stack frames line numbers in the user code.
            var syntaxTree = CSharpSyntaxTree.ParseText(
                SourceText.From(wrappedCode, Encoding.UTF8),
                path: GeneratedAssemblyName + ".cs");

            // Add required assembly references (all loaded assemblies)
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            // Compile the code
            var compilation = CSharpCompilation.Create(
                GeneratedAssemblyName,
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug)
            );

            using (var ms = new MemoryStream())
            using (var pdb = new MemoryStream())
            {
                var result = compilation.Emit(ms, pdb,
                    options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

                // Handle the compilation result
                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"Line {ToUserLine(d.Location.GetLineSpan().StartLinePosition.Line + 1)}: {d.GetMessage()}"));
                    throw new Exception($"Code compilation errors:\n{errors}");
                }

                // Invoke the execute method via reflection
                var assembly = Assembly.Load(ms.ToArray(), pdb.ToArray());
                var executorType = assembly.GetType("AIGeneratedCode.CodeExecutor");
                var executeMethod = executorType.GetMethod("Execute");

                return executeMethod.Invoke(null, new object[] { doc, parameters });
            }
        }

        public string GetName()
        {
            return "Execute AI Code";
        }

        /// <summary>Converts a 1-based line of the wrapped source to a 1-based line of the user code.</summary>
        private static int ToUserLine(int wrappedLine) => wrappedLine - HeaderLineCount;

        /// <summary>
        ///     Unwraps reflection and task wrappers so the error names the real
        ///     exception, and points at the user code line that raised it.
        /// </summary>
        internal static string DescribeException(Exception ex)
        {
            var extra = 0;
            while (true)
            {
                if (ex is TargetInvocationException tie && tie.InnerException != null)
                {
                    ex = tie.InnerException;
                    continue;
                }

                if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
                {
                    var inner = agg.Flatten().InnerExceptions;
                    extra += inner.Count - 1;
                    ex = inner[0];
                    continue;
                }

                break;
            }

            var message = new StringBuilder($"{ex.GetType().FullName}: {ex.Message}");
            if (ex.InnerException != null)
                message.Append($" (inner {ex.InnerException.GetType().FullName}: {ex.InnerException.Message})");
            if (extra > 0)
                message.Append($" (+{extra} more exception(s))");

            var frame = FindUserFrame(ex);
            if (frame != null)
                message.Append($"\n   at {frame}");
            return message.ToString();
        }

        /// <summary>The first stack frame of the exception that belongs to the generated assembly.</summary>
        private static string FindUserFrame(Exception ex)
        {
            try
            {
                foreach (var frame in new StackTrace(ex, true).GetFrames() ?? Array.Empty<StackFrame>())
                {
                    var method = frame.GetMethod();
                    var type = method?.DeclaringType;
                    if (type == null || type.Assembly.GetName().Name != GeneratedAssemblyName)
                        continue;
                    var line = frame.GetFileLineNumber();
                    var location = line > 0 ? $" (user code line {ToUserLine(line)})" : "";
                    return $"{type.FullName}.{method.Name}{location}";
                }
            }
            catch (Exception)
            {
                // Stack inspection is best effort.
            }

            return null;
        }
    }

    // Execution result data structure
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
