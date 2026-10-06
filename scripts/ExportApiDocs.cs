#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051, IL3000, IL2026, IL3050, CA1050, CA1861

#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// -----------------------------------------------------------------------------
// ExportApiDocs.cs
//
// Extracts structured, version-baselined API models for public types in
// Parquet.TypeProvider (Runtime and DesignTime assemblies).
//
// Produces:
//   <out>/api-model.json
//
// Usage:
//   dotnet run scripts/ExportApiDocs.cs
//   dotnet run scripts/ExportApiDocs.cs -- --repo <dir> --out <dir> --version <semver>
// -----------------------------------------------------------------------------

string? repoArg = null;
string? outArg = null;
string? versionArg = null;

string[] argv = Environment.GetCommandLineArgs();
for (int i = 1; i < argv.Length; i++)
{
    if (argv[i] == "--repo" && i + 1 < argv.Length)
        repoArg = argv[++i];
    else if (argv[i] == "--out" && i + 1 < argv.Length)
        outArg = argv[++i];
    else if (argv[i] == "--version" && i + 1 < argv.Length)
        versionArg = argv[++i];
}

string repoRoot = Path.GetFullPath(repoArg ?? Directory.GetCurrentDirectory());
string outputDir = Path.GetFullPath(outArg ?? Path.Combine(repoRoot, "artifacts"));
Directory.CreateDirectory(outputDir);

string version = versionArg
    ?? Environment.GetEnvironmentVariable("VERSION")
    ?? ResolveRepoVersion(repoRoot)
    ?? "0.0.1";

var projects = new[]
{
    new { Name = "Parquet.TypeProvider.Runtime", Path = "src/Parquet.TypeProvider.Runtime" },
    new { Name = "Parquet.TypeProvider.DesignTime", Path = "src/Parquet.TypeProvider.DesignTime" },
};

var allNamespaces = new Dictionary<string, List<TypeDocModel>>(StringComparer.Ordinal);
var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
var systemRuntime = MetadataReference.CreateFromFile(
    Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")
);

foreach (var proj in projects)
{
    string projDir = Path.Combine(repoRoot, proj.Path);
    string? dllPath = FindAssemblyDll(projDir, proj.Name);

    if (dllPath == null || !File.Exists(dllPath))
    {
        Console.WriteLine($"[WARN] Could not find compiled DLL for {proj.Name}. Attempting build...");
        BuildSolution(repoRoot);
        dllPath = FindAssemblyDll(projDir, proj.Name);
    }

    if (dllPath == null || !File.Exists(dllPath))
    {
        Console.Error.WriteLine($"[ERROR] Failed to locate {proj.Name}.dll");
        continue;
    }

    string xmlPath = Path.ChangeExtension(dllPath, ".xml");
    var memberDocs = LoadXmlDocumentation(xmlPath);

    var reference = MetadataReference.CreateFromFile(dllPath);
    var compilation = CSharpCompilation.Create(
        "DocCompilation_" + proj.Name,
        references: new[] { mscorlib, systemRuntime, reference },
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    var asmSymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
    if (asmSymbol == null)
    {
        Console.Error.WriteLine($"[ERROR] Could not resolve assembly symbol for {dllPath}");
        continue;
    }

    void WalkNamespace(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (!IsPubliclyAccessible(type))
                continue;

            // Exclude compiler-generated or internal F# closures/types
            if (type.Name.Contains('<') || type.Name.Contains('@') || type.Name.StartsWith('_'))
                continue;

            string nsName = type.ContainingNamespace?.ToDisplayString() ?? "Global";
            if (!allNamespaces.TryGetValue(nsName, out var typeList))
            {
                typeList = new List<TypeDocModel>();
                allNamespaces[nsName] = typeList;
            }

            var model = ExtractType(type, proj.Name, memberDocs);
            typeList.RemoveAll(t => t.Id == model.Id);
            typeList.Add(model);
        }

        foreach (var sub in ns.GetNamespaceMembers())
        {
            WalkNamespace(sub);
        }
    }

    WalkNamespace(asmSymbol.GlobalNamespace);
}

var apiDoc = new ApiDocProjectModel
{
    ProjectName = "Parquet.TypeProvider",
    Version = version,
    GeneratedAt = DateTime.UtcNow.ToString("o"),
    Namespaces = allNamespaces
        .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
        .Select(kvp => new NamespaceDocModel
        {
            Name = kvp.Key,
            Types = kvp.Value.OrderBy(t => t.Name, StringComparer.Ordinal).ToList(),
        })
        .ToList(),
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
};

string outFile = Path.Combine(outputDir, "api-model.json");
File.WriteAllText(outFile, JsonSerializer.Serialize(apiDoc, jsonOptions));

