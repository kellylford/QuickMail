using System;
using System.Threading.Tasks;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class TaskFaultLoggingTests
{
    [Fact]
    public async Task LogFaults_FaultedTask_DoesNotPropagate()
    {
        var task = Task.FromException(new InvalidOperationException("boom"));

        task.LogFaults("test context");

        // Give the observer continuation a moment; the fault must be swallowed (and logged),
        // never rethrown onto the caller or left unobserved.
        await Task.Delay(50);
    }

    [Fact]
    public async Task LogFaults_CancelledTask_DoesNotPropagate()
    {
        var task = Task.FromCanceled(new System.Threading.CancellationToken(canceled: true));

        task.LogFaults("test context");

        await Task.Delay(50);
    }

    [Fact]
    public async Task LogFaults_CompletedTask_DoesNothing()
    {
        Task.CompletedTask.LogFaults("test context");
        await Task.Delay(10);
    }
}
