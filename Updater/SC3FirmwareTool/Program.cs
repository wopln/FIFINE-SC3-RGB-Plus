using System.Text.Json;
using SC3FirmwareTool.Core;

static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
if (args.Length == 0 || args[0] is "help" or "--help")
{
    Console.WriteLine("SC3FirmwareTool detect|detect-recovery|info|verify|verify-stock|install-rgb [--dry-run|--confirm SC3R-11140100]|restore-stock --dry-run");
    return 2;
}

FirmwareService service = new();
service.ProgressChanged += p => Console.Error.WriteLine($"{p.State}: {p.Percent}% {p.Message} {p.CurrentBlock}/{p.TotalBlocks}");
try
{
    switch (args[0].ToLowerInvariant())
    {
        case "detect": Write(service.Detect()); break;
        case "detect-recovery": Write(service.DetectRestore()); break;
        case "info": Console.WriteLine(service.Info()); break;
        case "verify":
            var package = service.Verify();
            Write(new { verified = true, package.Sha256, size = package.Data.Length, releaseTier = ReleasePolicy.NativeUpdaterReleaseTier });
            break;
        case "verify-stock":
            var stock = service.VerifyStock();
            Write(new { verified = true, stock.Sha256, size = stock.Data.Length, buildId = StockRecoveryPolicy.BuildId });
            break;
        case "restore-stock" when args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase):
            Write(service.RestoreStockDryRun()); break;
        case "install-rgb" when args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase):
            Write(service.DryRun()); break;
        case "install-rgb":
            int confirm = Array.FindIndex(args, x => x.Equals("--confirm", StringComparison.OrdinalIgnoreCase));
            if (confirm < 0 || confirm + 1 >= args.Length) throw new FirmwareUpdateException("Use --confirm SC3R-11140100 after explicit user approval.");
            using (Semaphore gate = new(1, 1, "Global\\FIFINE-SC3-RGB-PLUS-FIRMWARE-INSTALL"))
            {
                if (!gate.WaitOne(0)) throw new FirmwareUpdateException("Another firmware installation is active.");
                try { await service.InstallRgbAsync(args[confirm + 1]); }
                finally { gate.Release(); }
            }
            Write(new { success = true, buildId = ReleasePolicy.BuildId });
            break;
        default: throw new FirmwareUpdateException("Unknown command.");
    }
    return 0;
}
catch (Exception ex)
{
    Write(new
    {
        success = false,
        error = ex.Message,
        outcome = ex is FirmwareUpdateException f ? f.Outcome.ToString() : UpdaterState.Failed.ToString(),
        recoveryRequired = ex is FirmwareUpdateException { Outcome: UpdaterState.RecoveryRequired }
    });
    return 1;
}
