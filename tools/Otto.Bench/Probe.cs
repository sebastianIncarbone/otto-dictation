using System.Diagnostics;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Otto.Bench;

/// <summary>
/// Reports which Whisper runtimes actually load on this machine.
///
/// The native library binds once per process and stays bound, so a single process
/// cannot try several runtimes. Probing therefore re-executes this tool once per
/// runtime and collects the results.
///
/// This is the prototype of the first-run hardware detection described in
/// docs/distribucion-y-primer-arranque.md (trampa 4): the app must recommend a
/// model based on what the machine can actually do, not on what the developer's
/// machine could.
/// </summary>
public static class Probe
{
    private static readonly string[] Candidates = ["cuda", "vulkan", "cpu"];

    public static int RunChild(string runtime)
    {
        try
        {
            Benchmark.SetRuntimeOrder(runtime);
            Console.WriteLine(WhisperFactory.GetRuntimeInfo() ?? "(sin información)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message.ReplaceLineEndings(" ").Trim());
            return 1;
        }
    }

    public static void RunAll()
    {
        Console.WriteLine("Probando cada runtime en un proceso separado…");
        Console.WriteLine();

        var available = new List<string>();

        foreach (var runtime in Candidates)
        {
            var (ok, detail) = TryLoad(runtime);

            if (ok)
            {
                available.Add(runtime);
                Console.WriteLine($"  ✓ {runtime,-7} {detail}");
            }
            else
            {
                // Report what the child actually said, and only then the hint. A
                // guessed cause that contradicts the evidence is worse than none.
                Console.WriteLine($"  ✗ {runtime,-7} {detail}");
                if (detail.Contains("Native Library not found", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"    {new string(' ', 6)} → {Explain(runtime)}");
            }
        }

        Console.WriteLine();

        if (available.Count == 0)
        {
            Console.WriteLine("Ningún runtime cargó.");
            return;
        }

        Console.WriteLine($"Usables: {string.Join(", ", available)}");
        Console.WriteLine(available.Contains("vulkan") || available.Contains("cuda")
            ? "Hay GPU: el modelo recomendado es large-v3-turbo."
            : "Solo CPU: el modelo recomendado es small. large-v3-turbo va a ser lento acá.");
    }

    private static (bool Ok, string Detail) TryLoad(string runtime)
    {
        var host = Environment.ProcessPath!;

        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Running under `dotnet run` the process is the shared host, so the managed
        // entry point has to be passed along. Published as an apphost it is the app
        // itself, and passing the dll would be read as a command name.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);

        startInfo.ArgumentList.Add("probe");
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add(runtime);

        using var child = Process.Start(startInfo)!;

        var stdout = child.StandardOutput.ReadToEnd().Trim();
        var stderr = child.StandardError.ReadToEnd().Trim();
        child.WaitForExit();

        return child.ExitCode == 0
            ? (true, stdout)
            : (false, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    /// <summary>
    /// A missing native library is almost always a missing host dependency rather
    /// than a missing file, so name the dependency instead of the file.
    /// </summary>
    public static string Explain(string runtime) => runtime switch
    {
        "cuda" =>
            "no carga — falta el CUDA Toolkit (cudart64/cublas64). El driver de NVIDIA " +
            "solo aporta nvcuda.dll; cuBLAS viene con el Toolkit, no con el driver.",
        "vulkan" =>
            "no carga — falta el Vulkan Runtime (vulkan-1.dll) o el driver no lo expone.",
        _ =>
            "no carga — falta el Visual C++ Redistributable.",
    };
}
