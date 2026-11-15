using System;
using System.Diagnostics;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;

namespace ChildProcessCreator
{
    internal sealed class ChildProcessCreator : StatelessService
    {
        public ChildProcessCreator(StatelessServiceContext context) : base(context) { }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RunChildAsync(cancellationToken)));
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private static async Task RunChildAsync(CancellationToken cancellationToken)
        {
            using Process child = Process.Start("cmd.exe")
                ?? throw new InvalidOperationException("Unable to start test child process.");
            try
            {
                await Task.Delay(TimeSpan.FromHours(2), cancellationToken);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync();
                }
            }
        }
    }

    internal static class Program
    {
        private static async Task Main()
        {
            await ServiceRuntime.RegisterServiceAsync("ChildProcessCreatorType",
                context => new ChildProcessCreator(context));
            await Task.Delay(Timeout.Infinite);
        }
    }
}
