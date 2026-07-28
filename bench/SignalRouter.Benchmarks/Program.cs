using BenchmarkDotNet.Running;

// BenchmarkSwitcher understands the CLI: --filter, --list, --job short, etc.
//   dotnet run -c Release --project bench/SignalRouter.Benchmarks -- --filter '*'
//   dotnet run -c Release --project bench/SignalRouter.Benchmarks -- --list flat
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

public partial class Program
{
}
