namespace ProjectRag.Core.Code;

/// <summary>
/// Gives the files of a code source their languages.
/// </summary>
public static class CodeLanguages
{
    /// <summary>The language of a file that no rule knows.</summary>
    public const string Text = "text";

    /// <summary>The language of Markdown files. They go through the pipeline of the documentation.</summary>
    public const string Markdown = "markdown";

    /// <summary>
    /// Finds the language of a file by its extension.
    /// </summary>
    /// <param name="path">The path or the name of the file.</param>
    /// <returns>The identifier of the language, for example <c>csharp</c>, or <see cref="Text"/>.</returns>
    public static string FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        if (extension.Length < 2)
            return Text;

        return extension[1..].ToUpperInvariant() switch
        {
            "CS" => "csharp",
            "CSPROJ" or "VBPROJ" or "FSPROJ" or "PROPS" or "TARGETS" => "msbuild",
            "SLN" or "SLNX" or "SLNF" => "solution",
            "JSON" => "json",
            "XML" or "XSD" or "XAML" or "RESX" or "CONFIG" => "xml",
            "YAML" or "YML" => "yaml",
            "TOML" => "toml",
            "MD" or "MARKDOWN" => Markdown,
            "GLSL" or "VERT" or "FRAG" or "COMP" or "GEOM" or "TESC" or "TESE" or "SHADER" => "glsl",
            "HLSL" => "hlsl",
            "METAL" => "metal",
            "PS1" or "PSM1" or "PSD1" => "powershell",
            "SH" or "BASH" => "shell",
            "PY" => "python",
            "JS" or "MJS" or "CJS" => "javascript",
            "TS" or "TSX" => "typescript",
            "C" or "H" => "c",
            "CPP" or "CC" or "HPP" or "HH" => "cpp",
            "SQL" => "sql",
            "EDITORCONFIG" or "INI" => "ini",
            _ => Text,
        };
    }

    /// <summary>
    /// Checks whether a language holds data files, which get the smaller size limit.
    /// </summary>
    /// <param name="language">The identifier of the language.</param>
    /// <returns><see langword="true"/> for JSON, XML, YAML and TOML.</returns>
    public static bool IsConfig(string language) => language is "json" or "xml" or "yaml" or "toml";

    /// <summary>The kind of a chunk of source code.</summary>
    public const string CodeKind = "code";

    /// <summary>The kind of a chunk of a data file or a build file.</summary>
    public const string ConfigKind = "config";

    /// <summary>
    /// Gives the kind of the chunks of a language: <see cref="ConfigKind"/> for data files, project files and solutions, else <see cref="CodeKind"/>.
    /// </summary>
    /// <param name="language">The identifier of the language.</param>
    /// <returns>The value of the column <c>chunk.kind</c>.</returns>
    public static string ChunkKind(string language) => IsConfig(language) || language is "msbuild" or "solution" ? ConfigKind : CodeKind;
}
