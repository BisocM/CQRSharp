using BenchmarkDotNet.Running;

// Without arguments BenchmarkSwitcher lists the benchmarks and waits for a choice on stdin; run all of them instead, so
// a plain `dotnet run` (and a script) runs the whole comparison. Any argument (--filter, --job, ...) is passed through.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args.Length == 0 ? ["--filter", "*"] : args);