Console.WriteLine($"✓ Wrote API model to: {outFile}");
Console.WriteLine($"  Namespaces: {apiDoc.Namespaces.Count}");
Console.WriteLine($"  Total Types: {apiDoc.Namespaces.Sum(n => n.Types.Count)}");

return 0;

static string? FindAssemblyDll(string projDir, string assemblyName)
{
    string[] candidates = new[]
    {
        Path.Combine(projDir, "bin/Release/net9.0", assemblyName + ".dll"),
        Path.Combine(projDir, "bin/Release/net8.0", assemblyName + ".dll"),
        Path.Combine(projDir, "bin/Debug/net9.0", assemblyName + ".dll"),
        Path.Combine(projDir, "bin/Debug/net8.0", assemblyName + ".dll"),
    };

    return candidates.FirstOrDefault(File.Exists);
}

static void BuildSolution(string repoRoot)
{
    var start = new System.Diagnostics.ProcessStartInfo("dotnet")
    {
        Arguments = "build Parquet.TypeProvider.sln -c Release",
        WorkingDirectory = repoRoot,
        UseShellExecute = false,
    };
    using var p = System.Diagnostics.Process.Start(start);
    p?.WaitForExit();
}

static Dictionary<string, XElement> LoadXmlDocumentation(string xmlPath)
{
    if (!File.Exists(xmlPath))
        return new Dictionary<string, XElement>(StringComparer.Ordinal);

    try
    {
        var doc = XDocument.Load(xmlPath);
        return doc.Root?.Element("members")?.Elements("member")
            .Where(m => m.Attribute("name") != null)
            .ToDictionary(
                m => m.Attribute("name")!.Value,
                m => m,
                StringComparer.Ordinal
            ) ?? new Dictionary<string, XElement>(StringComparer.Ordinal);
    }
    catch
    {
        return new Dictionary<string, XElement>(StringComparer.Ordinal);
    }
}

static bool IsPubliclyAccessible(INamedTypeSymbol symbol)
{
    for (INamedTypeSymbol? current = symbol; current != null; current = current.ContainingType)
    {
        if (current.DeclaredAccessibility != Accessibility.Public)
            return false;
    }
    return true;
}

