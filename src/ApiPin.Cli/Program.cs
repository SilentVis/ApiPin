// apipin — runs ApiPin collections from a terminal or a CI pipeline.
// See CliApp.Usage, or run `apipin --help`.

using ApiPin.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
Console.OutputEncoding = System.Text.Encoding.UTF8;
return await CliApp.RunAsync(args, Console.Out, Console.Error, cancellationToken: cancel.Token);
