using Playfront.Helper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Punto de entrada del ayudante de Playfront. Dos modos:
//  (1) Gestion del servicio desde linea de comandos (requiere admin UNA vez):
//        Playfront.Helper.exe --install    -> se registra como servicio de Windows y arranca
//        Playfront.Helper.exe --uninstall  -> lo para y lo borra
//        Playfront.Helper.exe --status     -> muestra su estado
//  (2) Sin argumentos: lo lanza el Administrador de servicios de Windows y corre como servicio SYSTEM,
//      atendiendo peticiones de la interfaz de Playfront por la tuberia con nombre (ver PipeServer).
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
