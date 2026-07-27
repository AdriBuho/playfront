using Playfront.Helper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Entry point of the Playfront helper. Two modes:
//  (1) Service management from the command line (needs admin ONCE):
//        Playfront.Helper.exe --install    -> registers as a Windows service and starts it
//        Playfront.Helper.exe --uninstall  -> stops and removes it
//        Playfront.Helper.exe --status     -> prints its state
//  (2) No arguments: started by the Windows Service Control Manager, runs as SYSTEM and serves
//      requests from the Playfront UI over the named pipe (see PipeServer).
if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "--install": return ServiceInstaller.Install();
        case "--uninstall": return ServiceInstaller.Uninstall();
        case "--status": return ServiceInstaller.Status();
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName);
builder.Services.AddHostedService<PipeServer>();
builder.Build().Run();
return 0;
