using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ProjectRag.Core.Tests.Code;

// Runs the dot program of Graphviz over a text. The tests that use it skip themselves when the program is not installed.
internal static class GraphvizRunner
{
    public static (bool Started, int ExitCode, string Error) Run(string dot)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("dot", "-Tsvg")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
            })!;
            process.StandardInput.Write(dot);
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            string error = process.StandardError.ReadToEnd();
            output.Wait();
            process.WaitForExit();
            return (true, process.ExitCode, error);
        }
        catch (Win32Exception)
        {
            return (false, 0, string.Empty);
        }
    }
}
