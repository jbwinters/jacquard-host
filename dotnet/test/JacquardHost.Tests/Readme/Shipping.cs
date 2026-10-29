namespace JacquardLang.Host.Tests.Readme;

using JacquardLang.Host.Sdk;

public static class Shipping
{
    public static long Cost(string rules, string cacheDir, string inspection)
    {
        // Core is $JACQUARD_BIN and $JACQUARD_PRELUDE unless RuntimeOptions say otherwise.
        var runtime = new Runtime();
        PreparedProgram program = runtime.Prepare(new[] { rules }, new PrepareOptions { CacheDir = cacheDir });
        Function shipping = program.Function("shipping-cost");

        // A long converts to an Int argument; Call decodes the result.
        long free = shipping.Call(Codecs.Int, 75L);

        // Invoke keeps the raw frames and evidence, saved only on request.
        Invocation invocation = shipping.Invoke(35L);
        invocation.Save(inspection);
        return invocation.Decode(Codecs.Int) + free;
    }
}
