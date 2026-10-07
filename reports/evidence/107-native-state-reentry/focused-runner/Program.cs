using System.Reflection;
using System.Runtime.Loader;

var assemblyPath = Path.Combine(AppContext.BaseDirectory, "AgentLang.Llvm.Tests.dll");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var dependencyPath = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(dependencyPath) ? context.LoadFromAssemblyPath(dependencyPath) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var testModule = assembly.GetType("AgentLang.Llvm.Tests", throwOnError: true)!;
var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var stateTest = testModule.GetMethod("testTypedStateReentry", flags)
    ?? throw new MissingMethodException(testModule.FullName, "testTypedStateReentry");

try
{
    stateTest.Invoke(null, null);
    Console.WriteLine("testTypedStateReentry completed.");
}
catch (TargetInvocationException error) when (error.InnerException is not null)
{
    Console.Error.WriteLine(error.InnerException);
    Environment.ExitCode = 1;
}
