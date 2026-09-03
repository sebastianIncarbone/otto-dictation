using System.Reflection;
using Otto.App.Views;

namespace Otto.Core.Tests;

/// <summary>
/// Que cada pose tenga dibujo.
///
/// <para>
/// El modo de falla que esto cierra es silencioso: <c>OttoCharacter.Load</c> devuelve null
/// cuando no encuentra el archivo, el diccionario lo cachea, y el personaje simplemente no
/// dibuja nada. Ni excepción, ni log, ni build roto — un Otto invisible en pantalla y
/// ninguna pista de por qué. Agregar un valor al enum y olvidarse de la entrada en la tabla
/// es un renglón de distancia.
/// </para>
/// <para>
/// Llega a la tabla por reflexión a propósito. Hacerla internal o pública solo para el test
/// sería agrandar la superficie de una clase de dibujo por una razón que no tiene nada que
/// ver con dibujar.
/// </para>
/// </summary>
public class OttoPoseArtTests
{
    private static IReadOnlyDictionary<OttoPose, string> Art()
    {
        var field = typeof(OttoCharacter).GetField("Art", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        var table = field.GetValue(null);

        Assert.NotNull(table);

        // La tabla es Dictionary<OttoPose, (string File, double Feet)>. El ValueTuple se
        // desarma por reflexión en vez de referenciarlo, para que agregarle un campo a la
        // tupla no rompa este test por algo que no es lo que mide.
        var pairs = new Dictionary<OttoPose, string>();

        foreach (var entry in (System.Collections.IEnumerable)table)
        {
            var type = entry.GetType();

            var key = (OttoPose)type.GetProperty("Key")!.GetValue(entry)!;
            var value = type.GetProperty("Value")!.GetValue(entry)!;

            pairs[key] = (string)value.GetType().GetField("Item1")!.GetValue(value)!;
        }

        return pairs;
    }

    [Fact]
    public void Todas_las_poses_tienen_un_dibujo()
    {
        var art = Art();

        foreach (var pose in Enum.GetValues<OttoPose>())
            Assert.True(art.ContainsKey(pose), $"La pose {pose} no tiene entrada en la tabla de dibujos.");
    }

    [Fact]
    public void Ninguna_pose_comparte_dibujo_con_otra()
    {
        // Dos poses apuntando al mismo PNG es casi siempre un copiar y pegar sin terminar,
        // y en pantalla se ve como un personaje que no reacciona.
        var files = Art().Values.ToList();

        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Hablando_es_la_pose_de_la_lectura()
    {
        Assert.Equal("hablando.png", Art()[OttoPose.Speaking]);
    }
}
