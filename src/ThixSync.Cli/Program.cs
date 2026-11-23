using System.Text;
using ThixSync.Cli;

// thixsync-cli: the command line of ThixSync (CliApp). UTF-8 out, so umlauts, "→" and the progress bar look right in every
// terminal and in redirected files.
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException)
{
    // no console at all (a service, a scheduled task): the output goes to files, the default encoding stays
}

return await CliApp.RunAsync(args, CliHost.ForConsole());
