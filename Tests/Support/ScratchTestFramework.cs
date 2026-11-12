using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework(Tests.Support.ScratchTestFramework.TypeName, "Tests")]

namespace Tests.Support;

/// <summary>
/// xunit's own test framework, with the run's end (<see cref="TestProcessState.EndRun"/>) as the assembly runner's last
/// step: after the last test, and before the assembly reports itself finished, which xunit awaits and vstest waits for.
///
/// <para><b>Why not <c>ProcessExit</c></b>, where the run's state directory used to be deleted: once the run has
/// reported finished, vstest gives the test host 100 ms to exit and then kills it. Its diagnostic log says so
/// (<c>--diag</c>: "ProxyOperationManager.Close: waiting for test host to exit for 100 ms", then "Timed out waiting
/// for test host to exit. Will terminate process."), and a run's state directory holds hundreds of SQLite files,
/// so the delete was cut off and the directory stayed: 785 of them had piled up in one development machine's temp
/// directory by 2026-11-12. Here, nothing has reported finished yet, so the delete takes as long as it takes.</para>
///
/// <para>The attribute names this type through <see cref="TypeName"/>, built from its own names, because xunit falls
/// back to its own framework without a word when it cannot find the one named; <see cref="RunnerInUse"/> lets a fact
/// check that it found this one.</para>
/// </summary>
public sealed class ScratchTestFramework : XunitTestFramework
{
    /// <summary>This type's full name, from its own names, so a rename cannot leave the attribute pointing nowhere.</summary>
    public const string TypeName = nameof(Tests) + "." + nameof(Support) + "." + nameof(ScratchTestFramework);

    /// <summary>Set when a run starts through this framework's assembly runner.</summary>
    internal static bool RunnerInUse { get; private set; }

    public ScratchTestFramework(IMessageSink messageSink) : base(messageSink) { }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor : XunitTestFrameworkExecutor
    {
        public Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider,
                        IMessageSink diagnosticMessageSink)
            : base(assemblyName, sourceInformationProvider, diagnosticMessageSink) { }

        // As xunit's own, with the runner below.
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
                                                   ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class Runner : XunitTestAssemblyRunner
    {
        public Runner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink,
                      IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
            : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions) { }

        protected override async Task AfterTestAssemblyStartingAsync()
        {
            await base.AfterTestAssemblyStartingAsync();
            RunnerInUse = true;
            // A test host that runs the assembly a second time (an IDE that keeps its host between runs) would find the
            // state directory gone, and the database with it, while this process still believes its schema is built.
            if (TestProcessState.RunEnded)
                Aggregator.Add(new InvalidOperationException(
                    "this test host already ran the suite once and deleted that run's scratch folder, the state " +
                    "directory with it, at the run's end; run the tests again in a new test host"));
        }

        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            await base.BeforeTestAssemblyFinishedAsync();
            string? problem = TestProcessState.EndRun();
            if (problem is not null) DiagnosticMessageSink.OnMessage(new DiagnosticMessage(problem));
        }
    }
}
