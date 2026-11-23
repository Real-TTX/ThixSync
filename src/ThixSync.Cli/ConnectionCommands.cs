using ThixSync.Core.Business;
using ThixSync.Core.Storage;

namespace ThixSync.Cli;

/// <summary>connections, test.</summary>
internal static class ConnectionCommands
{
    public static Task<int> List(CliContext context)
    {
        var connections = ConnectionService.Query(null, null, ConnectionSort.Name);
        if (context.Json)
        {
            CliOutput.WriteJson(context.Out, connections.Select(ConnectionJson.Of).ToList());
            return Task.FromResult(ExitCodes.Success);
        }

        if (connections.Count == 0)
        {
            context.Out.WriteLine("Noch keine Verbindungen. In der App unter „Geräte“ anlegen oder mit „thixsync-cli import“ einlesen.");
            return Task.FromResult(ExitCodes.Success);
        }

        var rows = connections.Select(connection => (IReadOnlyList<string>)
        [
            connection.Name,
            ConnectionCatalog.Get(connection.Kind).Name,
            ConnectionService.Address(connection),
            ConnectionService.IsUsableHere(connection, out var reason) ? "Bereit" : reason,
        ]).ToList();
        CliOutput.WriteTable(context.Out, ["Name", "Typ", "Adresse", "Status"], rows);
        return Task.FromResult(ExitCodes.Success);
    }

    public static async Task<int> Test(CliContext context)
    {
        var connection = Lookup.Connection(context.Arguments[0]);
        if (!context.Json)
        {
            context.Out.WriteLine($"Teste „{connection.Name}“ …");
        }

        using var cancellation = new CancellationTokenSource();
        using var cancelKey = context.Host.OnCancelKey(cancellation.Cancel);
        ConnectionTestResult result;
        try
        {
            result = await ConnectionService.TestAsync(connection, null, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            context.Error.WriteLine("Test abgebrochen.");
            return ExitCodes.Cancelled;
        }

        var exitCode = result.Success ? ExitCodes.Success : ExitCodes.Failed;
        if (context.Json)
        {
            CliOutput.WriteJson(context.Out, new TestJson(connection.Id, connection.Name, result.Success, result.Message, exitCode));
            return exitCode;
        }

        (result.Success ? context.Out : context.Error).WriteLine(result.Success ? $"OK: {result.Message}" : $"Fehlgeschlagen: {result.Message}");
        return exitCode;
    }
}
