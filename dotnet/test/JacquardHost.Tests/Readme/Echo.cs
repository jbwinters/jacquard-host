namespace JacquardLang.Host.Tests.Readme;

using System;
using JacquardLang.Host;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;

public static class Echo
{
    public static Value Run(string store, Hash target, Hash text, Hash world, Hash send)
    {
        // The command defaults to $JACQUARD_BIN, then `jacquard`.
        var options = new HostOptions { Carrier = new WorkerOptions { Deadline = TimeSpan.FromSeconds(5) } };
        var host = new Host(store, options);
        OperationRegistry registry = new OperationRegistry().Bind(world, send, (context, args) =>
        {
            if (context.IsCancelled)
            {
                throw HandlerException.CompletionUnknown("cancelled");
            }

            if (args is [TextValue input])
            {
                return new TextValue(input.Value + "!");
            }

            throw HandlerException.OutsideFailed("expected one text argument");
        });
        var textType = new NominalType(text);
        var spec = new CallInterface(new[] { textType }, textType, new[] { world });
        return host.Invoke(target, spec, new[] { new TextValue("ping") }, registry).ValueOrThrow();
    }
}