static TypeDocModel ExtractType(INamedTypeSymbol symbol, string assemblyName, Dictionary<string, XElement> memberDocs)
{
    var hierarchy = new List<string>();
    var current = symbol.BaseType;
    while (current != null)
    {
        hierarchy.Insert(0, current.ToDisplayString());
        current = current.BaseType;
    }
    hierarchy.Add(symbol.ToDisplayString());

    string typeDocId = symbol.GetDocumentationCommentId() ?? "";
    string? summary = null;
    string? remarks = null;

    if (memberDocs.TryGetValue(typeDocId, out var typeElem))
    {
        summary = CleanXml(typeElem.Element("summary")?.Value);
        remarks = CleanXml(typeElem.Element("remarks")?.Value);
    }

    string kind = symbol.TypeKind switch
    {
        TypeKind.Class => "Class",
        TypeKind.Struct => "Struct",
        TypeKind.Interface => "Interface",
        TypeKind.Enum => "Enum",
        TypeKind.Delegate => "Delegate",
        _ => symbol.TypeKind.ToString(),
    };

    var constructors = new List<MemberDocModel>();
    var properties = new List<MemberDocModel>();
    var methods = new List<MemberDocModel>();
    var fields = new List<MemberDocModel>();

    foreach (var member in symbol.GetMembers().Where(m => m.DeclaredAccessibility == Accessibility.Public))
    {
        // Skip compiler-generated or F# internal artifact members
        if (member.Name.Contains('<') || member.Name.Contains('@') || member.Name.StartsWith('_'))
            continue;

        if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "CompilerGeneratedAttribute"))
            continue;

        string memDocId = member.GetDocumentationCommentId() ?? "";
        string? memSummary = null;
        string? memRemarks = null;
        var paramDocs = new Dictionary<string, string>(StringComparer.Ordinal);

        if (memberDocs.TryGetValue(memDocId, out var memElem))
        {
            memSummary = CleanXml(memElem.Element("summary")?.Value);
            memRemarks = CleanXml(memElem.Element("remarks")?.Value);
            foreach (var p in memElem.Elements("param"))
            {
                string? pName = p.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(pName))
                    paramDocs[pName] = CleanXml(p.Value) ?? "";
            }
        }

        if (member is IMethodSymbol method)
        {
            // Skip property/event accessors
            if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove)
                continue;

            if (method.MethodKind == MethodKind.Constructor)
            {
                constructors.Add(new MemberDocModel
                {
                    Name = symbol.Name,
                    Kind = "Constructor",
                    Syntax = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Summary = memSummary,
                    Remarks = memRemarks,
                    Parameters = method.Parameters.Select(p => new ParameterDocModel
                    {
                        Name = p.Name,
                        Type = p.Type.ToDisplayString(),
                        Summary = paramDocs.TryGetValue(p.Name, out var ps) ? ps : null,
                    }).ToList(),
                });
            }
            else if (method.MethodKind is MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion)
            {
                string memberKind = method.MethodKind switch
                {
                    MethodKind.UserDefinedOperator or MethodKind.Conversion => "Operator",
                    _ => "Method",
                };

                methods.Add(new MemberDocModel
                {
                    Name = method.Name,
                    Kind = memberKind,
                    ReturnType = method.ReturnType.ToDisplayString(),
                    IsStatic = method.IsStatic,
                    Syntax = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Summary = memSummary,
                    Remarks = memRemarks,
                    Parameters = method.Parameters.Select(p => new ParameterDocModel
                    {
                        Name = p.Name,
                        Type = p.Type.ToDisplayString(),
                        Summary = paramDocs.TryGetValue(p.Name, out var ps) ? ps : null,
                    }).ToList(),
                });
            }
        }
        else if (member is IPropertySymbol prop)
        {
            properties.Add(new MemberDocModel
            {
                Name = prop.Name,
                Kind = "Property",
                ReturnType = prop.Type.ToDisplayString(),
                IsStatic = prop.IsStatic,
                Syntax = prop.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Summary = memSummary,
                Remarks = memRemarks,
            });
        }
        else if (member is IFieldSymbol field)
        {
            fields.Add(new MemberDocModel
            {
                Name = field.Name,
                Kind = "Field",
                ReturnType = field.Type.ToDisplayString(),
                IsStatic = field.IsStatic,
                Syntax = field.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Summary = memSummary,
                Remarks = memRemarks,
                ConstantValue = field.ConstantValue?.ToString(),
            });
        }
    }

    return new TypeDocModel
    {
        Id = symbol.ToDisplayString(),
        Name = symbol.Name,
        Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? "Global",
        Assembly = assemblyName,
        Kind = kind,
        IsStatic = symbol.IsStatic,
        IsSealed = symbol.IsSealed,
        IsAbstract = symbol.IsAbstract,
        Summary = summary,
        Remarks = remarks,
        Syntax = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        InheritanceHierarchy = hierarchy,
        Interfaces = symbol.AllInterfaces.Select(i => i.ToDisplayString()).OrderBy(x => x, StringComparer.Ordinal).ToList(),
        Constructors = constructors.OrderBy(c => c.Syntax, StringComparer.Ordinal).ToList(),
        Properties = properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
        Methods = methods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
        Fields = fields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
    };
}

static string? CleanXml(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return null;

    var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim().TrimStart('/', '*').Trim())
        .Where(l => !string.IsNullOrEmpty(l));

    string result = string.Join(" ", lines);
    return string.IsNullOrWhiteSpace(result) ? null : result;
}

static string? ResolveRepoVersion(string repoRoot)
{
    string propsFile = Path.Combine(repoRoot, "Directory.Build.props");
    if (!File.Exists(propsFile))
        return null;

    string text = File.ReadAllText(propsFile);
    var match = System.Text.RegularExpressions.Regex.Match(text, @"<Version>([^<]+)</Version>");
    return match.Success ? match.Groups[1].Value.Trim() : null;
}

public class ApiDocProjectModel
{
    public required string ProjectName { get; set; }
    public required string Version { get; set; }
    public required string GeneratedAt { get; set; }
    public required List<NamespaceDocModel> Namespaces { get; set; }
}

public class NamespaceDocModel
{
    public required string Name { get; set; }
    public required List<TypeDocModel> Types { get; set; }
}

public class TypeDocModel
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public required string Assembly { get; set; }
    public required string Kind { get; set; }
    public bool IsStatic { get; set; }
    public bool IsSealed { get; set; }
    public bool IsAbstract { get; set; }
    public string? Summary { get; set; }
    public string? Remarks { get; set; }
    public required string Syntax { get; set; }
    public required List<string> InheritanceHierarchy { get; set; }
    public required List<string> Interfaces { get; set; }
    public required List<MemberDocModel> Constructors { get; set; }
    public required List<MemberDocModel> Properties { get; set; }
    public required List<MemberDocModel> Methods { get; set; }
    public required List<MemberDocModel> Fields { get; set; }
}

public class MemberDocModel
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string? ReturnType { get; set; }
    public bool IsStatic { get; set; }
    public required string Syntax { get; set; }
    public string? Summary { get; set; }
    public string? Remarks { get; set; }
    public string? ConstantValue { get; set; }
    public List<ParameterDocModel>? Parameters { get; set; }
}

public class ParameterDocModel
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Summary { get; set; }
}
