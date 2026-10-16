using Powder;

if (args.Contains("--self-test"))
{
    int filterIndex = Array.IndexOf(args, "--test-filter");
    return SelfTests.Run(filterIndex >= 0 && filterIndex + 1 < args.Length ? args[filterIndex + 1] : null);
}

if (args.Contains("--benchmark"))
    return Benchmarks.Run(args);

return new SandboxApp().Run(args);
