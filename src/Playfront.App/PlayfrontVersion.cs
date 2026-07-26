using System;
using System.Diagnostics;
using System.Reflection;

namespace Playfront.App;

/// <summary>
/// La version de Playfront, para mostrarla en pantalla y escribirla en el registro de la app.
/// No hay ningun numero escrito aqui a proposito: se lee del propio ejecutable, y ese numero lo
/// pone la compilacion desde PlayfrontVersion en Directory.Build.props (unico sitio donde se define).
/// Asi la version que se ve en la app NO puede desincronizarse de la del .exe ni de la del instalador.
/// </summary>
internal static class PlayfrontVersion
{
    // Se calcula una sola vez: leer metadatos del ejecutable es barato pero no gratis, y esto se
    // consulta desde la interfaz.
    private static readonly Lazy<string> Lazy = new(Read);

    /// <summary>Version corta, tal como se ensena al usuario. Ejemplo: "0.1.0".</summary>
    public static string Current => Lazy.Value;

    /// <summary>Lo que se ensena en la interfaz. Ejemplo: "Playfront 0.1.0".</summary>
    public static string Display => "Playfront " + Current;

    private static string Read()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();

            // InformationalVersion es la que refleja <Version> tal cual (AssemblyVersion siempre
            // anade un ".0" de mas y FileVersion tampoco admite sufijos tipo "-beta").
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Algunas herramientas de compilacion le pegan "+<identificador del commit>";
                // eso no le dice nada al usuario, asi que se recorta.
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            // Reservas por si ese atributo no estuviera: las propiedades del fichero, y por ultimo
            // la version del ensamblado.
            var file = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
            if (!string.IsNullOrWhiteSpace(file)) return file;

            // En ingles porque puede acabar en pantalla (norma: la interfaz de la app va en ingles).
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            // Degradar, nunca reventar (norma de distribucion): no saber la version jamas debe
            // impedir que la app arranque.
            return "unknown";
        }
    }
}
