namespace JacquardLang.Host.Tests.Readme;

using System.Collections.Generic;
using JacquardLang.Host;
using JacquardLang.Host.Sdk;

public static class Documents
{
    // summarize : (Text) ->{Documents} Text, where Documents has one operation,
    // read-document : (Text) -> Text.
    public static Function Summarize(PreparedProgram program)
    {
        Operations documents = new Operations().On("read-document", Codecs.Text, Codecs.Text, (context, key) =>
        {
            // The context reports a cancelled or ended invocation; its
            // CancellationToken is cancelled then too.
            if (context.IsCancelled)
            {
                throw HandlerException.CompletionUnknown("cancelled");
            }

            return key == "example" ? "A synthetic document supplied by .NET." : throw HandlerException.Refused("unknown document");
        });
        return program.Function("summarize", new Dictionary<string, Operations> { ["Documents"] = documents });
    }
}
