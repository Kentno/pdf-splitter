using System.IO;

namespace PdfSplitter;

public static class Program
{
    /// <summary>
    /// Normally starts the app. "--mcp" starts the same exe as a headless MCP server (stdio) instead: that is how
    /// Claude Desktop uses it, without opening any window or touching WPF.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
            return McpServer.RunAsync().GetAwaiter().GetResult();

        // "PdfSplitter.exe --make-mcpb <file>" writes the Claude Desktop extension without opening the app (handy for scripts).
        int make = Array.FindIndex(args, a => a.Equals("--make-mcpb", StringComparison.OrdinalIgnoreCase));
        if (make >= 0 && make + 1 < args.Length)
        {
            McpbBuilder.Create(Path.GetFullPath(args[make + 1]));
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
