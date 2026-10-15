using Powder;

if (args.Contains("--self-test"))
    return SelfTests.Run();

return new SandboxApp().Run(args);
